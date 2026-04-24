using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Platform;

namespace SkiaNative.Avalonia.Geometry;

internal enum NativeGeometryKind
{
    Empty,
    Rectangle,
    Ellipse,
    Line,
    Stream,
    Group,
    Combined,
    Transformed
}

internal class NativeGeometry : IGeometryImpl
{
    public NativeGeometry(NativeGeometryKind kind, Rect bounds)
    {
        Kind = kind;
        Bounds = bounds;
    }

    public NativeGeometryKind Kind { get; }
    public Rect Bounds { get; protected set; }
    public virtual double ContourLength => TryGetStrokePath(out var path) ? NativePathCommands.GetContourLength(path) : 0;

    public static NativeGeometry Rectangle(Rect rect) => NativePathGeometry.CreateRect(rect);
    public static NativeGeometry Ellipse(Rect rect) => NativePathGeometry.CreateEllipse(rect);
    public static NativeGeometry Line(Point p1, Point p2) => NativePathGeometry.CreateLine(p1, p2);
    public static NativeGeometry Group(FillRule fillRule, IReadOnlyList<IGeometryImpl> children) => NativePathGeometry.CreateGroup(fillRule, children);
    public static NativeGeometry Combined(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2) => NativePathGeometry.CreateCombined(combineMode, g1, g2);

    public virtual Rect GetRenderBounds(IPen? pen)
    {
        return GetPathRenderBounds(pen);
    }

    public virtual IGeometryImpl GetWidenedGeometry(IPen pen)
    {
        if (TryGetStrokePath(out var path) && NativePathCommands.CreateStroked(path, pen) is { IsInvalid: false } widened)
        {
            return new NativePathGeometry(NativeGeometryKind.Stream, NativePathGeometry.ResolveBounds(GetRenderBounds(pen), widened, widened), widened, widened);
        }

        return new NativePathGeometry(NativeGeometryKind.Empty, default, null, null);
    }

    public virtual bool FillContains(Point point) => TryGetFillPath(out var path) ? NativePathCommands.Contains(path, point) : Bounds.Contains(point);
    public virtual IGeometryImpl? Intersect(IGeometryImpl geometry) => Bounds.Intersects(geometry.Bounds) ? Combined(GeometryCombineMode.Intersect, this, geometry) : null;
    public virtual bool StrokeContains(IPen? pen, Point point)
    {
        if (pen is null || !TryGetStrokePath(out var path))
        {
            return false;
        }

        using var stroked = NativePathCommands.CreateStroked(path, pen);
        return stroked is { IsInvalid: false } && NativePathCommands.Contains(stroked, point);
    }

    public virtual ITransformedGeometryImpl WithTransform(Matrix transform) => new NativeTransformedGeometry(this, transform);

    public virtual bool TryGetPointAtDistance(double distance, out Point point)
    {
        if (TryGetStrokePath(out var path))
        {
            return NativePathCommands.TryGetPointAtDistance(path, distance, out point);
        }

        point = default;
        return false;
    }

    public virtual bool TryGetPointAndTangentAtDistance(double distance, out Point point, out Point tangent)
    {
        if (TryGetStrokePath(out var path))
        {
            return NativePathCommands.TryGetPointAndTangentAtDistance(path, distance, out point, out tangent);
        }

        point = default;
        tangent = default;
        return false;
    }

    public virtual bool TryGetSegment(double startDistance, double stopDistance, bool startOnBeginFigure, [NotNullWhen(true)] out IGeometryImpl? segmentGeometry)
    {
        segmentGeometry = null;
        if (!TryGetStrokePath(out var path))
        {
            return false;
        }

        var segment = NativePathCommands.CreateSegment(path, startDistance, stopDistance, startOnBeginFigure);
        if (segment is null || segment.IsInvalid)
        {
            segment?.Dispose();
            return false;
        }

        segmentGeometry = new NativePathGeometry(NativeGeometryKind.Stream, NativePathGeometry.ResolveBounds(default, null, segment), null, segment);
        return true;
    }

    public virtual bool TryGetFillPath([NotNullWhen(true)] out NativePathHandle? path) { path = null; return false; }
    public virtual bool TryGetStrokePath([NotNullWhen(true)] out NativePathHandle? path) { path = null; return false; }

