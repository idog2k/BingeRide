using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MyWahoo.Core.Protocol;

namespace MyWahoo.App.Controls;

/// <summary>The 24 virtual gears as bars sized by ratio, with the current gear highlighted.</summary>
public sealed class GearIndicator : Control
{
    public static readonly StyledProperty<int> GearProperty = AvaloniaProperty.Register<GearIndicator, int>(nameof(Gear), GearTable.DefaultGear);

    static GearIndicator() => AffectsRender<GearIndicator>(GearProperty);

    public int Gear { get => GetValue(GearProperty); set => SetValue(GearProperty, value); }

    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#2A3142"));
    private static readonly IBrush Below = new SolidColorBrush(Color.Parse("#4E4373"));
    private static readonly IBrush Current = new SolidColorBrush(Color.Parse("#A78BFA"));

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        const double gap = 2;
        var width = (size.Width - gap * (GearTable.Count - 1)) / GearTable.Count;
        if (width <= 0) return;
        var max = GearTable.Ratios[^1];
        for (var i = 0; i < GearTable.Count; i++)
        {
            var h = Math.Max(3, GearTable.Ratios[i] / max * size.Height);
            var brush = i + 1 == Gear ? Current : i + 1 < Gear ? Below : Idle;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(i * (width + gap), size.Height - h, width, h), 1.5));
        }
    }
}
