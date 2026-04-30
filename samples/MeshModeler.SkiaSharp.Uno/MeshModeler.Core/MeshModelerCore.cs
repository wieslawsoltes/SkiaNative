using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshModeler.Core;

public static class MeshModelerConstants
{
    public static readonly Vec3 DefaultMeshColor = new(0.70f, 0.78f, 0.82f);
}

public readonly record struct Vec2(float X, float Y);

public readonly record struct Corner(int PositionIndex, float U, float V, Vec3 Normal, bool HasNormal)
{
    public Corner(int positionIndex, float u, float v)
        : this(positionIndex, u, v, default, false)
    {
    }
}

public readonly record struct Triangle(Corner A, Corner B, Corner C, Vec3 MaterialColor, int MaterialIndex)
{
    public Triangle(Corner a, Corner b, Corner c)
        : this(a, b, c, MeshModelerConstants.DefaultMeshColor, 0)
    {
    }

    public Triangle(Corner a, Corner b, Corner c, Vec3 materialColor)
        : this(a, b, c, materialColor, 0)
    {
    }
}

public struct Vec3
{
    public float X;
    public float Y;
    public float Z;

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public readonly float LengthSquared => X * X + Y * Y + Z * Z;
    public readonly float Length => MathF.Sqrt(LengthSquared);

    public readonly Vec3 Normalized()
    {
        var length = Length;
        return length <= 0.000001f ? new Vec3(0, 0, 1) : this / length;
    }

    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float b) => new(a.X * b, a.Y * b, a.Z * b);
    public static Vec3 operator /(Vec3 a, float b) => new(a.X / b, a.Y / b, a.Z / b);
}

public sealed class MeshMaterial
{
    public MeshMaterial(string name, Vec3 diffuse)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "default" : name;
        Diffuse = diffuse;
        Ambient = diffuse * 0.35f;
        Specular = new Vec3(0.20f, 0.20f, 0.20f);
        Emission = new Vec3(0, 0, 0);
        Alpha = 1.0f;
        Shininess = 16.0f;
    }

    public string Name { get; }
    public Vec3 Diffuse { get; set; }
    public Vec3 Ambient { get; set; }
    public Vec3 Specular { get; set; }
    public Vec3 Emission { get; set; }
    public float Alpha { get; set; }
    public float Shininess { get; set; }
    public string? DiffuseTexturePath { get; set; }
    public bool HasDiffuseTexture => !string.IsNullOrWhiteSpace(DiffuseTexturePath) && File.Exists(DiffuseTexturePath);
    public bool RequiresDepthSortedRendering => Alpha < 0.999f;
}

public readonly record struct GaussianSplat(
    Vec3 Position,
    Vec3 Axis0,
    Vec3 Axis1,
    Vec3 Axis2,
    float ColorR,
    float ColorG,
    float ColorB,
    float Alpha);

public interface ISogImageDecoder
{
    SogImage DecodeRgba8888(ReadOnlySpan<byte> encoded, string name);
}

public sealed class SogImage : IDisposable
{
    private readonly byte[] _pixels;

    public SogImage(int width, int height, byte[] rgbaPixels)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (rgbaPixels.Length < checked(width * height * 4)) throw new ArgumentException("The pixel buffer is smaller than width * height * 4.", nameof(rgbaPixels));
        Width = width;
        Height = height;
        _pixels = rgbaPixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int PixelCount => Width * Height;
    public int Get(int index, int channel) => _pixels[index * 4 + channel];
    public void Dispose()
    {
    }
}

public sealed class GaussianSplatCloud
{
    private const int SplatReadBufferBytes = 4 * 1024 * 1024;
    private const float SphericalHarmonicsC0 = 0.28209479177387814f;
    private static readonly JsonSerializerOptions SogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private GaussianSplatCloud(string name, GaussianSplat[] splats, string sourceFormat, int sourceSplatCount)
    {
        Name = name;
        SourceFormat = sourceFormat;
        SourceSplatCount = sourceSplatCount;
        NormalizeSplats(splats, out var radius);
        Splats = splats;
        Center = new Vec3(0, 0, 0);
        Radius = radius;
    }

    public string Name { get; }
    public string SourceFormat { get; }
    public int SourceSplatCount { get; }
    public GaussianSplat[] Splats { get; }
    public Vec3 Center { get; }
    public float Radius { get; }

