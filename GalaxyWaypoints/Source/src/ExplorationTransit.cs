using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using DistantWorlds.Core;
using DistantWorlds.Types;
using HarmonyLib;

namespace GalaxyWaypoints;

public static partial class Entry
{
    [ThreadStatic] private static ExplorationSearch? explorationSearch;
    private sealed record ExplorationSearch(Ship Ship, Galaxy Galaxy, IReadOnlySet<int> Blocked, Campaign Campaign)
    {
        internal readonly Stopwatch Budget = Stopwatch.StartNew();
        internal int Evaluations;
    }
    private static readonly ConditionalWeakTable<Ship, ExplorationRouteCache> ExplorationRoutes = new();
    private sealed class ExplorationRouteCache
    {
        internal readonly object Gate = new();
        internal Waypoint Origin;
        internal Waypoint[]? Points;
        internal object? Transit;
        internal RouteCapability Capability;
        internal long Expires;
        internal int Cursor;
        internal StarSystem[] Systems = [];
        internal readonly Dictionary<int, float> Costs = new();
    }

    private static void InstallExplorationTransit(Harmony harmony)
    {
        var select = AccessTools.Method(typeof(Ship), "CheckAssignExploration",
            new[] { typeof(Empire), typeof(StarSystemList), typeof(bool) })
            ?? throw new MissingMethodException("Ship.CheckAssignExploration");
        harmony.Patch(select, prefix: new HarmonyMethod(typeof(Entry), nameof(BeginExplorationSearch)),
            finalizer: new HarmonyMethod(typeof(Entry), nameof(EndExplorationSearch)));

        // Some native fallbacks pass null instead of the exclusion list. Filter at the query itself.
        var nearest = typeof(ExplorationMap).GetMethods().Single(m => m.Name == "FindNearestByPath" &&
            m.GetParameters().Any(p => p.Name == "excludeSystemsSupplied"));
        harmony.Patch(nearest, prefix: new HarmonyMethod(typeof(Entry), nameof(ExcludeExplorationSystems)));

        harmony.Patch(AccessTools.Method(typeof(SystemPathTimeSet), nameof(SystemPathTimeSet.GetPathTimesForSystem)),
            postfix: new HarmonyMethod(typeof(Entry), nameof(AugmentExplorationPaths)));
        harmony.Patch(AccessTools.Method(typeof(SystemPathTimeSet), nameof(SystemPathTimeSet.GetPathTimeDelayed)),
            postfix: new HarmonyMethod(typeof(Entry), nameof(VerifyExplorationPathCost)));

        var fuel = AccessTools.Method(typeof(Ship), "WithinFuelRangeBestPathApproximateExtended",
            new[] { typeof(Empire), typeof(StellarObject), typeof(float), typeof(long), typeof(ShipMissionType) })
            ?? throw new MissingMethodException("Ship.WithinFuelRangeBestPathApproximateExtended");
        harmony.Patch(fuel, prefix: new HarmonyMethod(typeof(Entry), nameof(FilterExplorationCandidate)));

        // Encounter/ruin investigation uses Move rather than an Explore mission.
        foreach (var method in typeof(Ship).GetMethods().Where(m =>
            new[] { "AssignMissionExploreSystem", "AssignMissionExploreLocation", "AssignMissionExplore",
                "AssignMissionExploreSurvey", "AssignMissionScout", "AssignMissionMove" }.Contains(m.Name) &&
            m.ReturnType == typeof(bool) && m.GetParameters().Length >= 3 &&
            typeof(StellarObject).IsAssignableFrom(m.GetParameters()[0].ParameterType)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Entry), nameof(AllowExplorationAssignment)));
    }

    private static IReadOnlySet<int>? ExplorerBlocks(Ship ship)
    {
        if (ship.Role != ShipRole.ExplorationShip || ship.GetEmpire() is not { IsPlayer: true } empire ||
            !Campaigns.TryGetValue(empire.Galaxy, out var campaign)) return null;
        // A previous manual mission must not exempt the NEXT automatic assignment.
        return campaign.Transit?.For(TransitGroup.Explorers);
    }

    private static bool BeginExplorationSearch(Ship __instance, Empire empire, bool allowRetrofit, out ExplorationSearch? __state, ref bool __result)
    {
        __state = explorationSearch;
        explorationSearch = __instance.Role == ShipRole.ExplorationShip && empire.IsPlayer &&
            Campaigns.TryGetValue(empire.Galaxy, out var campaign) && campaign.Observer.EmpireId == empire.EmpireId
            ? new(__instance, empire.Galaxy, campaign.Transit!.For(TransitGroup.Explorers), campaign) : null;
        if (explorationSearch is { } search && search.Campaign.Survey.Snapshot.Length > 0 &&
            PriorityExplorerAvailable(__instance, empire, false))
        {
            if (__instance.CheckRepairRefuelRetrofit(empire, empire.Galaxy.Time, false, out bool disabled, allowRetrofit))
            { __result = true; return false; }
            if (!disabled && TryAssignSurvey(search.Campaign, __instance, atObjectBoundary: false))
            { __result = true; return false; }
        }
        return true;
    }

    private static void AugmentExplorationPaths(Galaxy galaxy, Empire empire, int systemId, float jumpRange,
        ref SystemPathTimeList? __result)
    {
        var search = explorationSearch;
        if (assessmentDepth != 0 || search == null || !ReferenceEquals(search.Galaxy, galaxy) ||
            !ReferenceEquals(search.Campaign.Observer, empire) || search.Ship.Summary == null ||
            search.Campaign.ActivePoints.Length == 0 || search.Ship.GetSystem(galaxy, true)?.SystemId != systemId) return;
        try
        {
            var ship = search.Ship;
            var cache = ExplorationRoutes.GetOrCreateValue(ship);
            // Costs are geometry/terrain costs. Current-fuel feasibility is still decided by the native fuel method.
            var capability = ShipCapability(ship) with { PoweredRange = double.MaxValue };
            lock (cache.Gate)
            {
                bool changedNetwork = cache.Points != search.Campaign.ActivePoints ||
                    !ReferenceEquals(cache.Transit, search.Campaign.Transit!.Revision) || cache.Capability != capability;
                if (changedNetwork)
                {
                    cache.Points = search.Campaign.ActivePoints; cache.Transit = search.Campaign.Transit!.Revision;
                    cache.Capability = capability;
                    cache.Cursor = 0; cache.Costs.Clear();
                    var priorities = search.Campaign.Survey.Snapshot.Select(e => e.SystemId).ToHashSet();
                    cache.Systems = galaxy.Systems.Values.Where(s => s != null)
                        .OrderBy(s => priorities.Contains(s.SystemId) ? 0 : 1)
                        .ThenBy(s => cache.Points.Min(p => RouteChoice.Distance(p, new(s.GalaxyX, s.GalaxyY))))
                        .ThenBy(s => s.SystemId).ToArray();
                }
                if (changedNetwork || cache.Origin != Coordinates(ship) || Environment.TickCount64 >= cache.Expires)
                {
                    // Moving invalidates all first-leg results, but never restarts the destination cursor.
                    cache.Origin = Coordinates(ship); cache.Expires = Environment.TickCount64 + 15_000; cache.Costs.Clear();
                }
                if (cache.Cursor == cache.Systems.Length) cache.Cursor = 0;
                // Each native assignment search advances a small portion; cached results make later searches cheap.
                while (cache.Cursor < cache.Systems.Length && search.Evaluations < 4 &&
                    (search.Evaluations == 0 || search.Budget.ElapsedMilliseconds < 16))
                {
                    var system = cache.Systems[cache.Cursor++]; search.Evaluations++;
                    if (search.Blocked.Contains(system.SystemId)) continue;
                    var assessment = AssessShipRoute(search.Campaign, ship, system, unlimitedPoweredRange: true);
                    if (assessment?.Network is { } route && route.Nodes.Any(n => n.LocationId < 0) &&
                        route.Cost > 0 && route.Cost < float.MaxValue)
                        cache.Costs[system.SystemId] = (float)route.Cost;
                }
                if (cache.Costs.Count == 0) return;
                var costs = new Dictionary<int, float>();
                if (__result != null)
                {
                    using var read = __result.RWLock.SharedScope();
                    foreach (var item in __result.List) costs[item.SystemId] = item.PathTime;
                }
                foreach (var item in cache.Costs)
                    if (!search.Blocked.Contains(item.Key) && (!costs.TryGetValue(item.Key, out float old) || item.Value < old))
                        costs[item.Key] = item.Value;
                // Never modify the galaxy's shared table, its rows, or another empire's cached search.
                __result = new SystemPathTimeList(costs, jumpRange, galaxy.StarDate);
            }
        }
        catch (Exception ex) { ReportError("Exploration waypoint eligibility", ex); }
    }

    private static void VerifyExplorationPathCost(Galaxy galaxy, Empire empire, int startSystemId,
        int destinationSystemId, ref float __result)
    {
        if (assessmentDepth != 0 || explorationSearch is not { } search || !ReferenceEquals(search.Galaxy, galaxy) ||
            !ReferenceEquals(search.Campaign.Observer, empire) || startSystemId == destinationSystemId ||
            search.Ship.GetSystem(galaxy, true)?.SystemId != startSystemId ||
            !galaxy.Systems.TryGetValue(destinationSystemId, out var destination) ||
            !ExplorationRoutes.TryGetValue(search.Ship, out var cache)) return;
        bool contributed;
        lock (cache.Gate) contributed = cache.Costs.ContainsKey(destinationSystemId);
        if (!contributed) return;
        // Cached candidate ordering is advisory. Real fuel acceptance always rechecks current origin, storms and transit.
        var fresh = AssessShipRoute(search.Campaign, search.Ship, destination, unlimitedPoweredRange: true);
        __result = fresh?.Network is { } route && route.Cost is > 0 and < float.MaxValue
            ? (float)route.Cost : float.MaxValue;
    }

    private static Exception? EndExplorationSearch(Exception? __exception, ExplorationSearch? __state)
    {
        explorationSearch = __state;
        return __exception;
    }

    private static void ExcludeExplorationSystems(Galaxy galaxy, Ship ship, ref StarSystemList? excludeSystemsSupplied)
    {
        if (explorationSearch is not { } search || !ReferenceEquals(search.Ship, ship) ||
            !ReferenceEquals(search.Galaxy, galaxy)) return;
        var copy = new StarSystemList();
        if (excludeSystemsSupplied != null)
            foreach (var system in excludeSystemsSupplied) copy.Add(system);
        foreach (int id in search.Blocked)
            if (!copy.ContainsId(id) && galaxy.Systems.TryGetValue(id, out var system)) copy.Add(system);
        excludeSystemsSupplied = copy;
    }

    private static bool ForbiddenExplorationTarget(StellarObject? target, Galaxy galaxy, IReadOnlySet<int> blocked) =>
        target != null && (blocked.Contains(target.GetSystem(galaxy)?.SystemId ?? -1) ||
            IsBlockedCoordinate(galaxy, Coordinates(target), blocked));

    private static bool FilterExplorationCandidate(Ship __instance, StellarObject destination, ref bool __result)
    {
        if (explorationSearch is not { } search || !ReferenceEquals(search.Ship, __instance) ||
            !ForbiddenExplorationTarget(destination, search.Galaxy, search.Blocked)) return true;
        __result = false;
        return false;
    }

    private static bool AllowExplorationAssignment(Ship __instance, StellarObject __0, bool manuallyAssigned, ref bool __result)
    {
        if (manuallyAssigned || ExplorerBlocks(__instance) is not { Count: > 0 } blocked ||
            __instance.GetEmpire() is not { } empire ||
            !ForbiddenExplorationTarget(__0, empire.Galaxy, blocked)) return true;
        __result = false;
        return false;
    }

    private static bool ReleaseBlockedExploration(Ship ship, ShipMission mission, Location? destination)
    {
        if (ship.Role != ShipRole.ExplorationShip || mission.IsManuallyAssigned ||
            ship.MissionAssignmentType == ShipMissionAssignmentType.Manual || ship.IsHyperjumping() ||
            !ReferenceEquals(ship.Mission, mission) ||
            (mission.Type != ShipMissionType.Move && !ShipMission.ExplorationMissionTypes.Contains(mission.Type))) return false;

        var previous = mission.Type;
        // Native cleanup releases reservations and clears the mission and jump countdown.
        // The normal automation tick then chooses a replacement (or waits), keeping service priorities.
        ship.ClearPreviousMissionRequirements(ShipMissionType.Undefined, false, false, true);
        ship.NextExplorationAssignmentDate = 0;
        Log($"Exploration replan {ship.Name} #{ship.ShipId}: released automatic {previous} to blocked {destination?.Name ?? "target"}; native automation will select a permitted target.");
        return true;
    }
}
