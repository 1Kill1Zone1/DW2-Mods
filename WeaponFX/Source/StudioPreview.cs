using System.Diagnostics;
using System.Runtime.Versioning;
using DistantWorlds.Core;
using DistantWorlds.Types;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal sealed class StudioPreview : IDisposable
{
    private readonly StudioFrame _frame = new();
    private Texture? _texture;
    private long _lastFrame;
    private long _replay;
    private Entry.Style? _lastStyle;
    private short _lastWeapon = -1;
    private int _lastCharge = -1;
    private bool _lastShield;
    private bool _cameraChanged = true;
    internal bool Failed { get; private set; }

    internal static bool Supports(ComponentDefinition? weapon) => weapon != null &&
        (weapon.Name.Contains("Fusion", StringComparison.OrdinalIgnoreCase) && Panel.IsBeam(weapon) ||
         weapon.Name.Contains("Epsilon", StringComparison.OrdinalIgnoreCase) && Panel.IsSeeking(weapon));

    internal void Orbit(Vector2 movement)
    {
        _frame.Yaw = MathF.IEEERemainder(_frame.Yaw - movement.X * .012f, MathF.Tau);
        _frame.Pitch = Math.Clamp(_frame.Pitch + movement.Y * .010f, -1.15f, 1.15f);
        _cameraChanged = true;
    }

    internal void Zoom(float delta)
    {
        _frame.Distance = Math.Clamp(_frame.Distance + delta, 9f, 23f);
        _cameraChanged = true;
    }

    internal void Replay()
    {
        _replay = Stopwatch.GetTimestamp();
        _cameraChanged = true;
    }

    internal bool Draw(RenderDrawContext context, SpriteBatch batch, RectangleF area, ComponentDefinition weapon,
        Entry.Style style, bool shield, float impactBrightness, int charge)
    {
        if (Failed) return false;
        try
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastWeapon != weapon.ComponentId)
            {
                _replay = now;
                _cameraChanged = true;
            }
            _texture ??= Texture.New2D(context.GraphicsDevice, StudioFrame.Width, StudioFrame.Height, PixelFormat.R8G8B8A8_UNorm_SRgb);
            if (_cameraChanged || !Equals(style, _lastStyle) || _lastShield != shield || _lastCharge != charge ||
                Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds >= 1.0 / 30)
            {
                float time = (float)(Stopwatch.GetElapsedTime(_replay, now).TotalSeconds % 2.6);
                _frame.Render(weapon, style, shield, impactBrightness, time, charge);
                _texture.SetData(context.CommandList, _frame.Pixels);
                _lastFrame = now;
                _lastStyle = style;
                _lastWeapon = weapon.ComponentId;
                _lastShield = shield;
                _lastCharge = charge;
                _cameraChanged = false;
            }
            DrawingHelper.DrawTexture(batch, _texture, area, 0f, Color.White);
            return true;
        }
        catch (Exception ex)
        {
            Failed = true;
            Entry.Log("3D weapon preview switched to 2D: " + ex.GetBaseException().Message);
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        _cameraChanged = true;
    }
}

