using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static int trafficReports;

    private static void InstallTrafficTracking(Harmony harmony)
    {
        var exit = AccessTools.Method(typeof(Ship), "ExitHyperjump") ?? throw new MissingMethodException("Ship.ExitHyperjump");
        harmony.Patch(exit, postfix: new HarmonyMethod(typeof(Entry), nameof(RecordWaypointExit)));
    }

    private static void RecordWaypointExit(Ship __instance, Location? exitLocation, Location? __result)
    {
        try
        {
            if (__instance.IsBase || __instance.IsFighter || __result == null ||
                __instance.GetGalaxy() is not { } galaxy || !Campaigns.TryGetValue(galaxy, out Campaign? campaign))
                return;

            // This is the actual native arrival result, not a nearest-system inference. It records only the observer's own ship;
            // UpdateMap later delivers any discovery event on the UI thread.
            if (__instance.EmpireId == campaign.Observer.EmpireId)
                campaign.ObserveOwnArrival(__instance, __result);

            Waypoint? planned = exitLocation is { IsTemporary: true } ? Coordinates(exitLocation) : null;
            Waypoint? actual = __result.IsTemporary ? Coordinates(__result) : null;
            if (TrafficLedger.MatchArrival(campaign.ActivePoints, planned, actual, Coordinates(__instance),
                    ExitMargin(__instance, exitLocation)) is not { } point)
                return;

            Empire observer = campaign.Observer;
            bool own = __instance.EmpireId == observer.EmpireId;
            if (!own && observer.VisibilityMap?.DetermineVisibilityTypeForItemAllEmpires(__instance, observer, galaxy, galaxy.StarDate)
                is not (VisibilityType.PartiallyVisible or VisibilityType.FullyVisible))
                return;

            campaign.RecordPassage(point, __instance.ShipId, galaxy.StarDate, Galaxy.FreighterShipRoles.Contains(__instance.Role), __instance.EmpireId);
            if (Interlocked.Increment(ref trafficReports) <= 32)
                Log($"Waypoint arrival ({point.X},{point.Y}): {__instance.Role}, {(own ? "own" : "observed foreign")}, ship #{__instance.ShipId}.");
        }
        catch (Exception ex)
        {
            ReportError("Waypoint arrival", ex);
        }
    }

}
