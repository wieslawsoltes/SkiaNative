using Avalonia;
using Avalonia.Metal;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using SkiaNative.Avalonia.Imaging;

namespace SkiaNative.Avalonia.Backend;

internal sealed class NativeRenderInterfaceContext : IPlatformRenderInterfaceContext
{
    private readonly IPlatformGraphicsContext? _graphicsContext;
    private readonly SkiaNativeOptions _options;
    private NativeContextHandle? _nativeContext;

    public NativeRenderInterfaceContext(IPlatformGraphicsContext? graphicsContext, SkiaNativeOptions options)
    {
        _graphicsContext = graphicsContext;
        _options = options;
        PublicFeatures = new Dictionary<Type, object>();
    }

    internal NativeContextHandle NativeContext => _nativeContext ??= CreateNativeContext();

    public IReadOnlyDictionary<Type, object> PublicFeatures { get; }
    public PixelSize? MaxOffscreenRenderTargetPixelSize => null;
    public bool IsLost => _graphicsContext?.IsLost ?? false;

    public IRenderTarget CreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
    {
        var surfaceList = surfaces as IList<IPlatformRenderSurface> ?? surfaces.ToList();

        if (_graphicsContext is IMetalDevice metal)
        {
            foreach (var surface in surfaceList)
            {
                if (surface is IMetalPlatformSurface metalSurface)
                {
                    return new NativeMetalRenderTarget(this, metal, metalSurface, _options);
                }
            }
        }

        if (!_options.EnableCpuFallback)
        {
            throw new NotSupportedException("SkiaNative could not find a supported GPU surface and CPU fallback is disabled.");
        }

        foreach (var surface in surfaceList)
        {
            if (surface is IFramebufferPlatformSurface framebuffer)
            {
                return new NativeFramebufferRenderTarget(framebuffer, _options);
            }
        }

        throw new NotSupportedException("SkiaNative does not know how to render to the provided platform surfaces.");
    }

    public IDrawingContextLayerImpl CreateOffscreenRenderTarget(PixelSize pixelSize, Vector scaling, bool enableTextAntialiasing)
    {
        return new NativeRenderTargetBitmap(pixelSize, scaling * 96, _options);
    }

    public bool IsReadyToCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
    {
        if (_graphicsContext is IMetalDevice)
        {
            return surfaces.Any(static s => s is IMetalPlatformSurface && s.IsReady);
        }

        return _options.EnableCpuFallback && surfaces.Any(static s => s is IFramebufferPlatformSurface && s.IsReady);
    }

    public object? TryGetFeature(Type featureType) => null;

    public void Dispose()
    {
        _nativeContext?.Dispose();
        _nativeContext = null;
    }

    private NativeContextHandle CreateNativeContext()
    {
        if (_graphicsContext is IMetalDevice metal)
        {
            return NativeMethods.ContextCreateMetal(
                metal.Device,
                metal.CommandQueue,
                checked((ulong)(_options.MaxGpuResourceBytes ?? 0)),
                _options.EnableDiagnostics ? 1 : 0);
        }

        return NativeMethods.ContextCreateCpu(
            checked((ulong)(_options.MaxGpuResourceBytes ?? 0)),
            _options.EnableDiagnostics ? 1 : 0);
    }
}
