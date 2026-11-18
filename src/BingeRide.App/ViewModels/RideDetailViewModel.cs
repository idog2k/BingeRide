using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BingeRide.App.Services;
using BingeRide.Core.Session;
using BingeRide.Workouts.Export;

namespace BingeRide.App.ViewModels;

public sealed record StatTile(string Label, string Value);

/// <summary>One saved ride opened from the Rides list: its totals plus the power / heart rate / cadence timeline.</summary>
public sealed partial class RideDetailViewModel : ObservableObject
{
    private readonly RideEntry _entry;
    private double _viewStart, _viewEnd;
    private bool _notifying;

    public RideDetailViewModel(RideEntry entry)
    {
        _entry = entry;
        Tiles = BuildTiles(entry.Summary);
    }

    public string Title => _entry.Title;
    public string DateText => _entry.DateText;
    public string WindowTitle => $"{_entry.Title} · {_entry.DateText} · BingeRide";
    public IReadOnlyList<StatTile> Tiles { get; }

    /// <summary>The FTP the ride was recorded with (colours the power zones).</summary>
    public double Ftp => _entry.Summary.Ftp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(Seconds))]
    private RideTrack? _track;

    [ObservableProperty] private string? _status = "Loading the ride…";

    public bool HasTrack => Track is not null;
    public int Seconds => Track?.Count ?? 0;

    /// <summary>First riding second shown in the chart (bound two-way to the chart, which zooms).</summary>
    public double ViewStart
    {
        get => _viewStart;
        set
        {
            if (SetProperty(ref _viewStart, value)) ViewChanged();
        }
    }

    public double ViewEnd
    {
        get => _viewEnd;
        set
        {
            if (SetProperty(ref _viewEnd, value)) ViewChanged();
        }
    }

    public double ViewSpan => Math.Max(0, ViewEnd - ViewStart);
    public bool IsZoomed => HasTrack && ViewSpan >= 1 && ViewSpan < Seconds - 0.5;
    public double ScrollMax => Math.Max(0, Seconds - ViewSpan);

    /// <summary>The scroll bar under a zoomed chart: moves the view without changing its length.</summary>
    public double ScrollValue
    {
        get => ViewStart;
        set
        {
            // While the view changes, the scroll bar clamps its old value to the new maximum and writes it back: ignore that.
            if (_notifying) return;
            var span = ViewSpan;
            var from = Math.Clamp(value, 0, Math.Max(0, Seconds - span));
            if (Math.Abs(from - _viewStart) < 1e-6) return;
            _viewStart = from;
            _viewEnd = from + span;
            OnPropertyChanged(nameof(ViewStart));
            OnPropertyChanged(nameof(ViewEnd));
            ViewChanged();
        }
    }

    /// <summary>"Whole ride · 1:02:15 · 24.3 km · 680 kJ", or the zoomed stretch.</summary>
    public string RangeText
    {
        get
        {
            if (Track is not { } track) return "";
            var (from, to) = IsZoomed ? (ViewStart, ViewEnd) : (0, Seconds);
            var s = track.Stats((int)Math.Floor(from), (int)Math.Ceiling(to));
            var (what, seconds) = IsZoomed
                ? ($"{Fmt.Duration(from)} → {Fmt.Duration(to)}", to - from)
                : ("Whole ride", _entry.Summary.MovingSec); // same as the RIDE TIME tile
            return $"{what} · {Fmt.Duration(seconds)} · {Fmt.Distance(s.DistanceM)} · {s.WorkKj:0} kJ";
        }
    }

    [RelayCommand]
    private void ResetZoom() => SetView(0, Seconds);

    /// <summary>Reads the .fit file on a worker thread.</summary>
    public async Task LoadAsync()
    {
        try
        {
            Show(await Task.Run(() => FitActivityReader.Read(_entry.FitPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Couldn't read {Path.GetFileName(_entry.FitPath)}: {ex.Message}";
        }
    }

    /// <summary>Same as <see cref="LoadAsync"/>, on the calling thread (tests).</summary>
    public void Load()
    {
        try
        {
            Show(FitActivityReader.Read(_entry.FitPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Couldn't read {Path.GetFileName(_entry.FitPath)}: {ex.Message}";
        }
    }

    private void Show(RideRecording ride)
    {
        if (ride.Samples.Count < 2)
        {
            Status = "This ride has no recorded data to chart.";
            return;
        }
        Track = new RideTrack(ride.Samples);
        Status = null;
        SetView(0, Track.Count);
    }

    private void SetView(double from, double to)
    {
        _viewStart = from;
        _viewEnd = to;
        OnPropertyChanged(nameof(ViewStart));
        OnPropertyChanged(nameof(ViewEnd));
        ViewChanged();
    }

    private void ViewChanged()
    {
        _notifying = true;
        try
        {
            OnPropertyChanged(nameof(ViewSpan));
            OnPropertyChanged(nameof(ScrollMax));
            OnPropertyChanged(nameof(ScrollValue)); // after the maximum, so the scroll bar ends on the right value
            OnPropertyChanged(nameof(IsZoomed));
            OnPropertyChanged(nameof(RangeText));
        }
        finally
        {
            _notifying = false;
        }
    }

    private static List<StatTile> BuildTiles(RideSummary s)
    {
        var tiles = new List<StatTile>
        {
            new("RIDE TIME", Fmt.Duration(s.MovingSec)),
            new("DISTANCE", Fmt.Distance(s.DistanceM)),
            new("AVG POWER", $"{s.AvgPowerW:0} W"),
        };
        if (s.NormalizedPowerW > 0) tiles.Add(new("NORMALIZED", $"{s.NormalizedPowerW:0} W"));
        tiles.Add(new("MAX POWER", $"{s.MaxPowerW} W"));
        if (s.Tss > 0) tiles.Add(new("TSS", $"{s.Tss:0}"));
        if (s.IntensityFactor > 0) tiles.Add(new("IF", $"{s.IntensityFactor:0.00}"));
        if (s.AvgHeartRate is { } avgHr) tiles.Add(new("AVG HR", $"{avgHr} bpm"));
        if (s.MaxHeartRate is { } maxHr) tiles.Add(new("MAX HR", $"{maxHr} bpm"));
        if (s.AvgCadence > 0) tiles.Add(new("CADENCE", $"{s.AvgCadence:0} rpm"));
        tiles.Add(new("WORK", $"{s.WorkKj:0} kJ"));
        return tiles;
    }
}
