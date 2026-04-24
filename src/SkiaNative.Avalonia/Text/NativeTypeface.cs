using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace SkiaNative.Avalonia.Text;

internal sealed unsafe class NativeTypeface : IPlatformTypeface
{
    private readonly bool _deleteOnDispose;
    private readonly Dictionary<OpenTypeTag, byte[]?> _fontTables = new();
    private NativeTypefaceHandle? _nativeHandle;

    public NativeTypeface(string path, string familyName, FontStyle style, FontWeight weight, FontStretch stretch, FontSimulations fontSimulations, bool deleteOnDispose = false)
    {
        Path = path;
        FamilyName = familyName;
        Style = style;
        Weight = weight;
        Stretch = stretch;
        FontSimulations = fontSimulations;
        _deleteOnDispose = deleteOnDispose;
    }

    public string Path { get; }
    public string FamilyName { get; }
    public FontWeight Weight { get; }
    public FontStyle Style { get; }
    public FontStretch Stretch { get; }
    public FontSimulations FontSimulations { get; }
    internal NativeTypefaceHandle NativeHandle => _nativeHandle ??= CreateNativeHandle();

    public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
    {
        table = default;
        if (_fontTables.TryGetValue(tag, out var cached))
        {
            table = cached;
            return cached is not null;
        }

        using var stream = File.OpenRead(Path);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != header.Length)
        {
            _fontTables[tag] = null;
            return false;
        }

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2));
        var recordsStart = 12;
        var recordsLength = checked(numTables * 16);
        Span<byte> records = recordsLength <= 1024 ? stackalloc byte[recordsLength] : new byte[recordsLength];
        if (stream.Read(records) != records.Length)
        {
            _fontTables[tag] = null;
            return false;
        }

        for (var i = 0; i < numTables; i++)
        {
            var entry = records.Slice((recordsStart - 12) + i * 16, 16);
            var entryTag = (OpenTypeTag)BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(0, 4));
            if (entryTag != tag)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(12, 4));
            if (offset > (ulong)stream.Length || length > (ulong)stream.Length || (ulong)offset + length > (ulong)stream.Length)
            {
                _fontTables[tag] = null;
                return false;
            }

            var data = new byte[checked((int)length)];
            stream.Position = offset;
            if (stream.Read(data) != data.Length)
            {
                _fontTables[tag] = null;
                return false;
            }

            _fontTables[tag] = data;
            table = data;
            return true;
        }

        _fontTables[tag] = null;
        return false;
    }

    public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
    {
        stream = File.OpenRead(Path);
        return true;
    }

    public void Dispose()
    {
        _fontTables.Clear();
        _nativeHandle?.Dispose();
        _nativeHandle = null;
        if (_deleteOnDispose)
        {
            try { File.Delete(Path); } catch { }
        }
    }

    private NativeTypefaceHandle CreateNativeHandle()
    {
        var bytes = Encoding.UTF8.GetBytes(Path + '\0');
        fixed (byte* path = bytes)
        {
            var handle = NativeMethods.TypefaceCreateFromFile(path);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException($"SkiaNative could not create a native typeface for '{Path}'.");
            }

            return handle;
        }
    }
}
