using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyWahoo.App.Services;
using MyWahoo.Core.Ble;
using MyWahoo.Core.Devices;
using MyWahoo.Core.Protocol;
using MyWahoo.Core.Session;
using MyWahoo.Core.Settings;
using MyWahoo.Core.Workouts;
using MyWahoo.Workouts;

namespace MyWahoo.App.ViewModels;

public sealed partial class ClickMappingItem(ClickButtons button, string label, ClickAction action, Action<ClickButtons, ClickAction> onChanged) : ObservableObject
{
    public ClickButtons Button { get; } = button;
    public string Label { get; } = label;

    [ObservableProperty]
    private ClickAction _action = action;

    partial void OnActionChanged(ClickAction value) => onChanged(Button, value);
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPlatformServices _platform;
    private readonly string? _settingsPath;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _saveSettings;
    private IBleAdapter? _adapter;
    private DeviceManager? _devices;
    private SimulatedTrainer? _demo;
    private ITrainer? _attached;

    public MainViewModel(IPlatformServices platform, AppSettings settings, string? settingsPath = null)
    {
        _platform = platform;
        _settingsPath = settingsPath;
        Settings = settings;
        Ride = new RideController(settings);
        Ride.ToggleCompactRequested += (_, _) => IsCompact = !IsCompact;
        Library = new WorkoutLibrary(settings.ResolvedWorkoutsFolder);
        Library.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshLibrary);
        Library.WorkoutArrived += (_, e) => Dispatcher.UIThread.Post(() => AnnounceWorkout(e, "New workout"));