    protected Rect GetPathRenderBounds(IPen? pen)
    {
        var hasBounds = false;
        var bounds = default(Rect);

        if (TryGetFillPath(out var fillPath) && NativePathCommands.TryGetBounds(fillPath, out var fillBounds))
        {
            bounds = fillBounds;
            hasBounds = true;
        }

        if (TryGetStrokePath(out var strokePath))
        {
            Rect? strokeBounds = null;
            if (pen is not null)
            {
                using var widened = NativePathCommands.CreateStroked(strokePath, pen);
                if (widened is { IsInvalid: false } && NativePathCommands.TryGetBounds(widened, out var widenedBounds))
                {
                    strokeBounds = widenedBounds;
                }
            }

            if (strokeBounds is null && NativePathCommands.TryGetBounds(strokePath, out var rawStrokeBounds))
            {
                strokeBounds = rawStrokeBounds;
            }

            if (strokeBounds is { } resolvedStrokeBounds)
            {
                bounds = hasBounds ? bounds.Union(resolvedStrokeBounds) : resolvedStrokeBounds;
                hasBounds = true;
            }
        }

        if (hasBounds)
        {
            return bounds;
        }

        var thickness = pen?.Thickness ?? 0;
        return thickness <= 0 ? Bounds : Bounds.Inflate(thickness / 2);
    }

    protected static Rect Union(IEnumerable<IGeometryImpl> children)
    {
        var hasBounds = false;
        var bounds = default(Rect);
        foreach (var child in children)
        {
            bounds = hasBounds ? bounds.Union(child.Bounds) : child.Bounds;
            hasBounds = true;
        }

        return bounds;
    }

    protected static bool HasSamePath(NativePathHandle? first, NativePathHandle? second) =>
        ReferenceEquals(first, second) ||
        first is not null &&
        second is not null &&
        !first.IsInvalid &&
        !second.IsInvalid &&
        first.DangerousGetHandle() == second.DangerousGetHandle();
}

internal sealed class NativePathGeometry : NativeGeometry
{
    private readonly NativePathHandle? _fillPath;
    private readonly NativePathHandle? _strokePath;

    public NativePathGeometry(NativeGeometryKind kind, Rect bounds, NativePathHandle? fillPath, NativePathHandle? strokePath)
        : base(kind, bounds)
    {
        _fillPath = fillPath;
        _strokePath = strokePath;
    }

    public static NativePathGeometry CreateLine(Point p1, Point p2)
    {
        var commands = new[]
        {
            NativePathCommands.MoveTo(p1),
            NativePathCommands.LineTo(p2)
        };

        var stroke = NativePathCommands.Create(commands, FillRule.NonZero);
        return new NativePathGeometry(NativeGeometryKind.Line, ResolveBounds(new Rect(p1, p2).Normalize(), null, stroke), null, stroke);
    }

    public static NativePathGeometry CreateRect(Rect rect)
    {
        rect = rect.Normalize();
        var path = NativePathCommands.CreateRect(rect, FillRule.NonZero);
        return new NativePathGeometry(NativeGeometryKind.Rectangle, ResolveBounds(rect, path, path), path, path);
    }

    public static NativePathGeometry CreateEllipse(Rect rect)
    {
        rect = rect.Normalize();
        var path = NativePathCommands.CreateEllipse(rect, FillRule.NonZero);
        return new NativePathGeometry(NativeGeometryKind.Ellipse, ResolveBounds(rect, path, path), path, path);
    }

