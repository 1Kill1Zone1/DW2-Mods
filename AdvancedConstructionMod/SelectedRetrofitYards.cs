using System.Reflection.Emit;
using DistantWorlds.Types;
using HarmonyLib;

namespace AdvancedConstructionMod;

internal static class SelectedRetrofitYards
{
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ConstructionSystem),"DoTasks"),
            prefix:new HarmonyMethod(typeof(SelectedRetrofitYards),nameof(BeforeYards)),
            transpiler:new HarmonyMethod(typeof(SelectedRetrofitYards),nameof(FilterWaiting)),
            finalizer:new HarmonyMethod(typeof(SelectedRetrofitYards),nameof(FinishYards)));
    }
    private static readonly System.Reflection.FieldInfo YardShip=AccessTools.Field(typeof(ConstructionYard),"_ShipUnderConstructionId");
    private static void BeforeYards(ConstructionSystem __instance,Galaxy galaxy,Empire empire,out object? __state)
    {
        __state=null;
        var player=empire?.IsPlayer==true?empire:galaxy.Empires.GetPlayer();if(player==null)return;
        // Allied yards can contain a player's order. Other AI yards never initialize ACM empire state.
        if(empire?.IsPlayer!=true&&!__instance.WaitQueue.Any(w=>w!=null&&galaxy.Ships.GetById(w.ShipUnderConstructionId)?.EmpireId==player.EmpireId)&&
            !__instance.Yards.Any(y=>y!=null&&galaxy.Ships.GetById((int)YardShip.GetValue(y)!)?.EmpireId==player.EmpireId))return;
        var registry=ConstructionCampaigns.Get(galaxy,player);
        if(!registry.TryGet(player,out var snapshot,out _)||snapshot.ManualRetrofits.Count==0)return;
        __state=registry.Gate;Monitor.Enter(__state);
    }
    private static Exception? FinishYards(Exception? __exception,object? __state)
    {if(__state!=null)Monitor.Exit(__state);return __exception;}
    private static IEnumerable<CodeInstruction> FilterWaiting(IEnumerable<CodeInstruction> source)
    {
        var code=source.ToList();int count=0;
        var field=AccessTools.Field(typeof(ConstructionSystem),"_WaitQueue");
        for(int i=0;i<code.Count;i++)
        {
            yield return code[i];
            if(i<2||code[i-2].opcode!=OpCodes.Ldfld||!Equals(code[i-2].operand,field)||
                code[i].operand is not System.Reflection.MethodInfo method||method.Name!="get_Item"||method.ReturnType!=typeof(ConstructionYardWait))continue;
            yield return new(OpCodes.Ldarg_3);
            yield return new(OpCodes.Call,AccessTools.Method(typeof(SelectedRetrofitYards),nameof(WaitingAllowed)));count++;
        }
        if(count!=2)throw new InvalidOperationException("Native yard wait loop no longer matches the supported beta build.");
    }
    internal static ConstructionYardWait? WaitingAllowed(ConstructionYardWait? wait,Galaxy galaxy)
    {
        if(wait==null||wait.ConstructionType!=ConstructionType.Retrofit)return wait;
        var ship=galaxy.Ships.GetById(wait.ShipUnderConstructionId);if(ship==null)return wait;
        var empire=ship.GetEmpireCached(galaxy);if(empire==null||!empire.IsPlayer)return wait;
        if(!ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var snapshot,out _))return null;
        if(!snapshot.ManualRetrofits.TryGetValue(ship.ShipId,out var order))return wait;
        return wait.DesignId==order.TargetDesignId&&SelectedRetrofit.Allows(ship,empire,galaxy.Designs.GetById(wait.DesignId),order)?wait:null;
    }
}
