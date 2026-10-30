using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MyWahoo.App.Services;
using MyWahoo.App.ViewModels;

namespace MyWahoo.App.Views;

/// <summary>
/// Swaps between the full window and the compact always-on-top overlay, applies click-through,
/// keeps the overlay on top of full-screen video, and wires global hotkeys.
/// </summary>
public sealed class WindowSwitcher
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly OverlayWindow _overlay;
    private readonly IPlatformServices _platform;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly DispatcherTimer _keepOnTop;
    private readonly IDisposable? _hotkeys;
    private readonly IDisposable? _activation;
    private bool _exiting;

    public WindowSwitcher(MainViewModel vm, MainWindow main, OverlayWindow overlay, IPlatformServices platform, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _vm = vm;
        _main = main;
        _overlay = overlay;
        _platform = platform;
        _desktop = desktop;

        _keepOnTop = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _platform.KeepOnTop(_overlay));
        _vm.PropertyChanged += OnVmChanged;
        _main.Closing += (_, _) => Exit();
        _overlay.CloseRequested += (_, _) => Exit();
        _overlay.Opened += (_, _) => _platform.SetClickThrough(_overlay, _vm.ClickThrough);
        _overlay.PositionChanged += (_, e) =>
        {
            _vm.Settings.OverlayX = e.Point.X;
            _vm.Settings.OverlayY = e.Point.Y;
        };

        _hotkeys = _platform.RegisterGlobalHotkeys(
            () => Dispatcher.UIThread.Post(() => _vm.IsCompact = !_vm.IsCompact),
            () => Dispatcher.UIThread.Post(() => _vm.ClickThrough = !_vm.ClickThrough));
        _activation = _platform.ListenForActivation(overlay => Dispatcher.UIThread.Post(() =>
        {
            if (_vm.IsCompact == overlay)
            {
                // Already in that mode: just bring it forward (and make sure the overlay is clickable).
                _vm.ClickThrough = false;
                if (overlay) _overlay.Activate();
                else _main.Activate();
            }
            _vm.IsCompact = overlay;
        }));
    }

    public void ShowInitial(bool compact)
    {
        if (compact) _vm.IsCompact = true;
        else _main.Show();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsCompact):
                if (_vm.IsCompact) ShowOverlay();
                else ShowMain();
                break;
            case nameof(MainViewModel.ClickThrough):
                _platform.SetClickThrough(_overlay, _vm.ClickThrough);
                break;
        }
    }

    private void ShowOverlay()
    {
        // Always open clickable: click-through is switched on deliberately, never left on from last time.
        _vm.ClickThrough = false;
        if (_vm.Settings.OverlayX is { } x && _vm.Settings.OverlayY is { } y)
        {
            _overlay.WindowStartupLocation = WindowStartupLocation.Manual;
            _overlay.Position = new PixelPoint(x, y);
        }
        else if (_main.Screens.Primary is { } screen)
        {
            // Default: top-right corner of the main screen.
            _overlay.WindowStartupLocation = WindowStartupLocation.Manual;
            _overlay.Position = new PixelPoint(screen.WorkingArea.Right - (int)(380 * screen.Scaling), screen.WorkingArea.Y + (int)(24 * screen.Scaling));
        }
        _overlay.Show();
        _main.Hide();
        _platform.SetClickThrough(_overlay, _vm.ClickThrough);
        _keepOnTop.Start();
    }

    private void ShowMain()
    {
        _keepOnTop.Stop();
        _vm.ClickThrough = false;
        _main.Show();
        _main.Activate();
        _overlay.Hide();
    }

    private async void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _keepOnTop.Stop();
        _hotkeys?.Dispose();
        _activation?.Dispose();
        _vm.SaveSettingsNow(); // overlay position and anything changed in the last second
        await _vm.DisposeAsync();
        _desktop.Shutdown();
    }
}
