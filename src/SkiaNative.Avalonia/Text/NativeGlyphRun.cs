using Avalonia.Media;
using Avalonia.Platform;
using SkiaNative.Avalonia.Geometry;

namespace SkiaNative.Avalonia.Text;

internal sealed unsafe class NativeGlyphRun : IGlyphRunImpl
{
    private const uint TextEdgingAlias = 1u;
    private const uint TextEdgingAntialias = 2u;
    private const uint TextEdgingSubpixelAntialias = 3u;
    private const int TextHintingShift = 2;
    private const uint TextHintingNone = 1u;
    private const uint TextHintingLight = 2u;
    private const uint TextHintingStrong = 3u;
    private const uint TextForceAutoHintingFlag = 1u << 4;
    private const uint TextSubpixelFlag = 1u << 5;
    private const uint TextBaselineSnapFlag = 1u << 6;

    private readonly ushort[] _glyphIndices;
    private readonly NativeGlyphPosition[] _positions;
    private uint _primaryNativeGlyphRunOptions;
    private NativeGlyphRunHandle? _primaryNativeGlyphRun;
    private Dictionary<uint, NativeGlyphRunHandle>? _additionalNativeGlyphRuns;

    public NativeGlyphRun(GlyphTypeface glyphTypeface, double fontRenderingEmSize, IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
    {
        GlyphTypeface = glyphTypeface;
        FontRenderingEmSize = fontRenderingEmSize;
        BaselineOrigin = baselineOrigin;
        _glyphIndices = new ushort[glyphInfos.Count];
        _positions = new NativeGlyphPosition[glyphInfos.Count];

        var x = 0.0;
        var bounds = default(Rect);
        var hasBounds = false;

        for (var i = 0; i < glyphInfos.Count; i++)
        {
            var glyph = glyphInfos[i];
            _glyphIndices[i] = glyph.GlyphIndex;
            _positions[i] = new NativeGlyphPosition((float)(x + glyph.GlyphOffset.X), (float)glyph.GlyphOffset.Y);
            var glyphRect = new Rect(baselineOrigin.X + x + glyph.GlyphOffset.X, baselineOrigin.Y - fontRenderingEmSize, Math.Max(glyph.GlyphAdvance, 1), fontRenderingEmSize * 1.25);
            bounds = hasBounds ? bounds.Union(glyphRect) : glyphRect;
            hasBounds = true;
            x += glyph.GlyphAdvance;
        }

        Bounds = hasBounds ? bounds : new Rect(baselineOrigin.X, baselineOrigin.Y, 0, 0);
    }

    public GlyphTypeface GlyphTypeface { get; }
    public double FontRenderingEmSize { get; }
    public Point BaselineOrigin { get; }
    public Rect Bounds { get; }
    public ReadOnlySpan<ushort> GlyphIndices => _glyphIndices;
    public ReadOnlySpan<NativeGlyphPosition> Positions => _positions;
    internal NativeGlyphRunHandle NativeGlyphRunHandle => GetNativeGlyphRunHandle(default, default);

    internal NativeGlyphRunHandle GetNativeGlyphRunHandle(TextOptions textOptions, RenderOptions renderOptions)
    {
        var options = CreateTextOptions(textOptions, renderOptions);
        if (_primaryNativeGlyphRun is null)
        {
            _primaryNativeGlyphRunOptions = options;
            _primaryNativeGlyphRun = CreateNativeGlyphRun(options);
            return _primaryNativeGlyphRun;
        }

        if (options == _primaryNativeGlyphRunOptions)
        {
            return _primaryNativeGlyphRun;
        }

        _additionalNativeGlyphRuns ??= new Dictionary<uint, NativeGlyphRunHandle>();
        if (!_additionalNativeGlyphRuns.TryGetValue(options, out var additional))
        {
            _additionalNativeGlyphRuns.Add(options, additional = CreateNativeGlyphRun(options));
        }

        return additional;
    }

    public IReadOnlyList<float> GetIntersections(float lowerLimit, float upperLimit)
    {
        var handle = NativeGlyphRunHandle;
        var count = NativeMethods.GlyphRunGetIntersections(handle, lowerLimit, upperLimit, null, 0);
        if (count <= 0)
        {
            return Array.Empty<float>();
        }

        var values = new float[count];
        fixed (float* ptr = values)
        {
            var written = NativeMethods.GlyphRunGetIntersections(handle, lowerLimit, upperLimit, ptr, values.Length);
            if (written == values.Length)
            {
                return values;
            }

            return written <= 0 ? Array.Empty<float>() : values.AsSpan(0, Math.Min(written, values.Length)).ToArray();
        }
    }

    internal IGeometryImpl CreateOutlineGeometry() => NativePathGeometry.FromGlyphRun(NativeGlyphRunHandle, Bounds);

    public void Dispose()
    {
        _primaryNativeGlyphRun?.Dispose();
        _primaryNativeGlyphRun = null;

        if (_additionalNativeGlyphRuns is null)
        {
            return;
        }

        foreach (var handle in _additionalNativeGlyphRuns.Values)
        {
            handle.Dispose();
        }

        _additionalNativeGlyphRuns.Clear();
    }

    private NativeGlyphRunHandle CreateNativeGlyphRun(uint textOptions)
    {
        if (GlyphTypeface.PlatformTypeface is not NativeTypeface nativeTypeface)
        {
            throw new InvalidOperationException("SkiaNative glyph runs require a SkiaNative platform typeface.");
        }

        fixed (ushort* glyphs = _glyphIndices)
        fixed (NativeGlyphPosition* positions = _positions)
        {
            var handle = NativeMethods.GlyphRunCreateWithOptions(
                nativeTypeface.NativeHandle,
                (float)FontRenderingEmSize,
                glyphs,
                positions,
                _glyphIndices.Length,
                (float)BaselineOrigin.X,
                (float)BaselineOrigin.Y,
                textOptions);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException("SkiaNative could not create the native glyph run.");
            }

            return handle;
        }
    }

