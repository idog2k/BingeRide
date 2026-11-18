using Avalonia.Controls;
using Avalonia.Input;

namespace BingeRide.App.Views;

/// <summary>A saved ride's totals and its power / heart rate / cadence timeline (double-click a ride in the Rides tab).</summary>
public partial class RideDetailWindow : Window
{
    public RideDetailWindow() => InitializeComponent();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Home) Chart.ResetZoom();
        else return;
        e.Handled = true;
    }
}
