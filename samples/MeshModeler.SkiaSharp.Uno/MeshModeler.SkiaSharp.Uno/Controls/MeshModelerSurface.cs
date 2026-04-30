using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using MeshModeler.Core;
using MeshModeler.SkiaSharp.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Windows.System;

namespace MeshModeler.SkiaSharp.Uno.Controls;

public readonly record struct MeshModelerStats(
    double FrameMilliseconds,
    double RenderMilliseconds,
    double FramesPerSecond,
    int MeshVertices,
    int MeshTriangles,
    int SubmittedVertices,
    int SubmittedIndices,
    int DrawCalls,
    int UniformBytes,
    int SelectedVertex,
    double CameraYawDegrees,
    double CameraPitchDegrees,
    double CameraDistance,
    string ShadingMode,
    string Backend,
    string Status);

public sealed partial class MeshModelerSurface : SKCanvasElement
{
    private const int MaxSubmittedVertices = 65520;
    private const int MaxSubmittedIndices = 65520;
    private const int UniformFloatCount = 28;
    private const float NearPlane = 0.08f;
    private const int DepthSortedMaterialTriangleLimit = 350_000;
    private const int MaxSplatsPerBatch = MaxSubmittedIndices / 6;
    private const float MinProjectedSplatExtent = 0.35f;
    private const float MinCameraDistance = 0.025f;
    private const float MaxCameraDistance = 500.0f;

