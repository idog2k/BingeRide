using System.Diagnostics;
using Avalonia.Controls;
using BingeRide.Core.Ble;

namespace BingeRide.App.Services;

/// <summary>OS-specific pieces, supplied by the Windows head (no-ops elsewhere, e.g. in tests on Linux).</summary>
public interface IPlatformServices
{
    /// <summary>The Bluetooth LE radio, or null when unavailable (then only the demo trainer works).</summary>
    IBleAdapter? CreateBleAdapter();

    /// <summary>Lets mouse clicks pass through the window to whatever is underneath (e.g. Netflix).</summary>
    void SetClickThrough(Window window, bool enabled);

    /// <summary>Re-asserts topmost z-order; full-screen video players sometimes push topmost windows down.</summary>
    void KeepOnTop(Window window);

    /// <summary>System-wide hotkeys that still work while the overlay ignores the mouse.</summary>
    IDisposable? RegisterGlobalHotkeys(Action toggleCompact, Action toggleClickThrough);

    void OpenFolder(string path);

    /// <summary>
    /// Called when the app is launched a second time (e.g. from the "BingeRide Overlay" shortcut while it's running):
    /// the running copy shows its overlay (true) or full window (false) instead of a second copy starting.
    /// </summary>
    IDisposable? ListenForActivation(Action<bool> show);
}

public sealed class DefaultPlatformServices : IPlatformServices
{
    public IBleAdapter? CreateBleAdapter() => null;
    public void SetClickThrough(Window window, bool enabled) { }
    public void KeepOnTop(Window window) { }
    public IDisposable? RegisterGlobalHotkeys(Action toggleCompact, Action toggleClickThrough) => null;
    public IDisposable? ListenForActivation(Action<bool> show) => null;

    public void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best effort: nothing useful to do if there's no file manager.
        }
    }
}
