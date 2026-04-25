using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.VisualTree;
using MotionMark.SkiaNative.AvaloniaApp.Rendering;
using ISkiaNativeApiLeaseFeature = global::SkiaNative.Avalonia.ISkiaNativeApiLeaseFeature;
using SkiaNativeDirectCanvas = global::SkiaNative.Avalonia.SkiaNativeDirectCanvas;
using SkiaNativeDiagnostics = global::SkiaNative.Avalonia.SkiaNativeDiagnostics;
using SkiaNativeFrameDiagnostics = global::SkiaNative.Avalonia.SkiaNativeFrameDiagnostics;
using SkiaNativePathStreamMesh = global::SkiaNative.Avalonia.SkiaNativePathStreamMesh;
using SkiaNativeStrokeCap = global::SkiaNative.Avalonia.SkiaNativeStrokeCap;
using SkiaNativeStrokeJoin = global::SkiaNative.Avalonia.SkiaNativeStrokeJoin;

namespace MotionMark.SkiaNative.AvaloniaApp.Controls;

internal sealed class MotionMarkSurface : Control
{
    public static readonly StyledProperty<int> ComplexityProperty =
        AvaloniaProperty.Register<MotionMarkSurface, int>(
            nameof(Complexity),
            8,
            coerce: static (_, value) => Math.Clamp(value, 0, 24));

    public static readonly StyledProperty<bool> MutateSplitsProperty =
        AvaloniaProperty.Register<MotionMarkSurface, bool>(nameof(MutateSplits));

    public static readonly StyledProperty<bool> UseCachedMeshProperty =
        AvaloniaProperty.Register<MotionMarkSurface, bool>(nameof(UseCachedMesh));

    public static readonly StyledProperty<bool> AnimateMotionProperty =
        AvaloniaProperty.Register<MotionMarkSurface, bool>(nameof(AnimateMotion), true);

    private static readonly Color s_backgroundColor = Color.FromRgb(12, 16, 24);
    private static readonly Color s_gridColor = Color.FromArgb(38, 255, 255, 255);

    private readonly MotionMarkScene _scene = new();
    private readonly object _sceneLock = new();
    private bool _frameRequested;
    private bool _isAttached;
    private bool _mutateSplitsValue;
    private bool _useCachedMeshValue;
    private bool _animateMotionValue = true;
    private TimeSpan? _lastFrameTimestamp;
    private TimeSpan? _animationStartTimestamp;
    private long _animationTicks;
    private double _frameAccumulatorMs;
    private int _statsFrameCount;
    private long _renderTicksAccumulator;
    private long _renderFrameCount;
    private int _lastElementCount;
    private int _lastPathRunCount;
    private int _cachedMeshVersion = -1;
    private SkiaNativePathStreamMesh? _cachedMesh;
    private SkiaNativeFrameDiagnostics _lastNativeFrame;

    public event EventHandler<FrameStats>? FrameStatsUpdated;

    static MotionMarkSurface()
    {
        AffectsRender<MotionMarkSurface>(ComplexityProperty, MutateSplitsProperty);
        AffectsRender<MotionMarkSurface>(UseCachedMeshProperty);
        AffectsRender<MotionMarkSurface>(AnimateMotionProperty);
    }

    public MotionMarkSurface()
    {
        ClipToBounds = true;
    }

    public int Complexity
    {
        get => GetValue(ComplexityProperty);
        set => SetValue(ComplexityProperty, value);
    }

    public bool MutateSplits
    {
        get => GetValue(MutateSplitsProperty);
        set => SetValue(MutateSplitsProperty, value);
    }

    public bool UseCachedMesh
    {
        get => GetValue(UseCachedMeshProperty);
        set => SetValue(UseCachedMeshProperty, value);
    }

