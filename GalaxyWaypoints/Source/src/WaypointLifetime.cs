using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private const string RouteNodeName = "Galaxy Waypoints route node";
    private static readonly ConditionalWeakTable<Location, NodeLease> NodeLeases = new();
    private static readonly ConditionalWeakTable<Galaxy, RouteReferences> References = new();
    private static readonly ConditionalWeakTable<Ship, RouteRepair> Repairs = new();

    private static void InstallWaypointLifetime(Harmony harmony)
    {
        foreach (string name in new[] { nameof(Location.ReviewTemporaryLocation), nameof(Location.RemoveFromGalaxy) })
            harmony.Patch(AccessTools.Method(typeof(Location), name),
                prefix: new HarmonyMethod(typeof(Entry), nameof(KeepRouteNode)));
        harmony.Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.ReadFromStream)),
            postfix: new HarmonyMethod(typeof(Entry), nameof(LoadedWaypoints)));
    }

    private static Campaign? GetCampaign(Galaxy galaxy)
    {
        Empire? player = galaxy.Empires?.FirstOrDefault(empire => empire?.IsPlayer == true);
        if (Campaigns.TryGetValue(galaxy, out Campaign? campaign))
        {
            if (ReferenceEquals(campaign.Observer, player)) return campaign;
            // Observer switches must retain the old observer's pending priorities before loading the new partition.
            campaign.Persist();
            Campaigns.Remove(galaxy);
        }
        return player == null ? null : Campaigns.GetValue(galaxy, key => new Campaign(key, player));
    }

    private static void LoadedWaypoints(Galaxy __result)
    {
        try
        {
            if (__result != null && GetCampaign(__result) is { } campaign)
            { campaign.DisplayMode = WaypointDisplayMode.Discovered; campaign.Selection = null; }
        }
        catch (Exception ex) { ReportError("Load waypoint nodes", ex); }
    }

    private static bool KeepRouteNode(Location __instance, Galaxy galaxy)
    {
        if (!__instance.IsTemporary)
            return true;
        bool owned = __instance.Name == RouteNodeName;
        try
        {
            Campaign? campaign = GetCampaign(galaxy);
            if (campaign?.ContainsActive(Coordinates(__instance)) == true)
                return false;
            if (!owned)
                return true;
            if (NodeLeases.TryGetValue(__instance, out NodeLease? lease) && Environment.TickCount64 < lease.Until)
                return false;
            return !References.GetOrCreateValue(galaxy).Contains(galaxy, __instance.LocationId);
        }
        catch (Exception ex)
        {
            ReportError("Keep waypoint node", ex);
            return !owned;
        }
    }

    private static bool RepairMissingRoute(Ship ship, Empire empire, ShipMission mission, StellarObject? target,
        Point targetCoordinates, Location current, ref float result)
    {
        Galaxy galaxy = empire.Galaxy;
        List<int>? old = mission.LocationIdPath;
        if (old == null || !old.Skip(mission.LocationPathProgress).Any(id => galaxy.Locations.GetById(id) == null))
            return true;

        // Never execute a dangling hop. Retain the mission and retry without inventing a mapping for an old ID.
        result = 0;
        ship.TargetSpeed = 0;
        RouteRepair retry = Repairs.GetOrCreateValue(ship);
        long now = Environment.TickCount64;
        if (ReferenceEquals(retry.Mission, mission) && now < retry.At)
            return false;
        retry.Mission = mission;
        retry.At = now + 2_000;
        try
        {
            GetCampaign(galaxy);
            Location? destination = target?.GetLocation(galaxy) ?? galaxy.Locations.GetById(mission.DestinationLocationId);
            if (current == null || ship.Summary is not { } summary)
                return false;
            List<int>? route = destination != null
                ? empire.FindBestJumpPathToDestination(ship, summary.HyperDriveJumpRange, 0, true,
                    current, destination, out _, out _, new Point(ship.GalaxyX, ship.GalaxyY), Point.Zero)
                : targetCoordinates != Point.Zero && targetCoordinates.X > 0 && targetCoordinates.Y > 0
                    ? empire.FindBestJumpPathToDestination(ship, targetCoordinates.X, targetCoordinates.Y, out _, out _)
                    : null;
            if (route is not { Count: > 0 } || route.Any(id => galaxy.Locations.GetById(id) == null))
                return false;
            mission.LocationIdPath = route;
            mission.LocationPathProgress = 0;
            DepartureChecks.Remove(ship);
            Log($"Recovered route {ship.Role} #{ship.ShipId} to {destination?.Name ?? targetCoordinates.ToString()}: " +
                $"[{string.Join(",", old)}] -> [{string.Join(",", route)}].");
            return true;
        }
        catch (Exception ex)
        {
            ReportError("Recover waypoint route", ex);
            return false;
        }
    }

    private sealed class NodeLease { public long Until; }
    private sealed class RouteRepair { public ShipMission? Mission; public long At; }

    private sealed class RouteReferences
    {
        private readonly object gate = new();
        private HashSet<int> ids = new();
        private long expires;

        public bool Contains(Galaxy galaxy, int id)
        {
            lock (gate)
            {
                long now = Environment.TickCount64;
                if (now >= expires)
                {
                    var next = new HashSet<int>();
                    foreach (Ship ship in galaxy.Ships)
                    {
                        if (ship == null) continue;
                        Add(next, ship.Mission);
                        Add(next, ship.RevertMission);
                        if (ship.NextMissions != null)
                            foreach (ShipMission queued in ship.NextMissions) Add(next, queued);
                    }
                    ids = next;
                    expires = now + 2_000;
                }
                return ids.Contains(id);
            }
        }

        private static void Add(HashSet<int> ids, ShipMission? mission)
        {
            if (mission?.LocationIdPath is { } path)
                for (int i = mission.LocationPathProgress; i < path.Count; i++) ids.Add(path[i]);
        }
    }
}
