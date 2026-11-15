using System.Reflection;
using System.Runtime.Versioning;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class BeamLayers
{
    private sealed record Owner(short Id, Vector3 Tint);
    [ThreadStatic] private static Owner? _owner;
    internal static void Install(Harmony harmony, Assembly assembly)
    {
        var beam = assembly.GetType("DW2R_Visual_Effects.Patches.BeamCore", true)!;
        harmony.Patch(AccessTools.Method(beam, "Add"), prefix: new HarmonyMethod(typeof(BeamLayers), nameof(Context)),
            finalizer: new HarmonyMethod(typeof(BeamLayers), nameof(Restore)));
        harmony.Patch(AccessTools.Method(beam, "AddQuad"), prefix: new HarmonyMethod(typeof(BeamLayers), nameof(Quad)));
    }
    private static void Context(WeaponBlast blast, BillboardRenderItem item, ref Owner? __state)
    {
        __state = _owner;
        var tint = item.TextureTintColor;
        var peak = Math.Max(tint.X, Math.Max(tint.Y, tint.Z));
        _owner = new Owner(blast.FiringWeaponComponentId, peak > 0 ? tint / peak : Vector3.One);
    }
    private static void Restore(Owner? __state) => _owner = __state;
    private static void Quad(Vector4 finish, ref Vector4 color, ref Vector4 firing)
    {
        if (finish.W >= 0 || _owner is not { } owner || Entry.StyleOf(owner.Id) is not { } style) return;
        color = new Vector4(style.ChannelTint(3) ?? owner.Tint,
            color.W / style.ChannelBrightness(1) * style.BeamImpactBrightness * style.ImpactOpacity);
        if (firing.X >= 1000000) firing.X = (firing.X - 1000000) % 256 / 100f;
    }
}
