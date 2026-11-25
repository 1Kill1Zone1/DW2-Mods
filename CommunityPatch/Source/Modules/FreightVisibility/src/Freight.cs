using System.Globalization;
using DistantWorlds.Types;

namespace FreightVisibility;

public sealed class Totals
{
    public int Loaded, Pickup, Transport, Refuel, Retrofit, Other;
    public double Cargo, Planned;
    public HashSet<int> Foreign = new();

    public string Lines() => string.Format(CultureInfo.InvariantCulture,
        "Freight - our freighters\nOn board: {0:N0} units / {1} ships\nGoing to load / loading: {2} ships ({3:N0} planned)\nLoaded ships: transport {4}, refuel {5}, retrofit {6}, other/idle {7}\nForeign carriers on our orders: {8}",
        Cargo, Loaded, Pickup, Planned, Transport, Refuel, Retrofit, Other, Foreign.Count);
}

public static class Freight
{
    public static Dictionary<short, Totals> Read(Empire empire)
    {
        var result = new Dictionary<short, Totals>();
        var seen = new HashSet<int>();
        foreach (var ship in empire.Ships.ToArray())
        {
            if (ship == null || ship.IsDestroyed || !seen.Add(ship.ShipId) || ship.GetEmpire() != empire ||
                ship.Role is not (ShipRole.FreighterSmall or ShipRole.FreighterMedium or ShipRole.FreighterLarge)) continue;
            var mission = ship.Mission;
            var type = mission?.Type ?? ShipMissionType.Undefined;
            var activity = type == ShipMissionType.Transport && mission!.ResolveCurrentCommand().Type == ShipCommandType.Refuel
                ? ShipMissionType.Refuel : type;
            // Cargo belongs to the hold, not to a contract or the fuel tank. Several
            // owners/contract rows can share one ship and one resource.
            var actual = Amounts(ship.Cargo);
            foreach (var (resource, amount) in actual)
            {
                var t = Get(result, resource);
                t.Loaded++;
                t.Cargo += amount;
                switch (activity)
                {
                    case ShipMissionType.Transport: t.Transport++; break;
                    case ShipMissionType.Refuel: t.Refuel++; break;
                    case ShipMissionType.Retrofit: t.Retrofit++; break;
                    default: t.Other++; break;
                }
            }
            // A remaining Load command distinguishes a pickup from the return leg.
            // A stale contract is deliberately not evidence of a current pickup.
            if (type == ShipMissionType.Transport && mission != null &&
                mission.CheckForCurrentOrFutureCommand(ShipCommandType.Load))
                foreach (var (resource, amount) in Amounts(mission.Cargo))
                {
                    var t = Get(result, resource);
                    t.Pickup++;
                    t.Planned += amount;
                }
            if (!ReferenceEquals(ship.Mission, mission))
                throw new InvalidOperationException("Freighter mission changed during sample");
        }
        foreach (var order in empire.Orders.ToArray())
        {
            if (order == null || order.EmpireId != empire.EmpireId || order.Contracts == null) continue;
            foreach (var contract in order.Contracts.ToArray())
            {
                if (contract == null || (float)contract.AmountToFulfill <= 0) continue;
                var ship = empire.Galaxy.Ships.GetById(contract.FreighterId);
                if (ship == null || ship.GetEmpire() == empire || ship.GetEmpire() == null ||
                    ship.Summary == null || !ShipSummary.IsFreighterFast(ship.Role, ship.Summary)) continue;
                var mission = ship.Mission;
                if (mission?.Type != ShipMissionType.Transport ||
                    mission.PrimaryTarget != contract.Supplier || mission.SecondaryTarget != order.Destination) continue;
                Get(result, order.ResourceId).Foreign.Add(ship.ShipId);
            }
        }
        return result;
    }

    public static Dictionary<short, double> Amounts(CargoList? cargo)
    {
        var amounts = new Dictionary<short, double>();
        if (cargo == null) return amounts;
        foreach (var item in cargo.ToArray())
        {
            if (item == null) continue;
            float amount = item.Amount;
            if (float.IsFinite(amount) && amount > 0)
                amounts[item.ResourceId] = amounts.GetValueOrDefault(item.ResourceId) + amount;
        }
        return amounts;
    }

    private static Totals Get(Dictionary<short, Totals> totals, short resource)
    {
        if (!totals.TryGetValue(resource, out var t)) totals.Add(resource, t = new Totals());
        return t;
    }
}
