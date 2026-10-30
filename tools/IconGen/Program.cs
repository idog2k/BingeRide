using SkiaSharp;

// Draws the MyWahoo icon (a cog for the Zwift Cog + a lightning bolt for watts) and writes
// app.ico (16–256 px) plus PNGs. Small sizes use a simplified bolt-only design that stays legible.
var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var entries = icoSizes.Select(size => (size, png: Png(Render(size, detailed: size > 32)))).ToList();
WriteIco(Path.Combine(outDir, "app.ico"), entries);
File.WriteAllBytes(Path.Combine(outDir, "logo-64.png"), Png(Render(64, true)));
File.WriteAllBytes(Path.Combine(outDir, "logo-256.png"), Png(Render(256, true)));
File.WriteAllBytes(Path.Combine(outDir, "logo-32.png"), Png(Render(32, false)));
Console.WriteLine($"Wrote app.ico ({string.Join(", ", icoSizes)}) and logo PNGs to {Path.GetFullPath(outDir)}");

static SKBitmap Render(int size, bool detailed)
{
    // Draw large, then downscale with mipmaps for clean anti-aliasing at every size.
    const int master = 1024;
    using var big = new SKBitmap(master, master, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(big))
    {
        canvas.Clear(SKColors.Transparent);
        DrawIcon(canvas, detailed);
    }
    using var image = SKImage.FromBitmap(big);
    var result = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(result))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(image, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }
    return result;
}

static void DrawIcon(SKCanvas c, bool detailed)
{
    // Rounded tile with a deep blue-to-black gradient.
    using var tile = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(new SKPoint(512, 24), new SKPoint(512, 1000),
            [SKColor.Parse("#26364F"), SKColor.Parse("#0B0D12")], SKShaderTileMode.Clamp),
    };
    c.DrawRoundRect(new SKRoundRect(new SKRect(24, 24, 1000, 1000), detailed ? 220 : 200), tile);

    if (detailed)
    {
        // The cog: 12 trapezoid teeth around a ring.
        using var cog = new SKPath { FillType = SKPathFillType.EvenOdd };
        const int teeth = 12;
        const float rTip = 400, rRoot = 338, rInner = 272;
        var step = Math.PI * 2 / teeth;
        var first = true;
        for (var i = 0; i < teeth; i++)
        {
            var a = i * step - Math.PI / 2;
            foreach (var (r, da) in new[] { (rRoot, -0.5), (rRoot, -0.27), (rTip, -0.16), (rTip, 0.16), (rRoot, 0.27) })
            {
                var p = new SKPoint(512 + (float)(r * Math.Cos(a + da * step)), 512 + (float)(r * Math.Sin(a + da * step)));
                if (first) { cog.MoveTo(p); first = false; }
                else cog.LineTo(p);
            }
        }
        cog.Close();
        cog.AddCircle(512, 512, rInner);
        using var cogPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(200, 150), new SKPoint(820, 880),
                [SKColor.Parse("#5CC0FF"), SKColor.Parse("#2D6FE8")], SKShaderTileMode.Clamp),
        };
        c.DrawPath(cog, cogPaint);
    }

    // The bolt, outlined in the tile colour so it reads where it crosses the cog.
    using var bolt = new SKPath();
    SKPoint[] pts = [new(536, 136), new(700, 136), new(600, 420), new(742, 420), new(424, 888), new(500, 566), new(338, 566)];
    bolt.AddPoly(pts, close: true);
    if (!detailed)
    {
        // Bigger bolt on small icons.
        bolt.Transform(SKMatrix.CreateScale(1.12f, 1.12f, 540, 512));
    }
    using var outline = new SKPaint
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = detailed ? 44 : 0,
        StrokeJoin = SKStrokeJoin.Round,
        Color = SKColor.Parse("#0E131C"),
    };
    if (detailed) c.DrawPath(bolt, outline);
    using var boltPaint = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(new SKPoint(540, 136), new SKPoint(540, 888),
            [SKColor.Parse("#FFD15C"), SKColor.Parse("#FF6A2B")], SKShaderTileMode.Clamp),
    };
    c.DrawPath(bolt, boltPaint);
}

static byte[] Png(SKBitmap bitmap)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ICO container with PNG-compressed entries (supported by Windows Vista and later).
static void WriteIco(string path, List<(int Size, byte[] Png)> entries)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)entries.Count);
    var offset = 6 + 16 * entries.Count;
    foreach (var (size, png) in entries)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in entries) w.Write(png);
}
