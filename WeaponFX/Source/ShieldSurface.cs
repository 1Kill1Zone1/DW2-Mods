using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Versioning;
using System.Diagnostics;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

// Replaces only the planar Ring/Burst pair emitted by MissileImpact.Pop. The
// native shield flash remains; hull and every other RVE Burst remain untouched.
[SupportedOSPlatform("windows7.0")]
internal static class ShieldSurface
{
    internal const int AngularSegments = 24;
    internal const int RadialBands = 2;
    internal const int QuadsPerWave = AngularSegments * RadialBands;
    internal const int MaximumWaves = 12;
    private const float Seconds = .5f;
    private const int BurstKind = 2;
    private const int RingKind = 3;

    private sealed record Wave(Galaxy Galaxy, Ship Ship, object Shell, Vector3 Local, float Range, float Turn, double Start, long PreviewStarted, short ComponentId, Vector3 StockTint);
    private sealed class PopState
    {
        internal PopState(Wave? prior) => Prior = prior;
        internal Wave? Prior { get; }
    }

    private static readonly object Gate = new();
    private static readonly List<Wave> Waves = [];
    [ThreadStatic] private static Wave? _replacing;
    private static Array _vertices = null!;
    private static FieldInfo _center = null!, _corner = null!, _data = null!, _sideU = null!, _sideV = null!, _tint = null!, _kind = null!, _halfExtents = null!, _shellCenter = null!;
    private static Func<Galaxy?> _galaxy = null!;
    private static Func<Galaxy, Ship, float> _shownScale = null!;
    private static Func<Galaxy, double> _clock = null!;
    private static Func<Ship, bool> _here = null!;
    private static Func<Ship, bool> _shieldsUp = null!;
    private static Func<Ship, Vector3, bool> _facesAway = null!;
    private static Func<Ship, float, Vector3, Vector3> _toWorld = null!;
    private static Func<object, Vector3, Vector3> _surfacePoint = null!;
    private static bool _installed;

    internal static void Install(Harmony harmony, Assembly assembly)
    {
        try
        {
            const string prefix = "DW2R_Visual_Effects.Patches.";
            var impact = assembly.GetType(prefix + "MissileImpact", true)!;
            var shell = assembly.GetType(prefix + "ShieldShellMesh", true)!;
            var stop = assembly.GetType(prefix + "ShieldBeamStop", true)!;
            var collapse = assembly.GetType(prefix + "ShieldCollapse", true)!;
            var aim = assembly.GetType(prefix + "ShotAim", true)!;
            var trail = assembly.GetType(prefix + "MissileTrail", true)!;
            _vertices = (Array)AccessTools.Field(impact, "Vertices").GetValue(null)!;
            var vertex = _vertices.GetType().GetElementType()!;
            _center = AccessTools.Field(vertex, "Center");
            _corner = AccessTools.Field(vertex, "Corner");
            _data = AccessTools.Field(vertex, "Data");
            _sideU = AccessTools.Field(vertex, "SideU");
            _sideV = AccessTools.Field(vertex, "SideV");
            _tint = AccessTools.Field(vertex, "Tint");
            _kind = AccessTools.Field(AccessTools.Inner(impact, "Impact"), "Kind");
            var kindType = _kind.FieldType;
            if (Convert.ToInt32(Enum.Parse(kindType, "ShieldFlash")) != 1 || Convert.ToInt32(Enum.Parse(kindType, "Burst")) != BurstKind ||
                Convert.ToInt32(Enum.Parse(kindType, "Ring")) != RingKind) throw new InvalidOperationException("Unrecognized RVE shield impact kinds.");
            var shellType = AccessTools.Inner(shell, "Shell");
            _halfExtents = AccessTools.Field(shellType, "HalfExtents");
            _shellCenter = AccessTools.Field(shellType, "Center");
            _surfacePoint = SurfaceDelegate(AccessTools.Method(shell, "SurfacePoint"));
            _galaxy = AccessTools.PropertyGetter(stop, "CurrentGalaxy").CreateDelegate<Func<Galaxy?>>();
            _shownScale = AccessTools.Method(shell, "ShownScale", [typeof(Galaxy), typeof(Ship)]).CreateDelegate<Func<Galaxy, Ship, float>>();
            _clock = AccessTools.Method(trail, "Clock").CreateDelegate<Func<Galaxy, double>>();
            _here = AccessTools.Method(stop, "Here").CreateDelegate<Func<Ship, bool>>();
            _shieldsUp = AccessTools.Method(collapse, "ShieldsUp").CreateDelegate<Func<Ship, bool>>();
            _facesAway = AccessTools.Method(shell, "FacesAway").CreateDelegate<Func<Ship, Vector3, bool>>();
            _toWorld = AccessTools.Method(aim, "ToWorld").CreateDelegate<Func<Ship, float, Vector3, Vector3>>();
            harmony.Patch(AccessTools.Method(impact, "Pop"), prefix: new HarmonyMethod(typeof(ShieldSurface), nameof(Pop)), finalizer: new HarmonyMethod(typeof(ShieldSurface), nameof(Restore)));
            harmony.Patch(AccessTools.Method(impact, "Keep"), prefix: new HarmonyMethod(typeof(ShieldSurface), nameof(Keep)));
            harmony.Patch(AccessTools.Method(impact, "Fill"), postfix: new HarmonyMethod(typeof(ShieldSurface), nameof(Fill)));
            _installed = true;
        }
        catch (Exception ex)
        {
            _installed = false;
            Entry.Log("Shield-surface wave unavailable; native shield impacts retained: " + ex.GetBaseException().Message);
        }
    }

