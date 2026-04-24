using Avalonia.Controls;
using SkiaNative.Avalonia;

namespace Avalonia;

public static class SkiaNativeApplicationExtensions
{
    public static AppBuilder UseSkiaNative(this AppBuilder builder, SkiaNativeOptions options)
    {
        return builder
            .With(options)
            .UseSkiaNative();
    }

    public static AppBuilder UseSkiaNative(this AppBuilder builder)
    {
        return builder.UseRenderingSubsystem(
            () => SkiaNativePlatform.Initialize(
                AvaloniaLocator.Current.GetService<SkiaNativeOptions>() ?? new SkiaNativeOptions()),
            "SkiaNative");
    }
}
