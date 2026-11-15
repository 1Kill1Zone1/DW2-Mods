using DistantWorlds.Types;

namespace GalaxyWaypoints;

internal readonly record struct GeneratedWaypoint(Waypoint Point, int AnchorSystemId)
{
    public bool Valid(Galaxy galaxy) => AnchorSystemId >= 0 && galaxy.Systems.ContainsKey(AnchorSystemId) && Point.X > 0 && Point.Y > 0 && Point.X < galaxy.SizeX && Point.Y < galaxy.SizeY;
    // Matches ExplorationMap.CountExploredSystems: a visible system with no unknown items and a usable survey level.
    public bool IsExplored(Galaxy galaxy, Empire observer) => Valid(galaxy) && observer.VisibilityMap is { } visibility &&
        visibility.GetSystemVisibilityStatus(AnchorSystemId) != VisibilityStatus.Unexplored &&
        observer.ExplorationMap.GetLowestLevelForSystem(AnchorSystemId) > 1 &&
        !observer.ExplorationMap.CheckSystemHasUnknownItems(AnchorSystemId);
}

public static partial class Entry
{
    // GOG 1.3.8.4 Galaxy.CreateTemporaryLocation assigns a system inside this squared distance.
    internal const long SystemMembershipDistanceSquared = 1_822_500_000_000L;

    internal static bool IsPhysicalSystemLocation(Galaxy galaxy, Location? location)
    {
        if (location is not { SystemId: >= 0 } || !galaxy.Systems.ContainsKey(location.SystemId)) return false;
        if (!location.IsTemporary) return true;
        StarSystem nearest = galaxy.FindNearestSystem(location.GalaxyX, location.GalaxyY, out long distanceSquared);
        return nearest != null && nearest.SystemId == location.SystemId && distanceSquared < SystemMembershipDistanceSquared;
    }

    private static GeneratedWaypoint[] GenerateNetwork(Galaxy galaxy)
    {
        // Offline/check callers can drain the same job; the live UI advances it in bounded slices.
        var search=CreateUsefulNetwork(galaxy);
        while(!search.Complete)search.Tick();
        if(search.Error!=null)throw new InvalidOperationException(search.Error);
        return search.Result.Select(point=>new GeneratedWaypoint(point,galaxy.FindNearestSystem(point.X,point.Y,out _).SystemId)).ToArray();
    }
}
