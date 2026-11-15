using DistantWorlds.Types;
using DistantWorlds2;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

internal readonly record struct MapProjection(
    Waypoint Origin, double A, double B, double D, double E, double G, double H,
    int Left, int Top, int SpanX, int SpanY)
{
    public static MapProjection? FromMap(ScaledRenderer renderer, CameraInfo camera, int width, int height)
    {
        int left = width / 4;
        int top = height / 4;
        int right = width - left;
        int bottom = height - top;
        if (left <= 0 || top <= 0)
            return null;

        Point p0 = renderer.ResolveGalaxyCoordinates(camera, left, top);
        Point p1 = renderer.ResolveGalaxyCoordinates(camera, right, top);
        Point p2 = renderer.ResolveGalaxyCoordinates(camera, right, bottom);
        Point p3 = renderer.ResolveGalaxyCoordinates(camera, left, bottom);
        return FromCorners(new(p0.X, p0.Y), new(p1.X, p1.Y), new(p2.X, p2.Y), new(p3.X, p3.Y),
            left, top, right - left, bottom - top);
    }

    public static MapProjection? FromCorners(Waypoint p0, Waypoint p1, Waypoint p2, Waypoint p3,
        int left, int top, int spanX, int spanY)
    {
        double x1 = (double)p1.X - p0.X, y1 = (double)p1.Y - p0.Y;
        double x2 = (double)p2.X - p0.X, y2 = (double)p2.Y - p0.Y;
        double x3 = (double)p3.X - p0.X, y3 = (double)p3.Y - p0.Y;
        double dx1 = x1 - x2, dy1 = y1 - y2;
        double dx2 = x3 - x2, dy2 = y3 - y2;
        double determinant = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(determinant) < 1)
            return null;

        double sumX = x2 - x1 - x3, sumY = y2 - y1 - y3;
        double g = (sumX * dy2 - dx2 * sumY) / determinant;
        double h = (dx1 * sumY - sumX * dy1) / determinant;
        return new(p0, x1 * (1 + g), x3 * (1 + h), y1 * (1 + g), y3 * (1 + h), g, h, left, top, spanX, spanY);
    }

    public bool TryProject(Waypoint point, out Vector2 screen)
    {
        double x = (double)point.X - Origin.X, y = (double)point.Y - Origin.Y;
        double a = A - G * x, b = B - H * x;
        double d = D - G * y, e = E - H * y;
        double determinant = a * e - b * d;
        if (Math.Abs(determinant) < 1)
        {
            screen = default;
            return false;
        }

        double u = (x * e - b * y) / determinant;
        double v = (a * y - x * d) / determinant;
        screen = new((float)(Left + SpanX * u), (float)(Top + SpanY * v));
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y) && 1 + G * u + H * v > 0;
    }

    internal static bool ClipSegment(ref Vector2 a, ref Vector2 b, int width, int height)
    {
        double x = a.X, y = a.Y, dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
        double enter = 0, leave = 1;
        bool Edge(double p, double q)
        {
            if (p == 0) return q >= 0;
            double t = q / p;
            if (p < 0) enter = Math.Max(enter, t); else leave = Math.Min(leave, t);
            return enter <= leave;
        }
        if (width <= 16 || height <= 16 || !double.IsFinite(dx) || !double.IsFinite(dy) ||
            !Edge(-dx, x - 8) || !Edge(dx, width - 9 - x) || !Edge(-dy, y - 8) || !Edge(dy, height - 9 - y)) return false;
        a = new((float)(x + enter * dx), (float)(y + enter * dy));
        b = new((float)(x + leave * dx), (float)(y + leave * dy));
        return true;
    }
}
