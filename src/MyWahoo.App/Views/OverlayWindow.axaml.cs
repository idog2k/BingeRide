using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyWahoo.App.ViewModels;

namespace MyWahoo.App.Views;

/// <summary>Compact, borderless, always-on-top, see-through ride display; drag anywhere to move.</summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Panel.PointerPressed += OnPanelPressed;
    }

    /// <summary>The ✕ button: quitting is the switcher's job (it also shuts down Bluetooth cleanly).</summary>
    public event EventHandler? CloseRequested;

    private void OnPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
            BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MainViewModel vm) return;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus or Key.Up: vm.UpCommand.Execute(null); break;
            case Key.Subtract or Key.OemMinus or Key.Down: vm.DownCommand.Execute(null); break;
            case Key.Space: vm.TogglePauseCommand.Execute(null); break;
            case Key.Escape: vm.ToggleCompactCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 on the overlay: go back to the full window instead of leaving a headless app running.
        if (!e.IsProgrammatic && DataContext is MainViewModel vm && vm.IsCompact)
        {
            e.Cancel = true;
            vm.IsCompact = false;
        }
        base.OnClosing(e);
    }
}
