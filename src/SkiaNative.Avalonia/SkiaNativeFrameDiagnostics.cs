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
    ulong GpuResourceLimit,
    TimeSpan SessionEndElapsed,
    TimeSpan PlatformPresentElapsed,
    TimeSpan GpuCleanupElapsed,
    TimeSpan DiagnosticsElapsed);

public static class SkiaNativeDiagnostics
{
    private static long s_frameId;

    public static event Action<SkiaNativeFrameDiagnostics>? FrameRendered;

    internal static bool ShouldPublish(SkiaNativeOptions options) =>
        options.EnableDiagnostics || options.DiagnosticsCallback is not null || FrameRendered is not null;

    internal static void Publish(
        SkiaNativeOptions options,
        CommandBufferFlushResult result,
        NativeResourceCacheUsage resourceCacheUsage,
        NativeFrameTiming timing)
    {
        if (!ShouldPublish(options))
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
            resourceCacheUsage.ResourceLimit,
            timing.SessionEndElapsed,
            timing.PlatformPresentElapsed,
            timing.GpuCleanupElapsed,
            timing.DiagnosticsElapsed);

        options.DiagnosticsCallback?.Invoke(diagnostics);
        FrameRendered?.Invoke(diagnostics);
    }
}

internal readonly record struct NativeResourceCacheUsage(
    int ResourceCount,
    ulong ResourceBytes,
    ulong PurgeableBytes,
    ulong ResourceLimit);

internal readonly record struct NativeFrameTiming(
    TimeSpan SessionEndElapsed,
    TimeSpan PlatformPresentElapsed,
    TimeSpan GpuCleanupElapsed,
    TimeSpan DiagnosticsElapsed);
