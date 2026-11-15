using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using HarmonyLib;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly Func<Ship, Empire, ShipMission, DateTime, bool> NativeReassignSurvey =
        AccessTools.Method(typeof(Ship), "CheckReassignExploreSystemLocation")
            .CreateDelegate<Func<Ship, Empire, ShipMission, DateTime, bool>>();
    private static readonly Func<Ship, bool> NativeCanReassign = AccessTools.Method(typeof(Ship), "CanCurrentlyReassignMission")
        .CreateDelegate<Func<Ship, bool>>();
    [ThreadStatic] private static bool assigningSurvey;
    private static readonly ConditionalWeakTable<Ship, SurveyReadiness> SurveyReadyShips = new();
    private sealed class SurveyReadiness { internal volatile SurveyReadySnapshot? Snapshot; }
    private sealed record SurveyReadySnapshot(ShipMission? Mission, StellarObject? Target, byte Command,
        Waypoint Position, RouteCapability Capability, float CruiseSpeed, long Expires);
    private readonly record struct PriorityRoute(double Seconds, double FuelCost)
    {
        internal bool Fits(Ship ship) => double.IsFinite(Seconds) && (FuelCost < 0 || FuelCost < ship.CurrentFuelRange(0));
    }
    private static bool SameSurveyGeometry(SurveyReadySnapshot a, SurveyReadySnapshot b) =>
        ReferenceEquals(a.Mission, b.Mission) && ReferenceEquals(a.Target, b.Target) && a.Command == b.Command &&
        a.Position == b.Position && a.CruiseSpeed == b.CruiseSpeed &&
        (a.Capability with { PoweredRange = 0 }) == (b.Capability with { PoweredRange = 0 });
    private sealed class SurveyRank(SurveyPriority entry, Waypoint[] points, object transit, int geometry)
    {
        internal readonly SurveyPriority Entry = entry;
        internal readonly Waypoint[] Points = points;
        internal readonly object Transit = transit;
        internal readonly int Geometry = geometry;
        internal readonly Dictionary<int, (SurveyReadySnapshot State, PriorityRoute Route)> Scores = new();
    }

    private static void InstallSurveyDispatch(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Ship), "CommandExplore"),
            transpiler: new HarmonyMethod(typeof(Entry), nameof(SurveyObjectBoundary)));
        harmony.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.CheckRepairRefuelRetrofit),
            [typeof(Empire), typeof(DateTime), typeof(bool), typeof(bool).MakeByRefType(), typeof(bool)]),
            postfix: new HarmonyMethod(typeof(Entry), nameof(ObserveSurveyReadiness)));
    }

    private static void ObserveSurveyReadiness(Ship __instance, Empire empire, bool disabledMovement, bool __result)
    {
        if (!__result && !disabledMovement && PriorityExplorerAvailable(__instance, empire, false)) MarkSurveyReady(__instance, empire);
        else if (SurveyReadyShips.TryGetValue(__instance, out var state)) state.Snapshot = null;
    }

    private static void MarkSurveyReady(Ship ship, Empire empire) => SurveyReadyShips.GetOrCreateValue(ship).Snapshot =
        new(ship.Mission, ship.Mission?.ResolveMissionTarget(empire.Galaxy, ShipCommandTargetType.PrimaryTarget),
            ship.Mission?.CommandProgress ?? 0, Coordinates(ship), ShipCapability(ship), ship.Summary.CruiseSpeed, Environment.TickCount64 + 2_000);

    private static bool SurveyReady(Ship ship, Empire empire, bool atObjectBoundary)
    {
        if (!PriorityExplorerAvailable(ship, empire, atObjectBoundary) || !SurveyReadyShips.TryGetValue(ship, out var state) ||
            state.Snapshot is not { } ready) return false;
        return Environment.TickCount64 < ready.Expires && ReferenceEquals(ready.Mission, ship.Mission) &&
            ReferenceEquals(ready.Target, ship.Mission?.ResolveMissionTarget(empire.Galaxy, ShipCommandTargetType.PrimaryTarget)) &&
            ready.Command == (ship.Mission?.CommandProgress ?? 0) && ready.Position == Coordinates(ship) &&
            ready.Capability == ShipCapability(ship) && ready.CruiseSpeed == ship.Summary.CruiseSpeed &&
            !CurrentSystemSurveyPending(ship, empire);
    }

    private static bool CurrentSystemSurveyPending(Ship ship, Empire empire)
    {
        try
        {
            var galaxy = empire.Galaxy;
            if (ship.GetLocation(galaxy) is not { } location || !IsPhysicalSystemLocation(galaxy, location) ||
                ship.GetSystem(galaxy) is not { } system || ExplorerBlocks(ship)?.Contains(system.SystemId) == true)
                return false;
            // Use the native scanner/danger/range filters; an unavailable future survey must not hold a scout forever.
            if (ship.Summary.HyperDriveJumpRange <= 0 || ship.Summary.HyperDriveSpeed <= 0)
                return empire.ExplorationMap.FindNearestInLocation(galaxy, empire, location, ship.GalaxyX, ship.GalaxyY,
                    (byte)Math.Clamp(ship.Summary.ScannerExplorationPower, 0, 255),
                    (byte)Math.Clamp(ship.Summary.ScannerExplorationSurveyAmount, 0, 255),
                    (byte)Math.Clamp(ship.Summary.ScannerExplorationSurveyMaximumLevel, 0, 255),
                    ship.CurrentFuelRange(0), null!, empire.DangerousLocations, ship, true, true, out _, false) != null;
            return ResolveSurveyTarget(empire, ship, system, out _) != null;
        }
        catch (Exception ex)
        {
            ReportError("Current-system survey availability; native exploration retained", ex);
            return true;
        }
    }

    private static IEnumerable<CodeInstruction> SurveyObjectBoundary(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var reassign = AccessTools.Method(typeof(Ship), "CheckReassignExploreSystemLocation");
        var calls = code.Where(i => i.Calls(reassign)).ToArray();
        if (calls.Length != 4) throw new InvalidOperationException("Unsupported native survey completion layout; no partial survey hook installed.");
        // GOG 1.3.8.4: first is an already-complete object; last follows actual scan/survey and hidden-item continuation.
        // The middle calls handle out-of-range/non-orb failures and must not release a current scan.
        foreach (var instruction in new[] { calls[0], calls[3] })
        { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(Entry), nameof(AfterSurveyObject)); }
        return code;
    }

    private static bool AfterSurveyObject(Ship ship, Empire empire, ShipMission mission, DateTime time)
    {
        if (PriorityExplorerAvailable(ship, empire, atObjectBoundary: true) && ReferenceEquals(ship.Mission, mission) &&
            Campaigns.TryGetValue(empire.Galaxy, out var campaign) && campaign.SurveyFault == null && campaign.Survey.Snapshot.Length > 0)
        {
            // Native system/location continuation gives repair/refuel/retrofit precedence at this same boundary.
            var location = ship.GetLocation(empire.Galaxy);
            bool allowRetrofit = location == null || location.CheckShipCanHandleLocationEffects(empire.Galaxy, ship, ship);
            if (ship.CheckRepairRefuelRetrofit(empire, time, false, out bool disabledMovement, allowRetrofit)) return true;
            if (!disabledMovement)
            {
                MarkSurveyReady(ship, empire);
                if (TryAssignSurvey(campaign, ship, atObjectBoundary: true)) return true;
            }
        }
        return NativeReassignSurvey(ship, empire, mission, time);
    }

    private static bool PriorityExplorerAvailable(Ship ship, Empire empire, bool atObjectBoundary) =>
        ship != null && empire != null && ReferenceEquals(ship.GetEmpire(), empire) && empire.IsPlayer && ship.Role == ShipRole.ExplorationShip &&
        !ship.IsDestroyed && !ship.IsHyperjumping() && !ship.InBattleComplete && ship.Fleet == null &&
        NativeCanReassign(ship) &&
        (ship.MissionAssignmentType is ShipMissionAssignmentType.FullyAutomate or ShipMissionAssignmentType.AutoExplore) && ship.Mission?.IsManuallyAssigned != true &&
        ship.Summary is { TopSpeed: > 0 } &&
        ship.UnbuiltComponentCount == 0 && ship.DamagedComponentCount == 0 && ship.DestroyedComponentCount == 0 &&
        !ship.ShouldRepair(empire, includeDamagedComponents: true) &&
        (ship.Mission == null || ship.Mission.Type == ShipMissionType.Undefined ||
            atObjectBoundary && ShipMission.ExplorationMissionTypes.Contains(ship.Mission.Type));

    private static void RefreshSurveyQueue(Campaign campaign)
    {
        var galaxy = campaign.Observer.Galaxy;
        var nativeSurveys = new Dictionary<int, Ship>();
        foreach (var explorer in campaign.Observer.Ships)
            if (explorer is { IsDestroyed: false, Role: ShipRole.ExplorationShip, Mission: { } mission } &&
                ShipMission.ExplorationMissionTypes.Contains(mission.Type) && SurveyMissionSystem(galaxy, mission) is >= 0 and var systemId)
                nativeSurveys.TryAdd(systemId, explorer);
        foreach (var entry in campaign.Survey.Snapshot)
        {
            if (FullySurveyed(campaign, entry.SystemId)) { campaign.Survey.Remove(entry.SystemId); continue; }
            if (entry.ShipId is not { } shipId)
            {
                // Loading restores IDs, then recognizes existing native/manual orders without issuing a second one.
                if (nativeSurveys.TryGetValue(entry.SystemId, out var existing)) campaign.Survey.Reserve(entry, existing.ShipId);
                continue;
            }
            var ship = galaxy.Ships.GetById(shipId);
            if (ship == null || ship.IsDestroyed || ship.EmpireId != campaign.Observer.EmpireId ||
                ship.Mission is not { } mission ||
                !ShipMission.ExplorationMissionTypes.Contains(mission.Type) ||
                SurveyMissionSystem(galaxy, mission) != entry.SystemId)
                campaign.Survey.Release(entry, "Waiting for an available explorer");
        }
    }

    private static int SurveyMissionSystem(Galaxy galaxy, ShipMission mission) =>
        mission.ResolveMissionTarget(galaxy, ShipCommandTargetType.SecondaryTarget)?.GetSystem(galaxy)?.SystemId ??
        mission.ResolveMissionTarget(galaxy, ShipCommandTargetType.PrimaryTarget)?.GetSystem(galaxy)?.SystemId ??
        galaxy.Locations.GetById(mission.DestinationLocationId)?.SystemId ?? -1;

    private static void RefreshSurveyUi(Campaign campaign)
    {
        long now = Environment.TickCount64;
        if (campaign.SurveyFault != null || campaign.Survey.Snapshot.Length == 0 ||
            now < campaign.NextSurveyRefresh || !Monitor.TryEnter(campaign.SurveyDispatchGate)) return;
        try { campaign.NextSurveyRefresh = now + 1_000; RefreshSurveyQueue(campaign); }
        finally { Monitor.Exit(campaign.SurveyDispatchGate); }
    }

    private static bool TryAssignSurvey(Campaign campaign, Ship ship, bool atObjectBoundary)
    {
        var empire = campaign.Observer;
        if (assigningSurvey || campaign.SurveyFault != null || campaign.Survey.Snapshot.Length == 0 ||
            !SurveyReady(ship, empire, atObjectBoundary) || !Monitor.TryEnter(empire.ExplorationAssignmentLock)) return false;
        if (!Monitor.TryEnter(campaign.SurveyDispatchGate))
        { Monitor.Exit(empire.ExplorationAssignmentLock); return false; }
        assigningSurvey = true;
        try
        {
            RefreshSurveyQueue(campaign);
            var entries = campaign.Survey.Snapshot;
            if (entries.Length == 0) return false;
            // A claimed system is a whole-system job, even though dispatch can begin at an object boundary.
            if (entries.Any(e => e.ShipId == ship.ShipId)) return false;
            ShipMission? originalMission = ship.Mission;
            byte originalCommand = originalMission?.CommandProgress ?? 0;
            var candidates = empire.Ships.Where(other => ReferenceEquals(other, ship) || SurveyReady(other, empire, false)).ToArray();
            int cursor = Math.Min(campaign.SurveyCursor, entries.Length - 1), examined = 0, routeEvaluations = 0;
            int geometry = NetworkGeometry(empire.Galaxy);
            var budget = Stopwatch.StartNew();
            for (; cursor < entries.Length; cursor++)
            {
                if (examined > 0 && (examined >= 4 || budget.ElapsedMilliseconds >= 16)) break;
                var entry = entries[cursor];
                if (entry.ShipId != null) continue;
                examined++;
                if (!empire.Galaxy.Systems.TryGetValue(entry.SystemId, out var system)) continue;
                if (campaign.Transit!.For(TransitGroup.Explorers).Contains(entry.SystemId))
                { campaign.Survey.SetStatus(entry, "Waiting: transit rule"); continue; }
                var rank = campaign.SurveyRank;
                if (rank == null || !ReferenceEquals(rank.Entry, entry) || !ReferenceEquals(rank.Points, campaign.ActivePoints) ||
                    !ReferenceEquals(rank.Transit, campaign.Transit.Revision) || rank.Geometry != geometry)
                    campaign.SurveyRank = rank = new(entry, campaign.ActivePoints, campaign.Transit.Revision, geometry);
                Ship? nearest = null; double nearestSeconds = double.PositiveInfinity;
                bool incomplete = false;
                string? environmentReason = null;
                foreach (var candidate in candidates)
                {
                    if (!SurveyReady(candidate, empire, ReferenceEquals(candidate, ship) && atObjectBoundary) ||
                        SurveyReadyShips.GetOrCreateValue(candidate).Snapshot is not { } ready) continue;
                    if (EnvironmentReason(candidate, system.Star) is { } refusal)
                    { environmentReason ??= refusal; continue; }
                    if (!rank.Scores.TryGetValue(candidate.ShipId, out var score) || !SameSurveyGeometry(score.State, ready))
                    {
                        if (routeEvaluations >= 4 || routeEvaluations > 0 && budget.ElapsedMilliseconds >= 16) { incomplete = true; break; }
                        routeEvaluations++;
                        var route = PriorityRouteFor(campaign, candidate, system);
                        rank.Scores[candidate.ShipId] = score = (ready, route);
                    }
                    if (!score.Route.Fits(candidate)) continue;
                    double seconds = score.Route.Seconds;
                    if (seconds < nearestSeconds || seconds == nearestSeconds && (nearest == null || candidate.ShipId < nearest.ShipId))
                    { nearest = candidate; nearestSeconds = seconds; }
                }
                if (incomplete) break;
                campaign.SurveyRank = null;
                if (nearest == null) { campaign.Survey.SetStatus(entry, "Waiting: " + (environmentReason ?? "no suitable explorer, scanner, safe route or fuel")); continue; }
                if (!ReferenceEquals(nearest, ship)) { campaign.Survey.SetStatus(entry, "Waiting for the nearest available explorer"); continue; }
                if (!ReferenceEquals(rank.Points, campaign.ActivePoints) || !ReferenceEquals(rank.Transit, campaign.Transit.Revision) ||
                    FullySurveyed(campaign, system.SystemId) || geometry != NetworkGeometry(empire.Galaxy) ||
                    !double.IsFinite(PriorityTravelSeconds(campaign, ship, system)) ||
                    !ReferenceEquals(ship.Mission, originalMission) || (ship.Mission?.CommandProgress ?? 0) != originalCommand ||
                    !PriorityExplorerAvailable(ship, empire, atObjectBoundary) || !campaign.Survey.Reserve(entry, ship.ShipId)) continue;
                var reservation = campaign.Survey.Snapshot.FirstOrDefault(e => e.SystemId == entry.SystemId && e.ShipId == ship.ShipId);
                if (reservation == null) continue;
                bool assigned = false;
                try
                {
                    if (!ReferenceEquals(ship.Mission, originalMission) || !SurveyReady(ship, empire, atObjectBoundary)) continue;
                    if (ship.Summary.HyperDriveJumpRange > 0 && ship.Summary.HyperDriveSpeed > 0)
                        assigned = ship.AssignMissionExploreSystem(system.Star, null!, false);
                    else if (ship.GetSystem(empire.Galaxy)?.SystemId == system.SystemId &&
                        ResolveSurveyTarget(empire, ship, system, out bool shouldSurvey) is { } target)
                        // Prewarp uses explicit native object orders; the actual Ship Logistics leash still vets the target.
                        assigned = shouldSurvey ? ship.AssignMissionExploreSurvey(target, null!, false) : ship.AssignMissionExplore(target, null!, false);
                    if (assigned) { campaign.SurveyCursor = 0; return true; }
                }
                finally
                {
                    if (!assigned) campaign.Survey.Release(reservation, "Waiting: native assignment declined");
                }
            }
            campaign.SurveyCursor = cursor >= entries.Length ? 0 : cursor;
            return false;
        }
        catch (Exception ex) { ReportError("Priority survey assignment", ex); return false; }
        finally
        {
            assigningSurvey = false;
            Monitor.Exit(campaign.SurveyDispatchGate);
            Monitor.Exit(empire.ExplorationAssignmentLock);
        }
    }

    private static double PriorityTravelSeconds(Campaign campaign, Ship candidate, StarSystem system)
    {
        var route = PriorityRouteFor(campaign, candidate, system);
        return route.Fits(candidate) ? route.Seconds : double.PositiveInfinity;
    }

    private static PriorityRoute PriorityRouteFor(Campaign campaign, Ship candidate, StarSystem system)
    {
        var empire = campaign.Observer;
        if (candidate.Summary == null || !system.CheckShipCanHandleLocationEffects(empire.Galaxy, candidate) ||
            SurveyTarget(empire, candidate, system) is not { } target) return new(double.PositiveInfinity, double.PositiveInfinity);
        if (candidate.GetSystem(empire.Galaxy)?.SystemId == system.SystemId)
            return new(RouteChoice.Distance(Coordinates(candidate), Coordinates(target)) / Math.Max(1, candidate.Summary.CruiseSpeed), -1);
        // Cache geometry and powered travel time independently of continuously changing fuel/energy.
        // Actual current native fuel is checked cheaply on every reuse and again before issuing an order.
        var assessment = AssessShipRoute(campaign, candidate, system, unlimitedPoweredRange: true);
        return assessment?.Network is { } route ? new(assessment.NetworkSeconds, route.Cost) : new(double.PositiveInfinity, double.PositiveInfinity);
    }

    private static Orb? SurveyTarget(Empire empire, Ship ship, StarSystem system) => ResolveSurveyTarget(empire, ship, system, out _);

    private static Orb? ResolveSurveyTarget(Empire empire, Ship ship, StarSystem system, out bool shouldSurvey)
    {
        var summary = ship.Summary;
        byte scan = (byte)Math.Clamp(summary.ScannerExplorationPower, 0, 255);
        byte survey = (byte)Math.Clamp(summary.ScannerExplorationSurveyAmount, 0, 255);
        byte maximum = (byte)Math.Clamp(summary.ScannerExplorationSurveyMaximumLevel, 0, 255);
        if (summary.HyperDriveJumpRange <= 0 || summary.HyperDriveSpeed <= 0)
        {
            shouldSurvey = false;
            if (ship.GetSystem(empire.Galaxy)?.SystemId != system.SystemId || ship.GetLocation(empire.Galaxy) is not { } location) return null;
            var local = empire.ExplorationMap.FindNearestInLocation(empire.Galaxy, empire, location, ship.GalaxyX, ship.GalaxyY,
                scan, survey, maximum, ship.CurrentFuelRange(0), null!, empire.DangerousLocations, ship, true, true, out shouldSurvey, false);
            if (local != null && EnvironmentReason(ship, local, approach: true) == null) return local;
        }
        float range = summary.HyperDriveJumpRange > 0 && summary.HyperDriveSpeed > 0 ? summary.HyperDriveJumpRange : ship.CurrentFuelRange(0);
        var result = empire.ExplorationMap.FindNearestInSystem(empire.Galaxy, empire, system, ship.GalaxyX, ship.GalaxyY,
            scan, survey, maximum, range,
            null!, empire.DangerousLocations, ship, true, out shouldSurvey, false, true);
        return result != null && EnvironmentReason(ship, result, approach: true) == null ? result : null;
    }
}
