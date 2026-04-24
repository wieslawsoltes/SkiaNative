#if AVALONIA_SKIA
using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Xunit;
using Path = System.IO.Path;

namespace Avalonia.Skia.RenderTests;

public class DirectFbCompositionTests : TestBase
{
    public DirectFbCompositionTests()
        : base(@"Composition\DirectFb")
    {
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Should_Only_Update_Clipped_Rects_When_Retained_Fb_Is_Advertised(bool advertised)
    {
        var factory = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
        using var bitmap = factory.CreateWriteableBitmap(
            new PixelSize(200, 200),
            new Vector(96, 96),
            factory.DefaultPixelFormat,
            factory.DefaultAlphaFormat);

        var timer = new ManualRenderTimer();
        var compositor = new Compositor(
            RenderLoop.FromTimer(timer),
            null,
            true,
            new DispatcherCompositorScheduler(),
            true,
            Dispatcher.UIThread,
            new CompositionOptions { UseRegionDirtyRectClipping = true });

        Rectangle r1;
        Rectangle r2;
        var control = new Canvas
        {
            Width = 200,
            Height = 200,
            Background = Brushes.Yellow,
            Children =
            {
                (r1 = new Rectangle
                {
                    Fill = Brushes.Black,
                    Width = 40,
                    Height = 40,
                    Opacity = 0.6,
                    [Canvas.LeftProperty] = 40,
                    [Canvas.TopProperty] = 40,
                }),
                (r2 = new Rectangle
                {
                    Fill = Brushes.Black,
                    Width = 40,
                    Height = 40,
                    Opacity = 0.6,
                    [Canvas.LeftProperty] = 120,
                    [Canvas.TopProperty] = 40,
                }),
            }
        };

        var root = new TestRenderRoot(1, null!);
        var previousFrameIsRetained = false;
        IFramebufferRenderTarget renderTarget = new FuncFramebufferRenderTarget((_, out properties) =>
        {
            properties = new FramebufferLockProperties(previousFrameIsRetained);
            return bitmap.Lock();
        }, advertised);

        using var renderer = new CompositingRenderer(
            root,
            compositor,
            () => new[] { new FuncFramebufferSurface(() => renderTarget) });
        root.Initialize(renderer, control);
        control.Measure(new Size(control.Width, control.Height));
        control.Arrange(new Rect(control.DesiredSize));
        renderer.Start();
        Dispatcher.UIThread.RunJobs(null, TestContext.Current.CancellationToken);
        timer.TriggerTick();

        var image1 = $"{nameof(Should_Only_Update_Clipped_Rects_When_Retained_Fb_Is_Advertised)}_advertized-{advertised}_initial";
        SaveFile(bitmap, image1);
        ClearBitmap(bitmap);

        previousFrameIsRetained = advertised;
        r1.Fill = Brushes.Red;
        r2.Fill = Brushes.Green;
        Dispatcher.UIThread.RunJobs(null, TestContext.Current.CancellationToken);
        timer.TriggerTick();

        var image2 = $"{nameof(Should_Only_Update_Clipped_Rects_When_Retained_Fb_Is_Advertised)}_advertized-{advertised}_updated";
        SaveFile(bitmap, image2);
        CompareImages(image1, skipImmediate: true);
        CompareImages(image2, skipImmediate: true);
    }

    private void SaveFile(IBitmapImpl bitmap, string name)
    {
        Directory.CreateDirectory(OutputPath);
        bitmap.Save(Path.Combine(OutputPath, name + ".composited.out.png"));
    }

    private static unsafe void ClearBitmap(IWriteableBitmapImpl bitmap)
    {
        using var framebuffer = bitmap.Lock();
        var basePtr = (byte*)framebuffer.Address;

        for (var y = 0; y < framebuffer.Size.Height; y++)
        {
            new Span<byte>(basePtr + y * framebuffer.RowBytes, framebuffer.RowBytes).Clear();
        }
    }

    private sealed class FuncFramebufferSurface(Func<IFramebufferRenderTarget> callback) : IFramebufferPlatformSurface
    {
        public IFramebufferRenderTarget CreateFramebufferRenderTarget() => callback();
    }
}
#endif
