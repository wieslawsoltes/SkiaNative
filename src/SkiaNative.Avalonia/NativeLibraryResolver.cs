using System.Reflection;
using System.Runtime.InteropServices;

namespace SkiaNative.Avalonia;

internal static class NativeLibraryResolver
{
    private static int s_configured;
    private static string? s_nativeLibraryPath;

    public static void Configure(SkiaNativeOptions options)
    {
        s_nativeLibraryPath = options.NativeLibraryPath;

        if (Interlocked.Exchange(ref s_configured, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName)
        {
            return IntPtr.Zero;
        }

        if (!string.IsNullOrWhiteSpace(s_nativeLibraryPath)
            && NativeLibrary.TryLoad(s_nativeLibraryPath, assembly, searchPath, out var configured))
        {
            return configured;
        }

        if (NativeLibrary.TryLoad("libSkiaNativeAvalonia", assembly, searchPath, out var unix))
        {
            return unix;
        }

        return NativeLibrary.TryLoad("SkiaNativeAvalonia", assembly, searchPath, out var plain)
            ? plain
            : IntPtr.Zero;
    }
}
