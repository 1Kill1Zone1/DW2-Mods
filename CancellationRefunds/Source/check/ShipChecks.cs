using CancellationRefunds;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace DW2ModLauncher.Loader;
internal static partial class Program
{
    static void ShipCancellationChecks(string fixture)
    {
        var g = Load(fixture, out var saveData);
        var empire = g.Empires.GetPlayer();
        empire.Economy.EditMoney = 1000000f;
        var yard = empire.Spaceports.First(s => s.GetConstructionSystem() is not null && s.ConstructionLevel >= 1f);
        var escort = empire.Designs.First(d => !d.Obsolete && d.GetRole(g) == ShipRole.Escort && d.BuildCost > 0);
        var port = empire.Designs.First(d => !d.Obsolete && Galaxy.CheckRoleIsBase(d.GetRole(g)) &&
            !empire.DetermineShipPurchaseCostIsPrivate(d.GetRole(g)) && d.BuildCost > 0);
        var freighter = empire.Designs.First(d => !d.Obsolete && d.GetRole(g) == ShipRole.FreighterSmall && d.BuildCost > 0);

        Ship Buy(Design design, out float paid)
        {
            float before = empire.Economy.Money;
            var designs = new DesignList { design };
            var batch = empire.ExecuteTaskBuildShips(yard, designs, [1], Vector3.Zero, null!);
            Check(batch.Count == 1, "native build order enqueues " + design.Name);
            paid = before - empire.Economy.Money;
            return batch[0];
        }

        var ship = Buy(escort, out float paid);
        Check(paid > 0 && Ledger.Candidate(ship, empire)?.Payment.Paid == paid, "paid yard hull records exact state debit");
        var balance = empire.Economy.Money;
        empire.ExecuteTaskCancelShipConstruction(ship, yard);
        Near(empire.Economy.Money - balance, paid, "cancel at zero work returns the full payment");
        balance = empire.Economy.Money;
        empire.ExecuteTaskCancelShipConstruction(ship, yard);
        Near(empire.Economy.Money, balance, "repeated yard cancel credits nothing");

        var station = Buy(port, out paid);
        Check(station.IsBase && paid > 0 && Ledger.Candidate(station, empire)?.Payment.Paid == paid, "paid base order records exact state debit");
        station.SetConstructionLevel(.25f, Rng, false);
        float fraction = station.ConstructionLevel;
        Check(fraction > 0 && fraction < 1, "station is partially constructed");
        var path = Path.Combine(TestOutput.Root, "ship-refund-roundtrip-" + Guid.NewGuid().ToString("N") + ".DWGame");
        using (var writer = new BinaryWriter(File.Open(path, FileMode.CreateNew))) g.WriteToStream(writer, saveData);
        var copy = Load(path);
        var copyEmpire = copy.Empires.GetById(empire.EmpireId);
        var copyStation = copy.Ships.GetById(station.ShipId) ?? throw new InvalidOperationException("Loaded ship missing");
        Check(Ledger.Candidate(copyStation, copyEmpire)?.Payment.Paid == paid, "station payment survives native save/load");
        fraction = Math.Max(fraction, copyStation.ConstructionLevel);
        balance = copyEmpire.Economy.Money;
        copyEmpire.ExecuteTaskScrapShip(copyStation);
        Check(copyStation.IsDestroyed, "native Scuttle destroys the partially built station");
        Near(copyEmpire.Economy.Money - balance, paid * (1f - fraction), "native Scuttle after reload returns the remaining station cost");

        var privateShip = Buy(freighter, out paid);
        Check(Ledger.Candidate(privateShip, empire) == null, "private construction has no refund entry");
        float privateMoney = empire.Economy.PrivateMoney;
        balance = empire.Economy.Money;
        empire.ExecuteTaskScrapShip(privateShip);
        Near(empire.Economy.Money, balance, "cancelling private construction does not credit the treasury");
        Near(empire.Economy.PrivateMoney, privateMoney, "cancelling private construction does not change private balance");

        ship = Buy(escort, out paid);
        var second = Buy(escort, out float secondPaid);
        var group = new ShipList { ship, second };
        balance = empire.Economy.Money;
        empire.ExecuteTaskScrapShips(group);
        Near(empire.Economy.Money - balance, paid + secondPaid, "native group Scuttle credits each paid hull once");

        ship = Buy(escort, out _);
        balance = empire.Economy.Money;
        ship.EnsureDestroyShip(g, g.Time, null!, false);
        Near(empire.Economy.Money, balance, "destruction outside cancellation credits nothing");

        ship = Buy(escort, out _);
        object[] construction = [ship, g, empire, false, yard.Cargo, yard.CargoReserved, 600f, 10000f, 10000f, 1f, false];
        AccessTools.Method(typeof(ConstructionSystem), "BuildShip").Invoke(null, construction);
        Check((bool)construction[10] && ship.ConstructionLevel >= 1f, "actual native construction completes the paid ship");
        Check(Ledger.Candidate(ship, empire) == null, "completion drops the payment entry");
        balance = empire.Economy.Money;
        empire.ExecuteTaskScrapShip(ship);
        Near(empire.Economy.Money, balance, "Scuttle of a completed ship credits nothing");

        var builder = empire.Ships.First(s => s.Role == ShipRole.ConstructionShip && s.ConstructionLevel >= 1 &&
            !s.IsDestroyed && s.ConstructionSystem is not null && !s.ConstructionSystem.IsConstructing());
        var target = empire.Colonies.First(o => empire.CheckCanBuildBaseHere(o, port.GetRole(g), o.GetLocation(g), true));
        var location = target.GetLocation(g);
        builder.TransferLocation(g, location);
        builder.Parent = default;
        builder.ParentIsDockedAt = builder.ParentIsOnboard = false;
        builder.GalaxyX = target.GalaxyX;
        builder.GalaxyY = target.GalaxyY;
        builder.SetPosition(target.GetPosition(location), location);
        // Start at the work site to exercise the payment in CommandBuild without simulating travel.
        builder.Mission = new ShipMission { Type = ShipMissionType.Build, BuildDesignId = port.DesignId };
        var oldShips = g.Ships.ToHashSet(ReferenceEqualityComparer.Instance);
        balance = empire.Economy.Money;
        AccessTools.Method(typeof(Ship), "CommandBuild").Invoke(builder, [g.Time, .01f, g.StarDate, empire, location, builder.Mission, target, Point.Zero]);
        paid = balance - empire.Economy.Money;
        station = g.Ships.Single(s => s != null && s.EmpireId == empire.EmpireId && s.IsBase && !oldShips.Contains(s));
        Check(paid > 0 && Ledger.Candidate(station, empire)?.Payment.Paid == paid, "real constructor-built station records the exact state debit");
        fraction = station.ConstructionLevel;
        balance = empire.Economy.Money;
        empire.ExecuteTaskScrapShip(station);
        Near(empire.Economy.Money - balance, paid * (1f - fraction), "native Scuttle refunds a station built by a constructor");

    }
}
