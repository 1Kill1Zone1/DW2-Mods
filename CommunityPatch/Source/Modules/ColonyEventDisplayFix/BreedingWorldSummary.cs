using System.Reflection.Emit;
using DistantWorlds.Types;
using HarmonyLib;

namespace ColonyEventDisplayFix;

// A render-local projection only. Never edit a shared Bonus, event instance or definition.
internal static class BreedingWorldSummary
{
    private sealed record Display(string Worlds,string Race,float Total);

    private static Display? Capture(Galaxy galaxy,Empire empire,ColonyEventList? events,OrbList? worlds,bool endDateAfterName)
    {
        try
        {
            if(endDateAfterName||events==null||worlds==null||events.Count==0||events.Count!=worlds.Count)return null;
            var seenWorlds=new HashSet<int>();var seenEvents=new HashSet<ColonyEvent>(ReferenceEqualityComparer.Instance);var lines=new List<string>();float total=0;
            for(int i=0;i<events.Count;i++)
            {
                var item=events[i];var world=worlds[i];var definition=item?.Event;
                if(item==null||world?.Colony==null||definition==null||item.ColonyEventDefinitionId!=73||definition.ColonyEventDefinitionId!=73||
                    definition.ValuesDecayGradually||definition.Bonuses?.Count!=1||!item.IsCurrent(galaxy.StarDate)||
                    !ReferenceEquals(galaxy.Orbs.GetById(world.OrbId),world)||!ReferenceEquals(empire.Colonies.GetById(world.OrbId),world)||
                    world.Colony.EmpireId!=empire.EmpireId||!world.Colony.Events.Any(e=>ReferenceEquals(e,item))||
                    !seenWorlds.Add(world.OrbId)||!seenEvents.Add(item))return null;
                var bonus=definition.Bonuses[0];
                if(bonus==null||bonus.Type!=BonusType.ColonyPopulationGrowthRate||bonus.AppliesTo!=BonusAppliesTo.Empire||bonus.FilterId!=11||
                    !float.IsFinite(bonus.Amount)||bonus.Amount<0||!float.IsFinite(item.EventValueFactor))return null;
                // Mirror the verified racial-growth branch of Empire.CalculateBonuses: raw
                // additive Amount times a nonnegative instance factor. GetBonusAmount instead
                // reads GetAmountByType's 1 + amount multiplier and is not a growth delta.
                float contribution=bonus.Amount*(item.EventValueFactor>=0?item.EventValueFactor:1);
                if(!float.IsFinite(contribution)||contribution<0)return null;
                total+=contribution;lines.Add(world.Name+": "+contribution.ToString("+0.##%;-0.##%;0%"));
            }
            var race=galaxy.Races.GetById(11);
            return race==null||!float.IsFinite(total)?null:new(string.Join("\n",lines),race.Name,total);
        }
        catch{return null;} // Unknown or concurrently changing payload: preserve native display.
    }
    private static string Worlds(string native,Display? display)=>display?.Worlds??native;
    private static string Label(string native,Display? display)=>display==null?native:"Total - "+display.Race+" "+native;
    private static float Amount(float native,Display? display)=>display?.Total??native;

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
    {
        var code=instructions.ToList();var display=generator.DeclareLocal(typeof(Display));
        var resolve=AccessTools.DeclaredMethod(typeof(TextHelper),nameof(TextHelper.ResolveDescription),[typeof(BonusType)]);
        var amount=AccessTools.Field(typeof(Bonus),nameof(Bonus.Amount));
        // Verified Steam 1.3.8.7 anchors: the sole completed world-name string load, the
        // bonus-type label resolver and Amount * event factor. Fail installation on drift.
        int names=0,labels=0,values=0;
        var output=new List<CodeInstruction>
        {
            new(OpCodes.Ldarg_S,(byte)10),new(OpCodes.Ldarg_S,(byte)11),new(OpCodes.Ldarg_S,(byte)12),
            new(OpCodes.Ldarg_S,(byte)13),new(OpCodes.Ldarg_S,(byte)15),
            new(OpCodes.Call,AccessTools.Method(typeof(BreedingWorldSummary),nameof(Capture))),new(OpCodes.Stloc,display)
        };
        for(int i=0;i<code.Count;i++)
        {
            output.Add(code[i]);string? helper=null;
            if(code[i].IsLdloc()&&code[i].operand is LocalBuilder local&&local.LocalIndex==30&&
                i+1<code.Count&&code[i+1].IsLdarg(5)){helper=nameof(Worlds);names++;}
            if(code[i].Calls(resolve)){helper=nameof(Label);labels++;}
            if(code[i].opcode==OpCodes.Mul&&i>=2&&code[i-2].LoadsField(amount)){helper=nameof(Amount);values++;}
            if(helper!=null){output.Add(new(OpCodes.Ldloc,display));output.Add(new(OpCodes.Call,AccessTools.Method(typeof(BreedingWorldSummary),helper)));}
        }
        if(names!=1||labels!=1||values!=1)throw new NotSupportedException($"Breeding World native display anchors differ ({names}/{labels}/{values}); no partial patch installed.");
        return output;
    }
}
