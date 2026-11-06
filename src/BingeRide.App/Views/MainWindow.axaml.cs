using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
