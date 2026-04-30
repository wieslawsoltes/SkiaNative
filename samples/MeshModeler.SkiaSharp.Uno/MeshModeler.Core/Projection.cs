namespace MeshModeler.Core;

public readonly record struct CameraBasis(Vec3 Position, Vec3 Forward, Vec3 Right, Vec3 Up)
{
    public static CameraBasis FromOrbit(Vec3 target, float yaw, float pitch, float distance)
    {
        var cp = MathF.Cos(pitch);
        var forward = new Vec3(cp * MathF.Sin(yaw), MathF.Sin(pitch), cp * MathF.Cos(yaw)).Normalized();
        var position = target - forward * distance;
        var right = Vec3.Cross(new Vec3(0, 1, 0), forward).Normalized();
        if (right.LengthSquared < 0.0001f)
        {
            right = new Vec3(1, 0, 0);
        }

        var up = Vec3.Cross(forward, right).Normalized();
        return new CameraBasis(position, forward, right, up);
    }
}

public readonly record struct ProjectedMeshCorner(
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
    float MaterialB);

public readonly record struct ProjectedMeshTriangle(
    int MaterialIndex,
    float Depth,
    ProjectedMeshCorner A,
    ProjectedMeshCorner B,
    ProjectedMeshCorner C);

public readonly record struct ProjectedGaussianSplat(
    float CenterX,
    float CenterY,
    float AxisAX,
    float AxisAY,
    float AxisBX,
    float AxisBY,
    float ColorR,
    float ColorG,
    float ColorB,
    float Alpha,
    float Depth);

public static class MeshProjection
{
    public static bool TryProjectPoint(Vec3 world, CameraBasis camera, float width, float height, float focal, float nearPlane, out Vec2 screen)
    {
        var rel = world - camera.Position;
        var vx = Vec3.Dot(rel, camera.Right);
        var vy = Vec3.Dot(rel, camera.Up);
        var vz = Vec3.Dot(rel, camera.Forward);
        if (vz <= nearPlane)
        {
            screen = default;
            return false;
        }

        screen = new Vec2(width * 0.5f + vx / vz * focal, height * 0.5f - vy / vz * focal);
        return true;
    }

    public static bool TryProjectTriangle(
        Triangle triangle,
        MeshDocument document,
        CameraBasis camera,
        float width,
        float height,
        float focal,
        float nearPlane,
        bool usePerMaterialUniforms,
        out ProjectedMeshTriangle projected)
    {
        projected = default;
        if (!TryProjectCorner(triangle.A, triangle.MaterialColor, document, camera, width, height, focal, nearPlane, out var a) ||
            !TryProjectCorner(triangle.B, triangle.MaterialColor, document, camera, width, height, focal, nearPlane, out var b) ||
            !TryProjectCorner(triangle.C, triangle.MaterialColor, document, camera, width, height, focal, nearPlane, out var c))
        {
            return false;
        }

        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 0.01f)
        {
            return false;
        }

        const float padding = 96.0f;
        var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        var minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        var maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (maxX < -padding || minX > width + padding || maxY < -padding || minY > height + padding)
        {
            return false;
        }

        var materialIndex = usePerMaterialUniforms ? triangle.MaterialIndex : 0;
        projected = new ProjectedMeshTriangle(materialIndex, (a.Depth + b.Depth + c.Depth) / 3.0f, a, b, c);
        return true;
    }

    private static bool TryProjectCorner(
        Corner corner,
        Vec3 materialColor,
        MeshDocument document,
        CameraBasis camera,
        float width,
        float height,
        float focal,
        float nearPlane,
        out ProjectedMeshCorner vertex)
    {
        vertex = default;
        var position = document.Positions[corner.PositionIndex];
        var rel = position - camera.Position;
        var vx = Vec3.Dot(rel, camera.Right);
        var vy = Vec3.Dot(rel, camera.Up);
        var vz = Vec3.Dot(rel, camera.Forward);
        if (vz <= nearPlane)
        {
            return false;
        }

        var normal = corner.HasNormal && document.UseAuthoredNormals
            ? corner.Normal
            : document.Normals[corner.PositionIndex];
        var viewNormal = new Vec3(
            Vec3.Dot(normal, camera.Right),
            Vec3.Dot(normal, camera.Up),
            Vec3.Dot(normal, camera.Forward)).Normalized();
        vertex = new ProjectedMeshCorner(
            width * 0.5f + vx / vz * focal,
            height * 0.5f - vy / vz * focal,
            corner.U,
            corner.V,
            viewNormal.X,
            viewNormal.Y,
            viewNormal.Z,
            vz,
            materialColor.X,
            materialColor.Y,
            materialColor.Z);
        return true;
    }
}

