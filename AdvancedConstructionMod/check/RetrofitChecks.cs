using AdvancedConstructionMod;
using DistantWorlds.Types;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static partial class Checks
{
    private static bool yardAllowed;
    private static int yardTarget;
    private static bool yardGateHeld;
    private static object yardGate=null!;
    private static void ObserveYard(bool __runOriginal,int designId){yardAllowed=__runOriginal;yardTarget=designId;yardGateHeld=Monitor.IsEntered(yardGate);}
    private static void ThrowYardAdmission()=>throw new IOException("Injected yard admission failure");
    private static bool SkipYard()=>false;
    private static void RetrofitChecks(Galaxy galaxy,Empire empire,Design h1,Design h2,Design beam,string root)
    {
        Console.WriteLine("Native checks: retrofit admission and installed Ship Logistics.");
        var ship=galaxy.Ships.First(s=>s!=null&&!s.IsDestroyed&&s.EmpireId==empire.EmpireId&&!s.IsBase);
        ship.Role=ShipRole.Escort;
        ship.SetGalaxy(galaxy);ship.Design=h1;h1.AllowAutoRetrofit=true;h1.UpgradeDesignId=h2.DesignId;
        ship.UpdateBonuses();ship.RegenerateSummary();
        Require(!ship.ShouldRetrofit(empire,beam,false),"actual Ship.ShouldRetrofit rejects another class");
        Require(ship.ShouldRetrofit(empire,h2,false),"actual Ship.ShouldRetrofit permits matching class");
        h1.AllowAutoRetrofit=false;
        Require(!ship.ShouldRetrofit(empire,h2,false),"native AllowAutoRetrofit remains authoritative");h1.AllowAutoRetrofit=true;
        var choose=AccessTools.Method(typeof(Ship),"ShouldRetrofit",[typeof(Empire),typeof(bool),typeof(bool),typeof(Design).MakeByRefType(),typeof(ResourceQuantityList).MakeByRefType(),typeof(float).MakeByRefType(),typeof(int)]);
        object?[] args=[empire,true,false,null,new ResourceQuantityList(),0f,beam.DesignId];
        Require(choose.Invoke(ship,args) is false&&args[3]==null,"actual fleet template cannot bypass class restriction");
        args=[empire,true,false,null,new ResourceQuantityList(),0f,-1];
        Require(choose.Invoke(ship,args) is true&&ReferenceEquals(args[3],h2),"actual automatic selection recomputes native target inside class");
        args=[empire,true,false,null,new ResourceQuantityList(),0f,h1.DesignId];
        Require(choose.Invoke(ship,args) is true&&ReferenceEquals(args[3],h2),"same-series fleet template is advanced to the latest project before native costs are computed");
        var oldMission=ship.Mission;
        Require(!ship.AssignMissionRetrofit(null!,beam,null!,true)&&ReferenceEquals(oldMission,ship.Mission),"manual Retrofit assignment cannot silently change class");
        var yard=AccessTools.GetDeclaredMethods(typeof(ConstructionSystem)).Where(m=>m.Name=="AddShipToConstruct").OrderByDescending(m=>m.GetParameters().Length).First();
        var system=galaxy.Ships.First(s=>s?.ConstructionSystem!=null).ConstructionSystem;
        yardGate=ConstructionCampaigns.Get(galaxy).Gate;
        var sentry=new Harmony("acm.check.yard-sentry");
        sentry.Patch(yard,prefix:new HarmonyMethod(typeof(Checks),nameof(ObserveYard)){priority=-10});
        sentry.Patch(yard,prefix:new HarmonyMethod(typeof(Checks),nameof(SkipYard)){priority=-20});
        void Queue(int target,bool expected,int resulting)
        {
            yardAllowed=!expected;yardTarget=-999;
            yard.Invoke(system,[galaxy,empire,empire.EmpireId,ship,ConstructionType.Retrofit,target,false,null,null,false,false]);
            Require(yardAllowed==expected&&yardTarget==resulting,"actual yard admission expected="+expected+", target="+resulting);
            Require(yardGateHeld,"native yard admission holds the same transaction gate as series commit");
        }
        Queue(beam.DesignId,false,beam.DesignId);Queue(h2.DesignId,true,h2.DesignId);
        var installed=Path.Combine(root,"mods","3815304122");
        using var metadata=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(installed,"mod.json")));
        Require(metadata.RootElement.GetProperty("version").GetString()=="1.0.40","compatibility uses installed Ship Logistics1.0.40");
        var module=AssemblyLoadContext.Default.LoadFromAssemblyPath(Directory.GetFiles(installed,"*.dll").Single());
        var latest=module.GetType("DW2ShipLogistics.LatestDesign",true)!;
        var host=module.GetTypes().Single(t=>t.Name=="ModHost");
        AccessTools.Field(host,"ErrorSink").SetValue(null,(Action<string>)(s=>throw new InvalidOperationException("Ship Logistics: "+s)));
        var actualPrefix=AccessTools.Method(latest,"Prefix");var sl=new Harmony("acm.check.actual-ship-logistics");
        sl.Patch(yard,prefix:new HarmonyMethod(actualPrefix));
        AccessTools.Field(typeof(Ship),"MissionAssignmentType").SetValue(ship,ShipMissionAssignmentType.FullyAutomate);
        Queue(beam.DesignId,true,h2.DesignId);
        AccessTools.Field(typeof(Ship),"MissionAssignmentType").SetValue(ship,ShipMissionAssignmentType.Manual);
        Queue(beam.DesignId,false,beam.DesignId);
        // Equal registration order cannot override the final priority of our admission check.
        var ours=Harmony.GetPatchInfo(yard)!.Prefixes.Single(p=>p.owner=="local.dw2.advanced-construction");
        new Harmony(ours.owner).Unpatch(yard,ours.PatchMethod);
        new Harmony(ours.owner).Patch(yard,prefix:new HarmonyMethod(ours.PatchMethod){priority=ours.priority});
        Queue(beam.DesignId,false,beam.DesignId);
        AccessTools.Field(typeof(Ship),"MissionAssignmentType").SetValue(ship,ShipMissionAssignmentType.FullyAutomate);
        Queue(beam.DesignId,true,h2.DesignId);
        sentry.Patch(yard,prefix:new HarmonyMethod(typeof(Checks),nameof(ThrowYardAdmission)){priority=-30});
        ExpectFailure(()=>yard.Invoke(system,[galaxy,empire,empire.EmpireId,ship,ConstructionType.Retrofit,h2.DesignId,false,null,null,false,false]),"native yard failure propagates");
        object admissionGate=ConstructionCampaigns.Get(galaxy).Gate;
        Require(Task.Run(()=>{if(!Monitor.TryEnter(admissionGate,500))return false;Monitor.Exit(admissionGate);return true;}).GetAwaiter().GetResult(),"native yard failure finalizer releases series transaction gate");
        sentry.UnpatchAll(sentry.Id);sl.UnpatchAll(sl.Id);
        var wait=(ConstructionYardWait)RuntimeHelpers.GetUninitializedObject(typeof(ConstructionYardWait));
        int previousArrow=h1.UpgradeDesignId;h1.UpgradeDesignId=-2;
        var directIncoming=empire.Designs.Where(d=>d.UpgradeDesignId==h2.DesignId).ToArray();
        foreach(var design in directIncoming)design.UpgradeDesignId=-2;
        wait.ShipUnderConstructionId=ship.ShipId;wait.DesignId=h2.DesignId;wait.ConstructionType=ConstructionType.Retrofit;
        system.WaitQueue.Add(wait);
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var state,out _),"live branch graph available before queue case");
        string before=Serialized(state);var staged=state.Clone();string other=ConstructionGraph.CreateClass(staged,"Queued conflict");
        Require(!ConstructionCommands.TryApply(galaxy,empire,h2,state.Revision,other,[],Facts(galaxy,empire),out _,out var error,staged)&&error!.Contains("queued",StringComparison.OrdinalIgnoreCase),"class reclassification rejects an already queued incompatible retrofit with a reason");
        Require(registry.TryGet(empire,out state,out _)&&Serialized(state)==before&&system.WaitQueue.Contains(wait),"queue conflict leaves class graph and existing order unchanged");
        system.WaitQueue.Remove(wait);
        var yardFields=typeof(ConstructionSystem).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)
            .Where(f=>typeof(IEnumerable<ConstructionYard>).IsAssignableFrom(f.FieldType)).ToArray();
        Require(yardFields.Length>0,"native active construction-yard collection is discovered");
        var activeYard=(ConstructionYard)RuntimeHelpers.GetUninitializedObject(typeof(ConstructionYard));
        system.Yards.Add(activeYard);
        var shipField=AccessTools.Field(typeof(ConstructionYard),"_ShipUnderConstructionId");
        int savedShip=(int)shipField.GetValue(activeYard)!,savedDesign=activeYard.DesignId;var savedType=activeYard.ConstructionType;
        try
        {
            shipField.SetValue(activeYard,ship.ShipId);activeYard.DesignId=h2.DesignId;activeYard.ConstructionType=ConstructionType.Retrofit;
            Require(!ConstructionCommands.TryApply(galaxy,empire,h2,state.Revision,other,[],Facts(galaxy,empire),out _,out error,staged)&&error!.Contains("construction yard"),"already-started yard retrofit blocks series mutation even without a waiting entry");
            Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"active-yard rejection leaves metadata unchanged");
        }
        finally{system.Yards.Remove(activeYard);}
        foreach(var design in directIncoming)design.UpgradeDesignId=h2.DesignId;
        h1.UpgradeDesignId=previousArrow;
        var galaxyField=AccessTools.Field(typeof(Ship),"_Galaxy");var priorGalaxy=galaxyField.GetValue(ship);galaxyField.SetValue(ship,null);
        try{Require(!RetrofitAdmission.Allows(ship,empire,beam),"final admission without a campaign fails closed");}
        finally{galaxyField.SetValue(ship,priorGalaxy);}
    }
}
