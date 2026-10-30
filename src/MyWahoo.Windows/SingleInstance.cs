namespace MyWahoo.Windows;

/// <summary>
/// One copy of the app at a time (two would fight over the trainer and the Click). A second launch signals
/// the running copy to show its full window or overlay, then exits.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\MyWahoo.SingleInstance";
    private const string ShowMainEvent = @"Local\MyWahoo.ShowMain";
    private const string ShowOverlayEvent = @"Local\MyWahoo.ShowOverlay";

    private static Mutex? _mutex;

    /// <summary>True for the first copy; later copies get false (after signalling the first).</summary>
    public static bool Claim(bool wantOverlay)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (first) return true;
        try
        {
            using var show = EventWaitHandle.OpenExisting(wantOverlay ? ShowOverlayEvent : ShowMainEvent);
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The running copy is still starting up; it will appear on its own.
        }
        return false;
    }

    public static IDisposable Listen(Action<bool> show) => new Listener(show);

    private sealed class Listener : IDisposable
    {
        private readonly EventWaitHandle _main = new(false, EventResetMode.AutoReset, ShowMainEvent);
        private readonly EventWaitHandle _overlay = new(false, EventResetMode.AutoReset, ShowOverlayEvent);
        private readonly ManualResetEvent _stop = new(false);

        public Listener(Action<bool> show)
        {
            new Thread(() =>
            {
                while (true)
                {
                    var which = WaitHandle.WaitAny([_main, _overlay, _stop]);
                    if (which == 2) return;
                    show(which == 1);
                }
            }) { IsBackground = true, Name = "Single instance" }.Start();
        }

        public void Dispose()
        {
            _stop.Set();
            _main.Dispose();
            _overlay.Dispose();
        }
    }
}
