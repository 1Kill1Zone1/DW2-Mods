using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly ConditionalWeakTable<Ship, DepartureCheck> DepartureChecks = new();

    private static void ConsiderSharedWaypoint(Ship ship, Empire empire, Galaxy galaxy, Campaign campaign,
        ShipMission mission, ShipCommand command, StellarObject? target, Location location)
    {
        if (mission.LocationPathProgress != 0 || target?.GetLocation(galaxy) is not { IsTemporary: false } destination ||
            location == null || location.SystemId == destination.SystemId || ship.Summary is not { } summary)
            return;

        var check = DepartureChecks.GetOrCreateValue(ship);
        Waypoint[] points = campaign.ActivePoints;
        if (ReferenceEquals(check.Mission, mission) && check.Destination == destination.LocationId &&
            ReferenceEquals(check.Points, points) && Environment.TickCount64 < check.RetryAt)
            return;

        check.Mission = mission;
        check.Destination = destination.LocationId;
        check.Points = points;
        check.RetryAt = Environment.TickCount64 + 2_000;

        List<int>? original = mission.LocationIdPath;
        List<int>? proposed = original;
        float distance = 0;
        // The shared planner recomputes the path cost from the actual location IDs.
        float time = original is { Count: > 0 } ? 1 : 0;
        ChooseWaypoint(empire, ship, summary.HyperDriveJumpRange, 0, true, location, destination,
            ref distance, ref time, Point.Zero, Point.Zero, ref proposed);
        if (proposed is { Count: > 0 } && !ReferenceEquals(original, proposed))
        {
            Location? firstHop = galaxy.Locations.GetById(proposed[0]);
            bool applied = VerifyReplacement(ship, galaxy, mission, command, target, location, UnsafeEnvironment(ship, galaxy), proposed,
                firstHop is { IsTemporary: true } ? (next, _) => next?.LocationId == firstHop.LocationId : null);
            if (empire.IsPlayer)
                Log($"Departure {ship.Role} #{ship.ShipId} to {destination.Name}: {(applied ? "waypoint route applied" : "kept existing route after verification")}.");
        }
    }

    private static bool CheckForeignWaypoint(Ship ship, Empire empire, Galaxy galaxy, Campaign campaign, ShipMission mission,
        ShipCommand command, StellarObject? target, Location current, ref float result)
    {
        if (mission.LocationIdPath is not { Count: > 0 } path)
            return true;
        int index = mission.LocationPathProgress;
        while (index < path.Count && path[index] == ship.LocationId)
            index++;
        if (index >= path.Count || galaxy.Locations.GetById(path[index]) is not { IsTemporary: true } waypoint ||
            !campaign.ContainsActive(Coordinates(waypoint)))
            return true;

        Storm[] storms = UnsafeEnvironment(ship, galaxy);
        Point actual = ship.GetJumpCoordinates(command, target!, current, out Location actualLocation,
            out _, out _, out float accuracy);
        double margin = Math.Max(ExitMargin(ship, waypoint), 20_000 + Positive(accuracy));
        if (!Unsafe(storms, Coordinates(waypoint), margin) &&
            (actualLocation?.LocationId != waypoint.LocationId || !Unsafe(storms, new(actual.X, actual.Y), margin)))
            return true;

        result = 0;
        ship.TargetSpeed = 0;
        StormHold hold = StormHolds.GetOrCreateValue(ship);
        Waypoint point = Coordinates(waypoint);
        long now = Environment.TickCount64;
        if (ReferenceEquals(hold.Mission, mission) && hold.Exit == point && now < hold.NextAttempt)
            return false;
        hold.Mission = mission;
        hold.Exit = point;
        hold.NextAttempt = now + 2_000;

        Location? destination = target?.GetLocation(galaxy) ?? galaxy.Locations.GetById(mission.DestinationLocationId);
        if (destination == null || destination.LocationId == waypoint.LocationId)
            return false;

        bool previous = skipWaypointRouting;
        skipWaypointRouting = true;
        try
        {
            List<int>? ordinary = empire.FindBestJumpPathToDestination(ship, destination.LocationId, out _, out _);
            if (ordinary is { Count: > 0 } && !ordinary.Contains(waypoint.LocationId))
                VerifyReplacement(ship, galaxy, mission, command, target, current, storms, ordinary);
        }
        catch (Exception ex)
        {
            ReportError("Foreign waypoint reroute", ex);
        }
        finally
        {
            skipWaypointRouting = previous;
        }
        return false;
    }

    private sealed class DepartureCheck
    {
        public ShipMission? Mission;
        public int Destination;
        public Waypoint[]? Points;
        public long RetryAt;
    }
}
