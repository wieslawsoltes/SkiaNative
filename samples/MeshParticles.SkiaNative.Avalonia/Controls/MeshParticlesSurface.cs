using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.VisualTree;
using ISkiaNativeApiLeaseFeature = global::SkiaNative.Avalonia.ISkiaNativeApiLeaseFeature;
using SkiaNativeDiagnostics = global::SkiaNative.Avalonia.SkiaNativeDiagnostics;
using SkiaNativeDirectCanvas = global::SkiaNative.Avalonia.SkiaNativeDirectCanvas;
using SkiaNativeFrameDiagnostics = global::SkiaNative.Avalonia.SkiaNativeFrameDiagnostics;
using SkiaNativeMesh = global::SkiaNative.Avalonia.SkiaNativeMesh;
using SkiaNativeMeshAttribute = global::SkiaNative.Avalonia.SkiaNativeMeshAttribute;
using SkiaNativeMeshAttributeType = global::SkiaNative.Avalonia.SkiaNativeMeshAttributeType;
using SkiaNativeMeshMode = global::SkiaNative.Avalonia.SkiaNativeMeshMode;
using SkiaNativeMeshSpecification = global::SkiaNative.Avalonia.SkiaNativeMeshSpecification;
using SkiaNativeMeshUniformInfo = global::SkiaNative.Avalonia.SkiaNativeMeshUniformInfo;
using SkiaNativeMeshVarying = global::SkiaNative.Avalonia.SkiaNativeMeshVarying;
using SkiaNativeMeshVaryingType = global::SkiaNative.Avalonia.SkiaNativeMeshVaryingType;

namespace MeshParticles.SkiaNative.AvaloniaApp.Controls;

internal sealed class MeshParticlesSurface : Control
{
    private static readonly Color s_backgroundColor = Color.FromRgb(5, 9, 16);
    private static readonly Color s_gridColor = Color.FromArgb(34, 126, 164, 210);
    private static readonly Color s_axisColor = Color.FromArgb(72, 95, 228, 255);

    private readonly MeshParticleScene _scene = new();
    private readonly object _sceneLock = new();
    private bool _isAttached;
    private bool _frameRequested;
    private TimeSpan? _lastFrameTimestamp;
    private double _elapsedSeconds;
    private double _frameAccumulatorMs;
    private int _statsFrameCount;
    private long _renderTicksAccumulator;
    private long _renderFrameCount;
    private SkiaNativeFrameDiagnostics _lastNativeFrame;

    public event EventHandler<MeshParticleStats>? FrameStatsUpdated;

