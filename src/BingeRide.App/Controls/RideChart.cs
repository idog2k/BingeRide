using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using BingeRide.App.ViewModels;
using BingeRide.Core.Session;

namespace BingeRide.App.Controls;

/// <summary>
/// A recorded ride as stacked timelines on one time axis: power (coloured by zone), heart rate and cadence, each
/// with its own scale. Hover shows every value at that moment; drag across a stretch to zoom into it; the wheel
/// zooms around the pointer (Shift+wheel or a sideways swipe scrolls); double-click shows the whole ride again.
/// </summary>
public sealed class RideChart : Control
{
    public static readonly StyledProperty<RideTrack?> TrackProperty = AvaloniaProperty.Register<RideChart, RideTrack?>(nameof(Track));
    public static readonly StyledProperty<double> FtpProperty = AvaloniaProperty.Register<RideChart, double>(nameof(Ftp));

    /// <summary>First riding second shown (two-way: the chart changes it when zooming).</summary>
    public static readonly StyledProperty<double> ViewStartProperty =
        AvaloniaProperty.Register<RideChart, double>(nameof(ViewStart), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Riding second where the view ends; at or before <see cref="ViewStart"/> means the whole ride.</summary>
    public static readonly StyledProperty<double> ViewEndProperty =
        AvaloniaProperty.Register<RideChart, double>(nameof(ViewEnd), defaultBindingMode: BindingMode.TwoWay);

    static RideChart() => AffectsRender<RideChart>(TrackProperty, FtpProperty, ViewStartProperty, ViewEndProperty);

    public RideTrack? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Ftp { get => GetValue(FtpProperty); set => SetValue(FtpProperty, value); }
    public double ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }

    /// <summary>The shortest stretch the chart zooms into, in seconds.</summary>
    public const double MinSpan = 20;

    public static readonly Color HeartColor = Color.Parse("#F0445E");
    public static readonly Color CadenceColor = Color.Parse("#9F7AEA");

    private const double Left = 44, Right = 14, HeaderHeight = 22, LaneGap = 10, AxisHeight = 22, ColumnWidth = 2;

