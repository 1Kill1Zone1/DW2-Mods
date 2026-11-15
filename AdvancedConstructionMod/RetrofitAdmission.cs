using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;

namespace AdvancedConstructionMod;

// Final admission is shared by template selection, manual Retrofit and the yard queue.
internal static class RetrofitAdmission
{
    private static readonly FieldInfo GalaxyField = AccessTools.Field(typeof(Ship), "_Galaxy");
    private static readonly FieldInfo YardShipField=AccessTools.Field(typeof(ConstructionYard),"_ShipUnderConstructionId");

    internal static void Install(Harmony harmony)
    {
        MethodInfo simple = AccessTools.DeclaredMethod(typeof(Ship), "ShouldRetrofit", [typeof(Empire),typeof(Design),typeof(bool)])
            ?? throw new MissingMethodException("Ship.ShouldRetrofit(Empire,Design,bool)");
        harmony.Patch(simple, postfix:new HarmonyMethod(typeof(RetrofitAdmission),nameof(AfterShouldRetrofit)) { priority=Priority.Last });
        MethodInfo selection = AccessTools.DeclaredMethod(typeof(Ship), "ShouldRetrofit", [typeof(Empire),typeof(bool),typeof(bool),typeof(Design).MakeByRefType(),typeof(ResourceQuantityList).MakeByRefType(),typeof(float).MakeByRefType(),typeof(int)])
            ?? throw new MissingMethodException("Ship.ShouldRetrofit target selection");
        harmony.Patch(selection,prefix:new HarmonyMethod(typeof(RetrofitAdmission),nameof(BeforeSelection)));
        MethodInfo assign = AccessTools.DeclaredMethod(typeof(Ship), "AssignMissionRetrofit", [typeof(StellarObject),typeof(Design),typeof(List<int>),typeof(bool)])
            ?? throw new MissingMethodException("Ship.AssignMissionRetrofit");
        harmony.Patch(assign,prefix:new HarmonyMethod(typeof(RetrofitAdmission),nameof(BeforeAssignment)) { priority=Priority.Last },finalizer:new HarmonyMethod(typeof(RetrofitAdmission),nameof(FinishAdmission)));
        MethodInfo command = AccessTools.DeclaredMethod(typeof(Ship), "CommandRetrofit", [typeof(Empire),typeof(DateTime),typeof(float),typeof(Location),typeof(int)])
            ?? throw new MissingMethodException("Ship.CommandRetrofit");
        harmony.Patch(command,prefix:new HarmonyMethod(typeof(RetrofitAdmission),nameof(BeforeCommand)) { priority=Priority.Last },finalizer:new HarmonyMethod(typeof(RetrofitAdmission),nameof(FinishAdmission)));
        MethodInfo yard = AccessTools.GetDeclaredMethods(typeof(ConstructionSystem)).Where(m=>m.Name=="AddShipToConstruct")
            .OrderByDescending(m=>m.GetParameters().Length).First();
        harmony.Patch(yard,prefix:new HarmonyMethod(typeof(RetrofitAdmission),nameof(BeforeYard)) { priority=Priority.Last },finalizer:new HarmonyMethod(typeof(RetrofitAdmission),nameof(FinishAdmission)));
        var waiting=AccessTools.DeclaredMethod(typeof(ConstructionSystem),"AddToWaitQueue",[typeof(Galaxy),typeof(Empire),typeof(Ship),typeof(ConstructionType),typeof(int)])
            ??throw new MissingMethodException("ConstructionSystem.AddToWaitQueue");
        harmony.Patch(waiting,prefix:new HarmonyMethod(typeof(RetrofitAdmission),nameof(BeforeYard)){priority=Priority.Last},finalizer:new HarmonyMethod(typeof(RetrofitAdmission),nameof(FinishAdmission)));
    }

    internal static Galaxy? GalaxyOf(Ship ship) => GalaxyField.GetValue(ship) as Galaxy;

