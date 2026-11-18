using AdvancedConstructionMod;
using DistantWorlds.Types;
using HarmonyLib;

internal static partial class Checks
{
    private static void SelectedRetrofitLifecycleChecks(Galaxy galaxy,Empire empire,Design source,Design target,List<Ship> ships,Func<Galaxy,Galaxy> roundtrip)
    {
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var live,out _),"accepted intent state available");
        string dirty=Serialized(live),stored=Serialized(Read(Storage(galaxy,empire.EmpireId))!);
        var edited=live.Clone();edited.Revision++;throwAfterHead=true;
        Require(!registry.TryCommit(empire,edited,out _)&&registry.TryGet(empire,out live,out _)&&Serialized(live)==dirty,"failed editor metadata commit preserves dirty accepted intents and usable registry");
        Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==stored,"failed editor commit retains exact prior Matrix bank");
        var reloaded=roundtrip(galaxy);var restoredEmpire=reloaded.Empires.GetById(empire.EmpireId);
        var restored=Read(Storage(reloaded,empire.EmpireId))!;
        Require(Serialized(restored)==dirty,"native save/load retains all eight accepted partial/full orders");
        foreach(var ship in ships.Take(8))
        {
            var restoredShip=reloaded.Ships.GetById(ship.ShipId);restoredShip.SetGalaxy(reloaded);
            Require(restoredShip.Mission?.Type==ShipMissionType.Retrofit&&restoredShip.Mission.BuildDesignId==target.DesignId&&SelectedRetrofit.HasOrder(restoredShip,restoredEmpire,out var pin)&&SelectedRetrofit.Matches(restoredShip,pin),"native journey mission plus exact pin survives reload "+ship.ShipId);
        }
        Require(SelectedRetrofit.HasOrder(ships[6],empire,out var dirtyOrder)&&registry.SetManualOrder(empire,ships[6].ShipId,dirtyOrder,out _),"stage a newer intent revision before the save failure case");
        string prefix="construction."+empire.EmpireId,head=getSaved(galaxy,Owner,prefix+".head");
        string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
        byte[] accepted=ShipBytes(ships[6]);rejectedKey=prefix+"."+bank+".0";
        try{ExpectFailure(()=>roundtrip(galaxy),"Matrix save preflight refuses unverified intent persistence");}finally{rejectedKey=null;}
        Require(getSaved(galaxy,Owner,prefix+".head")==head&&ShipBytes(ships[6]).SequenceEqual(accepted),"refused save retains prior save metadata and live accepted order for retry");
        Require(SelectedRetrofit.HasOrder(ships[6],empire,out var exact),"exact intent remains active before policy change");
        string targetName=target.Name;bool obsolete=target.Obsolete;int oldUpgrade=target.UpgradeDesignId;
        target.Name="Renamed accepted target";target.Obsolete=true;target.UpgradeDesignId=source.DesignId;
        Require(SelectedRetrofit.Allows(ships[6],empire,target,exact),"accepted payload remains exact despite name/obsolete/upgrade policy change");
        target.Name=targetName;target.Obsolete=obsolete;target.UpgradeDesignId=oldUpgrade;
        // The automatic native Core path records the kept job; unlike the explicit refuel command it does not discard that job.
        var fuelShip=ships[5];var fuelYard=fuelShip.Mission.PrimaryTarget.ResolveBase<StellarObject>(galaxy)!;
        var core=AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m=>m.Name=="AssignMissionCore"&&m.GetParameters().Length==8&&m.GetParameters()[0].ParameterType==typeof(ShipMission));
        void FuelDetour(Ship actor,Galaxy world,StellarObject point)
        {
            var mission=new ShipMission(world,ShipMissionType.Refuel,point,null!,null!,-1,-1,-1,-1,new CargoList(),null!,new ContractStubList(),new List<int>(),false);
            core.Invoke(actor,[mission,new List<int>(),actor.ResourceReservations,false,false,false,false,true]);
        }
        FuelDetour(fuelShip,galaxy,fuelYard);
        Require(fuelShip.Mission.Type==ShipMissionType.Refuel&&fuelShip.RevertMission?.Type==ShipMissionType.Retrofit&&SelectedRetrofit.HasOrder(fuelShip,empire,out var fuelPin)&&SelectedRetrofit.Matches(fuelShip,fuelPin),"actual automatic fuel detour retains the exact accepted retrofit as native saved goal");
        var fuelSave=roundtrip(galaxy);var loadedFuel=fuelSave.Ships.GetById(fuelShip.ShipId);loadedFuel.SetGalaxy(fuelSave);var fuelEmpire=fuelSave.Empires.GetById(empire.EmpireId);
        Require(loadedFuel.Mission.Type==ShipMissionType.Refuel&&SelectedRetrofit.HasOrder(loadedFuel,fuelEmpire,out var loadedPin)&&SelectedRetrofit.Matches(loadedFuel,loadedPin),"native save/load during fuel detour retains exact saved goal and individual pin");
        Require((bool)AccessTools.Method(typeof(Ship),"RevertToPreviousMission").Invoke(loadedFuel,[fuelEmpire,fuelSave.Time])!&&loadedFuel.Mission.Type==ShipMissionType.Retrofit&&loadedFuel.Mission.BuildDesignId==target.DesignId&&loadedFuel.Mission.IsManuallyAssigned&&SelectedRetrofit.HasOrder(loadedFuel,fuelEmpire,out _),"actual native replay after load resumes the exact manual target without dropping its pin");
        Require((bool)AccessTools.Method(typeof(Ship),"RevertToPreviousMission").Invoke(fuelShip,[empire,galaxy.Time])!&&SelectedRetrofit.HasOrder(fuelShip,empire,out _),"actual native replay preserves original live selected order");
        FuelDetour(fuelShip,galaxy,fuelYard);
        var clear=GameTask.GenerateTaskClearMission(galaxy,empire,ships[5]);empire.ExecuteGameTask(clear,true,false);
        Require(!SelectedRetrofit.HasOrder(ships[5],empire,out _)&&ships[5].Mission?.Type!=ShipMissionType.Retrofit,"actual native cancel releases individual pin");
        Require(ships[5].MissionAssignmentType==ShipMissionAssignmentType.Manual,"cancel does not resurrect previous automation decision");
        var destination=empire.GetFastestConstructionYardRetrofit(ships[6],target,true,null!,out _);
        ships[6].AssignMissionMove(destination,null!,true);
        Require(!SelectedRetrofit.HasOrder(ships[6],empire,out _)&&ships[6].Mission?.Type!=ShipMissionType.Retrofit,"new actual native manual movement releases pin without reissuing retrofit");
        Require(ships[6].MissionAssignmentType==ShipMissionAssignmentType.Manual,"new manual command retains manual control");
        var afterCancel=roundtrip(galaxy);var afterState=Read(Storage(afterCancel,empire.EmpireId))!;
        Require(!afterState.ManualRetrofits.ContainsKey(ships[5].ShipId)&&!afterState.ManualRetrofits.ContainsKey(ships[6].ShipId)&&afterState.ManualRetrofits.Count==6,"native save after cancellation cannot resurrect cancelled orders");
    }
}
