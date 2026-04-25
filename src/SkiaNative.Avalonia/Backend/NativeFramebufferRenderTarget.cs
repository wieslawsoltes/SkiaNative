using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using SkiaNative.Avalonia.Imaging;

namespace SkiaNative.Avalonia.Backend;

internal sealed class NativeFramebufferRenderTarget : IRenderTarget
{
    private readonly SkiaNativeOptions _options;
    private IFramebufferRenderTarget? _target;

    public NativeFramebufferRenderTarget(IFramebufferPlatformSurface surface, SkiaNativeOptions options)
    {
        _options = options;
        _target = surface.CreateFramebufferRenderTarget();
    }

    public RenderTargetProperties Properties => new()
    {
        IsSuitableForDirectRendering = true,
        RetainsPreviousFrameContents = _target?.RetainsFrameContents == true
    };
    public PlatformRenderTargetState PlatformRenderTargetState => _target?.State ?? PlatformRenderTargetState.Disposed;

    public IDrawingContextImpl CreateDrawingContext(IRenderTarget.RenderTargetSceneInfo sceneInfo, out RenderTargetDrawingContextProperties properties)
    {
        if (_target is null)
        {
            throw new ObjectDisposedException(nameof(NativeFramebufferRenderTarget));
        }

        var framebuffer = _target.Lock(sceneInfo, out var lockProperties);

        var nativeContext = NativeMethods.ContextCreateCpu(
            checked((ulong)(_options.MaxGpuResourceBytes ?? 0)),
            _options.EnableDiagnostics ? 1 : 0,
            (int)_options.GpuSubmitMode);
        var nativeBitmap = NativeMethods.BitmapCreateRaster(framebuffer.Size.Width, framebuffer.Size.Height, framebuffer.Dpi.X, framebuffer.Dpi.Y);
        var previousFrameSeeded = false;
        unsafe
        {
            if (lockProperties.PreviousFrameIsRetained && framebuffer.Address != IntPtr.Zero)
            {
                previousFrameSeeded = NativeMethods.BitmapUploadPixels(
                    nativeBitmap,
                    (byte*)framebuffer.Address,
                    framebuffer.RowBytes,
                    NativeWriteableBitmap.ToNativePixelFormat(framebuffer.Format),
                    NativeWriteableBitmap.ToNativeAlphaFormat(framebuffer.AlphaFormat)) == 0;
            }
        }

        properties = new RenderTargetDrawingContextProperties { PreviousFrameIsRetained = previousFrameSeeded };

        var nativeSession = NativeMethods.SessionBeginBitmap(nativeContext, nativeBitmap, framebuffer.Dpi.X, framebuffer.Dpi.Y);
        return new NativeDrawingContext(nativeSession, framebuffer.Dpi.X / 96.0, new FramebufferCopyOnDispose(framebuffer, nativeBitmap, nativeContext), _options);
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
    }

    private sealed class FramebufferCopyOnDispose : IDisposable
    {
        private readonly ILockedFramebuffer _framebuffer;
        private readonly NativeBitmapHandle _bitmap;
        private readonly NativeContextHandle _context;

        public FramebufferCopyOnDispose(ILockedFramebuffer framebuffer, NativeBitmapHandle bitmap, NativeContextHandle context)
        {
            _framebuffer = framebuffer;
            _bitmap = bitmap;
            _context = context;
        }

        public unsafe void Dispose()
        {
            try
            {
                if (!_bitmap.IsInvalid && _framebuffer.Address != IntPtr.Zero)
                {
                    _ = NativeMethods.BitmapReadPixels(
                        _bitmap,
                        (byte*)_framebuffer.Address,
                        _framebuffer.RowBytes,
                        NativeWriteableBitmap.ToNativePixelFormat(_framebuffer.Format),
                        NativeWriteableBitmap.ToNativeAlphaFormat(_framebuffer.AlphaFormat));
                }
            }
            finally
            {
                _bitmap.Dispose();
                _framebuffer.Dispose();
                _context.Dispose();
            }
        }
    }

}
