using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using SkiaNative.Avalonia.Geometry;
using SkiaNative.Avalonia.Imaging;
using SkiaNative.Avalonia.Text;
using Xunit;

namespace SkiaNative.Avalonia.Tests;

public sealed class RenderTests
{
    [Fact]
    public void RenderTargetBitmap_Readback_RendersSolidRectangle()
    {
        using var bitmap = CreateBitmap(64, 64);
        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawRectangle(Brushes.Red, null, new RoundedRect(new Rect(10, 10, 24, 24)));
        }

        var pixels = bitmap.CopyPixelBytes();
        var inside = GetPixel(bitmap, pixels, 18, 18);
        var outside = GetPixel(bitmap, pixels, 2, 2);

        AssertColor(inside, r: 255, g: 0, b: 0, a: 255, tolerance: 2);
        Assert.True(outside.A <= 2, $"Expected transparent outside pixel, got A={outside.A}.");
    }

    [Fact]
    public void RenderTargetBitmap_Readback_RendersLinearGradient()
    {
        using var bitmap = CreateBitmap(100, 16);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.White, 1)
            }
        };

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawRectangle(gradient, null, new RoundedRect(new Rect(0, 0, 100, 16)));
        }

        var pixels = bitmap.CopyPixelBytes();
        var left = GetPixel(bitmap, pixels, 8, 8);
        var right = GetPixel(bitmap, pixels, 92, 8);

        Assert.True(left.R < right.R, $"Expected gradient red channel to increase left-to-right, got {left.R} >= {right.R}.");
        Assert.True(left.G < right.G, $"Expected gradient green channel to increase left-to-right, got {left.G} >= {right.G}.");
        Assert.True(left.B < right.B, $"Expected gradient blue channel to increase left-to-right, got {left.B} >= {right.B}.");
        Assert.True(left.A > 250 && right.A > 250, $"Expected opaque gradient endpoints, got A={left.A}/{right.A}.");
    }

    [Fact]
    public void RenderTargetBitmap_ImageTolerance_ComparesWholeScene()
    {
        using var expected = CreateBitmap(96, 64);
        using var actual = CreateBitmap(96, 64);

        DrawParityScene(expected);
        DrawParityScene(actual);

        RenderTestImageAssert.Similar(
            expected,
            actual,
            perChannelTolerance: 1,
            maxMismatchRatio: 0,
            nameof(RenderTargetBitmap_ImageTolerance_ComparesWholeScene));
    }

    [Fact]
    public void RenderTargetBitmap_Readback_AppliesOpacityMaskAlpha()
    {
        using var bitmap = CreateBitmap(100, 24);
        var mask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, 0.5),
                new GradientStop(Colors.Transparent, 1)
            }
        };

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.PushOpacityMask(mask, new Rect(0, 0, 100, 24));
            context.DrawRectangle(Brushes.DodgerBlue, null, new RoundedRect(new Rect(0, 0, 100, 24)));
            context.PopOpacityMask();
        }

        var pixels = bitmap.CopyPixelBytes();
        var left = GetPixel(bitmap, pixels, 4, 12);
        var center = GetPixel(bitmap, pixels, 50, 12);
        var right = GetPixel(bitmap, pixels, 96, 12);

        Assert.True(center.A > 200, $"Expected opaque center after mask, got A={center.A}.");
        Assert.True(left.A < center.A / 2, $"Expected left edge to be masked, got left A={left.A}, center A={center.A}.");
        Assert.True(right.A < center.A / 2, $"Expected right edge to be masked, got right A={right.A}, center A={center.A}.");
    }

    [Fact]
    public void RenderTargetBitmap_Readback_DrawsUploadedBitmapWithScaling()
    {
        using var bitmap = CreateBitmap(48, 48);
        using var source = CreateQuadrantBitmap();

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawBitmap(source, 1, new Rect(0, 0, 4, 4), new Rect(8, 8, 32, 32));
        }

        var pixels = bitmap.CopyPixelBytes();

        AssertColor(GetPixel(bitmap, pixels, 15, 15), r: 255, g: 0, b: 0, a: 255, tolerance: 12);
        AssertColor(GetPixel(bitmap, pixels, 33, 15), r: 0, g: 255, b: 0, a: 255, tolerance: 12);
        AssertColor(GetPixel(bitmap, pixels, 15, 33), r: 0, g: 0, b: 255, a: 255, tolerance: 12);
        AssertColor(GetPixel(bitmap, pixels, 33, 33), r: 255, g: 255, b: 0, a: 255, tolerance: 12);
        Assert.True(GetPixel(bitmap, pixels, 2, 2).A <= 2);
    }

    [Fact]
    public void NativeWriteableBitmap_Resize_ScalesNativeContent()
    {
        using var source = CreateQuadrantBitmap();
        using var resized = NativeWriteableBitmap.Resize(source, new PixelSize(20, 20));

        var pixels = resized.CopyPixelBytes();

        Assert.Equal(new PixelSize(20, 20), resized.PixelSize);
        AssertColor(GetPixel(resized, pixels, 5, 5), r: 255, g: 0, b: 0, a: 255, tolerance: 20);
        AssertColor(GetPixel(resized, pixels, 15, 5), r: 0, g: 255, b: 0, a: 255, tolerance: 20);
        AssertColor(GetPixel(resized, pixels, 5, 15), r: 0, g: 0, b: 255, a: 255, tolerance: 20);
        AssertColor(GetPixel(resized, pixels, 15, 15), r: 255, g: 255, b: 0, a: 255, tolerance: 20);
    }

    [Fact]
    public void RenderTargetBitmap_Save_WritesPngAndRoundTripsThroughNativeDecoder()
    {
        using var bitmap = CreateBitmap(32, 32);
        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawRectangle(Brushes.LimeGreen, null, new RoundedRect(new Rect(4, 4, 20, 20)));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        var png = stream.ToArray();

        AssertPngSignature(png);

        using var decodedStream = new MemoryStream(png);
        using var decoded = NativeWriteableBitmap.FromStream(decodedStream);
        var pixels = decoded.CopyPixelBytes();

        Assert.Equal(new PixelSize(32, 32), decoded.PixelSize);
        AssertColor(GetPixel(decoded, pixels, 12, 12), r: 50, g: 205, b: 50, a: 255, tolerance: 4);
        Assert.True(GetPixel(decoded, pixels, 1, 1).A <= 2);
    }

    [Fact]
    public void NativeWriteableBitmap_FromStream_ToWidth_DecodesAndScalesNativeContent()
    {
        using var source = CreateQuadrantBitmap();
        using var stream = new MemoryStream();

        source.Save(stream);
        stream.Position = 0;

        using var decoded = NativeWriteableBitmap.FromStream(stream, width: 8);
        var pixels = decoded.CopyPixelBytes();

        Assert.Equal(new PixelSize(8, 8), decoded.PixelSize);
        AssertColor(GetPixel(decoded, pixels, 2, 2), r: 255, g: 0, b: 0, a: 255, tolerance: 24);
        AssertColor(GetPixel(decoded, pixels, 6, 2), r: 0, g: 255, b: 0, a: 255, tolerance: 24);
        AssertColor(GetPixel(decoded, pixels, 2, 6), r: 0, g: 0, b: 255, a: 255, tolerance: 24);
        AssertColor(GetPixel(decoded, pixels, 6, 6), r: 255, g: 255, b: 0, a: 255, tolerance: 24);
    }

    [Fact]
    public void RenderTargetBitmap_DrawBitmap_UsesAndRestoresRenderOptionsBlendMode()
    {
        using var bitmap = CreateBitmap(24, 12);
        using var source = CreateSolidBitmap(8, 8, Colors.Red);

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Lime);
            context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Multiply });
            try
            {
                context.DrawBitmap(source, 1, new Rect(0, 0, 8, 8), new Rect(2, 2, 8, 8));
            }
            finally
            {
                context.PopRenderOptions();
            }

            context.DrawBitmap(source, 1, new Rect(0, 0, 8, 8), new Rect(14, 2, 8, 8));
        }

        var pixels = bitmap.CopyPixelBytes();

        AssertColor(GetPixel(bitmap, pixels, 5, 5), r: 0, g: 0, b: 0, a: 255, tolerance: 2);
        AssertColor(GetPixel(bitmap, pixels, 17, 5), r: 255, g: 0, b: 0, a: 255, tolerance: 2);
        AssertColor(GetPixel(bitmap, pixels, 1, 1), r: 0, g: 255, b: 0, a: 255, tolerance: 2);
    }

    [Fact]
    public void RenderTargetBitmap_DrawEllipse_UsesAndRestoresRenderOptionsEdgeMode()
    {
        using var bitmap = CreateBitmap(64, 28);

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawEllipse(Brushes.Black, null, new Rect(2.5, 2.5, 21, 21));

            context.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased });
            try
            {
                context.DrawEllipse(Brushes.Black, null, new Rect(34.5, 2.5, 21, 21));
            }
            finally
            {
                context.PopRenderOptions();
            }
        }

        var pixels = bitmap.CopyPixelBytes();
        var antialiasPartial = CountPixels(bitmap, pixels, 0, 0, 30, 28, static pixel => pixel.A is > 0 and < 255);
        var aliasedPartial = CountPixels(bitmap, pixels, 32, 0, 30, 28, static pixel => pixel.A is > 0 and < 255);

        Assert.True(antialiasPartial > 8, $"Expected antialiased ellipse edge pixels, got {antialiasPartial}.");
        Assert.True(aliasedPartial <= 2, $"Expected aliased ellipse edge to avoid partial alpha, got {aliasedPartial}.");
    }

    [Fact]
    public void RenderTargetBitmap_DrawRectangle_RendersBoxShadowWithoutBrushOrPen()
    {
        using var bitmap = CreateBitmap(80, 80);

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawRectangle(
                null,
                null,
                new RoundedRect(new Rect(20, 20, 20, 20)),
                new BoxShadows(new BoxShadow
                {
                    Color = Colors.Blue,
                    OffsetX = 10,
                    OffsetY = 15,
                    Blur = 0,
                    Spread = 0
                }));
        }

        var pixels = bitmap.CopyPixelBytes();

        Assert.True(GetPixel(bitmap, pixels, 25, 25).A <= 2, "Expected source rectangle area to stay transparent when brush and pen are null.");
        AssertColor(GetPixel(bitmap, pixels, 45, 50), r: 0, g: 0, b: 255, a: 255, tolerance: 2);
    }

    [Fact]
    public void RenderTargetBitmap_Readback_AppliesClipAndTransform()
    {
        using var bitmap = CreateBitmap(64, 40);

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);

            context.PushClip(new Rect(8, 8, 20, 20));
            context.DrawRectangle(Brushes.Red, null, new RoundedRect(new Rect(0, 0, 44, 36)));
            context.PopClip();

            context.Transform = Matrix.CreateTranslation(36, 0);
            context.DrawRectangle(Brushes.DodgerBlue, null, new RoundedRect(new Rect(0, 0, 14, 14)));
            context.Transform = Matrix.Identity;
        }

        var pixels = bitmap.CopyPixelBytes();

        AssertColor(GetPixel(bitmap, pixels, 18, 18), r: 255, g: 0, b: 0, a: 255, tolerance: 2);
        Assert.True(GetPixel(bitmap, pixels, 4, 18).A <= 2, "Expected left side outside clip to stay transparent.");
        AssertColor(GetPixel(bitmap, pixels, 42, 7), r: 30, g: 144, b: 255, a: 255, tolerance: 2);
        Assert.True(GetPixel(bitmap, pixels, 6, 6).A <= 2, "Expected untranslated blue rectangle location to stay transparent.");
    }

    [Fact]
    public void RenderTargetBitmap_Readback_AppliesRegionClipToClear()
    {
        using var bitmap = CreateBitmap(64, 40);
        using var region = new NativeRegion();
        region.AddRect(new LtrbPixelRect { Left = 10, Top = 8, Right = 22, Bottom = 20 });

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawRectangle(Brushes.DodgerBlue, null, new RoundedRect(new Rect(0, 0, 64, 40)));

            context.PushClip(region);
            context.Clear(Colors.Transparent);
            context.PopClip();
        }

        var pixels = bitmap.CopyPixelBytes();

        Assert.True(GetPixel(bitmap, pixels, 14, 12).A <= 2, "Expected clipped clear to affect pixels inside the dirty region.");
        AssertColor(GetPixel(bitmap, pixels, 4, 12), r: 30, g: 144, b: 255, a: 255, tolerance: 2);
        AssertColor(GetPixel(bitmap, pixels, 30, 12), r: 30, g: 144, b: 255, a: 255, tolerance: 2);
    }

    [Fact]
    public void RenderTargetBitmap_Readback_RendersStyledDashedStroke()
    {
        using var bitmap = CreateBitmap(100, 32);
        var pen = new Pen(
            Brushes.Black,
            4,
            new DashStyle([2, 2], 0),
            PenLineCap.Flat,
            PenLineJoin.Miter);

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawLine(pen, new Point(4, 16), new Point(96, 16));
        }

        var pixels = bitmap.CopyPixelBytes();
        var visibleSamples = CountPixels(bitmap, pixels, 4, 14, 92, 5, static pixel => pixel.A > 96);

        Assert.InRange(visibleSamples, 120, 360);
    }

    [Fact]
    public void RenderTargetBitmap_Readback_RendersCombinedGeometryHole()
    {
        using var bitmap = CreateBitmap(80, 80);
        var outer = NativeGeometry.Rectangle(new Rect(8, 8, 64, 64));
        var inner = NativeGeometry.Ellipse(new Rect(24, 24, 32, 32));
        var geometry = NativeGeometry.Combined(GeometryCombineMode.Exclude, outer, inner);

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawGeometry(Brushes.OrangeRed, null, geometry);
        }

        var pixels = bitmap.CopyPixelBytes();

        AssertColor(GetPixel(bitmap, pixels, 14, 14), r: 255, g: 69, b: 0, a: 255, tolerance: 2);
        Assert.True(GetPixel(bitmap, pixels, 40, 40).A <= 2, "Expected excluded ellipse center to remain transparent.");
    }

    [Fact]
    public void RenderTargetBitmap_Readback_RendersNativeGlyphRun()
    {
        using var bitmap = CreateBitmap(96, 64);
        using var glyphRun = CreateGlyphRun("AV", 38, new Point(6, 46));

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawGlyphRun(Brushes.Black, glyphRun);
        }

        var pixels = bitmap.CopyPixelBytes();
        var visibleGlyphPixels = CountPixels(bitmap, pixels, 0, 0, 96, 64, static pixel => pixel.A > 32);

        Assert.True(visibleGlyphPixels > 80, $"Expected visible glyph rasterization, got {visibleGlyphPixels} visible pixels.");
    }

    [Fact]
    public void RenderTargetBitmap_DrawGlyphRun_UsesTextOptionsRenderingMode()
    {
        using var bitmap = CreateBitmap(180, 64);
        using var antialiasedGlyphRun = CreateGlyphRun("AV", 38, new Point(6, 46));
        using var aliasedGlyphRun = CreateGlyphRun("AV", 38, new Point(96, 46));

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawGlyphRun(Brushes.Black, antialiasedGlyphRun);

            context.PushTextOptions(new TextOptions
            {
                TextRenderingMode = TextRenderingMode.Alias,
                TextHintingMode = TextHintingMode.None,
                BaselinePixelAlignment = BaselinePixelAlignment.Aligned
            });
            try
            {
                context.DrawGlyphRun(Brushes.Black, aliasedGlyphRun);
            }
            finally
            {
                context.PopTextOptions();
            }
        }

        var pixels = bitmap.CopyPixelBytes();
        var antialiasedPartial = CountPixels(bitmap, pixels, 0, 0, 84, 64, static pixel => pixel.A is > 0 and < 255);
        var aliasedPartial = CountPixels(bitmap, pixels, 90, 0, 84, 64, static pixel => pixel.A is > 0 and < 255);

        Assert.True(antialiasedPartial > 20, $"Expected antialiased glyph edge pixels, got {antialiasedPartial}.");
        Assert.True(aliasedPartial < antialiasedPartial / 2, $"Expected aliased glyph to reduce partial alpha, got alias={aliasedPartial}, antialias={antialiasedPartial}.");
    }

    [Fact]
    public void DiagnosticsCallback_ReceivesBulkFlushCounters()
    {
        SkiaNativeFrameDiagnostics? diagnostics = null;
        using var bitmap = CreateBitmap(16, 16, options =>
        {
            options.EnableDiagnostics = true;
            options.DiagnosticsCallback = value => diagnostics = value;
        });

        using (var context = bitmap.CreateDrawingContext())
        {
            context.Clear(Colors.Transparent);
            context.DrawRectangle(Brushes.Red, null, new RoundedRect(new Rect(0, 0, 16, 16)));
        }

        Assert.NotNull(diagnostics);
        Assert.True(diagnostics.Value.CommandCount >= 2);
        Assert.Equal(1, diagnostics.Value.NativeTransitionCount);
        Assert.Equal(diagnostics.Value.CommandCount, diagnostics.Value.NativeResult);
    }

    private static NativeRenderTargetBitmap CreateBitmap(int width, int height, Action<SkiaNativeOptions>? configure = null)
    {
        var nativeLibraryPath = FindNativeLibrary();
        Assert.SkipUnless(nativeLibraryPath is not null, "Native dylib artifacts are required for render tests.");

        var options = new SkiaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath
        };
        configure?.Invoke(options);
        NativeLibraryResolver.Configure(options);
        return new NativeRenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96), options);
    }

    private static void DrawParityScene(NativeRenderTargetBitmap bitmap)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.DeepSkyBlue, 0),
                new GradientStop(Colors.Gold, 0.55),
                new GradientStop(Colors.OrangeRed, 1)
            }
        };

        using var context = bitmap.CreateDrawingContext();
        context.Clear(Colors.Transparent);
        context.DrawRectangle(gradient, new Pen(Brushes.Black, 1), new RoundedRect(new Rect(4, 4, 88, 42), 8));
        context.DrawEllipse(Brushes.White, new Pen(Brushes.MidnightBlue, 2), new Rect(12, 16, 28, 28));
        context.DrawLine(new Pen(Brushes.Crimson, 3, DashStyle.Dash, PenLineCap.Round, PenLineJoin.Round), new Point(46, 48), new Point(88, 12));
    }

    private static unsafe NativeWriteableBitmap CreateQuadrantBitmap()
    {
        var bitmap = new NativeWriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = bitmap.Lock();
        var basePtr = (byte*)framebuffer.Address;

        for (var y = 0; y < framebuffer.Size.Height; y++)
        {
            var row = basePtr + y * framebuffer.RowBytes;
            for (var x = 0; x < framebuffer.Size.Width; x++)
            {
                var pixel = row + x * 4;
                if (x < 2 && y < 2)
                {
                    SetBgra(pixel, b: 0, g: 0, r: 255);
                }
                else if (x >= 2 && y < 2)
                {
                    SetBgra(pixel, b: 0, g: 255, r: 0);
                }
                else if (x < 2)
                {
                    SetBgra(pixel, b: 255, g: 0, r: 0);
                }
                else
                {
                    SetBgra(pixel, b: 0, g: 255, r: 255);
                }
            }
        }

        return bitmap;
    }

    private static unsafe NativeWriteableBitmap CreateSolidBitmap(int width, int height, Color color)
    {
        var bitmap = new NativeWriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = bitmap.Lock();
        var basePtr = (byte*)framebuffer.Address;

        for (var y = 0; y < framebuffer.Size.Height; y++)
        {
            var row = basePtr + y * framebuffer.RowBytes;
            for (var x = 0; x < framebuffer.Size.Width; x++)
            {
                SetBgra(row + x * 4, color.B, color.G, color.R);
            }
        }

        return bitmap;
    }

    private static NativeGlyphRun CreateGlyphRun(string text, double size, Point baselineOrigin)
    {
        var fontManager = new NativeFontManager();
        Assert.SkipUnless(
            fontManager.TryCreateGlyphTypeface("Arial", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var platformTypeface),
            "A native macOS test font is required for glyph-run render tests.");

        var glyphTypeface = new GlyphTypeface(platformTypeface);
        var scale = size / glyphTypeface.Metrics.DesignEmHeight;
        var glyphInfos = new GlyphInfo[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(text[i], out var glyph), $"Font did not contain glyph for '{text[i]}'.");
            var advance = glyphTypeface.TryGetHorizontalGlyphAdvance(glyph, out var advanceDesignUnits)
                ? advanceDesignUnits * scale
                : size * 0.7;
            glyphInfos[i] = new GlyphInfo(glyph, i, advance);
        }

        return new NativeGlyphRun(glyphTypeface, size, glyphInfos, baselineOrigin);
    }

    private static BgraPixel GetPixel(NativeWriteableBitmap bitmap, byte[] pixels, int x, int y)
    {
        var offset = y * bitmap.PixelRowBytes + x * 4;
        return new BgraPixel(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static int CountPixels(NativeWriteableBitmap bitmap, byte[] pixels, int x, int y, int width, int height, Func<BgraPixel, bool> predicate)
    {
        var count = 0;
        var maxX = Math.Min(bitmap.PixelSize.Width, x + width);
        var maxY = Math.Min(bitmap.PixelSize.Height, y + height);

        for (var py = Math.Max(0, y); py < maxY; py++)
        {
            for (var px = Math.Max(0, x); px < maxX; px++)
            {
                if (predicate(GetPixel(bitmap, pixels, px, py)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static unsafe void SetBgra(byte* pixel, byte b, byte g, byte r)
    {
        pixel[0] = b;
        pixel[1] = g;
        pixel[2] = r;
        pixel[3] = 255;
    }

    private static void AssertColor(BgraPixel pixel, byte r, byte g, byte b, byte a, byte tolerance)
    {
        Assert.InRange(Math.Abs(pixel.R - r), 0, tolerance);
        Assert.InRange(Math.Abs(pixel.G - g), 0, tolerance);
        Assert.InRange(Math.Abs(pixel.B - b), 0, tolerance);
        Assert.InRange(Math.Abs(pixel.A - a), 0, tolerance);
    }

    private static void AssertPngSignature(byte[] bytes)
    {
        ReadOnlySpan<byte> expected = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(bytes.Length > expected.Length, $"Expected PNG payload, got {bytes.Length} bytes.");
        Assert.True(bytes.AsSpan(0, expected.Length).SequenceEqual(expected), "Saved bitmap did not start with the PNG signature.");
    }

    private static string? FindNativeLibrary()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64",
            Architecture.X64 => "osx-x64",
            _ => null
        };

        if (rid is null)
        {
            return null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "native", rid, "libSkiaNativeAvalonia.dylib");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private readonly record struct BgraPixel(byte B, byte G, byte R, byte A);
}
