# MeshParticles.SkiaSharp.Uno

Uno Platform desktop sample for the SkiaSharp v4 `SKMesh` API from PR 3779.

The UI intentionally mirrors `samples/MeshParticles.SkiaNative.Avalonia`: a compact metrics sidebar plus a dark mesh-particle render surface. Rendering uses Uno's `SKCanvasElement` and SkiaSharp PR 3779 APIs:

- `SKMeshSpecification.Make`
- `SKMeshVertexBuffer.Make`
- `SKMeshIndexBuffer.Make`
- `SKMesh.MakeIndexed`
- `SKCanvas.DrawMesh`

The particle buffers are static. Animation is driven by per-frame uniform data consumed by SkSL vertex and fragment shaders.

Current PR 3779 caveat: the sample intentionally does not use a fallback renderer. It submits only the `SKMesh` path every frame. If the render surface stays empty while frame metrics update, capture that as a `DrawMesh` rasterization failure using `plan/SKIASHARP_PR3779_MESH_ISSUE.md`. A minimal offscreen raster probe also produced no pixels with the same PR package build.

## Prerequisites

- .NET SDK `10.0.201` or compatible latest feature roll-forward.
- Uno SDK `6.5.31`, pinned in `global.json`.
- SkiaSharp PR 3779 NuGet artifacts, expected in `~/.skiasharp/hives/pr-3779/packages`.

Fetch the PR artifacts with the SkiaSharp helper script:

```bash
curl -fsSL https://raw.githubusercontent.com/mono/SkiaSharp/main/scripts/get-skiasharp-pr.sh | bash -s -- 3779
```

If the latest PR build has no NuGet artifact, create a local feed from the PR source and the available native macOS artifact:

```bash
./eng/bootstrap-skiasharp-pr3779.sh
```

The bootstrap script writes `SkiaSharp` and `SkiaSharp.NativeAssets.macOS` packages to `~/.skiasharp/hives/pr-3779/packages`. It also patches the current PR source to avoid an `SKPath` lazy-builder finalizer crash observed in `sk_pathbuilder_detach_path`, then clears the matching NuGet cache entries so restore uses the patched packages. If a newer PR build publishes a different package version, pass `-p:SkiaSharpPr3779Version=<version>` to restore/run.

## Run

```bash
dotnet run --project samples/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno.csproj
```

To use a different local PR package folder:

```bash
dotnet run \
  -p:SkiaSharpPr3779Packages=/path/to/pr-3779/packages \
  -p:SkiaSharpPr3779Version=4.147.0-pr.3779.1 \
  --project samples/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno/MeshParticles.SkiaSharp.Uno.csproj
```

## API Usage Guide

The sample uses the PR 3779 mesh API as a programmable Skia draw primitive. Instead of issuing thousands of `DrawCircle` or `DrawPath` calls, the app uploads one static vertex buffer, one static index buffer, and then changes only a tiny uniform block every frame. Skia runs the vertex and fragment SkSL programs for the mesh and the sample submits the whole particle field with one `SKCanvas.DrawMesh` call.

The render flow is:

1. Uno calls `MeshParticlesSurface.RenderOverride(SKCanvas canvas, Size area)`.
2. The control clears the canvas and draws the background grid with normal SkiaSharp drawing APIs.
3. `EnsureMeshResources()` creates the `SKMeshSpecification`, `SKMeshVertexBuffer`, and `SKMeshIndexBuffer` once.
4. Each frame packs time and viewport size into a 16-byte uniform block.
5. `SKMesh.MakeIndexed(...)` creates a frame-local mesh object that references the cached specification and buffers.
6. `canvas.DrawMesh(mesh, _meshPaint)` rasterizes all particles through the SkSL mesh programs.

There is intentionally no fallback renderer. If the grid draws but particles do not, the mesh path is failing.

## Project Setup

The sample is an Uno desktop app using the Skia renderer:

```xml
<Project Sdk="Uno.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-desktop</TargetFrameworks>
    <UnoSingleProject>true</UnoSingleProject>
    <UnoFeatures>
      CSharpMarkup;
      SkiaRenderer;
    </UnoFeatures>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SkiaSharp" />
    <PackageReference Include="SkiaSharp.NativeAssets.macOS" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
  </ItemGroup>
</Project>
```

