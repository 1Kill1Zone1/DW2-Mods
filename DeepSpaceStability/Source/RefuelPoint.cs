using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace DeepSpaceStability;

internal static class RefuelPoint
{
    internal static bool Enabled;
    // Only used on a queued coordinate Move. The native save writer already stores ExtraId.
    internal const int QueueMarker = -13087021;
    private sealed record Destination(Fleet Fleet, Point Point);
    private static readonly ConditionalWeakTable<GameTask, Destination> Commands = new();

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ShipMission), nameof(ShipMission.ResolveValidMissions),
            [typeof(Galaxy),typeof(Empire),typeof(Fleet),typeof(StellarObject),typeof(Fleet),typeof(int),typeof(int),typeof(bool)]),
            postfix: new HarmonyMethod(typeof(RefuelPoint), nameof(AddCommand)));
        harmony.Patch(AccessTools.Method(typeof(Fleet), nameof(Fleet.CheckAssignNextQueuedMission)),
            prefix: new HarmonyMethod(typeof(RefuelPoint), nameof(Advance)));
    }

    private static void AddCommand(Galaxy galaxy, Empire empire, Fleet fleet, StellarObject target, Fleet targetFleet,
        int galaxyX, int galaxyY, bool missionsAreManuallyAssigned, ref GameTaskList __result)
    {
        if (!Enabled || !missionsAreManuallyAssigned || target != null || targetFleet != null ||
            !Rally.Eligible(fleet) || empire != fleet.GetEmpire() || galaxyX < 0 || galaxyY < 0 ||
            __result == null || !__result.Any(t => t?.Enabled == true && t.Type == GameTaskType.AssignFleetMission && t.ShipMissionType == ShipMissionType.Move)) return;
        var command = GameTask.GenerateTaskFleetMission(galaxy, empire, ShipMissionType.Move,
            fleet, null!, null!, galaxyX, galaxyY, null!, Vector3.Zero, null!);
        command.Text = "Gather and refuel here";
        if (!HasTanker(fleet))
        {
            command.Enabled = false;
            command.DisabledReason = "Add a fuel tanker to this fleet.";
            command.Text += " (requires a fleet tanker)";
        }
        else if (RallyStore.For(fleet).Faulted) return;
        else if (RallyStore.For(fleet).State is { PointOrder:true, Active:true })
            command.Text += " (rendezvous in progress)";
        Commands.Add(command, new(fleet, new(galaxyX,galaxyY)));
        var menu = new GameTaskList();
        menu.AddRange(__result);
        menu.Add(command);
        __result = menu;
    }

    private static bool HasTanker(Fleet fleet) => fleet.Ships.Any(s => s.Role == ShipRole.FuelTanker && !s.IsDestroyed);
    internal static bool Consume(Empire empire, GameTask task, bool queued)
    {
        if (!Commands.TryGetValue(task, out var destination)) return false;
        var fleet = destination.Fleet;
        if (Enabled && task.Enabled && fleet.GetEmpire() == empire && Rally.Eligible(fleet) && HasTanker(fleet))
            Issue(fleet, destination.Point, queued);
        return true;
    }

    internal static void Issue(Fleet fleet, Point point, bool queued)
    {
        var runtime = RallyStore.For(fleet);
        if (runtime.Faulted) return;
        if (queued && (fleet.Mission is { Type: not ShipMissionType.Undefined } || runtime.State.Active || fleet.NextMissions.Count > 0))
        {
            var mission = new ShipMission(RallyStore.GalaxyOf(fleet), ShipMissionType.Move, null!, null!, null!,
                point.X, point.Y, -1, -1, new CargoList(), null!, new ContractStubList(), [], true) { ExtraId = QueueMarker };
            fleet.QueueMission(mission);
            return;
        }
        if (!queued) fleet.NextMissions.Clear();
        Start(fleet, point);
    }

    private static void Start(Fleet fleet, Point point)
    {
        fleet.AssignMissionMove(point.X, point.Y, true);
        var runtime = RallyStore.For(fleet);
        lock (runtime.Gate)
        {
            if (runtime.Faulted || !runtime.State.HasGoal || runtime.State.X != point.X || runtime.State.Y != point.Y) return;
            runtime.State.Active = runtime.State.PointOrder = true;
            runtime.Revision++;
            RallyStore.Save(fleet, runtime);
        }
    }

    private static bool Advance(Fleet __instance, ref bool __result)
    {
        if (!Enabled || !Rally.Eligible(__instance)) return true;
        var runtime = RallyStore.For(__instance);
        if (runtime.Faulted) return true;
        if (runtime.State is { PointOrder:true, Active:true }) return false;
        if (__instance.NextMissions is not { Count: > 0 } queue ||
            queue[0] is not { Type:ShipMissionType.Move, ExtraId:QueueMarker } mission) return true;
        queue.RemoveAt(0);
        Start(__instance, new(mission.PrimaryGalaxyX, mission.PrimaryGalaxyY));
        __result = true;
        return false;
    }
}