    public static GaussianSplatCloud CreateSample()
    {
        const int count = 3200;
        var random = new Random(2741);
        var splats = new GaussianSplat[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)(count - 1);
            var angle = t * MathF.Tau * 7.0f;
            var radius = 0.25f + t * 2.6f;
            var y = MathF.Sin(t * MathF.Tau * 3.0f) * 0.32f + (NextFloat(random) - 0.5f) * 0.18f;
            var position = new Vec3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius);
            var tangent = new Vec3(-MathF.Sin(angle), 0.12f, MathF.Cos(angle)).Normalized();
            var normal = new Vec3(MathF.Cos(angle), 0.0f, MathF.Sin(angle)).Normalized();
            var up = Vec3.Cross(tangent, normal).Normalized();
            var size = 0.025f + MathF.Pow(NextFloat(random), 2.0f) * 0.075f;
            var axis0 = tangent * (size * 1.8f);
            var axis1 = up * (size * 0.75f);
            var axis2 = normal * (size * 1.15f);
            var hue = t * 0.82f + 0.10f;
            var color = HsvToRgb(hue, 0.72f, 1.0f);
            var alpha = 0.18f + NextFloat(random) * 0.34f;
            splats[i] = new GaussianSplat(position, axis0, axis1, axis2, color.X, color.Y, color.Z, alpha);
        }

        return new GaussianSplatCloud("Procedural Gaussian splat spiral", splats, "procedural", splats.Length);
    }

    public static GaussianSplatCloud Load(string path, string name, ISogImageDecoder? sogImageDecoder = null)
    {
        if (Directory.Exists(path))
        {
            return LoadSog(path, name, RequireSogImageDecoder(sogImageDecoder));
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".ply", StringComparison.OrdinalIgnoreCase))
        {
            return LoadPly(path, name);
        }

        if (extension.Equals(".sog", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            System.IO.Path.GetFileName(path).Equals("meta.json", StringComparison.OrdinalIgnoreCase))
        {
            return LoadSog(path, name, RequireSogImageDecoder(sogImageDecoder));
        }

        throw new NotSupportedException($"Gaussian splat file '{path}' is not supported. Supported formats: PLY, SOG zip, SOG directory, and SOG meta.json.");
    }

    private static ISogImageDecoder RequireSogImageDecoder(ISogImageDecoder? imageDecoder)
        => imageDecoder ?? throw new NotSupportedException("SOG loading requires an ISogImageDecoder implementation; use a rendering-specific decoder such as SkiaSogImageDecoder.");

    public static GaussianSplatCloud LoadPly(string path, string name)
    {
        using var stream = File.OpenRead(path);
        var header = ReadHeader(stream);
        if (header.VertexCount <= 0)
        {
            throw new InvalidOperationException("PLY does not contain a vertex element.");
        }

        var splats = header.Format switch
        {
            "ascii" => ReadAsciiSplats(stream, header),
            "binary_little_endian" => ReadBinarySplats(stream, header),
            _ => throw new NotSupportedException($"PLY format '{header.Format}' is not supported. Supported formats: ascii, binary_little_endian.")
        };

        if (splats.Length == 0)
        {
            throw new InvalidOperationException("PLY did not contain any readable Gaussian splats.");
        }

        return new GaussianSplatCloud(name, splats, header.Format, header.VertexCount);
    }

    public static GaussianSplatCloud LoadSog(string path, string name, ISogImageDecoder imageDecoder)
    {
        if (Directory.Exists(path))
        {
            var root = System.IO.Path.GetFullPath(path);
            var metaPath = System.IO.Path.Combine(root, "meta.json");
            if (!File.Exists(metaPath))
            {
                throw new FileNotFoundException("SOG directory does not contain meta.json.", metaPath);
            }

            return LoadSogFromBytes(
                File.ReadAllBytes(metaPath),
                relativePath => File.ReadAllBytes(System.IO.Path.Combine(root, NormalizeSogPath(relativePath))),
                name,
                "PlayCanvas SOG directory",
                imageDecoder);
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".sog", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var metaBytes = ReadSogArchiveFile(archive, "meta.json");
            return LoadSogFromBytes(
                metaBytes,
                relativePath => ReadSogArchiveFile(archive, relativePath),
                name,
                "PlayCanvas SOG zip",
                imageDecoder);
        }

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        return LoadSogFromBytes(
            File.ReadAllBytes(path),
            relativePath => File.ReadAllBytes(System.IO.Path.Combine(directory, NormalizeSogPath(relativePath))),
            name,
            "PlayCanvas SOG directory",
            imageDecoder);
    }

    private static GaussianSplatCloud LoadSogFromBytes(byte[] metaBytes, Func<string, byte[]> readFile, string name, string sourceFormat, ISogImageDecoder imageDecoder)
    {
        var meta = JsonSerializer.Deserialize<SogMeta>(metaBytes, SogJsonOptions)
            ?? throw new InvalidDataException("SOG meta.json is empty or invalid.");
        ValidateSogMeta(meta);

        using var meansLow = DecodeSogImage(imageDecoder, readFile(meta.Means!.Files![0]), meta.Means.Files[0]);
        using var meansHigh = DecodeSogImage(imageDecoder, readFile(meta.Means.Files[1]), meta.Means.Files[1]);
        using var scales = DecodeSogImage(imageDecoder, readFile(meta.Scales!.Files![0]), meta.Scales.Files[0]);
        using var quats = DecodeSogImage(imageDecoder, readFile(meta.Quats!.Files![0]), meta.Quats.Files[0]);
        using var sh0 = DecodeSogImage(imageDecoder, readFile(meta.Sh0!.Files![0]), meta.Sh0.Files[0]);

        var imageCapacity = MinPixelCount(meansLow, meansHigh, scales, quats, sh0);
        var sourceCount = Math.Min(meta.Count, imageCapacity);
        if (sourceCount <= 0)
        {
            throw new InvalidDataException("SOG does not contain any addressable Gaussian splats.");
        }

        var maxLoadedSplats = GetMaxLoadedSplats(sourceCount);
        var sampleStep = Math.Max(1, (sourceCount + maxLoadedSplats - 1) / maxLoadedSplats);
        var splats = new List<GaussianSplat>(Math.Min(sourceCount, maxLoadedSplats));
        var scalesAreLogEncoded = IsSogLogScaleCodebook(meta.Scales.Codebook!);
        for (var i = 0; i < sourceCount; i++)
        {
            if (sampleStep > 1 && i % sampleStep != 0)
            {
                continue;
            }

            if (TryCreateSogSplat(meta, meansLow, meansHigh, scales, quats, sh0, scalesAreLogEncoded, i, out var splat))
            {
                splats.Add(splat);
            }
        }

        if (splats.Count == 0)
        {
            throw new InvalidDataException("SOG did not contain any readable Gaussian splats.");
        }

        return new GaussianSplatCloud(name, splats.ToArray(), sourceFormat, meta.Count);
    }

    private static bool TryCreateSogSplat(
        SogMeta meta,
        SogImage meansLow,
        SogImage meansHigh,
        SogImage scales,
        SogImage quats,
        SogImage sh0,
        bool scalesAreLogEncoded,
        int index,
        out GaussianSplat splat)
    {
        splat = default;
        var x = DecodeSogUInt16(meansLow, meansHigh, index, 0);
        var y = DecodeSogUInt16(meansLow, meansHigh, index, 1);
        var z = DecodeSogUInt16(meansLow, meansHigh, index, 2);
        var position = new Vec3(
            DecodeSogUnlogLerp(x, meta.Means!.Mins![0], meta.Means.Maxs![0]),
            DecodeSogUnlogLerp(y, meta.Means.Mins[1], meta.Means.Maxs[1]),
            DecodeSogUnlogLerp(z, meta.Means.Mins[2], meta.Means.Maxs[2]));

        var scaleCodebook = meta.Scales!.Codebook!;
        var scale = new Vec3(
            ReadSogScale(scaleCodebook, scales.Get(index, 0), scalesAreLogEncoded),
            ReadSogScale(scaleCodebook, scales.Get(index, 1), scalesAreLogEncoded),
            ReadSogScale(scaleCodebook, scales.Get(index, 2), scalesAreLogEncoded));

        DecodeSogQuaternion(quats, index, out var qw, out var qx, out var qy, out var qz);
        QuaternionToAxes(qw, qx, qy, qz, out var basis0, out var basis1, out var basis2);

        var sh0Codebook = meta.Sh0!.Codebook!;
        var color = new Vec3(
            Math.Clamp(0.5f + SphericalHarmonicsC0 * ReadSogCodebook(sh0Codebook, sh0.Get(index, 0), "sh0"), 0.0f, 1.0f),
            Math.Clamp(0.5f + SphericalHarmonicsC0 * ReadSogCodebook(sh0Codebook, sh0.Get(index, 1), "sh0"), 0.0f, 1.0f),
            Math.Clamp(0.5f + SphericalHarmonicsC0 * ReadSogCodebook(sh0Codebook, sh0.Get(index, 2), "sh0"), 0.0f, 1.0f));
        var alpha = sh0.Get(index, 3) / 255.0f;
        if (alpha <= 0.003f)
        {
            return false;
        }

        splat = ConvertSplatToViewerCoordinates(new GaussianSplat(
            position,
            basis0 * scale.X,
            basis1 * scale.Y,
            basis2 * scale.Z,
            color.X,
            color.Y,
            color.Z,
            alpha));
        return true;
    }

    private static void ValidateSogMeta(SogMeta meta)
    {
        if (meta.Version != 2)
        {
            throw new NotSupportedException($"SOG version {meta.Version} is not supported. Supported version: 2.");
        }

        if (meta.Count <= 0)
        {
            throw new InvalidDataException("SOG count must be greater than zero.");
        }

        ValidateSogFiles(meta.Means, "means", expectedFileCount: 2);
        ValidateSogFiles(meta.Scales, "scales", expectedFileCount: 1);
        ValidateSogFiles(meta.Quats, "quats", expectedFileCount: 1);
        ValidateSogFiles(meta.Sh0, "sh0", expectedFileCount: 1);

        if (meta.Means!.Mins is null || meta.Means.Maxs is null || meta.Means.Mins.Length < 3 || meta.Means.Maxs.Length < 3)
        {
            throw new InvalidDataException("SOG means.mins and means.maxs must contain at least three values.");
        }

        if (meta.Scales!.Codebook is null || meta.Scales.Codebook.Length < 256 ||
            meta.Sh0!.Codebook is null || meta.Sh0.Codebook.Length < 256)
        {
            throw new InvalidDataException("SOG scales.codebook and sh0.codebook must contain at least 256 values.");
        }
    }

    private static void ValidateSogFiles(SogSection? section, string name, int expectedFileCount)
    {
        if (section?.Files is null || section.Files.Length < expectedFileCount)
        {
            throw new InvalidDataException($"SOG {name}.files must contain at least {expectedFileCount} file path{(expectedFileCount == 1 ? string.Empty : "s")}.");
        }
    }

    private static SogImage DecodeSogImage(ISogImageDecoder imageDecoder, byte[] encoded, string name)
        => imageDecoder.DecodeRgba8888(encoded, name);

    private static byte[] ReadSogArchiveFile(ZipArchive archive, string path)
    {
        var normalized = NormalizeSogPath(path);
        var entry = archive.GetEntry(normalized) ?? FindSogArchiveEntry(archive, normalized);
        if (entry is null)
        {
            throw new FileNotFoundException($"SOG zip does not contain '{path}'.");
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static ZipArchiveEntry? FindSogArchiveEntry(ZipArchive archive, string normalizedPath)
    {
        var fileName = System.IO.Path.GetFileName(normalizedPath);
        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeSogPath(entry.FullName);
            if (entryName.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                entryName.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFileName(entryName).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string NormalizeSogPath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static int MinPixelCount(params SogImage[] images)
    {
        var count = int.MaxValue;
        foreach (var image in images)
        {
            count = Math.Min(count, image.PixelCount);
        }

        return count == int.MaxValue ? 0 : count;
    }

    private static int DecodeSogUInt16(SogImage low, SogImage high, int index, int channel)
        => low.Get(index, channel) | (high.Get(index, channel) << 8);

    private static float DecodeSogUnlogLerp(int quantized, float min, float max)
    {
        var encoded = min + (max - min) * (quantized / 65535.0f);
        return encoded < 0.0f ? 1.0f - MathF.Exp(-encoded) : MathF.Exp(encoded) - 1.0f;
    }

    private static float ReadSogCodebook(float[] codebook, int index, string name)
    {
        if ((uint)index >= (uint)codebook.Length)
        {
            throw new InvalidDataException($"SOG {name}.codebook does not contain index {index}.");
        }

        return codebook[index];
    }

    private static bool IsSogLogScaleCodebook(float[] codebook)
    {
        foreach (var value in codebook)
        {
            if (value <= 0.0f)
            {
                return true;
            }
        }

        return false;
    }

    private static float ReadSogScale(float[] codebook, int index, bool isLogEncoded)
    {
        var value = ReadSogCodebook(codebook, index, "scales");
        return isLogEncoded ? LogScaleToRadius(value) : MathF.Max(0.00001f, value);
    }

    private static void DecodeSogQuaternion(SogImage quats, int index, out float qw, out float qx, out float qy, out float qz)
    {
        var a = DecodeSogQuatComponent(quats.Get(index, 0));
        var b = DecodeSogQuatComponent(quats.Get(index, 1));
        var c = DecodeSogQuatComponent(quats.Get(index, 2));
        var mode = quats.Get(index, 3);
        var d = MathF.Sqrt(MathF.Max(0.0f, 1.0f - a * a - b * b - c * c));

        // SOG stores the three non-largest components in original rot_0..rot_3 order.
        switch (mode)
        {
            case 252:
                qw = d; qx = a; qy = b; qz = c;
                break;
            case 253:
                qw = a; qx = d; qy = b; qz = c;
                break;
            case 254:
                qw = a; qx = b; qy = d; qz = c;
                break;
            case 255:
                qw = a; qx = b; qy = c; qz = d;
                break;
            default:
                qx = qy = qz = 0.0f;
                qw = 1.0f;
                break;
        }

        var length = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (length <= 0.00001f)
        {
            qw = 1.0f;
            qx = qy = qz = 0.0f;
            return;
        }

        qw /= length;
        qx /= length;
        qy /= length;
        qz /= length;
    }

    private static float DecodeSogQuatComponent(int value) => (value / 255.0f - 0.5f) * (2.0f / MathF.Sqrt(2.0f));

    private static void NormalizeSplats(GaussianSplat[] splats, out float radius)
    {
        if (splats.Length == 0)
        {
            radius = 1.0f;
            return;
        }

        var min = splats[0].Position;
        var max = splats[0].Position;
        foreach (var splat in splats)
        {
            ExpandSplatBounds(splat.Position, ref min, ref max);
        }

        var center = (min + max) * 0.5f;
        radius = 0.001f;
        foreach (var splat in splats)
        {
            radius = MathF.Max(radius, (splat.Position - center).Length);
        }

        var scale = radius <= 0.0001f ? 1.0f : 1.65f / radius;
        radius = 0.001f;
        for (var i = 0; i < splats.Length; i++)
        {
            var splat = splats[i];
            var position = (splat.Position - center) * scale;
            splats[i] = splat with
            {
                Position = position,
                Axis0 = splat.Axis0 * scale,
                Axis1 = splat.Axis1 * scale,
                Axis2 = splat.Axis2 * scale
            };
            radius = MathF.Max(radius, position.Length);
        }
    }

    private static void ExpandSplatBounds(Vec3 point, ref Vec3 min, ref Vec3 max)
    {
        min = new Vec3(MathF.Min(min.X, point.X), MathF.Min(min.Y, point.Y), MathF.Min(min.Z, point.Z));
        max = new Vec3(MathF.Max(max.X, point.X), MathF.Max(max.Y, point.Y), MathF.Max(max.Z, point.Z));
    }

    private static PlyHeader ReadHeader(Stream stream)
    {
        var lines = new List<string>(64);
        var bytes = new List<byte>(128);
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                throw new InvalidDataException("Unexpected end of PLY header.");
            }

            if (value == '\n')
            {
                var line = Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                lines.Add(line);
                bytes.Clear();
                if (line == "end_header")
                {
                    break;
                }
            }
            else
            {
                bytes.Add((byte)value);
            }
        }

        if (lines.Count == 0 || lines[0] != "ply")
        {
            throw new InvalidDataException("File is not a PLY file.");
        }

        var format = string.Empty;
        var currentElement = string.Empty;
        var vertexCount = 0;
        var vertexProperties = new List<PlyProperty>(64);
        foreach (var line in lines)
        {
            var parts = SplitWhitespace(line);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "format" when parts.Length >= 2:
                    format = parts[1];
                    break;
                case "element" when parts.Length >= 3:
                    currentElement = parts[1];
                    if (currentElement == "vertex")
                    {
                        vertexCount = int.Parse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture);
                    }

                    break;
                case "property" when currentElement == "vertex" && parts.Length >= 3 && parts[1] != "list":
                    vertexProperties.Add(new PlyProperty(parts[2], parts[1]));
                    break;
            }
        }

        return new PlyHeader(format, vertexCount, vertexProperties.ToArray());
    }

    private static GaussianSplat[] ReadAsciiSplats(Stream stream, PlyHeader header)
    {
        var maxLoadedSplats = GetMaxLoadedSplats(header.VertexCount);
        var sampleStep = Math.Max(1, (header.VertexCount + maxLoadedSplats - 1) / maxLoadedSplats);
        var splats = new List<GaussianSplat>(Math.Min(header.VertexCount, maxLoadedSplats));
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
        var values = new double[header.Properties.Length];
        var layout = new PlyLayout(header.Properties);
        for (var i = 0; i < header.VertexCount; i++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (sampleStep > 1 && i % sampleStep != 0)
            {
                continue;
            }

            var parts = SplitWhitespace(line);
            if (parts.Length < header.Properties.Length)
            {
                continue;
            }

            for (var p = 0; p < header.Properties.Length; p++)
            {
                values[p] = double.Parse(parts[p], NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            if (TryCreateSplat(layout, values, out var splat))
            {
                splats.Add(splat);
            }
        }

        return splats.ToArray();
    }

    private static GaussianSplat[] ReadBinarySplats(Stream stream, PlyHeader header)
    {
        var layout = new PlyBinaryLayout(header.Properties);
        var maxLoadedSplats = GetMaxLoadedSplats(header.VertexCount);
        var sampleStep = Math.Max(1, (header.VertexCount + maxLoadedSplats - 1) / maxLoadedSplats);
        var splats = new List<GaussianSplat>(Math.Min(header.VertexCount, maxLoadedSplats));
        var recordsPerBuffer = Math.Max(1, SplatReadBufferBytes / layout.RecordStride);
        var buffer = ArrayPool<byte>.Shared.Rent(recordsPerBuffer * layout.RecordStride);
        try
        {
            var remaining = header.VertexCount;
            var recordIndex = 0;
            while (remaining > 0)
            {
                var recordsToRead = Math.Min(recordsPerBuffer, remaining);
                var bytesToRead = recordsToRead * layout.RecordStride;
                stream.ReadExactly(buffer.AsSpan(0, bytesToRead));
                var chunk = buffer.AsSpan(0, bytesToRead);
                for (var r = 0; r < recordsToRead; r++, recordIndex++)
                {
                    if (sampleStep > 1 && recordIndex % sampleStep != 0)
                    {
                        continue;
                    }

                    var row = chunk.Slice(r * layout.RecordStride, layout.RecordStride);
                    if (TryCreateSplat(new PlyBinaryValues(layout, row), out var splat))
                    {
                        splats.Add(splat);
                    }
                }

                remaining -= recordsToRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return splats.ToArray();
    }

    private static int GetMaxLoadedSplats(int sourceCount)
    {
        var value = Environment.GetEnvironmentVariable("MESHMODELER_MAX_SPLATS");
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return Math.Clamp(parsed, 10_000, sourceCount);
        }

        return sourceCount;
    }

    private static bool TryCreateSplat(PlyLayout layout, double[] values, out GaussianSplat splat)
    {
        splat = default;
        if (!layout.TryGet(values, "x", out var x) ||
            !layout.TryGet(values, "y", out var y) ||
            !layout.TryGet(values, "z", out var z))
        {
            return false;
        }

        var position = new Vec3((float)x, (float)y, (float)z);
        var color = ReadSplatColor(layout, values);
        var alpha = ReadSplatAlpha(layout, values);
        if (alpha <= 0.003f)
        {
            return false;
        }

        var scale = ReadSplatScale(layout, values);
        ReadSplatRotation(layout, values, out var qw, out var qx, out var qy, out var qz);
        QuaternionToAxes(qw, qx, qy, qz, out var basis0, out var basis1, out var basis2);
        splat = ConvertSplatToViewerCoordinates(new GaussianSplat(
            position,
            basis0 * scale.X,
            basis1 * scale.Y,
            basis2 * scale.Z,
            color.X,
            color.Y,
            color.Z,
            alpha));
        return true;
    }

    private static bool TryCreateSplat(PlyBinaryValues values, out GaussianSplat splat)
    {
        splat = default;
        if (!values.TryGet("x", out var x) ||
            !values.TryGet("y", out var y) ||
            !values.TryGet("z", out var z))
        {
            return false;
        }

        var position = new Vec3((float)x, (float)y, (float)z);
        var color = ReadSplatColor(values);
        var alpha = ReadSplatAlpha(values);
        if (alpha <= 0.003f)
        {
            return false;
        }

        var scale = ReadSplatScale(values);
        ReadSplatRotation(values, out var qw, out var qx, out var qy, out var qz);
        QuaternionToAxes(qw, qx, qy, qz, out var basis0, out var basis1, out var basis2);
        splat = ConvertSplatToViewerCoordinates(new GaussianSplat(
            position,
            basis0 * scale.X,
            basis1 * scale.Y,
            basis2 * scale.Z,
            color.X,
            color.Y,
            color.Z,
            alpha));
        return true;
    }

    private static GaussianSplat ConvertSplatToViewerCoordinates(GaussianSplat splat)
        => splat with
        {
            Position = FlipSplatY(splat.Position),
            Axis0 = FlipSplatY(splat.Axis0),
            Axis1 = FlipSplatY(splat.Axis1),
            Axis2 = FlipSplatY(splat.Axis2)
        };

    private static Vec3 FlipSplatY(Vec3 value) => new(value.X, -value.Y, value.Z);

    private static Vec3 ReadSplatColor(PlyLayout layout, double[] values)
    {
        if (layout.TryGet(values, "f_dc_0", out var dc0) &&
            layout.TryGet(values, "f_dc_1", out var dc1) &&
            layout.TryGet(values, "f_dc_2", out var dc2))
        {
            return new Vec3(
                Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc0, 0.0f, 1.0f),
                Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc1, 0.0f, 1.0f),
                Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc2, 0.0f, 1.0f));
        }

        var r = ReadColorChannel(layout, values, "red", "r", fallback: 0.85f);
        var g = ReadColorChannel(layout, values, "green", "g", fallback: 0.90f);
        var b = ReadColorChannel(layout, values, "blue", "b", fallback: 1.0f);
        return new Vec3(r, g, b);
    }

    private static Vec3 ReadSplatColor(PlyBinaryValues values)
    {
        if (values.TryGet("f_dc_0", out var dc0) &&
            values.TryGet("f_dc_1", out var dc1) &&
            values.TryGet("f_dc_2", out var dc2))
        {
            return new Vec3(
                Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc0, 0.0f, 1.0f),
                Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc1, 0.0f, 1.0f),
                Math.Clamp(0.5f + SphericalHarmonicsC0 * (float)dc2, 0.0f, 1.0f));
        }

        var r = ReadColorChannel(values, "red", "r", fallback: 0.85f);
        var g = ReadColorChannel(values, "green", "g", fallback: 0.90f);
        var b = ReadColorChannel(values, "blue", "b", fallback: 1.0f);
        return new Vec3(r, g, b);
    }

    private static float ReadSplatAlpha(PlyLayout layout, double[] values)
    {
        if (layout.TryGet(values, "opacity", out var opacity))
        {
            return Sigmoid((float)opacity);
        }

        if (layout.TryGet(values, "alpha", out var alpha))
        {
            return NormalizeColorChannel((float)alpha);
        }

        return 0.45f;
    }

    private static float ReadSplatAlpha(PlyBinaryValues values)
    {
        if (values.TryGet("opacity", out var opacity))
        {
            return Sigmoid((float)opacity);
        }

        if (values.TryGet("alpha", out var alpha))
        {
            return NormalizeColorChannel((float)alpha);
        }

        return 0.45f;
    }

    private static Vec3 ReadSplatScale(PlyLayout layout, double[] values)
    {
        if (layout.TryGet(values, "scale_0", out var s0) &&
            layout.TryGet(values, "scale_1", out var s1) &&
            layout.TryGet(values, "scale_2", out var s2))
        {
            return new Vec3(LogScaleToRadius((float)s0), LogScaleToRadius((float)s1), LogScaleToRadius((float)s2));
        }

        var sx = ReadDirectScale(layout, values, "scale_x", "sx", 0.025f);
        var sy = ReadDirectScale(layout, values, "scale_y", "sy", sx);
        var sz = ReadDirectScale(layout, values, "scale_z", "sz", sy);
        return new Vec3(sx, sy, sz);
    }

    private static Vec3 ReadSplatScale(PlyBinaryValues values)
    {
        if (values.TryGet("scale_0", out var s0) &&
            values.TryGet("scale_1", out var s1) &&
            values.TryGet("scale_2", out var s2))
        {
            return new Vec3(LogScaleToRadius((float)s0), LogScaleToRadius((float)s1), LogScaleToRadius((float)s2));
        }

        var sx = ReadDirectScale(values, "scale_x", "sx", 0.025f);
        var sy = ReadDirectScale(values, "scale_y", "sy", sx);
        var sz = ReadDirectScale(values, "scale_z", "sz", sy);
        return new Vec3(sx, sy, sz);
    }

    private static void ReadSplatRotation(PlyLayout layout, double[] values, out float qw, out float qx, out float qy, out float qz)
    {
        qw = 1.0f;
        qx = 0.0f;
        qy = 0.0f;
        qz = 0.0f;
        if (layout.TryGet(values, "rot_0", out var r0) &&
            layout.TryGet(values, "rot_1", out var r1) &&
            layout.TryGet(values, "rot_2", out var r2) &&
            layout.TryGet(values, "rot_3", out var r3))
        {
            qw = (float)r0;
            qx = (float)r1;
            qy = (float)r2;
            qz = (float)r3;
        }
        else
        {
            layout.TryGet(values, "qw", out var qwv);
            layout.TryGet(values, "qx", out var qxv);
            layout.TryGet(values, "qy", out var qyv);
            layout.TryGet(values, "qz", out var qzv);
            qw = qwv == 0 ? 1.0f : (float)qwv;
            qx = (float)qxv;
            qy = (float)qyv;
            qz = (float)qzv;
        }

        var length = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (length <= 0.00001f)
        {
            qw = 1.0f;
            qx = qy = qz = 0.0f;
            return;
        }

        qw /= length;
        qx /= length;
        qy /= length;
        qz /= length;
    }

    private static void ReadSplatRotation(PlyBinaryValues values, out float qw, out float qx, out float qy, out float qz)
    {
        qw = 1.0f;
        qx = 0.0f;
        qy = 0.0f;
        qz = 0.0f;
        if (values.TryGet("rot_0", out var r0) &&
            values.TryGet("rot_1", out var r1) &&
            values.TryGet("rot_2", out var r2) &&
            values.TryGet("rot_3", out var r3))
        {
            qw = (float)r0;
            qx = (float)r1;
            qy = (float)r2;
            qz = (float)r3;
        }
        else
        {
            values.TryGet("qw", out var qwv);
            values.TryGet("qx", out var qxv);
            values.TryGet("qy", out var qyv);
            values.TryGet("qz", out var qzv);
            qw = qwv == 0 ? 1.0f : (float)qwv;
            qx = (float)qxv;
            qy = (float)qyv;
            qz = (float)qzv;
        }

        var length = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (length <= 0.00001f)
        {
            qw = 1.0f;
            qx = qy = qz = 0.0f;
            return;
        }

        qw /= length;
        qx /= length;
        qy /= length;
        qz /= length;
    }

    private static void QuaternionToAxes(float w, float x, float y, float z, out Vec3 axis0, out Vec3 axis1, out Vec3 axis2)
    {
        var xx = x * x;
        var yy = y * y;
        var zz = z * z;
        var xy = x * y;
        var xz = x * z;
        var yz = y * z;
        var wx = w * x;
        var wy = w * y;
        var wz = w * z;
        axis0 = new Vec3(1.0f - 2.0f * (yy + zz), 2.0f * (xy + wz), 2.0f * (xz - wy));
        axis1 = new Vec3(2.0f * (xy - wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz + wx));
        axis2 = new Vec3(2.0f * (xz + wy), 2.0f * (yz - wx), 1.0f - 2.0f * (xx + yy));
    }

    private static double ReadBinaryScalar(BinaryReader reader, string type) => type switch
    {
        "char" or "int8" => reader.ReadSByte(),
        "uchar" or "uint8" => reader.ReadByte(),
        "short" or "int16" => reader.ReadInt16(),
        "ushort" or "uint16" => reader.ReadUInt16(),
        "int" or "int32" => reader.ReadInt32(),
        "uint" or "uint32" => reader.ReadUInt32(),
        "float" or "float32" => reader.ReadSingle(),
        "double" or "float64" => reader.ReadDouble(),
        _ => throw new NotSupportedException($"PLY scalar type '{type}' is not supported.")
    };

    private static double ReadBinaryScalar(ReadOnlySpan<byte> row, int offset, string type) => type switch
    {
        "char" or "int8" => (sbyte)row[offset],
        "uchar" or "uint8" => row[offset],
        "short" or "int16" => BinaryPrimitives.ReadInt16LittleEndian(row.Slice(offset, sizeof(short))),
        "ushort" or "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset, sizeof(ushort))),
        "int" or "int32" => BinaryPrimitives.ReadInt32LittleEndian(row.Slice(offset, sizeof(int))),
        "uint" or "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(offset, sizeof(uint))),
        "float" or "float32" => BinaryPrimitives.ReadSingleLittleEndian(row.Slice(offset, sizeof(float))),
        "double" or "float64" => BinaryPrimitives.ReadDoubleLittleEndian(row.Slice(offset, sizeof(double))),
        _ => throw new NotSupportedException($"PLY scalar type '{type}' is not supported.")
    };

    private static int BinaryScalarSize(string type) => type switch
    {
        "char" or "int8" or "uchar" or "uint8" => 1,
        "short" or "int16" or "ushort" or "uint16" => 2,
        "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => throw new NotSupportedException($"PLY scalar type '{type}' is not supported.")
    };

    private static float ReadColorChannel(PlyLayout layout, double[] values, string name, string shortName, float fallback)
    {
        if (layout.TryGet(values, name, out var value) || layout.TryGet(values, shortName, out value))
        {
            return NormalizeColorChannel((float)value);
        }

        return fallback;
    }

    private static float ReadColorChannel(PlyBinaryValues values, string name, string shortName, float fallback)
    {
        if (values.TryGet(name, out var value) || values.TryGet(shortName, out value))
        {
            return NormalizeColorChannel((float)value);
        }

        return fallback;
    }

    private static float NormalizeColorChannel(float value) => value > 1.0f ? Math.Clamp(value / 255.0f, 0.0f, 1.0f) : Math.Clamp(value, 0.0f, 1.0f);
    private static float LogScaleToRadius(float value) => MathF.Exp(Math.Clamp(value, -12.0f, 4.0f));
    private static float ReadDirectScale(PlyLayout layout, double[] values, string name, string shortName, float fallback)
        => layout.TryGet(values, name, out var value) || layout.TryGet(values, shortName, out value)
            ? MathF.Max(0.00001f, (float)value)
            : fallback;

    private static float ReadDirectScale(PlyBinaryValues values, string name, string shortName, float fallback)
        => values.TryGet(name, out var value) || values.TryGet(shortName, out value)
            ? MathF.Max(0.00001f, (float)value)
            : fallback;

    private static float Sigmoid(float value)
    {
        if (value >= 0)
        {
            var z = MathF.Exp(-value);
            return 1.0f / (1.0f + z);
        }

        var ez = MathF.Exp(value);
        return ez / (1.0f + ez);
    }

    private static string[] SplitWhitespace(string line) => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    private static float NextFloat(Random random) => (float)random.NextDouble();

    private static Vec3 HsvToRgb(float h, float s, float v)
    {
        h -= MathF.Floor(h);
        var c = v * s;
        var x = c * (1.0f - MathF.Abs(h * 6.0f % 2.0f - 1.0f));
        var m = v - c;
        var sector = (int)MathF.Floor(h * 6.0f);
        var rgb = sector switch
        {
            0 => new Vec3(c, x, 0),
            1 => new Vec3(x, c, 0),
            2 => new Vec3(0, c, x),
            3 => new Vec3(0, x, c),
            4 => new Vec3(x, 0, c),
            _ => new Vec3(c, 0, x)
        };

        return new Vec3(rgb.X + m, rgb.Y + m, rgb.Z + m);
    }

    private readonly record struct PlyHeader(string Format, int VertexCount, PlyProperty[] Properties);
    private readonly record struct PlyProperty(string Name, string Type);
    private readonly record struct PlyBinaryProperty(int Offset, string Type);

    private sealed class SogMeta
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("means")]
        public SogSection? Means { get; set; }

        [JsonPropertyName("scales")]
        public SogSection? Scales { get; set; }

        [JsonPropertyName("quats")]
        public SogSection? Quats { get; set; }

        [JsonPropertyName("sh0")]
        public SogSection? Sh0 { get; set; }

        [JsonPropertyName("shN")]
        public SogSection? ShN { get; set; }
    }

    private sealed class SogSection
    {
        [JsonPropertyName("files")]
        public string[]? Files { get; set; }

        [JsonPropertyName("mins")]
        public float[]? Mins { get; set; }

        [JsonPropertyName("maxs")]
        public float[]? Maxs { get; set; }

        [JsonPropertyName("codebook")]
        public float[]? Codebook { get; set; }
    }

    private readonly ref struct PlyBinaryValues
    {
        private readonly PlyBinaryLayout _layout;
        private readonly ReadOnlySpan<byte> _row;

        public PlyBinaryValues(PlyBinaryLayout layout, ReadOnlySpan<byte> row)
        {
            _layout = layout;
            _row = row;
        }

        public bool TryGet(string name, out double value) => _layout.TryGet(_row, name, out value);
    }

    private sealed class PlyBinaryLayout
    {
        private readonly Dictionary<string, PlyBinaryProperty> _properties;

        public PlyBinaryLayout(PlyProperty[] properties)
        {
            _properties = new Dictionary<string, PlyBinaryProperty>(properties.Length, StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            foreach (var property in properties)
            {
                _properties[property.Name] = new PlyBinaryProperty(offset, property.Type);
                offset += BinaryScalarSize(property.Type);
            }

            RecordStride = offset;
        }

        public int RecordStride { get; }

        public bool TryGet(ReadOnlySpan<byte> row, string name, out double value)
        {
            if (_properties.TryGetValue(name, out var property) &&
                property.Offset >= 0 &&
                property.Offset + BinaryScalarSize(property.Type) <= row.Length)
            {
                value = ReadBinaryScalar(row, property.Offset, property.Type);
                return true;
            }

            value = 0;
            return false;
        }
    }

    private sealed class PlyLayout
    {
        private readonly Dictionary<string, int> _indices;

        public PlyLayout(PlyProperty[] properties)
        {
            _indices = new Dictionary<string, int>(properties.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < properties.Length; i++)
            {
                _indices[properties[i].Name] = i;
            }
        }

        public bool TryGet(double[] values, string name, out double value)
        {
            if (_indices.TryGetValue(name, out var index) && index >= 0 && index < values.Length)
            {
                value = values[index];
                return true;
            }

            value = 0;
            return false;
        }
    }
}


