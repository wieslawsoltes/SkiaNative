using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SkiaNative.Avalonia.Imaging;

internal unsafe class NativeWriteableBitmap : IWriteableBitmapImpl
{
    private byte[] _pixels;
    private bool _disposed;

    public NativeWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
    {
        PixelSize = size;
        Dpi = dpi;
        Format = format;
        AlphaFormat = alphaFormat;
        var bytesPerPixel = format == PixelFormats.Rgb565 ? 2 : 4;
        RowBytes = Math.Max(1, size.Width) * bytesPerPixel;
        _pixels = new byte[Math.Max(1, RowBytes * Math.Max(1, size.Height))];
        NativeBitmap = NativeMethods.BitmapCreateRaster(PixelSize.Width, PixelSize.Height, Dpi.X, Dpi.Y);
        UploadPixels();
        Version = 1;
    }

    private NativeWriteableBitmap(NativeBitmapHandle nativeBitmap, PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
    {
        NativeBitmap = nativeBitmap;
        PixelSize = size;
        Dpi = dpi;
        Format = format;
        AlphaFormat = alphaFormat;
        RowBytes = Math.Max(1, size.Width) * Math.Max(1, format.BitsPerPixel / 8);
        _pixels = Array.Empty<byte>();
        Version = 1;
    }

    public Vector Dpi { get; }
    public PixelSize PixelSize { get; }
    public int Version { get; protected set; }
    public PixelFormat? Format { get; }
    public AlphaFormat? AlphaFormat { get; }
    protected int RowBytes { get; }
    internal NativeBitmapHandle NativeBitmap { get; }
    internal int PixelRowBytes => RowBytes;

    public static NativeWriteableBitmap FromStream(
        Stream stream,
        int? width = null,
        int? height = null,
        BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();
        if (data.Length > 0)
        {
            fixed (byte* dataPtr = data)
            {
                var handle = NativeMethods.BitmapCreateFromEncoded(dataPtr, data.Length, SkiaNativePlatform.DefaultDpi.X, SkiaNativePlatform.DefaultDpi.Y);
                if (!handle.IsInvalid)
                {
                    var decodedSize = new PixelSize(NativeMethods.BitmapGetWidth(handle), NativeMethods.BitmapGetHeight(handle));
                    var bitmap = new NativeWriteableBitmap(handle, decodedSize, SkiaNativePlatform.DefaultDpi, PixelFormats.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul);
                    var requestedSize = CalculateDecodeSize(decodedSize, width, height);
                    if (requestedSize is null || requestedSize.Value == decodedSize)
                    {
                        return bitmap;
                    }

                    try
                    {
                        return Resize(bitmap, requestedSize.Value, interpolationMode: interpolationMode);
                    }
                    finally
                    {
                        bitmap.Dispose();
                    }
                }

                handle.Dispose();
            }
        }

        var fallbackSize = new PixelSize(Math.Max(width ?? 1, 1), Math.Max(height ?? 1, 1));
        return new NativeWriteableBitmap(fallbackSize, SkiaNativePlatform.DefaultDpi, PixelFormats.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul);
    }

    public static NativeWriteableBitmap FromPixels(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride)
    {
        var bitmap = new NativeWriteableBitmap(size, dpi, format, alphaFormat);
        var bytes = Math.Min(bitmap._pixels.Length, Math.Max(0, stride * size.Height));
        if (bytes > 0)
        {
            Marshal.Copy(data, bitmap._pixels, 0, bytes);
            bitmap.UploadPixels();
        }

        return bitmap;
    }

    public static NativeWriteableBitmap Resize(
        IBitmapImpl source,
        PixelSize destinationSize,
        SkiaNativeOptions? options = null,
        BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
    {
        ArgumentNullException.ThrowIfNull(source);

        destinationSize = new PixelSize(Math.Max(1, destinationSize.Width), Math.Max(1, destinationSize.Height));
        if (source is not NativeWriteableBitmap)
        {
            return new NativeWriteableBitmap(destinationSize, source.Dpi, PixelFormats.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul);
        }

        var target = new NativeRenderTargetBitmap(destinationSize, source.Dpi, options ?? new SkiaNativeOptions());
        using var context = target.CreateDrawingContext();
        context.Clear(Colors.Transparent);
        context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolationMode });
        try
        {
            context.DrawBitmap(
                source,
                opacity: 1,
                sourceRect: new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height),
                destRect: new Rect(0, 0, destinationSize.Width, destinationSize.Height));
        }
        finally
        {
            context.PopRenderOptions();
        }

