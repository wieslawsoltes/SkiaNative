using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Image = SixLabors.ImageSharp.Image;

namespace Avalonia.Skia.RenderTests;

public class TestBase : IDisposable
{
    private const double AllowedError = 0.022;
    private const string FontUri = "resm:Avalonia.Skia.RenderTests.Assets?assembly=Avalonia.Skia.RenderTests#Noto Mono";

    public static FontFamily TestFontFamily { get; } = new(FontUri);

    public TestBase(string outputPath)
    {
        outputPath = outputPath.Replace('\\', Path.DirectorySeparatorChar);
        var testFiles = Path.Combine(TestRenderHelper.GetTestsDirectory(), "TestFiles");
        OutputPath = Path.Combine(testFiles, "Skia", outputPath);

        TestRenderHelper.BeginTest();
    }

    public string OutputPath { get; }

    protected async Task RenderToFile(Control target, [CallerMemberName] string testName = "", double dpi = 96)
    {
        Directory.CreateDirectory(OutputPath);

        var immediatePath = Path.Combine(OutputPath, testName + ".immediate.out.png");
        var compositedPath = Path.Combine(OutputPath, testName + ".composited.out.png");
        await TestRenderHelper.RenderToFile(target, immediatePath, true, dpi);
        await TestRenderHelper.RenderToFile(target, compositedPath, false, dpi);
    }

    protected void CompareImages([CallerMemberName] string testName = "", bool skipImmediate = false, bool skipCompositor = false)
    {
        var expectedPath = Path.Combine(OutputPath, testName + ".expected.png");
        var immediatePath = Path.Combine(OutputPath, testName + ".immediate.out.png");
        var compositedPath = Path.Combine(OutputPath, testName + ".composited.out.png");

        using var expected = Image.Load<Rgba32>(expectedPath);
        using var immediate = skipImmediate ? null : Image.Load<Rgba32>(immediatePath);
        using var composited = skipCompositor ? null : Image.Load<Rgba32>(compositedPath);

        if (!skipImmediate)
        {
            var immediateError = TestRenderHelper.CompareImages(immediate!, expected);
            if (immediateError > AllowedError)
            {
                Assert.Fail(immediatePath + ": Error = " + immediateError);
            }
        }

        if (!skipCompositor)
        {
            var compositedError = TestRenderHelper.CompareImages(composited!, expected);
            if (compositedError > AllowedError)
            {
                Assert.Fail(compositedPath + ": Error = " + compositedError);
            }
        }
    }

    protected void CompareImagesNoRenderer([CallerMemberName] string testName = "", string? expectedName = null)
    {
        var expectedPath = Path.Combine(OutputPath, (expectedName ?? testName) + ".expected.png");
        var actualPath = Path.Combine(OutputPath, testName + ".out.png");
        TestRenderHelper.AssertCompareImages(actualPath, expectedPath);
    }

    public void Dispose()
    {
        TestRenderHelper.EndTest();
    }
}
