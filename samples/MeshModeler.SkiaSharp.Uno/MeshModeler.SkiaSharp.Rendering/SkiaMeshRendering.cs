using System.Runtime.InteropServices;
using MeshModeler.Core;
using SkiaSharp;

namespace MeshModeler.SkiaSharp.Rendering;

public static class SkiaMeshVertexLayout
{
    public const int MeshVertexStride = 14 * sizeof(float);
    public const int SplatVertexStride = 9 * sizeof(float);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct SkiaMeshVertex(
    float X,
    float Y,
    float Z,
    float U,
    float V,
    float Nx,
    float Ny,
    float Nz,
    float MaterialR,
    float MaterialG,
    float MaterialB,
    float BaryX,
    float BaryY,
    float BaryZ);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct SkiaProjectedMeshVertex(
    float X,
    float Y,
    float U,
    float V,
    float Nx,
    float Ny,
    float Nz,
    float Depth,
    float MaterialR,
    float MaterialG,
    float MaterialB,
    float BaryX,
    float BaryY,
    float BaryZ);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct SkiaSplatVertex(
    float X,
    float Y,
    float LocalX,
    float LocalY,
    float ColorR,
    float ColorG,
    float ColorB,
    float Alpha,
    float Depth);

public sealed class SkiaSogImageDecoder : ISogImageDecoder
{
    public static SkiaSogImageDecoder Shared { get; } = new();

    private SkiaSogImageDecoder()
    {
    }

    public SogImage DecodeRgba8888(ReadOnlySpan<byte> encoded, string name)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException($"SOG image '{name}' could not be decoded by Skia.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
        {
            throw new InvalidDataException($"SOG image '{name}' decode failed with result {result}.");
        }

        var pixels = new byte[checked(info.Width * info.Height * 4)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new SogImage(info.Width, info.Height, pixels);
    }
}

public sealed class SkiaMeshMaterialResourceCache : IDisposable
{
    private readonly Dictionary<MeshMaterial, SkiaMeshMaterialResources> _resources = new(ReferenceEqualityComparer.Instance);

    public SkiaMeshMaterialResources Get(MeshMaterial material)
    {
        if (!_resources.TryGetValue(material, out var resources))
        {
            resources = new SkiaMeshMaterialResources(material);
            _resources.Add(material, resources);
        }

        return resources;
    }

    public void Clear()
    {
        foreach (var resource in _resources.Values)
        {
            resource.Dispose();
        }

        _resources.Clear();
    }

    public void Dispose() => Clear();
}

public sealed class SkiaMeshMaterialResources : IDisposable
{
    private readonly MeshMaterial _material;
    private SKImage? _diffuseImage;
    private SKShader? _diffuseShader;
    private SKRuntimeEffectChild[]? _children;
    private bool _textureLoadAttempted;

    public SkiaMeshMaterialResources(MeshMaterial material)
    {
        _material = material;
    }

    public bool HasDiffuseTexture
    {
        get
        {
            EnsureTextureLoaded();
            return _diffuseImage is { Width: > 0, Height: > 0 };
        }
    }

    public int TextureWidth => HasDiffuseTexture ? _diffuseImage!.Width : 1;
    public int TextureHeight => HasDiffuseTexture ? _diffuseImage!.Height : 1;

    public ReadOnlySpan<SKRuntimeEffectChild> Children
    {
        get
        {
            EnsureShader();
            return _children!;
        }
    }

    public void Dispose()
    {
        _diffuseShader?.Dispose();
        _diffuseImage?.Dispose();
        _diffuseShader = null;
        _diffuseImage = null;
        _children = null;
    }

    private void EnsureTextureLoaded()
    {
        if (_textureLoadAttempted)
        {
            return;
        }

        _textureLoadAttempted = true;
        var path = _material.DiffuseTexturePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        using var encoded = SKData.Create(path);
        var image = encoded is null ? null : SKImage.FromEncodedData(encoded);
        if (image is null || image.Width <= 0 || image.Height <= 0)
        {
            image?.Dispose();
            return;
        }

        _diffuseImage = image;
    }

    private void EnsureShader()
    {
        if (_children is not null)
        {
            return;
        }

        if (HasDiffuseTexture)
        {
            _diffuseShader ??= _diffuseImage!.ToShader(
                SKShaderTileMode.Repeat,
                SKShaderTileMode.Repeat,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        else
        {
            _diffuseShader ??= SKShader.CreateColor(ToColor(_material.Diffuse, _material.Alpha));
        }

        _children = new[] { new SKRuntimeEffectChild(_diffuseShader!) };
    }

    private static SKColor ToColor(Vec3 color, float alpha)
        => new(ToByte(color.X), ToByte(color.Y), ToByte(color.Z), ToByte(alpha));

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
}
