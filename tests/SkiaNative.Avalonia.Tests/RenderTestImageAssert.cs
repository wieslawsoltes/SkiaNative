using Avalonia.Platform;
using SkiaNative.Avalonia.Imaging;
using Xunit;

namespace SkiaNative.Avalonia.Tests;

internal static unsafe class RenderTestImageAssert
{
    public static void Similar(
        NativeWriteableBitmap expected,
        NativeWriteableBitmap actual,
        int perChannelTolerance,
        double maxMismatchRatio,
        string artifactName)
    {
        Assert.Equal(expected.PixelSize, actual.PixelSize);

        var expectedPixels = expected.CopyPixelBytes();
        var actualPixels = actual.CopyPixelBytes();
        var width = expected.PixelSize.Width;
        var height = expected.PixelSize.Height;
        var mismatches = 0;
        var maxDelta = 0;

        using var diff = new NativeWriteableBitmap(expected.PixelSize, expected.Dpi, PixelFormats.Bgra8888, AlphaFormat.Premul);
        using (var framebuffer = diff.Lock())
        {
            var diffBase = (byte*)framebuffer.Address;
            for (var y = 0; y < height; y++)
            {
                var diffRow = diffBase + y * framebuffer.RowBytes;
                for (var x = 0; x < width; x++)
                {
                    var expectedOffset = y * expected.PixelRowBytes + x * 4;
                    var actualOffset = y * actual.PixelRowBytes + x * 4;
                    var diffOffset = x * 4;

                    var deltaB = Math.Abs(expectedPixels[expectedOffset] - actualPixels[actualOffset]);
                    var deltaG = Math.Abs(expectedPixels[expectedOffset + 1] - actualPixels[actualOffset + 1]);
                    var deltaR = Math.Abs(expectedPixels[expectedOffset + 2] - actualPixels[actualOffset + 2]);
                    var deltaA = Math.Abs(expectedPixels[expectedOffset + 3] - actualPixels[actualOffset + 3]);
                    var pixelDelta = Math.Max(Math.Max(deltaR, deltaG), Math.Max(deltaB, deltaA));
                    maxDelta = Math.Max(maxDelta, pixelDelta);

                    if (pixelDelta > perChannelTolerance)
                    {
                        mismatches++;
                        diffRow[diffOffset] = 0;
                        diffRow[diffOffset + 1] = 0;
                        diffRow[diffOffset + 2] = (byte)Math.Clamp(pixelDelta, 1, 255);
                        diffRow[diffOffset + 3] = 255;
                    }
                    else
                    {
                        diffRow[diffOffset] = 0;
                        diffRow[diffOffset + 1] = 0;
                        diffRow[diffOffset + 2] = 0;
                        diffRow[diffOffset + 3] = 0;
                    }
                }
            }
        }

        var mismatchRatio = mismatches / (double)(width * height);
        if (mismatchRatio > maxMismatchRatio)
        {
            var artifactDirectory = WriteFailureArtifacts(artifactName, expected, actual, diff);
            Assert.Fail($"Image mismatch ratio {mismatchRatio:P3} exceeded {maxMismatchRatio:P3}; max channel delta={maxDelta}. Artifacts: {artifactDirectory}");
        }
    }

    private static string WriteFailureArtifacts(string artifactName, NativeWriteableBitmap expected, NativeWriteableBitmap actual, NativeWriteableBitmap diff)
    {
        var safeName = string.Concat(artifactName.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, "artifacts", "render-tests", safeName);
        Directory.CreateDirectory(directory);

        expected.Save(Path.Combine(directory, "expected.png"));
        actual.Save(Path.Combine(directory, "actual.png"));
        diff.Save(Path.Combine(directory, "diff.png"));

        return directory;
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SkiaNative.Avalonia.slnx")))
            {
                return dir.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }
}