    public static NativePathGeometry CreateGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children)
    {
        var strokePaths = new List<NativePathHandle>(children.Count);
        var fillPaths = new List<NativePathHandle>(children.Count);
        var requiresFillPass = false;

        foreach (var child in children)
        {
            if (child is not NativeGeometry native)
            {
                requiresFillPass = true;
                continue;
            }

            native.TryGetStrokePath(out var strokePath);
            native.TryGetFillPath(out var fillPath);

            if (strokePath is { IsInvalid: false })
            {
                strokePaths.Add(strokePath);
            }

            if (fillPath is { IsInvalid: false })
            {
                fillPaths.Add(fillPath);
            }

            if (!HasSamePath(strokePath, fillPath))
            {
                requiresFillPass = true;
            }
        }

        var stroke = NativePathCommands.CreateGroup(strokePaths, fillRule);
        var fill = requiresFillPass ? NativePathCommands.CreateGroup(fillPaths, fillRule) : stroke;
        return new NativePathGeometry(NativeGeometryKind.Group, ResolveBounds(Union(children), fill, stroke), fill, stroke);
    }

    public static NativePathGeometry CreateCombined(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2)
    {
        NativePathHandle? stroke = null;
        NativePathHandle? fill = null;

        if (g1 is NativeGeometry native1 && g2 is NativeGeometry native2)
        {
            native1.TryGetStrokePath(out var stroke1);
            native2.TryGetStrokePath(out var stroke2);
            native1.TryGetFillPath(out var fill1);
            native2.TryGetFillPath(out var fill2);

            if (stroke1 is { IsInvalid: false } && stroke2 is { IsInvalid: false })
            {
                stroke = NativePathCommands.CreateCombined(stroke1, stroke2, combineMode, FillRule.NonZero);
            }

            if (fill1 is { IsInvalid: false } && fill2 is { IsInvalid: false })
            {
                fill = HasSamePath(fill1, stroke1) && HasSamePath(fill2, stroke2)
                    ? stroke
                    : NativePathCommands.CreateCombined(fill1, fill2, combineMode, FillRule.NonZero);
            }
        }

        var fallbackBounds = combineMode == GeometryCombineMode.Intersect && g1.Bounds.Intersects(g2.Bounds)
            ? g1.Bounds.Intersect(g2.Bounds)
            : g1.Bounds.Union(g2.Bounds);

        return new NativePathGeometry(NativeGeometryKind.Combined, ResolveBounds(fallbackBounds, fill, stroke), fill, stroke);
    }

    public static NativePathGeometry FromGlyphRun(NativeGlyphRunHandle glyphRun, Rect bounds)
    {
        var path = NativeMethods.PathCreateFromGlyphRun(glyphRun);
        if (path.IsInvalid)
        {
            path.Dispose();
            return new NativePathGeometry(NativeGeometryKind.Empty, bounds, null, null);
        }

        return new NativePathGeometry(NativeGeometryKind.Stream, ResolveBounds(bounds, path, path), path, path);
    }

    public override Rect GetRenderBounds(IPen? pen)
    {
        return GetPathRenderBounds(pen);
    }

    public override bool FillContains(Point point) =>
        _fillPath is { IsInvalid: false } && NativePathCommands.Contains(_fillPath, point);

    public override bool TryGetFillPath([NotNullWhen(true)] out NativePathHandle? path)
    {
        path = _fillPath;
        return path is { IsInvalid: false };
    }

    public override bool TryGetStrokePath([NotNullWhen(true)] out NativePathHandle? path)
    {
        path = _strokePath;
        return path is { IsInvalid: false };
    }

    internal static Rect ResolveBounds(Rect fallback, NativePathHandle? fillPath, NativePathHandle? strokePath)
    {
        var hasBounds = false;
        var bounds = default(Rect);

        if (fillPath is { IsInvalid: false } && NativePathCommands.TryGetBounds(fillPath, out var fillBounds))
        {
            bounds = fillBounds;
            hasBounds = true;
        }

        if (strokePath is { IsInvalid: false } && NativePathCommands.TryGetBounds(strokePath, out var strokeBounds))
        {
            bounds = hasBounds ? bounds.Union(strokeBounds) : strokeBounds;
            hasBounds = true;
        }

        return hasBounds ? bounds : fallback;
    }
}

internal static unsafe class NativePathCommands
{
    public const uint LargeArc = 1u;
    public const uint Clockwise = 1u << 1;

    public static NativePathCommand MoveTo(Point point) => new()
    {
        Kind = NativePathCommandKind.MoveTo,
        X0 = (float)point.X,
        Y0 = (float)point.Y
    };

    public static NativePathCommand LineTo(Point point) => new()
    {
        Kind = NativePathCommandKind.LineTo,
        X0 = (float)point.X,
        Y0 = (float)point.Y
    };

