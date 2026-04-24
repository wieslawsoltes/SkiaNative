using Avalonia.Platform;
using SkiaNative.Avalonia.Backend;

namespace SkiaNative.Avalonia.Imaging;

internal sealed class NativeRenderTargetBitmap : NativeWriteableBitmap, IRenderTargetBitmapImpl, IDrawingContextLayerImpl
{
    private readonly SkiaNativeOptions _options;

    public NativeRenderTargetBitmap(PixelSize size, Vector dpi, SkiaNativeOptions options)
        : base(size, dpi, PixelFormats.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul)
    {
        _options = options;
    }

    public bool CanBlit => true;
    public bool IsCorrupted => false;

    public IDrawingContextImpl CreateDrawingContext()
    {
        var context = NativeMethods.ContextCreateCpu(checked((ulong)(_options.MaxGpuResourceBytes ?? 0)), _options.EnableDiagnostics ? 1 : 0);
        var session = NativeMethods.SessionBeginBitmap(context, NativeBitmap, Dpi.X, Dpi.Y);
        return new NativeDrawingContext(session, Dpi.X / 96.0, context, _options);
    }

    public void Blit(IDrawingContextImpl context)
    {
        var bounds = new Rect(0, 0, PixelSize.Width, PixelSize.Height);
        context.DrawBitmap(this, 1, bounds, bounds);
    }
}
