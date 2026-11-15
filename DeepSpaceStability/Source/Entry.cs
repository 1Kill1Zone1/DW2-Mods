using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace DeepSpaceStability;

public static class Entry
{
    private const string PatchId = "local.dw2.deep-space-stability";

    public static void InitWithOptions(string optionsJson)
    {
        using var options = System.Text.Json.JsonDocument.Parse(optionsJson);
        Rally.Enabled = options.RootElement.TryGetProperty("RallyWhileRefuelling", out var setting) && setting.GetBoolean();
        Init();
    }

    public static void Init()
    {
        var target = AccessTools.DeclaredMethod(typeof(Location), nameof(Location.UpdateLocationPosition), [typeof(Point)])
            ?? throw new MissingMethodException(typeof(Location).FullName, nameof(Location.UpdateLocationPosition));
        if (Harmony.GetPatchInfo(target)?.Owners.Contains(PatchId) != true)
            new Harmony(PatchId).Patch(target, transpiler: new HarmonyMethod(typeof(Entry), nameof(Rebase)));
        Rally.Install();
    }

    private static IEnumerable<CodeInstruction> Rebase(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var locals = __originalMethod.GetMethodBody()?.LocalVariables;
        var expected = new[]
        {
            OpCodes.Ldarg_1, OpCodes.Ldfld, OpCodes.Conv_R4, OpCodes.Ldarg_0,
            OpCodes.Ldfld, OpCodes.Conv_R4, OpCodes.Sub, OpCodes.Stloc_0,
            OpCodes.Ldarg_1, OpCodes.Ldfld, OpCodes.Conv_R4, OpCodes.Ldarg_0,
            OpCodes.Ldfld, OpCodes.Conv_R4, OpCodes.Sub, OpCodes.Stloc_1
        };
        bool supported = code.Count > expected.Length && locals is { Count: >= 2 } &&
            locals[0].LocalType == typeof(float) && locals[1].LocalType == typeof(float) &&
            expected.Select((opcode, index) => code[index].opcode == opcode).All(match => match) &&
            code[1].LoadsField(AccessTools.Field(typeof(Point), nameof(Point.X))) &&
            code[4].LoadsField(AccessTools.Field(typeof(Location), nameof(Location.GalaxyX))) &&
            code[9].LoadsField(AccessTools.Field(typeof(Point), nameof(Point.Y))) &&
            code[12].LoadsField(AccessTools.Field(typeof(Location), nameof(Location.GalaxyY)));
        if (!supported)
            throw new InvalidOperationException("Unsupported Location.UpdateLocationPosition body; Deep Space Stability was not applied.");

        // Native object updates convert local positions back to galaxy coordinates inside the loop.
        // Publish the new origin after computing its delta and before that conversion, not after the loop.
        code.InsertRange(expected.Length,
        [
            new(OpCodes.Ldarg_0),
            new(OpCodes.Ldarg_1),
            new(OpCodes.Ldloca_S, (byte)0),
            new(OpCodes.Ldloca_S, (byte)1),
            new(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Entry), nameof(PrepareOrigin)))
        ]);
        return code;
    }

    private static void PrepareOrigin(Location location, Point next, ref float deltaX, ref float deltaY)
    {
        if (!location.IsTemporary) return;
        // Subtract integer coordinates first: absolute galactic floats lose small rebase distances.
        deltaX = (float)((long)next.X - location.GalaxyX);
        deltaY = (float)((long)next.Y - location.GalaxyY);
        location.GalaxyX = next.X;
        location.GalaxyY = next.Y;
    }
}
