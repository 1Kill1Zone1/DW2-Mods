using System.Reflection;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly ConditionalWeakTable<Galaxy, StormField> StormFields = new();
    private static readonly ConditionalWeakTable<Ship, StormHold> StormHolds = new();

    private static void InstallStormSafety(Harmony harmony)
    {
        MethodInfo jump = AccessTools.Method(typeof(Ship), "CommandHyperjump")
            ?? throw new MissingMethodException("Ship.CommandHyperjump");
        var passengerOrders = new[] { nameof(Ship.AssignMissionMigration), nameof(Ship.AssignMissionTourism) }
            .Select(name => AccessTools.Method(typeof(Ship), name) ?? throw new MissingMethodException($"Ship.{name}"))
            .ToArray();
        harmony.Patch(jump, prefix: new HarmonyMethod(typeof(Entry), nameof(CheckJump)) { priority = Priority.Last });
        foreach (MethodInfo order in passengerOrders)
            harmony.Patch(order, prefix: new HarmonyMethod(typeof(Entry), nameof(CheckPassengerPorts)));
    }

    private static bool CheckPassengerPorts(Ship __instance, Orb source, StellarObject destination, bool manuallyAssigned)
    {
        try
        {
            if (manuallyAssigned || __instance.Role != ShipRole.PassengerShip ||
                __instance.GetEmpire() is not { IsPlayer: true } empire ||
                __instance.MissionAssignmentType == ShipMissionAssignmentType.Manual || source == null || destination == null)
                return true;

            Galaxy galaxy = empire.Galaxy;
            Storm[] storms = UnsafeEnvironment(__instance, galaxy);
            if (!Unsafe(storms, Coordinates(source), ExitMargin(__instance, source.GetLocation(galaxy))) &&
                !Unsafe(storms, Coordinates(destination), ExitMargin(__instance, destination.GetLocation(galaxy))))
                return true;

            RecordHold(__instance, null, Coordinates(destination), "passenger port inside a storm");
            return false;
        }
        catch (Exception ex)
        {
            ReportError("Passenger ports", ex);
            return __instance.GetEmpire()?.IsPlayer != true || manuallyAssigned;
        }
    }

    private static bool CheckJump(Ship __instance, Location location, Empire empire, ShipMission mission,
        ShipCommand command, StellarObject? missionTarget, Point missionGalaxyCoordinates, ref float __result)
    {
        return CanStartJump(__instance, location, empire, mission, command, missionTarget, missionGalaxyCoordinates, ref __result);
    }

    private static bool CanStartJump(Ship __instance, Location location, Empire empire, ShipMission mission,
        ShipCommand command, StellarObject? missionTarget, Point missionGalaxyCoordinates, ref float __result)
    {
        bool unsafeDetected = false;
        try
        {
            if (empire == null || __instance.IsBase || __instance.IsFighter || mission == null || __instance.IsHyperjumping())
                return true;

            if (!RepairMissingRoute(__instance, empire, mission, missionTarget, missionGalaxyCoordinates, location, ref __result))
                return false;

            if (empire.IsPlayer && !CheckTransitJump(__instance, empire, mission, command, missionTarget, location, ref __result))
                return false;

            Galaxy galaxy = empire.Galaxy;
            bool manual = mission.IsManuallyAssigned || __instance.MissionAssignmentType == ShipMissionAssignmentType.Manual;
            if (!manual && empire.IsPlayer && missionTarget == null && missionGalaxyCoordinates != Point.Zero &&
                CommonCoordinateFleetReason(__instance, mission, new(missionGalaxyCoordinates.X, missionGalaxyCoordinates.Y), localApproach: false) is { } coordinateReason)
            { __result = 0; __instance.TargetSpeed = 0; RecordHold(__instance, mission, new(missionGalaxyCoordinates.X, missionGalaxyCoordinates.Y), coordinateReason); return false; }
            if (!manual && empire.IsPlayer && missionTarget != null && CommonFleetEnvironmentReason(__instance, mission, missionTarget) is { } reason &&
                !EscapingEnvironment(__instance, missionTarget))
            { __result = 0; __instance.TargetSpeed = 0; RecordHold(__instance, mission, Coordinates(missionTarget), reason); return false; }
            if ((!manual || empire.IsPlayer) && Campaigns.TryGetValue(galaxy, out Campaign? shared) && shared.ActivePoints.Length > 0)
            {
                if (!CheckFleetDeparture(__instance, empire, galaxy, shared, mission, command, missionTarget, location,
                    ref __result, out bool coordinated)) return false;
                if (!coordinated)
                    ConsiderSharedWaypoint(__instance, empire, galaxy, shared, mission, command, missionTarget, location);
                if (!manual && !empire.IsPlayer)
                    return CheckForeignWaypoint(__instance, empire, galaxy, shared, mission, command, missionTarget, location, ref __result);
            }
            if (manual) WarnManualEnvironment(__instance, mission, missionTarget);
            if (manual || !empire.IsPlayer)
                return true;

            Storm[] storms = UnsafeEnvironment(__instance, galaxy);
            string? destinationReason = missionTarget == null ? null : EnvironmentReason(__instance, missionTarget);
            if (storms.Length == 0 && destinationReason == null)
                return true;

            Point next = __instance.GetJumpCoordinates(command, missionTarget!, location, out Location nextLocation,
                out _, out bool lastJump, out float accuracy);
            var exit = new Waypoint(next.X, next.Y);
            double margin = Math.Max(ExitMargin(__instance, nextLocation), 20_000 + Positive(accuracy));
            bool nextUnsafe = Unsafe(storms, exit, margin);
            Location? goalLocation = missionTarget?.GetLocation(galaxy) ?? galaxy.Locations.GetById(mission.DestinationLocationId);
            Waypoint goal = missionTarget != null ? Coordinates(missionTarget)
                : missionGalaxyCoordinates != Point.Zero ? new(missionGalaxyCoordinates.X, missionGalaxyCoordinates.Y)
                : goalLocation != null ? Coordinates(goalLocation) : exit;
            bool goalUnsafe = destinationReason != null || Unsafe(storms, goal, ExitMargin(__instance, goalLocation));
            if (!nextUnsafe)
            {
                if (!lastJump && Unsafe(storms, Coordinates(__instance), 0))
                    return true;
                if (!goalUnsafe && !FindUnsafeHop(__instance, galaxy, mission, storms, out exit, out margin))
                    return true;
            }

            // Keep the current command pending. The ordinary ship update can retry it on a later tick.
            unsafeDetected = true;
            __result = 0;
            __instance.TargetSpeed = 0;
            if (goalUnsafe)
            {
                RecordHold(__instance, mission, goal, destinationReason ?? "destination exit inside an unsafe environment");
                return false;
            }

            StormHold hold = StormHolds.GetOrCreateValue(__instance);
            long now = Environment.TickCount64;
            if (ReferenceEquals(hold.Mission, mission) && hold.Exit == exit && now < hold.NextAttempt)
                return false;

            hold.Mission = mission;
            hold.Exit = exit;
            hold.NextAttempt = now + 2_000;
            if (!TryBypass(__instance, empire, galaxy, mission, command, missionTarget, location,
                storms, nextUnsafe, nextLocation?.LocationId, exit, margin, goal, goalLocation))
            {
                RecordHold(__instance, mission, exit, "no safe reachable bypass within jump limits");
            }
            return false;
        }
        catch (Exception ex)
        {
            ReportError("Storm jump guard", ex);
            if (empire?.IsPlayer == true && mission != null && !mission.IsManuallyAssigned &&
                __instance.MissionAssignmentType != ShipMissionAssignmentType.Manual)
            { __result = 0; __instance.TargetSpeed = 0; RecordHold(__instance, mission, Coordinates(__instance), "environment assessment unavailable; automatic departure waits"); return false; }
            return !unsafeDetected;
        }
    }

    private static bool FindUnsafeHop(Ship ship, Galaxy galaxy, ShipMission mission, Storm[] storms,
        out Waypoint point, out double margin)
    {
        if (mission.LocationIdPath is { } path)
        {
            for (int i = mission.LocationPathProgress; i < path.Count; i++)
            {
                int id = path[i];
                if (id == ship.LocationId || galaxy.Locations.GetById(id) is not { } hop)
                    continue;
                point = Coordinates(hop);
                margin = ExitMargin(ship, hop);
                if (Unsafe(storms, point, margin))
                    return true;
            }
        }
        point = default;
        margin = 0;
        return false;
    }

    private static bool TryBypass(Ship ship, Empire empire, Galaxy galaxy, ShipMission mission, ShipCommand command,
        StellarObject? missionTarget, Location currentLocation, Storm[] storms, bool firstExitUnsafe, int? firstExitId,
        Waypoint unsafeExit, double margin, Waypoint goal, Location? goalLocation)
    {
        if (goalLocation == null || ship.Summary is not { } summary) return false;
        double range = summary.HyperDriveJumpRange;
        double minimum = Math.Max(1, ship.CalculateHyperjumpThreshold());
        TravelTiming timing = TravelTiming.For(ship);
        double fuel = timing.PoweredRange;
        Waypoint from = Coordinates(ship);
        if (!(range >= minimum)) return false;
        Campaign campaign = Campaigns.GetValue(galaxy, key => new Campaign(key, empire));
        var blocked = empire.IsPlayer ? campaign.Transit?.For(ship, mission) : null;
        bool civilian = blocked is { Count: > 0 };
        bool Safe(Waypoint point) => Campaign.InBounds(galaxy, point) && !Unsafe(storms, point, margin) &&
            (!civilian || blocked == null || !IsBlockedCoordinate(galaxy, point, blocked));
        if (!Safe(goal)) return false;
        var anchors = new List<RouteAnchor> { new(currentLocation.LocationId, from) };
        if (mission.LocationIdPath is { } existing)
            foreach (int id in existing.Skip(mission.LocationPathProgress))
            {
                if (id == ship.LocationId || id == goalLocation.LocationId || (firstExitUnsafe && id == firstExitId)) continue;
                if (galaxy.Locations.GetById(id) is not { } hop ||
                    Unsafe(storms, Coordinates(hop), ExitMargin(ship, hop)) || !Safe(Coordinates(hop))) continue;
                if (!anchors.Any(a => a.LocationId == id)) anchors.Add(new(id, Coordinates(hop)));
            }
        anchors.Add(new(goalLocation.LocationId, goal));
        // Sample only relevant cloud rims; player nodes always remain available, including existing temporary hops.
        // shortcut: at most 192 automatic rim nodes; very narrow corridors can still need a player waypoint.
        double corridor = RouteChoice.Distance(from, goal) + 2 * range;
        var automatic = storms.Where(storm => RouteChoice.Distance(from, storm.Center) + RouteChoice.Distance(storm.Center, goal)
                <= corridor + 2 * (storm.Radius + margin))
            .SelectMany(storm => storm.Candidates(from, goal, range, margin)).Distinct().Where(Safe)
            .OrderBy(point => RouteChoice.Distance(from, point) + RouteChoice.Distance(point, goal)).Take(192);
        Waypoint[] points = campaign.ActivePoints.Concat(automatic).Distinct().ToArray();
        double jumpCost = Positive(summary.HyperDriveJumpInitiationTime) * Positive(summary.HyperDriveSpeed);
        var plan = WaypointRouting.Find(anchors, points, false, minimum, range, fuel, jumpCost,
            Safe, (a, b) => LineCost(galaxy, a, b), allowOrdinaryResult: true, estimate: timing.Seconds);
        if (plan == null) return false;
        var path = new List<int>();
        foreach (var node in plan.Nodes.Skip(1))
        {
            int id = node.LocationId;
            if (id < 0)
            {
                if (campaign.ResolveLocation(galaxy, node.Point, automaticallyChosen: !campaign.ContainsActive(node.Point)) is not { } location)
                    return false;
                id = location.LocationId;
            }
            path.Add(id);
        }
        if (!VerifyReplacement(ship, galaxy, mission, command, missionTarget, currentLocation, storms, path,
            (next, point) => next != null && Safe(point) &&
                (!civilian || blocked == null || !IsBlockedStop(next, goalLocation.SystemId, blocked, true)))) return false;
        if (LoggedRoutes.AddOrUpdate(ship.Role, 1, (_, count) => count + 1) <= 40)
            Log($"Storm bypass {ship.Role} #{ship.ShipId}: hops=[{string.Join(",", path)}], " +
                $"distance={plan.Distance:0}, cost={plan.Cost:0}, fuelRange={fuel:0}.");
        return true;
    }
    private static bool VerifyReplacement(Ship ship, Galaxy galaxy, ShipMission mission, ShipCommand command,
        StellarObject? target, Location currentLocation, Storm[] storms, List<int> path, Func<Location?, Waypoint, bool>? validExit = null)
    {
        List<int>? previous = mission.LocationIdPath;
        byte progress = mission.LocationPathProgress;
        bool safe = false;
        try
        {
            mission.LocationIdPath = path;
            mission.LocationPathProgress = 0;
            Point next = ship.GetJumpCoordinates(command, target!, currentLocation, out Location location,
                out _, out _, out float accuracy);
            safe = (validExit == null || validExit(location, new(next.X, next.Y))) &&
                !Unsafe(storms, new(next.X, next.Y), Math.Max(ExitMargin(ship, location), 20_000 + Positive(accuracy))) &&
                !FindUnsafeHop(ship, galaxy, mission, storms, out _, out _);
            return safe;
        }
        finally
        {
            if (!safe)
            {
                mission.LocationIdPath = previous!;
                mission.LocationPathProgress = progress;
            }
        }
    }

    private static Storm[] GetStorms(Galaxy galaxy) => StormFields.GetOrCreateValue(galaxy).Get(galaxy);

    // Assessments require current hazards even immediately after a cloud/effect change.
    // Ordinary routing keeps its existing short-lived snapshot below.
    private static Storm[] ReadStorms(Galaxy galaxy)
    {
        var hazards = new List<Storm>();
        if (galaxy.NebulaClouds is { } clouds)
            foreach (NebulaCloud cloud in clouds)
            {
                if (cloud?.EffectGroups is not { Count: > 0 } effects || cloud.Radius <= 0) continue;
                effects.GetMaximumEffectAmountAll(galaxy, out float direct, out float energy, out float ion,
                    out float jumpBlock, out _, out _, out float shield, out _, out float standard, out _);
                if (direct != 0 || energy != 0 || ion != 0 || jumpBlock != 0 || shield != 0 || standard != 0)
                    hazards.Add(new Storm(new Waypoint(cloud.GalaxyX, cloud.GalaxyY), cloud.Radius));
            }
        return hazards.ToArray();
    }
    private static bool Unsafe(Storm[] storms, Waypoint point, double margin)
    {
        foreach (Storm storm in storms)
            if (storm.Contains(point, margin))
                return true;
        return false;
    }

    private static Waypoint Coordinates(StellarObject item) => new(item.GalaxyX, item.GalaxyY);
    private static Waypoint Coordinates(Location item) => new(item.GalaxyX, item.GalaxyY);
    private static float Positive(float value) => float.IsFinite(value) && value > 0 ? value : 0;

    private static double ExitMargin(Ship ship, Location? location) => 20_000 + Math.Max(
        Positive(ship.Summary?.HyperDriveJumpAccuracy ?? 0),
        location == null ? 0 : Positive(ship.CalculateExpectedJumpExitDistance(location)));

    private static void RecordHold(Ship ship, ShipMission? mission, Waypoint point, string reason)
    {
        StormHold hold = StormHolds.GetOrCreateValue(ship);
        long now = Environment.TickCount64;
        if (now < hold.NextLog && ReferenceEquals(hold.LastLoggedMission, mission) && hold.LastLoggedReason == reason)
            return;

        hold.LastLoggedMission = mission;
        hold.LastLoggedReason = reason;
        hold.NextLog = now + 30_000;
        if (ReferenceEquals(DistantWorlds.UI.UserInterfaceController.SelectionPanel?.Item, ship) ||
            ship.Fleet != null && ReferenceEquals(DistantWorlds.UI.UserInterfaceController.SelectionPanel?.Item, ship.Fleet))
            LocalModUi.MapUiGate.CurrentRenderer?.SetBriefMessage("Automatic travel waiting: " + reason, 8);
        Log($"Storm hold {ship.Role} #{ship.ShipId} at ({point.X},{point.Y}): {reason}.");
    }

    private sealed class StormHold
    {
        public ShipMission? Mission;
        public Waypoint Exit;
        public long NextAttempt;
        public ShipMission? LastLoggedMission;
        public string? LastLoggedReason;
        public long NextLog;
    }

    private sealed class StormField
    {
        private readonly object gate = new();
        private volatile Snapshot? cached;

        public Storm[] Get(Galaxy galaxy)
        {
            long now = Environment.TickCount64;
            if (cached is { } existing && now < existing.Expires)
                return existing.Storms;

            lock (gate)
            {
                if (cached is { } current && now < current.Expires)
                    return current.Storms;

                cached = new Snapshot(now + 2_000, ReadStorms(galaxy));
                return cached.Storms;
            }
        }

        private sealed record Snapshot(long Expires, Storm[] Storms);
    }
}

