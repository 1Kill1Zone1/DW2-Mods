using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Versioning;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class ShieldTrace
{
    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(BattleData), "ProcessShieldImpacts", [typeof(float), typeof(List<Vector4>), typeof(float)]),
        transpiler: new HarmonyMethod(typeof(ShieldTrace), nameof(Code)));

    // RVE's W>=1 is missile growth, (.1,.5) collapse, and (0,.1] the ordinary shield mark. Only the last is tunable.
    internal static float Decay(float remaining, float decrement) => remaining - decrement /
        (!Entry.Bypass && remaining > 0 && remaining <= .1f ? Entry.ShieldTraceDuration : 1f);

    private static IEnumerable<CodeInstruction> Code(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var matches = 0;
        for (var i = 0; i < code.Count; i++)
        {
            var op = code[i];
            if (i >= 2 && op.opcode == OpCodes.Sub && code[i - 2].operand is FieldInfo { DeclaringType: var type, Name: "W" } && type == typeof(Vector4))
            {
                op = new CodeInstruction(op) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(ShieldTrace), nameof(Decay)) };
                matches++;
            }
            yield return op;
        }
        if (matches != 1) throw new InvalidOperationException("Unrecognized shield mark decay.");
    }
}