        ClickMappings = new ObservableCollection<ClickMappingItem>(
            new (ClickButtons Button, string Label)[]
            {
                (ClickButtons.Plus, "+ button"), (ClickButtons.Minus, "− button"),
                (ClickButtons.A, "A"), (ClickButtons.B, "B"), (ClickButtons.Y, "Y"), (ClickButtons.Z, "Z"),
                (ClickButtons.Up, "D-pad up"), (ClickButtons.Down, "D-pad down"),
                (ClickButtons.Left, "D-pad left"), (ClickButtons.Right, "D-pad right"),
            }.Select(b => new ClickMappingItem(b.Button, b.Label, settings.ClickMapping.GetValueOrDefault(b.Button), OnMappingChanged)));

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => OnTick());
        _saveSettings = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveSettings.Tick += (_, _) =>
        {
            _saveSettings.Stop();
            SaveSettingsNow();
        };
        Refresh();
    }

    public AppSettings Settings { get; }
    public RideController Ride { get; }
    public WorkoutLibrary Library { get; }

    public ObservableCollection<LibraryEntry> Workouts { get; } = [];
    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<ClickMappingItem> ClickMappings { get; }

    public static IReadOnlyList<ClickAction> ClickActions { get; } = Enum.GetValues<ClickAction>();
    public static IReadOnlyList<TrainerControlPreference> ControlPreferences { get; } = Enum.GetValues<TrainerControlPreference>();

    /// <summary>Set by the window: reads text from the system clipboard.</summary>
    public Func<Task<string?>>? ClipboardReader { get; set; }

    /// <summary>Set by the window: shows an open-file dialog and returns local paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? FilePicker { get; set; }

    // ---- live ride display ----------------------------------------------------------------
    [ObservableProperty] private string _powerText = "0";
    [ObservableProperty] private string _targetText = "—";
    [ObservableProperty] private string _cadenceText = "0";
    [ObservableProperty] private string _heartRateText = "—";
    [ObservableProperty] private string _speedText = "0.0";
    [ObservableProperty] private string _distanceText = "0 m";
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private string _avgPowerText = "0";
    [ObservableProperty] private string _kjText = "0";
    [ObservableProperty] private string _gearText = "12";
    [ObservableProperty] private string _gearRatioText = "2.40";
    [ObservableProperty] private string _gradeText = "0.0%";
    [ObservableProperty] private string _ergWattsText = "0 W";
    [ObservableProperty] private string _modeLabel = "FREE";
    [ObservableProperty] private string _modeDetail = "";
    [ObservableProperty] private bool _isErgMode;
    [ObservableProperty] private bool _isSlopeMode;
    [ObservableProperty] private bool _isFreeMode = true;
    [ObservableProperty] private bool _isWorkoutMode;
    [ObservableProperty] private bool _showGearControls = true;
    [ObservableProperty] private bool _showIntensityControls;
    [ObservableProperty] private IBrush _modeBrush = Brushes.MediumPurple;

    // ---- workout ----------------------------------------------------------------------------
    [ObservableProperty] private Workout? _activeWorkout;
    [ObservableProperty] private double _workoutProgress = -1;
    [ObservableProperty] private string _stepTitle = "";
    [ObservableProperty] private string _stepTargetText = "";
    [ObservableProperty] private string _stepRemainingText = "";
    [ObservableProperty] private string _nextStepText = "";
    [ObservableProperty] private string _intensityText = "100%";
    [ObservableProperty] private string _workoutTimeText = "";
    [ObservableProperty] private string _pauseLabel = "Pause";
    [ObservableProperty] private string? _cueText;
    [ObservableProperty] private string _compactLine = "";

    // ---- library -----------------------------------------------------------------------------
    [ObservableProperty] private LibraryEntry? _selectedWorkout;
    [ObservableProperty] private string _selectedSummary = "";
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private LibraryEntry? _noticeEntry;

    // ---- devices -----------------------------------------------------------------------------
    [ObservableProperty] private string _trainerStatus = "Starting…";
    [ObservableProperty] private bool _trainerConnected;
    [ObservableProperty] private string _clickStatus = "No Zwift Click";
    [ObservableProperty] private bool _clickConnected;
    [ObservableProperty] private bool _bluetoothAvailable;

    // ---- overlay -----------------------------------------------------------------------------
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _clickThrough;
    [ObservableProperty] private IBrush _overlayBackground = Brushes.Black;

    public bool HasCue => !string.IsNullOrEmpty(CueText);
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool HasSelection => SelectedWorkout?.Workout is not null;
    public string? SelectedError => SelectedWorkout?.Error;
    public string? SelectedDescription => SelectedWorkout?.Workout?.Description;
    public bool HasActiveWorkout => ActiveWorkout is not null;
    public bool ShowGradeControls => IsSlopeMode;
    public bool ShowErgControls => IsErgMode;
    public bool ShowManualControls => !IsWorkoutMode;
    public double Ftp => Settings.Ftp;
    public int Gear => Ride.Gear;

    partial void OnCueTextChanged(string? value) => OnPropertyChanged(nameof(HasCue));
    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnActiveWorkoutChanged(Workout? value) => OnPropertyChanged(nameof(HasActiveWorkout));
    partial void OnIsSlopeModeChanged(bool value) => OnPropertyChanged(nameof(ShowGradeControls));
    partial void OnIsErgModeChanged(bool value) => OnPropertyChanged(nameof(ShowErgControls));
    partial void OnIsWorkoutModeChanged(bool value) => OnPropertyChanged(nameof(ShowManualControls));

    partial void OnSelectedWorkoutChanged(LibraryEntry? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedError));
        OnPropertyChanged(nameof(SelectedDescription));
        SelectedSummary = value?.Workout is { } w ? Summarize(w) : "";
    }

    partial void OnClickThroughChanged(bool value)
    {
        Settings.OverlayClickThrough = value;
        SaveSettingsSoon();
    }

    /// <summary>Loads the library, starts Bluetooth and the 4 Hz ride loop.</summary>
    public void Start(bool watchLibrary = true)
    {
        try
        {
            Library.Start(watchLibrary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"Workouts folder unavailable: {ex.Message}");
        }

        _adapter = _platform.CreateBleAdapter();
        BluetoothAvailable = _adapter is not null;
        if (_adapter is not null)
        {
            _devices = new DeviceManager(_adapter, Settings.ToTrainerOptions, Settings.ToClickOptions)
            {
                PreferredTrainer = Settings.TrainerAddress,
                ClicksEnabled = Settings.ClickEnabled,
            };
            _devices.TrainerChanged += (_, _) => Dispatcher.UIThread.Post(SyncDevices);
            _devices.ClicksChanged += (_, _) => Dispatcher.UIThread.Post(SyncDevices);
            _devices.DiscoveredChanged += (_, _) => Dispatcher.UIThread.Post(SyncDevices);
            _devices.ClickPressed += (_, b) => Dispatcher.UIThread.Post(() =>
            {
                Ride.OnClick(b);
                Refresh();
            });
            _devices.Log += (_, m) => Dispatcher.UIThread.Post(() => AddLog(m));
            try
            {
                _devices.Start();
                AddLog("Scanning for KICKR and Zwift Click…");
            }
            catch (Exception ex)
            {
                AddLog($"Bluetooth failed to start: {ex.Message}");
                BluetoothAvailable = false;
            }
        }
        else
        {
            AddLog("Bluetooth LE is not available; use the demo trainer to try the app.");
        }
        ClickThrough = Settings.OverlayClickThrough;
        SyncDevices();
        _tick.Start();
    }

    private void OnTick()
    {
        Ride.Tick(DateTimeOffset.UtcNow);
        Refresh();
    }

    /// <summary>Copies the ride state into display properties (cheap: unchanged values don't notify).</summary>
    public void Refresh()
    {
        var r = Ride;
        PowerText = $"{r.PowerW:0}";
        CadenceText = $"{r.CadenceRpm:0}";
        HeartRateText = r.HeartRate is { } hr ? $"{hr}" : "—";
        SpeedText = $"{r.SpeedKmh:0.0}";
        DistanceText = Fmt.Distance(r.DistanceM);
        ElapsedText = Fmt.Duration(r.RideSeconds);
        AvgPowerText = $"{r.AveragePowerW:0}";
        KjText = $"{r.WorkKj:0}";
        GearText = $"{r.Gear}";
        GearRatioText = $"{r.GearRatio:0.00}";
        GradeText = Fmt.Grade(r.EffectiveGrade);
        ErgWattsText = $"{r.ErgWatts} W";

        IsErgMode = r.Mode == RideMode.Erg;
        IsSlopeMode = r.Mode == RideMode.Slope;
        IsFreeMode = r.Mode == RideMode.Free;
        IsWorkoutMode = r.Mode == RideMode.Workout;
        var kind = r.EffectiveKind;
        ShowGearControls = kind != StepKind.Erg;
        ShowIntensityControls = IsWorkoutMode && kind == StepKind.Erg;
        TargetText = r.EffectiveErgWatts is { } erg && kind == StepKind.Erg ? $"{erg} W" : Fmt.Grade(r.EffectiveGrade);

        (ModeLabel, ModeBrush) = (r.Mode, kind) switch
        {
            (RideMode.Erg, _) => ("ERG", Brushes.DarkOrange),
            (RideMode.Slope, _) => ("SLOPE", Brushes.MediumSeaGreen),
            (RideMode.Free, _) => ("FREE", Brushes.MediumPurple),
            (_, StepKind.Erg) => ("WORKOUT · ERG", (IBrush)Brushes.DarkOrange),
            (_, StepKind.Slope) => ("WORKOUT · SLOPE", Brushes.MediumSeaGreen),
            _ => ("WORKOUT · FREE", Brushes.MediumPurple),
        };

        var player = r.Player;
        ActiveWorkout = player?.Workout;
        if (player is not null)
        {
            WorkoutProgress = player.Progress;
            IntensityText = $"{r.WorkoutIntensityPct:0}%";
            PauseLabel = r.IsWorkoutPaused ? "Resume" : "Pause";
            WorkoutTimeText = player.Workout.HasDistanceSteps
                ? $"{Fmt.Duration(player.ElapsedSec)} · {Fmt.Distance(r.DistanceM)}"
                : $"{Fmt.Duration(player.ElapsedSec)} / {Fmt.Duration(player.Workout.TotalDurationSec)}";
            if (player.CurrentStep is { } step)
            {
                StepTitle = Fmt.StepTitle(step, Settings.Ftp);
                StepTargetText = Fmt.StepTarget(step, Settings.Ftp, r.WorkoutIntensityPct);
                StepRemainingText = step.IsDistanceBased ? Fmt.Distance(player.StepRemaining) : Fmt.Duration(player.StepRemaining);
                NextStepText = player.NextStep is { } next
                    ? $"Next: {Fmt.StepTitle(next, Settings.Ftp)} · {Fmt.StepTarget(next, Settings.Ftp, r.WorkoutIntensityPct)} · {Fmt.StepLength(next)}"
                    : "Last step";
            }
            else
            {
                StepTitle = "Workout complete";
                StepTargetText = "Free";
                StepRemainingText = "0:00";
                NextStepText = "Shift freely, or pick another workout";
            }
            var status = r.IsWorkoutPaused ? "PAUSED · " : r.IsAutoPaused ? "Pedal to resume · " : "";
            CompactLine = status + StepTitle;
            ModeDetail = kind == StepKind.Erg
                ? "Trainer holds the target. Click +/− nudges the intensity."
                : "Ride it like a road. Click +/− shifts gears.";
        }
        else
        {
            WorkoutProgress = -1;
            CompactLine = $"{DistanceText} · {ElapsedText} · avg {AvgPowerText} W";
            ModeDetail = r.Mode switch
            {
                RideMode.Erg => "Trainer holds your power. Click +/− changes watts.",
                RideMode.Slope => "Simulated climb. Click +/− shifts gears.",
                _ => "Flat road. Click +/− shifts gears.",
            };
        }
        CueText = r.Cue;
        OverlayBackground = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(Settings.OverlayOpacity, 0.05, 1) * 255), 0x0B, 0x0D, 0x12));
        OnPropertyChanged(nameof(Ftp));
        OnPropertyChanged(nameof(Gear));
    }

    // ---- ride commands -----------------------------------------------------------------------
    [RelayCommand]
    private void SetMode(string mode)
    {
        if (Enum.TryParse<RideMode>(mode, out var m)) Ride.SetMode(m);
        Refresh();
    }

    [RelayCommand] private void Up() { Ride.Up(); Refresh(); }
    [RelayCommand] private void Down() { Ride.Down(); Refresh(); }
    [RelayCommand] private void GearUp() { Ride.SetGear(Ride.Gear + 1); Refresh(); }
    [RelayCommand] private void GearDown() { Ride.SetGear(Ride.Gear - 1); Refresh(); }
    [RelayCommand] private void ErgUp() { Ride.SetErgWatts(Ride.ErgWatts + Settings.ErgStepWatts); Refresh(); }
    [RelayCommand] private void ErgDown() { Ride.SetErgWatts(Ride.ErgWatts - Settings.ErgStepWatts); Refresh(); }
    [RelayCommand] private void GradeUp() { Ride.AdjustGrade(+1); Refresh(); }
    [RelayCommand] private void GradeDown() { Ride.AdjustGrade(-1); Refresh(); }
    [RelayCommand] private void IntensityUp() { Ride.SetWorkoutIntensity(Ride.WorkoutIntensityPct + Settings.WorkoutIntensityStepPct); Refresh(); }
    [RelayCommand] private void IntensityDown() { Ride.SetWorkoutIntensity(Ride.WorkoutIntensityPct - Settings.WorkoutIntensityStepPct); Refresh(); }
    [RelayCommand] private void TogglePause() { Ride.TogglePause(); Refresh(); }
    [RelayCommand] private void SkipStep() { Ride.SkipStep(); Refresh(); }
    [RelayCommand] private void PreviousStep() { Ride.PreviousStep(); Refresh(); }
    [RelayCommand] private void StopWorkout() { Ride.StopWorkout(); Refresh(); }

    [RelayCommand]
    private void StartWorkout(LibraryEntry? entry)
    {
        entry ??= SelectedWorkout;
        if (entry?.Workout is not { } w) return;
        Ride.StartWorkout(w);
        Notice = null;
        AddLog($"Started workout \"{w.Name}\"");
        Refresh();
    }

    [RelayCommand] private void StartNoticeWorkout() => StartWorkout(NoticeEntry);
    [RelayCommand] private void DismissNotice() => Notice = null;

    // ---- overlay commands --------------------------------------------------------------------
    [RelayCommand] private void ToggleCompact() => IsCompact = !IsCompact;
    [RelayCommand] private void ToggleClickThrough() => ClickThrough = !ClickThrough;

    [RelayCommand]
    private void OpacityUp() => SetOverlayOpacity(Settings.OverlayOpacity + 0.1);

    [RelayCommand]
    private void OpacityDown() => SetOverlayOpacity(Settings.OverlayOpacity - 0.1);

    public double OverlayOpacity
    {
        get => Settings.OverlayOpacity;
        set => SetOverlayOpacity(value);
    }

    private void SetOverlayOpacity(double value)
    {
        Settings.OverlayOpacity = Math.Round(Math.Clamp(value, 0.1, 1), 2);
        OnPropertyChanged(nameof(OverlayOpacity));
        SaveSettingsSoon();
        Refresh();
    }

    public double OverlayScale
    {
        get => Settings.OverlayScale;
        set
        {
            Settings.OverlayScale = Math.Round(Math.Clamp(value, 0.6, 2.0), 2);
            OnPropertyChanged();
            SaveSettingsSoon();
        }
    }

    // ---- library commands --------------------------------------------------------------------
    [RelayCommand]
    private async Task PasteWorkout()
    {
        var text = ClipboardReader is null ? null : await ClipboardReader();
        if (string.IsNullOrWhiteSpace(text))
        {
            Notice = "The clipboard is empty. Copy Claude's whole answer (or just the workout block) first.";
            NoticeEntry = null;
            return;
        }
        if (WorkoutImporter.TryImportFromPastedText(text, out var result, out var errors) && result is not null)
        {
            var entry = Library.Save(result);
            RefreshLibrary();
            AnnounceWorkout(entry, "Pasted");
        }
        else
        {
            Notice = "No workout found in the clipboard" + (errors.Count > 0 ? $": {errors[0]}" : ".");
            NoticeEntry = null;
        }
    }

    [RelayCommand]
    private async Task ImportFile()
    {
        if (FilePicker is null) return;
        ImportPaths(await FilePicker());
    }

    /// <summary>Imports files (from the picker or drag &amp; drop) by copying them into the library.</summary>
    public void ImportPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var result = WorkoutImporter.ImportFile(path);
                var inLibrary = string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Library.Folder), StringComparison.OrdinalIgnoreCase);
                var entry = inLibrary ? Library.Entries.FirstOrDefault(e => e.Path == path) : Library.Save(result, path);
                RefreshLibrary();
                if (entry is not null) AnnounceWorkout(entry, "Imported");
            }
            catch (Exception ex)
            {
                Notice = $"Couldn't import {Path.GetFileName(path)}: {ex.Message}";
                NoticeEntry = null;
            }
        }
    }

    [RelayCommand] private void OpenWorkoutsFolder() => _platform.OpenFolder(Library.Folder);
    [RelayCommand] private void ReloadLibrary() => Library.Rescan();

    // ---- device commands ---------------------------------------------------------------------
    [RelayCommand]
    private async Task UseDemoTrainer()
    {
        if (_devices?.Trainer is not null) await _devices.DisconnectTrainerAsync();
        if (_demo is not null) return;
        _demo = new SimulatedTrainer(Settings.RiderKg + Settings.BikeKg);
        AddLog("Demo trainer connected (simulated rider)");
        SyncDevices();
    }

    [RelayCommand]
    private async Task DisconnectTrainer()
    {
        if (_demo is { } demo)
        {
            _demo = null;
            await demo.DisposeAsync();
        }
        if (_devices is not null) await _devices.DisconnectTrainerAsync();
        SyncDevices();
    }

    [RelayCommand]
    private async Task ConnectDevice(DiscoveredDevice? device)
    {
        if (device is null || _devices is null || device.Role != DeviceRole.Trainer) return;
        if (_demo is { } demo)
        {
            _demo = null;
            await demo.DisposeAsync();
        }
        _devices.AutoConnectTrainer = true;
        Settings.TrainerAddress = device.Address;
        SaveSettingsSoon();
        await _devices.ConnectTrainerAsync(device.Address);
    }

    private void SyncDevices()
    {
        ITrainer? trainer = _demo ?? _devices?.Trainer;
        if (!ReferenceEquals(trainer, _attached))
        {
            if (_attached is not null) _attached.DataReceived -= OnTrainerData;
            _attached = trainer;
            if (trainer is not null) trainer.DataReceived += OnTrainerData;
            Ride.Trainer = trainer;
            if (trainer is KickrTrainer k && Settings.TrainerAddress != k.Address)
            {
                Settings.TrainerAddress = k.Address;
                SaveSettingsSoon();
            }
        }

        TrainerConnected = trainer is not null;
        TrainerStatus = trainer switch
        {
            SimulatedTrainer => "Demo trainer",
            not null => $"{trainer.Name} · {(trainer.ControlPlane == ControlPlane.Zwift ? "virtual shifting" : "FTMS")}",
            null when _devices is not null => _devices.TrainerStatus ?? "Searching…",
            _ => "Bluetooth off · try the demo trainer",
        };

        var clicks = _devices?.Clicks ?? [];
        ClickConnected = clicks.Count > 0;
        ClickStatus = clicks.Count == 0
            ? "No Zwift Click"
            : string.Join(" · ", clicks.Select(c => c.IsLocked ? $"{c.DisplayName} locked" : c.DisplayName));

        if (_devices is not null)
        {
            Devices.Clear();
            foreach (var d in _devices.Discovered) Devices.Add(d);
        }
    }

    private void OnTrainerData(object? sender, TrainerData data) => Ride.OnTrainerData(data);

    // ---- settings ----------------------------------------------------------------------------
    public decimal FtpSetting
    {
        get => Settings.Ftp;
        set => UpdateSetting(() => Settings.Ftp = (int)Math.Clamp(value, 50, 600));
    }

    public decimal RiderKgSetting
    {
        get => (decimal)Settings.RiderKg;
        set => UpdateSetting(() => Settings.RiderKg = (double)Math.Clamp(value, 30, 200));
    }

    public decimal BikeKgSetting
    {
        get => (decimal)Settings.BikeKg;
        set => UpdateSetting(() => Settings.BikeKg = (double)Math.Clamp(value, 3, 30));
    }

    public decimal ErgStepSetting
    {
        get => Settings.ErgStepWatts;
        set => UpdateSetting(() => Settings.ErgStepWatts = (int)Math.Clamp(value, 1, 50));
    }

    public decimal GradeStepSetting
    {
        get => (decimal)Settings.GradeStepPct;
        set => UpdateSetting(() => Settings.GradeStepPct = (double)Math.Clamp(value, 0.1m, 5));
    }

    public decimal IntensityStepSetting
    {
        get => (decimal)Settings.WorkoutIntensityStepPct;
        set => UpdateSetting(() => Settings.WorkoutIntensityStepPct = (double)Math.Clamp(value, 1, 10));
    }

    public decimal ChainringSetting
    {
        get => Settings.PhysicalChainring;
        set => UpdateSetting(() => Settings.PhysicalChainring = (int)Math.Clamp(value, 20, 60));
    }

    public decimal CogSetting
    {
        get => Settings.PhysicalCog;
        set => UpdateSetting(() => Settings.PhysicalCog = (int)Math.Clamp(value, 10, 36));
    }

    public bool AutoPauseSetting
    {
        get => Settings.AutoPause;
        set => UpdateSetting(() => Settings.AutoPause = value);
    }

    public bool ErgViaFtmsSetting
    {
        get => Settings.ErgViaFtms;
        set => UpdateSetting(() => Settings.ErgViaFtms = value);
    }

    public bool ClickIdleResetSetting
    {
        get => Settings.ClickResetIdleRightPuck;
        set => UpdateSetting(() => Settings.ClickResetIdleRightPuck = value);
    }

    public bool ClickRebootLeftSetting
    {
        get => Settings.ClickRebootLockedLeftPuck;
        set => UpdateSetting(() => Settings.ClickRebootLockedLeftPuck = value);
    }

    public TrainerControlPreference TrainerControlSetting
    {
        get => Settings.TrainerControl;
        set => UpdateSetting(() => Settings.TrainerControl = value);
    }

    public bool StartCompactSetting
    {
        get => Settings.StartCompact;
        set => UpdateSetting(() => Settings.StartCompact = value);
    }

    public string WorkoutsFolder => Library.Folder;

    private void UpdateSetting(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply();
        OnPropertyChanged(name);
        SaveSettingsSoon();
        if (name == nameof(FtpSetting))
        {
            SelectedSummary = SelectedWorkout?.Workout is { } w ? Summarize(w) : "";
            Refresh();
        }
    }

    private void OnMappingChanged(ClickButtons button, ClickAction action)
    {
        Settings.ClickMapping[button] = action;
        SaveSettingsSoon();
    }

    private void SaveSettingsSoon()
    {
        _saveSettings.Stop();
        _saveSettings.Start();
    }

    public void SaveSettingsNow()
    {
        try
        {
            SettingsStore.Save(Settings, _settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"Couldn't save settings: {ex.Message}");
        }
    }

    // ---- helpers -----------------------------------------------------------------------------
    private void RefreshLibrary()
    {
        var selectedPath = SelectedWorkout?.Path;
        Workouts.Clear();
        foreach (var e in Library.Entries) Workouts.Add(e);
        SelectedWorkout = Workouts.FirstOrDefault(e => e.Path == selectedPath) ?? Workouts.FirstOrDefault(e => e.Workout is not null);
    }

    private void AnnounceWorkout(LibraryEntry entry, string verb)
    {
        RefreshLibrary();
        SelectedWorkout = Workouts.FirstOrDefault(e => e.Path == entry.Path) ?? entry;
        if (entry.Workout is null)
        {
            Notice = $"{entry.FileName}: {entry.Error}";
            NoticeEntry = null;
            return;
        }
        Notice = $"{verb}: {entry.Name} ({Fmt.Duration(entry.Workout.TotalDurationSec)})";
        NoticeEntry = entry;
        AddLog(Notice);
    }

    private string Summarize(Workout w)
    {
        var s = WorkoutStats.Compute(w, Settings.Ftp);
        var parts = new List<string>();
        if (s.TotalSeconds > 0) parts.Add(Fmt.Duration(s.TotalSeconds));
        if (s.TotalMeters > 0) parts.Add(Fmt.Distance(s.TotalMeters));
        if (s.AverageWatts > 0) parts.Add($"avg {s.AverageWatts:0} W");
        if (s.Tss > 0) parts.Add($"TSS {s.Tss:0}");
        if (s.IntensityFactor > 0) parts.Add($"IF {s.IntensityFactor:0.00}");
        return string.Join(" · ", parts);
    }

    private void AddLog(string message)
    {
        LogLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogLines.Count > 300) LogLines.RemoveAt(LogLines.Count - 1);
    }

    public async ValueTask DisposeAsync()
    {
        _tick.Stop();
        if (_saveSettings.IsEnabled)
        {
            _saveSettings.Stop();
            SaveSettingsNow();
        }
        Library.Dispose();
        if (_demo is not null) await _demo.DisposeAsync();
        if (_devices is not null) await _devices.DisposeAsync();
    }
}
