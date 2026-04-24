using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SkiaNative.Avalonia;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class DirectPathBindingBenchmarks
{
    private NativePathCommand[][] _nativePathCommands = [];
    private SkiaNativePath[] _cachedPaths = [];
    private Color[] _colors = [];
    private double[] _widths = [];

    [Params(128, 2048)]
    public int PathCount { get; set; }

    [Params(16)]
    public int SegmentsPerPath { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        NativeLibraryResolver.Configure(new SkiaNativeOptions
        {
            NativeLibraryPath = FindNativeLibrary()
        });

        _nativePathCommands = new NativePathCommand[PathCount][];
        _cachedPaths = new SkiaNativePath[PathCount];
        _colors = new Color[PathCount];
        _widths = new double[PathCount];

        var random = new Random(12345);
        for (var i = 0; i < PathCount; i++)
        {
            var commands = CreatePathCommands(random, SegmentsPerPath, i);
            _nativePathCommands[i] = commands;
            _cachedPaths[i] = SkiaNativePath.Create(MemoryMarshal.Cast<NativePathCommand, SkiaNativePathCommand>(commands));
            _colors[i] = Color.FromRgb((byte)random.Next(32, 240), (byte)random.Next(32, 240), (byte)random.Next(32, 240));
            _widths[i] = 1 + random.NextDouble() * 8;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var path in _cachedPaths)
        {
            path.Dispose();
        }
    }

    [Benchmark(Baseline = true)]
    public int EncodeStrokePaths_CreateNativePathPerDraw()
    {
        using var commandBuffer = new CommandBuffer(PathCount);
        for (var i = 0; i < _nativePathCommands.Length; i++)
        {
            commandBuffer.StrokeSolidPath(
                _nativePathCommands[i],
                _colors[i],
                _widths[i],
                NativeStrokeCap.Round,
                NativeStrokeJoin.Round,
                10);
        }

        return commandBuffer.CommandCount;
    }

    [Benchmark]
    public int EncodeStrokePaths_ReusedNativePathHandles()
    {
        using var commandBuffer = new CommandBuffer(PathCount);
        for (var i = 0; i < _cachedPaths.Length; i++)
        {
            commandBuffer.StrokeNativePath(
                _cachedPaths[i].NativeHandle,
                _colors[i],
                _widths[i],
                NativeStrokeCap.Round,
                NativeStrokeJoin.Round,
                10);
        }

        return commandBuffer.CommandCount;
    }

    [Benchmark]
    public int CreateAndDisposeNativePathResources()
    {
        var created = 0;
        for (var i = 0; i < _nativePathCommands.Length; i++)
        {
            using var path = SkiaNativePath.Create(MemoryMarshal.Cast<NativePathCommand, SkiaNativePathCommand>(_nativePathCommands[i]));
            if (!path.IsDisposed)
            {
                created++;
            }
        }

        return created;
    }

    private static NativePathCommand[] CreatePathCommands(Random random, int segmentCount, int pathIndex)
    {
        var commands = new NativePathCommand[segmentCount + 1];
        var x = (float)(pathIndex % 64) * 3;
        var y = (float)(pathIndex / 64) * 3;
        commands[0] = new NativePathCommand
        {
            Kind = NativePathCommandKind.MoveTo,
            X0 = x,
            Y0 = y
        };

        for (var i = 1; i < commands.Length; i++)
        {
            x += random.NextSingle() * 8 - 4;
            y += random.NextSingle() * 8 - 4;
            commands[i] = (i % 3) switch
            {
                0 => new NativePathCommand
                {
                    Kind = NativePathCommandKind.QuadTo,
                    X0 = x + 2,
                    Y0 = y - 2,
                    X1 = x,
                    Y1 = y
                },
                1 => new NativePathCommand
                {
                    Kind = NativePathCommandKind.CubicTo,
                    X0 = x - 2,
                    Y0 = y + 2,
                    X1 = x + 3,
                    Y1 = y - 3,
                    X2 = x,
                    Y2 = y
                },
                _ => new NativePathCommand
                {
                    Kind = NativePathCommandKind.LineTo,
                    X0 = x,
                    Y0 = y
                }
            };
        }

        return commands;
    }

    private static string FindNativeLibrary()
    {
        var explicitPath = Environment.GetEnvironmentVariable("SKIANATIVE_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64",
            Architecture.X64 => "osx-x64",
            _ => throw new PlatformNotSupportedException($"Unsupported benchmark architecture: {RuntimeInformation.ProcessArchitecture}.")
        };

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "native", rid, "libSkiaNativeAvalonia.dylib");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not find libSkiaNativeAvalonia.dylib. Build native assets or set SKIANATIVE_NATIVE_LIBRARY.",
            "libSkiaNativeAvalonia.dylib");
    }
}
