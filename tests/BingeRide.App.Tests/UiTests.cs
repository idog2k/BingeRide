using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using BingeRide.App.Controls;
using BingeRide.App.Services;
using BingeRide.App.ViewModels;
using BingeRide.App.Views;
using BingeRide.Core.Devices;
using BingeRide.Core.Protocol;
using BingeRide.Core.Session;
using BingeRide.Core.Settings;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(BingeRide.App.Tests.TestApp))]

namespace BingeRide.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Renders the real windows headlessly with a simulated ride; PNGs land in ./screenshots for review.</summary>
public sealed class UiTests : IDisposable
{
    private static readonly string ShotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../screenshots"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"bingeride-ui-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private MainViewModel CreateVm(IPlatformServices? platform = null)
    {
        var settings = new AppSettings { Ftp = 140, WorkoutsFolder = _folder, RidesFolder = Path.Combine(_folder, "rides") };
        var vm = new MainViewModel(platform ?? new DefaultPlatformServices(), settings, Path.Combine(_folder, "settings.json"));
        // No wall-clock ticks: they'd move a ride dated at T0 to "now" (e.g. expiring its motivation sentence).
        vm.Start(watchLibrary: false, realClock: false);
        Dispatcher.UIThread.RunJobs(); // library refreshes are posted to the UI thread
        vm.UseTrainer(new SimulatedTrainer(autoRun: false));
        vm.Ride.Tick(T0); // the ride clock starts at the test's fake time
        return vm;
    }

    /// <summary>Simulates riding: the demo rider answers the ride controller's targets once per second.</summary>
    private static void Ride(MainViewModel vm, DateTimeOffset from, int seconds)
    {
        var sim = (SimulatedTrainer)vm.Ride.Trainer!;
        for (var i = 0; i <= seconds * 4; i++)
        {
            var now = from.AddSeconds(i / 4.0);
            if (i % 4 == 0) vm.Ride.OnTrainerData(sim.Sample(now));
            vm.Ride.Tick(now);
        }
        vm.Refresh();
    }

    private static void Save(Window window, string name)
    {
        Directory.CreateDirectory(ShotDir);
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ShotDir, name), PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Library_is_seeded_with_samples()
    {
        var vm = CreateVm();
        Assert.True(vm.Workouts.Count >= 7, $"only {vm.Workouts.Count} workouts");
        Assert.All(vm.Workouts, e => Assert.Null(e.Error));
        Assert.Contains(vm.Workouts, e => e.Name.StartsWith("VO2max"));
    }