    public static NativePathCommand QuadTo(Point control, Point end) => new()
    {
        Kind = NativePathCommandKind.QuadTo,
        X0 = (float)control.X,
        Y0 = (float)control.Y,
        X1 = (float)end.X,
        Y1 = (float)end.Y
    };

    public static NativePathCommand CubicTo(Point control1, Point control2, Point end) => new()
    {
        Kind = NativePathCommandKind.CubicTo,
        X0 = (float)control1.X,
        Y0 = (float)control1.Y,
        X1 = (float)control2.X,
        Y1 = (float)control2.Y,
        X2 = (float)end.X,
        Y2 = (float)end.Y
    };

    public static NativePathCommand ArcTo(Point end, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection) => new()
    {
        Kind = NativePathCommandKind.ArcTo,
        Flags = (isLargeArc ? LargeArc : 0u) | (sweepDirection == SweepDirection.Clockwise ? Clockwise : 0u),
        X0 = (float)size.Width,
        Y0 = (float)size.Height,
        X1 = (float)rotationAngle,
        X2 = (float)end.X,
        Y2 = (float)end.Y
    };

    public static NativePathCommand Close() => new()
    {
        Kind = NativePathCommandKind.Close
    };

    public static NativePathHandle Create(IReadOnlyList<NativePathCommand> commands, FillRule fillRule)
    {
        var fill = ToNativeFillRule(fillRule);
        if (commands.Count == 0)
        {
            return NativeMethods.PathCreate(null, 0, fill);
        }

        var array = commands as NativePathCommand[] ?? commands.ToArray();
        fixed (NativePathCommand* ptr = array)
        {
            return NativeMethods.PathCreate(ptr, array.Length, fill);
        }
    }