The package versions are pinned in `Directory.Packages.props`:

```xml
<PackageVersion Include="SkiaSharp" Version="[4.147.0-pr.3779.1]" />
<PackageVersion Include="SkiaSharp.NativeAssets.macOS" Version="[4.147.0-pr.3779.1]" />
```

The PR package feed is added through `SkiaSharpPr3779Packages`, which defaults to `~/.skiasharp/hives/pr-3779/packages`.

## Hosting In Uno

The mesh renderer is a custom Uno Skia surface:

```csharp
public sealed partial class MeshParticlesSurface : SKCanvasElement
{
    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var width = Math.Max(1, (int)MathF.Ceiling((float)area.Width));
        var height = Math.Max(1, (int)MathF.Ceiling((float)area.Height));

        DrawScene(canvas, width, height);
    }
}
```

The sample drives animation by invalidating the surface on Uno's composition frame event:

```csharp
private void OnLoaded(object sender, RoutedEventArgs e)
{
    CompositionTarget.Rendering += OnRendering;
    Invalidate();
}

private void OnRendering(object? sender, object e)
{
    Invalidate();
}
```

This keeps animation tied to the UI/render loop and avoids a separate timer thread.

## Mesh Data Model

Each particle is represented by one quad. The quad has four vertices and six indices:

```text
vertices: 0 = top-left, 1 = top-right, 2 = bottom-right, 3 = bottom-left
indices:  0, 1, 2, 0, 2, 3
```

The sample uses 4,096 particles:

```csharp
private const int ParticleCount = 4096;
private const int VerticesPerParticle = 4;
private const int IndicesPerParticle = 6;
```

The vertex payload is a tightly packed blittable struct:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
private readonly record struct ParticleVertex(
    float LocalX,
    float LocalY,
    float Radius,
    float Angle,
    float Speed,
    float Size,
    float Hue,
    float Alpha,
    float Phase);
```

The stride is `9 * sizeof(float)`, or 36 bytes. Attribute offsets must match the struct exactly:

| Attribute | Type | Offset | Purpose |
| --- | --- | ---: | --- |
| `local` | `Float2` | 0 | Quad-local coordinate from `(-1, -1)` to `(1, 1)` |
| `radius` | `Float` | 8 | Normalized orbital radius |
| `angle` | `Float` | 12 | Initial orbital angle |
| `speed` | `Float` | 16 | Per-particle angular speed |
| `size` | `Float` | 20 | Base particle size in pixels |
| `hue` | `Float` | 24 | Color seed consumed by the fragment shader |
| `alpha` | `Float` | 28 | Particle opacity |
| `phase` | `Float` | 32 | Extra animation phase/noise seed |

The corresponding SkiaSharp attribute declarations are:

```csharp
private static readonly SKMeshSpecificationAttribute[] Attributes =
{
    new(SKMeshSpecificationAttributeType.Float2, 0, "local"),
    new(SKMeshSpecificationAttributeType.Float, 8, "radius"),
    new(SKMeshSpecificationAttributeType.Float, 12, "angle"),
    new(SKMeshSpecificationAttributeType.Float, 16, "speed"),
    new(SKMeshSpecificationAttributeType.Float, 20, "size"),
    new(SKMeshSpecificationAttributeType.Float, 24, "hue"),
    new(SKMeshSpecificationAttributeType.Float, 28, "alpha"),
    new(SKMeshSpecificationAttributeType.Float, 32, "phase"),
};
```

The attribute names are part of the shader contract. `attrs.radius` in SkSL resolves to the C# attribute named `"radius"`.

## Varyings

Varyings are values written by the vertex shader and interpolated into the fragment shader. The sample passes local quad coordinates and color/animation data:

```csharp
private static readonly SKMeshSpecificationVarying[] Varyings =
{
    new(SKMeshSpecificationVaryingType.Float2, "local"),
    new(SKMeshSpecificationVaryingType.Float, "hue"),
    new(SKMeshSpecificationVaryingType.Float, "alpha"),
    new(SKMeshSpecificationVaryingType.Float, "phase"),
};
```

The vertex shader writes these fields:

```c
v.local = attrs.local;
v.hue = attrs.hue + u_time * 0.015;
v.alpha = attrs.alpha * (0.78 + 0.22 * sin(u_time * 1.7 + attrs.phase));
v.phase = attrs.phase + u_time * attrs.speed;
```

The fragment shader then uses them to compute the soft circular particle color and alpha.

## SkSL Shaders

The vertex shader is responsible for particle animation and final screen-space position. It receives `Attributes attrs` and returns `Varyings`:

```c
uniform float4 u_data;