    public bool AnimateMotion
    {
        get => GetValue(AnimateMotionProperty);
        set => SetValue(AnimateMotionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ComplexityProperty)
        {
            var complexity = Complexity;
            lock (_sceneLock)
            {
                _scene.SetComplexity(complexity);
            }

            RequestNextFrame();
        }
        else if (change.Property == MutateSplitsProperty)
        {
            Volatile.Write(ref _mutateSplitsValue, MutateSplits);
            RequestNextFrame();
        }
        else if (change.Property == UseCachedMeshProperty)
        {
            var useCachedMesh = UseCachedMesh;
            Volatile.Write(ref _useCachedMeshValue, useCachedMesh);
            if (!useCachedMesh)
            {
                lock (_sceneLock)
                {
                    DisposeCachedMesh();
                }
            }

            RequestNextFrame();
        }
        else if (change.Property == AnimateMotionProperty)
        {
            Volatile.Write(ref _animateMotionValue, AnimateMotion);
            RequestNextFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Volatile.Write(ref _mutateSplitsValue, MutateSplits);
        Volatile.Write(ref _useCachedMeshValue, UseCachedMesh);
        Volatile.Write(ref _animateMotionValue, AnimateMotion);
        Volatile.Write(ref _animationTicks, 0);
        lock (_sceneLock)
        {
            _scene.SetComplexity(Complexity);
        }

        SkiaNativeDiagnostics.FrameRendered += OnSkiaNativeFrameRendered;
        RequestNextFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SkiaNativeDiagnostics.FrameRendered -= OnSkiaNativeFrameRendered;
        _isAttached = false;
        _frameRequested = false;
        _lastFrameTimestamp = null;
        _animationStartTimestamp = null;
        Volatile.Write(ref _animationTicks, 0);
        _frameAccumulatorMs = 0;
        _statsFrameCount = 0;
        lock (_sceneLock)
        {
            DisposeCachedMesh();
            _scene.ClearSnapshot();
        }

        Interlocked.Exchange(ref _renderTicksAccumulator, 0);
        Interlocked.Exchange(ref _renderFrameCount, 0);
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

        context.Custom(new MotionMarkDrawOperation(this, bounds));
    }

    private void ReportRenderElapsed(TimeSpan elapsed)
    {
        Interlocked.Add(ref _renderTicksAccumulator, elapsed.Ticks);
        Interlocked.Increment(ref _renderFrameCount);
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

        _animationStartTimestamp ??= timestamp;
        Volatile.Write(ref _animationTicks, (timestamp - _animationStartTimestamp.Value).Ticks);

        if (_lastFrameTimestamp is TimeSpan last)
        {
            var deltaMs = (timestamp - last).TotalMilliseconds;
            if (deltaMs > 0 && deltaMs < 250)
            {
                _frameAccumulatorMs += deltaMs;
                _statsFrameCount++;

                const double statsWindowMs = 500.0;
                if (_frameAccumulatorMs >= statsWindowMs && _statsFrameCount > 0)
                {
                    var averageFrameMs = _frameAccumulatorMs / _statsFrameCount;
                    var renderTicks = Interlocked.Exchange(ref _renderTicksAccumulator, 0);
                    var renderFrames = Interlocked.Exchange(ref _renderFrameCount, 0);
                    var averageRenderMs = renderFrames > 0
                        ? TimeSpan.FromTicks(renderTicks / renderFrames).TotalMilliseconds
                        : 0;
                    var fps = averageFrameMs > 0 ? 1000.0 / averageFrameMs : 0;
                    FrameStatsUpdated?.Invoke(
                        this,
                        new FrameStats(
                            Complexity,
                            _lastElementCount,
                            _lastPathRunCount,
                            averageFrameMs,
                            averageRenderMs,
                            fps,
                            _lastNativeFrame.CommandCount,
                            _lastNativeFrame.NativeTransitionCount,
                            _lastNativeFrame.GpuResourceBytes,
                            _lastNativeFrame.FlushElapsed.TotalMilliseconds,
                            _lastNativeFrame.SessionEndElapsed.TotalMilliseconds,
                            _lastNativeFrame.PlatformPresentElapsed.TotalMilliseconds));

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

    private void DrawMotionMarkScene(SkiaNativeDirectCanvas canvas, Rect bounds)
    {
        var mutateSplits = Volatile.Read(ref _mutateSplitsValue);
        var useCachedMesh = Volatile.Read(ref _useCachedMeshValue);
        lock (_sceneLock)
        {
            var renderData = _scene.GetRenderData(bounds.Size, mutateSplits);
            _lastElementCount = renderData.ElementCount;
            _lastPathRunCount = renderData.PathRunCount;

            var elements = renderData.Elements.AsSpan(0, renderData.ElementCount);
            if (mutateSplits || !useCachedMesh)
            {
                canvas.StrokePathStream(
                    elements,
                    1,
                    SkiaNativeStrokeCap.Round,
                    SkiaNativeStrokeJoin.Round,
                    antiAlias: false);
                return;
            }

            if (_cachedMesh is null || _cachedMeshVersion != renderData.Version)
            {
                DisposeCachedMesh();
                _cachedMesh = SkiaNativePathStreamMesh.Create(elements);
                _cachedMeshVersion = renderData.Version;
            }

            canvas.DrawPathStreamMesh(_cachedMesh);
        }
    }

    private void DrawAnimatedMotionMarkScene(SkiaNativeDirectCanvas canvas, Rect bounds)
    {
        if (!Volatile.Read(ref _animateMotionValue))
        {
            DrawMotionMarkScene(canvas, bounds);
            return;
        }

        var animationSeconds = TimeSpan.FromTicks(Volatile.Read(ref _animationTicks)).TotalSeconds;
        var transform = CreateSmoothMotionTransform(bounds, animationSeconds);
        canvas.Save();
        canvas.ConcatTransform(transform);
        DrawMotionMarkScene(canvas, bounds);
        canvas.Restore();
    }

    private static Matrix CreateSmoothMotionTransform(Rect bounds, double seconds)
    {
        var centerX = bounds.X + bounds.Width * 0.5;
        var centerY = bounds.Y + bounds.Height * 0.5;
        var amplitude = Math.Min(bounds.Width, bounds.Height);
        var translateX = Math.Sin(seconds * 0.90) * amplitude * 0.015;
        var translateY = Math.Cos(seconds * 1.10) * amplitude * 0.012;
        var scale = 1.0 + Math.Sin(seconds * 0.70) * 0.012;
        var angle = Math.Sin(seconds * 0.55) * 0.018;

        return Matrix.CreateTranslation(-centerX, -centerY) *
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateRotation(angle) *
            Matrix.CreateTranslation(centerX + translateX, centerY + translateY);
    }

    private void DisposeCachedMesh()
    {
        _cachedMesh?.Dispose();
        _cachedMesh = null;
        _cachedMeshVersion = -1;
    }

    private sealed class MotionMarkDrawOperation : ICustomDrawOperation
    {
        private readonly MotionMarkSurface _owner;
        private readonly Rect _bounds;

        public MotionMarkDrawOperation(MotionMarkSurface owner, Rect bounds)
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
                _owner.DrawAnimatedMotionMarkScene(canvas, _bounds);

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
            var spacing = Math.Max(20, Math.Min(bounds.Width / 12, bounds.Height / 8));
            for (var x = bounds.Left; x <= bounds.Right; x += spacing)
            {
                canvas.DrawLine(s_gridColor, 1, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
            }

            for (var y = bounds.Top; y <= bounds.Bottom; y += spacing)
            {
                canvas.DrawLine(s_gridColor, 1, new Point(bounds.Left, y), new Point(bounds.Right, y));
            }
        }
    }
}