    private const string VertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform float4 u_camera0;
        uniform float4 u_camera1;
        uniform float4 u_camera2;
        uniform float4 u_camera3;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float3 rel = attrs.position - u_camera0.xyz;
            float3 right = normalize(u_camera1.xyz);
            float3 up = normalize(u_camera2.xyz);
            float3 forward = normalize(u_camera3.xyz);
            float vx = dot(rel, right);
            float vy = dot(rel, up);
            float vz = max(dot(rel, forward), u_light.w);
            float focal = u_camera0.w + u_texture.x * 0.0;
            v.position = float2(u_view.y * 0.5 + vx / vz * focal, u_camera1.w * 0.5 - vy / vz * focal);
            v.uv = attrs.uv;
            float3 n = normalize(attrs.normal);
            v.normal = normalize(float3(dot(n, right), dot(n, up), dot(n, forward)));
            v.depth = vz;
            v.material = attrs.material;
            v.bary = attrs.bary;
            return v;
        }";

    private const string ProjectedVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform float4 u_camera0;
        uniform float4 u_camera1;
        uniform float4 u_camera2;
        uniform float4 u_camera3;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float keepUniformLayout = (u_camera0.x + u_camera1.x + u_camera2.x + u_camera3.x) * 0.000000000000000001;
            v.position = attrs.position + float2(keepUniformLayout, keepUniformLayout);
            v.uv = attrs.uv;
            v.normal = normalize(attrs.normal);
            v.depth = attrs.depth + u_texture.x * 0.0;
            v.material = attrs.material;
            v.bary = attrs.bary;
            return v;
        }";

    private const string SplatVertexShader = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform float4 u_camera0;
        uniform float4 u_camera1;
        uniform float4 u_camera2;
        uniform float4 u_camera3;

        Varyings main(const Attributes attrs) {
            Varyings v;
            float keepUniformLayout = (u_view.x + u_light.x + u_texture.x + u_camera0.x + u_camera1.x + u_camera2.x + u_camera3.x) * 0.000000000000000001;
            v.position = attrs.position + float2(keepUniformLayout, keepUniformLayout);
            v.local = attrs.local;
            v.color = attrs.color;
            v.alpha = attrs.alpha;
            v.depth = attrs.depth;
            return v;
        }";

    private const string SplatFragmentShader = @"
        float2 main(const Varyings v, out half4 color) {
            float r2 = dot(v.local, v.local);
            float support = 1.0 - smoothstep(0.96, 1.0, r2);
            float alpha = clamp(v.alpha * exp(-4.5 * r2) * support, 0.0, 1.0);
            float halo = exp(-1.6 * r2) * 0.035 * support;
            float3 rgb = clamp(v.color * (alpha + halo), 0.0, 1.0);
            color = half4(half3(rgb), half(alpha));
            return v.position;
        }";

    private const string FragmentShaderColor = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;

        float checker(float2 uv) {
            float2 cell = floor(uv * 10.0);
            float parity = mod(cell.x + cell.y, 2.0);
            return mix(0.35, 1.0, parity);
        }

        float gridLine(float2 uv) {
            float2 g = abs(fract(uv * 10.0) - 0.5);
            float d = min(g.x, g.y);
            return 1.0 - smoothstep(0.020, 0.055, d);
        }

        float2 main(const Varyings v, out half4 color) {
            float mode = u_view.w;
            float3 n = normalize(v.normal);
            float3 light = normalize(u_light.xyz);
            float diffuse = clamp(dot(n, light), 0.0, 1.0);
            float rim = pow(1.0 - clamp(abs(n.z), 0.0, 1.0), 2.0);
            float depth01 = clamp((v.depth - u_light.w) / max(u_view.z - u_light.w, 0.001), 0.0, 1.0);
            float3 rgb;

            if (mode < 0.5) {
                float c = checker(v.uv);
                float line = gridLine(v.uv);
                float uvTint = mix(0.86, 1.05, c);
                rgb = v.material * uvTint * (0.30 + diffuse * 0.90);
                rgb += line * float3(0.06, 0.65, 0.95) * 0.45;
                rgb += rim * (v.material + float3(0.08, 0.18, 0.24)) * 0.32;
            } else if (mode < 1.5) {
                rgb = mix(float3(0.06, 0.14, 0.30), float3(0.95, 0.42, 0.15), depth01);
                rgb *= 0.30 + diffuse * 0.70;
            } else {
                rgb = n * 0.5 + 0.5;
                rgb *= 0.45 + diffuse * 0.55;
            }

            if (u_view.x > 0.5) {
                float wire = 1.0 - smoothstep(0.012, 0.035, min(min(v.bary.x, v.bary.y), v.bary.z));
                rgb = mix(rgb, float3(0.0, 0.0, 0.0), wire * 0.72);
            }

            color = half4(half3(rgb), half(u_texture.w));
            return v.position;
        }";

    private const string FragmentShaderTextured = @"
        uniform float4 u_view;
        uniform float4 u_light;
        uniform float4 u_texture;
        uniform shader diffuseTexture;

        float checker(float2 uv) {
            float2 cell = floor(uv * 10.0);
            float parity = mod(cell.x + cell.y, 2.0);
            return mix(0.35, 1.0, parity);
        }

        float gridLine(float2 uv) {
            float2 g = abs(fract(uv * 10.0) - 0.5);
            float d = min(g.x, g.y);
            return 1.0 - smoothstep(0.020, 0.055, d);
        }

        float2 main(const Varyings v, out half4 color) {
            float mode = u_view.w;
            float3 n = normalize(v.normal);
            float3 light = normalize(u_light.xyz);
            float diffuse = clamp(dot(n, light), 0.0, 1.0);
            float rim = pow(1.0 - clamp(abs(n.z), 0.0, 1.0), 2.0);
            float depth01 = clamp((v.depth - u_light.w) / max(u_view.z - u_light.w, 0.001), 0.0, 1.0);
            float3 rgb;

            if (mode < 0.5) {
                float c = checker(v.uv);
                float line = gridLine(v.uv);
                float uvTint = mix(0.86, 1.05, c);
                float texWeight = 0.0;
                float3 base = v.material;
                if (u_texture.z > 0.5) {
                    half4 tex = diffuseTexture.eval(float2(v.uv.x * u_texture.x, v.uv.y * u_texture.y));
                    texWeight = float(tex.a);
                    base *= float3(tex.r, tex.g, tex.b);
                }

                rgb = base * mix(uvTint, 1.0, texWeight) * (0.30 + diffuse * 0.90);
                rgb += line * float3(0.06, 0.65, 0.95) * (0.45 * (1.0 - texWeight * 0.75));
                rgb += rim * (v.material + float3(0.08, 0.18, 0.24)) * 0.32;
            } else if (mode < 1.5) {
                rgb = mix(float3(0.06, 0.14, 0.30), float3(0.95, 0.42, 0.15), depth01);
                rgb *= 0.30 + diffuse * 0.70;
            } else {
                rgb = n * 0.5 + 0.5;
                rgb *= 0.45 + diffuse * 0.55;
            }

            if (u_view.x > 0.5) {
                float wire = 1.0 - smoothstep(0.012, 0.035, min(min(v.bary.x, v.bary.y), v.bary.z));
                rgb = mix(rgb, float3(0.0, 0.0, 0.0), wire * 0.72);
            }

            color = half4(half3(rgb), half(u_texture.w));
            return v.position;
        }";

    private static readonly SKMeshSpecificationAttribute[] Attributes =
    {
        new(SKMeshSpecificationAttributeType.Float3, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 12, "uv"),
        new(SKMeshSpecificationAttributeType.Float3, 20, "normal"),
        new(SKMeshSpecificationAttributeType.Float3, 32, "material"),
        new(SKMeshSpecificationAttributeType.Float3, 44, "bary"),
    };

    private static readonly SKMeshSpecificationAttribute[] ProjectedAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "uv"),
        new(SKMeshSpecificationAttributeType.Float3, 16, "normal"),
        new(SKMeshSpecificationAttributeType.Float, 28, "depth"),
        new(SKMeshSpecificationAttributeType.Float3, 32, "material"),
        new(SKMeshSpecificationAttributeType.Float3, 44, "bary"),
    };

    private static readonly SKMeshSpecificationVarying[] Varyings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "uv"),
        new(SKMeshSpecificationVaryingType.Float3, "normal"),
        new(SKMeshSpecificationVaryingType.Float, "depth"),
        new(SKMeshSpecificationVaryingType.Float3, "material"),
        new(SKMeshSpecificationVaryingType.Float3, "bary"),
    };

    private static readonly SKMeshSpecificationAttribute[] SplatAttributes =
    {
        new(SKMeshSpecificationAttributeType.Float2, 0, "position"),
        new(SKMeshSpecificationAttributeType.Float2, 8, "local"),
        new(SKMeshSpecificationAttributeType.Float3, 16, "color"),
        new(SKMeshSpecificationAttributeType.Float, 28, "alpha"),
        new(SKMeshSpecificationAttributeType.Float, 32, "depth"),
    };

    private static readonly SKMeshSpecificationVarying[] SplatVaryings =
    {
        new(SKMeshSpecificationVaryingType.Float2, "local"),
        new(SKMeshSpecificationVaryingType.Float3, "color"),
        new(SKMeshSpecificationVaryingType.Float, "alpha"),
        new(SKMeshSpecificationVaryingType.Float, "depth"),
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _renderClock = new();
    private readonly float[] _uniformData = new float[UniformFloatCount];
    private readonly SkiaMeshVertex[] _submittedVertices = new SkiaMeshVertex[MaxSubmittedVertices];
    private readonly SkiaProjectedMeshVertex[] _projectedVertices = new SkiaProjectedMeshVertex[MaxSubmittedVertices];
    private readonly SkiaSplatVertex[] _splatVertices = new SkiaSplatVertex[MaxSubmittedVertices];
    private readonly ushort[] _submittedIndices = new ushort[MaxSubmittedIndices];
    private readonly List<ProjectedMeshTriangle> _projectedTriangles = new(4096);
    private readonly List<ProjectedGaussianSplat> _splatDrawItems = new(16384);
    private readonly List<MeshBatch> _meshBatches = new(16);
    private readonly List<MeshBatch> _projectedMeshBatches = new(16);
    private readonly List<SplatMeshBatch> _splatBatches = new(32);
    private readonly SkiaMeshMaterialResourceCache _materialResources = new();
    private bool _meshBatchesDirty = true;
    private bool _projectedMeshBatchesDirty = true;
    private bool _splatBatchesDirty = true;
    private readonly SKPaint _meshPaint = new() { IsAntialias = true, BlendMode = SKBlendMode.SrcOver, Color = SKColors.White };
    private readonly SKPaint _gridPaint = new() { IsAntialias = true, StrokeWidth = 1, Color = new SKColor(62, 91, 118, 120) };
    private readonly SKPaint _axisXPaint = new() { IsAntialias = true, StrokeWidth = 2, Color = new SKColor(240, 80, 80, 190) };
    private readonly SKPaint _axisYPaint = new() { IsAntialias = true, StrokeWidth = 2, Color = new SKColor(80, 230, 130, 190) };
    private readonly SKPaint _axisZPaint = new() { IsAntialias = true, StrokeWidth = 2, Color = new SKColor(90, 155, 255, 190) };
    private readonly SKPaint _vertexPaint = new() { IsAntialias = true, Color = new SKColor(230, 245, 255, 210) };
    private readonly SKPaint _selectedVertexPaint = new() { IsAntialias = true, Color = new SKColor(0, 255, 210, 255) };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Color = new SKColor(210, 226, 242, 230) };
    private readonly SKFont _textFont = new(SKTypeface.Default, 13);

    private MeshDocument _document = MeshDocument.CreateTorus();
    private GaussianSplatCloud? _splatCloud;
    private SKMeshSpecification? _colorSpecification;
    private SKMeshSpecification? _textureSpecification;
    private SKMeshSpecification? _projectedColorSpecification;
    private SKMeshSpecification? _projectedTextureSpecification;
    private SKMeshSpecification? _splatSpecification;
    private float _yaw = -0.58f;
    private float _pitch = 0.38f;
    private float _distance = 4.6f;
    private Vec3 _target = new(0, 0, 0);
    private CameraBasis _camera;
    private float _mode;
    private float _lastWidth;
    private float _lastHeight;
    private int _submittedVertexCount;
    private int _submittedIndexCount;
    private int _projectedVisibleTriangleCount;
    private int _splatVisibleCount;
    private float _projectedCacheWidth;
    private float _projectedCacheHeight;
    private float _projectedCacheYaw;
    private float _projectedCachePitch;
    private float _projectedCacheDistance;
    private float _projectedCacheTargetX;
    private float _projectedCacheTargetY;
    private float _projectedCacheTargetZ;
    private int _projectedCacheModeBucket;
    private bool _projectedCacheUsesPerMaterialUniforms;
    private float _splatCacheWidth;
    private float _splatCacheHeight;
    private float _splatCacheYaw;
    private float _splatCachePitch;
    private float _splatCacheDistance;
    private float _splatCacheTargetX;
    private float _splatCacheTargetY;
    private float _splatCacheTargetZ;
    private int _drawCalls;
    private int _selectedVertex = -1;
    private bool _editMode;
    private bool _dragging;
    private bool _draggingVertex;
    private PointerAction _pointerAction;
    private Point _lastPointer;
    private double _lastFrameSeconds;
    private double _lastStatsSeconds;
    private double _framesPerSecond;
    private int _frameCount;
    private bool _showVertexHandles;
    private bool _showMeshGrid;
    private string _status = "Waiting for first render.";

    public MeshModelerSurface()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsTabStop = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
    }

    public event EventHandler<MeshModelerStats>? StatsUpdated;

    public void LoadSampleTorus()
    {
        ReplaceDocument(MeshDocument.CreateTorus());
        ResetView();
        _status = "Loaded procedural torus sample.";
        Invalidate();
    }

    public void LoadSampleCube()
    {
        ReplaceDocument(MeshDocument.ParseObj(MeshSamples.BuiltInCubeObj, "Textured cube OBJ"));
        ResetView();
        _status = "Loaded built-in textured cube OBJ.";
        Invalidate();
    }

    public void LoadSampleSplats()
    {
        ReplaceSplatCloud(GaussianSplatCloud.CreateSample());
        ResetView();
        _status = BuildSplatLoadStatus(_splatCloud!.Name);
        Invalidate();
    }

    public void LoadObjText(string objText, string name)
    {
        ReplaceDocument(MeshDocument.ParseObj(objText, name));
        ResetView();
        _status = BuildLoadStatus(name);
        Invalidate();
    }

    public void LoadObjFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var baseDirectory = System.IO.Path.GetDirectoryName(path);
        ReplaceDocument(MeshDocument.ParseObj(File.ReadAllText(path), name, baseDirectory));
        ResetView();
        _status = BuildLoadStatus(name);
        Invalidate();
    }

    public void LoadGaussianSplatFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        ReplaceSplatCloud(GaussianSplatCloud.Load(path, name, SkiaSogImageDecoder.Shared));
        ResetView();
        _status = BuildSplatLoadStatus(name);
        Invalidate();
    }

    public void ToggleEditMode()
    {
        if (_splatCloud is not null)
        {
            _editMode = false;
            _selectedVertex = -1;
            _status = "Edit mode is disabled for Gaussian splat clouds; splats are rendered as oriented density kernels rather than editable mesh vertices.";
            Invalidate();
            return;
        }

        _editMode = !_editMode;
        _status = _editMode
            ? "Edit mode enabled. Click a projected vertex, then drag it in the camera plane."
            : "Edit mode disabled. Left drag orbits the camera.";
        Invalidate();
    }

    public void ToggleVertexHandles()
    {
        _showVertexHandles = !_showVertexHandles;
        _status = _showVertexHandles
            ? "Vertex handles visible. Press H or Handles to hide them."
            : "Vertex handles hidden. Edit mode still shows them for picking.";
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    public void ToggleMeshGrid()
    {
        _showMeshGrid = !_showMeshGrid;
        _status = _showMeshGrid
            ? "Mesh grid overlay visible. Press G or Mesh Grid to hide it."
            : "Mesh grid overlay hidden for material rendering.";
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    public void SetShadingMode(int mode)
    {
        _mode = Math.Clamp(mode, 0, 2);
        _status = _mode switch
        {
            < 0.5f => "Shading: OBJ material/texture child shader + UV checker + Lambert lighting.",
            < 1.5f => "Shading: depth visualization.",
            _ => "Shading: normal visualization."
        };
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    private string BuildLoadStatus(string name)
    {
        var normalText = _document.UseAuthoredNormals ? "authored normals" : "computed normals";
        var materialText = _document.MaterialCount == 1 ? "1 material" : $"{_document.MaterialCount:N0} materials";
        var skippedText = _document.SkippedFaceCount > 0
            ? $" Skipped {_document.SkippedFaceCount:N0} non-triangulatable face record{(_document.SkippedFaceCount == 1 ? string.Empty : "s")}."
            : string.Empty;
        var helperText = _document.SkippedSceneHelperFaceCount > 0
            ? $" Ignored {_document.SkippedSceneHelperFaceCount:N0} Blender scene-helper face record{(_document.SkippedSceneHelperFaceCount == 1 ? string.Empty : "s")}."
            : string.Empty;

        var textureText = _document.TextureMaterialCount == 1 ? "1 textured material" : $"{_document.TextureMaterialCount:N0} textured materials";
        return $"Loaded OBJ '{name}' with {_document.Positions.Count:N0} vertices, {_document.Triangles.Count:N0} triangles, {materialText}, {textureText}, and {normalText}.{skippedText}{helperText}";
    }

    private string BuildSplatLoadStatus(string name)
    {
        if (_splatCloud is null)
        {
            return $"Loaded Gaussian splat file '{name}'.";
        }

        var format = _splatCloud.SourceFormat.Length == 0 ? "Gaussian splat" : _splatCloud.SourceFormat;
        var loadedText = _splatCloud.SourceSplatCount == _splatCloud.Splats.Length
            ? $"{_splatCloud.Splats.Length:N0}"
            : $"{_splatCloud.Splats.Length:N0}/{_splatCloud.SourceSplatCount:N0}";
        return $"Loaded Gaussian splat file '{name}' with {loadedText} splats from {format}; rendering cached anisotropic SKMesh quad batches.";
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        _lastWidth = Math.Max(1, (float)area.Width);
        _lastHeight = Math.Max(1, (float)area.Height);
        _renderClock.Restart();
        DrawScene(canvas, _lastWidth, _lastHeight);
        _renderClock.Stop();
        PublishStats(_renderClock.Elapsed.TotalMilliseconds);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnRendering;
        Focus(FocusState.Programmatic);
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _colorSpecification?.Dispose();
        _textureSpecification?.Dispose();
        _projectedColorSpecification?.Dispose();
        _projectedTextureSpecification?.Dispose();
        _splatSpecification?.Dispose();
        _colorSpecification = null;
        _textureSpecification = null;
        _projectedColorSpecification = null;
        _projectedTextureSpecification = null;
        _splatSpecification = null;
        DisposeMeshBatches();
        DisposeProjectedMeshBatches();
        DisposeSplatBatches();
        _materialResources.Clear();
        _document.Dispose();
    }

    private void OnRendering(object? sender, object e) => Invalidate();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        CapturePointer(e.Pointer);
        _dragging = true;
        _lastPointer = e.GetCurrentPoint(this).Position;
        var props = e.GetCurrentPoint(this).Properties;

        if (_editMode && props.IsLeftButtonPressed)
        {
            _selectedVertex = FindNearestVertex((float)_lastPointer.X, (float)_lastPointer.Y, _lastWidth, _lastHeight, maxDistance: 18.0f);
            _draggingVertex = _selectedVertex >= 0;
            _pointerAction = _draggingVertex ? PointerAction.EditVertex : PointerAction.Orbit;
            _status = _draggingVertex
                ? $"Editing vertex {_selectedVertex}. Drag in the camera plane."
                : "No vertex under pointer. Left drag orbits.";
        }
        else if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            _pointerAction = PointerAction.Pan;
        }
        else
        {
            _pointerAction = PointerAction.Orbit;
        }

        Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Position;
        var dx = (float)(point.X - _lastPointer.X);
        var dy = (float)(point.Y - _lastPointer.Y);
        _lastPointer = point;

        switch (_pointerAction)
        {
            case PointerAction.Orbit:
                _yaw += dx * 0.008f;
                _pitch = Math.Clamp(_pitch + dy * 0.006f, -1.35f, 1.35f);
                _status = "Orbit camera: left drag. Pan: right/middle drag. Zoom: wheel.";
                break;
            case PointerAction.Pan:
                PanCamera(dx, dy);
                _status = "Panning camera target.";
                break;
            case PointerAction.EditVertex:
                MoveSelectedVertex(dx, dy);
                _status = $"Editing vertex {_selectedVertex}.";
                break;
        }

        Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _draggingVertex = false;
        _pointerAction = PointerAction.None;
        ReleasePointerCapture(e.Pointer);
        if (_splatCloud is not null)
        {
            _splatBatchesDirty = true;
            Invalidate();
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 0.88f : 1.13f;
        _distance = Math.Clamp(_distance * factor, MinZoomDistance, MaxZoomDistance);
        _status = $"Zoom {_distance:F2}.";
        e.Handled = true;
        Invalidate();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.E:
                ToggleEditMode();
                e.Handled = true;
                break;
            case VirtualKey.H:
                ToggleVertexHandles();
                e.Handled = true;
                break;
            case VirtualKey.G:
            case VirtualKey.W:
                ToggleMeshGrid();
                e.Handled = true;
                break;
            case VirtualKey.Number1:
                _mode = 0;
                _status = "Shading: OBJ material/texture child shader + UV checker + Lambert lighting.";
                e.Handled = true;
                break;
            case VirtualKey.Number2:
                _mode = 1;
                _status = "Shading: depth visualization.";
                e.Handled = true;
                break;
            case VirtualKey.Number3:
                _mode = 2;
                _status = "Shading: normal visualization.";
                e.Handled = true;
                break;
            case VirtualKey.F:
            case VirtualKey.R:
                ResetView();
                _status = "View reset to model bounds.";
                e.Handled = true;
                break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                if (_splatCloud is null && _selectedVertex >= 0)
                {
                    _document.Positions[_selectedVertex] = _document.OriginalPositions[_selectedVertex];
                    _document.RecomputeNormals();
                    _meshBatchesDirty = true;
                    _projectedMeshBatchesDirty = true;
                    _status = $"Reset vertex {_selectedVertex} to original position.";
                    e.Handled = true;
                }

                break;
        }
    }

    private void ReplaceDocument(MeshDocument document)
    {
        var old = _document;
        _document = document;
        _splatCloud = null;
        _meshBatchesDirty = true;
        _projectedMeshBatchesDirty = true;
        _splatBatchesDirty = true;
        _selectedVertex = -1;
        DisposeMeshBatches();
        DisposeProjectedMeshBatches();
        DisposeSplatBatches();
        _materialResources.Clear();
        old.Dispose();
    }

    private void ReplaceSplatCloud(GaussianSplatCloud cloud)
    {
        _splatCloud = cloud;
        _meshBatchesDirty = true;
        _projectedMeshBatchesDirty = true;
        _splatBatchesDirty = true;
        _selectedVertex = -1;
        _editMode = false;
        DisposeMeshBatches();
        DisposeProjectedMeshBatches();
        DisposeSplatBatches();
    }

    private void DrawScene(SKCanvas canvas, float width, float height)
    {
        EnsureSpecs();
        canvas.Clear(new SKColor(5, 9, 15));
        _camera = BuildCameraBasis();
        DrawReferenceGrid(canvas, width, height);
        if (_splatCloud is not null)
        {
            DrawGaussianSplats(canvas, width, height);
        }
        else if (ShouldUseDepthSortedProjectedPath)
        {
            DrawDepthSortedSubmittedMesh(canvas, width, height);
        }
        else
        {
            BuildSubmittedMesh(width, height);
            DrawSubmittedMesh(canvas, width, height);
        }

        if (_showVertexHandles || _editMode || _selectedVertex >= 0)
        {
            DrawVertexHandles(canvas, width, height);
        }

        DrawOverlay(canvas, width, height);
    }

    private bool ShouldUseDepthSortedProjectedPath
    {
        get
        {
            if (_mode >= 0.5f || _document.RequiresDepthSortedRendering)
            {
                return true;
            }

            return _document.Triangles.Count <= DepthSortedMaterialTriangleLimit;
        }
    }

    private bool IsLargeModelFastMaterialPath =>
        _mode < 0.5f &&
        !_document.RequiresDepthSortedRendering &&
        _document.Triangles.Count > DepthSortedMaterialTriangleLimit;

    private void EnsureSpecs()
    {
        if (_colorSpecification is not null &&
            _textureSpecification is not null &&
            _projectedColorSpecification is not null &&
            _projectedTextureSpecification is not null &&
            _splatSpecification is not null)
        {
            return;
        }

        using var colorSpace = SKColorSpace.CreateSrgb();
        if (_colorSpecification is null)
        {
            _colorSpecification = SKMeshSpecification.Make(
                Attributes,
                SkiaMeshVertexLayout.MeshVertexStride,
                Varyings,
                VertexShader,
                FragmentShaderColor,
                colorSpace,
                SKAlphaType.Premul,
                out var colorErrors);

            if (_colorSpecification is null)
            {
                _status = string.IsNullOrWhiteSpace(colorErrors) ? "Color SKMeshSpecification.Make failed." : colorErrors;
            }
        }

        if (_textureSpecification is null)
        {
            _textureSpecification = SKMeshSpecification.Make(
                Attributes,
                SkiaMeshVertexLayout.MeshVertexStride,
                Varyings,
                VertexShader,
                FragmentShaderTextured,
                colorSpace,
                SKAlphaType.Premul,
                out var textureErrors);

            if (_textureSpecification is null && _colorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(textureErrors) ? "Textured SKMeshSpecification.Make failed." : textureErrors;
            }
        }

        if (_projectedColorSpecification is null)
        {
            _projectedColorSpecification = SKMeshSpecification.Make(
                ProjectedAttributes,
                SkiaMeshVertexLayout.MeshVertexStride,
                Varyings,
                ProjectedVertexShader,
                FragmentShaderColor,
                colorSpace,
                SKAlphaType.Premul,
                out var projectedColorErrors);

            if (_projectedColorSpecification is null && _colorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(projectedColorErrors)
                    ? "Projected color SKMeshSpecification.Make failed."
                    : projectedColorErrors;
            }
        }

        if (_projectedTextureSpecification is null)
        {
            _projectedTextureSpecification = SKMeshSpecification.Make(
                ProjectedAttributes,
                SkiaMeshVertexLayout.MeshVertexStride,
                Varyings,
                ProjectedVertexShader,
                FragmentShaderTextured,
                colorSpace,
                SKAlphaType.Premul,
                out var projectedTextureErrors);

            if (_projectedTextureSpecification is null && _projectedColorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(projectedTextureErrors)
                    ? "Projected textured SKMeshSpecification.Make failed."
                    : projectedTextureErrors;
            }
        }

        if (_splatSpecification is null)
        {
            _splatSpecification = SKMeshSpecification.Make(
                SplatAttributes,
                SkiaMeshVertexLayout.SplatVertexStride,
                SplatVaryings,
                SplatVertexShader,
                SplatFragmentShader,
                colorSpace,
                SKAlphaType.Premul,
                out var splatErrors);

            if (_splatSpecification is null && _projectedColorSpecification is not null)
            {
                _status = string.IsNullOrWhiteSpace(splatErrors)
                    ? "Gaussian splat SKMeshSpecification.Make failed."
                    : splatErrors;
            }
        }
    }

    private void BuildSubmittedMesh(float width, float height)
    {
        if (!_meshBatchesDirty)
        {
            return;
        }

        DisposeMeshBatches();
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;

        var batchVertexCount = 0;
        var batchIndexCount = 0;
        var batchMaterialIndex = -1;

        foreach (var triangle in _document.Triangles)
        {
            if (batchVertexCount > 0 &&
                (triangle.MaterialIndex != batchMaterialIndex ||
                 batchVertexCount + 3 > MaxSubmittedVertices ||
                 batchIndexCount + 3 > MaxSubmittedIndices))
            {
                AddMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
                batchVertexCount = 0;
                batchIndexCount = 0;
            }

            if (batchVertexCount == 0)
            {
                batchMaterialIndex = triangle.MaterialIndex;
            }

            AppendTriangleToBatch(triangle, ref batchVertexCount, ref batchIndexCount);
        }

        if (batchVertexCount > 0)
        {
            AddMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
        }

        _meshBatchesDirty = false;
    }

    private void DrawSubmittedMesh(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;

        if (_colorSpecification is null || _meshBatches.Count == 0)
        {
            return;
        }

        foreach (var batch in _meshBatches)
        {
            if (!DrawMeshBatch(canvas, width, height, batch))
            {
                return;
            }
        }

        var fastPathText = IsLargeModelFastMaterialPath
            ? $" Large-model fast path is active over the {DepthSortedMaterialTriangleLimit:N0}-triangle projected-sort budget; material/source order is used instead of CPU depth sorting."
            : string.Empty;
        _status = $"Rendered {_document.Triangles.Count:N0} OBJ triangles through {_drawCalls:N0} cached world-space material SKMesh batch{(_drawCalls == 1 ? string.Empty : "es")}.{fastPathText}";
    }

    private void DrawGaussianSplats(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;

        var cloud = _splatCloud;
        if (cloud is null || _splatSpecification is null)
        {
            return;
        }

        if (!EnsureSplatBatches(width, height))
        {
            return;
        }

        foreach (var batch in _splatBatches)
        {
            canvas.DrawMesh(batch.Mesh, _meshPaint);
            _drawCalls++;
            _submittedVertexCount += batch.VertexCount;
            _submittedIndexCount += batch.IndexCount;
        }

        var sourceText = cloud.SourceSplatCount == cloud.Splats.Length
            ? $"{cloud.Splats.Length:N0}"
            : $"{cloud.Splats.Length:N0}/{cloud.SourceSplatCount:N0} loaded";
        _status = $"Rendered {sourceText} Gaussian splats (all {_splatVisibleCount:N0} visible splats, cached) through {_drawCalls:N0} SKMesh quad batch{(_drawCalls == 1 ? string.Empty : "es")}.";
    }

    private bool EnsureSplatBatches(float width, float height)
    {
        if (SplatBatchCacheMatches(width, height))
        {
            return _splatBatches.Count > 0;
        }

        DisposeSplatBatches();
        _splatDrawItems.Clear();
        _splatVisibleCount = 0;

        var cloud = _splatCloud;
        if (cloud is null || _splatSpecification is null)
        {
            return false;
        }

        for (var i = 0; i < cloud.Splats.Length; i++)
        {
            if (TryProjectSplat(cloud.Splats[i], width, height, out var item))
            {
                _splatDrawItems.Add(item);
            }
        }

        _splatVisibleCount = _splatDrawItems.Count;
        if (_splatDrawItems.Count == 0)
        {
            _status = "No visible Gaussian splats after camera near-plane and viewport culling.";
            _splatBatchesDirty = false;
            StoreSplatBatchCacheKey(width, height);
            return false;
        }

        _splatDrawItems.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        var batchVertexCount = 0;
        var batchIndexCount = 0;
        foreach (var item in _splatDrawItems)
        {
            if (batchVertexCount > 0 &&
                (batchVertexCount + 4 > MaxSubmittedVertices || batchIndexCount + 6 > MaxSubmittedIndices))
            {
                if (!AddSplatBatch(width, height, batchVertexCount, batchIndexCount))
                {
                    _splatDrawItems.Clear();
                    return false;
                }

                batchVertexCount = 0;
                batchIndexCount = 0;
            }

            AppendSplatToBatch(item, ref batchVertexCount, ref batchIndexCount);
        }

        if (batchVertexCount > 0 && !AddSplatBatch(width, height, batchVertexCount, batchIndexCount))
        {
            _splatDrawItems.Clear();
            return false;
        }

        _splatDrawItems.Clear();
        if (_splatDrawItems.Capacity > 262_144)
        {
            _splatDrawItems.TrimExcess();
        }

        _splatBatchesDirty = false;
        StoreSplatBatchCacheKey(width, height);
        return _splatBatches.Count > 0;
    }

    private bool SplatBatchCacheMatches(float width, float height)
    {
        const float epsilon = 0.0001f;
        return !_splatBatchesDirty &&
               MathF.Abs(_splatCacheWidth - width) < 0.5f &&
               MathF.Abs(_splatCacheHeight - height) < 0.5f &&
               MathF.Abs(_splatCacheYaw - _yaw) < epsilon &&
               MathF.Abs(_splatCachePitch - _pitch) < epsilon &&
               MathF.Abs(_splatCacheDistance - _distance) < epsilon &&
               MathF.Abs(_splatCacheTargetX - _target.X) < epsilon &&
               MathF.Abs(_splatCacheTargetY - _target.Y) < epsilon &&
               MathF.Abs(_splatCacheTargetZ - _target.Z) < epsilon;
    }

    private void StoreSplatBatchCacheKey(float width, float height)
    {
        _splatCacheWidth = width;
        _splatCacheHeight = height;
        _splatCacheYaw = _yaw;
        _splatCachePitch = _pitch;
        _splatCacheDistance = _distance;
        _splatCacheTargetX = _target.X;
        _splatCacheTargetY = _target.Y;
        _splatCacheTargetZ = _target.Z;
    }

    private bool TryProjectSplat(GaussianSplat splat, float width, float height, out ProjectedGaussianSplat item)
        => GaussianSplatProjection.TryProject(
            splat,
            _camera,
            width,
            height,
            FocalLength(width, height),
            NearPlane,
            MinProjectedSplatExtent,
            MathF.Min(width, height) * 0.22f,
            out item);

    private void AppendSplatToBatch(ProjectedGaussianSplat item, ref int batchVertexCount, ref int batchIndexCount)
    {
        var baseIndex = (ushort)batchVertexCount;
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, -1, -1);
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, 1, -1);
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, 1, 1);
        _splatVertices[batchVertexCount++] = CreateSplatVertex(item, -1, 1);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 1);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 3);
    }

    private static SkiaSplatVertex CreateSplatVertex(ProjectedGaussianSplat item, float localX, float localY) =>
        new(
            item.CenterX + localX * item.AxisAX + localY * item.AxisBX,
            item.CenterY + localX * item.AxisAY + localY * item.AxisBY,
            localX,
            localY,
            item.ColorR,
            item.ColorG,
            item.ColorB,
            item.Alpha,
            item.Depth);

    private bool AddSplatBatch(float width, float height, int vertexCount, int indexCount)
    {
        if (_splatSpecification is null)
        {
            _status = "Gaussian splat SKMeshSpecification.Make failed; cannot render splats.";
            return false;
        }

        var material = _document.GetMaterial(0);
        FillUniforms(width, height, material, _materialResources.Get(material));
        var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        var vertices = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_splatVertices.AsSpan(0, vertexCount)));
        var indices = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, indexCount)));
        var mesh = SKMesh.MakeIndexed(
            _splatSpecification,
            SKMeshMode.Triangles,
            vertices,
            vertexCount,
            0,
            indices,
            indexCount,
            0,
            uniforms,
            new SKRect(-64, -64, width + 64, height + 64),
            out var errors);

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Gaussian splat SKMesh.MakeIndexed failed." : errors;
            mesh?.Dispose();
            vertices.Dispose();
            indices.Dispose();
            uniforms.Dispose();
            return false;
        }

        _splatBatches.Add(new SplatMeshBatch(vertexCount, indexCount, vertices, indices, uniforms, mesh));
        return true;
    }

    private void DrawDepthSortedSubmittedMesh(SKCanvas canvas, float width, float height)
    {
        _drawCalls = 0;
        _submittedVertexCount = 0;
        _submittedIndexCount = 0;

        if (!EnsureDepthSortedProjectedMeshBatches(width, height))
        {
            return;
        }

        foreach (var batch in _projectedMeshBatches)
        {
            if (!DrawProjectedMeshBatch(canvas, width, height, batch))
            {
                return;
            }
        }

        var renderedText = _projectedVisibleTriangleCount == _document.Triangles.Count
            ? $"{_document.Triangles.Count:N0}"
            : $"{_projectedVisibleTriangleCount:N0}/{_document.Triangles.Count:N0} visible";
        _status = $"Rendered {renderedText} triangles through {_drawCalls:N0} depth-sorted projected SKMesh batch{(_drawCalls == 1 ? string.Empty : "es")}.";
    }

    private bool EnsureDepthSortedProjectedMeshBatches(float width, float height)
    {
        if (ProjectedMeshBatchCacheMatches(width, height))
        {
            return _projectedMeshBatches.Count > 0;
        }

        DisposeProjectedMeshBatches();
        _projectedVisibleTriangleCount = 0;
        _projectedTriangles.Clear();

        foreach (var triangle in _document.Triangles)
        {
            if (TryProjectTriangle(triangle, width, height, out var projected))
            {
                _projectedTriangles.Add(projected);
            }
        }

        if (_projectedTriangles.Count == 0)
        {
            _status = "No visible mesh triangles after camera near-plane and viewport culling.";
            _projectedMeshBatchesDirty = false;
            StoreProjectedMeshBatchCacheKey(width, height);
            return false;
        }

        _projectedTriangles.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
        _projectedVisibleTriangleCount = _projectedTriangles.Count;

        var batchVertexCount = 0;
        var batchIndexCount = 0;
        var batchMaterialIndex = -1;

        foreach (var triangle in _projectedTriangles)
        {
            if (batchVertexCount > 0 &&
                (triangle.MaterialIndex != batchMaterialIndex ||
                 batchVertexCount + 3 > MaxSubmittedVertices ||
                 batchIndexCount + 3 > MaxSubmittedIndices))
            {
                AddProjectedMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
                batchVertexCount = 0;
                batchIndexCount = 0;
            }

            if (batchVertexCount == 0)
            {
                batchMaterialIndex = triangle.MaterialIndex;
            }

            AppendProjectedTriangleToBatch(triangle, ref batchVertexCount, ref batchIndexCount);
        }

        if (batchVertexCount > 0)
        {
            AddProjectedMeshBatch(batchVertexCount, batchIndexCount, batchMaterialIndex);
        }

        _projectedTriangles.Clear();
        StoreProjectedMeshBatchCacheKey(width, height);
        _projectedMeshBatchesDirty = false;
        return _projectedMeshBatches.Count > 0;
    }

    private bool ProjectedPathUsesPerMaterialUniforms =>
        _mode < 0.5f && (_document.TextureMaterialCount > 0 || _document.RequiresDepthSortedRendering);

    private int ProjectedCacheModeBucket => _mode < 0.5f ? 0 : 1;

    private bool ProjectedMeshBatchCacheMatches(float width, float height)
    {
        const float epsilon = 0.0001f;
        return !_projectedMeshBatchesDirty &&
               MathF.Abs(_projectedCacheWidth - width) < 0.5f &&
               MathF.Abs(_projectedCacheHeight - height) < 0.5f &&
               MathF.Abs(_projectedCacheYaw - _yaw) < epsilon &&
               MathF.Abs(_projectedCachePitch - _pitch) < epsilon &&
               MathF.Abs(_projectedCacheDistance - _distance) < epsilon &&
               MathF.Abs(_projectedCacheTargetX - _target.X) < epsilon &&
               MathF.Abs(_projectedCacheTargetY - _target.Y) < epsilon &&
               MathF.Abs(_projectedCacheTargetZ - _target.Z) < epsilon &&
               _projectedCacheModeBucket == ProjectedCacheModeBucket &&
               _projectedCacheUsesPerMaterialUniforms == ProjectedPathUsesPerMaterialUniforms;
    }

    private void StoreProjectedMeshBatchCacheKey(float width, float height)
    {
        _projectedCacheWidth = width;
        _projectedCacheHeight = height;
        _projectedCacheYaw = _yaw;
        _projectedCachePitch = _pitch;
        _projectedCacheDistance = _distance;
        _projectedCacheTargetX = _target.X;
        _projectedCacheTargetY = _target.Y;
        _projectedCacheTargetZ = _target.Z;
        _projectedCacheModeBucket = ProjectedCacheModeBucket;
        _projectedCacheUsesPerMaterialUniforms = ProjectedPathUsesPerMaterialUniforms;
    }

    private bool TryProjectTriangle(Triangle triangle, float width, float height, out ProjectedMeshTriangle projected)
        => MeshProjection.TryProjectTriangle(
            triangle,
            _document,
            _camera,
            width,
            height,
            FocalLength(width, height),
            NearPlane,
            ProjectedPathUsesPerMaterialUniforms,
            out projected);

    private void AppendProjectedTriangleToBatch(ProjectedMeshTriangle triangle, ref int batchVertexCount, ref int batchIndexCount)
    {
        var baseIndex = (ushort)batchVertexCount;

        _projectedVertices[batchVertexCount++] = CreateProjectedMeshVertex(triangle.A, 1, 0, 0);
        _projectedVertices[batchVertexCount++] = CreateProjectedMeshVertex(triangle.B, 0, 1, 0);
        _projectedVertices[batchVertexCount++] = CreateProjectedMeshVertex(triangle.C, 0, 0, 1);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 1);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
    }

    private static SkiaProjectedMeshVertex CreateProjectedMeshVertex(ProjectedMeshCorner corner, float bx, float by, float bz)
        => new(
            corner.X,
            corner.Y,
            corner.U,
            corner.V,
            corner.Nx,
            corner.Ny,
            corner.Nz,
            corner.Depth,
            corner.MaterialR,
            corner.MaterialG,
            corner.MaterialB,
            bx,
            by,
            bz);

    private void AddProjectedMeshBatch(int vertexCount, int indexCount, int materialIndex)
    {
        var vertices = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_projectedVertices.AsSpan(0, vertexCount)));
        var indices = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, indexCount)));
        _projectedMeshBatches.Add(new MeshBatch(materialIndex, vertexCount, indexCount, vertices, indices));
    }

    private bool DrawProjectedMeshBatch(SKCanvas canvas, float width, float height, MeshBatch batch)
    {
        var material = _document.GetMaterial(batch.MaterialIndex);
        var materialResources = _materialResources.Get(material);
        var hasDiffuseTexture = materialResources.HasDiffuseTexture;
        var specification = hasDiffuseTexture && _mode < 0.5f
            ? _projectedTextureSpecification
            : _projectedColorSpecification;
        if (specification is null)
        {
            _status = hasDiffuseTexture
                ? "Projected textured SKMeshSpecification.Make failed; cannot render material."
                : "Projected color SKMeshSpecification.Make failed; cannot render material.";
            return false;
        }

        FillUniforms(width, height, material, materialResources);
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        var bounds = new SKRect(-64, -64, width + 64, height + 64);
        string errors;
        SKMesh? mesh;
        if (hasDiffuseTexture && _mode < 0.5f)
        {
            var children = materialResources.Children;
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                children,
                bounds,
                out errors);
        }
        else
        {
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                bounds,
                out errors);
        }

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "Projected SKMesh.MakeIndexed failed." : errors;
            return false;
        }

        using (mesh)
        {
            canvas.DrawMesh(mesh, _meshPaint);
            _drawCalls++;
            _submittedVertexCount += batch.VertexCount;
            _submittedIndexCount += batch.IndexCount;
        }

        return true;
    }

    private void AppendTriangleToBatch(Triangle triangle, ref int batchVertexCount, ref int batchIndexCount)
    {
        var baseIndex = (ushort)batchVertexCount;

        _submittedVertices[batchVertexCount++] = CreateMeshVertex(triangle.A, triangle.MaterialColor, 1, 0, 0);
        _submittedVertices[batchVertexCount++] = CreateMeshVertex(triangle.B, triangle.MaterialColor, 0, 1, 0);
        _submittedVertices[batchVertexCount++] = CreateMeshVertex(triangle.C, triangle.MaterialColor, 0, 0, 1);
        _submittedIndices[batchIndexCount++] = baseIndex;
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 1);
        _submittedIndices[batchIndexCount++] = (ushort)(baseIndex + 2);
    }

    private SkiaMeshVertex CreateMeshVertex(Corner corner, Vec3 materialColor, float bx, float by, float bz)
    {
        var position = _document.Positions[corner.PositionIndex];
        var normal = corner.HasNormal && _document.UseAuthoredNormals
            ? corner.Normal
            : _document.Normals[corner.PositionIndex];
        return new SkiaMeshVertex(
            position.X,
            position.Y,
            position.Z,
            corner.U,
            corner.V,
            normal.X,
            normal.Y,
            normal.Z,
            materialColor.X,
            materialColor.Y,
            materialColor.Z,
            bx,
            by,
            bz);
    }

    private void AddMeshBatch(int vertexCount, int indexCount, int materialIndex)
    {
        var vertices = SKMeshVertexBuffer.Make(MemoryMarshal.AsBytes(_submittedVertices.AsSpan(0, vertexCount)));
        var indices = SKMeshIndexBuffer.Make(MemoryMarshal.AsBytes(_submittedIndices.AsSpan(0, indexCount)));
        _meshBatches.Add(new MeshBatch(materialIndex, vertexCount, indexCount, vertices, indices));
        _submittedVertexCount += vertexCount;
        _submittedIndexCount += indexCount;
    }

    private bool DrawMeshBatch(SKCanvas canvas, float width, float height, MeshBatch batch)
    {
        var material = _document.GetMaterial(batch.MaterialIndex);
        var materialResources = _materialResources.Get(material);
        var hasDiffuseTexture = materialResources.HasDiffuseTexture;
        var specification = hasDiffuseTexture ? _textureSpecification : _colorSpecification;
        if (specification is null)
        {
            _status = hasDiffuseTexture
                ? "Textured SKMeshSpecification.Make failed; cannot render textured material."
                : "Color SKMeshSpecification.Make failed; cannot render material.";
            return false;
        }

        FillUniforms(width, height, material, materialResources);
        using var uniforms = SKData.CreateCopy(MemoryMarshal.AsBytes(_uniformData.AsSpan()));
        var bounds = new SKRect(-64, -64, width + 64, height + 64);
        string errors;
        SKMesh? mesh;
        if (hasDiffuseTexture)
        {
            var children = materialResources.Children;
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                children,
                bounds,
                out errors);
        }
        else
        {
            mesh = SKMesh.MakeIndexed(
                specification,
                SKMeshMode.Triangles,
                batch.VertexBuffer,
                batch.VertexCount,
                0,
                batch.IndexBuffer,
                batch.IndexCount,
                0,
                uniforms,
                bounds,
                out errors);
        }

        if (mesh is null || !mesh.IsValid)
        {
            _status = string.IsNullOrWhiteSpace(errors) ? "SKMesh.MakeIndexed failed." : errors;
            return false;
        }

        using (mesh)
        {
            canvas.DrawMesh(mesh, _meshPaint);
            _drawCalls++;
        }

        return true;
    }

    private void FillUniforms(float width, float height, MeshMaterial material, SkiaMeshMaterialResources materialResources)
    {
        _uniformData[0] = _showMeshGrid ? 1.0f : 0.0f;
        _uniformData[1] = width;
        _uniformData[2] = Math.Max(0.1f, _distance + _document.Radius);
        _uniformData[3] = _mode;
        _uniformData[4] = 0.42f;
        _uniformData[5] = 0.74f;
        _uniformData[6] = 0.52f;
        _uniformData[7] = Math.Max(NearPlane, _distance - _document.Radius * 1.4f);
        _uniformData[8] = Math.Max(1, materialResources.TextureWidth);
        _uniformData[9] = Math.Max(1, materialResources.TextureHeight);
        _uniformData[10] = materialResources.HasDiffuseTexture ? 1.0f : 0.0f;
        _uniformData[11] = Math.Clamp(material.Alpha, 0.0f, 1.0f);
        var focal = MathF.Min(width, height) * 0.78f;
        _uniformData[12] = _camera.Position.X;
        _uniformData[13] = _camera.Position.Y;
        _uniformData[14] = _camera.Position.Z;
        _uniformData[15] = focal;
        _uniformData[16] = _camera.Right.X;
        _uniformData[17] = _camera.Right.Y;
        _uniformData[18] = _camera.Right.Z;
        _uniformData[19] = height;
        _uniformData[20] = _camera.Up.X;
        _uniformData[21] = _camera.Up.Y;
        _uniformData[22] = _camera.Up.Z;
        _uniformData[23] = _distance;
        _uniformData[24] = _camera.Forward.X;
        _uniformData[25] = _camera.Forward.Y;
        _uniformData[26] = _camera.Forward.Z;
        _uniformData[27] = _document.Radius;
    }

    private void DisposeMeshBatches()
    {
        foreach (var batch in _meshBatches)
        {
            batch.Dispose();
        }

        _meshBatches.Clear();
    }

    private void DisposeProjectedMeshBatches()
    {
        foreach (var batch in _projectedMeshBatches)
        {
            batch.Dispose();
        }

        _projectedMeshBatches.Clear();
    }

    private void DisposeSplatBatches()
    {
        foreach (var batch in _splatBatches)
        {
            batch.Dispose();
        }

        _splatBatches.Clear();
    }

    private void DrawReferenceGrid(SKCanvas canvas, float width, float height)
    {
        var extent = MathF.Max(2, MathF.Ceiling(ActiveRadius * 1.8f));
        for (var i = -extent; i <= extent; i++)
        {
            DrawWorldLine(canvas, new Vec3(i, -extent, 0), new Vec3(i, extent, 0), _gridPaint, width, height);
            DrawWorldLine(canvas, new Vec3(-extent, i, 0), new Vec3(extent, i, 0), _gridPaint, width, height);
        }

        DrawWorldLine(canvas, new Vec3(0, 0, 0), new Vec3(extent, 0, 0), _axisXPaint, width, height);
        DrawWorldLine(canvas, new Vec3(0, 0, 0), new Vec3(0, extent, 0), _axisYPaint, width, height);
        DrawWorldLine(canvas, new Vec3(0, 0, 0), new Vec3(0, 0, extent), _axisZPaint, width, height);
    }

    private void DrawWorldLine(SKCanvas canvas, Vec3 a, Vec3 b, SKPaint paint, float width, float height)
    {
        if (ProjectPoint(a, width, height, out var pa) && ProjectPoint(b, width, height, out var pb))
        {
            canvas.DrawLine(pa.X, pa.Y, pb.X, pb.Y, paint);
        }
    }

    private bool ProjectPoint(Vec3 world, float width, float height, out Vec2 screen)
        => MeshProjection.TryProjectPoint(world, _camera, width, height, FocalLength(width, height), NearPlane, out screen);

    private void DrawVertexHandles(SKCanvas canvas, float width, float height)
    {
        if (_splatCloud is not null)
        {
            return;
        }

        var stride = Math.Max(1, _document.Positions.Count / 600);
        for (var i = 0; i < _document.Positions.Count; i += stride)
        {
            if (!ProjectPoint(_document.Positions[i], width, height, out var p))
            {
                continue;
            }

            var selected = i == _selectedVertex;
            if (!selected && !_showVertexHandles && !_editMode)
            {
                continue;
            }

            canvas.DrawCircle(p.X, p.Y, selected ? 5.5f : 2.3f, selected ? _selectedVertexPaint : _vertexPaint);
        }
    }

    private void DrawOverlay(SKCanvas canvas, float width, float height)
    {
        var mode = ShadingModeName;
        var edit = _editMode ? "EDIT" : "VIEW";
        var text = _splatCloud is not null
            ? $"{ActiveName} // {mode} // {edit} // splats {_splatCloud.Splats.Length:N0} // selected none"
            : $"{_document.Name} // {mode} // {edit} // vertices {_document.Positions.Count:N0} // triangles {_document.Triangles.Count:N0} // selected {(_selectedVertex >= 0 ? _selectedVertex.ToString(CultureInfo.InvariantCulture) : "none")}";
        canvas.DrawText(text, 18, height - 22, _textFont, _textPaint);
    }

    private int FindNearestVertex(float x, float y, float width, float height, float maxDistance)
    {
        if (_splatCloud is not null)
        {
            return -1;
        }

        var best = -1;
        var bestDistance = maxDistance * maxDistance;
        for (var i = 0; i < _document.Positions.Count; i++)
        {
            if (!ProjectPoint(_document.Positions[i], width, height, out var screen))
            {
                continue;
            }

            var dx = screen.X - x;
            var dy = screen.Y - y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void MoveSelectedVertex(float dx, float dy)
    {
        if (_splatCloud is not null || _selectedVertex < 0 || _selectedVertex >= _document.Positions.Count)
        {
            return;
        }

        var factor = _distance / MathF.Max(1.0f, MathF.Min(_lastWidth, _lastHeight) * 0.78f);
        var delta = _camera.Right * (dx * factor) - _camera.Up * (dy * factor);
        _document.Positions[_selectedVertex] += delta;
        _document.InvalidateAuthoredNormals();
        _document.RecomputeNormals();
        _meshBatchesDirty = true;
    }

    private void PanCamera(float dx, float dy)
    {
        var factor = _distance / MathF.Max(1.0f, MathF.Min(_lastWidth, _lastHeight) * 0.78f);
        _target -= _camera.Right * (dx * factor);
        _target += _camera.Up * (dy * factor);
    }

    private CameraBasis BuildCameraBasis() => CameraBasis.FromOrbit(_target, _yaw, _pitch, _distance);

    private static float FocalLength(float width, float height) => MathF.Min(width, height) * 0.78f;

    private void ResetView()
    {
        if (_splatCloud is null)
        {
            _document.RecomputeBounds();
        }

        _target = ActiveCenter;
        _distance = Math.Clamp(ActiveRadius * 3.2f, MinZoomDistance, MaxZoomDistance);
        _yaw = -0.58f;
        _pitch = 0.38f;
        _selectedVertex = -1;
    }

    private Vec3 ActiveCenter => _splatCloud?.Center ?? _document.Center;
    private float ActiveRadius => _splatCloud?.Radius ?? _document.Radius;
    private string ActiveName => _splatCloud?.Name ?? _document.Name;
    private float MinZoomDistance => MathF.Max(MinCameraDistance, ActiveRadius * 0.015f);
    private float MaxZoomDistance => MathF.Max(MaxCameraDistance, ActiveRadius * 120.0f);

    private string ShadingModeName => _splatCloud is not null
        ? "Gaussian Splats"
        : _mode switch
        {
            < 0.5f => "Material UV",
            < 1.5f => "Depth",
            _ => "Normals"
        };

    private void PublishStats(double renderMilliseconds)
    {
        _frameCount++;
        var now = _clock.Elapsed.TotalSeconds;
        var frameMilliseconds = _lastFrameSeconds <= 0 ? 0 : (now - _lastFrameSeconds) * 1000.0;
        _lastFrameSeconds = now;

        if (now - _lastStatsSeconds >= 0.25)
        {
            _framesPerSecond = _frameCount / (now - _lastStatsSeconds);
            _frameCount = 0;
            _lastStatsSeconds = now;
        }

        var splatCloud = _splatCloud;
        var meshVertexCount = splatCloud?.Splats.Length ?? _document.Positions.Count;
        var meshTriangleCount = splatCloud is null ? _document.Triangles.Count : 0;
        var backend = splatCloud is null
            ? "Uno SKCanvasElement + SkiaSharp v4 SKMesh"
            : "Uno SKCanvasElement + SkiaSharp v4 SKMesh Gaussian splats";

        StatsUpdated?.Invoke(this, new MeshModelerStats(
            frameMilliseconds,
            renderMilliseconds,
            _framesPerSecond,
            meshVertexCount,
            meshTriangleCount,
            _submittedVertexCount,
            _submittedIndexCount,
            _drawCalls,
            UniformFloatCount * sizeof(float),
            _selectedVertex,
            _yaw * 180.0 / Math.PI,
            _pitch * 180.0 / Math.PI,
            _distance,
            ShadingModeName,
            backend,
            _status));
    }

    private enum PointerAction
    {
        None,
        Orbit,
        Pan,
        EditVertex
    }

    private sealed class MeshBatch : IDisposable
    {
        public MeshBatch(int materialIndex, int vertexCount, int indexCount, SKMeshVertexBuffer vertexBuffer, SKMeshIndexBuffer indexBuffer)
        {
            MaterialIndex = materialIndex;
            VertexCount = vertexCount;
            IndexCount = indexCount;
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
        }

        public int MaterialIndex { get; }
        public int VertexCount { get; }
        public int IndexCount { get; }
        public SKMeshVertexBuffer VertexBuffer { get; }
        public SKMeshIndexBuffer IndexBuffer { get; }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }

    private sealed class SplatMeshBatch : IDisposable
    {
        public SplatMeshBatch(int vertexCount, int indexCount, SKMeshVertexBuffer vertexBuffer, SKMeshIndexBuffer indexBuffer, SKData uniforms, SKMesh mesh)
        {
            VertexCount = vertexCount;
            IndexCount = indexCount;
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            Uniforms = uniforms;
            Mesh = mesh;
        }

        public int VertexCount { get; }
        public int IndexCount { get; }
        public SKMeshVertexBuffer VertexBuffer { get; }
        public SKMeshIndexBuffer IndexBuffer { get; }
        public SKData Uniforms { get; }
        public SKMesh Mesh { get; }

        public void Dispose()
        {
            Mesh.Dispose();
            Uniforms.Dispose();
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }


}