    internal static bool Allows(Ship ship, Empire empire, Design? target)
    {
        Galaxy? galaxy=GalaxyOf(ship);
        if(galaxy==null) return false;
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!registry.TryGet(empire,out var state,out _))return false;
            Design? source=ship.GetDesign();
            if(source==null || !state.DesignClasses.TryGetValue(source.DesignId,out var sourceClass))return true;
            return source.UpgradeDesignId!=ConstructionGraph.Disabled && target!=null && !target.Obsolete &&
                target.DesignId!=source.DesignId && source.EmpireId==empire.EmpireId && target.EmpireId==empire.EmpireId &&
                state.DesignClasses.TryGetValue(target.DesignId,out var targetClass) && targetClass==sourceClass &&
                source.ResolveRetrofitDesignId(galaxy,empire)==target.DesignId &&
                RetrofitRuntime.NativeCompatible(RetrofitRuntime.Fact(galaxy,empire,source),RetrofitRuntime.Fact(galaxy,empire,target));
        }
    }

    private static void AfterShouldRetrofit(Ship __instance,Empire empire,Design __1,ref bool __result)
    { if(__result && !Allows(__instance,empire,__1))__result=false; }

    private static bool BeforeSelection(Ship __instance,Empire empire,ref int templateRoleDesignId,ref Design retrofitDesign,ref float retrofitSize,ref bool __result)
    {
        Galaxy? galaxy=GalaxyOf(__instance);
        if(galaxy==null || empire==null){retrofitDesign=null!;retrofitSize=0;__result=false;return false;}
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!registry.TryGet(empire,out var state,out _)){retrofitDesign=null!;retrofitSize=0;__result=false;return false;}
            Design? source=__instance.GetDesign();
            if(source==null || !state.DesignClasses.ContainsKey(source.DesignId))return true;
            Design? target;
            if(templateRoleDesignId>=0&&(!state.DesignClasses.TryGetValue(templateRoleDesignId,out var templateClass)||templateClass!=state.DesignClasses[source.DesignId]))
            {retrofitDesign=null!;retrofitSize=0;__result=false;return false;}
            source.ResolveRetrofitDesignId(galaxy,empire,out target);
            if(!Allows(__instance,empire,target)){retrofitDesign=null!;retrofitSize=0;__result=false;return false;}
            templateRoleDesignId=target!.DesignId;
            return true;
        }
    }

    private static bool BeforeAssignment(Ship __instance,ref Design retrofitDesign,out object? __state)
    {
        __state=null;
        Galaxy? galaxy=GalaxyOf(__instance);
        Empire? empire=galaxy==null?null:__instance.GetEmpireCached(galaxy);
        if(empire==null)return false;
        var registry=ConstructionCampaigns.Get(galaxy!,empire);
        Monitor.Enter(registry.Gate);__state=registry.Gate;
        if(!registry.TryGet(empire,out var state,out _))return false;
        var source=__instance.GetDesign();
        if(source!=null&&state.DesignClasses.TryGetValue(source.DesignId,out var ownClass))
        {
            if(retrofitDesign==null||!state.DesignClasses.TryGetValue(retrofitDesign.DesignId,out var targetClass)||targetClass!=ownClass)return false;
            source.ResolveRetrofitDesignId(galaxy!,empire,out var latest);retrofitDesign=latest!;
        }
        return Allows(__instance,empire,retrofitDesign);
    }
    private static bool BeforeCommand(Ship __instance,Empire empire,ref int designId,out object? __state)
    {
        __state=null;
        Galaxy? galaxy=GalaxyOf(__instance);
        if(galaxy==null)return false;
        var gate=ConstructionCampaigns.Get(galaxy,empire).Gate;Monitor.Enter(gate);__state=gate;
        if(Allows(__instance,empire,galaxy.Designs.GetById(designId)))return true;
        // A design may have changed class while the ship was travelling to the yard.
        // Re-evaluate the ship's existing rule, without allowing a cross-class fallback.
        var source=__instance.GetDesign();
        if(source==null)return false;
        source.ResolveRetrofitDesignId(galaxy,empire,out var latest);
        if(!Allows(__instance,empire,latest))return false;
        designId=latest.DesignId;return true;
    }
    private static bool BeforeYard(Galaxy galaxy,Empire empire,Ship ship,ConstructionType constructionType,int designId,out object? __state)
    {
        __state=null;
        if(constructionType!=ConstructionType.Retrofit || ship==null)return true;
        var owner=ship.GetEmpireCached(galaxy);
        var gate=ConstructionCampaigns.Get(galaxy).Gate;Monitor.Enter(gate);__state=gate;
        return owner!=null && Allows(ship,owner,galaxy.Designs.GetById(designId));
    }
    private static Exception? FinishAdmission(Exception? __exception,object? __state)
    {if(__state!=null)Monitor.Exit(__state);return __exception;}

    internal static bool QueuedChangesAllowed(Galaxy galaxy,Empire empire,ConstructionSnapshot current,ConstructionPreview preview,out string? error)
    {
        error=null;var next=preview.Next!;
        var affected=preview.UpgradeValues.Where(p=>galaxy.Designs.GetById(p.Key)?.UpgradeDesignId!=p.Value).Select(p=>p.Key).ToHashSet();
        foreach(int id in current.DesignClasses.Keys.Union(next.DesignClasses.Keys))
            if(current.DesignClasses.GetValueOrDefault(id)!=next.DesignClasses.GetValueOrDefault(id))affected.Add(id);
        var changedTargets=current.Targets.Keys.Union(next.Targets.Keys).Where(k=>current.Targets.GetValueOrDefault(k,-1)!=next.Targets.GetValueOrDefault(k,-1)).ToHashSet();
        // Adding/removing a member can change Automatic latest for every remaining project.
        var movedSeries=current.DesignClasses.Keys.Union(next.DesignClasses.Keys)
            .Where(id=>current.DesignClasses.GetValueOrDefault(id)!=next.DesignClasses.GetValueOrDefault(id))
            .SelectMany(id=>new[]{current.DesignClasses.GetValueOrDefault(id),next.DesignClasses.GetValueOrDefault(id)}).Where(id=>id!=null).ToHashSet();
        foreach(var design in empire.Designs)
            if(current.DesignClasses.TryGetValue(design.DesignId,out var a)&&(movedSeries.Contains(a)||changedTargets.Contains(ConstructionSnapshot.TargetKey(a,(int)design.GetRole(galaxy))))||
                next.DesignClasses.TryGetValue(design.DesignId,out var b)&&(movedSeries.Contains(b)||changedTargets.Contains(ConstructionSnapshot.TargetKey(b,(int)design.GetRole(galaxy)))))affected.Add(design.DesignId);
        foreach(int id in (preview.ObsoleteValues??new Dictionary<int,bool>()).Keys)affected.Add(id);
        if(affected.Count==0)return true;
        foreach(var ship in empire.Ships)
            if(affected.Contains(ship.DesignId)&&ship.Mission?.Type==ShipMissionType.Retrofit)
            {error=$"{ship.Name} already has a Retrofit order. Cancel it or let it finish before changing this series.";return false;}
        var systems=galaxy.Ships.Where(s=>s!=null&&s.ConstructionSystem!=null).Select(s=>s.ConstructionSystem)
            .Concat(galaxy.Orbs.Where(o=>o?.Colony?.ConstructionSystem!=null).Select(o=>o.Colony!.ConstructionSystem)).Distinct();
        foreach(var system in systems)
        {
        foreach(var wait in system.WaitQueue.ToArray())
        {
            if(wait==null||wait.ConstructionType!=ConstructionType.Retrofit)continue;
            var ship=galaxy.Ships.GetById(wait.ShipUnderConstructionId);
            if(ship==null||ship.EmpireId!=empire.EmpireId||(!affected.Contains(ship.DesignId)&&!affected.Contains(wait.DesignId)))continue;
            {error=$"{ship.Name} is already queued for a retrofit. Cancel it or let it finish before changing this branch.";return false;}
        }
        foreach(var yard in system.Yards.ToArray())
        {
            if(yard==null||yard.ConstructionType!=ConstructionType.Retrofit)continue;
            var ship=galaxy.Ships.GetById((int)YardShipField.GetValue(yard)!);
            if(ship==null||ship.EmpireId!=empire.EmpireId||(!affected.Contains(ship.DesignId)&&!affected.Contains(yard.DesignId)))continue;
            error=$"{ship.Name} is already being retrofitted at a construction yard. Let it finish before changing this series.";return false;
        }
        }
        return true;
    }
}
