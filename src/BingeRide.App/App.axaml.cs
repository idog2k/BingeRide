using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BingeRide.App.Services;
using BingeRide.App.ViewModels;
using BingeRide.App.Views;
using BingeRide.Core.Settings;

namespace BingeRide.App;

public partial class App : Application
{
    /// <summary>Set by the platform head before the app starts.</summary>
    public static IPlatformServices Platform { get; set; } = new DefaultPlatformServices();

    /// <summary>Start straight in the compact overlay (command line <c>--overlay</c>).</summary>
    public static bool StartInOverlay { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            var vm = new MainViewModel(Platform, settings);
            var main = new MainWindow { DataContext = vm };
            var overlay = new OverlayWindow { DataContext = vm };
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = main;

            var switcher = new WindowSwitcher(vm, main, overlay, Platform, desktop);
            vm.Start();
            switcher.ShowInitial(settings.StartCompact || StartInOverlay);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