Varyings main(const Attributes attrs) {
    Varyings v;
    float u_time = u_data.x;
    float u_width = u_data.y;
    float u_height = u_data.z;
    float minSide = min(u_width, u_height);
    float2 center = float2(u_width * 0.5, u_height * 0.5);

    float angle = attrs.angle + u_time * attrs.speed;
    float orbit = attrs.radius * minSide * 0.46;
    float2 radial = float2(cos(angle), sin(angle * 0.86 + attrs.phase * 0.11));
    float2 particleCenter = center + radial * orbit;

    v.position = particleCenter + attrs.local * attrs.size;
    v.local = attrs.local;
    v.hue = attrs.hue;
    v.alpha = attrs.alpha;
    v.phase = attrs.phase;
    return v;
}
```

The important field is `v.position`. It is the final device-space position consumed by Skia's mesh rasterizer.

The fragment shader receives interpolated `Varyings` and writes final color:

```c
uniform float4 u_data;

float2 main(const Varyings v, out half4 color) {
    float radius = length(v.local);
    float edge = 1.0 - smoothstep(0.78, 1.0, radius);
    float alpha = clamp(v.alpha * edge, 0.0, 1.0);
    color = half4(1.0, 0.8, 0.2, alpha);
    return v.position;
}
```

The sample uses `smoothstep` against `length(v.local)` to turn a rectangular quad into a soft circular particle. This is why each particle is drawn as a round sprite even though the geometry is only two triangles.

## Creating A Mesh Specification

`SKMeshSpecification` compiles and validates the attribute layout, varyings, vertex shader, fragment shader, color space, and alpha type:

```csharp
using var colorSpace = SKColorSpace.CreateSrgb();

var specification = SKMeshSpecification.Make(
    Attributes,
    9 * sizeof(float),
    Varyings,
    VertexShader,
    FragmentShader,
    colorSpace,
    SKAlphaType.Premul,
    out var errors);

if (specification is null)
{
    throw new InvalidOperationException(errors);
}
```

Create the specification once and reuse it. It is not a per-frame object.

## Creating Vertex And Index Buffers

The PR API accepts raw bytes for buffers. The sample uses `MemoryMarshal.AsBytes` over a blittable array:

```csharp
var vertices = new ParticleVertex[ParticleCount * VerticesPerParticle];
var indices = new ushort[ParticleCount * IndicesPerParticle];

// Fill vertices and indices here.

using var vertexBuffer = SKMeshVertexBuffer.Make(
    MemoryMarshal.AsBytes(vertices.AsSpan()));

using var indexBuffer = SKMeshIndexBuffer.Make(
    MemoryMarshal.AsBytes(indices.AsSpan()));
```

The sample stores the resulting buffers in fields and disposes them when the control unloads:

```csharp
private SKMeshVertexBuffer? _vertexBuffer;
private SKMeshIndexBuffer? _indexBuffer;

private void DisposeMeshResources()
{
    _vertexBuffer?.Dispose();
    _indexBuffer?.Dispose();
    _vertexBuffer = null;
    _indexBuffer = null;
}
```

Use indexed meshes when many primitives share the same topology. For particles, every quad has the same six-index pattern, so indexed drawing is a compact fit.

## Uniform Packing

Uniforms are passed as `SKData`. The sample packs four floats into one `float4`:

```csharp
private readonly float[] _uniformData = new float[4];

_uniformData[0] = time;
_uniformData[1] = width;
_uniformData[2] = height;
_uniformData[3] = 0;

using var uniforms = SKData.CreateCopy(
    MemoryMarshal.AsBytes(_uniformData.AsSpan()));