    public MeshParticlesSurface()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        SkiaNativeDiagnostics.FrameRendered += OnSkiaNativeFrameRendered;
        RequestNextFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SkiaNativeDiagnostics.FrameRendered -= OnSkiaNativeFrameRendered;
        _isAttached = false;
        _frameRequested = false;
        _lastFrameTimestamp = null;
        lock (_sceneLock)
        {
            _scene.Dispose();
        }

        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        context.Custom(new MeshParticlesDrawOperation(this, bounds));
    }

    private void RequestNextFrame()
    {
        if (!_isAttached || _frameRequested)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        _frameRequested = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        _frameRequested = false;
        if (!_isAttached)
        {
            return;
        }

        if (_lastFrameTimestamp is TimeSpan last)
        {
            var deltaMs = (timestamp - last).TotalMilliseconds;
            if (deltaMs > 0 && deltaMs < 250)
            {
                _elapsedSeconds += deltaMs / 1000.0;
                _frameAccumulatorMs += deltaMs;
                _statsFrameCount++;

                const double statsWindowMs = 500.0;
                if (_frameAccumulatorMs >= statsWindowMs && _statsFrameCount > 0)
                {
                    var renderTicks = Interlocked.Exchange(ref _renderTicksAccumulator, 0);
                    var renderFrames = Interlocked.Exchange(ref _renderFrameCount, 0);
                    var averageFrameMs = _frameAccumulatorMs / _statsFrameCount;
                    var averageRenderMs = renderFrames > 0
                        ? TimeSpan.FromTicks(renderTicks / renderFrames).TotalMilliseconds
                        : 0;
                    var fps = averageFrameMs > 0 ? 1000.0 / averageFrameMs : 0;
                    var snapshot = _scene.Snapshot;
                    FrameStatsUpdated?.Invoke(
                        this,
                        snapshot with
                        {
                            FrameMs = averageRenderMs > 0 ? averageRenderMs : averageFrameMs,
                            Fps = fps,
                            NativeTransitions = _lastNativeFrame.NativeTransitionCount,
                            GpuResourceBytes = _lastNativeFrame.GpuResourceBytes
                        });

                    _frameAccumulatorMs = 0;
                    _statsFrameCount = 0;
                }
            }
        }

        _lastFrameTimestamp = timestamp;
        InvalidateVisual();
        RequestNextFrame();
    }

    private void OnSkiaNativeFrameRendered(SkiaNativeFrameDiagnostics diagnostics)
    {
        _lastNativeFrame = diagnostics;
    }

    private void RenderScene(SkiaNativeDirectCanvas canvas, Rect bounds)
    {
        lock (_sceneLock)
        {
            _scene.Draw(canvas, bounds, _elapsedSeconds);
        }
    }

    private void ReportRenderElapsed(TimeSpan elapsed)
    {
        Interlocked.Add(ref _renderTicksAccumulator, elapsed.Ticks);
        Interlocked.Increment(ref _renderFrameCount);
    }

    private sealed class MeshParticlesDrawOperation : ICustomDrawOperation
    {
        private readonly MeshParticlesSurface _owner;
        private readonly Rect _bounds;

        public MeshParticlesDrawOperation(MeshParticlesSurface owner, Rect bounds)
        {
            _owner = owner;
            _bounds = bounds;
        }

        public Rect Bounds => _bounds;

        public void Dispose()
        {
        }

        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            if (context.TryGetFeature<ISkiaNativeApiLeaseFeature>(out var directFeature))
            {
                using var lease = directFeature.Lease();
                var canvas = lease.Canvas;
                canvas.Save();
                canvas.PushClip(_bounds);
                canvas.FillRectangle(s_backgroundColor, _bounds);
                DrawGrid(canvas, _bounds);
                _owner.RenderScene(canvas, _bounds);
                canvas.Restore();
            }
            else
            {
                using (context.PushClip(_bounds))
                {
                    context.FillRectangle(Brushes.Black, _bounds);
                }
            }

            stopwatch.Stop();
            _owner.ReportRenderElapsed(stopwatch.Elapsed);
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        private static void DrawGrid(SkiaNativeDirectCanvas canvas, Rect bounds)
        {
            var spacing = Math.Max(40, Math.Min(bounds.Width / 14, bounds.Height / 10));
            for (var x = bounds.Left; x <= bounds.Right; x += spacing)
            {
                canvas.DrawLine(s_gridColor, 1, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            }

            for (var y = bounds.Top; y <= bounds.Bottom; y += spacing)
            {
                canvas.DrawLine(s_gridColor, 1, new Point(bounds.Left, y), new Point(bounds.Right, y));
            }

            var center = bounds.Center;
            canvas.DrawLine(s_axisColor, 1.5, new Point(center.X, bounds.Top), new Point(center.X, bounds.Bottom));
            canvas.DrawLine(s_axisColor, 1.5, new Point(bounds.Left, center.Y), new Point(bounds.Right, center.Y));
        }
    }
}

internal sealed class MeshParticleScene : IDisposable
{
    private const int ParticleCount = 2048;
    private const int VerticesPerParticle = 4;
    private const int IndicesPerParticle = 6;
    private const int VertexStride = 32;

    private static readonly SkiaNativeMeshAttribute[] s_attributes =
    [
        new(SkiaNativeMeshAttributeType.Float2, 0, "position"),
        new(SkiaNativeMeshAttributeType.Float2, 8, "local"),
        new(SkiaNativeMeshAttributeType.Float, 16, "hue"),
        new(SkiaNativeMeshAttributeType.Float, 20, "alpha"),
        new(SkiaNativeMeshAttributeType.Float, 24, "size"),
        new(SkiaNativeMeshAttributeType.Float, 28, "phase")
    ];

