using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using DistantWorlds2;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class NativeEffects
{
    [ThreadStatic] private static Location? _location;
    private sealed record TrailTag(short Id);
    private sealed record WaveColors(Color First, Color Second);
    private static readonly ConditionalWeakTable<BillboardWeaponTrail, TrailTag> Trails = new();
    private static readonly ConditionalWeakTable<AreaBlastWaveRenderItem, WaveColors> Waves = new();

    internal static void Install(Harmony harmony)
    {
        foreach (var method in new[] { "GenerateBillboardVerticesRaw", "GenerateTrailVerticesRaw" })
            harmony.Patch(AccessTools.Method(typeof(BillboardSystem), method),
                prefix: new HarmonyMethod(typeof(NativeEffects), nameof(Context)),
                transpiler: new HarmonyMethod(typeof(NativeEffects), nameof(RenderFields)),
                finalizer: new HarmonyMethod(typeof(NativeEffects), nameof(RestoreContext)));
        harmony.Patch(AccessTools.Method(typeof(AreaBlastWaveRenderItem), "Update"),
            postfix: new HarmonyMethod(typeof(NativeEffects), nameof(Wave)));
        ParticleVisuals.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(GraphicsNodeList), "InitiateEffect"),
            prefix: new HarmonyMethod(typeof(NativeEffects), nameof(Flash)));
    }

    private static void Context(Galaxy ____Galaxy, int ____LocationId, ref Location? __state)
    {
        __state = _location;
        _location = ____Galaxy?.Locations?.GetById(____LocationId);
    }

    private static void RestoreContext(Location? __state) => _location = __state;

    private static IEnumerable<CodeInstruction> RenderFields(IEnumerable<CodeInstruction> operations)
    {
        var code = operations.ToList();
        var count = 0;
        for (var i = 0; i < code.Count; i++)
        {
            var operation = code[i];
            yield return operation;
            if (operation.opcode != OpCodes.Ldfld || operation.operand is not FieldInfo field) continue;
            var name = field.DeclaringType == typeof(BillboardRenderItem) ? field.Name switch
            {
                "Position" => nameof(Position),
                "Scale" => nameof(Scale),
                "TextureTintColor" or "AltTextureTintColor" => nameof(Tint),
                "ColorBoost" => nameof(Brightness),
                "Alpha" => nameof(Alpha),
                "PulsingIntensity" => nameof(Pulse),
                _ => null
            } : field.DeclaringType == typeof(BillboardWeaponTrail) ? field.Name switch
            {
                "Scale" => nameof(TrailScale),
                "TintColor" => nameof(TrailTint),
                "Alpha" => nameof(TrailAlpha),
                _ => null
            } : null;
            if (name == null) continue;
            if (i == 0 || !IsLoadLocal(code[i - 1].opcode)) throw new InvalidOperationException("Unrecognized native billboard input.");
            yield return new CodeInstruction(code[i - 1].opcode, code[i - 1].operand);
            yield return CodeInstruction.Call(typeof(NativeEffects), name);
            count++;
        }
        if (count < 3) throw new InvalidOperationException("Native weapon renderer fields were not found.");
    }

    private static bool IsLoadLocal(OpCode code) => code == OpCodes.Ldloc || code == OpCodes.Ldloc_S ||
        code == OpCodes.Ldloc_0 || code == OpCodes.Ldloc_1 || code == OpCodes.Ldloc_2 || code == OpCodes.Ldloc_3;

    private static WeaponBlast? Shot(BillboardRenderItem item) =>
        item.ParentType == ItemType.WeaponBlast && _location != null ? item.Parent.Resolve(_location.WeaponBlasts) : null;

    private static Entry.Style? Style(BillboardRenderItem item) => Shot(item) is { } shot ? Entry.StyleOf(shot.FiringWeaponComponentId) : null;
    private static Vector3 Tint(Vector3 value, BillboardRenderItem item) => Style(item)?.ChannelTint(1) ?? value;
    private static float Alpha(float value, BillboardRenderItem item) => Style(item) is { } style ?
        value * (style.ChannelColor(1) == 0 ? 0 : style.BodyOpacity) : value;
    private static float Brightness(float value, BillboardRenderItem item)
    {
        var style = Style(item);
        var brightness = style == null ? 1 : style.ChannelBrightness(1) * (style.ChannelColor(1) == 0 || style.BodyOpacity == 0 ? 0 : 1);
        return value > 999.99f ? 1000 + (value - 1000) * brightness : value * brightness;
    }
    private static Vector3 Position(Vector3 value, BillboardRenderItem item) => Shot(item) is { } shot && shot.FireType != WeaponFireType.FullLengthBeam ?
        value + Turrets.LaunchOffset(shot) : value;
    private static float Pulse(float value, BillboardRenderItem item) => value * (Style(item)?.Pulse ?? 1);
    private static Vector2 Scale(Vector2 value, BillboardRenderItem item)
    {
        var shot = Shot(item);
        if (shot == null || Entry.StyleOf(shot.FiringWeaponComponentId) is not { } style) return value;
        return new Vector2(value.X * style.Width, value.Y * (shot.FireType is WeaponFireType.FullLengthBeam or WeaponFireType.Seeking ? 1 : style.Length));
    }

    private static Entry.Style? TrailStyle(BillboardWeaponTrail item)
    {
        if (!Trails.TryGetValue(item, out var tag) && _location?.WeaponBlasts.GetById(item.ParentWeaponBlastId) is { } shot)
            tag = Trails.GetValue(item, _ => new TrailTag(shot.FiringWeaponComponentId));
        return tag == null ? null : Entry.StyleOf(tag.Id);
    }
    private static Vector3 TrailTint(Vector3 value, BillboardWeaponTrail item) => TrailStyle(item)?.ChannelTint(2) ?? value;
    private static Vector2 TrailScale(Vector2 value, BillboardWeaponTrail item)
    {
        if (ProjectileEffects.ReplacesNativeTorpedoTrail(item.WeaponEffect)) return Vector2.Zero;
        return TrailStyle(item) is { } style
            ? style.Length == 0 ? Vector2.Zero : new Vector2(value.X * style.Width, value.Y * style.Length)
            : value;
    }
    private static float TrailAlpha(float value, BillboardWeaponTrail item)
    {
        if (ProjectileEffects.ReplacesNativeTorpedoTrail(item.WeaponEffect)) return 0;
        return TrailStyle(item) is { } style
            ? style.Length == 0 || style.ChannelColor(2) == 0 ? 0 : value * style.ChannelBrightness(2) * style.TrailOpacity
            : value;
    }

    private static void Wave(AreaBlastWaveRenderItem __instance, CameraInfo camera, Location location)
    {
        if (camera == null || location == null || __instance.ParentType != ItemType.WeaponBlast ||
            location.WeaponBlasts.GetById(__instance.ParentId) is not { } shot) return;
        var colors = Waves.GetValue(__instance, item => new WaveColors(item.Color1, item.Color2));
        var style = Entry.StyleOf(shot.FiringWeaponComponentId);
        __instance.Color1 = WaveTint(colors.First, style);
        __instance.Color2 = WaveTint(colors.Second, style);
        __instance.Alpha *= style == null ? 1 : style.ChannelBrightness(1) * style.BodyOpacity;
        __instance.Radius *= style?.Width ?? 1;
    }

    private static Color WaveTint(Color color, Entry.Style? style) => style?.ChannelTint(1) is { } tint ?
        new Color((byte)(tint.X * 255), (byte)(tint.Y * 255), (byte)(tint.Z * 255), color.A) : color;

    private static void Flash(DisplayEffect? displayEffect, ref Matrix worldTransform, ref Color effectTintColor)
    {
        if (displayEffect == null || displayEffect.Id is < 0 or > short.MaxValue ||
            displayEffect.Type is not (DisplayEffectType.WeaponMuzzleFlash or DisplayEffectType.WeaponChargeup) ||
            Entry.StyleOf((short)displayEffect.Id) is not { } style) return;
        worldTransform = Matrix.Scaling(style.Width) * worldTransform;

    }
}
