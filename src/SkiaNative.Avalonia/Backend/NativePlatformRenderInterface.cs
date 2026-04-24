using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Metal;
using Avalonia.Platform;
using SkiaNative.Avalonia.Geometry;
using SkiaNative.Avalonia.Imaging;
using SkiaNative.Avalonia.Text;

namespace SkiaNative.Avalonia.Backend;

internal sealed class NativePlatformRenderInterface : IPlatformRenderInterface
{
    private readonly SkiaNativeOptions _options;

    public NativePlatformRenderInterface(SkiaNativeOptions options)
    {
        _options = options;
    }

    public bool SupportsIndividualRoundRects => true;
    public AlphaFormat DefaultAlphaFormat => AlphaFormat.Premul;
    public PixelFormat DefaultPixelFormat => PixelFormats.Bgra8888;
    public bool SupportsRegions => true;

    public IPlatformRenderInterfaceContext CreateBackendContext(IPlatformGraphicsContext? graphicsApiContext)
    {
        return new NativeRenderInterfaceContext(graphicsApiContext, _options);
    }

    public IGeometryImpl CreateEllipseGeometry(Rect rect) => NativeGeometry.Ellipse(rect);
    public IGeometryImpl CreateLineGeometry(Point p1, Point p2) => NativeGeometry.Line(p1, p2);
    public IGeometryImpl CreateRectangleGeometry(Rect rect) => NativeGeometry.Rectangle(rect);
    public IStreamGeometryImpl CreateStreamGeometry() => new NativeStreamGeometry();
    public IGeometryImpl CreateGeometryGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children) => NativeGeometry.Group(fillRule, children);
    public IGeometryImpl CreateCombinedGeometry(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2) => NativeGeometry.Combined(combineMode, g1, g2);

    public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun)
    {
        try
        {
            using var nativeGlyphRun = new NativeGlyphRun(
                glyphRun.GlyphTypeface,
                glyphRun.FontRenderingEmSize,
                glyphRun.GlyphInfos,
                glyphRun.BaselineOrigin);

            return nativeGlyphRun.CreateOutlineGeometry();
        }
        catch
        {
            return NativeGeometry.Rectangle(glyphRun.Bounds);
        }
    }

    public IRenderTargetBitmapImpl CreateRenderTargetBitmap(PixelSize size, Vector dpi) => new NativeRenderTargetBitmap(size, dpi, _options);

    public IWriteableBitmapImpl CreateWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat) =>
        new NativeWriteableBitmap(size, dpi, format, alphaFormat);

    public IBitmapImpl LoadBitmap(string fileName)
    {
        using var stream = File.OpenRead(fileName);
        return LoadBitmap(stream);
    }

    public IBitmapImpl LoadBitmap(Stream stream) => NativeWriteableBitmap.FromStream(stream);

    public IWriteableBitmapImpl LoadWriteableBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
        NativeWriteableBitmap.FromStream(stream, width, null, interpolationMode);

    public IWriteableBitmapImpl LoadWriteableBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
        NativeWriteableBitmap.FromStream(stream, null, height, interpolationMode);

    public IWriteableBitmapImpl LoadWriteableBitmap(string fileName)
    {
        using var stream = File.OpenRead(fileName);
        return LoadWriteableBitmap(stream);
    }

    public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream) => NativeWriteableBitmap.FromStream(stream);

    public IBitmapImpl LoadBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
        NativeWriteableBitmap.FromStream(stream, width, null, interpolationMode);

    public IBitmapImpl LoadBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
        NativeWriteableBitmap.FromStream(stream, null, height, interpolationMode);

    public IBitmapImpl ResizeBitmap(IBitmapImpl bitmapImpl, PixelSize destinationSize, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) =>
        NativeWriteableBitmap.Resize(bitmapImpl, destinationSize, _options, interpolationMode);

    public IBitmapImpl LoadBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride) =>
        NativeWriteableBitmap.FromPixels(format, alphaFormat, data, size, dpi, stride);

    public IGlyphRunImpl CreateGlyphRun(GlyphTypeface glyphTypeface, double fontRenderingEmSize, IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin) =>
        new NativeGlyphRun(glyphTypeface, fontRenderingEmSize, glyphInfos, baselineOrigin);

    public bool IsSupportedBitmapPixelFormat(PixelFormat format) =>
        format == PixelFormats.Bgra8888 || format == PixelFormats.Rgba8888 || format == PixelFormats.Rgb565;

    public IPlatformRenderInterfaceRegion CreateRegion() => new NativeRegion();
}
