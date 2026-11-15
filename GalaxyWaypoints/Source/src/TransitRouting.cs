using System.Reflection;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly ConditionalWeakTable<Ship, TransitWait> TransitWaits = new();
    [ThreadStatic] private static TransitSearch? transitSearch;

    private static void InstallTransitRouting(Harmony harmony)
    {
        MethodInfo findPath = typeof(PathFindingSystem).GetMethods().Single(method => method.Name == nameof(PathFindingSystem.FindPath) &&
            method.GetParameters().Any(parameter => parameter.Name == "allowReverse"));
        MethodInfo allowNebula = AccessTools.Method(typeof(PathFindingSystem), nameof(PathFindingSystem.FindBestPathAllowThroughNebula))
            ?? throw new MissingMethodException(nameof(PathFindingSystem.FindBestPathAllowThroughNebula));
        var prefix = new HarmonyMethod(typeof(Entry), nameof(ExcludeTransitSystems)) { priority = Priority.First, before = ["pathfindingfix.dw2mod"] };
        harmony.Patch(findPath, prefix: prefix);
        harmony.Patch(allowNebula, prefix: prefix);
    }

    private static void ExcludeTransitSystems(Galaxy galaxy, ref bool[] excludedSystems, StarSystem source, StarSystem destination)
    {
        if (transitSearch is { } search && ReferenceEquals(search.Galaxy, galaxy) &&
            source != null && destination != null &&
            (excludedSystems ?? galaxy.PathFindingExcludedSystemsNone) is { } baseline)
            excludedSystems = TransitRules.Exclude(baseline, search.Blocked, source.SystemId, destination.SystemId);
    }

    private static bool CheckTransitJump(Ship ship, Empire empire, ShipMission mission, ShipCommand command,
        StellarObject? target, Location current, ref float result)
    {
        Galaxy galaxy = empire.Galaxy;
        if (!empire.IsPlayer || !Campaigns.TryGetValue(galaxy, out Campaign? campaign) ||
            campaign.Transit?.For(ship, mission) is not { Count: > 0 } blocked)
            return true;

        Location? destination = target?.GetLocation(galaxy) ?? galaxy.Locations.GetById(mission.DestinationLocationId);
        int sourceSystem = current is { IsTemporary: false } ? current.SystemId : -1;
        Point exit = ship.GetJumpCoordinates(command, target!, current, out Location next, out _, out bool lastJump, out _);
        int destinationSystem = destination?.SystemId ?? (lastJump ? next?.SystemId : null) ?? -1;
        // The selected role's rules apply to both intermediate stops and destinations.
        bool civilian = true;
        bool destinationBlocked = civilian && (TransitRules.IsBlocked(destinationSystem, destinationSystem, blocked, true) ||
            (lastJump && IsBlockedCoordinate(galaxy, new(exit.X, exit.Y), blocked)));
        bool leavingSource = sourceSystem >= 0 && destinationSystem != sourceSystem && next?.SystemId == sourceSystem;
        bool exitBlocked = !leavingSource && (IsBlockedStop(next, destinationSystem, blocked, true) ||
            IsBlockedCoordinate(galaxy, new(exit.X, exit.Y), blocked));
        if (!destinationBlocked && !exitBlocked &&
            !HasBlockedHop(ship, galaxy, mission, sourceSystem, destinationSystem, blocked, civilian))
            return true;

        result = 0;
        ship.TargetSpeed = 0;
        try
        {
            if (destinationBlocked && ReleaseBlockedExploration(ship, mission, destination))
                return false;
            TransitWait wait = TransitWaits.GetOrCreateValue(ship);
            long now = Environment.TickCount64;
            bool sameRoute = ReferenceEquals(wait.Mission, mission) && ReferenceEquals(wait.Blocked, blocked) &&
                wait.Destination == (destination?.LocationId ?? -1);
            if (sameRoute && now < wait.RetryAt)
                return false;

            wait.Mission = mission;
            wait.Blocked = blocked;
            wait.Destination = destination?.LocationId ?? -1;
            wait.RetryAt = now + 2_000;
            if (!sameRoute)
                wait.Reported = false;

            if (!destinationBlocked && destination != null &&
                TryTransitBypass(ship, empire, galaxy, mission, command, target, current, destination, blocked, civilian, campaign))
            {
                Log($"Transit bypass {ship.Role} #{ship.ShipId} to {destination.Name}.");
                wait.Reported = false;
            }
            else if (!wait.Reported)
            {
                string reason = destinationBlocked ? "destination blocked for role" : "no reachable permitted path";
                Log($"Transit hold {ship.Role} #{ship.ShipId}: {reason} to {destination?.Name ?? "target"}.");
                wait.Reported = true;
            }
        }
        catch (Exception ex)
        {
            ReportError("Transit reroute", ex);
        }
        return false;
    }

    private static bool IsBlockedCoordinate(Galaxy galaxy, Waypoint point, IReadOnlySet<int> blocked)
    {
        foreach (int id in blocked)
            if (galaxy.Systems.TryGetValue(id, out StarSystem? system) &&
                RouteChoice.Distance(point, new(system.GalaxyX, system.GalaxyY)) <= Constants.SystemRadius)
                return true;
        return false;
    }

    private static bool IsBlockedStop(Location? hop, int destinationSystem, IReadOnlySet<int> blocked, bool civilian) =>
        hop is { IsTemporary: false } && TransitRules.IsBlocked(hop.SystemId, destinationSystem, blocked, civilian);

    private static bool HasBlockedHop(Ship ship, Galaxy galaxy, ShipMission mission, int sourceSystem, int destinationSystem,
        IReadOnlySet<int> blocked, bool civilian)
    {
        if (mission.LocationIdPath is { } path)
        {
            bool departing = sourceSystem >= 0;
            for (int i = mission.LocationPathProgress; i < path.Count; i++)
            {
                if (path[i] == ship.LocationId || galaxy.Locations.GetById(path[i]) is not { } hop)
                    continue;
                if (departing && !hop.IsTemporary && hop.SystemId == sourceSystem)
                    continue;
                departing = false;
                if (IsBlockedStop(hop, destinationSystem, blocked, civilian) ||
                    (civilian && IsBlockedCoordinate(galaxy, Coordinates(hop), blocked)))
                    return true;
            }
        }
        return false;
    }

    private static bool TryTransitBypass(Ship ship, Empire empire, Galaxy galaxy, ShipMission mission,
        ShipCommand command, StellarObject? target, Location current, Location destination, IReadOnlySet<int> blocked,
        bool civilian, Campaign campaign)
    {
        if (current == null || ship.Summary is not { } summary)
            return false;

        double range = summary.HyperDriveJumpRange;
        TravelTiming timing = TravelTiming.For(ship);
        double fuel = timing.PoweredRange;
        double minimum = Math.Max(1, ship.CalculateHyperjumpThreshold());
        Waypoint from = Coordinates(ship);
        Waypoint to = target == null ? Coordinates(destination) : Coordinates(target);
        if (!(range >= minimum))
            return false;

        Storm[] storms = UnsafeEnvironment(ship, galaxy);
        if (civilian)
        {
            var safePoints = campaign.ActivePoints.Where(point => !IsBlockedCoordinate(galaxy, point, blocked) &&
                !Unsafe(storms, point, ExitMargin(ship, null))).ToArray();
            var detour = TransitRules.FindWaypointDetour(from, to, safePoints, minimum, range, fuel,
                (a, b) => LineCost(galaxy, a, b), estimate: timing.Seconds);
            if (detour != null)
            {
                var hops = new List<int>();
                foreach (Waypoint point in detour)
                {
                    if (campaign.ResolveLocation(galaxy, point) is not { } location)
                        return false;
                    hops.Add(location.LocationId);
                }
                hops.Add(destination.LocationId);
                if (TryInstall(hops)) return true;
            }
        }

        List<int>? path;
        var previous = transitSearch;
        transitSearch = new TransitSearch(galaxy, blocked);
        try
        {
            path = empire.FindBestJumpPathToDestination(ship, destination.LocationId, out _, out _);
        }
        finally
        {
            transitSearch = previous;
        }

        if (path is { Count: > 0 })
        {
            // The engine may return a shared list or a fallback path that ignored exclusions.
            var hops = path.SkipWhile(id => id == current.LocationId && id != destination.LocationId).ToList();
            if (hops.Count == 0 || hops[^1] != destination.LocationId)
                hops.Add(destination.LocationId);
            if (TryInstall(hops))
                return true;
        }

        if (RouteChoice.CanJump(RouteChoice.Distance(from, to), minimum, range) && TryInstall([destination.LocationId]))
            return true;

        // A shared waypoint can bridge the gap even when the stock system search has no permitted route.
        List<int>? viaWaypoint = null;
        float distance = 0;
        float time = 0;
        ChooseWaypoint(empire, ship, summary.HyperDriveJumpRange, 0, true, current, destination,
            ref distance, ref time, new Point(from.X, from.Y), Point.Zero, ref viaWaypoint);
        return viaWaypoint is { Count: > 0 } && TryInstall(viaWaypoint);

        bool TryInstall(List<int> hops)
        {
            if (!TransitRules.RouteFits(hops, from, range, fuel, ResolveHop, (a, b) => LineCost(galaxy, a, b)))
                return false;
            return VerifyReplacement(ship, galaxy, mission, command, target, current, storms, hops,
                (next, point) => next != null && !IsBlockedStop(next, destination.SystemId, blocked, civilian)
                    && (!civilian || !IsBlockedCoordinate(galaxy, point, blocked)));
        }

        Waypoint? ResolveHop(int id)
        {
            Location? hop = galaxy.Locations.GetById(id);
            if (hop == null || IsBlockedStop(hop, destination.SystemId, blocked, civilian))
                return null;
            var point = id == destination.LocationId ? to : Coordinates(hop);
            return IsBlockedCoordinate(galaxy, point, blocked) ? null : point;
        }
    }

    private sealed record TransitSearch(Galaxy Galaxy, IReadOnlySet<int> Blocked);

    private sealed class TransitWait
    {
        public ShipMission? Mission;
        public IReadOnlySet<int>? Blocked;
        public int Destination;
        public long RetryAt;
        public bool Reported;
    }
}
