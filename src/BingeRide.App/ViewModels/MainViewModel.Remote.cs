using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BingeRide.App.Services.Remote;
using BingeRide.Core.Session;
using BingeRide.Core.Settings;
using BingeRide.Core.Workouts;
using QRCoder;

namespace BingeRide.App.ViewModels;

public sealed record PairedPhoneRow(string Id, string Name, string Detail);

/// <summary>The phone web remote: QR pairing, the paired-phones list, and the bridge that answers the phone.</summary>
public sealed partial class MainViewModel : IRemoteBackend
{
    private RemoteServer? _server;
    private RemotePairing? _pairing;
    private DispatcherTimer? _pairingTimer;

    public ObservableCollection<PairedPhoneRow> PairedPhones { get; } = [];

    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private Bitmap? _pairingQr;
    [ObservableProperty] private string _pairingUrl = "";
    [ObservableProperty] private string _pairingExpiresText = "";
    [ObservableProperty] private string _remoteStatus = "Off";
    [ObservableProperty] private bool _remoteRunning;

    public bool HasPairedPhones => PairedPhones.Count > 0;

    private RemotePairing Pairing => _pairing ??= CreatePairing();

    private RemotePairing CreatePairing()
    {
        var pairing = new RemotePairing(Settings, () => Dispatcher.UIThread.Post(SaveSettingsNow));
        pairing.PhonesChanged += (_, _) => Dispatcher.UIThread.Post(OnPhonesChanged);
        return pairing;
    }

    /// <summary>Starts the server if a phone was paired before (called from <see cref="Start"/>).</summary>
    private async Task StartRemoteIfEnabledAsync()
    {
        RefreshPairedPhones();
        if (Settings.RemoteEnabled) await StartRemoteAsync();
    }

    /// <summary>
    /// Ports tried after the saved one. Tests replace this with 0 (any free port): under WSL's mirrored
    /// networking, a port a Linux process has used stays blocked for Windows afterwards.
    /// </summary>
    internal int[] FallbackPorts { get; set; } =
        [AppSettings.DefaultRemotePort, AppSettings.DefaultRemotePort + 1, AppSettings.DefaultRemotePort + 2, 28765, 38765];

    private async Task<bool> StartRemoteAsync()
    {
        if (_server is { IsRunning: true }) return true;
        // The saved port first (so a phone's home-screen icon keeps working), then the fallbacks.
        // Port 0 means "any free port".
        int[] candidates = Settings.RemotePort == 0 ? [0] : [Settings.RemotePort, .. FallbackPorts];
        Exception? lastError = null;
        foreach (var port in candidates.Distinct())
        {
            var server = new RemoteServer();
            try
            {
                await server.StartAsync(port, Pairing, this);
                _server = server;
                break;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                lastError = ex;
                AddLog($"Phone remote: port {port} unavailable ({ex.GetBaseException().Message})");
                await server.DisposeAsync();
            }
        }
        if (_server is null)
        {
            RemoteRunning = false;
            RemoteStatus = $"Couldn't start the phone remote: {lastError?.GetBaseException().Message}";
            return false;
        }
        if (Settings.RemotePort != 0 && _server.Port != Settings.RemotePort)
        {
            Settings.RemotePort = _server.Port;
            SaveSettingsSoon();
        }
        RemoteRunning = true;
        var ip = RemoteServer.LanAddresses().FirstOrDefault() ?? "this-pc";
        RemoteStatus = $"On · http://{ip}:{_server.Port}";
        AddLog($"Phone remote listening on {string.Join(", ", RemoteServer.LanAddresses().Select(a => $"{a}:{_server.Port}"))}");
        return true;
    }