    private static readonly SkiaNativeMeshVarying[] s_varyings =
    [
        new(SkiaNativeMeshVaryingType.Float2, "position"),
        new(SkiaNativeMeshVaryingType.Float2, "local"),
        new(SkiaNativeMeshVaryingType.Float, "hue"),
        new(SkiaNativeMeshVaryingType.Float, "alpha"),
        new(SkiaNativeMeshVaryingType.Float, "phase")
    ];

    private const string VertexShader = """
Varyings main(const Attributes a) {
    Varyings v;
    v.position = a.position;
    v.local = a.local;
    v.hue = a.hue;
    v.alpha = a.alpha;
    v.phase = a.phase;
    return v;
}
""";

    private const string FragmentShader = """
uniform float u_time;

float3 hsv2rgb(float h) {
    float3 p = abs(fract(h + float3(0.0, 0.6666667, 0.3333333)) * 6.0 - 3.0);
    return clamp(p - 1.0, 0.0, 1.0);
}

float2 main(const Varyings v, out half4 color) {
    float r = length(v.local);
    float edge = 1.0 - smoothstep(0.78, 1.0, r);
    float core = exp(-r * r * 5.0);
    float ring = 0.5 + 0.5 * sin(24.0 * (1.0 - r) + v.phase + u_time * 4.0);
    float pulse = 0.72 + 0.28 * sin(u_time * 2.7 + v.phase);
    float3 rgb = hsv2rgb(fract(v.hue + 0.05 * sin(u_time + v.phase)));
    float intensity = core * 1.35 + ring * 0.28 + pulse * 0.18;
    float alpha = clamp(v.alpha * edge, 0.0, 1.0);
    color = half4(half3(rgb * intensity * alpha), half(alpha));
    return v.position;
}
""";

    private readonly ParticleSeed[] _seeds = new ParticleSeed[ParticleCount];
    private readonly ParticleVertex[] _vertices = new ParticleVertex[ParticleCount * VerticesPerParticle];
    private readonly ushort[] _indices = new ushort[ParticleCount * IndicesPerParticle];
    private SkiaNativeMeshSpecification? _specification;
    private SkiaNativeMesh? _mesh;
    private byte[] _uniforms = [];
    private SkiaNativeMeshUniformInfo _timeUniform;
    private bool _hasTimeUniform;
    private MeshParticleStats _snapshot = new(ParticleCount, 0, 0, 0, 0, 0, 0, "initializing", null);

    public MeshParticleScene()
    {
        var random = new Random(729_451);
        for (var i = 0; i < _seeds.Length; i++)
        {
            _seeds[i] = new ParticleSeed(
                Radius: 0.06f + (float)random.NextDouble() * 0.90f,
                Angle: (float)random.NextDouble() * MathF.Tau,
                Speed: 0.14f + (float)random.NextDouble() * 1.25f,
                Size: 2.5f + (float)Math.Pow(random.NextDouble(), 2.2) * 18.0f,
                Hue: (float)random.NextDouble(),
                Alpha: 0.32f + (float)random.NextDouble() * 0.62f,
                Phase: (float)random.NextDouble() * MathF.Tau);
        }

        for (var i = 0; i < ParticleCount; i++)
        {
            var vertex = i * VerticesPerParticle;
            var index = i * IndicesPerParticle;
            _indices[index + 0] = checked((ushort)(vertex + 0));
            _indices[index + 1] = checked((ushort)(vertex + 1));
            _indices[index + 2] = checked((ushort)(vertex + 2));
            _indices[index + 3] = checked((ushort)(vertex + 0));
            _indices[index + 4] = checked((ushort)(vertex + 2));
            _indices[index + 5] = checked((ushort)(vertex + 3));
        }
    }

    public MeshParticleStats Snapshot => _snapshot;