    private static uint CreateTextOptions(TextOptions textOptions, RenderOptions renderOptions)
    {
        var effective = textOptions;

#pragma warning disable CS0618
        if (effective.TextRenderingMode == TextRenderingMode.Unspecified && renderOptions.TextRenderingMode != TextRenderingMode.Unspecified)
        {
            effective = effective with { TextRenderingMode = renderOptions.TextRenderingMode };
        }
#pragma warning restore CS0618

        if (effective.TextRenderingMode == TextRenderingMode.Unspecified)
        {
            effective = effective with
            {
                TextRenderingMode = renderOptions.EdgeMode == EdgeMode.Aliased
                    ? TextRenderingMode.Alias
                    : TextRenderingMode.SubpixelAntialias
            };
        }

        if (effective.TextHintingMode == TextHintingMode.Unspecified)
        {
            effective = effective with { TextHintingMode = TextHintingMode.Strong };
        }

        var flags = effective.TextRenderingMode switch
        {
            TextRenderingMode.Alias => TextEdgingAlias,
            TextRenderingMode.Antialias => TextEdgingAntialias | TextSubpixelFlag,
            TextRenderingMode.SubpixelAntialias => TextEdgingSubpixelAntialias | TextSubpixelFlag,
            _ => TextEdgingSubpixelAntialias | TextSubpixelFlag
        };

        flags |= effective.TextHintingMode switch
        {
            TextHintingMode.None => TextHintingNone << TextHintingShift,
            TextHintingMode.Light => (TextHintingLight << TextHintingShift) | TextForceAutoHintingFlag,
            TextHintingMode.Strong => TextHintingStrong << TextHintingShift,
            _ => TextHintingStrong << TextHintingShift
        };

        if (effective.BaselinePixelAlignment != BaselinePixelAlignment.Unaligned)
        {
            flags |= TextBaselineSnapFlag;
        }

        return flags;
    }
}
