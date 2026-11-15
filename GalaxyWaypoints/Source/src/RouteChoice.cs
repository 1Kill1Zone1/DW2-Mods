namespace GalaxyWaypoints;

internal readonly record struct Waypoint(int X, int Y);

internal static class RouteChoice
{
    public static double Distance(Waypoint start, Waypoint end)
    {
        double x = (double)end.X - start.X;
        double y = (double)end.Y - start.Y;
        return Math.Sqrt(x * x + y * y);
    }

    public static bool CanJump(double distance, double minimum, double range) =>
        distance >= minimum && distance <= range;

    public static bool Improves(double travelCost, double fuelRange, double jumpCost, double bestCost) =>
        double.IsFinite(travelCost) && travelCost > 0 &&
        TravelCost(travelCost, fuelRange) + 2 * jumpCost < bestCost;

    // DW2 uses one-third hyperdrive speed when both fuel and stored energy are exhausted.
    public static double TravelCost(double cost, double poweredRange) =>
        cost + 2 * Math.Max(0, cost - Math.Max(0, double.IsNaN(poweredRange) ? 0 : poweredRange));
}