    public void Draw(SkiaNativeDirectCanvas canvas, Rect bounds, double timeSeconds)
    {
        try
        {
            EnsureResources(canvas, bounds, timeSeconds);
            if (_mesh is null)
            {
                return;
            }

            UpdateVertices(bounds, timeSeconds);
            UpdateUniforms(timeSeconds);
            _mesh.UpdateVertices(_vertices);
            if (_uniforms.Length > 0)
            {
                _mesh.UpdateUniforms(_uniforms);
            }

            _mesh.SetBounds(bounds);
            canvas.DrawMesh(_mesh, Colors.White, antiAlias: true);
            _snapshot = new MeshParticleStats(
                ParticleCount,
                _vertices.Length,
                _uniforms.Length,
                0,
                0,
                0,
                0,
                "GPU-backed SkMesh drawMesh",
                null);
        }
        catch (Exception ex)
        {
            _snapshot = _snapshot with { Error = ex.Message };
        }
    }

    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        _specification?.Dispose();
        _specification = null;
    }

    private void EnsureResources(SkiaNativeDirectCanvas canvas, Rect bounds, double timeSeconds)
    {
        if (_specification is null)
        {
            _specification = SkiaNativeMeshSpecification.Create(s_attributes, VertexStride, s_varyings, VertexShader, FragmentShader);
            _uniforms = _specification.UniformSize > 0 ? new byte[_specification.UniformSize] : [];
            _hasTimeUniform = _specification.TryGetUniform("u_time", out _timeUniform);
        }

        if (_mesh is null)
        {
            UpdateVertices(bounds, timeSeconds);
            UpdateUniforms(timeSeconds);
            _mesh = canvas.CreateMesh(
                _specification,
                SkiaNativeMeshMode.Triangles,
                _vertices,
                _indices,
                _uniforms,
                bounds);
        }
    }

    private void UpdateUniforms(double timeSeconds)
    {
        if (!_hasTimeUniform || _uniforms.Length == 0 || _timeUniform.Size < sizeof(float))
        {
            return;
        }

        var time = (float)timeSeconds;
        MemoryMarshal.Write(_uniforms.AsSpan(_timeUniform.Offset, sizeof(float)), in time);
    }

    private void UpdateVertices(Rect bounds, double timeSeconds)
    {
        var width = Math.Max(1f, (float)bounds.Width);
        var height = Math.Max(1f, (float)bounds.Height);
        var min = Math.Min(width, height);
        var centerX = width * 0.5f;
        var centerY = height * 0.5f;
        var time = (float)timeSeconds;

        for (var i = 0; i < ParticleCount; i++)
        {
            var seed = _seeds[i];
            var angle = seed.Angle + time * seed.Speed;
            var orbit = seed.Radius * min * 0.47f;
            var wobble = MathF.Sin(seed.Phase + time * 1.35f) * min * 0.045f;
            var curl = MathF.Cos(seed.Phase * 0.7f + time * 0.9f) * min * 0.035f;
            var x = centerX + MathF.Cos(angle) * (orbit + wobble) + MathF.Cos(angle * 2.0f + seed.Phase) * curl;
            var y = centerY + MathF.Sin(angle * 0.83f + seed.Phase * 0.13f) * (orbit * 0.72f + curl);
            var size = seed.Size * (0.74f + 0.26f * MathF.Sin(time * 2.2f + seed.Phase));
            var hue = seed.Hue + time * 0.015f;
            var alpha = seed.Alpha * (0.78f + 0.22f * MathF.Sin(time * 1.7f + seed.Phase));
            var phase = seed.Phase + time * seed.Speed;
            var vertex = i * VerticesPerParticle;

            _vertices[vertex + 0] = new ParticleVertex(x - size, y - size, -1, -1, hue, alpha, size, phase);
            _vertices[vertex + 1] = new ParticleVertex(x + size, y - size, 1, -1, hue, alpha, size, phase);
            _vertices[vertex + 2] = new ParticleVertex(x + size, y + size, 1, 1, hue, alpha, size, phase);
            _vertices[vertex + 3] = new ParticleVertex(x - size, y + size, -1, 1, hue, alpha, size, phase);
        }
    }

    private readonly record struct ParticleSeed(float Radius, float Angle, float Speed, float Size, float Hue, float Alpha, float Phase);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct ParticleVertex(
        float X,
        float Y,
        float LocalX,
        float LocalY,
        float Hue,
        float Alpha,
        float Size,
        float Phase);
}

internal readonly record struct MeshParticleStats(
    int ParticleCount,
    int VertexCount,
    int UniformBytes,
    double FrameMs,
    double Fps,
    int NativeTransitions,
    ulong GpuResourceBytes,
    string Mode,
    string? Error);
