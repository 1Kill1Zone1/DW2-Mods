using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DistantWorlds.Types;
using HarmonyLib;

[assembly: InternalsVisibleTo("ColonySupportRules.Check")]

namespace ColonySupportRules;

public static class Entry
{
    internal const string PatchId = "local.dw2.colony-support-rules";
    internal const string EngineHash = "62F03F35A7EE8BBC7BF93AC7D184D614209631A4F68C488224E6CE9C9E2A0E23";
    private const string PreviousEngineHash = "787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254";
    static readonly Harmony Hooks = new(PatchId);
    static bool installed;
    internal static readonly MethodInfo NativeSupport = AccessTools.DeclaredMethod(typeof(Colony), nameof(Colony.CalculateColonySupportCosts),
        [typeof(Orb), typeof(float), typeof(long), typeof(float), typeof(float)]);
    static readonly MethodInfo NativeScale = AccessTools.DeclaredMethod(typeof(Colony), "CalculatePopulationFillFactorScaled", [typeof(float).MakeByRefType()]);
    delegate float PopulationScale(Colony colony, out float millions);
    static readonly PopulationScale Scale = AccessTools.MethodDelegate<PopulationScale>(NativeScale);

    public static void Init()
    {
        if (installed) return;
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Colony).Assembly.Location))) is not (EngineHash or PreviousEngineHash))
            throw new NotSupportedException("Colony Support Rules 0.1.1 requires a verified Steam beta engine; no economic changes installed.");
        var forecastAware = AccessTools.DeclaredMethod(typeof(Colony), nameof(Colony.CalculateColonySupportCosts),
            [typeof(Empire), typeof(Orb), typeof(long), typeof(Race), typeof(float), typeof(float)]);
        var annual = AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == nameof(Colony.CalculateAnnualRevenue) && m.GetParameters().Length == 8);
        try
        {
            Hooks.Patch(forecastAware, transpiler: new HarmonyMethod(typeof(Entry), nameof(ReplaceSupport)));
            Hooks.Patch(annual, transpiler: new HarmonyMethod(typeof(Entry), nameof(ReplaceSupport)));
            installed = true;
        }
        catch { Hooks.UnpatchAll(PatchId); throw; }
    }

    static IEnumerable<CodeInstruction> ReplaceSupport(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
    {
        int count = 0, incomeCount = 0;
        foreach (var item in input)
        {
            var code = new CodeInstruction(item);
            bool income = !__originalMethod.IsStatic && code.Calls(NativeScale);
            if (!code.Calls(NativeSupport) && !income) { yield return code; continue; }
            // Supply the actual payer: foreign colonization forecasts must not inherit the current owner's policy.
            var payer = new CodeInstruction(__originalMethod.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_2);
            payer.labels.AddRange(code.labels); code.labels.Clear();
            payer.blocks.AddRange(code.blocks); code.blocks.Clear();
            if(income)
            {
                var target=new CodeInstruction(OpCodes.Ldarg_1);
                target.labels.AddRange(payer.labels);payer.labels.Clear();
                target.blocks.AddRange(payer.blocks);payer.blocks.Clear();
                yield return target;
            }
            yield return payer;
            code.opcode = OpCodes.Call;
            code.operand = AccessTools.DeclaredMethod(typeof(Entry), income ? nameof(IncomePopulation) : nameof(Calculate));
            yield return code;
            if(income) incomeCount++; else count++;
        }
        if (count != 1 || incomeCount != (__originalMethod.IsStatic ? 0 : 1))
            throw new InvalidOperationException("Native colony income/support no longer matches the verified beta: " + __originalMethod);
    }

    internal static float Calculate(Orb orb, float suitability, long maximumPopulation, float populationAmount, float assimilation, Empire payer)
        => Adjust(orb, payer, Colony.CalculateColonySupportCosts(orb, suitability, maximumPopulation, populationAmount, assimilation));

    static float IncomePopulation(Colony colony, out float millions, Orb orb, Empire payer)
    {
        float fill = Scale(colony, out millions);
        // Reduce production before development, corruption and taxes; support is calculated separately.
        if (float.IsFinite(millions) && millions >= 0 && ReferenceEquals(orb?.Colony,colony) && TryShare(orb, payer, out double share))
            millions = (float)(millions * (1d - share));
        return fill;
    }

    internal static float Adjust(Orb? orb, Empire? payer, float original)
    {
        if (!float.IsFinite(original) || original <= 0f || !TryShare(orb,payer,out double share)) return original;
        return (float)(original * (1d - .8d * share));
    }

    static bool TryShare(Orb? orb, Empire? payer, out double share)
    {
        share = 0;
        if (payer == null ||
            orb?.Colony is not { Type: ColonyType.Colony } colony || colony.EmpireId != payer.EmpireId ||
            colony.Population == null || colony.PopulationPolicy == null) return false;
        try
        {
            double total = 0, exterminated = 0;
            // Copy the collection so a UI calculation does not enumerate a concurrently edited population list.
            foreach (var resident in colony.Population.ToArray())
            {
                if (resident == null || resident.Amount < 0) return false;
                var policy = colony.PopulationPolicy.GetPolicyForRace(resident.RaceId);
                if (!Enum.IsDefined(policy)) return false;
                total += resident.Amount;
                if (policy == ColonyPopulationPolicyType.Exterminate) exterminated += resident.Amount;
            }
            if (!(total > 0) || total > long.MaxValue || !double.IsFinite(total) || exterminated <= 0) return false;
            share = Math.Clamp(exterminated / total,0d,1d);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (IndexOutOfRangeException) { return false; }
    }
}
