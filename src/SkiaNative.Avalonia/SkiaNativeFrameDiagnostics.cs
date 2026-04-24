namespace SkiaNative.Avalonia;

public readonly record struct SkiaNativeFrameDiagnostics(
    long FrameId,
    int CommandCount,
    int NativeTransitionCount,
    TimeSpan FlushElapsed,
    int NativeResult,
    int GpuResourceCount,
    ulong GpuResourceBytes,
    ulong GpuPurgeableBytes,
    ulong GpuResourceLimit);

public static class SkiaNativeDiagnostics
{
    private static long s_frameId;

    public static event Action<SkiaNativeFrameDiagnostics>? FrameRendered;

    internal static void Publish(SkiaNativeOptions options, CommandBufferFlushResult result, NativeResourceCacheUsage resourceCacheUsage)
    {
        if (!options.EnableDiagnostics && options.DiagnosticsCallback is null && FrameRendered is null)
        {
            return;
        }

        var diagnostics = new SkiaNativeFrameDiagnostics(
            Interlocked.Increment(ref s_frameId),
            result.CommandCount,
            result.NativeTransitionCount,
            result.FlushElapsed,
            result.NativeResult,
            resourceCacheUsage.ResourceCount,
            resourceCacheUsage.ResourceBytes,
            resourceCacheUsage.PurgeableBytes,
            resourceCacheUsage.ResourceLimit);

        options.DiagnosticsCallback?.Invoke(diagnostics);
        FrameRendered?.Invoke(diagnostics);
    }
}

internal readonly record struct NativeResourceCacheUsage(
    int ResourceCount,
    ulong ResourceBytes,
    ulong PurgeableBytes,
    ulong ResourceLimit);
