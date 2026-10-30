using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MyWahoo.Core.Session;
using MyWahoo.Core.Workouts;

namespace MyWahoo.App.Controls;

/// <summary>Workout profile: ERG steps as zone-coloured bars, slope steps by grade, free steps as low grey bars.</summary>
public sealed class WorkoutChart : Control
{
    public static readonly StyledProperty<Workout?> WorkoutProperty = AvaloniaProperty.Register<WorkoutChart, Workout?>(nameof(Workout));
    public static readonly StyledProperty<double> FtpProperty = AvaloniaProperty.Register<WorkoutChart, double>(nameof(Ftp), 200);
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<WorkoutChart, double>(nameof(Progress), -1);
    public static readonly StyledProperty<IReadOnlyList<FoodMarker>?> FoodMarkersProperty =
        AvaloniaProperty.Register<WorkoutChart, IReadOnlyList<FoodMarker>?>(nameof(FoodMarkers));

    static WorkoutChart() => AffectsRender<WorkoutChart>(WorkoutProperty, FtpProperty, ProgressProperty, FoodMarkersProperty);

    /// <summary>Foods drawn above the profile where they'll be burned (time-based workouts only).</summary>
    public IReadOnlyList<FoodMarker>? FoodMarkers { get => GetValue(FoodMarkersProperty); set => SetValue(FoodMarkersProperty, value); }

    private static readonly Typeface EmojiTypeface = new(new FontFamily("Segoe UI Emoji, Noto Color Emoji, Apple Color Emoji"));
    private static readonly IBrush Tick = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    public Workout? Workout { get => GetValue(WorkoutProperty); set => SetValue(WorkoutProperty, value); }
    public double Ftp { get => GetValue(FtpProperty); set => SetValue(FtpProperty, value); }

    /// <summary>0..1 to draw the cursor and dim completed steps; negative hides it.</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    private static readonly IBrush Slope = new SolidColorBrush(Color.Parse("#2FBF9B"));
    private static readonly IBrush Free = new SolidColorBrush(Color.Parse("#4B5363"));
    private static readonly IBrush Done = new SolidColorBrush(Color.FromArgb(0x88, 0x0B, 0x0D, 0x12));
    private static readonly IPen CursorPen = new Pen(Brushes.White, 2);
    private static readonly IPen FtpLine = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1, new DashStyle([4, 4], 0));

    public static IBrush ZoneBrush(double pctFtp) => pctFtp switch
    {
        < 55 => Zone("#7D8597"),
        < 76 => Zone("#3BA7FF"),
        < 91 => Zone("#39D98A"),
        < 106 => Zone("#FFD43B"),
        < 121 => Zone("#FF8A3D"),
        _ => Zone("#FF4D6A"),
    };

    private static readonly Dictionary<string, IBrush> ZoneCache = [];
    private static IBrush Zone(string hex)
    {
        lock (ZoneCache)
        {
            if (!ZoneCache.TryGetValue(hex, out var b)) ZoneCache[hex] = b = new SolidColorBrush(Color.Parse(hex));
            return b;
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = Workout;
        var size = Bounds.Size;
        if (w is null || w.Steps.Count == 0 || size.Width < 2 || size.Height < 2) return;

        var ftp = Ftp > 0 ? Ftp : 200;

        // Reserve a band at the top for food when there are markers to show.
        var markers = w.HasDistanceSteps ? null : FoodMarkers;
        var band = markers is { Count: > 0 } ? Math.Clamp(size.Height * 0.3, 12, 24) : 0;
        var chartHeight = size.Height - band;
        // Routes mix distance and time steps: put both on one axis, ~8 m/s standing in for time steps.
        var byDistance = w.HasDistanceSteps;
        double Length(WorkoutStep s) => byDistance ? s.DistanceM + s.DurationSec * 8 : s.DurationSec;
        var total = w.Steps.Sum(Length);
        if (total <= 0) return;

        // Vertical scale: % FTP for ERG (at least 130 %), grade mapped so 10 % ≈ 100 % FTP.
        double Height(WorkoutStep s, double fraction) => s.Kind switch
        {
            StepKind.Erg => (s.TargetWatts(ftp, fraction) ?? 0) / ftp * 100,
            StepKind.Slope => 35 + Math.Max(0, s.Grade ?? 0) * 7,
            _ => 30,
        };
        var maxPct = Math.Max(130, w.Steps.Max(s => Math.Max(Height(s, 0), Height(s, 1))) * 1.05);
        double Y(double pct) => size.Height - Math.Clamp(pct / maxPct, 0.02, 1) * chartHeight;

        var x = 0.0;
        foreach (var step in w.Steps)
        {
            var width = Length(step) / total * size.Width;
            var brush = step.Kind switch
            {
                StepKind.Erg => ZoneBrush(Math.Max(Height(step, 0), Height(step, 1))),
                StepKind.Slope => Slope,
                _ => Free,
            };
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(x, size.Height), true);
                g.LineTo(new Point(x, Y(Height(step, 0))));
                g.LineTo(new Point(x + width, Y(Height(step, 1))));
                g.LineTo(new Point(x + width, size.Height));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geometry);
            x += width;
        }

        var ftpY = Y(100);
        context.DrawLine(FtpLine, new Point(0, ftpY), new Point(size.Width, ftpY));

        if (Progress >= 0)
        {
            var cx = Math.Clamp(Progress, 0, 1) * size.Width;
            context.FillRectangle(Done, new Rect(0, band, cx, chartHeight));
            context.DrawLine(CursorPen, new Point(cx, band), new Point(cx, size.Height));
        }

        if (markers is { Count: > 0 }) DrawFood(context, markers, total, size.Width, band);
    }

    private void DrawFood(DrawingContext context, IReadOnlyList<FoodMarker> markers, double totalSec, double width, double band)
    {
        var fontSize = band * 0.78;
        var lastX = double.NegativeInfinity;
        foreach (var marker in markers)
        {
            var x = marker.TimeSec / totalSec * width;
            var reached = Progress >= 0 && marker.TimeSec / totalSec <= Progress;
            context.FillRectangle(Tick, new Rect(x - 0.5, band - 3, 1, 3));
            if (x - lastX < fontSize * 1.05) continue; // too crowded: keep the tick, skip the picture
            lastX = x;
            var text = new FormattedText(marker.Food.Emoji, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, EmojiTypeface, fontSize, Brushes.White);
            using (context.PushOpacity(reached || Progress < 0 ? 1 : 0.4))
                context.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, width - text.Width), (band - text.Height) / 2 - 1));
        }
    }
}