        return target;
    }

    private static PixelSize? CalculateDecodeSize(PixelSize sourceSize, int? width, int? height)
    {
        if (width is null && height is null)
        {
            return null;
        }

        var destinationWidth = width.GetValueOrDefault();
        var destinationHeight = height.GetValueOrDefault();
        if (destinationWidth <= 0 && destinationHeight <= 0)
        {
            return null;
        }

        if (destinationWidth <= 0)
        {
            destinationWidth = Math.Max(1, (int)Math.Round((double)sourceSize.Width * destinationHeight / sourceSize.Height));
        }

        if (destinationHeight <= 0)
        {
            destinationHeight = Math.Max(1, (int)Math.Round((double)sourceSize.Height * destinationWidth / sourceSize.Width));
        }

        return new PixelSize(Math.Max(1, destinationWidth), Math.Max(1, destinationHeight));
    }

    public ILockedFramebuffer Lock()
    {
        EnsureManagedPixels();
        ReadPixels();
        var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        return new LockedFramebuffer(
            handle.AddrOfPinnedObject(),
            PixelSize,
            RowBytes,
            Dpi,
            Format ?? PixelFormats.Bgra8888,
            AlphaFormat ?? global::Avalonia.Platform.AlphaFormat.Premul,
            () =>
            {
                Version++;
                UploadPixels();
                handle.Free();
            });
    }

    public void Save(string fileName, int? quality = null)
    {
        using var stream = File.Create(fileName);
        Save(stream, quality);
    }

    public void Save(Stream stream, int? quality = null)
    {
        using var encoded = NativeMethods.BitmapEncode(NativeBitmap, NativeEncodedImageFormat.Png, quality ?? -1);
        if (encoded.IsInvalid)
        {
            throw new InvalidOperationException("SkiaNative failed to encode bitmap as PNG.");
        }

        var bytes = NativeMethods.DataGetBytes(encoded);
        var length = checked((int)NativeMethods.DataGetSize(encoded));
        if (bytes == null || length <= 0)
        {
            throw new InvalidOperationException("SkiaNative produced an empty PNG payload.");
        }

        stream.Write(new ReadOnlySpan<byte>(bytes, length));
    }

    internal byte[] CopyPixelBytes()
    {
        EnsureManagedPixels();
        ReadPixels();
        return (byte[])_pixels.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pixels = Array.Empty<byte>();
        NativeBitmap.Dispose();
    }

    private void EnsureManagedPixels()
    {
        if (_pixels.Length == 0)
        {
            _pixels = new byte[Math.Max(1, RowBytes * Math.Max(1, PixelSize.Height))];
        }
    }

    private void UploadPixels()
    {
        if (_pixels.Length == 0 || NativeBitmap.IsInvalid)
        {
            return;
        }

        fixed (byte* pixels = _pixels)
        {
            _ = NativeMethods.BitmapUploadPixels(
                NativeBitmap,
                pixels,
                RowBytes,
                ToNativePixelFormat(Format ?? PixelFormats.Bgra8888),
                ToNativeAlphaFormat(AlphaFormat ?? global::Avalonia.Platform.AlphaFormat.Premul));
        }
    }

    private void ReadPixels()
    {
        if (_pixels.Length == 0 || NativeBitmap.IsInvalid)
        {
            return;
        }

        fixed (byte* pixels = _pixels)
        {
            _ = NativeMethods.BitmapReadPixels(
                NativeBitmap,
                pixels,
                RowBytes,
                ToNativePixelFormat(Format ?? PixelFormats.Bgra8888),
                ToNativeAlphaFormat(AlphaFormat ?? global::Avalonia.Platform.AlphaFormat.Premul));
        }
    }

    internal static NativePixelFormat ToNativePixelFormat(PixelFormat format)
    {
        if (format == PixelFormats.Rgba8888)
        {
            return NativePixelFormat.Rgba8888;
        }

        if (format == PixelFormats.Rgb565)
        {
            return NativePixelFormat.Rgb565;
        }

        return NativePixelFormat.Bgra8888;
    }

    internal static NativeAlphaFormat ToNativeAlphaFormat(AlphaFormat alphaFormat)
    {
        return alphaFormat switch
        {
            global::Avalonia.Platform.AlphaFormat.Opaque => NativeAlphaFormat.Opaque,
            global::Avalonia.Platform.AlphaFormat.Unpremul => NativeAlphaFormat.Unpremul,
            _ => NativeAlphaFormat.Premul
        };
    }
}
