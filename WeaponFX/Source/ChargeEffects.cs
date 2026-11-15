using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class ChargeEffects
{
    internal const float Window = .3f;
    private static Func<Galaxy?> _galaxy = null!;
    private static Func<Location?> _location = null!;
    private static Array _vertices = null!;
    private static FieldInfo _center = null!, _corner = null!, _data = null!, _tint = null!;
    private static readonly Vector2[] Corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];

    internal static void Install(Harmony harmony, Assembly assembly)
    {
        const string prefix = "DW2R_Visual_Effects.Patches.";
        var source = assembly.GetType(prefix + "ShieldBeamStop", true)!;
        _galaxy = AccessTools.PropertyGetter(source, "CurrentGalaxy").CreateDelegate<Func<Galaxy?>>();
        _location = AccessTools.PropertyGetter(source, "CurrentLocation").CreateDelegate<Func<Location?>>();
        var impact = assembly.GetType(prefix + "MissileImpact", true)!;
        _vertices = (Array)AccessTools.Field(impact, "Vertices").GetValue(null)!;
        var vertex = _vertices.GetType().GetElementType()!;
        _center = AccessTools.Field(vertex, "Center");
        _corner = AccessTools.Field(vertex, "Corner");
        _data = AccessTools.Field(vertex, "Data");
        _tint = AccessTools.Field(vertex, "Tint");
        harmony.Patch(AccessTools.Method(impact, "Fill"), postfix: new HarmonyMethod(typeof(ChargeEffects), nameof(Fill)));
    }

    internal static bool Supports(ComponentDefinition component) => component.WeaponEffect?.WeaponChargeupLength > 0 &&
        component.Category != ComponentCategory.PlanetaryDetonator;

    internal static float Progress(float remaining) => remaining > 0 && remaining <= Window ? 1f - remaining / Window : -1f;

    private static void Fill(Vector3 camera, bool behind, ref int __result)
    {
        if (behind || Entry.Bypass || !MissileDepth.ChargeShaderReady || _galaxy() is not { } galaxy ||
            _location()?.Ships is not { } ships) return;
        foreach (var ship in ships.GetReadOnlySpan())
        {
            if (__result >= _vertices.Length / 4) break;
            if (ship == null || ship.IsDestroyed || !ship.IsPlanetDestroyer || ship.Weapons == null ||
                !ship.IsFiringPlanetDestroyerWeapon(out var weapon, out var remaining) || weapon?.WeaponChargeupFireTarget is not { IsDestroyed: false } ||
                Progress(remaining) is not (>= 0) || weapon.Component is not { } component || !Supports(component) ||
                Entry.StyleOf(weapon.ComponentId) is not { ChargeStyle: > 0 } style ||
                !Turrets.TryVisualMuzzle(galaxy, ship, weapon.ComponentBayId, style.MountScale, out var muzzle, out var barrel)) continue;

            var progress = Progress(remaining);
            var tint = style.ChannelTint(1) ?? component.WeaponEffect!.WeaponChargeupEffectTintColor.ToVector3();
            if (style.ChannelTint(1) == null && tint.LengthSquared() < .0001f) tint = Vector3.One;
            var phase = (float)(Stopwatch.GetTimestamp() * (18d / Stopwatch.Frequency) % Math.Tau);
            var data = new Vector4(Math.Clamp(barrel * .7f * style.Width, .45f, 30f), progress, phase, 4 + style.ChargeStyle);
            var index = __result * 4;
            var value = _vertices.GetValue(index)!;
            _center.SetValue(value, muzzle - camera);
            _data.SetValue(value, data);
            _tint.SetValue(value, tint * style.ChannelBrightness(1) * style.BodyOpacity);
            for (var i = 0; i < 4; i++)
            {
                _corner.SetValue(value, Corners[i]);
                _vertices.SetValue(value, index + i);
            }
            __result++;
        }
    }
}
