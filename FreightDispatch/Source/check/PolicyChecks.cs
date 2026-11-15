using System.Reflection;
using DistantWorlds.Core;
using DistantWorlds.Types;
using FreightDispatch;
using HarmonyLib;

namespace DW2ModLauncher.Loader;
static partial class Program
{
    static void VerifyPolicy(Galaxy g, Empire e, Ship source, Orb destination, DispatchTask task)
    {
        var requestedStock = new ResourceQuantityList(); requestedStock.AddAmount(7, 100); requestedStock.AddAmount(0, 200);
        object?[] stockArgs = [e, source.ShipId, ItemType.Ship, requestedStock];
        AccessTools.Method(typeof(Native), "BeforeStockOrders").Invoke(null, stockArgs);
        var filteredStock = (ResourceQuantityList)stockArgs[3]!;
        Check(filteredStock.GetAmount(7) == 0 && filteredStock.GetAmount(0) == 200 && requestedStock.GetAmount(7) == 100, "native stock-order argument filter suppresses selected export only and preserves original request plus unselected fuel");
        var order = new Order(e.Orders.GetNextId(), 7, 1000, g.StarDate, e, new Item(ItemType.Ship, source.ShipId, source));
        var ship = e.Ships.First(s => Native.EconomyFreighter(e, s));
        var committed = new Contract(order.OrderId, 0, new Item<Empire>(e), 100, new Item<Ship>(ship), new Item(ItemType.Orb, destination.OrbId, destination));
        order.Contracts.Add(committed); e.Orders.Add(order);
        Native.RetireReverseDemand(g, e, task);
        Check(order.AmountOutstanding == 0 && order.Contracts.Contains(committed) && (float)committed.AmountToFulfill == 100, "reverse open demand retired; committed inbound contract preserved");
        var denied = g.InitiateContract(g.StarDate, e, null!, order, destination, ship, 100, 0, 0, out _, 0, true);
        Check(denied == null, "new reverse contract blocked during export");
        e.Orders.Remove(order);
        Check(Dispatch.SetPriority(g, e, source, true, out _), "ordinary source priority stored");
        var ordinary = new Order(e.Orders.GetNextId(), 7, 100, g.StarDate, e, new Item(ItemType.Orb, destination.OrbId, destination));
        var sources = new SortableStellarObjectList { new(destination, 10), new(source, 200) };
        object?[] args = [g, e, ordinary, sources]; AccessTools.Method(typeof(Native), "OrderSources").Invoke(null, args);
        var ordered = (SortableStellarObjectList)args[3]!;
        Check(ordered[0].StellarObject == source && ordered[0].SortTag == 200 && ordered[1].SortTag == 10, "High prioritizes eligible source without modifying native path values");
        Dispatch.SetPriority(g, e, source, false, out _);
        args = [g, e, ordinary, sources]; AccessTools.Method(typeof(Native), "OrderSources").Invoke(null, args);
        Check(((SortableStellarObjectList)args[3]!)[0].StellarObject == destination, "Normal preserves native source order");
        var owner = source.Empire; source.Empire = new Item<Empire>(g.GetIndependentEmpire());
        try { Check(!Native.ValidSource(g, e, source), "ownership loss invalidates task pickup source"); }
        finally { source.Empire = owner; }
        VerifyForeignScope(g, e, source, task);
    }
    static void VerifyForeignScope(Galaxy g, Empire player, Ship source, DispatchTask task)
    {
        var foreign = g.Empires.First(e => e.EmpireId > 0 && e != player && !e.IsPirate && e.Ships.Any(s => Native.EconomyFreighter(e, s)));
        var ship = foreign.Ships.First(s => Native.EconomyFreighter(foreign, s));
        var priorOwner = source.Empire; var priorCargo = source.Cargo; var priorReserved = source.CargoReserved; var priorStock = source.StockLevels;
        var order = new Order(foreign.Orders.GetNextId(), 7, 100, g.StarDate, foreign, new Item(ItemType.Ship, source.ShipId, source));
        foreign.Orders.Add(order); task.Orders.Add(order.OrderId);
        try
        {
            source.Empire = new Item<Empire>(foreign); source.Cargo = new CargoList(priorCargo.IsIndexed, priorCargo.RollupAmounts); source.CargoReserved = new CargoList(priorReserved.IsIndexed, priorReserved.RollupAmounts); source.StockLevels = new ResourceQuantityList();
            source.Cargo.Add(new Cargo(7, 1000, new Item<Empire>(foreign)));
            var contract = g.InitiateContract(g.StarDate, foreign, null!, order, source, ship, 100, 0, 0, out _, 0, true);
            Check(contract != null, "captured source's new owner contracts unaffected despite colliding OrderId");
            if (contract != null) g.CancelContract(contract);
            source.Empire = priorOwner; source.Cargo = new CargoList(priorCargo.IsIndexed, priorCargo.RollupAmounts); source.Cargo.Add(new Cargo(7, 300, new Item<Empire>(player)));
            var blocked = g.InitiateContract(g.StarDate, foreign, null!, order, source, ship, 100, 0, 0, out _, float.MaxValue, true);
            Check(blocked == null && source.Cargo.GetAmount(player.EmpireId, 7) == 300, "foreign buying cannot consume manual retained300 at still-owned source");
        }
        finally
        {
            source.Empire = priorOwner; source.Cargo = priorCargo; source.CargoReserved = priorReserved; source.StockLevels = priorStock;
            task.Orders.Remove(order.OrderId); foreign.Orders.Remove(order);
        }
    }
    static void VerifyReserveCeiling(Galaxy g, Empire e, Ship source, Orb destination, Ship ship)
    {
        source.Cargo = new CargoList(true, true); source.Cargo.Add(new Cargo(7, 1800, new Item<Empire>(e)));
        source.CargoReserved = new CargoList(true, true); source.CargoReserved.Add(new Cargo(7, 500, new Item<Empire>(e)));
        source.StockLevels = new ResourceQuantityList();
        var orders = new List<Order>(); float committed = 0;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var order = new Order(e.Orders.GetNextId(), 7, 10000, g.StarDate, e, new Item(ItemType.Orb, destination.OrbId, destination));
                orders.Add(order); e.Orders.Add(order);
                var contract = g.InitiateContract(g.StarDate, e, null!, order, source, ship, 10000, 0, 0, out _, 0, true);
                if (contract != null) committed += contract.AmountToFulfill;
            }
            Check(Math.Abs(committed - 1500) < .01 && Math.Abs(source.Cargo.GetAmount(e.EmpireId, 7) - 300) < .01 && Math.Abs(source.CargoReserved.GetAmount(e.EmpireId, 7) - 2000) < .01, "total2300 with pre-reserved500 and retain300 permits exactly1500 across capacity-limited native contracts");
        }
        finally { foreach (var order in orders) { foreach (var contract in order.Contracts.ToArray()) g.CancelContract(contract); e.Orders.Remove(order); } }
        Check(Math.Abs(source.Cargo.GetAmount(e.EmpireId, 7) - 1800) < .01 && Math.Abs(source.CargoReserved.GetAmount(e.EmpireId, 7) - 500) < .01, "zero-pickup native cancellations restore free1800 and preserve unrelated reserved500");
    }
    static void Tick(Galaxy g, Empire e)
    {
        // Invoke the real native scheduler; its installed FuelFirst prefix runs before dispatch.
        Empire.FulfillOrders(g, e, null!, g.StarDate, 0, e.Orders, 1, 0, [], new EmpireList(), [], new OrbList(), ulong.MaxValue);
    }
    static void BootstrapRouting(Galaxy g)
    {
        // ReadFromStream omits the native post-load terrain/distance caches used by pathfinding.
        g.DetermineSystemsOutsideNebulae();
        g.SystemsWithNebulaEffects = g.DetermineSystemsWithNebulaEffects();
        g.CalculateSystemDistances();
        PathFindingSystem.CalculateSystemDistances(g);
        g.SystemPathTimesCached.Clear(g);
    }
    static void VerifyScheduler(Galaxy g, string fixture)
    {
        BootstrapRouting(g);
        var e = g.Empires.GetPlayer(); var source = g.Ships.GetById(936);
        var destination = e.Capital ?? throw new Exception("Capital missing");
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 1, out _), "scheduler scenario started at Ghost->capital limit1");
        var session = State.For(g, e.EmpireId); var task = session.Data!.Tasks.Single();
        var freighters = e.Ships.Where(s => Native.EconomyFreighter(e, s) && s.Summary.CargoCapacity > 0).Take(3).ToArray();
        Check(freighters.Length == 3, "scheduler fixture has three actual civil hulls");
        var pickupLocation = source.GetLocation(g) ?? throw new Exception("Pickup has no native location");
        foreach (var ship in freighters)
        {
            ship.CheckCancelContracts(ship.Mission); ship.Mission = new ShipMission(); ship.MissionAssignmentType = ShipMissionAssignmentType.FullyAutomate;
            ship.Cargo.Clear(); ship.CargoSmuggling.Clear();
            var old = ship.GetLocation(g); old?.Ships.Remove(ship);
            ship.LocationId = source.LocationId; ship.GalaxyX = source.GalaxyX; ship.GalaxyY = source.GalaxyY;
            pickupLocation.Ships.Add(ship);
        }
        // Keep actual tank quantities; the guard must support the real approved route.
        freighters[0].Cargo.Add(new Cargo(7, 5, new Item<Empire>(e)));
        var pickupSystem = source.GetSystem(g, true); var destinationSystem = destination.GetSystem(g, true);
        Console.WriteLine($"ROUTE source-system={pickupSystem?.SystemId} destination-system={destinationSystem?.SystemId} best-jump={e.BestJumpRange} allow-tasks={Galaxy.AllowNewTasks}");
        if (pickupSystem != null && destinationSystem != null)
        {
            var delayed = AccessTools.Method(g.SystemPathTimesCached.GetType(), "GetPathTimeDelayed");
            Console.WriteLine("ROUTE delayed-time=" + delayed.Invoke(g.SystemPathTimesCached, [pickupSystem.SystemId, destinationSystem.SystemId, g, e, g.StarDate, e.BestJumpRange, pickupSystem, destinationSystem]));
            float warmed = g.SystemPathTimesCached.GetPathTime(pickupSystem.SystemId, destinationSystem.SystemId, g, e, g.StarDate, e.BestJumpRange, pickupSystem, destinationSystem);
            Console.WriteLine("ROUTE warmed-time=" + warmed);
            var path = PathFindingSystem.FindPath(g, g.PathFindingExcludedSystemsNone, pickupSystem, destinationSystem, e.BestJumpRange, out float nativeDistance, out float nativeTime, Stride.Core.Mathematics.Point.Zero, Stride.Core.Mathematics.Point.Zero);
            Console.WriteLine($"ROUTE sourceXY={source.GalaxyX},{source.GalaxyY} destinationXY={destination.GalaxyX},{destination.GalaxyY} direct={Calculations.CalculateDistance(source, destination)} path-count={path.Count} distance={nativeDistance} time={nativeTime}");
            Check(float.IsFinite(warmed) && warmed < float.MaxValue, "native synchronous cache bootstrap resolves Ghost->capital route without enabling background tasks");
        }
        Tick(g, e);
        Check(session.Fault == null, "scheduler does not latch integration error: " + session.Fault);
        Console.WriteLine("SCHEDULER status=" + task.Status);
        foreach (var s in freighters) Console.WriteLine($"SHIP {s.ShipId} fuel={(float)s.Fuel:0.##} currentRange={s.CurrentFuelRange(0):0} role={s.Role} mission={s.Mission?.Type}");
        Check(task.Voyages.Count == 1, "real scheduler allocates one voyage for approved Ghost->capital route");
        Check(task.Voyages[0].ShipId != freighters[0].ShipId, "loaded candidate skipped; another eligible own freighter selected");
        int orderId = task.Voyages[0].OrderId;
        Tick(g, e);
        Check(task.Voyages.Count == 1 && task.Voyages[0].OrderId == orderId && Dispatch.Assigned(g, e, [task]).Count == 1, "repeated scheduler pass cannot exceed limit or duplicate contract");
        var allocatedShip = g.Ships.GetById(task.Voyages[0].ShipId);
        Check(allocatedShip.Mission.Type == ShipMissionType.Transport && allocatedShip.Cargo.TotalAmount == 0, "scheduler uses physical native transport mission, no immediate delivery");
        VerifyFuelCostGuard(g, e, task, source, destination, allocatedShip);
        VerifyActiveRoundTrip(g, e, task, fixture);
        // ReadFromStream for the roundtrip changes native global routing tables; restore this fixture's tables.
        BootstrapRouting(g);
        _ = g.SystemPathTimesCached.GetPathTime(source.GetSystemId(g), destination.GetSystemId(g), g, e, g.StarDate, e.BestJumpRange, source.GetSystem(g, true), destination.GetSystem(g, true));
        Check(Dispatch.Cancel(g, e, task.Id, out _), "unloaded voyage cancellation accepted");
        Check(task.Voyages.Count == 0 && (allocatedShip.Mission == null || allocatedShip.Mission.Type == ShipMissionType.Undefined), "unloaded cancelled freighter released and reservation cancelled natively");
        Tick(g, e); Check(task.Phase == DispatchPhase.Cancelled, "cancellation reaches terminal state without pickups");
        foreach (var free in freighters.Skip(1)) { free.CheckCancelContracts(free.Mission); free.Mission = new ShipMission(); free.MissionAssignmentType = ShipMissionAssignmentType.FullyAutomate; }
        var destinationColony = ((Orb)destination).Colony!;
        float held = destinationColony.Cargo.GetAmount(e.EmpireId, 7);
        float capacity = destinationColony.GetCargoCapacities(false)[7];
        float inbound = e.Orders.Where(o => o.ResourceId == 7 && o.DestinationType == ItemType.Orb && o.DestinationId == ((Orb)destination).OrbId).Sum(o => o.Contracts.Sum(c => Math.Max(0, (float)c.AmountToFulfill - (float)c.AmountDelivered)));
        Console.WriteLine($"LIMIT2-ACCEPTANCE before capacity={capacity} held={held} inbound={inbound}");
        // This scenario models a destination that has consumed its Silicon; retain native capacity and inbound commitments.
        destinationColony.Cargo.CheckReduceCargo(e.EmpireId, 7, held);
        float room = (float)AccessTools.Method(typeof(Native), "DestinationRoom").Invoke(null, [g, e, destination, (short)7])!;
        Console.WriteLine($"LIMIT2-ACCEPTANCE after held={destinationColony.Cargo.GetAmount(e.EmpireId, 7)} native-room={room}");
        Check(room > freighters.Skip(1).Max(s => s.Summary.CargoCapacity), "limit2 fixture has native acceptance for a full first load and a positive partial second load without changing capacity policy");
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 2, out _), "real scheduler limit2 task started");
        var two = session.Data.Tasks.Last(); Tick(g, e);
        Console.WriteLine($"LIMIT2 status={two.Status} fault={session.Fault} voyages={two.Voyages.Count} sourceFree={source.Cargo.GetAmount(e.EmpireId, 7)}");
        foreach (var ship in freighters) Console.WriteLine($"LIMIT2-SHIP {ship.ShipId} cargo={ship.Cargo.TotalAmount} mission={ship.Mission?.Type} mode={ship.MissionAssignmentType} fuel={(float)ship.Fuel} range={ship.CurrentFuelRange(0)} jump={ship.Summary.HyperDriveJumpRange}");
        var twoIds = two.Voyages.Select(v => v.ShipId).ToHashSet(); var twoOrders = two.Voyages.Select(v => v.OrderId).ToHashSet();
        Check(twoIds.Count == 2 && two.Voyages.Count == 2 && twoOrders.Count == 2 && !twoIds.Contains(freighters[0].ShipId), "real scheduler allocates two distinct empty own ships and contracts at limit2");
        var amounts = two.Voyages.Select(v => (float)e.Orders.GetByOrderId(v.OrderId).Contracts.GetByContractId(v.ContractId).AmountToFulfill).ToArray();
        Check(amounts.All(a => a > 0) && amounts.Sum() <= room + .01 && amounts.Any(a => a < freighters.Skip(1).Min(s => s.Summary.CargoCapacity)), "limit2 uses a final partial contract within actual destination acceptance");
        Tick(g, e); Tick(g, e);
        Check(two.Voyages.Count == 2 && two.Voyages.All(v => twoIds.Contains(v.ShipId) && twoOrders.Contains(v.OrderId)) && Dispatch.Overview(g, e, two) == Dispatch.FleetShare(2, e.Ships.Count(s => Native.EconomyFreighter(e, s)), 2), "limit2 retries preserve two identities and actual allocation counter without overshoot");
        Check(Dispatch.Cancel(g, e, two.Id, out _) && two.Voyages.Count == 0 && twoIds.All(id => g.Ships.GetById(id).Mission?.Type is null or ShipMissionType.Undefined), "limit2 zero-pickup cancellation releases both ships and native reservations");
        Tick(g, e);
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 1, out _), "new task allowed after previous ships released");
        var waiting = session.Data.Tasks.Last();
        var own = e.Ships.Where(s => Native.EconomyFreighter(e, s)).ToArray();
        var modes = own.Select(s => s.MissionAssignmentType).ToArray();
        try
        {
            foreach (var s in own) s.MissionAssignmentType = ShipMissionAssignmentType.Manual;
            Tick(g, e);
            Check(waiting.Voyages.Count == 0 && waiting.Phase == DispatchPhase.Active && waiting.Status.Contains("Waiting", StringComparison.Ordinal), "no eligible free ship waits without creating phantom allocations");
            var colony = ((Orb)destination).Colony!; var originalCargo = colony.Cargo;
            try
            {
                colony.Cargo = new CargoList(originalCargo.IsIndexed, originalCargo.RollupAmounts); colony.Cargo.Add(new Cargo(7, 1_000_000_000, new Item<Empire>(e)));
                Tick(g, e);
                Check(waiting.Voyages.Count == 0 && waiting.Status.Contains("destination", StringComparison.Ordinal), "full destination waits without reserving more cargo");
            }
            finally { colony.Cargo = originalCargo; }
            var owner = source.Empire;
            try
            {
                source.Empire = new Item<Empire>(g.GetIndependentEmpire()); Tick(g, e);
                Check(waiting.Voyages.Count == 0 && waiting.Status.StartsWith("Suspended", StringComparison.Ordinal), "source ownership loss suspends actual scheduler pickups");
            }
            finally { source.Empire = owner; }
        }
        finally { for (int i = 0; i < own.Length; i++) own[i].MissionAssignmentType = modes[i]; }
        Dispatch.Cancel(g, e, waiting.Id, out _);
        Tick(g, e);
        VerifyPersistenceFailure(g, e, source, destination, freighters.Skip(1).ToArray(), fixture);
    }
    static void VerifyFuelCostGuard(Galaxy g, Empire e, DispatchTask task, Ship source, StellarObject destination, Ship ship)
    {
        var context = Activator.CreateInstance(typeof(Native).GetNestedType("Context", BindingFlags.NonPublic)!, g, e, task, source, destination);
        var guard = AccessTools.Method(typeof(Native), "SafeTwoLegRoute");
        Check((bool)guard.Invoke(null, [context, ship])!, "allocated real ship passes combined native route cost on its current tank");
        var method = AccessTools.Method(typeof(Empire), "FindBestJumpPathToDestination", [typeof(StellarObject), typeof(StellarObject), typeof(float), typeof(float), typeof(float).MakeByRefType(), typeof(float).MakeByRefType()]);
        var costFault = new Harmony("local.dw2.freight-dispatch-cost-check");
        try
        {
            costFault.Patch(method, postfix: new HarmonyMethod(typeof(Program), nameof(InflatePathCost)));
            Check(!(bool)guard.Invoke(null, [context, ship])!, "injected high native path-time cost rejects fuel-short route even when geometric distance fits");
        }
        finally { costFault.UnpatchAll(costFault.Id); }
        try
        {
            costFault.Patch(method, postfix: new HarmonyMethod(typeof(Program), nameof(EmptyPath)));
            Check(!(bool)guard.Invoke(null, [context, ship])!, "empty native cross-system path rejects allocation despite sufficient geometric fuel range");
        }
        finally { costFault.UnpatchAll(costFault.Id); }
    }
    static void InflatePathCost(ref float __5) => __5 = float.MaxValue;
    static void EmptyPath(ref List<int> __result) => __result = [];
    static void VerifyActiveRoundTrip(Galaxy g, Empire e, DispatchTask task, string fixture)
    {
        using var reader = new BinaryReader(File.OpenRead(fixture));
        _ = new Galaxy().ReadFromStream(reader, out var metadata, out var errors);
        Check(errors.Count == 0, "active roundtrip metadata loaded from isolated fixture");
        var voyage = task.Voyages.Single();
        var output = Path.Combine(AppContext.BaseDirectory, "freight-dispatch-active-" + Guid.NewGuid().ToString("N") + ".DWGame");
        using (var writer = new BinaryWriter(File.Open(output, FileMode.CreateNew))) g.WriteToStream(writer, metadata);
        var restored = Load(output); var player = restored.Empires.GetPlayer(); var data = State.For(restored, player.EmpireId).Data!;
        var restoredTask = data.Tasks.Single(); var restoredVoyage = restoredTask.Voyages.Single();
        Check(restoredVoyage.OrderId == voyage.OrderId && restoredVoyage.ContractId == voyage.ContractId && restoredVoyage.ShipId == voyage.ShipId && Dispatch.Assigned(restored, player, [restoredTask]).Count == 1, "native save/load preserves active contract identity and allocated ship");
        Tick(restored, player); Tick(restored, player);
        Check(restoredTask.Voyages.Count == 1 && restoredTask.Voyages[0].OrderId == voyage.OrderId && restoredTask.Voyages[0].ContractId == voyage.ContractId && Dispatch.Assigned(restored, player, [restoredTask]).Count == 1, "reload retries retain original contract and enforce quota");
        Console.WriteLine("Retained active-contract roundtrip artifact: " + output);
    }
}