    private static readonly Typeface Regular = new(FontFamily.Default);
    private static readonly Typeface SemiBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E8ECF4"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#141821"));
    private static readonly IBrush TooltipBrush = new SolidColorBrush(Color.Parse("#1B2030"));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush NoFtpBrush = new SolidColorBrush(Color.Parse("#3BA7FF"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#262C3B")), 1);
    private static readonly IPen TimeGridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x26, 0x2C, 0x3B)), 1);
    private static readonly IPen TooltipBorder = new Pen(new SolidColorBrush(Color.Parse("#262C3B")), 1);
    private static readonly IPen CrosshairPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen SelectionEdge = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen FtpPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1, new DashStyle([4, 4], 0));
    private static readonly IPen DotRing = new Pen(new SolidColorBrush(Color.Parse("#141821")), 2);

    private enum Metric { Power, HeartRate, Cadence }

    private sealed record Series(Metric Metric, string Title, string Name, string Unit, IBrush Stroke, IPen? Line, IBrush? Area);

    private static readonly Series PowerSeries = new(Metric.Power, "POWER", "Power", "W", NoFtpBrush, null, null);
    private static readonly Series HeartSeries = new(Metric.HeartRate, "HEART RATE", "Heart rate", "bpm", new SolidColorBrush(HeartColor),
        new Pen(new SolidColorBrush(HeartColor), 2, lineJoin: PenLineJoin.Round), new SolidColorBrush(HeartColor, 0.16));
    private static readonly Series CadenceSeries = new(Metric.Cadence, "CADENCE", "Cadence", "rpm", new SolidColorBrush(CadenceColor),
        new Pen(new SolidColorBrush(CadenceColor), 2, lineJoin: PenLineJoin.Round), new SolidColorBrush(CadenceColor, 0.16));

    /// <summary>A lane's plot area (below its header) and value scale.</summary>
    private readonly record struct Lane(Series Series, double Top, double Bottom, double Min, double Max)
    {
        public double Y(double value) => Bottom - Math.Clamp((value - Min) / (Max - Min), 0, 1) * (Bottom - Top);
    }

    /// <summary>One chart column: samples [From, To) drawn between X0 and X1.</summary>
    private readonly record struct Segment(double X0, double X1, int From, int To)
    {
        public double Mid => (X0 + X1) / 2;
    }

    private double? _hoverX;
    private double? _dragStart;
    private double _dragX;

    public RideChart()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
    }

    /// <summary>The visible stretch in riding seconds (the whole ride unless zoomed).</summary>
    public (double From, double To) View
    {
        get
        {
            var count = Track?.Count ?? 0;
            var from = Math.Clamp(ViewStart, 0, count);
            var to = Math.Min(ViewEnd, count);
            return to - from >= 1 ? (from, to) : (0, count);
        }
    }

    public void ZoomTo(double from, double to)
    {
        var count = Track?.Count ?? 0;
        if (count == 0) return;
        var span = Math.Clamp(to - from, Math.Min(MinSpan, count), count);
        var mid = (from + to) / 2;
        from = Math.Clamp(mid - span / 2, 0, count - span);
        to = from + span;
        // Keep start < end at every step: a two-way binding sees each property change on its own.
        if (from >= ViewEnd)
        {
            SetCurrentValue(ViewEndProperty, to);
            SetCurrentValue(ViewStartProperty, from);
        }
        else
        {
            SetCurrentValue(ViewStartProperty, from);
            SetCurrentValue(ViewEndProperty, to);
        }
    }

    public void ResetZoom() => ZoomTo(0, Track?.Count ?? 0);

    private double PlotWidth => Math.Max(1, Bounds.Width - Left - Right);
    private double X(double t, (double From, double To) view) => Left + (t - view.From) / (view.To - view.From) * PlotWidth;
    private double T(double x, (double From, double To) view) => view.From + (x - Left) / PlotWidth * (view.To - view.From);

    private List<Lane> Lanes(RideTrack track)
    {
        var series = new List<(Series S, double Weight)> { (PowerSeries, 1.6) };
        if (track.HasHeartRate) series.Add((HeartSeries, 1));
        series.Add((CadenceSeries, 1));

        var plotHeight = Bounds.Height - AxisHeight - series.Count * HeaderHeight - (series.Count - 1) * LaneGap;
        var lanes = new List<Lane>();
        if (plotHeight < series.Count * 20) return lanes;
        var weights = series.Sum(s => s.Weight);
        var top = 0.0;
        foreach (var (s, weight) in series)
        {
            var height = plotHeight * weight / weights;
            var (min, max) = s.Metric switch
            {
                Metric.Power => (0.0, NiceCeiling(Math.Max(track.MaxPowerW, Ftp > 0 ? Ftp * 1.2 : 100))),
                Metric.HeartRate => (Math.Floor(((track.MinHeartRate ?? 60) - 5) / 10.0) * 10,
                                     Math.Ceiling(((track.MaxHeartRate ?? 180) + 5) / 10.0) * 10),
                _ => (0.0, NiceCeiling(Math.Max(track.MaxCadence, 110))),
            };
            lanes.Add(new Lane(s, top + HeaderHeight, top + HeaderHeight + height, min, max));
            top += HeaderHeight + height + LaneGap;
        }
        return lanes;
    }

    /// <summary>Two-pixel columns averaging the samples under them, or one segment per sample when zoomed in further.</summary>
    private List<Segment> Segments(RideTrack track, (double From, double To) view)
    {
        var segments = new List<Segment>();
        var span = view.To - view.From;
        var columns = (int)Math.Ceiling(PlotWidth / ColumnWidth);
        if (span >= columns)
        {
            var perColumn = span / columns;
            for (var c = 0; c < columns; c++)
            {
                var from = (int)Math.Floor(view.From + c * perColumn);
                var to = Math.Min(track.Count, Math.Max(from + 1, (int)Math.Floor(view.From + (c + 1) * perColumn)));
                if (from >= track.Count) break;
                segments.Add(new Segment(Left + c * ColumnWidth, Math.Min(Left + (c + 1) * ColumnWidth, Left + PlotWidth), from, to));
            }
        }
        else
        {
            for (var i = (int)Math.Floor(view.From); i < Math.Min(track.Count, Math.Ceiling(view.To)); i++)
                segments.Add(new Segment(X(i, view), X(i + 1, view), i, i + 1));
        }
        return segments;
    }

    private double? Value(RideTrack track, Metric metric, Segment s) => metric switch
    {
        Metric.Power => track.MeanPower(s.From, s.To),
        Metric.HeartRate => track.MeanHeartRate(s.From, s.To),
        _ => track.MeanCadence(s.From, s.To),
    };

    /// <summary>Seconds on each side of a second that vote on its zone colour.</summary>
    private const int ZoneVoteHalfWindow = 15;

    /// <summary>Zone stretches shorter than this (seconds) take the colour of the stretch before them.</summary>
    private const int MinZoneRun = 10;

    private RideTrack? _zonesTrack;
    private double _zonesFtp;
    private int[] _zones = []; // each riding second's smoothed zone

    /// <summary>
    /// A column's zone colour. Colouring every second by its own zone stripes the chart wherever power wobbles
    /// across a zone boundary, so each second takes the zone most of the 30 s around it sits in (it still changes
    /// right at an interval's edge). A column two or more zones away from that (a sprint, a stop) keeps its own colour.
    /// </summary>
    private IBrush ColumnBrush(RideTrack track, Segment s, double watts)
    {
        if (Ftp <= 0) return NoFtpBrush;
        if (!ReferenceEquals(_zonesTrack, track) || _zonesFtp != Ftp) SmoothZones(track);
        var smoothed = _zones[Math.Clamp((s.From + s.To) / 2, 0, _zones.Length - 1)];
        var own = WorkoutChart.Zone(watts / Ftp * 100);
        return WorkoutChart.ZoneBrushOf(Math.Abs(own - smoothed) <= 1 ? smoothed : own);
    }

    private void SmoothZones(RideTrack track)
    {
        var n = track.Count;
        var zoneCount = WorkoutChart.ZoneCount;
        var counts = new int[zoneCount, n + 1]; // [zone, i]: seconds before i that were in that zone
        for (var i = 0; i < n; i++)
        {
            var zone = WorkoutChart.Zone(track.Samples[i].PowerW / Ftp * 100);
            for (var z = 0; z < zoneCount; z++) counts[z, i + 1] = counts[z, i] + (z == zone ? 1 : 0);
        }

        var zones = new int[n];
        for (var i = 0; i < n; i++)
        {
            int from = Math.Max(0, i - ZoneVoteHalfWindow), to = Math.Min(n, i + ZoneVoteHalfWindow + 1), most = -1;
            for (var z = 0; z < zoneCount; z++)
            {
                var c = counts[z, to] - counts[z, from];
                if (c > most) (zones[i], most) = (z, c);
            }
        }

        // Where power sits right on a boundary the vote is close to a tie and flips back and forth: fold short runs.
        for (int start = 0, i = 1; i <= n; i++)
        {
            if (i < n && zones[i] == zones[start]) continue;
            if (start > 0 && i - start < MinZoneRun) Array.Fill(zones, zones[start - 1], start, i - start);
            start = i;
        }
        (_zones, _zonesTrack, _zonesFtp) = (zones, track, Ftp);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brushes.Transparent, new Rect(size)); // the whole area takes the mouse
        if (Track is not { Count: > 1 } track || size.Width < Left + Right + 20) return;
        var lanes = Lanes(track);
        if (lanes.Count == 0) return;

        var view = View;
        var segments = Segments(track, view);
        var stats = track.Stats((int)Math.Floor(view.From), (int)Math.Ceiling(view.To));
        var plot = new Rect(Left, 0, PlotWidth, size.Height);
        var axisTop = lanes[^1].Bottom;

        var ticks = TimeTicks(view);
        foreach (var lane in lanes)
        {
            DrawHeader(context, lane, stats);
            DrawValueGrid(context, lane);
            foreach (var t in ticks)
            {
                var x = Math.Round(X(t, view)) + 0.5;
                context.DrawLine(TimeGridPen, new Point(x, lane.Top), new Point(x, lane.Bottom));
            }
            using (context.PushClip(new Rect(Left, lane.Top, PlotWidth, lane.Bottom - lane.Top)))
            {
                if (lane.Series.Metric == Metric.Power) DrawPower(context, track, lane, segments);
                else DrawLine(context, track, lane, segments);
            }
        }
        DrawTimeAxis(context, ticks, view, axisTop);

        if (_dragStart is { } start)
        {
            var x0 = Math.Min(start, _dragX);
            var x1 = Math.Max(start, _dragX);
            var area = new Rect(x0, lanes[0].Top, x1 - x0, axisTop - lanes[0].Top);
            context.FillRectangle(SelectionBrush, area);
            context.DrawLine(SelectionEdge, area.TopLeft, area.BottomLeft);
            context.DrawLine(SelectionEdge, area.TopRight, area.BottomRight);
        }
        else if (_hoverX is { } hx && plot.Contains(new Point(hx, 1)) && segments.Count > 0)
        {
            DrawHover(context, track, lanes, segments, hx);
        }
    }

    private void DrawHeader(DrawingContext context, Lane lane, RangeStats stats)
    {
        var y = lane.Top - HeaderHeight + 2;
        var title = Text(lane.Series.Title, 11, MutedBrush, SemiBold);
        context.DrawText(title, new Point(Left, y + 2));
        var line = lane.Series.Metric switch
        {
            Metric.Power => $"avg {stats.AvgPowerW:0} W · NP {stats.NormalizedPowerW:0} W · max {stats.MaxPowerW} W",
            Metric.HeartRate => stats.AvgHeartRate is { } hr ? $"avg {hr:0} · max {stats.MaxHeartRate} bpm" : "no heart rate here",
            _ => $"avg {stats.AvgCadence:0} · max {stats.MaxCadence} rpm",
        };
        context.DrawText(Text(line, 12, TextBrush, Regular), new Point(Left + title.Width + 10, y));
    }

    private void DrawValueGrid(DrawingContext context, Lane lane)
    {
        var step = NiceStep(lane.Max - lane.Min, Math.Max(2, (lane.Bottom - lane.Top) / 30));
        for (var v = Math.Ceiling(lane.Min / step) * step; v <= lane.Max + 1e-9; v += step)
        {
            var y = Math.Round(lane.Y(v)) + 0.5;
            context.DrawLine(GridPen, new Point(Left, y), new Point(Left + PlotWidth, y));
            var label = Text($"{v:0}", 10.5, MutedBrush, Regular);
            var ly = Math.Clamp(y - label.Height / 2, lane.Top - 2, lane.Bottom - label.Height + 2);
            context.DrawText(label, new Point(Left - 6 - label.Width, ly));
        }
        if (lane.Series.Metric == Metric.Power && Ftp > 0 && Ftp < lane.Max)
        {
            var y = Math.Round(lane.Y(Ftp)) + 0.5;
            context.DrawLine(FtpPen, new Point(Left, y), new Point(Left + PlotWidth, y));
            var label = Text("FTP", 10, MutedBrush, SemiBold);
            context.DrawText(label, new Point(Left + PlotWidth - label.Width - 2, y - label.Height - 1));
        }
    }

    /// <summary>Filled columns, one geometry per run of the same zone colour (no seams between columns).</summary>
    private void DrawPower(DrawingContext context, RideTrack track, Lane lane, List<Segment> segments)
    {
        IBrush? brush = null;
        StreamGeometry? geometry = null;
        StreamGeometryContext? g = null;
        var lastX = 0.0;

        void Flush()
        {
            if (g is null) return;
            g.LineTo(new Point(lastX, lane.Bottom));
            g.EndFigure(true);
            g.Dispose();
            context.DrawGeometry(brush, null, geometry!);
            g = null;
        }

        foreach (var s in segments)
        {
            var watts = track.MeanPower(s.From, s.To);
            var b = ColumnBrush(track, s, watts);
            if (!ReferenceEquals(b, brush))
            {
                Flush();
                brush = b;
                geometry = new StreamGeometry();
                g = geometry.Open();
                g.BeginFigure(new Point(s.X0, lane.Bottom), true);
            }
            var y = lane.Y(watts);
            g!.LineTo(new Point(s.X0, y));
            g.LineTo(new Point(s.X1, y));
            lastX = s.X1;
        }
        Flush();
    }

    /// <summary>A line with a soft fill under it; gaps (no heart rate) break both.</summary>
    private void DrawLine(DrawingContext context, RideTrack track, Lane lane, List<Segment> segments)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            var open = false;
            var lastX = 0.0;
            foreach (var s in segments)
            {
                if (Value(track, lane.Series.Metric, s) is not { } v)
                {
                    if (open) Close();
                    continue;
                }
                var p = new Point(s.Mid, lane.Y(v));
                if (!open)
                {
                    l.BeginFigure(p, false);
                    a.BeginFigure(new Point(p.X, lane.Bottom), true);
                    open = true;
                }
                else l.LineTo(p);
                a.LineTo(p);
                lastX = p.X;
            }
            if (open) Close();

            void Close()
            {
                l.EndFigure(false);
                a.LineTo(new Point(lastX, lane.Bottom));
                a.EndFigure(true);
                open = false;
            }
        }
        context.DrawGeometry(lane.Series.Area, null, area);
        context.DrawGeometry(null, lane.Series.Line, line);
    }

    private void DrawTimeAxis(DrawingContext context, List<double> ticks, (double From, double To) view, double top)
    {
        foreach (var t in ticks)
        {
            var label = Text(Fmt.Duration(t), 10.5, MutedBrush, Regular);
            var x = Math.Clamp(X(t, view) - label.Width / 2, Left - 4, Left + PlotWidth - label.Width + Right);
            context.DrawText(label, new Point(x, top + 5));
        }
    }

    private void DrawHover(DrawingContext context, RideTrack track, List<Lane> lanes, List<Segment> segments, double hx)
    {
        var index = segments.FindIndex(s => hx < s.X1);
        var seg = segments[index < 0 ? segments.Count - 1 : index];
        var x = Math.Round(seg.Mid) + 0.5;
        context.DrawLine(CrosshairPen, new Point(x, lanes[0].Top), new Point(x, lanes[^1].Bottom));

        // One readout listing every series at this moment: values lead, names follow.
        var rows = new List<(string Value, string Name, IBrush Key)>();
        foreach (var lane in lanes)
        {
            var v = Value(track, lane.Series.Metric, seg);
            var key = lane.Series.Metric == Metric.Power ? ColumnBrush(track, seg, v ?? 0) : lane.Series.Stroke;
            if (v is { } value)
            {
                context.DrawEllipse(key, DotRing, new Point(x, lane.Y(value)), 4.5, 4.5);
                rows.Add(($"{value:0} {lane.Series.Unit}", lane.Series.Name, key));
            }
            else rows.Add(("—", lane.Series.Name, key));
        }

        var time = seg.To - seg.From > 1 ? $"{Fmt.Duration(seg.From)}–{Fmt.Duration(seg.To)}" : Fmt.Duration(seg.From);
        var header = Text(time, 11, MutedBrush, Regular);
        var values = rows.Select(r => Text(r.Value, 13, TextBrush, SemiBold)).ToList();
        var names = rows.Select(r => Text(r.Name, 11, MutedBrush, Regular)).ToList();
        const double pad = 8, rowHeight = 19, keyWidth = 12;
        var valueWidth = values.Max(v => v.Width);
        var width = pad * 2 + Math.Max(header.Width, keyWidth + 6 + valueWidth + 8 + names.Max(n => n.Width));
        var height = pad * 2 + header.Height + 4 + rows.Count * rowHeight;
        var bx = x + 12 + width <= Left + PlotWidth ? x + 12 : x - 12 - width;
        var box = new Rect(Math.Max(0, bx), lanes[0].Top + 4, width, height);
        context.DrawRectangle(TooltipBrush, TooltipBorder, box, 6, 6);
        context.DrawText(header, new Point(box.X + pad, box.Y + pad));
        var y = box.Y + pad + header.Height + 4;
        for (var i = 0; i < rows.Count; i++)
        {
            var mid = y + rowHeight / 2;
            context.DrawLine(new Pen(rows[i].Key, 2.5), new Point(box.X + pad, mid), new Point(box.X + pad + keyWidth, mid));
            var vx = box.X + pad + keyWidth + 6;
            context.DrawText(values[i], new Point(vx + valueWidth - values[i].Width, mid - values[i].Height / 2));
            context.DrawText(names[i], new Point(vx + valueWidth + 8, mid - names[i].Height / 2));
            y += rowHeight;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Track is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            _dragStart = null;
            ResetZoom();
            e.Handled = true;
            return;
        }
        _dragStart = _dragX = Math.Clamp(e.GetPosition(this).X, Left, Left + PlotWidth);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _hoverX = p.X;
        if (_dragStart is not null) _dragX = Math.Clamp(p.X, Left, Left + PlotWidth);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not { } start) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        if (Math.Abs(_dragX - start) >= 6)
        {
            var view = View;
            ZoomTo(T(Math.Min(start, _dragX), view), T(Math.Max(start, _dragX), view));
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Track is null) return;
        var view = View;
        var span = view.To - view.From;
        var (dx, dy) = (e.Delta.X, e.Delta.Y);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(dx) > Math.Abs(dy))
        {
            var d = Math.Abs(dx) > Math.Abs(dy) ? dx : dy;
            var shift = -d * span * 0.15;
            ZoomTo(view.From + shift, view.To + shift);
        }
        else
        {
            var anchor = Math.Clamp(T(e.GetPosition(this).X, view), view.From, view.To);
            var factor = Math.Pow(1.25, -dy);
            ZoomTo(anchor - (anchor - view.From) * factor, anchor + (view.To - anchor) * factor);
        }
        e.Handled = true;
    }

    private List<double> TimeTicks((double From, double To) view)
    {
        double[] steps = [10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400];
        var span = view.To - view.From;
        var maxLabels = Math.Max(2, PlotWidth / 80);
        var step = steps.FirstOrDefault(s => span / s <= maxLabels, steps[^1]);
        var ticks = new List<double>();
        for (var t = Math.Ceiling(view.From / step) * step; t <= view.To + 1e-9; t += step) ticks.Add(t);
        return ticks;
    }

    private static double NiceStep(double range, double maxTicks)
    {
        var rough = range / Math.Max(1, maxTicks);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rough, 1e-9))));
        var norm = rough / magnitude;
        return (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10) * magnitude;
    }

    private static double NiceCeiling(double value)
    {
        var step = NiceStep(value, 5);
        return Math.Ceiling(value * 1.04 / step) * step;
    }

    private static FormattedText Text(string text, double size, IBrush brush, Typeface face) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
}