    /// <summary>Shows a QR code with a one-time pairing code (5 minutes); the server starts on first use.</summary>
    [RelayCommand]
    private async Task PairPhone()
    {
        if (!await StartRemoteAsync()) return;
        if (!Settings.RemoteEnabled)
        {
            Settings.RemoteEnabled = true;
            SaveSettingsSoon();
        }
        var ip = RemoteServer.LanAddresses().FirstOrDefault();
        if (ip is null)
        {
            RemoteStatus = "No network connection found: connect the PC to your Wi-Fi/router first.";
            return;
        }
        var code = Pairing.NewCode();
        PairingUrl = $"http://{ip}:{_server!.Port}/pair?c={code}";
        PairingQr = QrBitmap(PairingUrl);
        IsPairing = true;
        _pairingTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdatePairingCountdown());
        _pairingTimer.Start();
        UpdatePairingCountdown();
    }

    [RelayCommand]
    private void CancelPairing()
    {
        Pairing.CancelCode();
        EndPairingUi();
    }

    [RelayCommand]
    private void ForgetPhone(PairedPhoneRow? phone)
    {
        if (phone is not null) Pairing.Forget(phone.Id);
    }

    /// <summary>Stops the web server; paired phones stay paired for when it's turned back on.</summary>
    [RelayCommand]
    private async Task TurnOffRemote()
    {
        CancelPairing();
        Settings.RemoteEnabled = false;
        SaveSettingsSoon();
        if (_server is { } server)
        {
            _server = null;
            await server.StopAsync();
        }
        RemoteRunning = false;
        RemoteStatus = "Off";
        AddLog("Phone remote turned off");
    }

    private void UpdatePairingCountdown()
    {
        if (Pairing.CodeExpires is not { } expires || expires <= DateTimeOffset.UtcNow)
        {
            EndPairingUi();
            return;
        }
        var left = expires - DateTimeOffset.UtcNow;
        PairingExpiresText = $"Code valid for {(int)left.TotalMinutes}:{left.Seconds:00} · works once";
    }

    private void EndPairingUi()
    {
        _pairingTimer?.Stop();
        IsPairing = false;
        PairingQr = null;
        PairingUrl = "";
    }

    private void OnPhonesChanged()
    {
        var before = PairedPhones.Count;
        RefreshPairedPhones();
        if (PairedPhones.Count > before)
        {
            EndPairingUi();
            Notice = $"📱 {PairedPhones[^1].Name} paired. Add the page to its Home Screen (Share → Add to Home Screen) for an app icon.";
            NoticeEntry = null;
            AddLog($"Paired {PairedPhones[^1].Name}");
        }
    }

    private void RefreshPairedPhones()
    {
        PairedPhones.Clear();
        foreach (var p in Pairing.Phones)
        {
            var seen = p.LastSeen is { } t ? $"last used {t.ToLocalTime():d MMM HH:mm}" : "never used";
            PairedPhones.Add(new PairedPhoneRow(p.Id, p.Name, $"paired {p.PairedAt.ToLocalTime():d MMM yyyy} · {seen}"));
        }
        OnPropertyChanged(nameof(HasPairedPhones));
    }

    private static Bitmap QrBitmap(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return new Bitmap(new MemoryStream(png));
    }

    private async ValueTask StopRemoteAsync()
    {
        if (_server is { } server)
        {
            _server = null;
            await server.StopAsync();
        }
    }

    // ---- IRemoteBackend: everything runs on the UI thread ----------------------------------------
    Task<RemoteState> IRemoteBackend.GetStateAsync() => Dispatcher.UIThread.InvokeAsync(BuildRemoteState).GetTask();

    Task<IReadOnlyList<RemoteWorkout>> IRemoteBackend.GetWorkoutsAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        (IReadOnlyList<RemoteWorkout>)Workouts.Select(e => new RemoteWorkout(
            e.FileName, e.Name, e.Workout is { } w ? Summarize(w) : "", e.Workout is not null, e.Error)).ToList()).GetTask();

    Task<IReadOnlyList<RemoteRide>> IRemoteBackend.GetRidesAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        (IReadOnlyList<RemoteRide>)RideEntries.Select(r => new RemoteRide(r.Title, r.DateText, r.StatsText, r.FoodText, r.LandmarkText)).ToList()).GetTask();

    Task<bool> IRemoteBackend.ExecuteAsync(RemoteCommand command) => Dispatcher.UIThread.InvokeAsync(() => Execute(command)).GetTask();

    internal bool Execute(RemoteCommand command)
    {
        switch (command.Action)
        {
            case "up": UpCommand.Execute(null); break;
            case "down": DownCommand.Execute(null); break;
            case "gearUp": GearUpCommand.Execute(null); break;
            case "gearDown": GearDownCommand.Execute(null); break;
            case "ergUp": ErgUpCommand.Execute(null); break;
            case "ergDown": ErgDownCommand.Execute(null); break;
            case "gradeUp": GradeUpCommand.Execute(null); break;
            case "gradeDown": GradeDownCommand.Execute(null); break;
            case "intensityUp": IntensityUpCommand.Execute(null); break;
            case "intensityDown": IntensityDownCommand.Execute(null); break;
            case "startRide": StartRideCommand.Execute(null); break;
            case "togglePause": TogglePauseCommand.Execute(null); break;
            case "finishRide": FinishRideCommand.Execute(null); break;
            case "endWorkout": StopWorkoutCommand.Execute(null); break;
            case "skipStep": SkipStepCommand.Execute(null); break;
            case "previousStep": PreviousStepCommand.Execute(null); break;
            case "mode" when command.Mode is "Erg" or "Slope" or "Free":
                SetModeCommand.Execute(command.Mode);
                break;
            case "startWorkout":
                var entry = Workouts.FirstOrDefault(e => e.FileName == command.Workout && e.Workout is not null);
                if (entry is null) return false;
                StartWorkoutCommand.Execute(entry);
                break;
            default:
                return false;
        }
        return true;
    }

    internal RemoteState BuildRemoteState()
    {
        var r = Ride;
        var player = r.Player;
        var control = (r.Mode, r.EffectiveKind) switch
        {
            (RideMode.Erg, _) => "watts",
            (RideMode.Workout, StepKind.Erg) => "intensity",
            _ => "gears",
        };
        var controlValue = control switch { "watts" => ErgWattsText, "intensity" => IntensityText, _ => GearText };
        RemoteWorkoutProgress? workout = player is null ? null : new RemoteWorkoutProgress(
            WorkoutBannerText, StepTitle, StepTargetText, StepRemainingText, NextStepText, Math.Max(0, WorkoutProgress),
            IntensityText, r.IsPaused, player.IsFinished);
        return new RemoteState(
            PowerText, TargetText, CadenceText, HeartRateText, SpeedText, DistanceText, ElapsedText, AvgPowerText,
            KcalText, BurnedFoodText, NextFoodText, GearText, GearRatioText, GradeText,
            r.Mode.ToString(), ModeLabel, Hex(ModeBrush), r.State.ToString(), Fmt.Duration(r.RideTimerSeconds),
            control, controlValue,
            new RemoteDevice(TrainerConnected, TrainerStatus),
            new RemoteDevice(ClickConnected, ClickStatus),
            new RemoteDevice(HeartRateConnected, HeartRateStatus),
            CueText, workout, Settings.ShowFood,
            Settings.ShowLandmarks, DistanceLandmarkText, LifetimeText, LifetimeNextText, LifetimeFraction);
    }

    private static string Hex(IBrush brush) =>
        brush is ISolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "#9370DB";
}
