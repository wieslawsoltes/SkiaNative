using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaNative.Avalonia.Backend;
using SkiaNative.Avalonia.Text;

namespace SkiaNative.Avalonia;

public static class SkiaNativePlatform
{
    public static readonly Vector DefaultDpi = new(96, 96);

    public static void Initialize() => Initialize(new SkiaNativeOptions());

    public static void Initialize(SkiaNativeOptions options)
    {
        NativeLibraryResolver.Configure(options);
        AvaloniaLocator.CurrentMutable
            .Bind<IPlatformRenderInterface>().ToConstant(new NativePlatformRenderInterface(options))
            .Bind<IFontManagerImpl>().ToConstant(new NativeFontManager());
    }
}
