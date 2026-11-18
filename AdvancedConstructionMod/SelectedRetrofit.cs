using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DistantWorlds.Types;
using HarmonyLib;

namespace AdvancedConstructionMod;

internal static class SelectedRetrofit
{
    [ThreadStatic] private static Dispatch? dispatch;
    [ThreadStatic] private static Ship? replay;
    private static readonly MethodInfo JumpPath=AccessTools.Method(typeof(Ship),"CheckNullJumpPathAndCalculate",[typeof(List<int>),typeof(StellarObject),typeof(ShipMissionType)]);
    private static readonly ConditionalWeakTable<Galaxy,ConcurrentDictionary<int,string>> Problems=new();
    private sealed class Dispatch(Ship ship,Design target)
    {
        internal readonly Ship Ship=ship;
        internal readonly Design Target=target;
    }

    internal static string Fingerprint(Design design)
    {
        using var data=new MemoryStream();using var writer=new BinaryWriter(data,Encoding.UTF8,true);
        writer.Write(design.EmpireId);writer.Write(design.HullId);
        design.Tactics.WriteToStream(writer);design.Components.WriteToStream(writer);
        writer.Flush();return Convert.ToHexString(SHA256.HashData(data.ToArray()));
    }

    internal static string? ShipReason(Galaxy galaxy,Empire empire,Ship ship)
    {
        if(ship.EmpireId!=empire.EmpireId||!ReferenceEquals(galaxy.Ships.GetById(ship.ShipId),ship)||ship.IsDestroyed)return "Ship is missing, destroyed or no longer owned";
        if(ship.IsBase||Galaxy.CheckRoleIsBase(ship.Role))return "Only mobile ships can use this command";
        if(ship.IsUnderConstructionOrRepair||ship.IsBeingBuiltByConstructionShip())return "Current construction or yard work must finish first";
        if(Problem(galaxy,ship.ShipId) is string blocked&&HasOrder(ship,empire,out _))return "Retrofit blocked: "+blocked;
        if(ship.Mission?.Type==ShipMissionType.Retrofit||ship.NextMissions.Any(m=>m?.Type==ShipMissionType.Retrofit)||ship.RevertMission?.Type==ShipMissionType.Retrofit||InYard(galaxy,ship))return "Already ordered, queued or in a retrofit yard";
        if(ship.Summary==null||ship.Summary.TopSpeed<=0)return "Ship cannot move";
        if(ship.IsHyperjumping(ship.GetDesign()))return "Wait for the current hyperjump to finish";
        if(ship.ConstructionSystem?.IsConstructing()==true)return "Finish or cancel the ship's active construction work first";
        if(ship.Mission?.ContractStubs.Count>0||ship.Mission?.RelatedQuestId>=0)return "Finish or explicitly cancel the active contract/quest first";
        if(ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var state,out var error)&&state.ManualRetrofits.ContainsKey(ship.ShipId))return "An individual retrofit order is already active";
        return error;
    }

    internal static string? Reason(Galaxy galaxy,Empire empire,Ship ship,Design target)
    {
        try
        {
            string? reason=TargetReason(galaxy,empire,ship,target,false);if(reason!=null)return reason;
            reason=ShipReason(galaxy,empire,ship);if(reason!=null)return reason;
            var native=GameTask.GenerateTaskRetrofitShip(galaxy,empire,ship,true,true).FirstOrDefault(t=>t.Values.Count>0&&t.Values[0]==target.DesignId);
            if(native==null)return "Native retrofit rules do not permit this hull/project";
            if(!native.Enabled)return string.IsNullOrWhiteSpace(native.DisabledReason)?"Native retrofit is unavailable":native.DisabledReason;
            var yard=empire.GetFastestConstructionYardRetrofit(ship,target,true,null!,out _);
            if(yard==null)return "No native construction yard available";
            var resources=target.GetRetrofitResources(galaxy,empire,ship.GetDesign());
            if(resources.Count>0&&(yard.GetCargo()==null||!yard.GetCargo().CheckResourcesPresent(empire.EmpireId,resources,out _)))return "Native yard lacks required resources";
            return null;
        }
        catch(Exception ex){return "Native eligibility unavailable: "+ex.GetBaseException().Message;}
    }
    private static string? TargetReason(Galaxy galaxy,Empire empire,Ship ship,Design? target,bool accepted)
    {
        var source=ship.GetDesign();
        if(source==null||source.EmpireId!=empire.EmpireId)return "Current project is unavailable";
        if(target==null||target.EmpireId!=empire.EmpireId||!ReferenceEquals(empire.Designs.GetById(target.DesignId),target)||!ReferenceEquals(galaxy.Designs.GetById(target.DesignId),target))return "Exact target project is missing or no longer owned";
        if(!accepted&&target.Obsolete)return "Select an active project";
        if(!accepted&&source.DesignId==target.DesignId)return "Already using this project";
        if(source.GetRole(galaxy)!=target.GetRole(galaxy))return "Native ship roles do not match";
        if(!RetrofitRuntime.Fact(galaxy,empire,target).Buildable)return "Target hull or components are not researched or valid";
        return null;
    }
    internal static string Estimate(Galaxy galaxy,Empire empire,IEnumerable<Ship> ships,Design target)
    {
        var rows=new List<string>();double total=0;bool available=true;
        foreach(var ship in ships)
        {
            try
            {
                float cost=target.CalculateRetrofitCost(galaxy,empire,ship.GetDesign(),out var resources);
                if(!float.IsFinite(cost)||cost<0)throw new InvalidOperationException("Native cost is not available");
                total+=cost;rows.Add(ship.Name+": "+cost.ToString("N0")+" credits; "+TextHelper.ResolveDescription(resources,false,true));
            }
            catch{available=false;rows.Add(ship.Name+": cost/resources estimate unavailable");}
        }
        return (available?"Native approximate total: "+total.ToString("N0")+" credits.":"Total estimate unavailable.")+" Native payment applies when an order is accepted; yard resource rules remain native.\n"+string.Join("\n",rows);
    }
    internal static bool InYard(Galaxy galaxy,Ship ship)
    {
        var parent=ship.Parent.ResolveBase<StellarObject>(galaxy);
        var system=parent is Ship port?port.ConstructionSystem:(parent as Orb)?.Colony?.ConstructionSystem;
        return system?.ContainsShipWithLock(ship)==true||ship.RetrofitConstructionSystem?.ContainsShipWithLock(ship)==true;
    }

    internal static string[] Send(Galaxy galaxy,Empire empire,IEnumerable<Ship> selected,Design target,string fingerprint,IReadOnlyDictionary<int,int>? sourceDesigns=null)
    {
        var result=new List<string>();var registry=ConstructionCampaigns.Get(galaxy,empire);
        // This command is invoked by the main UI. Drain native workers before taking the save/series gate:
        // workers may themselves need that gate, and rollback must not overwrite concurrent cargo changes.
        bool priorTasks=Galaxy.AllowNewTasks,priorExit=TaskHelper.ExitTasksEarly;
        try
        {
        galaxy.WaitAllTasksCompleted();
        lock(registry.Gate)
        {
            foreach(var ship in selected.DistinctBy(s=>s.ShipId))
            {
                string? reason=sourceDesigns!=null&&sourceDesigns.GetValueOrDefault(ship.ShipId,-1)!=ship.DesignId?"Current project changed after review":Reason(galaxy,empire,ship,target);
                if(reason==null&&fingerprint!=Fingerprint(target))reason="Target project changed after confirmation";
                if(reason!=null){result.Add(ship.Name+": UNCHANGED - "+reason);continue;}
                try{result.Add(ship.Name+": "+(Assign(galaxy,empire,ship,target,fingerprint,out reason)?"Order issued -> "+target.Name:"UNCHANGED - "+reason));}
                catch(Exception ex){result.Add(ship.Name+": UNCHANGED - "+ex.GetBaseException().Message);}
            }
        }
        return result.ToArray();
        }
        finally{TaskHelper.ExitTasksEarly=priorExit;Galaxy.AllowNewTasks=priorTasks;}
    }

    internal static SelectedRetrofitResult[] Send(Galaxy galaxy,Empire empire,IEnumerable<SelectedRetrofitRequest> requests)
    {
        var result=new List<SelectedRetrofitResult>();var registry=ConstructionCampaigns.Get(galaxy,empire);
        bool priorTasks=Galaxy.AllowNewTasks,priorExit=TaskHelper.ExitTasksEarly;
        try
        {
            galaxy.WaitAllTasksCompleted();
            lock(registry.Gate)
            {
                foreach(var request in requests.DistinctBy(r=>r.Plan.Ship.ShipId))
                {
                    var plan=request.Plan;bool issued=false;string? reason;
                    try
                    {
                        reason=request.Reason(galaxy,empire);
                        if(reason==null)issued=Assign(galaxy,empire,plan.Ship,plan.Target!,plan.Fingerprint!,out reason);
                    }
                    catch(Exception ex){reason=ex.GetBaseException().Message;}
                    result.Add(new(plan.Ship.ShipId,issued,plan.Ship.Name+": "+(issued?"Order issued -> "+plan.Target!.Name:"UNCHANGED - "+reason)));
                }
            }
            return result.ToArray();
        }
        finally{TaskHelper.ExitTasksEarly=priorExit;Galaxy.AllowNewTasks=priorTasks;}
    }

    private static bool Assign(Galaxy galaxy,Empire empire,Ship ship,Design target,string fingerprint,out string? error)
    {
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        var yard=empire.GetFastestConstructionYardRetrofit(ship,target,true,null!,out _);
        if(yard==null){error="No native construction yard available";return false;}
        var path=(List<int>?)JumpPath.Invoke(ship,[null,yard,ShipMissionType.Retrofit]);
        if(path==null){error="No native route to the construction yard";return false;}
        var missionTarget=new ShipMission(galaxy,ShipMissionType.Retrofit,yard,null!,null!,-1,-1,-1,-1,new CargoList(),target,new ContractStubList(),path,true);
        var order=new ManualRetrofitOrder(ship.DesignId,target.DesignId,fingerprint,(int)ship.MissionAssignmentType,galaxy.StarDate,(int)missionTarget.PrimaryTargetType,missionTarget.PrimaryTarget.Id);
        var rollback=new RetrofitRollback(galaxy,empire,ship,yard);var oldReservations=ship.ResourceReservations;
        if(!registry.SetManualOrder(empire,ship.ShipId,order,out error))return false;
        var previous=dispatch;var current=new Dispatch(ship,target);dispatch=current;
        bool assigned=false;
        try
        {
            // The native method clears this list before checking reservations; preserve the old order until success.
            ship.ResourceReservations=new CargoReservationList();
            assigned=empire.ExecuteTaskRetrofitShip(yard,ship,target,true,false,false);
            if(!assigned||!Matches(ship,order))throw new InvalidOperationException("Native assignment did not accept the exact retrofit order");
            ship.MissionAssignmentType=ShipMissionAssignmentType.Manual;
            foreach(var reservation in oldReservations.ToArray())galaxy.UnreserveResources(reservation);
            oldReservations.Clear();error=null;return true;
        }
        catch(Exception ex)
        {
            // Native workers are quiescent; restore the bounded yard/reservation/mission graph together.
            // Do not call another fallible native resource mutation while recovering an exception.
            rollback.Restore();
            _=registry.SetManualOrder(empire,ship.ShipId,null,out _);error=ex.GetBaseException().Message;return false;
        }
        finally{dispatch=previous;}
    }

    internal static bool HasOrder(Ship ship,Empire empire,out ManualRetrofitOrder order)
    {
        order=null!;var galaxy=RetrofitAdmission.GalaxyOf(ship);if(galaxy==null)return false;
        return ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var state,out _)&&state.ManualRetrofits.TryGetValue(ship.ShipId,out order!);
    }
    private static bool MissionMatches(ShipMission? mission,ManualRetrofitOrder order)=>mission!=null&&mission.Type==ShipMissionType.Retrofit&&mission.IsManuallyAssigned&&mission.BuildDesignId==order.TargetDesignId&&
        (int)mission.PrimaryTargetType==order.YardType&&mission.PrimaryTarget.Id==order.YardId;
    internal static bool Matches(Ship ship,ManualRetrofitOrder order)=>MissionMatches(ship.Mission,order)||ship.NextMissions.Any(m=>MissionMatches(m,order))||MissionMatches(ship.RevertMission,order);
    internal static bool Allows(Ship ship,Empire empire,Design? target,ManualRetrofitOrder order)
    {
        var galaxy=RetrofitAdmission.GalaxyOf(ship);if(galaxy==null)return false;
        bool issuing=ReferenceEquals(dispatch?.Ship,ship)&&ReferenceEquals(dispatch.Target,target);
        if(!issuing&&(!Matches(ship,order)||ship.MissionAssignmentType!=ShipMissionAssignmentType.Manual))return false;
        if(ship.EmpireId!=empire.EmpireId||ship.IsDestroyed)return false;
        if(target?.DesignId!=order.TargetDesignId){Problems.GetOrCreateValue(galaxy)[ship.ShipId]="The exact accepted project is missing; cancel this order and review another project";return false;}
        if(ship.DesignId!=order.SourceDesignId&&ship.DesignId!=order.TargetDesignId){Problems.GetOrCreateValue(galaxy)[ship.ShipId]="The ship's current project changed after this order was accepted";return false;}
        string? error;
        try
        {
            error=TargetReason(galaxy,empire,ship,target,true);
            if(error==null&&Fingerprint(target!)!=order.TargetFingerprint)error="Exact target project changed; cancel this order and review a new target";
        }
        catch(Exception ex){error="Exact target eligibility is unavailable: "+ex.GetBaseException().Message;}
        if(error!=null){Problems.GetOrCreateValue(galaxy)[ship.ShipId]=error;return false;}
        Problems.GetOrCreateValue(galaxy).TryRemove(ship.ShipId,out _);return true;
    }
    internal static string? Problem(Galaxy galaxy,int shipId)=>Problems.GetOrCreateValue(galaxy).GetValueOrDefault(shipId);

    internal static void Observe(Ship ship)
    {
        if(ship==null)return;
        if(ReferenceEquals(dispatch?.Ship,ship)||ReferenceEquals(replay,ship))return;
        var galaxy=RetrofitAdmission.GalaxyOf(ship);if(galaxy==null)return;
        var empire=ship.GetEmpireCached(galaxy);if(empire==null||!empire.IsPlayer)return;
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!HasOrder(ship,empire,out var order))return;
            bool completed=ship.DesignId==order.TargetDesignId&&!ship.IsUnderConstructionOrRepair&&!InYard(galaxy,ship);
            bool cancelled=!Matches(ship,order)&&!InYard(galaxy,ship)||ship.MissionAssignmentType!=ShipMissionAssignmentType.Manual;
            if(!completed&&!cancelled)return;
            if(!registry.SetManualOrder(empire,ship.ShipId,null,out _))return;
            Problems.GetOrCreateValue(galaxy).TryRemove(ship.ShipId,out _);
            if(completed&&ship.MissionAssignmentType==ShipMissionAssignmentType.Manual)ship.MissionAssignmentType=(ShipMissionAssignmentType)order.PreviousControl;
        }
    }
    private static void BeforeReplay(Ship __instance,out Ship? __state)
    {
        __state=replay;
        var galaxy=RetrofitAdmission.GalaxyOf(__instance);var empire=galaxy==null?null:__instance.GetEmpireCached(galaxy);
        if(empire?.IsPlayer==true&&HasOrder(__instance,empire,out var order)&&MissionMatches(__instance.RevertMission,order))replay=__instance;
    }
    private static void BeforeMission(Ship __instance,ShipMission mission,bool revertingPreviousMission,out Ship? __state)
    {
        __state=replay;
        if(!revertingPreviousMission)return;
        var galaxy=RetrofitAdmission.GalaxyOf(__instance);var empire=galaxy==null?null:__instance.GetEmpireCached(galaxy);
        if(empire?.IsPlayer==true&&HasOrder(__instance,empire,out var order)&&MissionMatches(mission,order)&&Matches(__instance,order))replay=__instance;
    }
    private static Exception? FinishMission(Ship __instance,Ship? __state,Exception? __exception)
    {replay=__state;Observe(__instance);return __exception;}
    private static void AfterMission(Ship __instance,ShipMission mission,bool manuallyAssigned,bool revertingPreviousMission)
    {
        // Native replay resets IsManuallyAssigned. Preserve only the already accepted exact manual order.
        if(revertingPreviousMission&&ReferenceEquals(replay,__instance)&&ReferenceEquals(__instance.Mission,mission))
        {
            var galaxy=RetrofitAdmission.GalaxyOf(__instance);var empire=galaxy==null?null:__instance.GetEmpireCached(galaxy);
            if(empire?.IsPlayer==true&&HasOrder(__instance,empire,out var order)&&mission.Type==ShipMissionType.Retrofit&&mission.BuildDesignId==order.TargetDesignId&&
                (int)mission.PrimaryTargetType==order.YardType&&mission.PrimaryTarget.Id==order.YardId)mission.IsManuallyAssigned=true;
        }
        if(!ReferenceEquals(dispatch?.Ship,__instance)&&!ReferenceEquals(replay,__instance)&&manuallyAssigned)
        {
            var galaxy=RetrofitAdmission.GalaxyOf(__instance);var empire=galaxy==null?null:__instance.GetEmpireCached(galaxy);
            if(empire?.IsPlayer==true)ConstructionCampaigns.Get(galaxy!,empire).SetManualOrder(empire,__instance.ShipId,null,out _);
        }
        Observe(__instance);
    }
    private static void AfterCancel(Empire __instance,GameTask task)
    {
        if(!__instance.IsPlayer||task==null||(int)task.Type!=135||!ConstructionCampaigns.TryGetGalaxy(__instance,out var galaxy))return;
        IEnumerable<Ship> affected=task.Entities.Count>0&&(int)task.Entities[0].Type==5
            ?galaxy.Fleets.GetById(task.Entities[0].Id)?.Ships??Enumerable.Empty<Ship>()
            :task.Entities.Where(e=>(int)e.Type==2).Select(e=>galaxy.Ships.GetById(e.Id)).Where(s=>s!=null);
        var registry=ConstructionCampaigns.Get(galaxy,__instance);
        lock(registry.Gate)
            foreach(var ship in affected)
                if(ship.EmpireId==__instance.EmpireId&&HasOrder(ship,__instance,out var order))
                {
                    // Native cancellation clears the current detour but can retain its saved goal.
                    // Discard only this cancelled command's exact saved goal; never restore an old control mode.
                    if(MissionMatches(ship.RevertMission,order))ship.RevertMission=null!;
                    registry.SetManualOrder(__instance,ship.ShipId,null,out _);
                    Problems.GetOrCreateValue(galaxy).TryRemove(ship.ShipId,out _);
                }
    }
    private static void AfterClear(Ship __instance)=>Observe(__instance);
    private static void AfterBuild(Ship ship,bool shipCompleted)
    {
        if(!shipCompleted)return;
        var galaxy=RetrofitAdmission.GalaxyOf(ship);var empire=galaxy==null?null:ship.GetEmpireCached(galaxy);
        if(empire==null||!empire.IsPlayer)return;
        var registry=ConstructionCampaigns.Get(galaxy!,empire);
        lock(registry.Gate)
            if(HasOrder(ship,empire,out var order)&&ship.DesignId==order.TargetDesignId&&registry.SetManualOrder(empire,ship.ShipId,null,out _)&&ship.MissionAssignmentType==ShipMissionAssignmentType.Manual)
                ship.MissionAssignmentType=(ShipMissionAssignmentType)order.PreviousControl;
    }
    private static void AfterComplete(Ship ship)=>Observe(ship);
    internal static void Install(Harmony harmony)
    {
        var core=AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m=>m.Name=="AssignMissionCore"&&m.GetParameters().Length==8&&m.GetParameters()[0].ParameterType==typeof(ShipMission));
        harmony.Patch(AccessTools.Method(typeof(Empire),"ExecuteTaskClearShipMission"),postfix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(AfterCancel)));
        harmony.Patch(AccessTools.Method(typeof(Ship),"RevertToPreviousMission"),prefix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(BeforeReplay)),finalizer:new HarmonyMethod(typeof(SelectedRetrofit),nameof(FinishMission)));
        harmony.Patch(core,prefix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(BeforeMission)),postfix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(AfterMission)),finalizer:new HarmonyMethod(typeof(SelectedRetrofit),nameof(FinishMission)));
        harmony.Patch(AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m=>m.Name=="ClearPreviousMissionRequirements"&&m.GetParameters().Length==5),postfix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(AfterClear)));
        harmony.Patch(AccessTools.Method(typeof(Ship),"ProcessMission"),prefix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(AfterClear)));
        harmony.Patch(AccessTools.Method(typeof(ConstructionSystem),"BuildShip"),postfix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(AfterBuild)));
        harmony.Patch(AccessTools.Method(typeof(ShipMission),"CompleteMission"),postfix:new HarmonyMethod(typeof(SelectedRetrofit),nameof(AfterComplete)));
    }
}