```

The SkSL side declares the same shape:

```c
uniform float4 u_data;
```

Then reads:

```c
float u_time = u_data.x;
float u_width = u_data.y;
float u_height = u_data.z;
```

Keep uniform data tightly packed and stable. If the SkSL uniform declaration changes, update the C# packing in the same commit.

## Drawing The Mesh

The frame draw path creates an `SKMesh` object over the cached resources and submits it:

```csharp
using var mesh = SKMesh.MakeIndexed(
    specification,
    SKMeshMode.Triangles,
    vertexBuffer,
    vertexCount,
    0,
    indexBuffer,
    indexCount,
    0,
    uniforms,
    new SKRect(0, 0, width, height),
    out var errors);

if (mesh is null || !mesh.IsValid)
{
    throw new InvalidOperationException(errors);
}

using var paint = new SKPaint
{
    IsAntialias = true,
    Color = SKColors.White,
    BlendMode = SKBlendMode.Plus
};

canvas.DrawMesh(mesh, paint);
```

The bounds rectangle should cover the possible rendered mesh area. Skia can use it for culling and validation, so stale or too-small bounds can clip rendering.

## Minimal Triangle Example

This is the smallest useful version of the API without particles:

```csharp
using System.Runtime.InteropServices;
using SkiaSharp;

const string vertexShader = """
    Varyings main(const Attributes attrs) {
        Varyings v;
        v.position = attrs.position;
        return v;
    }
    """;

const string fragmentShader = """
    float2 main(const Varyings v, out half4 color) {
        color = half4(1.0, 0.2, 0.1, 1.0);
        return v.position;
    }
    """;

var attributes = new[]
{
    new SKMeshSpecificationAttribute(SKMeshSpecificationAttributeType.Float2, 0, "position"),
};

using var colorSpace = SKColorSpace.CreateSrgb();
using var specification = SKMeshSpecification.Make(
    attributes,
    2 * sizeof(float),
    Array.Empty<SKMeshSpecificationVarying>(),
    vertexShader,
    fragmentShader,
    colorSpace,
    SKAlphaType.Premul,
    out var specificationErrors);

if (specification is null)
{
    throw new InvalidOperationException(specificationErrors);
}

var vertices = new float[]
{
    20, 20,
    180, 40,
    80, 180,
};

using var vertexBuffer = SKMeshVertexBuffer.Make(
    MemoryMarshal.AsBytes(vertices.AsSpan()));

using var mesh = SKMesh.Make(
    specification,
    SKMeshMode.Triangles,
    vertexBuffer,
    3,
    0,
    new SKRect(0, 0, 200, 200),
    out var meshErrors);

if (mesh is null || !mesh.IsValid)
{
    throw new InvalidOperationException(meshErrors);
}

canvas.DrawMesh(mesh, new SKPaint { Color = SKColors.White });
```

Use `SKMesh.Make` for non-indexed geometry and `SKMesh.MakeIndexed` when you have an index buffer.

## Performance Notes

- Cache `SKMeshSpecification`, `SKMeshVertexBuffer`, and `SKMeshIndexBuffer`; do not recreate them every frame.
- Keep per-frame data small. This sample updates only 16 bytes of uniforms each frame.
- Prefer one `DrawMesh` call over thousands of individual draw calls when geometry can be expressed as a single mesh.
- Use blittable vertex structs and `MemoryMarshal.AsBytes` to avoid per-field marshaling or object conversion.
- Dispose frame-local `SKData` and `SKMesh` objects promptly.
- Use accurate bounds. Oversized bounds can reduce culling efficiency; undersized bounds can clip the mesh.
- Use `SKBlendMode.Plus` or another additive mode only when the visual effect needs it. Normal alpha blending is cheaper and more predictable for UI-like content.

## Troubleshooting

- If `SKMeshSpecification.Make` returns `null`, inspect the returned errors first. Most failures are shader compile errors, mismatched attribute names, invalid varying declarations, or wrong stride.
- If `SKMesh.MakeIndexed` returns `null` or an invalid mesh, verify vertex count, index count, offsets, bounds, and buffer lifetime.
- If the grid renders but particles do not, the sample is still executing the mesh-only path. Capture that as a `DrawMesh` rasterization issue; do not add fallback drawing.
- If the app crashes in `sk_pathbuilder_detach_path`, rebuild the local PR packages with `./eng/bootstrap-skiasharp-pr3779.sh`; the script applies the local `SKPath` finalizer safety patch before packing.
