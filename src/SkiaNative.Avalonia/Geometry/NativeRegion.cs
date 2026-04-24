using Avalonia.Platform;

namespace SkiaNative.Avalonia.Geometry;

internal sealed class NativeRegion : IPlatformRenderInterfaceRegion
{
    private readonly List<LtrbPixelRect> _rects = new();

    public bool IsEmpty => _rects.Count == 0;
    public IList<LtrbPixelRect> Rects => _rects;

    public LtrbPixelRect Bounds
    {
        get
        {
            if (_rects.Count == 0)
            {
                return default;
            }

            var left = _rects.Min(static r => r.Left);
            var top = _rects.Min(static r => r.Top);
            var right = _rects.Max(static r => r.Right);
            var bottom = _rects.Max(static r => r.Bottom);
            return new LtrbPixelRect { Left = left, Top = top, Right = right, Bottom = bottom };
        }
    }

    public void AddRect(LtrbPixelRect rect) => _rects.Add(rect);
    public void Reset() => _rects.Clear();
    public bool Intersects(LtrbRect rect) => _rects.Any(r => rect.Left < r.Right && r.Left < rect.Right && rect.Top < r.Bottom && r.Top < rect.Bottom);
    public bool Contains(Point pt) => _rects.Any(r => pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom);
    public void Dispose() => _rects.Clear();
}
