using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

    private MainViewModel CreateVm()
    {
        var settings = new AppSettings { Ftp = 140, WorkoutsFolder = _folder, RidesFolder = Path.Combine(_folder, "rides") };
        var vm = new MainViewModel(new DefaultPlatformServices(), settings, Path.Combine(_folder, "settings.json"));
        vm.Start(watchLibrary: false);
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
