using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Platform;

namespace SkiaNative.Avalonia.Geometry;

internal sealed class NativeStreamGeometry : NativeGeometry, IStreamGeometryImpl
{
    private readonly List<NativePathCommand> _strokeCommands = new();
    private List<NativePathCommand>? _fillCommands;
    private FillRule _fillRule = FillRule.EvenOdd;
    private NativePathHandle? _fillPath;
    private NativePathHandle? _strokePath;

    public NativeStreamGeometry() : base(NativeGeometryKind.Stream, default)
    {
    }

    private NativeStreamGeometry(IReadOnlyList<NativePathCommand> strokeCommands, IReadOnlyList<NativePathCommand>? fillCommands, FillRule fillRule, Rect bounds)
        : base(NativeGeometryKind.Stream, bounds)
    {
        _strokeCommands.AddRange(strokeCommands);
        _fillCommands = fillCommands is null ? null : new List<NativePathCommand>(fillCommands);
        _fillRule = fillRule;
    }

    public IStreamGeometryImpl Clone() => new NativeStreamGeometry(_strokeCommands, _fillCommands, _fillRule, Bounds);

    public IStreamGeometryContextImpl Open()
    {
        _strokeCommands.Clear();
        _fillCommands = null;
        Bounds = default;
        DisposeNativePaths();
        return new Context(this);
    }

    public override bool TryGetFillPath([NotNullWhen(true)] out NativePathHandle? path)
    {
        var commands = _fillCommands ?? _strokeCommands;
        if (commands.Count == 0)
        {
            path = null;
            return false;
        }

        path = _fillPath ??= NativePathCommands.Create(commands, _fillRule);
        Bounds = NativePathGeometry.ResolveBounds(Bounds, _fillPath, _strokePath);
        return !path.IsInvalid;
    }

    public override bool TryGetStrokePath([NotNullWhen(true)] out NativePathHandle? path)
    {
        if (_strokeCommands.Count == 0)
        {
            path = null;
            return false;
        }

        path = _strokePath ??= NativePathCommands.Create(_strokeCommands, FillRule.NonZero);
        Bounds = NativePathGeometry.ResolveBounds(Bounds, _fillPath, _strokePath);
        return !path.IsInvalid;
    }

    private void AddBounds(Point point)
    {
        Bounds = Bounds == default ? new Rect(point, point) : Bounds.Union(new Rect(point, point));
    }

    private void InvalidateNativePaths()
    {
        DisposeNativePaths();
    }

    private void DisposeNativePaths()
    {
        _fillPath?.Dispose();
        _fillPath = null;
        _strokePath?.Dispose();
        _strokePath = null;
    }

    private sealed class Context : IStreamGeometryContextImpl
    {
        private readonly NativeStreamGeometry _owner;
        private bool _isFilled;
        private bool _isFigureBroken;
        private Point _startPoint;

        public Context(NativeStreamGeometry owner)
        {
            _owner = owner;
        }

        private List<NativePathCommand> Stroke => _owner._strokeCommands;
        private List<NativePathCommand> Fill => _owner._fillCommands ??= new List<NativePathCommand>(Stroke);
        private bool Duplicate => _isFilled && _owner._fillCommands is not null;

        public void SetFillRule(FillRule fillRule)
        {
            _owner._fillRule = fillRule;
            _owner.InvalidateNativePaths();
        }

        public void BeginFigure(Point startPoint, bool isFilled = true)
        {
            if (!isFilled)
            {
                EnsureSeparateFillPath();
            }

            _isFilled = isFilled;
            _startPoint = startPoint;
            _isFigureBroken = false;
            Stroke.Add(NativePathCommands.MoveTo(startPoint));
            if (Duplicate)
            {
                Fill.Add(NativePathCommands.MoveTo(startPoint));
            }

            _owner.AddBounds(startPoint);
        }

        public void LineTo(Point point, bool isStroked = true)
        {
            if (isStroked)
            {
                Stroke.Add(NativePathCommands.LineTo(point));
            }
            else
            {
                BreakFigure();
                Stroke.Add(NativePathCommands.MoveTo(point));
            }

            if (Duplicate)
            {
                Fill.Add(NativePathCommands.LineTo(point));
            }

            _owner.AddBounds(point);
        }

        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection, bool isStroked = true)
        {
            if (isStroked)
            {
                Stroke.Add(NativePathCommands.ArcTo(point, size, rotationAngle, isLargeArc, sweepDirection));
            }
            else
            {
                BreakFigure();
                Stroke.Add(NativePathCommands.MoveTo(point));
            }

            if (Duplicate)
            {
                Fill.Add(NativePathCommands.ArcTo(point, size, rotationAngle, isLargeArc, sweepDirection));
            }

            _owner.AddBounds(point);
        }

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
        {
            if (isStroked)
            {
                Stroke.Add(NativePathCommands.CubicTo(controlPoint1, controlPoint2, endPoint));
            }
            else
            {
                BreakFigure();
                Stroke.Add(NativePathCommands.MoveTo(endPoint));
            }

            if (Duplicate)
            {
                Fill.Add(NativePathCommands.CubicTo(controlPoint1, controlPoint2, endPoint));
            }

            _owner.AddBounds(controlPoint1);
            _owner.AddBounds(controlPoint2);
            _owner.AddBounds(endPoint);
        }

        public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
        {
            if (isStroked)
            {
                Stroke.Add(NativePathCommands.QuadTo(controlPoint, endPoint));
            }
            else
            {
                BreakFigure();
                Stroke.Add(NativePathCommands.MoveTo(endPoint));
            }

            if (Duplicate)
            {
                Fill.Add(NativePathCommands.QuadTo(controlPoint, endPoint));
            }

            _owner.AddBounds(controlPoint);
            _owner.AddBounds(endPoint);
        }

        public void EndFigure(bool isClosed)
        {
            if (!isClosed)
            {
                return;
            }

            if (_isFigureBroken)
            {
                Stroke.Add(NativePathCommands.LineTo(_startPoint));
                _isFigureBroken = false;
            }
            else
            {
                Stroke.Add(NativePathCommands.Close());
            }

            if (Duplicate)
            {
                Fill.Add(NativePathCommands.Close());
            }
        }

        public void Dispose()
        {
            _owner.InvalidateNativePaths();
        }

        private void EnsureSeparateFillPath()
        {
            _ = Fill;
        }

        private void BreakFigure()
        {
            if (_isFigureBroken)
            {
                return;
            }

            _isFigureBroken = true;
            EnsureSeparateFillPath();
        }
    }
}
