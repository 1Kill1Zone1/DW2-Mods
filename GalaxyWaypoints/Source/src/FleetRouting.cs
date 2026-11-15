using System.Runtime.CompilerServices;
using DistantWorlds.Types;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly ConditionalWeakTable<Fleet, FleetDeparture> FleetDepartures = new();

    private static bool CheckFleetDeparture(Ship ship, Empire empire, Galaxy galaxy, Campaign campaign,
        ShipMission mission, ShipCommand command, StellarObject? target, Location current,
        ref float result, out bool coordinated)
    {
        coordinated = false;
        if (ship.Fleet is not { } fleet || current == null || mission.LocationPathProgress != 0 ||
            target is not Orb || target.GetLocation(galaxy) is not { IsTemporary: false } goal ||
            current.SystemId == goal.SystemId || !GroupOrder(mission.Type)) return true;

        var state = FleetDepartures.GetOrCreateValue(fleet);
        lock (state)
        {
            long now = Environment.TickCount64;
            bool same = state.Destination == goal.LocationId && ReferenceEquals(state.Points, campaign.Points) &&
                ReferenceEquals(state.TransitRevision, campaign.Transit?.Revision) &&
                state.Members.TryGetValue(ship, out ShipMission? previous) && ReferenceEquals(previous, mission);
            if (!same || state.Path == null && now >= state.RetryAt)
            {
                var members = fleet.Ships.GetReadOnlySpan().ToArray().Where(s => s != null && !s.IsDestroyedCached &&
                    s.Summary != null && s.Mission is { } m && m.Type == mission.Type &&
                    m.PrimaryTarget.Equals(mission.PrimaryTarget) && m.SecondaryTarget.Equals(mission.SecondaryTarget))
                    .OrderBy(s => s.ShipId).ToArray();
                if (members.Length < 2) return true;
                Storm[] storms = members.SelectMany(member => UnsafeEnvironment(member, galaxy)).Distinct().ToArray();
                double margin = members.Max(s => ExitMargin(s, null));
                // Explicit military orders into a storm retain the existing manual-order semantics.
                if (Unsafe(storms, Coordinates(goal), margin)) return true;
                state.Destination = goal.LocationId;
                state.Points = campaign.Points;
                state.TransitRevision = campaign.Transit?.Revision;
                state.Members = members.ToDictionary(s => s, s => s.Mission);
                state.Path = null;
                state.RetryAt = now + 2_000;
                var pending = members.Where(s => !s.IsHyperjumping()).ToArray();
                if (pending.Length == 0) return true;
                Ship lead = pending[0];
                var start = Coordinates(lead);
                double range = members.Min(s => (double)s.Summary.HyperDriveJumpRange);
                double minimum = members.Max(s => Math.Max(1, s.CalculateHyperjumpThreshold()));
                TravelTiming[] timing = members.Select(TravelTiming.For).ToArray();
                var blocks = members.Select(s => empire.IsPlayer ? campaign.Transit?.For(s, s.Mission) : null)
                    .Where(b => b is { Count: > 0 }).ToArray();
                bool Safe(Waypoint p) => !Unsafe(storms, p, margin) &&
                    !blocks.Any(b => IsBlockedCoordinate(galaxy, p, b!));
                var anchors = new List<RouteAnchor> { new(current.LocationId, start) };
                foreach (int id in lead.Mission.LocationIdPath ?? new())
                {
                    if (id == goal.LocationId) break;
                    if (id != current.LocationId && galaxy.Locations.GetById(id) is { } hop &&
                        anchors.All(a => a.LocationId != id)) anchors.Add(new(id, Coordinates(hop)));
                }
                anchors.Add(new(goal.LocationId, Coordinates(goal)));
                double Edge(Waypoint a, Waypoint b)
                {
                    if (a != start) return LineCost(galaxy, a, b);
                    double cost = 0;
                    foreach (var member in pending)
                    {
                        var from = Coordinates(member);
                        if (!RouteChoice.CanJump(RouteChoice.Distance(from, b),
                            Math.Max(1, member.CalculateHyperjumpThreshold()), member.Summary.HyperDriveJumpRange))
                            return double.PositiveInfinity;
                        cost = Math.Max(cost, LineCost(galaxy, from, b));
                    }
                    return cost;
                }
                var plan = WaypointRouting.Find(anchors, campaign.ActivePoints, true, minimum, range, 0, 0,
                    Safe, Edge, allowOrdinaryResult: true,
                    estimate: (cost, hops) => timing.Max(t => t.Seconds(cost, hops)));
                if (plan != null)
                {
                    var path = new List<int>();
                    foreach (var node in plan.Nodes.Skip(1))
                    {
                        int id = node.LocationId;
                        if (id < 0)
                        {
                            if (campaign.ResolveLocation(galaxy, node.Point) is not { } location) { path.Clear(); break; }
                            id = location.LocationId;
                        }
                        path.Add(id);
                    }
                    if (path.Count > 0) state.Path = path.ToArray();
                }
                if (empire.IsPlayer && (state.Path != null || now >= state.NextLog))
                {
                    Log($"Fleet route {fleet.Name} to {goal.Name}: " +
                        (state.Path == null ? "waiting: no permitted route within jump limits" :
                        $"hops=[{string.Join(",", state.Path)}], members={members.Length}; fuel affects travel time"));
                    state.NextLog = now + 30_000;
                }
            }
            coordinated = true;
            if (state.Path != null && VerifyReplacement(ship, galaxy, mission, command, target, current,
                UnsafeEnvironment(ship, galaxy), state.Path.ToList())) return true;
            if (state.Path != null)
            {
                state.Path = null;
                state.RetryAt = now + 2_000;
            }
            result = 0;
            ship.TargetSpeed = 0;
            return false;
        }
    }

    private static bool GroupOrder(ShipMissionType type) => type == ShipMissionType.Move ||
        ShipMission.AttackMissionTypes.Contains(type) || ShipMission.InvadeOnlyMissionTypes.Contains(type) ||
        ShipMission.BlockadeOnlyMissionTypes.Contains(type) || ShipMission.BombardOnlyMissionTypes.Contains(type);

    private sealed class FleetDeparture
    {
        public int Destination;
        public Waypoint[]? Points;
        public object? TransitRevision;
        public Dictionary<Ship, ShipMission> Members = new();
        public int[]? Path;
        public long RetryAt, NextLog;
    }
}