    private static Func<object, Vector3, Vector3> SurfaceDelegate(MethodInfo method)
    {
        var shell = Expression.Parameter(typeof(object), "shell");
        var direction = Expression.Parameter(typeof(Vector3), "direction");
        var parameter = method.GetParameters()[0].ParameterType;
        return Expression.Lambda<Func<object, Vector3, Vector3>>(Expression.Call(method, Expression.Convert(shell, parameter), direction), shell, direction).Compile();
    }

    private static void Pop(Ship ship, object shell, Vector3 local, float scale, float sprite, double now, Vector3 tint, ref PopState? __state)
    {
        __state = new PopState(_replacing);
        _replacing = null;
        if (!_installed || !MissileDepth.ImpactChannelsReady || _galaxy() is not { } galaxy || !ImpactColors.TryCurrentStyle(out var componentId, out _, out var previewStarted) ||
            !(scale > 0) || !float.IsFinite(scale) || !(sprite > 0) || !float.IsFinite(sprite)) return;
        var extent = ((Vector3)_halfExtents.GetValue(shell)!).Length() * scale;
        var range = Math.Min(sprite * 4f, extent * .45f);
        if (!(range > .01f) || !float.IsFinite(range)) return;
        var wave = new Wave(galaxy, ship, shell, local, range, Turn(local), now, previewStarted, componentId, tint);
        lock (Gate)
        {
            Prune(galaxy, now);
            // Under sustained fire cap bookkeeping deterministically: this contact retains its
            // tiny native flash but not a second free-space RVE Ring/Burst.
            if (Waves.Count < MaximumWaves) Waves.Add(wave);
            _replacing = wave;
        }
    }

    private static Exception? Restore(Exception? __exception, PopState? __state)
    {
        _replacing = __state?.Prior;
        return __exception;
    }

    private static bool Keep(object impact)
    {
        if (_replacing == null) return true;
        var kind = Convert.ToInt32(_kind.GetValue(impact));
        // Keep the small native contact flash (Kind 1); only this Pop's planar pair is replaced.
        return kind is not (BurstKind or RingKind);
    }

    private static void Fill(Vector3 camera, bool behind, ref int __result)
    {
        if (!_installed) return;
        if (Entry.Bypass || _galaxy() is not { } galaxy)
        {
            lock (Gate) Waves.Clear();
            return;
        }
        var now = _clock(galaxy);
        lock (Gate)
        {
            Prune(galaxy, now);
            // Preserve insertion order when capacity is constrained, so older admitted contacts do
            // not flicker behind newer fire while their short simulation lifetime is still valid.
            foreach (var wave in Waves)
            {
                if (Entry.StyleOf(wave.ComponentId) is not { } style || !TryScale(galaxy, wave.Ship, out var scale)) continue;
                var age = Age(wave, now);
                // A complete wave is appended only if the native 1,024-quad buffer has room. Overflow is flash-only.
                if (__result + QuadsPerWave > _vertices.Length / 4) continue;
                var data = new Vector4(.02f, age, ImpactTurn(style, wave.Turn), RingKind);
                WriteWave(wave, scale, camera, behind, data, ImpactTint(style, wave.StockTint), ref __result);
            }
        }
    }

