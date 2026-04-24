using Avalonia.Metal;
using Avalonia.Platform;

namespace SkiaNative.Avalonia.Backend;

internal sealed class NativeMetalRenderTarget : IRenderTarget
{
    private readonly NativeRenderInterfaceContext _context;
    private readonly SkiaNativeOptions _options;
    private IMetalPlatformSurfaceRenderTarget? _target;

    public NativeMetalRenderTarget(NativeRenderInterfaceContext context, IMetalDevice device, IMetalPlatformSurface surface, SkiaNativeOptions options)
    {
        _context = context;
        _options = options;
        _target = surface.CreateMetalRenderTarget(device);
    }

    public RenderTargetProperties Properties => new()
    {
        IsSuitableForDirectRendering = true,
        RetainsPreviousFrameContents = true
    };
    public PlatformRenderTargetState PlatformRenderTargetState => _target?.State ?? PlatformRenderTargetState.Disposed;

    public IDrawingContextImpl CreateDrawingContext(IRenderTarget.RenderTargetSceneInfo sceneInfo, out RenderTargetDrawingContextProperties properties)
    {
        properties = new RenderTargetDrawingContextProperties { PreviousFrameIsRetained = false };
        var platformSession = (_target ?? throw new ObjectDisposedException(nameof(NativeMetalRenderTarget))).BeginRendering();
        var nativeSession = NativeMethods.SessionBeginMetal(
            _context.NativeContext,
            platformSession.Texture,
            platformSession.Size.Width,
            platformSession.Size.Height,
            platformSession.Scaling,
            platformSession.IsYFlipped ? 1 : 0);

        return new NativeDrawingContext(
            nativeSession,
            sceneInfo.Scaling,
            platformSession,
            _options,
            _context.NativeContext);
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
    }
}
