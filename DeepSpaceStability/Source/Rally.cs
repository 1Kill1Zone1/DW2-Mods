using System.Reflection;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace DeepSpaceStability;

internal static class Rally
{
    internal static bool Enabled;
    private static bool installed;
    [ThreadStatic] private static int ownOrder;
    [ThreadStatic] private static int dispatch;
    [ThreadStatic] private static int uiInput;
    [ThreadStatic] private static RallyRuntime? worker;
    [ThreadStatic] private static Fleet? workerFleet;
    [ThreadStatic] private static int workerRevision;
    // Match native Fleet.GatherShips' 64,000,000 squared-distance tolerance.
    private const float ArrivalRadius = 8000;

    internal static void Install()
    {
        if (installed) return;
        var h = new Harmony("local.dw2.deep-space-stability.refuel");
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(UserInterfaceController).Module.ModuleHandle);
        var ui = typeof(UserInterfaceController).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Single(m=>m.Name=="ExecuteAction"&&m.GetParameters().Length==7);
        h.Patch(ui, prefix:new HarmonyMethod(typeof(Rally),nameof(UiEntered)), finalizer:new HarmonyMethod(typeof(Rally),nameof(UiExited)));
        var core = typeof(Fleet).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "AssignMissionCore" && m.GetParameters().Length == 11);
        h.Patch(core, postfix: new HarmonyMethod(typeof(Rally), nameof(FleetOrdered)) { priority = Priority.Last });
        h.Patch(AccessTools.Method(typeof(Empire), nameof(Empire.ExecuteGameTask), [typeof(GameTask), typeof(bool), typeof(bool), typeof(bool)]), prefix: new HarmonyMethod(typeof(Rally), nameof(TaskEntered)) { priority = Priority.First });
        h.Patch(AccessTools.Method(typeof(Fleet), nameof(Fleet.DoTasks), [typeof(DateTime), typeof(Location)]), postfix: new HarmonyMethod(typeof(Rally), nameof(Reviewed)) { priority = Priority.Last });
        h.Patch(AccessTools.Method(typeof(Fleet), nameof(Fleet.GatherShips)), prefix: new HarmonyMethod(typeof(Rally), nameof(AllowGather)));
        h.Patch(AccessTools.Method(typeof(Fleet), nameof(Fleet.SignalFuelTankersForRefuelling), [typeof(Empire), typeof(ResourceQuantityList), typeof(float), typeof(bool)]), prefix: new HarmonyMethod(typeof(Rally), nameof(Dispatch)) { priority = Priority.Last });
        var shipCore = typeof(Ship).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "AssignMissionCore" && m.GetParameters().Length == 8);
        h.Patch(shipCore, prefix:new HarmonyMethod(typeof(Rally),nameof(CurrentWorkerOrder)){priority=Priority.First}, postfix: new HarmonyMethod(typeof(Rally), nameof(ShipOrdered)) { priority = Priority.Last });
        foreach (var method in typeof(ShipList).GetMethods().Where(m => m.Name == "GetByLowFuelLevel"))
            h.Patch(method, postfix: new HarmonyMethod(typeof(Rally), nameof(NearestFirst)));
        h.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.AssignMissionTransferFuel)), prefix: new HarmonyMethod(typeof(Rally), nameof(KeepTransfer)), finalizer:new HarmonyMethod(typeof(Rally),nameof(TransferFinished)));
        installed = true;
    }

    private static bool Eligible(Fleet? fleet) => Enabled && fleet != null && fleet.Role == FleetRole.Manual && fleet.GetEmpire() is { IsPlayer: true } && fleet.Ships.Count <= 1024;
    private static bool Active(Fleet fleet, out RallyRuntime? runtime)
    {
        runtime = Eligible(fleet) ? RallyStore.For(fleet) : null;
        return runtime is { Faulted: false, State.Active: true };
    }
    private static bool Busy(Ship ship) => ship.IsDestroyed || ship.IsUnderConstructionOrRepair || ship.Threats?.TotalHostileStrength > 0 || ship.Threats?.TotalHostileMobileStrength > 0;
    private static double Distance(Ship ship, int x, int y) => Math.Pow((double)ship.GalaxyX - x, 2) + Math.Pow((double)ship.GalaxyY - y, 2);
    private static bool HasOrder(Ship ship) => ship.Mission != null && ship.Mission.Type != ShipMissionType.Undefined;
    private static void UiEntered(out int __state) { __state=uiInput;uiInput++; }
    private static Exception? UiExited(Exception? __exception, int __state) { uiInput=__state;return __exception; }

    private static void FleetOrdered(Fleet __instance, ShipMissionType missionType, bool manuallyAssigned, bool __runOriginal)
    {
        if (!__runOriginal || !manuallyAssigned || ownOrder > 0 || !Eligible(__instance)) return;
        var runtime = RallyStore.For(__instance);
        lock (runtime.Gate)
        {
            if (runtime.Faulted) return;
            runtime.State.Active = false; runtime.State.Excluded = []; runtime.State.HasGoal = false;
            runtime.Revision++;
            if (missionType == ShipMissionType.Move && __instance.Mission != null)
            {
                var point = __instance.Mission.ResolveMissionTargetCoordinates(RallyStore.GalaxyOf(__instance));
                if (point.X >= 0 && point.Y >= 0) { runtime.State.X = point.X; runtime.State.Y = point.Y; runtime.State.HasGoal = true; }
            }
            RallyStore.Save(__instance, runtime);
        }
    }

    private static IEnumerable<Fleet> Fleets(GameTask task, Empire empire)
    {
        if (task.Entities == null) yield break;
        var galaxy = RallyStore.EmpireGalaxy(empire);
        foreach (var item in task.Entities)
        {
            if (item.Type == ItemType.Fleet) { var fleet = item.Resolve(galaxy) as Fleet; if (fleet != null) yield return fleet; }
            else if (item.Type == ItemType.Ship) { var ship = item.Resolve(galaxy) as Ship; if (ship?.Fleet != null) yield return ship.Fleet; }
        }
    }

    private static bool TaskEntered(Empire __instance, GameTask task, bool manuallyExecuted, bool shipFleetMissionIsQueued)
    {
        if (!Enabled || (!manuallyExecuted && uiInput==0) || !__instance.IsPlayer || task == null) return true;
        if (task.Type == GameTaskType.ClearShipMission)
        {
            foreach(var item in task.Entities ?? [])
            {
                if(item.Type==ItemType.Fleet && item.Resolve(RallyStore.EmpireGalaxy(__instance)) is Fleet fleet)Cancel(fleet,true);
                else if(item.Type==ItemType.Ship && item.Resolve(RallyStore.EmpireGalaxy(__instance)) is Ship ship && ship.Fleet is {} memberFleet && Active(memberFleet,out var memberRuntime))
                    lock(memberRuntime!.Gate){memberRuntime.State.Excluded=new HashSet<int>(memberRuntime.State.Excluded){ship.ShipId};memberRuntime.Revision++;RallyStore.Save(memberFleet,memberRuntime);}
            }
            return true;
        }
        if (task.Type != GameTaskType.FleetSignalFuelTankers || !task.Enabled) return true;
        var selected = Fleets(task, __instance).FirstOrDefault();
        if (!Eligible(selected)) return true;
        var runtime = RallyStore.For(selected!);
        lock (runtime.Gate)
        {
            if (runtime.Faulted || !runtime.State.HasGoal) return true;
            // A repeated request keeps ship exclusions and the same destination.
            if (!runtime.State.Active) {runtime.State.Excluded=[];runtime.Revision++;}
            runtime.State.Active = true;
            if (!RallyStore.Save(selected!, runtime)) return true;
        }
        Review(selected!, DateTime.MinValue, true);
        return false;
    }

    private static void Cancel(Fleet fleet, bool forgetGoal)
    {
        if (!Eligible(fleet)) return;
        var runtime = RallyStore.For(fleet);
        lock (runtime.Gate) { runtime.State.Active = false; runtime.Revision++; if (forgetGoal) runtime.State.HasGoal = false; RallyStore.Save(fleet, runtime); }
    }
    private static void ShipOrdered(Ship __instance, ShipMission mission, bool manuallyAssigned, bool __runOriginal)
    {
        var fleet = __instance.Fleet;
        if (!__runOriginal || ownOrder > 0 || fleet == null || mission == null || !Active(fleet, out var runtime)) return;
        if(!manuallyAssigned)
        {
            if(mission.Type is not (ShipMissionType.Move or ShipMissionType.Undefined or ShipMissionType.TransferFuel))Interlocked.Increment(ref runtime!.Revision);
            return;
        }
        lock (runtime!.Gate) { runtime.State.Excluded=new HashSet<int>(runtime.State.Excluded){__instance.ShipId}; runtime.Revision++; RallyStore.Save(fleet, runtime); }
    }
    private static bool AllowGather(Fleet __instance) => !Active(__instance, out _);
    private static void Reviewed(Fleet __instance, DateTime time) => Review(__instance, time, false);

    private static void NearestFirst(Ship? fuelTanker, ref ShipList __result)
    {
        if (fuelTanker?.Fleet is not { } fleet || !Active(fleet, out _) || fuelTanker.Mission?.IsManuallyAssigned == true || __result == null) return;
        var ordered = new ShipList();
        foreach (var ship in __result.OrderBy(s => Distance(s, fuelTanker.GalaxyX, fuelTanker.GalaxyY)).ThenBy(s => s.ShipId)) ordered.Add(ship);
        __result = ordered;
    }
    private static bool CurrentWorkerOrder(Ship __instance)=>ownOrder==0&&dispatch==0||worker==null||Eligible(workerFleet)&&__instance.Fleet==workerFleet&&worker.State.Active&&Volatile.Read(ref worker.Revision)==workerRevision;
    private sealed record Claim(RallyRuntime Runtime,int Target);
    private static bool KeepTransfer(Ship __instance, Ship target, bool manuallyAssigned, out Claim? __state)
    {
        __state=null;
        if(dispatch>0&&worker!=null&&(!Eligible(workerFleet)||__instance.Fleet!=workerFleet||!worker.State.Active||Volatile.Read(ref worker.Revision)!=workerRevision))return false;
        if (manuallyAssigned || __instance.Fleet is not { } fleet || !Active(fleet, out _)) return true;
        var mission = __instance.Mission;
        var current = mission?.Type==ShipMissionType.TransferFuel?mission.PrimaryTarget.Resolve<Ship>(RallyStore.GalaxyOf(fleet)):null;
        if(current!=null&&!current.IsDestroyed&&current.Fleet==fleet&&!current.IsHyperjumping()&&current.Summary!=null&&(float)current.Fuel<.99f*current.Summary.FuelCapacity)return false;
        if(target==null||target.Fleet!=fleet)return true;
        var runtime=RallyStore.For(fleet);
        if(!runtime.Claims.TryAdd(target.ShipId,__instance.ShipId))return false;
        __state=new Claim(runtime,target.ShipId);
        if(target.CheckForIncomingFuelTanker(RallyStore.GalaxyOf(fleet),fleet.GetEmpire()!))
        {runtime.Claims.TryRemove(target.ShipId,out _);__state=null;return false;}
        return true;
    }
    private static Exception? TransferFinished(Exception? __exception,Claim? __state)
    {if(__state!=null)__state.Runtime.Claims.TryRemove(__state.Target,out _);return __exception;}

    internal static void Review(Fleet fleet, DateTime now, bool force)
    {
        if (!Active(fleet, out var runtime)) return;
        if(Interlocked.CompareExchange(ref runtime!.Reviewing,1,0)!=0)return;
        var previousWorker=worker;var previousFleet=workerFleet;int previousRevision=workerRevision;
        try
        {
            lock(runtime.Gate)
            {
                if (!force && (now - runtime.LastReview).TotalSeconds is >= 0 and < 2) return;
                runtime.LastReview = now;worker=runtime;workerFleet=fleet;workerRevision=runtime.Revision;
            }
            try
            {
                if (fleet.Mission != null && fleet.Mission.Type is not (ShipMissionType.Undefined or ShipMissionType.Move)) { Cancel(fleet, true); return; }
                var state = runtime.State; bool gathered = true, needsFuel = false;
                if (runtime.Restored)
                {
                    runtime.Restored = false;
                    // If a cancelled order could not be persisted, its stale record must not
                    // resurrect movement on reload. Normal in-flight members retain their Move.
                    bool outside = fleet.Ships.Any(s=>s.Role!=ShipRole.FuelTanker && s.Summary?.TopSpeed>0 && !Busy(s) && !state.Excluded.Contains(s.ShipId) && Distance(s,state.X,state.Y)>ArrivalRadius*ArrivalRadius);
                    bool continuing = fleet.Mission?.Type==ShipMissionType.Move || fleet.Ships.Any(s=>s.Mission?.Type==ShipMissionType.Move && s.Mission.ResolveMissionTargetCoordinates(RallyStore.GalaxyOf(fleet))==new Point(state.X,state.Y));
                    if (outside && !continuing) { Cancel(fleet,true);return; }
                }
                if (fleet.Mission is { Type: ShipMissionType.Move, IsManuallyAssigned: true })
                {
                    var destination = fleet.Mission.ResolveMissionTargetCoordinates(RallyStore.GalaxyOf(fleet));
                    if (destination.X != state.X || destination.Y != state.Y) { Cancel(fleet, true); return; }
                }
                foreach (var ship in fleet.Ships)
                {
                    if(!state.Active||Volatile.Read(ref runtime.Revision)!=workerRevision)return;
                    if (ship == null || ship.Fleet!=fleet || ship.Role == ShipRole.FuelTanker || ship.IsFighter || ship.IsBase || state.Excluded.Contains(ship.ShipId)) continue;
                    if (Busy(ship) || (HasOrder(ship) && ship.Mission.Type != ShipMissionType.Move)) continue;
                    if (ship.Summary == null || ship.Summary.TopSpeed <= 0) continue;
                    needsFuel |= ship.Summary.FuelCapacity > 0 && (float)ship.Fuel < .99f * ship.Summary.FuelCapacity;
                    if (Distance(ship, state.X, state.Y) <= ArrivalRadius * ArrivalRadius) continue;
                    gathered = false;
                    if (HasOrder(ship))
                    {
                        var target = ship.Mission.ResolveMissionTargetCoordinates(RallyStore.GalaxyOf(fleet));
                        if (target.X == state.X && target.Y == state.Y) continue;
                    }
                    // Keep the player's manual movement semantics, including native no-fuel speed.
                    ownOrder++;
                    try { ship.AssignMissionMove(state.X, state.Y, null!, true, false); }
                    finally { ownOrder--; }
                }
                if(!state.Active||Volatile.Read(ref runtime.Revision)!=workerRevision)return;
                if (gathered && !needsFuel)
                {lock(runtime.Gate){if(runtime.Revision==workerRevision){state.Active=false;runtime.Revision++;RallyStore.Save(fleet,runtime);}}return;}
                dispatch++;
                try { fleet.SignalFuelTankersForRefuelling(fleet.GetEmpire()!, fleet.GetFuelRequired(), float.MaxValue, true); }
                finally { dispatch--; }
            }
            catch (Exception error) { RallyStore.Fault(runtime, error); }
        }
        finally{worker=previousWorker;workerFleet=previousFleet;workerRevision=previousRevision;Volatile.Write(ref runtime.Reviewing,0);}
    }

    private static bool Dispatch(Fleet __instance, Empire empire, bool forceRefuel, ref bool __result)
    {
        if (dispatch == 0 || !forceRefuel || !Active(__instance, out var runtime)) return true;
        // This native call retains Ship Logistics' signalling scope (prefix/finalizer).
        // That scope distinguishes an explicit fleet request from an automatic 70% top-up.
        var galaxy = RallyStore.GalaxyOf(__instance);
        var state = runtime!.State;
        bool assigned = false;
        foreach (var tanker in __instance.Ships)
        {
            if(!state.Active||worker!=null&&Volatile.Read(ref runtime!.Revision)!=workerRevision)break;
            if (tanker == null || tanker.Fleet!=__instance || tanker.Role != ShipRole.FuelTanker || tanker.MissionAssignmentType != ShipMissionAssignmentType.FullyAutomate || Busy(tanker) || state.Excluded.Contains(tanker.ShipId) || tanker.Summary == null) continue;
            // Finish an existing transfer, cargo collection, repair or specific manual order.
            var current = tanker.Mission;
            if (current != null && current.Type != ShipMissionType.Undefined && (current.Type != ShipMissionType.Move || current.IsManuallyAssigned)) continue;
            var summary = tanker.Summary;
            if (summary.TopSpeed <= 0 || tanker.Cargo.GetAmount(tanker.GetEmpireId(), summary.ReactorFuelResourceId) <= .1f * summary.CargoCapacity) continue;
            var reserved = empire.FuelTankers.GetFuelTankerTargets(galaxy);
            // A future jump command in a preserved Move is not an active jump/preparation.
            // includeJumpingShips=false still rejects actual hyperspace/countdown states.
            var candidates = __instance.Ships.GetByLowFuelLevel(galaxy, empire, .99f, summary.ReactorFuelResourceId, tanker, false, reserved, true, true, tanker, false);
            foreach (var target in candidates.OrderBy(s => Distance(s, tanker.GalaxyX, tanker.GalaxyY)).ThenBy(s => s.ShipId))
            {
                if (target.Fleet != __instance || state.Excluded.Contains(target.ShipId) || Busy(target) || target.CheckForIncomingFuelTanker(galaxy, empire)) continue;
                if (Distance(target, tanker.GalaxyX, tanker.GalaxyY) >= summary.RemoteFuelTransferRange * summary.RemoteFuelTransferRange && !tanker.WithinFuelRangeBestPathApproximate(empire, target, 0, galaxy.StarDate, ShipMissionType.Undefined, out _)) continue;
                tanker.AssignMissionTransferFuel(target, null!, false);
                if (tanker.Mission?.Type == ShipMissionType.TransferFuel && tanker.Mission.PrimaryTarget.Resolve<Ship>(galaxy) == target) { assigned = true; break; }
            }
        }
        if (assigned) { __instance.WaitingForFuelTankers = true; __instance.WaitingForFuelTankersLevel = .99f; }
        __result = assigned; return false;
    }
}