    [AvaloniaFact]
    public void Free_ride_main_window()
    {
        var vm = CreateVm();
        vm.UpCommand.Execute(null);
        vm.UpCommand.Execute(null);
        vm.Ride.StartRide(T0);
        Ride(vm, T0, 90);
        Assert.Equal("14", vm.GearText);
        Assert.StartsWith("avg ", vm.HeartRateStatsText); // demo rider's HR is tracked
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 780 };
        w.Show();
        Save(w, "main-free.png");
    }

    [AvaloniaFact]
    public void Erg_mode_main_window()
    {
        var vm = CreateVm();
        vm.SetModeCommand.Execute("Erg");
        vm.Ride.SetErgWatts(150);
        Ride(vm, T0, 30);
        Assert.Equal(new ErgTarget(150), vm.Ride.CurrentTarget);
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 780 };
        w.Show();
        Save(w, "main-erg.png");
    }

    [AvaloniaFact]
    public void Workout_main_and_overlay()
    {
        var vm = CreateVm();
        var vo2 = vm.Workouts.First(e => e.Name.StartsWith("VO2max"));
        vm.SelectedWorkout = vo2;
        vm.StartWorkoutCommand.Execute(vo2);
        Ride(vm, T0, 21 * 60); // 10 min warm-up + 6 min openers + 4 min settle, then 1 min into rep 1
        Assert.Equal(RideMode.Workout, vm.Ride.Mode);
        Assert.IsType<ErgTarget>(vm.Ride.CurrentTarget);
        Assert.Equal(168, ((ErgTarget)vm.Ride.CurrentTarget!).Watts);
        Assert.NotNull(vm.ActiveFoodMarkers);
        Assert.NotEmpty(vm.ActiveFoodMarkers!);
        Assert.Contains("🍎", vm.BurnedFoodText);

        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 780 };
        w.Show();
        Save(w, "main-workout.png");

        var overlay = new OverlayWindow { DataContext = vm };
        overlay.Show();
        Save(overlay, "overlay-workout.png");
    }

    [AvaloniaFact]
    public void Overlay_free_ride_with_cue()
    {
        var vm = CreateVm();
        var hills = vm.Workouts.First(e => e.FileName.StartsWith("hill-repeats"));
        vm.StartWorkoutCommand.Execute(hills);
        Ride(vm, T0, 5);
        vm.OverlayOpacity = 0.35;
        var overlay = new OverlayWindow { DataContext = vm };
        overlay.Show();
        Save(overlay, "overlay-slope.png");
    }

    [AvaloniaFact]
    public void Settings_tab_renders()
    {
        var vm = CreateVm();
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        w.Show();
        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 3;
        Save(w, "main-settings.png");
    }

    [AvaloniaFact]
    public void Paste_extracts_workout_from_chat_text_and_saves_it()
    {
        var vm = CreateVm();
        vm.ClipboardReader = () => Task.FromResult<string?>("""
            Here's your session!

            ```json
            {"format":"bingeride-workout","version":1,"name":"Quick openers","power_unit":"pct_ftp",
             "steps":[{"type":"steady","sec":300,"power":60},{"type":"repeat","times":3,"steps":[{"type":"steady","sec":30,"power":130},{"type":"steady","sec":90,"power":55}]}]}
            ```
            Enjoy.
            """);
        vm.PasteWorkoutCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.NoticeEntry);
        Assert.Equal("Quick openers", vm.NoticeEntry!.Name);
        Assert.True(File.Exists(Path.Combine(_folder, "quick-openers.json")));
        vm.StartNoticeWorkoutCommand.Execute(null);
        Assert.Equal(RideMode.Workout, vm.Ride.Mode);
    }

    [AvaloniaFact]
    public void Finished_ride_is_saved_as_fit_and_listed()
    {
        var vm = CreateVm();
        Assert.True(vm.TrainerConnected);
        Assert.Equal("Demo trainer", vm.TrainerChipText);
        Assert.True(vm.IsRideIdle);
        vm.Ride.Tick(T0);
        vm.StartRideCommand.Execute(null);
        Ride(vm, T0, 5 * 60);
        Assert.True(vm.IsRecording);
        Assert.Equal("5:00", vm.RideTimeText);

        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 780 };
        w.Show();
        Save(w, "main-recording.png");

        vm.FinishRideCommand.Execute(null);
        var fit = Directory.GetFiles(Path.Combine(_folder, "rides"), "*.fit");
        Assert.Single(fit);
        Assert.True(File.Exists(Path.ChangeExtension(fit[0], ".json")));
        Assert.Single(vm.RideEntries);
        Assert.StartsWith("Ride saved: 5:00", vm.Notice);
        Assert.True(vm.IsRideIdle);

        var tabs = w.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 2;
        Save(w, "main-rides.png");
    }

    /// <summary>Records keep-awake requests; everything else does nothing.</summary>
    private sealed class AwakePlatform : IPlatformServices
    {
        public List<bool> KeepAwakeCalls { get; } = [];
        public void KeepAwake(bool on) => KeepAwakeCalls.Add(on);
        public Core.Ble.IBleAdapter? CreateBleAdapter() => null;
        public void SetClickThrough(Window window, bool enabled) { }
        public void KeepOnTop(Window window) { }
        public IDisposable? RegisterGlobalHotkeys(Action toggleCompact, Action toggleClickThrough) => null;
        public void OpenFolder(string path) { }
        public IDisposable? ListenForActivation(Action<bool> show) => null;
    }

    [AvaloniaFact]
    public async Task Screen_stays_awake_only_while_a_ride_is_active()
    {
        var platform = new AwakePlatform();
        var vm = CreateVm(platform);
        Ride(vm, T0, 30);
        Assert.Empty(platform.KeepAwakeCalls); // just pedalling around: Windows may sleep as usual

        vm.StartRideCommand.Execute(null);
        Ride(vm, T0.AddSeconds(30), 30);
        Assert.Equal([true], platform.KeepAwakeCalls);

        vm.TogglePauseCommand.Execute(null);
        vm.Refresh();
        Assert.True(vm.IsRidePaused);
        Assert.Equal([true], platform.KeepAwakeCalls); // a pause (drink, toilet) keeps the screen on

        vm.TogglePauseCommand.Execute(null);
        vm.FinishRideCommand.Execute(null);
        Assert.Equal([true, false], platform.KeepAwakeCalls);

        vm.StartRideCommand.Execute(null);
        vm.Refresh();
        await vm.DisposeAsync(); // closing the app mid-ride lets the PC sleep again
        Assert.Equal([true, false, true, false], platform.KeepAwakeCalls);
    }

    [AvaloniaFact]
    public void Ride_graphs_open_on_double_click_and_zoom()
    {
        var vm = CreateVm();
        var vo2 = vm.Workouts.First(e => e.Name.StartsWith("VO2max"));
        vm.StartRideCommand.Execute(null);
        vm.StartWorkoutCommand.Execute(vo2);
        Ride(vm, T0, 32 * 60); // warm-up, openers, settle and the first VO2 reps
        vm.FinishRideCommand.Execute(null);
        var entry = Assert.Single(vm.RideEntries);

        var main = new MainWindow { DataContext = vm, Width = 1200, Height = 780 };
        main.Show();
        main.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        main.UpdateLayout();
        var item = main.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, entry));
        var at = item.TranslatePoint(new Point(40, 12), main)!.Value;
        main.MouseDown(at, MouseButton.Left);
        main.MouseUp(at, MouseButton.Left);
        main.MouseDown(at, MouseButton.Left);
        main.MouseUp(at, MouseButton.Left);

        var window = Assert.Single(main.OwnedWindows.OfType<RideDetailWindow>());
        var detail = (RideDetailViewModel)window.DataContext!;
        for (var i = 0; i < 300 && !detail.HasTrack; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs(); // the .fit file is read on a worker thread
        }
        Assert.True(detail.HasTrack, detail.Status);
        Assert.InRange(detail.Seconds, 32 * 60, 32 * 60 + 1); // one sample per riding second
        Assert.False(detail.IsZoomed);
        Assert.StartsWith("Whole ride · 32:00 ·", detail.RangeText);
        Assert.Contains(detail.Tiles, t => t.Label == "AVG HR");
        window.Width = 1100;
        window.Height = 740;
        Save(window, "ride-detail.png");

        // Drag across 10:00–20:00 of the chart to zoom into it.
        var chart = window.GetVisualDescendants().OfType<RideChart>().Single();
        var plotLeft = 44.0;
        var plotWidth = chart.Bounds.Width - 44 - 14;
        Point On(double second, double y = 60) => chart.TranslatePoint(new Point(plotLeft + second / detail.Seconds * plotWidth, y), window)!.Value;
        window.MouseDown(On(600), MouseButton.Left);
        window.MouseMove(On(900));
        window.MouseMove(On(1200));
        window.MouseUp(On(1200), MouseButton.Left);
        Assert.True(detail.IsZoomed);
        Assert.Equal(600, detail.ViewStart, 0);
        Assert.Equal(1200, detail.ViewEnd, 0);
        Assert.StartsWith("10:00 → 20:00 · 10:00 ·", detail.RangeText);

        window.MouseMove(chart.TranslatePoint(new Point(plotLeft + plotWidth * 0.4, 120), window)!.Value);
        Save(window, "ride-detail-zoomed.png");

        detail.ScrollValue = 900; // the scroll bar keeps the zoomed length
        Assert.Equal((900.0, 1500.0), (detail.ViewStart, detail.ViewEnd));
        Assert.Equal((900.0, 1500.0), chart.View);

        window.MouseDown(On(100), MouseButton.Left); // a double-click shows the whole ride again
        window.MouseUp(On(100), MouseButton.Left);
        window.MouseDown(On(100), MouseButton.Left);
        window.MouseUp(On(100), MouseButton.Left);
        Assert.False(detail.IsZoomed);
        Assert.Equal((0.0, (double)detail.Seconds), chart.View);
    }

    [AvaloniaFact]
    public void A_missing_ride_file_shows_a_message()
    {
        var summary = new RideSummary(T0, 600, 600, 5000, 90, 150, 300, 160, 0.8, 10, 85, null, null, "Gone", 200);
        var detail = new RideDetailViewModel(new RideEntry(Path.Combine(_folder, "missing.fit"), summary));
        detail.Load();
        Assert.False(detail.HasTrack);
        Assert.StartsWith("Couldn't read missing.fit", detail.Status);
        Assert.DoesNotContain(detail.Tiles, t => t.Label == "AVG HR");
    }

    [AvaloniaFact]
    public void Accidental_short_ride_is_not_saved()
    {
        var vm = CreateVm();
        vm.Ride.Tick(T0);
        vm.StartRideCommand.Execute(null);
        Ride(vm, T0, 20);
        vm.FinishRideCommand.Execute(null);
        Assert.False(Directory.Exists(Path.Combine(_folder, "rides")) && Directory.GetFiles(Path.Combine(_folder, "rides")).Length > 0);
        Assert.Contains("not saved", vm.Notice);
    }

    [AvaloniaFact]
    public void Overlay_fields_are_configurable()
    {
        var vm = CreateVm();
        // Pick distance + heart rate as the big numbers, then ride time, avg power and cadence.
        foreach (var o in vm.OverlayFieldOptions) o.IsSelected = false;
        foreach (var f in new[] { OverlayField.Distance, OverlayField.HeartRate, OverlayField.RideTime, OverlayField.AvgPower, OverlayField.Cadence })
            vm.OverlayFieldOptions.First(o => o.Field == f).IsSelected = true;
        // Checked order follows the list order; move Distance to the top so it's the first big number.
        var distance = vm.OverlayFieldOptions.First(o => o.Field == OverlayField.Distance);
        while (vm.OverlayFieldOptions.IndexOf(distance) > 0) vm.MoveFieldUpCommand.Execute(distance);
        var hr = vm.OverlayFieldOptions.First(o => o.Field == OverlayField.HeartRate);
        while (vm.OverlayFieldOptions.IndexOf(hr) > 1) vm.MoveFieldUpCommand.Execute(hr);

        Assert.Equal([OverlayField.Distance, OverlayField.HeartRate, OverlayField.Cadence, OverlayField.RideTime, OverlayField.AvgPower],
            vm.Settings.OverlayFields);

        vm.Ride.StartRide(T0);
        Ride(vm, T0, 10 * 60);
        Assert.Equal("km", vm.OverlayBig1.Unit);
        Assert.StartsWith("♥ ", vm.OverlayBig2.Value);
        Assert.Equal(3, vm.OverlaySmall.Count);
        Assert.Equal("10:00", vm.OverlaySmall[1].Value);

        var overlay = new OverlayWindow { DataContext = vm };
        overlay.Show();
        Save(overlay, "overlay-custom-fields.png");

        vm.ResetOverlayFieldsCommand.Execute(null);
        Assert.Equal(AppSettings.DefaultOverlayFields(), vm.Settings.OverlayFields);
        Assert.Equal(OverlayField.Power, vm.OverlayFieldOptions[0].Field);
    }

    [AvaloniaFact]
    public void Motivation_sentence_shows_on_the_overlay_and_the_main_window()
    {
        var vm = CreateVm();
        vm.Ride.StartRide(T0);
        Ride(vm, T0, 10);
        Assert.Contains(vm.MotivationText, MotivationSentences.All);

        var overlay = new OverlayWindow { DataContext = vm };
        overlay.Show();
        var main = new MainWindow { DataContext = vm, Width = 1200, Height = 780 };
        main.Show();
        var overlayLine = Line(overlay);
        var mainLine = Line(main);
        Assert.True(overlayLine.IsVisible);
        Assert.True(mainLine.IsVisible);

        // The longest sentence is what has to fit the overlay's width.
        vm.MotivationText = MotivationSentences.All.MaxBy(s => s.Length);
        Save(overlay, "overlay-motivation.png");
        Save(main, "main-motivation.png");

        // Unticking the overlay field hides it there, and only there.
        vm.OverlayMotivationSetting = false;
        Assert.False(overlayLine.IsVisible);
        Assert.True(mainLine.IsVisible);
        vm.ResetOverlayFieldsCommand.Execute(null);
        Assert.True(overlayLine.IsVisible);

        // Switched off in Settings, it leaves both screens at the next tick.
        vm.ShowMotivationSetting = false;
        Ride(vm, T0.AddSeconds(11), 1);
        Assert.False(overlayLine.IsVisible);
        Assert.False(mainLine.IsVisible);

        static Border Line(Window window) =>
            window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MotivationLine");
    }

    [AvaloniaFact]
    public async Task Pairing_shows_a_one_time_qr_code()
    {
        var vm = CreateVm();
        vm.Settings.RemotePort = 0; // any free port
        await vm.PairPhoneCommand.ExecuteAsync(null);
        Assert.True(vm.RemoteRunning, vm.RemoteStatus);
        Assert.True(vm.IsPairing);
        Assert.NotNull(vm.PairingQr);
        Assert.Matches(@"^http://\d+\.\d+\.\d+\.\d+:\d+/pair\?c=[A-Za-z0-9_-]{20,}$", vm.PairingUrl);

        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        w.Show();
        w.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        Save(w, "main-devices-pairing.png");

        vm.CancelPairingCommand.Execute(null);
        Assert.False(vm.IsPairing);
        await vm.TurnOffRemoteCommand.ExecuteAsync(null);
        Assert.False(vm.RemoteRunning);
        Assert.False(vm.Settings.RemoteEnabled);
    }

    [AvaloniaFact]
    public async Task Busy_port_falls_back_and_is_remembered()
    {
        var vm = CreateVm();
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        blocker.Start();
        var busy = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
        vm.Settings.RemotePort = busy;
        vm.FallbackPorts = [0]; // never bind real default ports from WSL (see FallbackPorts)
        await vm.PairPhoneCommand.ExecuteAsync(null);
        Assert.True(vm.RemoteRunning, vm.RemoteStatus);
        Assert.NotEqual(busy, vm.Settings.RemotePort);
        Assert.Contains($":{vm.Settings.RemotePort}/pair?c=", vm.PairingUrl);
        await vm.TurnOffRemoteCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public void Phone_commands_drive_the_app()
    {
        var vm = CreateVm();
        Assert.True(vm.Execute(new BingeRide.App.Services.Remote.RemoteCommand("mode", Mode: "Erg")));
        Assert.Equal(RideMode.Erg, vm.Ride.Mode);
        vm.Refresh();
        Assert.Equal("watts", vm.BuildRemoteState().Control);
        Assert.True(vm.Execute(new("startRide")));
        Assert.Equal(RideState.Recording, vm.Ride.State);
        var vo2 = vm.Workouts.First(e => e.Name.StartsWith("VO2max"));
        Assert.True(vm.Execute(new("startWorkout", Workout: vo2.FileName)));
        Assert.Equal(RideMode.Workout, vm.Ride.Mode);
        vm.Refresh();
        Assert.NotNull(vm.BuildRemoteState().Workout);
        Assert.False(vm.Execute(new("startWorkout", Workout: "nope.json")));
        Assert.False(vm.Execute(new("bogus")));
    }

    [AvaloniaFact]
    public void Click_buttons_drive_the_controller()
    {
        var vm = CreateVm();
        vm.Ride.OnClick(ClickButtons.Plus);
        vm.Refresh();
        Assert.Equal("13", vm.GearText);
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<Avalonia.Visual> GetVisualDescendants(this Avalonia.Visual v) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v);
}
