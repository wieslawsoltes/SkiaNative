using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Platform;

namespace SkiaNative.Avalonia.Text;

internal sealed class NativeFontManager : IFontManagerImpl
{
    private const string ArialFont = "/System/Library/Fonts/Supplemental/Arial.ttf";
    private const string HelveticaFont = "/System/Library/Fonts/Supplemental/Helvetica.ttf";
    private const string GenevaFont = "/System/Library/Fonts/Geneva.ttf";
    private const string MonoFont = "/System/Library/Fonts/SFNSMono.ttf";
    private const string SystemFont = "/System/Library/Fonts/SFNS.ttf";

    private static readonly string[] CandidateFonts =
    [
        ArialFont,
        HelveticaFont,
        GenevaFont,
        MonoFont,
        "/System/Library/Fonts/SFCompact.ttf",
        SystemFont
    ];

    public string GetDefaultFontFamilyName() => "System Font";

    public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false) =>
        ["System Font", "SF Pro", "Arial", "Helvetica", "Menlo"];

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch, string? familyName, CultureInfo? culture, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
    {
        return TryCreateGlyphTypeface(familyName ?? GetDefaultFontFamilyName(), fontStyle, fontWeight, fontStretch, out platformTypeface);
    }

    public bool TryCreateGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
    {
        var path = ResolveFontPath(familyName);
        if (path is null)
        {
            platformTypeface = null;
            return false;
        }

        platformTypeface = new NativeTypeface(path, familyName, style, weight, stretch, FontSimulations.None);
        return true;
    }

    public bool TryCreateGlyphTypeface(Stream stream, FontSimulations fontSimulations, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "SkiaNative.Avalonia", Guid.NewGuid().ToString("N") + ".ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        using (var file = File.Create(tempPath))
        {
            stream.CopyTo(file);
        }

        platformTypeface = new NativeTypeface(tempPath, Path.GetFileNameWithoutExtension(tempPath), FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, fontSimulations, deleteOnDispose: true);
        return true;
    }

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        familyTypefaces = [new Typeface(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)];
        return true;
    }

    private static string? ResolveFontPath(string familyName)
    {
        if (familyName.Contains("mono", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("menlo", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(MonoFont))
            {
                return MonoFont;
            }
        }

        return CandidateFonts.FirstOrDefault(File.Exists);
    }
}