    private static void Prune(Galaxy galaxy, double now)
    {
        for (var index = Waves.Count - 1; index >= 0; index--)
        {
            var wave = Waves[index];
            if (!ReferenceEquals(wave.Galaxy, galaxy) || Age(wave, now) is < 0 or >= 1 || !_here(wave.Ship) || !_shieldsUp(wave.Ship))
                Waves.RemoveAt(index);
        }
    }

    private static float Age(Wave wave, double now) => wave.PreviewStarted == 0 ? (float)((now - wave.Start) / Seconds) :
        (float)(Stopwatch.GetElapsedTime(wave.PreviewStarted).TotalSeconds / Seconds);

    private static bool TryScale(Galaxy galaxy, Ship ship, out float scale)
    {
        try { scale = _shownScale(galaxy, ship); return scale > 0 && float.IsFinite(scale); }
        catch { scale = 0; return false; }
    }

    private static void WriteWave(Wave wave, float scale, Vector3 camera, bool behind, Vector4 data, Vector3 tint, ref int count)
    {
        var center = (Vector3)_shellCenter.GetValue(wave.Shell)!;
        var radial = wave.Local - center;
        if (radial.LengthSquared() < .000001f) radial = Vector3.UnitZ;
        var reference = Math.Abs(radial.Y) < radial.Length() * .9f ? Vector3.UnitY : Vector3.UnitX;
        var sideU = Vector3.Normalize(Vector3.Cross(radial, reference));
        var sideV = Vector3.Normalize(Vector3.Cross(radial, sideU));
        var localRange = wave.Range / scale;
        Span<Vector2> corners = stackalloc Vector2[4];
        Span<Vector3> world = stackalloc Vector3[4];
        // All six fields are rewritten below, so this one box can be copied to every array slot.
        // It avoids making a boxed Vertex per corner while leaving no stale axes/data behind.
        var value = _vertices.GetValue(0)!;
        for (var band = 0; band < RadialBands; band++)
        {
            var inner = band / (float)RadialBands;
            var outer = (band + 1f) / RadialBands;
            for (var segment = 0; segment < AngularSegments; segment++)
            {
                var start = segment * MathF.Tau / AngularSegments;
                var end = (segment + 1f) * MathF.Tau / AngularSegments;
                corners[0] = Polar(inner, start);
                corners[1] = Polar(outer, start);
                corners[2] = Polar(outer, end);
                corners[3] = Polar(inner, end);
                for (var i = 0; i < 4; i++)
                {
                    var direction = radial + (sideU * corners[i].X + sideV * corners[i].Y) * localRange;
                    world[i] = _toWorld(wave.Ship, scale, _surfacePoint(wave.Shell, direction));
                }
                var patchCenter = (world[0] + world[1] + world[2] + world[3]) * .25f;
                if (_facesAway(wave.Ship, patchCenter) != behind) continue;
                var vertex = count * 4;
                for (var i = 0; i < 4; i++)
                {
                    _center.SetValue(value, world[i] - camera);
                    _corner.SetValue(value, corners[i]);
                    _data.SetValue(value, data);
                    _sideU.SetValue(value, Vector3.Zero);
                    _sideV.SetValue(value, Vector3.Zero);
                    _tint.SetValue(value, tint);
                    _vertices.SetValue(value, vertex + i);
                }
                count++;
            }
        }
    }

    private static Vector2 Polar(float radius, float angle) => new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    private static float Turn(Vector3 local) => ((local.X * 12.9898f + local.Y * 78.233f + local.Z * 37.719f) % 1 + 1) % 1 * MathF.Tau;
    private static Vector3 ImpactTint(Entry.Style style, Vector3 stock)
    {
        var custom = style.ImpactFollowsBody ? style.ChannelColor(3) >= 0 : style.ImpactColor >= 0 || style.ChannelColor(3) == 0;
        return custom ? (style.ChannelTint(3) ?? stock) + new Vector3(14) : stock;
    }
    private static float ImpactTurn(Entry.Style style, float turn)
    {
        var brightness = style.RveImpactBrightness;
        return style.ImpactOpacity == 1 && brightness == 1 ? turn :
            1000 + MathF.Round(turn / MathF.Tau * 255) + 256 * (MathF.Round(style.ImpactOpacity * 100) + 101 * MathF.Round(brightness * 100));
    }
}
