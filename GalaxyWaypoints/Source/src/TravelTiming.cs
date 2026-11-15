using DistantWorlds.Types;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private sealed record TravelTiming(double PoweredRange, double Speed, double Preparation)
    {
        public double Seconds(double cost, int hops) =>
            RouteChoice.TravelCost(cost, PoweredRange) / Speed + hops * Preparation;

        public static TravelTiming For(Ship ship)
        {
            var summary = ship.Summary;
            double speed = Positive(summary.HyperDriveSpeed);
            if (ship.Fleet is { JumpSpeed: > 0 } fleet &&
                (ship.Role != ShipRole.FuelTanker || ship.MissionAssignmentType == ShipMissionAssignmentType.Manual))
                speed = Math.Min(speed, fleet.JumpSpeed);
            speed = Math.Max(1, speed);
            double energyPerSecond = Math.Max(0, summary.HyperDriveEnergyUsage + summary.StaticEnergyConsumption);
            double fuelPerSecond = energyPerSecond * Positive(ship.FuelUnitPerEnergyUnit()) + 0.000001;
            double powered = Positive(ship.Fuel) / fuelPerSecond * speed;
            // Estimate the remaining stored-energy buffer; do not assume a tanker will arrive en route.
            if (energyPerSecond > 0)
                powered += Positive(ship.Energy) / energyPerSecond * speed;
            return new(powered, speed, Positive(summary.HyperDriveJumpInitiationTime));
        }
    }
}