public sealed class MeshDocument : IDisposable
{
    private static readonly Vec3 DefaultMeshColor = MeshModelerConstants.DefaultMeshColor;

    private readonly Dictionary<string, int> _materialIndices = new(StringComparer.OrdinalIgnoreCase);

    private MeshDocument()
    {
        Materials.Add(new MeshMaterial("default", DefaultMeshColor));
        _materialIndices[string.Empty] = 0;
    }

    public string Name { get; private set; } = "Untitled";
    public List<Vec3> Positions { get; } = new();
    public List<Vec3> OriginalPositions { get; } = new();
    public List<Vec3> Normals { get; } = new();
    public List<Triangle> Triangles { get; } = new();
    public List<MeshMaterial> Materials { get; } = new();
    public Vec3 Center { get; private set; }
    public float Radius { get; private set; } = 1.0f;
    public int MaterialCount => Materials.Count;
    public int TextureMaterialCount
    {
        get
        {
            var count = 0;
            foreach (var material in Materials)
            {
                if (material.HasDiffuseTexture)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public bool RequiresDepthSortedRendering
    {
        get
        {
            foreach (var material in Materials)
            {
                if (material.RequiresDepthSortedRendering)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public int SkippedFaceCount { get; private set; }
    public int SkippedSceneHelperFaceCount { get; private set; }
    public bool UseAuthoredNormals { get; private set; }

    public MeshMaterial GetMaterial(int index)
        => index >= 0 && index < Materials.Count ? Materials[index] : Materials[0];

    public void Dispose()
    {
    }

    public static MeshDocument CreateTorus()
    {
        var doc = new MeshDocument { Name = "Procedural UV torus" };
        const int majorSegments = 48;
        const int minorSegments = 16;
        const float majorRadius = 1.05f;
        const float minorRadius = 0.34f;

        for (var y = 0; y < minorSegments; y++)
        {
            var v = y / (float)minorSegments;
            var minor = v * MathF.Tau;
            var ringRadius = majorRadius + MathF.Cos(minor) * minorRadius;
            var py = MathF.Sin(minor) * minorRadius;

            for (var x = 0; x < majorSegments; x++)
            {
                var u = x / (float)majorSegments;
                var major = u * MathF.Tau;
                doc.Positions.Add(new Vec3(MathF.Cos(major) * ringRadius, py, MathF.Sin(major) * ringRadius));
            }
        }

        for (var y = 0; y < minorSegments; y++)
        {
            var y1 = (y + 1) % minorSegments;
            for (var x = 0; x < majorSegments; x++)
            {
                var x1 = (x + 1) % majorSegments;
                var a = y * majorSegments + x;
                var b = y * majorSegments + x1;
                var c = y1 * majorSegments + x1;
                var d = y1 * majorSegments + x;
                var u0 = x / (float)majorSegments;
                var u1 = (x + 1) / (float)majorSegments;
                var v0 = y / (float)minorSegments;
                var v1 = (y + 1) / (float)minorSegments;
                doc.Triangles.Add(new Triangle(new Corner(a, u0, v0), new Corner(b, u1, v0), new Corner(c, u1, v1), DefaultMeshColor));
                doc.Triangles.Add(new Triangle(new Corner(a, u0, v0), new Corner(c, u1, v1), new Corner(d, u0, v1), DefaultMeshColor));
            }
        }

        doc.FinalizeDocument(normalize: false);
        return doc;
    }

    public static MeshDocument ParseObj(string text, string name, string? baseDirectory = null)
    {
        var doc = new MeshDocument { Name = name };
        var uvs = new List<Vec2>();
        var authoredNormals = new List<Vec3>();
        var faceCorners = new List<Corner>(8);
        var materialDefinitions = new Dictionary<string, MeshMaterial>(StringComparer.OrdinalIgnoreCase);
        var currentMaterialIndex = 0;
        var currentMaterialColor = doc.Materials[0].Diffuse;
        var sawAuthoredNormal = false;
        var culture = CultureInfo.InvariantCulture;

        foreach (var raw in EnumerateLogicalObjLines(text))
        {
            var line = StripInlineComment(raw).Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = SplitWhitespace(line);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    doc.Positions.Add(new Vec3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                    break;
                case "vt" when parts.Length >= 3:
                    uvs.Add(new Vec2(Parse(parts[1]), 1.0f - Parse(parts[2])));
                    break;
                case "vn" when parts.Length >= 4:
                    authoredNormals.Add(new Vec3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])).Normalized());
                    break;
                case "mtllib":
                    LoadMaterialLibraries(RestAfterKeyword(line, "mtllib"), baseDirectory, materialDefinitions);
                    break;
                case "usemtl":
                    currentMaterialIndex = doc.ResolveMaterialIndex(RestAfterKeyword(line, "usemtl"), materialDefinitions);
                    currentMaterialColor = doc.Materials[currentMaterialIndex].Diffuse;
                    break;
                case "f" when parts.Length >= 4:
                    var skipSceneHelper = IsSceneHelperMaterial(doc.GetMaterial(currentMaterialIndex).Name);
                    faceCorners.Clear();
                    for (var i = 1; i < parts.Length; i++)
                    {
                        if (!TryParseCorner(parts[i], doc.Positions.Count, uvs, authoredNormals, out var corner))
                        {
                            faceCorners.Clear();
                            doc.SkippedFaceCount++;
                            break;
                        }

                        sawAuthoredNormal |= corner.HasNormal;
                        faceCorners.Add(corner);
                    }

                    if (skipSceneHelper)
                    {
                        doc.SkippedSceneHelperFaceCount++;
                        break;
                    }

                    for (var i = 1; i < faceCorners.Count - 1; i++)
                    {
                        doc.Triangles.Add(new Triangle(faceCorners[0], faceCorners[i], faceCorners[i + 1], currentMaterialColor, currentMaterialIndex));
                    }

                    break;
                case "f":
                    doc.SkippedFaceCount++;
                    break;
            }
        }

        if (doc.Positions.Count == 0 || doc.Triangles.Count == 0)
        {
            throw new InvalidOperationException("OBJ did not contain any triangulatable faces.");
        }

        doc.UseAuthoredNormals = sawAuthoredNormal;
        doc.FinalizeDocument(normalize: true);
        return doc;

        float Parse(string value) => float.Parse(value, NumberStyles.Float, culture);
    }

    private static bool TryParseCorner(string token, int positionCount, List<Vec2> uvs, List<Vec3> authoredNormals, out Corner corner)
    {
        corner = default;
        var pieces = token.Split('/');
        if (pieces.Length == 0 || !TryParseObjIndex(pieces[0], positionCount, out var positionIndex))
        {
            return false;
        }

        var uv = DefaultUv(positionIndex, positionCount);
        if (pieces.Length > 1 &&
            pieces[1].Length > 0 &&
            uvs.Count > 0 &&
            TryParseObjIndex(pieces[1], uvs.Count, out var uvIndex))
        {
            uv = uvs[uvIndex];
        }

        var normal = default(Vec3);
        var hasNormal = false;
        if (pieces.Length > 2 &&
            pieces[2].Length > 0 &&
            authoredNormals.Count > 0 &&
            TryParseObjIndex(pieces[2], authoredNormals.Count, out var normalIndex))
        {
            normal = authoredNormals[normalIndex];
            hasNormal = true;
        }

        corner = new Corner(positionIndex, uv.X, uv.Y, normal, hasNormal);
        return true;
    }

    private static bool TryParseObjIndex(string token, int count, out int resolvedIndex)
    {
        resolvedIndex = -1;
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
        {
            return false;
        }

        resolvedIndex = index < 0 ? count + index : index - 1;
        return resolvedIndex >= 0 && resolvedIndex < count;
    }

    private static IEnumerable<string> EnumerateLogicalObjLines(string text)
    {
        using var reader = new StringReader(text);
        var pending = string.Empty;
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            var line = raw.TrimEnd();
            if (line.EndsWith("\\", StringComparison.Ordinal))
            {
                pending += line.Substring(0, line.Length - 1) + " ";
                continue;
            }

            yield return pending + line;
            pending = string.Empty;
        }

        if (pending.Length > 0)
        {
            yield return pending;
        }
    }

    private static string StripInlineComment(string line)
    {
        var index = line.IndexOf('#', StringComparison.Ordinal);
        return index >= 0 ? line.Substring(0, index) : line;
    }

    private static string[] SplitWhitespace(string line)
        => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    private static string RestAfterKeyword(string line, string keyword)
        => line.Length > keyword.Length ? line.Substring(keyword.Length).Trim() : string.Empty;

    private static void LoadMaterialLibraries(string materialLibraries, string? baseDirectory, Dictionary<string, MeshMaterial> materialDefinitions)
    {
        if (string.IsNullOrWhiteSpace(materialLibraries) || string.IsNullOrWhiteSpace(baseDirectory))
        {
            return;
        }

        var combinedPath = System.IO.Path.Combine(baseDirectory, materialLibraries);
        if (File.Exists(combinedPath))
        {
            ParseMtl(File.ReadAllText(combinedPath), System.IO.Path.GetDirectoryName(combinedPath), materialDefinitions);
            return;
        }

        foreach (var token in SplitWhitespace(materialLibraries))
        {
            var path = System.IO.Path.Combine(baseDirectory, token);
            if (File.Exists(path))
            {
                ParseMtl(File.ReadAllText(path), System.IO.Path.GetDirectoryName(path), materialDefinitions);
            }
        }
    }

    private static void ParseMtl(string text, string? materialDirectory, Dictionary<string, MeshMaterial> materialDefinitions)
    {
        MeshMaterial? currentMaterial = null;
        foreach (var raw in EnumerateLogicalObjLines(text))
        {
            var line = StripInlineComment(raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = SplitWhitespace(line);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "newmtl":
                    currentMaterial = GetOrCreateMaterialDefinition(
                        materialDefinitions,
                        RestAfterKeyword(line, "newmtl"));
                    break;
                case "Ka" when currentMaterial is not null && parts.Length >= 4:
                    currentMaterial.Ambient = new Vec3(
                        ParseMtlFloat(parts[1]),
                        ParseMtlFloat(parts[2]),
                        ParseMtlFloat(parts[3]));
                    break;
                case "Kd" when currentMaterial is not null && parts.Length >= 4:
                    currentMaterial.Diffuse = new Vec3(
                        ParseMtlFloat(parts[1]),
                        ParseMtlFloat(parts[2]),
                        ParseMtlFloat(parts[3]));
                    break;
                case "Ks" when currentMaterial is not null && parts.Length >= 4:
                    currentMaterial.Specular = new Vec3(
                        ParseMtlFloat(parts[1]),
                        ParseMtlFloat(parts[2]),
                        ParseMtlFloat(parts[3]));
                    break;
                case "Ke" when currentMaterial is not null && parts.Length >= 4:
                    currentMaterial.Emission = new Vec3(
                        ParseMtlFloat(parts[1]),
                        ParseMtlFloat(parts[2]),
                        ParseMtlFloat(parts[3]));
                    break;
                case "Ns" when currentMaterial is not null && parts.Length >= 2:
                    currentMaterial.Shininess = Math.Clamp(float.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture), 0.0f, 1000.0f);
                    break;
                case "d" when currentMaterial is not null && parts.Length >= 2:
                    currentMaterial.Alpha = ParseMtlFloat(parts[1]);
                    break;
                case "Tr" when currentMaterial is not null && parts.Length >= 2:
                    currentMaterial.Alpha = 1.0f - ParseMtlFloat(parts[1]);
                    break;
                case "map_Kd" when currentMaterial is not null:
                    currentMaterial.DiffuseTexturePath = ResolveMaterialMapPath(
                        ExtractMaterialMapPath(line, "map_Kd"),
                        materialDirectory);
                    break;
            }
        }
    }

    private static float ParseMtlFloat(string value)
        => Math.Clamp(float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture), 0.0f, 1.0f);