public static class GaussianSplatProjection
{
    public static bool TryProject(
        GaussianSplat splat,
        CameraBasis camera,
        float width,
        float height,
        float focal,
        float nearPlane,
        float minProjectedExtent,
        float maxRadius,
        out ProjectedGaussianSplat item)
    {
        item = default;
        var rel = splat.Position - camera.Position;
        var cx = Vec3.Dot(rel, camera.Right);
        var cy = Vec3.Dot(rel, camera.Up);
        var cz = Vec3.Dot(rel, camera.Forward);
        if (cz <= nearPlane || splat.Alpha <= 0.003f)
        {
            return false;
        }

        var centerX = width * 0.5f + cx / cz * focal;
        var centerY = height * 0.5f - cy / cz * focal;

        ProjectAxisToCovariance(splat.Axis0, camera, cx, cy, cz, focal, out var c00, out var c01, out var c11);
        AccumulateAxisToCovariance(splat.Axis1, camera, cx, cy, cz, focal, ref c00, ref c01, ref c11);
        AccumulateAxisToCovariance(splat.Axis2, camera, cx, cy, cz, focal, ref c00, ref c01, ref c11);
        c00 += 0.35f;
        c11 += 0.35f;

        ComputeEllipseAxes(c00, c01, c11, maxRadius, out var ax, out var ay, out var bx, out var by);
        var maxExtent = MathF.Max(MathF.Sqrt(ax * ax + ay * ay), MathF.Sqrt(bx * bx + by * by));
        if (maxExtent < minProjectedExtent)
        {
            return false;
        }

        const float padding = 96.0f;
        if (centerX + maxExtent < -padding ||
            centerX - maxExtent > width + padding ||
            centerY + maxExtent < -padding ||
            centerY - maxExtent > height + padding)
        {
            return false;
        }

        item = new ProjectedGaussianSplat(centerX, centerY, ax, ay, bx, by, splat.ColorR, splat.ColorG, splat.ColorB, splat.Alpha, cz);
        return true;
    }

    private static void ProjectAxisToCovariance(Vec3 axis, CameraBasis camera, float cx, float cy, float cz, float focal, out float c00, out float c01, out float c11)
    {
        var ax = Vec3.Dot(axis, camera.Right);
        var ay = Vec3.Dot(axis, camera.Up);
        var az = Vec3.Dot(axis, camera.Forward);
        var invZ = 1.0f / cz;
        var invZ2 = invZ * invZ;
        var sx = focal * (ax * invZ - cx * az * invZ2);
        var sy = focal * (-ay * invZ + cy * az * invZ2);
        c00 = sx * sx;
        c01 = sx * sy;
        c11 = sy * sy;
    }

    private static void AccumulateAxisToCovariance(Vec3 axis, CameraBasis camera, float cx, float cy, float cz, float focal, ref float c00, ref float c01, ref float c11)
    {
        ProjectAxisToCovariance(axis, camera, cx, cy, cz, focal, out var a00, out var a01, out var a11);
        c00 += a00;
        c01 += a01;
        c11 += a11;
    }

    private static void ComputeEllipseAxes(float c00, float c01, float c11, float maxRadius, out float ax, out float ay, out float bx, out float by)
    {
        var trace = c00 + c11;
        var delta = MathF.Sqrt(MathF.Max(0.0f, (c00 - c11) * (c00 - c11) + 4.0f * c01 * c01));
        var lambda0 = Math.Clamp((trace + delta) * 0.5f, 0.04f, maxRadius * maxRadius);
        var lambda1 = Math.Clamp((trace - delta) * 0.5f, 0.04f, maxRadius * maxRadius);
        var vx = c01;
        var vy = lambda0 - c00;
        var length = MathF.Sqrt(vx * vx + vy * vy);
        if (length < 0.00001f)
        {
            vx = 1.0f;
            vy = 0.0f;
        }
        else
        {
            vx /= length;
            vy /= length;
        }

        var scale0 = MathF.Sqrt(lambda0);
        var scale1 = MathF.Sqrt(lambda1);
        ax = vx * scale0;
        ay = vy * scale0;
        bx = -vy * scale1;
        by = vx * scale1;
    }
}
