using System.Buffers;

namespace SkiaNative.Avalonia;

internal sealed class PooledReferenceList<T> : IDisposable
    where T : class
{
    private T[] _items;
    private int _count;

    public PooledReferenceList(int capacity = 4)
    {
        _items = ArrayPool<T>.Shared.Rent(Math.Max(capacity, 4));
    }

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count);
            return _items[index];
        }
    }

    public void Add(T item)
    {
        if (_count == _items.Length)
        {
            Grow();
        }

        _items[_count++] = item;
    }

    public void Clear()
    {
        if (_count == 0)
        {
            return;
        }

        Array.Clear(_items, 0, _count);
        _count = 0;
    }

    public void Dispose()
    {
        if (_items.Length == 0)
        {
            return;
        }

        Clear();
        ArrayPool<T>.Shared.Return(_items);
        _items = [];
    }

    private void Grow()
    {
        var next = ArrayPool<T>.Shared.Rent(_items.Length * 2);
        Array.Copy(_items, next, _count);
        Array.Clear(_items, 0, _count);
        ArrayPool<T>.Shared.Return(_items);
        _items = next;
    }
}
