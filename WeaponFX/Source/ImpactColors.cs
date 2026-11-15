using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class ImpactColors
{
    private sealed record Owner(short Id, bool LegacyColor = true)
    {
        internal long PreviewStarted { get; init; }
    }
    private static readonly ConditionalWeakTable<object, Owner> Owners = new();
    [ThreadStatic] private static Owner? _owner;
    private static Func<WeaponBlast?> _current = null!;

    internal static void Install(Harmony harmony, Assembly assembly)
    {
        const string prefix = "DW2R_Visual_Effects.Patches.";
        var impact = assembly.GetType(prefix + "MissileImpact", true)!;
        _current = AccessTools.PropertyGetter(assembly.GetType(prefix + "ShotAim", true)!, "Current").CreateDelegate<Func<WeaponBlast?>>();
        foreach (var name in new[] { "Hit", "ShieldContact", "BoltHit", "KineticArmorFlash" })
            harmony.Patch(AccessTools.Method(impact, name), prefix: new HarmonyMethod(typeof(ImpactColors), nameof(Context)),
                finalizer: new HarmonyMethod(typeof(ImpactColors), nameof(Restore)));
        harmony.Patch(AccessTools.Method(assembly.GetType(prefix + "MissileTrail", true)!, "ShotDown"),
            prefix: new HarmonyMethod(typeof(ImpactColors), nameof(EmitterContext)), finalizer: new HarmonyMethod(typeof(ImpactColors), nameof(Restore)));
        harmony.Patch(AccessTools.Method(impact, "Keep"), prefix: new HarmonyMethod(typeof(ImpactColors), nameof(Keep)));
        harmony.Patch(AccessTools.Method(impact, "Fill"), transpiler: new HarmonyMethod(typeof(ImpactColors), nameof(Code)));
        ImpactPreview.Install(harmony, assembly, impact);
    }

    private static void Context(object[] __args, MethodBase __originalMethod, ref Owner? __state)
    {
        __state = _owner;
        var shot = __args.OfType<WeaponBlast>().FirstOrDefault() ?? _current();
        _owner = shot == null ? null : new Owner(shot.FiringWeaponComponentId, __originalMethod.Name is "Hit" or "ShieldContact");
    }
    private static void EmitterContext(object emitter, ref Owner? __state)
    {
        __state = _owner;
        _owner = ProjectileEffects.EmitterId(emitter) is { } id ? new Owner(id, false) : null;
    }
    private static void Restore(Owner? __state) => _owner = __state;
    internal static void Preview(short id, Action draw)
    {
        var prior = _owner;
        _owner = new Owner(id) { PreviewStarted = Stopwatch.GetTimestamp() };
        try { draw(); }
        finally { _owner = prior; }
    }
    internal static bool TryCurrentStyle(out short id, out Entry.Style style, out long previewStarted)
    {
        if (_owner is { } owner && Entry.StyleOf(owner.Id) is { } current)
        {
            id = owner.Id;
            style = current;
            previewStarted = owner.PreviewStarted;
            return true;
        }
        id = default;
        style = null!;
        previewStarted = default;
        return false;
    }
    private static void Keep(object impact)
    {
        if (_owner is { } owner) Owners.GetValue(impact, _ => owner);
    }
    private static Vector3 Tint(Vector3 original, object impact)
    {
        if (!Owners.TryGetValue(impact, out var owner) || Entry.StyleOf(owner.Id) is not { } style) return original;
        if (!owner.LegacyColor && style.ImpactColor == null && !style.ImpactFollowsBody) return original;
        var tint = style.ChannelTint(3) ?? original;
        // Keep the tint marker away from the shader's threshold even with a zero channel.
        return MissileDepth.ImpactChannelsReady && (style.ImpactFollowsBody ? style.ChannelColor(3) >= 0 :
            style.ImpactColor >= 0 || style.ChannelColor(3) == 0) ? tint + new Vector3(14) : tint;
    }
    private static float Turn(float turn, object impact)
    {
        if (!Owners.TryGetValue(impact, out var owner) || Entry.StyleOf(owner.Id) is not { } style || !MissileDepth.ImpactChannelsReady) return turn;
        var brightness = style.RveImpactBrightness;
        if (style.ImpactOpacity == 1 && brightness == 1) return turn;
        var angle = ((turn % MathF.Tau) + MathF.Tau) % MathF.Tau;
        return 1000 + MathF.Round(angle / MathF.Tau * 255) + 256 * (MathF.Round(style.ImpactOpacity * 100) + 101 * MathF.Round(brightness * 100));
    }

    private static double Start(double start, object impact) =>
        Owners.TryGetValue(impact, out var owner) && owner.PreviewStarted != 0
            ? ImpactPreview.GameTime() - Stopwatch.GetElapsedTime(owner.PreviewStarted).TotalSeconds
            : start;

    private static IEnumerable<CodeInstruction> Code(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var count = 0;
        for (var i = 0; i < code.Count; i++)
        {
            yield return code[i];
            if (code[i].opcode != OpCodes.Ldfld || code[i].operand is not FieldInfo { Name: "Tint" or "Turn" or "Start", DeclaringType.Name: "Impact" } field) continue;
            if (i == 0 || !code[i - 1].IsLdloc()) throw new InvalidOperationException("Unrecognized impact color owner.");
            yield return new CodeInstruction(code[i - 1].opcode, code[i - 1].operand);
            yield return CodeInstruction.Call(typeof(ImpactColors), field.Name);
            count++;
        }
        if (count != 4) throw new InvalidOperationException("Unrecognized impact color path.");
    }
}