[SupportedOSPlatform("windows7.0")]
internal sealed class StudioFrame
{
    internal const int Width = 400;
    internal const int Height = 148;
    private const float FocalLength = 350f;
    private static readonly Vector3 Source = new(-3.32f, 0, 0);
    private static readonly Vector3 Target = new(3.2f, 0, 0);
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-.4f, .8f, .6f));
    private static readonly Color[] Background = CreateBackground();
    private static readonly Triangle[] Mesh = [
        .. Hull(new Vector3(-4.25f, 0, 0), 1.25f, .63f, .85f, new Vector3(.31f, .41f, .51f)),
        .. Hull(new Vector3(-3.59f, 0, 0), .52f, .24f, .28f, new Vector3(.42f, .53f, .63f)),
        .. Hull(Target, 1.65f, .85f, 1.08f, new Vector3(.31f, .43f, .55f)),
        .. Hull(Target + Vector3.UnitY * .65f, .95f, .62f, .54f, new Vector3(.51f, .62f, .7f)),
        .. Hull(Target + Vector3.UnitZ * .87f, 1.5f, .27f, .32f, new Vector3(.23f, .34f, .49f)),
        .. Hull(Target - Vector3.UnitZ * .87f, 1.5f, .27f, .32f, new Vector3(.23f, .34f, .49f))
    ];
    private readonly float[] _depth = new float[Width * Height];
    private Vector3 _right, _up, _camera;
    internal float Yaw = -.26f;
    internal float Pitch = .31f;
    internal float Distance = 13.5f;
    internal Color[] Pixels { get; } = new Color[Width * Height];
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, Vector3 Tint);
    private readonly record struct Point2(float X, float Y, float Z);

    internal void Render(ComponentDefinition weapon, Entry.Style style, bool shield, float impactBrightness, float time, int charge)
    {
        Array.Copy(Background, Pixels, Pixels.Length);
        Array.Fill(_depth, float.PositiveInfinity);
        var cosPitch = MathF.Cos(Pitch);
        _camera = new Vector3(MathF.Sin(Yaw) * cosPitch, MathF.Sin(Pitch), MathF.Cos(Yaw) * cosPitch);
        _right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, _camera));
        _up = Vector3.Cross(_camera, _right);

        var grid = new Vector3(.07f, .12f, .17f);
        for (int i = -6; i <= 6; i++)
        {
            Stroke(new Vector3(i, -1.23f, -3), new Vector3(i, -1.23f, 3), .45f, grid);
            if (i >= -3 && i <= 3)
                Stroke(new Vector3(-6, -1.23f, i), new Vector3(6, -1.23f, i), .45f, grid);
        }
        foreach (var triangle in Mesh) Face(triangle);
        if (shield)
        {
            var shell = new Vector3(.14f, .29f, .36f);
            Arc(Target, Vector3.UnitY * 1.43f, Vector3.UnitZ * 1.55f, shell, .8f);
            Arc(Target, Vector3.UnitX * 1.62f, Vector3.UnitZ * 1.55f, shell, .8f);
            Arc(Target, Vector3.UnitX * 1.62f, Vector3.UnitY * 1.43f, shell, .8f);
        }
        var contact = Target - Vector3.UnitX * (shield ? 1.62f : .83f);
        var body = style.ChannelTint(1) ?? Tint(weapon.WeaponEffect?.TextureTintColor, new Vector3(.64f, .78f, 1));
        var trail = style.ChannelTint(2) ?? Tint(weapon.WeaponEffect?.TrailEffectTintColor, body);
        var impact = style.ChannelTint(3) ?? (shield ? new Vector3(.35f, .85f, 1) : new Vector3(1, .67f, .32f));
        const float fireAt = .16f;
        bool beam = Panel.IsBeam(weapon);
        float impactAt = beam ? fireAt : 1.02f;
        if (beam && time < fireAt && charge != 0)
        {
            float progress = time / fireAt;
            var glow = body * (progress * style.ChannelBrightness(1) * style.BodyOpacity);
            float radius = (.2f + progress * .62f) * style.Width;
            if (charge == 1)
            {
                Stroke(Source + new Vector3(.18f, radius, 0), Source, 1.5f, glow);
                Stroke(Source + new Vector3(.18f, -radius, 0), Source, 1.5f, glow);
            }
            else if (charge == 3)
                Arc(Source, Vector3.UnitY * radius, Vector3.UnitZ * radius, glow, 1.1f);
            else
                Stroke(Source, Source, 3.5f * progress * style.Width, glow);
        }
        if (beam && time is >= fireAt and < .75f)
        {
            float pulse = 1f - style.Pulse * .20f * (1 + MathF.Sin((time - fireAt) * 32f));
            float bodyPower = style.ChannelBrightness(1) * style.BodyOpacity * pulse;
            float corePower = style.ChannelBrightness(4) * style.CoreOpacity * pulse;
            float peak = Math.Max(body.X, Math.Max(body.Y, body.Z));
            Stroke(Source, contact, 5f * style.Width, body * (bodyPower * .32f));
            Stroke(Source, contact, 2.2f * style.Width, body * bodyPower);
            Stroke(Source, contact, 1.15f * style.CoreWidth, new Vector3(peak * corePower));
        }
        else if (!beam && time is >= fireAt and < 1.02f)
        {
            float progress = (time - fireAt) / (impactAt - fireAt);
            var head = Vector3.Lerp(Source, contact, progress);
            var tail = Vector3.Lerp(Source, contact, Math.Max(0, progress - .26f * style.Length));
            var trailLight = trail * (style.ChannelBrightness(2) * style.TrailOpacity);
            var bodyLight = body * (style.ChannelBrightness(1) * style.BodyOpacity);
            if (style.Length > 0) Stroke(tail, head, 2.5f * style.Width, trailLight, fade: true);
            Stroke(head - Vector3.UnitX * .14f, head + Vector3.UnitX * .07f, 4.5f * style.Width, bodyLight * .40f);
            Stroke(head - Vector3.UnitX * .1f, head, 2f * style.Width, bodyLight);
        }
        float age = time - impactAt;
        if (age is >= 0 and < .52f && style.ImpactOpacity > 0)
        {
            float fade = 1 - age / .52f;
            var glow = impact * (fade * impactBrightness * style.ImpactOpacity);
            Stroke(contact, contact, 6f * fade, glow * .65f);
            if (shield)
            {
                float radius = .10f + 1.85f * age;
                Arc(contact, Vector3.UnitY * radius, Vector3.UnitZ * radius, glow, 1.8f);
            }
            else
            {
                for (int i = 0; i < 10; i++)
                {
                    float angle = MathF.Tau * i / 10;
                    var ray = new Vector3(-.6f - .2f * (i % 3), MathF.Cos(angle), MathF.Sin(angle));
                    Stroke(contact + ray * age * .8f, contact + ray * age * (4f + .6f * (i % 4)), 1.45f, glow);
                }
            }
        }
    }

    private static Vector3 Tint(Color? color, Vector3 fallback) =>
        color is { A: > 0 } value ? value.ToVector3() : fallback;

    private Point2 Project(Vector3 point)
    {
        float z = Distance - Vector3.Dot(_camera, point);
        float scale = FocalLength / Math.Max(1f, z);
        return new Point2(Width * .5f + Vector3.Dot(_right, point) * scale,
            Height * .5f - Vector3.Dot(_up, point) * scale, z);
    }

    private void Face(Triangle triangle)
    {
        var a = Project(triangle.A);
        var b = Project(triangle.B);
        var c = Project(triangle.C);
        float area = Edge(a, b, c.X, c.Y);
        if (Math.Abs(area) < .01f) return;
        var normal = Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A));
        var tint = triangle.Tint * (.31f + .69f * Math.Abs(Vector3.Dot(normal, Light)));
        var color = new Color(new Vector4(tint, 1));
        int left = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int right = Math.Min(Width - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int top = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int bottom = Math.Min(Height - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                float first = Edge(b, c, x + .5f, y + .5f) / area;
                float second = Edge(c, a, x + .5f, y + .5f) / area;
                float third = 1 - first - second;
                if (first < 0 || second < 0 || third < 0) continue;
                float depth = 1 / (first / a.Z + second / b.Z + third / c.Z);
                int index = y * Width + x;
                if (depth >= _depth[index]) continue;
                _depth[index] = depth;
                Pixels[index] = color;
            }
    }

    private static float Edge(Point2 a, Point2 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private void Arc(Vector3 center, Vector3 u, Vector3 v, Vector3 tint, float size)
    {
        var previous = center + u;
        for (int i = 1; i <= 40; i++)
        {
            float angle = MathF.Tau * i / 40;
            var next = center + u * MathF.Cos(angle) + v * MathF.Sin(angle);
            Stroke(previous, next, size, tint);
            previous = next;
        }
    }

    private void Stroke(Vector3 start, Vector3 end, float radius, Vector3 tint, bool fade = false)
    {
        if (radius < .08f || tint.LengthSquared() < .00001f) return;
        var a = Project(start);
        var b = Project(end);
        radius *= 13.5f / ((a.Z + b.Z) * .5f);
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float lengthSquared = Math.Max(.0001f, dx * dx + dy * dy);
        int left = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, b.X) - radius));
        int right = Math.Min(Width - 1, (int)MathF.Ceiling(Math.Max(a.X, b.X) + radius));
        int top = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, b.Y) - radius));
        int bottom = Math.Min(Height - 1, (int)MathF.Ceiling(Math.Max(a.Y, b.Y) + radius));
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                float t = Math.Clamp(((x + .5f - a.X) * dx + (y + .5f - a.Y) * dy) / lengthSquared, 0, 1);
                float sx = x + .5f - a.X - t * dx, sy = y + .5f - a.Y - t * dy;
                float amount = 1 - (sx * sx + sy * sy) / (radius * radius);
                if (amount <= 0) continue;
                int index = y * Width + x;
                float depth = 1 / ((1 - t) / a.Z + t / b.Z);
                if (depth > _depth[index] + .035f) continue;
                float gain = amount * amount * (fade ? t : 1f);
                var previous = Pixels[index];
                Pixels[index] = new Color(Add(previous.R, tint.X * gain), Add(previous.G, tint.Y * gain),
                    Add(previous.B, tint.Z * gain), (byte)255);
            }
    }

    private static byte Add(byte value, float light) => (byte)Math.Min(255f, value + 255f * light);

    private static IEnumerable<Triangle> Hull(Vector3 center, float length, float height, float width, Vector3 tint)
    {
        Vector2[] outline = [new(.55f, 1), new(-.55f, 1), new(-1, .55f), new(-1, -.55f),
            new(-.55f, -1), new(.55f, -1), new(1, -.55f), new(1, .55f)];
        float[] slices = [-.5f, -.32f, .32f, .5f];
        float[] bevel = [.65f, 1f, 1f, .65f];
        Vector3 Point(int slice, int corner) => center + new Vector3(length * slices[slice],
            outline[corner].X * height * bevel[slice], outline[corner].Y * width * bevel[slice]);
        for (int i = 0; i < outline.Length; i++)
        {
            int next = (i + 1) % outline.Length;
            yield return new Triangle(center - Vector3.UnitX * length * .5f, Point(0, i), Point(0, next), tint);
            yield return new Triangle(center + Vector3.UnitX * length * .5f, Point(3, next), Point(3, i), tint);
            for (int slice = 0; slice < 3; slice++)
            {
                yield return new Triangle(Point(slice, i), Point(slice + 1, i), Point(slice + 1, next), tint);
                yield return new Triangle(Point(slice, i), Point(slice + 1, next), Point(slice, next), tint);
            }
        }
    }

    private static Color[] CreateBackground()
    {
        var pixels = new Color[Width * Height];
        var random = new Random(119);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float glow = 1 - Math.Min(1, Math.Abs(y - Height * .48f) / (Height * .64f));
                int star = random.Next(1100) == 0 ? random.Next(13, 57) : 0;
                pixels[y * Width + x] = new Color((byte)(6 + star + (int)(glow * 5)),
                    (byte)(10 + star + (int)(glow * 7)), (byte)(17 + star + (int)(glow * 11)), (byte)255);
            }
        return pixels;
    }
}
