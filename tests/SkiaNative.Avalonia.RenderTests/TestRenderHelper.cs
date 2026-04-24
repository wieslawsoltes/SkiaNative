using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Harfbuzz;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaNative.Avalonia;
using Xunit;
using Image = SixLabors.ImageSharp.Image;

namespace Avalonia.Skia.RenderTests;

internal static class TestRenderHelper
{
    private static readonly object s_testFilesLock = new();
    private static string? s_testsDirectory;

    static TestRenderHelper()
    {
        InitializeAvaloniaServices();
    }

    public static Task RenderToFile(Control target, string path, bool immediate, double dpi = 96)
    {
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);

        var factory = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
        var pixelSize = new PixelSize((int)target.Width, (int)target.Height);
        var size = new Size(target.Width, target.Height);
        var dpiVector = new Vector(dpi, dpi);

        if (immediate)
        {
            using var bitmap = new RenderTargetBitmap(pixelSize, dpiVector);
            target.Measure(size);
            target.Arrange(new Rect(size));
            bitmap.Render(target);
            bitmap.Save(path);
        }
        else
        {
            var timer = new ManualRenderTimer();
            var compositor = new Compositor(
                RenderLoop.FromTimer(timer),
                null,
                true,
                new DispatcherCompositorScheduler(),
                true,
                Dispatcher.UIThread);

            using var writableBitmap = factory.CreateWriteableBitmap(
                pixelSize,
                dpiVector,
                factory.DefaultPixelFormat,
                factory.DefaultAlphaFormat);
            var root = new TestRenderRoot(dpiVector.X / 96, null!);
            using (var renderer = new CompositingRenderer(
                       root,
                       compositor,
                       () => new[] { new BitmapFramebufferSurface(writableBitmap) }))
            {
                root.Initialize(renderer, target);
                renderer.Start();
                Dispatcher.UIThread.RunJobs();
                renderer.Paint(new Rect(root.Bounds.Size), false);
            }

            writableBitmap.Save(path);
        }

        return Task.CompletedTask;
    }

    public static void BeginTest()
    {
        InitializeAvaloniaServices();
        Dispatcher.ResetBeforeUnitTests();
    }

    public static void EndTest()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.ResetForUnitTests();
    }

    public static string GetTestsDirectory()
    {
        if (s_testsDirectory is not null)
        {
            return s_testsDirectory;
        }

        lock (s_testFilesLock)
        {
            if (s_testsDirectory is not null)
            {
                return s_testsDirectory;
            }

            var sourceTestFiles = ResolveAvaloniaSourceTestFiles();
            var repoRoot = ResolveRepositoryRoot();
            var testsDirectory = Path.Combine(repoRoot, "artifacts", "avalonia-render-tests");
            var targetTestFiles = Path.Combine(testsDirectory, "TestFiles");
            CopyDirectory(sourceTestFiles, targetTestFiles);
            s_testsDirectory = testsDirectory;
            return testsDirectory;
        }
    }

    public static void AssertCompareImages(string actualPath, string expectedPath)
    {
        using var expected = Image.Load<Rgba32>(expectedPath);
        using var actual = Image.Load<Rgba32>(actualPath);
        var error = CompareImages(actual, expected);

        if (error > 0.022)
        {
            Assert.Fail(actualPath + ": Error = " + error);
        }
    }

    public static double CompareImages(Image<Rgba32> actual, Image<Rgba32> expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            throw new ArgumentException("Images have different resolutions");
        }

        var quantity = actual.Width * actual.Height;
        double squaresError = 0;
        const double scale = 1 / 255d;

        for (var x = 0; x < actual.Width; x++)
        {
            double localError = 0;

            for (var y = 0; y < actual.Height; y++)
            {
                var expectedAlpha = expected[x, y].A * scale;
                var actualAlpha = actual[x, y].A * scale;

                var r = scale * (expectedAlpha * expected[x, y].R - actualAlpha * actual[x, y].R);
                var g = scale * (expectedAlpha * expected[x, y].G - actualAlpha * actual[x, y].G);
                var b = scale * (expectedAlpha * expected[x, y].B - actualAlpha * actual[x, y].B);
                var a = expectedAlpha - actualAlpha;

                localError += r * r + g * g + b * b + a * a;
            }

            squaresError += localError;
        }

        var meanSquaresError = squaresError / quantity;
        return Math.Sqrt(meanSquaresError / 4);
    }

    private static void InitializeAvaloniaServices()
    {
        var options = new SkiaNativeOptions
        {
            NativeLibraryPath = FindNativeLibrary(),
            EnableCpuFallback = true
        };

        SkiaNativePlatform.Initialize(options);
        AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
        AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant(new HarfBuzzTextShaper());
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new NullCursorFactory());
    }

    private static string ResolveAvaloniaSourceTestFiles()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("AVALONIA_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configured = Path.Combine(configuredRoot, "tests", "TestFiles");
            if (Directory.Exists(configured))
            {
                return configured;
            }
        }

        const string defaultRoot = "/Users/wieslawsoltes/GitHub/Avalonia/tests/TestFiles";
        if (Directory.Exists(defaultRoot))
        {
            return defaultRoot;
        }

        throw new DirectoryNotFoundException("Avalonia tests/TestFiles directory was not found. Set AVALONIA_SOURCE_ROOT to the local Avalonia source root.");
    }

    private static string ResolveRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SkiaNative.Avalonia.slnx")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            var targetFile = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
            if (!File.Exists(targetFile))
            {
                File.Copy(sourceFile, targetFile);
            }
        }

        foreach (var sourceChild in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(sourceChild, Path.Combine(targetDirectory, Path.GetFileName(sourceChild)));
        }
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

    private sealed class BitmapFramebufferSurface(IWriteableBitmapImpl bitmap) : IFramebufferPlatformSurface
    {
        public IFramebufferRenderTarget CreateFramebufferRenderTarget()
        {
            return new FuncFramebufferRenderTarget(() => bitmap.Lock());
        }
    }

    private sealed class NullCursorFactory : ICursorFactory
    {
        public ICursorImpl GetCursor(StandardCursorType cursorType) => new NullCursor();
        public ICursorImpl CreateCursor(Bitmap cursor, PixelPoint hotSpot) => new NullCursor();

        private sealed class NullCursor : ICursorImpl
        {
            public void Dispose()
            {
            }
        }
    }
}
