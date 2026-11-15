using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private readonly record struct EnvironmentRegion(Storm Area, EnvironmentLoad Load);

    private static EnvironmentProtection Protection(Ship ship)
    {
        var s = ship.Summary;
        return s == null ? new(double.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false) :
            new(s.ArmorReactive, s.IonDefense, s.ShieldResistance, s.ShieldCapacity, s.ShieldRechargeRate,
                s.ShieldRechargeEnergyUsage, s.ReactorEnergyOutputPerSecond, s.ReactorEnergyStorageCapacity,
                s.StaticEnergyConsumption + Math.Max(s.HyperDriveEnergyUsage, Math.Max(s.TopSpeedEnergyUsage, s.CruiseSpeedEnergyUsage)),
                ship.Energy, ship.Shields, ship.Fuel > 0);
    }

    private static EnvironmentLoad PeakEffects(Galaxy galaxy, IEnumerable<LocationEffectGroup> groups, Location? location = null,
        StellarObject? target = null, Ship? approaching = null)
    {
        EnvironmentLoad total = default;
        foreach (var group in groups)
        {
            if (group?.Effects == null || !float.IsFinite(group.AmountModifier) || group.AmountModifier < 0)
            { total += new EnvironmentLoad(0, 0, 0, 0, 0, 0, true); continue; }
            var definition = galaxy.LocationEffectGroupDefinitions.GetById(group.LocationEffectGroupDefinitionId);
            if (definition?.EffectDefinitions == null) { total += new EnvironmentLoad(0, 0, 0, 0, 0, 0, true); continue; }
            foreach (var effect in group.Effects)
            {
                var d = effect == null ? null : definition.EffectDefinitions.GetById(effect.LocationEffectDefinitionId);
                if (d == null || !float.IsFinite(d.AmountMaximum) || d.AmountMaximum < 0 || d.AmountMaximum < d.AmountMinimum)
                { total += new EnvironmentLoad(0, 0, 0, 0, 0, 0, true); continue; }
                double peak = d.AmountMaximum * group.AmountModifier;
                if (d.RangeCollisionSphereMultiplier > 0)
                {
                    if (location == null || target == null) continue; // Local range is assessed at the actual object/approach below.
                    if (location.Orbs.FirstOrDefault() is not { } center) { total += new EnvironmentLoad(0, 0, 0, 0, 0, 0, true); continue; }
                    double radius = center.CollisionSphereRadius * d.RangeCollisionSphereMultiplier;
                    double distance = Vector3.Distance(center.GetPositionRaw(), target.GetPositionRaw());
                    if (approaching?.LocationId == location.LocationId)
                        distance = SegmentDistance(center.GetPositionRaw(), approaching.GetPositionRaw(), target.GetPositionRaw());
                    peak *= radius > 0 ? Math.Clamp(1 - distance / radius, 0, 1) : 0;
                }
                // Pulsed drains cannot be certified from an average; never label an unbounded burst safe.
                bool uncertain = (int)d.Mode == 1 && (int)d.Type is 5 or 6 && peak > 0;
                total += (int)d.Type switch
                {
                    1 => new(0, 0, peak, 0, 0, 0), 2 => new(0, 0, 0, 0, 0, peak),
                    3 => new(peak, 0, 0, 0, 0, 0), 4 => new(0, peak, 0, 0, 0, 0),
                    5 => new(0, 0, 0, peak, 0, 0, uncertain), 6 => new(0, 0, 0, 0, peak, 0, uncertain),
                    10 when peak > 0 => new(0, 0, 0, 0, 0, 0, true),
                    _ => default
                };
            }
        }
        return total;
    }

    private static double SegmentDistance(Vector3 point, Vector3 a, Vector3 b)
    {
        var delta = b - a;
        float length = delta.LengthSquared();
        float t = length > 0 ? Math.Clamp(Vector3.Dot(point - a, delta) / length, 0, 1) : 0;
        return Vector3.Distance(point, a + delta * t);
    }

    private static EnvironmentRegion[] KnownEnvironment(Galaxy galaxy, Empire observer) => galaxy.NebulaClouds
        .Where(c => c?.EffectGroups != null && c.Radius > 0 && observer.VisibilityMap?.CheckNebulaKnown(c.NebulaCloudId) == true)
        .Select(c => new EnvironmentRegion(new(new(c.GalaxyX, c.GalaxyY), c.Radius), PeakEffects(galaxy, c.EffectGroups)))
        .Where(r => r.Load.Any).ToArray();

    private static Storm[] UnsafeEnvironment(Ship ship, Galaxy galaxy)
    {
        var observer = ship.GetEmpire();
        if (observer?.IsPlayer != true) return GetStorms(galaxy);
        var regions = KnownEnvironment(galaxy, observer);
        var protection = Protection(ship);
        // Worst simultaneous overlap, including peaks. An individually tolerable cloud can overlap a second drain.
        return regions.Where(region => protection.Refusal(regions.Where(other =>
                RouteChoice.Distance(region.Area.Center, other.Area.Center) <= region.Area.Radius + other.Area.Radius)
            .Aggregate(default(EnvironmentLoad), (load, other) => load + other.Load)) != null).Select(r => r.Area).ToArray();
    }

    private static string? EnvironmentReason(Ship ship, StellarObject target, bool approach = false)
    {
        var empire = ship.GetEmpire();
        if (empire?.IsPlayer != true) return null;
        var galaxy = empire.Galaxy;
        var regions = KnownEnvironment(galaxy, empire);
        var point = Coordinates(target);
        var exposure = regions.Where(r => r.Area.Contains(point, approach ? 0 : ExitMargin(ship, target.GetLocation(galaxy))) ||
                approach && ship.LocationId == target.LocationId && SegmentHits(Coordinates(ship), point, r.Area))
            .Aggregate(default(EnvironmentLoad), (load, region) => load + region.Load);
        if (target.GetLocation(galaxy) is { EffectGroups.Count: > 0 } location && empire.VisibilityMap != null &&
            empire.VisibilityMap.GetSystemVisibilityStatus(location.SystemId) != VisibilityStatus.Unexplored)
        {
            // Location groups can be aliases of the cloud groups; apply each environmental source once.
            var cloudGroups = galaxy.NebulaClouds.Where(c => c?.EffectGroups != null && new Storm(new(c.GalaxyX, c.GalaxyY), c.Radius).Contains(point, 0))
                .SelectMany(c => c.EffectGroups).Select(g => g.LocationEffectGroupDefinitionId).ToHashSet();
            exposure += PeakEffects(galaxy, location.EffectGroups.Where(g => !cloudGroups.Contains(g.LocationEffectGroupDefinitionId)), location, target, approach ? ship : null);
        }
        return Protection(ship).Refusal(exposure);
    }

    private static bool SegmentHits(Waypoint a, Waypoint b, Storm storm)
    {
        double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
        double length = dx * dx + dy * dy;
        double t = length > 0 ? Math.Clamp(((storm.Center.X - (double)a.X) * dx + (storm.Center.Y - (double)a.Y) * dy) / length, 0, 1) : 0;
        double x = a.X + t * dx - storm.Center.X, y = a.Y + t * dy - storm.Center.Y;
        return x * x + y * y <= storm.Radius * storm.Radius;
    }

    // Optional BCL-only bridge for FD: called before reservations/contracts are created.
    public static string? AutomaticEnvironmentBlockReason(object vessel, object destination)
    {
        try { return vessel is Ship ship && destination is StellarObject target ? EnvironmentReason(ship, target) : "invalid environmental eligibility request"; }
        catch (Exception ex) { ReportError("Environment eligibility", ex); return "environment assessment unavailable"; }
    }

    private static void InstallEnvironmentSafety(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Ship), "CheckCanHandleLocationEffects"), postfix: new HarmonyMethod(typeof(Entry), nameof(CheckEnvironmentalCandidate)));
        harmony.Patch(AccessTools.Method(typeof(Ship), "CommandMovement"), prefix: new HarmonyMethod(typeof(Entry), nameof(CheckEnvironmentalMovement)) { priority = Priority.Last });
    }

    private static void CheckEnvironmentalCandidate(Ship __instance, StellarObject positionItem, ref bool __result)
    {
        if (__result && positionItem != null && __instance.GetEmpire()?.IsPlayer == true)
            __result = AutomaticEnvironmentBlockReason(__instance, positionItem) == null;
    }

    private static bool CheckEnvironmentalMovement(Ship __instance, Empire empire, ShipMission mission,
        StellarObject? missionTarget, Point missionGalaxyCoordinates, ref float __result)
    {
        if (empire?.IsPlayer != true || __instance.IsBase || mission == null) return true;
        if (missionTarget == null)
        {
            if (missionGalaxyCoordinates == Point.Zero) return true;
            var goal = new Waypoint(missionGalaxyCoordinates.X, missionGalaxyCoordinates.Y);
            bool manual = mission.IsManuallyAssigned || __instance.MissionAssignmentType == ShipMissionAssignmentType.Manual;
            try
            {
                string? refusal = CoordinateMovementReason(__instance, goal) ?? CommonCoordinateFleetReason(__instance, mission, goal);
                if (manual)
                { if (refusal != null) WarnManualEnvironment(__instance, mission, null, refusal); return true; }
                if (refusal == null) return true;
                __result = 0; __instance.TargetSpeed = 0;
                RecordHold(__instance, mission, goal, refusal); return false;
            }
            catch (Exception ex) { ReportError("Coordinate environment guard", ex); __result = 0; __instance.TargetSpeed = 0; return false; }
        }
        if (mission.IsManuallyAssigned || __instance.MissionAssignmentType == ShipMissionAssignmentType.Manual)
        { WarnManualEnvironment(__instance, mission, missionTarget); return true; }
        try
        {
            string? reason = CommonFleetEnvironmentReason(__instance, mission, missionTarget) ?? EnvironmentReason(__instance, missionTarget, approach: true);
            if (reason == null || EscapingEnvironment(__instance, missionTarget)) return true;
            __result = 0; __instance.TargetSpeed = 0;
            RecordHold(__instance, mission, Coordinates(missionTarget), reason);
            return false;
        }
        catch (Exception ex) { ReportError("Local environment guard", ex); __result = 0; __instance.TargetSpeed = 0; return false; }
    }

    private static string? CoordinateMovementReason(Ship ship, Waypoint goal, bool localApproach = true)
    {
        if (ship.GetEmpire() is not { IsPlayer: true } empire) return null;
        var current = Coordinates(ship);
        bool sublight = localApproach && (ship.Summary?.HyperDriveJumpRange <= 0 ||
            RouteChoice.Distance(current, goal) <= ship.CalculateHyperjumpThreshold());
        var exposure = KnownEnvironment(empire.Galaxy, empire).Where(region => region.Area.Contains(goal, 0) ||
            sublight && !region.Area.Contains(current, 0) && SegmentHits(current, goal, region.Area))
            .Aggregate(default(EnvironmentLoad), (load, region) => load + region.Load);
        return Protection(ship).Refusal(exposure);
    }

    private static string? CommonCoordinateFleetReason(Ship ship, ShipMission mission, Waypoint goal, bool localApproach = true)
    {
        if (ship.Fleet is not { Mission: { } fleetOrder } fleet || !GroupOrder(mission.Type) || fleetOrder.Type != mission.Type ||
            !fleetOrder.PrimaryTarget.Equals(mission.PrimaryTarget) || !fleetOrder.SecondaryTarget.Equals(mission.SecondaryTarget)) return null;
        foreach (var member in fleet.Ships)
            if (member != null && !member.IsDestroyed && member.Mission is { IsManuallyAssigned: false } other && other.Type == mission.Type &&
                other.PrimaryTarget.Equals(mission.PrimaryTarget) && other.SecondaryTarget.Equals(mission.SecondaryTarget) &&
                CoordinateMovementReason(member, goal, localApproach) is { } reason) return $"Fleet waits for {member.Name}: {reason}";
        return null;
    }

    private static bool EscapingEnvironment(Ship ship, StellarObject target)
    {
        if (ship.GetEmpire() is not { } empire) return false;
        var galaxy = empire.Galaxy;
        var unsafeAreas = UnsafeEnvironment(ship, galaxy);
        var current = Coordinates(ship); var goal = Coordinates(target);
        var occupied = unsafeAreas.Where(area => area.Contains(current, 0)).ToArray();
        return EnvironmentReason(ship, ship) != null && EnvironmentReason(ship, target) == null &&
            !unsafeAreas.Any(area => area.Contains(goal, 0)) && occupied.All(area => RouteChoice.Distance(goal, area.Center) > RouteChoice.Distance(current, area.Center));
    }

    private static string? CommonFleetEnvironmentReason(Ship ship, ShipMission mission, StellarObject target)
    {
        if (ship.Fleet == null || !GroupOrder(mission.Type)) return null;
        foreach (var member in ship.Fleet.Ships)
            if (member != null && !member.IsDestroyed && member.Mission is { } other && other.Type == mission.Type &&
                other.PrimaryTarget.Equals(mission.PrimaryTarget) && other.SecondaryTarget.Equals(mission.SecondaryTarget) &&
                !other.IsManuallyAssigned)
            {
                if (member.GetEmpire() is not { } owner) return "Fleet member environment data unavailable";
                if (EnvironmentReason(member, target, approach: true) is { } reason)
                    return $"Fleet waits for {member.Name}: {reason}";
                var hazards = UnsafeEnvironment(member, owner.Galaxy);
                if (other.LocationIdPath?.Skip(other.LocationPathProgress).Any(id => id != member.LocationId &&
                    owner.Galaxy.Locations.GetById(id) is { } hop && Unsafe(hazards, Coordinates(hop), ExitMargin(member, hop))) == true)
                    return $"Fleet waits for {member.Name}: unsafe intermediate exit";
            }
        return null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ship, ManualEnvironmentWarning> ManualWarnings = new();
    private sealed class ManualEnvironmentWarning { internal ShipMission? Mission; internal long Next; }
    private static void WarnManualEnvironment(Ship ship, ShipMission mission, StellarObject? target, string? knownReason = null)
    {
        try
        {
            if (ship.GetEmpire()?.IsPlayer != true || (knownReason ?? (target == null ? null : EnvironmentReason(ship, target))) is not { } reason) return;
            var warning = ManualWarnings.GetOrCreateValue(ship);
            long now = Environment.TickCount64;
            if (ReferenceEquals(warning.Mission, mission) && now < warning.Next) return;
            warning.Mission = mission; warning.Next = now + 30_000;
            LocalModUi.MapUiGate.CurrentRenderer?.SetBriefMessage($"Manual order overrides environmental protection: {ship.Name}: {reason}", 8);
            Log($"Manual environmental override {ship.Role} #{ship.ShipId}: {reason}");
        }
        catch (Exception ex) { ReportError("Manual environment warning", ex); }
    }
}
