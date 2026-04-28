using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeshParticles.SkiaNative.AvaloniaApp.Controls;

namespace MeshParticles.SkiaNative.AvaloniaApp;

internal sealed class MainView : Grid
{
    private readonly TextBlock _fps = Metric("FPS", "--");
    private readonly TextBlock _frame = Metric("Frame", "-- ms");
    private readonly TextBlock _particles = Metric("Particles", "--");
    private readonly TextBlock _vertices = Metric("Vertices", "--");
    private readonly TextBlock _transitions = Metric("Transitions", "--");
    private readonly TextBlock _gpu = Metric("GPU cache", "-- MiB");
    private readonly TextBlock _status = new()
    {
        Text = "Waiting for first render-thread mesh allocation.",
        Foreground = new SolidColorBrush(Color.FromRgb(157, 174, 196)),
        TextWrapping = TextWrapping.Wrap
    };

    public MainView()
    {
        RowDefinitions = new RowDefinitions("Auto,*");
        ColumnDefinitions = new ColumnDefinitions("300,*");
        Background = new SolidColorBrush(Color.FromRgb(6, 10, 18));

        var header = new Border
        {
            Padding = new Thickness(24, 18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 120, 151, 188)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(12, 20, 34), 0),
                    new GradientStop(Color.FromRgb(15, 29, 48), 0.62),
                    new GradientStop(Color.FromRgb(8, 16, 28), 1)
                }
            },
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "SkiaNative mesh, particles, and shader animation",
                        FontSize = 26,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = "One reusable SkMesh GPU buffer, span-packed vertices and uniforms, one direct drawMesh transition per frame.",
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(178, 194, 216))
                    }
                }
            }
        };
        Grid.SetColumnSpan(header, 2);

        var surface = new MeshParticlesSurface();
        surface.FrameStatsUpdated += (_, stats) => UpdateStats(stats);

        var sidebar = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(12, 20, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 73, 102)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    SectionTitle("Render path"),
                    _status,
                    Divider(),
                    SectionTitle("Frame"),
                    _fps,
                    _frame,
                    _transitions,
                    Divider(),
                    SectionTitle("Mesh"),
                    _particles,
                    _vertices,
                    _gpu,
                    Divider(),
                    new TextBlock
                    {
                        Text = "The fragment shader produces glow, radial falloff, ring modulation, and per-particle hue entirely in Skia's mesh shader pipeline.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(157, 174, 196))
                    }
                }
            }
        };
        Grid.SetRow(sidebar, 1);

        var surfaceHost = new Border
        {
            Margin = new Thickness(0, 16, 16, 16),
            CornerRadius = new CornerRadius(18),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 62, 90)),
            BorderThickness = new Thickness(1),
            Child = surface
        };
        Grid.SetRow(surfaceHost, 1);
        Grid.SetColumn(surfaceHost, 1);

        Children.Add(header);
        Children.Add(sidebar);
        Children.Add(surfaceHost);
    }

    private void UpdateStats(MeshParticleStats stats)
    {
        _fps.Text = $"FPS {stats.Fps:F1}";
        _frame.Text = $"Frame {stats.FrameMs:F2} ms";
        _particles.Text = $"Particles {stats.ParticleCount:N0}";
        _vertices.Text = $"Vertices {stats.VertexCount:N0}";
        _transitions.Text = $"Transitions {stats.NativeTransitions:N0}";
        _gpu.Text = $"GPU cache {stats.GpuResourceBytes / 1024.0 / 1024.0:F1} MiB";
        _status.Text = stats.Error is null
            ? $"Active: {stats.Mode}. Uniform bytes: {stats.UniformBytes}."
            : $"Mesh shader error: {stats.Error}";
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White
    };

    private static TextBlock Metric(string label, string value) => new()
    {
        Text = $"{label} {value}",
        FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromRgb(219, 229, 242))
    };

    private static Border Divider() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(Color.FromArgb(70, 120, 151, 188)),
        Margin = new Thickness(0, 2)
    };
}