    public static NativePathHandle CreateRect(Rect rect, FillRule fillRule)
    {
        rect = rect.Normalize();
        return NativeMethods.PathCreateRect((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, ToNativeFillRule(fillRule));
    }

    public static NativePathHandle CreateEllipse(Rect rect, FillRule fillRule)
    {
        rect = rect.Normalize();
        return NativeMethods.PathCreateEllipse((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, ToNativeFillRule(fillRule));
    }

    public static NativePathHandle CreateGroup(IReadOnlyList<NativePathHandle> paths, FillRule fillRule)
    {
        if (paths.Count == 0)
        {
            return NativeMethods.PathCreateGroup(null, 0, ToNativeFillRule(fillRule));
        }

        var handles = new nint[paths.Count];
        for (var i = 0; i < paths.Count; ++i)
        {
            handles[i] = paths[i].DangerousGetHandle();
        }

        fixed (nint* ptr = handles)
        {
            return NativeMethods.PathCreateGroup(ptr, handles.Length, ToNativeFillRule(fillRule));
        }
    }

    public static NativePathHandle? CreateCombined(NativePathHandle first, NativePathHandle second, GeometryCombineMode combineMode, FillRule fillRule)
    {
        var result = NativeMethods.PathCreateCombined(first, second, ToNativePathOp(combineMode), ToNativeFillRule(fillRule));
        if (result.IsInvalid)
        {
            result.Dispose();
            return null;
        }

        return result;
    }

    public static NativePathHandle? CreateTransformed(NativePathHandle source, Matrix transform)
    {
        var nativeTransform = transform.ToNative();
        var result = NativeMethods.PathCreateTransformed(source, &nativeTransform);
        if (result.IsInvalid)
        {
            result.Dispose();
            return null;
        }

        return result;
    }

    public static bool TryGetBounds(NativePathHandle path, out Rect bounds)
    {
        float x;
        float y;
        float width;
        float height;
        if (NativeMethods.PathGetBounds(path, &x, &y, &width, &height) != 0)
        {
            bounds = new Rect(x, y, width, height);
            return true;
        }

        bounds = default;
        return false;
    }

    public static bool Contains(NativePathHandle path, Point point) =>
        NativeMethods.PathContains(path, (float)point.X, (float)point.Y) != 0;

    public static double GetContourLength(NativePathHandle path) =>
        NativeMethods.PathGetContourLength(path);

    public static bool TryGetPointAtDistance(NativePathHandle path, double distance, out Point point)
    {
        float x;
        float y;
        if (NativeMethods.PathGetPointAtDistance(path, (float)distance, &x, &y) != 0)
        {
            point = new Point(x, y);
            return true;
        }

        point = default;
        return false;
    }

    public static bool TryGetPointAndTangentAtDistance(NativePathHandle path, double distance, out Point point, out Point tangent)
    {
        float x;
        float y;
        float tangentX;
        float tangentY;
        if (NativeMethods.PathGetPointAndTangentAtDistance(path, (float)distance, &x, &y, &tangentX, &tangentY) != 0)
        {
            point = new Point(x, y);
            tangent = new Point(tangentX, tangentY);
            return true;
        }

        point = default;
        tangent = default;
        return false;
    }

    public static NativePathHandle? CreateSegment(NativePathHandle path, double startDistance, double stopDistance, bool startWithMoveTo)
    {
        var result = NativeMethods.PathCreateSegment(path, (float)startDistance, (float)stopDistance, startWithMoveTo ? 1 : 0);
        if (result.IsInvalid)
        {
            result.Dispose();
            return null;
        }

        return result;
    }

    public static NativePathHandle? CreateStroked(NativePathHandle path, IPen pen)
    {
        if (pen.Thickness <= 0)
        {
            return null;
        }

        using var stroke = BrushUtil.CreateStrokeStyle(pen);
        if (stroke is null || stroke.IsInvalid)
        {
            return null;
        }

        var result = NativeMethods.PathCreateStroked(path, (float)pen.Thickness, stroke);
        if (result.IsInvalid)
        {
            result.Dispose();
            return null;
        }

        return result;
    }

    private static NativePathFillRule ToNativeFillRule(FillRule fillRule) =>
        fillRule == FillRule.NonZero ? NativePathFillRule.NonZero : NativePathFillRule.EvenOdd;

    private static NativePathOp ToNativePathOp(GeometryCombineMode combineMode) =>
        combineMode switch
        {
            GeometryCombineMode.Intersect => NativePathOp.Intersect,
            GeometryCombineMode.Xor => NativePathOp.Xor,
            GeometryCombineMode.Exclude => NativePathOp.Difference,
            _ => NativePathOp.Union
        };
}

internal sealed class NativeTransformedGeometry : NativeGeometry, ITransformedGeometryImpl
{
    private readonly NativePathHandle? _fillPath;
    private readonly NativePathHandle? _strokePath;

    public NativeTransformedGeometry(IGeometryImpl source, Matrix transform)
        : base(NativeGeometryKind.Transformed, source.Bounds.TransformToAABB(transform))
    {
        SourceGeometry = source;
        Transform = transform;

        if (source is not NativeGeometry native)
        {
            return;
        }

        native.TryGetFillPath(out var sourceFillPath);
        native.TryGetStrokePath(out var sourceStrokePath);

        if (sourceStrokePath is { IsInvalid: false })
        {
            _strokePath = NativePathCommands.CreateTransformed(sourceStrokePath, transform);
        }

        if (sourceFillPath is { IsInvalid: false })
        {
            _fillPath = HasSamePath(sourceFillPath, sourceStrokePath)
                ? _strokePath
                : NativePathCommands.CreateTransformed(sourceFillPath, transform);
        }

        Bounds = NativePathGeometry.ResolveBounds(Bounds, _fillPath, _strokePath);
    }

    public IGeometryImpl SourceGeometry { get; }
    public Matrix Transform { get; }

    public override Rect GetRenderBounds(IPen? pen)
    {
        return GetPathRenderBounds(pen);
    }

    public override bool FillContains(Point point) =>
        _fillPath is { IsInvalid: false } && NativePathCommands.Contains(_fillPath, point);

    public override bool TryGetFillPath([NotNullWhen(true)] out NativePathHandle? path)
    {
        path = _fillPath;
        return path is { IsInvalid: false };
    }

    public override bool TryGetStrokePath([NotNullWhen(true)] out NativePathHandle? path)
    {
        path = _strokePath;
        return path is { IsInvalid: false };
    }
}
