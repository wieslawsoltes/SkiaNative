namespace SkiaNative.Avalonia;

public enum SkiaNativeGpuSubmitMode
{
    FlushAndSubmit = 0,
    FlushOnly = 1,
}

public sealed class SkiaNativeOptions
{
    public const long DefaultMaxGpuResourceBytes = 32L * 1024 * 1024;

    public string? NativeLibraryPath { get; set; }
    public bool EnableDiagnostics { get; set; }
    public Action<SkiaNativeFrameDiagnostics>? DiagnosticsCallback { get; set; }
    public bool EnableCpuFallback { get; set; } = true;
    public int InitialCommandBufferCapacity { get; set; } = 512;

    /// <summary>
    /// Maximum native Skia GPU resource cache size. Set to <c>null</c> to leave Skia's default cache limit.
    /// </summary>
    public long? MaxGpuResourceBytes { get; set; } = DefaultMaxGpuResourceBytes;

    /// <summary>
    /// Controls how the native Metal backend ends a GPU-backed frame.
    /// <see cref="SkiaNativeGpuSubmitMode.FlushAndSubmit"/> is the safe default for wrapped platform textures.
    /// <see cref="SkiaNativeGpuSubmitMode.FlushOnly"/> is useful when the host platform performs submission itself.
    /// </summary>
    public SkiaNativeGpuSubmitMode GpuSubmitMode { get; set; } = SkiaNativeGpuSubmitMode.FlushAndSubmit;

    /// <summary>
    /// Forces Skia to submit GPU work and purge unlocked resources after each frame.
    /// This lowers memory during validation or simple apps, but can reduce throughput.
    /// </summary>
    public bool PurgeGpuResourcesAfterFrame { get; set; }
}
