using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BingeRide.App.Services;
using BingeRide.App.ViewModels;

namespace BingeRide.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        DataContextChanged += (_, _) => Attach();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Attach()
    {
        if (Vm is not { } vm) return;
        vm.ClipboardReader = async () => Clipboard is { } cb ? await cb.TryGetTextAsync() : null;
        vm.FilePicker = PickFilesAsync;
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import workouts",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Workouts and routes") { Patterns = ["*.json", "*.zwo", "*.erg", "*.mrc", "*.fit", "*.txt", "*.gpx", "*.tcx"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            vm.ImportPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>());
            return;
        }
        if (e.DataTransfer.TryGetText() is { Length: > 0 } text)
        {
            vm.ClipboardReader = () => Task.FromResult<string?>(text);
            vm.PasteWorkoutCommand.Execute(null);
            Attach();
        }
    }

    private void OnRideDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is RideEntry ride)
            ShowRide(ride);
    }

    private void OnRideKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RidesList.SelectedItem is not RideEntry ride) return;
        ShowRide(ride);
        e.Handled = true;
    }

    /// <summary>Opens the ride's graphs in their own window (several can be open to compare rides).</summary>
    internal RideDetailWindow ShowRide(RideEntry ride)
    {
        var vm = new RideDetailViewModel(ride);
        var window = new RideDetailWindow { DataContext = vm };
        window.Show(this);
        _ = vm.LoadAsync();
        return window;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Vm is not { } vm || FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return;
        switch (e.Key)
        {
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.PasteWorkoutCommand.Execute(null);
                break;
            case Key.Add or Key.OemPlus or Key.Up:
                vm.UpCommand.Execute(null);
                break;
            case Key.Subtract or Key.OemMinus or Key.Down:
                vm.DownCommand.Execute(null);
                break;
            case Key.Space:
                vm.TogglePauseCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
