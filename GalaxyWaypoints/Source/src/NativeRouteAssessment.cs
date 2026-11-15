using DistantWorlds.Types;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    [ThreadStatic] private static int assessmentDepth;

    private static RouteCapability ShipCapability(Ship ship)
    {
        var timing = TravelTiming.For(ship);
        return new(Math.Max(1, ship.CalculateHyperjumpThreshold()), ship.Summary.HyperDriveJumpRange,
            timing.PoweredRange, timing.Speed, timing.Preparation);
    }

    // Called only on the existing native search/UI owner thread. No worker invokes native mutable caches.
    private static RouteAssessment? AssessRoute(Campaign campaign, StellarObject start, Location source,
        Location destination, RouteCapability capability, TransitGroup group, double exitMargin,
        Func<Waypoint, Waypoint, double>? terrainCost = null, IReadOnlyList<Waypoint>? assessmentPoints = null)
    {
        if (!capability.Valid || source.SystemId == destination.SystemId) return null;
        var empire = campaign.Observer;
        var galaxy = empire.Galaxy;
        var blocked = campaign.Transit!.For(group);
        if (blocked.Contains(destination.SystemId)) return null;
        if (empire.PathFindingDangerousSystems is not { } dangerous || destination.SystemId < 0 ||
            destination.SystemId >= dangerous.Length || dangerous[destination.SystemId]) return null;
        var previousTransit = transitSearch;
        bool previousSkip = skipWaypointRouting;
        List<int>? ordinary;
        float ordinaryTime;
        try
        {
            assessmentDepth++;
            skipWaypointRouting = true;
            transitSearch = new(galaxy, blocked);
            ordinary = empire.FindBestJumpPathToDestination(start, (float)capability.JumpRange, 0,
                true, source, destination, out _, out ordinaryTime, Point.Zero, Point.Zero);
        }
        finally { transitSearch = previousTransit; skipWaypointRouting = previousSkip; assessmentDepth--; }
        var anchors = new List<RouteAnchor> { new(source.LocationId, Coordinates(start)) };
        bool hasOrdinary = ordinary is { Count: > 0 } && ordinaryTime is > 0 and < 1e10f;
        if (hasOrdinary)
            foreach (int id in ordinary!)
            {
                if (id == destination.LocationId) break;
                if (id == anchors[^1].LocationId) continue;
                if (galaxy.Locations.GetById(id) is { } location) anchors.Add(new(id, Coordinates(location)));
            }
        anchors.Add(new(destination.LocationId, Coordinates(destination)));
        var storms = start is Ship vessel ? UnsafeEnvironment(vessel, galaxy) : ReadStorms(galaxy);
        bool Safe(Waypoint point) => !Unsafe(storms, point, exitMargin) &&
            !IsBlockedCoordinate(galaxy, point, blocked) && !DangerousStop(point);
        bool DangerousStop(Waypoint point)
        {
            var nearest = galaxy.FindNearestSystem(point.X, point.Y, out long distanceSquared);
            return distanceSquared < SystemMembershipDistanceSquared && nearest != null &&
                ((uint)nearest.SystemId >= (uint)dangerous.Length || dangerous[nearest.SystemId]);
        }
        // assessmentPoints is diagnostic input only; ordinary navigation always supplies the active snapshot.
        return RouteAssessment.Evaluate(anchors, assessmentPoints ?? campaign.ActivePoints, hasOrdinary, capability,
            Safe, terrainCost ?? ((a, b) => LineCost(galaxy, a, b)));
    }

    private static RouteAssessment? AssessShipRoute(Campaign campaign, Ship ship, StarSystem system, bool unlimitedPoweredRange = false)
    {
        if (ship.Summary == null || ship.GetLocation(campaign.Observer.Galaxy) is not { } source ||
            system.Star?.GetLocation(campaign.Observer.Galaxy) is not { } destination) return null;
        var capability = ShipCapability(ship);
        if (unlimitedPoweredRange) capability = capability with { PoweredRange = double.MaxValue };
        return AssessRoute(campaign, ship, source, destination, capability,
            TransitGroup.Explorers, ExitMargin(ship, null));
    }

    private static bool FullySurveyed(Campaign campaign, int systemId) =>
        campaign.Observer.VisibilityMap?.GetSystemVisibilityStatus(systemId) != VisibilityStatus.Unexplored &&
        campaign.Observer.ExplorationMap.GetLowestLevelForSystem(systemId) > 1 &&
        !campaign.Observer.ExplorationMap.CheckSystemHasUnknownItems(systemId);
}
