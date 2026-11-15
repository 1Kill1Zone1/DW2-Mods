using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class ImpactPreview
{
    private sealed record Request(Ship Ship, ComponentDefinition Weapon, bool Shield, long Started);
    private static Request? _pending;
    private static Func<Location?>? _location;
    private static Func<Galaxy, Ship, float> _scale = null!;
    private static Func<Galaxy, double> _clock = null!;
    private static Func<bool> _ready = null!;
    private static MethodInfo _getShell = null!, _pick = null!, _leaveShell = null!, _pop = null!, _keep = null!;
    private static FieldInfo _galaxy = null!, _halfExtents = null!;
    private static Type _effect = null!;
    private static Dictionary<string, FieldInfo> _effectFields = null!;
    private static object _burst = null!;
    private static bool _installed;

    internal static void Install(Harmony harmony, Assembly assembly, Type impact)
    {
        const string prefix = "DW2R_Visual_Effects.Patches.";
        Type TypeOf(string name) => assembly.GetType(prefix + name, throwOnError: true)!;
        try
        {
            var shell = TypeOf("ShieldShellMesh");
            var aim = TypeOf("ShotAim");
            _scale = AccessTools.Method(shell, "ShownScale", [typeof(Galaxy), typeof(Ship)]).CreateDelegate<Func<Galaxy, Ship, float>>();
            _clock = AccessTools.Method(TypeOf("MissileTrail"), "Clock").CreateDelegate<Func<Galaxy, double>>();
            _location = AccessTools.PropertyGetter(TypeOf("ShieldBeamStop"), "CurrentLocation").CreateDelegate<Func<Location?>>();
            _ready = AccessTools.PropertyGetter(impact, "Ready").CreateDelegate<Func<bool>>();
            _getShell = AccessTools.Method(shell, "TryGetShell");
            _pick = AccessTools.Method(aim, "Pick");
            _leaveShell = AccessTools.Method(aim, "LeaveShell");
            _pop = AccessTools.Method(impact, "Pop");
            _keep = AccessTools.Method(impact, "Keep");
            _galaxy = AccessTools.Field(impact, "_galaxy");
            _halfExtents = AccessTools.Field(AccessTools.Inner(shell, "Shell"), "HalfExtents");
            _effect = AccessTools.Inner(impact, "Impact");
            _effectFields = new[] { "Kind", "Ship", "Point", "Scale", "Radius", "Seconds", "Start", "Tint" }
                .ToDictionary(name => name, name => _effect.GetField(name) ?? throw new MissingFieldException(_effect.FullName, name));
            _burst = Enum.Parse(_effectFields["Kind"].FieldType, "Burst");
            harmony.Patch(AccessTools.Method(impact, "Fill"), prefix: new HarmonyMethod(typeof(ImpactPreview), nameof(PlayPending)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Entry.Log("3D impact preview unavailable: " + ex.GetBaseException().Message);
        }
    }

    internal static void Queue(Ship? ship, ComponentDefinition weapon, bool shield)
    {
        if (_installed && ship != null)
            Interlocked.Exchange(ref _pending, new Request(ship, weapon, shield, Stopwatch.GetTimestamp()));
    }

    internal static double GameTime() => _clock((Galaxy)_galaxy.GetValue(null)!);

    private static void PlayPending(Vector3 camera)
    {
        if (Volatile.Read(ref _pending) == null) return;
        var request = Interlocked.Exchange(ref _pending, null);
        if (request == null || Stopwatch.GetElapsedTime(request.Started).TotalSeconds > 1) return;
        var ship = request.Ship;
        if (!_ready() || ship.IsDestroyed || ship.GetGalaxy() is not { } galaxy || _location?.Invoke() is not { } location ||
            !ReferenceEquals(location, ship.GetLocation(galaxy))) return;

        try
        {
            var model = ship.GetShipHull()?.ModelName;
            if (string.IsNullOrEmpty(model)) return;
            object?[] shellArgs = [model, null];
            if (_getShell.Invoke(null, shellArgs) is not true || shellArgs[1] is not { } shell) return;
            var scale = _scale(galaxy, ship);
            if (!(scale > 0) || !float.IsFinite(scale)) return;
            var facing = Vector3.TransformNormal(camera - ship.GetPositionRaw(), ship.DeRotation);
            if (!(facing.LengthSquared() > .0001f)) facing = Vector3.UnitY;
            facing.Normalize();
            var hull = (Vector3)_pick.Invoke(null, [shell, facing, request.Weapon.ComponentId.GetHashCode() + 1])!;
            var sprite = Math.Clamp(((Vector3)_halfExtents.GetValue(shell)!).Length() * scale * .10f, 2, 10);
            var tint = request.Weapon.WeaponEffect?.TextureTintColor.ToVector3() ?? Vector3.One;
            var peak = Math.Max(tint.X, Math.Max(tint.Y, tint.Z));
            tint = peak > 0 ? tint / peak : Vector3.One;
            var now = _clock(galaxy);
            _galaxy.SetValue(null, galaxy);
            ImpactColors.Preview(request.Weapon.ComponentId, () =>
            {
                if (request.Shield)
                {
                    var point = (Vector3)_leaveShell.Invoke(null, [shell, hull, facing])!;
                    _pop.Invoke(null, [ship, shell, point, scale, sprite, now, tint]);
                }
                else
                {
                    var effect = Activator.CreateInstance(_effect, nonPublic: true)!;
                    _effectFields["Kind"].SetValue(effect, _burst);
                    _effectFields["Ship"].SetValue(effect, ship);
                    _effectFields["Point"].SetValue(effect, hull + facing * (.25f / scale));
                    _effectFields["Scale"].SetValue(effect, scale);
                    _effectFields["Radius"].SetValue(effect, sprite * 1.1f);
                    _effectFields["Seconds"].SetValue(effect, .32f);
                    _effectFields["Start"].SetValue(effect, now);
                    _effectFields["Tint"].SetValue(effect, tint);
                    _keep.Invoke(null, [effect]);
                }
            });
        }
        catch (Exception ex)
        {
            Entry.Log("3D impact preview skipped: " + ex.GetBaseException().Message);
        }
    }
}