    private static MeshMaterial GetOrCreateMaterialDefinition(Dictionary<string, MeshMaterial> materialDefinitions, string materialName)
    {
        if (!materialDefinitions.TryGetValue(materialName, out var material))
        {
            material = new MeshMaterial(materialName, GuessMaterialColor(materialName));
            materialDefinitions[materialName] = material;
        }

        return material;
    }

    private int ResolveMaterialIndex(string materialName, Dictionary<string, MeshMaterial> materialDefinitions)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return 0;
        }

        if (_materialIndices.TryGetValue(materialName, out var index))
        {
            return index;
        }

        var material = materialDefinitions.TryGetValue(materialName, out var defined)
            ? defined
            : new MeshMaterial(materialName, GuessMaterialColor(materialName));

        index = Materials.Count;
        Materials.Add(material);
        _materialIndices[materialName] = index;
        return index;
    }

    private static bool IsSceneHelperMaterial(string materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return false;
        }

        var name = materialName.ToLowerInvariant();
        return name.Contains("studio_lights", StringComparison.Ordinal) ||
               name.Contains("back_drop", StringComparison.Ordinal) ||
               name is "sun";
    }

    private static string ResolveMaterialMapPath(string mapPath, string? materialDirectory)
    {
        if (string.IsNullOrWhiteSpace(mapPath))
        {
            return string.Empty;
        }

        var normalized = mapPath.Trim().Trim('"');
        if (System.IO.Path.IsPathRooted(normalized))
        {
            return normalized;
        }

        if (string.IsNullOrWhiteSpace(materialDirectory))
        {
            return normalized;
        }

        return System.IO.Path.Combine(materialDirectory, normalized);
    }

    private static string ExtractMaterialMapPath(string line, string keyword)
    {
        var rest = RestAfterKeyword(line, keyword);
        var tokens = SplitWhitespace(rest);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        var fileTokens = new List<string>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                i += MaterialMapOptionArgumentCount(token, tokens, i + 1);
                continue;
            }

            fileTokens.Add(token);
        }

        return string.Join(' ', fileTokens);
    }

    private static int MaterialMapOptionArgumentCount(string option, string[] tokens, int start)
    {
        var lower = option.ToLowerInvariant();
        if (lower is "-mm")
        {
            return 2;
        }

        if (lower is "-o" or "-s" or "-t")
        {
            var count = 0;
            while (start + count < tokens.Length &&
                   count < 3 &&
                   !tokens[start + count].StartsWith("-", StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        return lower is "-blendu" or "-blendv" or "-boost" or "-bm" or "-cc" or "-clamp" or "-imfchan" or "-texres" or "-type"
            ? 1
            : 0;
    }

    private static Vec3 GuessMaterialColor(string materialName)
    {
        var name = materialName.ToLowerInvariant();
        if (name.Contains("polar", StringComparison.Ordinal) || name.Contains("white", StringComparison.Ordinal))
        {
            return new Vec3(0.86f, 0.88f, 0.84f);
        }

        if (name.Contains("interior", StringComparison.Ordinal))
        {
            return new Vec3(0.045f, 0.044f, 0.040f);
        }

        if (name.Contains("under", StringComparison.Ordinal) || name.Contains("tire", StringComparison.Ordinal) || name.Contains("rubber", StringComparison.Ordinal))
        {
            return new Vec3(0.035f, 0.038f, 0.040f);
        }

        if (name.Contains("glass", StringComparison.Ordinal) || name.Contains("window", StringComparison.Ordinal))
        {
            return new Vec3(0.12f, 0.20f, 0.24f);
        }

        if (name.Contains("color_m02", StringComparison.Ordinal) || name.Contains("chrome", StringComparison.Ordinal))
        {
            return new Vec3(0.52f, 0.56f, 0.54f);
        }

        return DefaultMeshColor;
    }

    private static Vec2 DefaultUv(int index, int count)
    {
        var t = count <= 1 ? 0 : index / (float)(count - 1);
        return new Vec2(t, 1.0f - t);
    }

    private void FinalizeDocument(bool normalize)
    {
        RecomputeBounds();
        if (normalize)
        {
            var scale = Radius <= 0.0001f ? 1.0f : 1.65f / Radius;
            for (var i = 0; i < Positions.Count; i++)
            {
                Positions[i] = (Positions[i] - Center) * scale;
            }
        }

        OriginalPositions.Clear();
        OriginalPositions.AddRange(Positions);
        RecomputeBounds();
        RecomputeNormals();
    }

    public void InvalidateAuthoredNormals()
    {
        UseAuthoredNormals = false;
    }

    public void RecomputeBounds()
    {
        if (Positions.Count == 0 || Triangles.Count == 0)
        {
            if (Positions.Count == 0)
            {
                Center = new Vec3(0, 0, 0);
                Radius = 1.0f;
                return;
            }

            var fallbackMin = Positions[0];
            var fallbackMax = Positions[0];
            foreach (var p in Positions)
            {
                ExpandBounds(p, ref fallbackMin, ref fallbackMax);
            }

            ApplyBounds(fallbackMin, fallbackMax);
            return;
        }

        var first = Positions[Triangles[0].A.PositionIndex];
        var min = first;
        var max = first;
        foreach (var triangle in Triangles)
        {
            ExpandBounds(Positions[triangle.A.PositionIndex], ref min, ref max);
            ExpandBounds(Positions[triangle.B.PositionIndex], ref min, ref max);
            ExpandBounds(Positions[triangle.C.PositionIndex], ref min, ref max);
        }

        ApplyBounds(min, max);
    }

    private void ApplyBounds(Vec3 min, Vec3 max)
    {
        Center = (min + max) * 0.5f;
        Radius = 0.001f;
        if (Triangles.Count == 0)
        {
            foreach (var p in Positions)
            {
                Radius = MathF.Max(Radius, (p - Center).Length);
            }

            return;
        }

        foreach (var triangle in Triangles)
        {
            Radius = MathF.Max(Radius, (Positions[triangle.A.PositionIndex] - Center).Length);
            Radius = MathF.Max(Radius, (Positions[triangle.B.PositionIndex] - Center).Length);
            Radius = MathF.Max(Radius, (Positions[triangle.C.PositionIndex] - Center).Length);
        }
    }

    private static void ExpandBounds(Vec3 point, ref Vec3 min, ref Vec3 max)
    {
        min = new Vec3(MathF.Min(min.X, point.X), MathF.Min(min.Y, point.Y), MathF.Min(min.Z, point.Z));
        max = new Vec3(MathF.Max(max.X, point.X), MathF.Max(max.Y, point.Y), MathF.Max(max.Z, point.Z));
    }

    public void RecomputeNormals()
    {
        Normals.Clear();
        for (var i = 0; i < Positions.Count; i++)
        {
            Normals.Add(new Vec3(0, 0, 0));
        }

        foreach (var triangle in Triangles)
        {
            var a = Positions[triangle.A.PositionIndex];
            var b = Positions[triangle.B.PositionIndex];
            var c = Positions[triangle.C.PositionIndex];
            var normal = Vec3.Cross(b - a, c - a).Normalized();
            Normals[triangle.A.PositionIndex] = Normals[triangle.A.PositionIndex] + normal;
            Normals[triangle.B.PositionIndex] = Normals[triangle.B.PositionIndex] + normal;
            Normals[triangle.C.PositionIndex] = Normals[triangle.C.PositionIndex] + normal;
        }

        for (var i = 0; i < Normals.Count; i++)
        {
            Normals[i] = Normals[i].Normalized();
        }

        RecomputeBounds();
    }
}


public static class MeshSamples
{
    public const string BuiltInCubeObj = """
# Textured cube with explicit UVs
v -1 -1 -1
v  1 -1 -1
v  1  1 -1
v -1  1 -1
v -1 -1  1
v  1 -1  1
v  1  1  1
v -1  1  1
vt 0 0
vt 1 0
vt 1 1
vt 0 1
f 1/1 2/2 3/3 4/4
f 5/1 8/4 7/3 6/2
f 1/1 5/2 6/3 2/4
f 2/1 6/2 7/3 3/4
f 3/1 7/2 8/3 4/4
f 5/1 1/2 4/3 8/4
""";
}
