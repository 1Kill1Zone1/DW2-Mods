using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DistantWorlds.Types;
using FreightDispatch;
using HarmonyLib;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

[assembly: SupportedOSPlatform("windows7.0")]

namespace DW2ModLauncher.Loader;
public static class LiveChannels
{
    public static readonly Dictionary<string, Delegate> Channels = [];
    public static Delegate? Get(string consumer, string channel, int contract, Type expected) => Channels.TryGetValue(channel, out var value) && expected.IsInstanceOfType(value) ? value : null;
}
public static class GameState
{
    public static int Generation => Environment.GetEnvironmentVariable("FREIGHT_CHECK_WARCHEST") == "1" ? 1 : 0;
}
static partial class Program
{
    static int checks;
    static readonly Random random = new(12);
    static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); checks++; Console.WriteLine("PASS " + detail); }
    static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Check <GOG game-root> <isolated copied campaign>");
        AssemblyLoadContext.Default.Resolving += (_, n) => { var p = Path.Combine(args[0], n.Name + ".dll"); return File.Exists(p) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(p) : null; };
        Run(args);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Run(string[] args)
    {
        string original = Hash(args[1]);
        RuntimeHelpers.RunModuleConstructor(typeof(Galaxy).Module.ModuleHandle);
        var first = Load(args[1]); var firstPlayer = first.Empires.GetPlayer();
        Check(!State.Ready(first, firstPlayer, out _) && State.Error(first, firstPlayer) != null, "missing Matrix prevents session-only dispatch");
        bool missingRejected = false; try { Entry.Init(); } catch (InvalidOperationException) { missingRejected = true; }
        Check(missingRejected && !Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)!.Owners.Contains("local.dw2.freight-dispatch")), "missing Matrix installs no economic hooks");
        BindMatrix(args[0]);
        var h = new Harmony("local.dw2.freight-dispatch-check");
        h.Patch(AccessTools.PropertyGetter(typeof(Galaxy), nameof(Galaxy.Rnd)), prefix: new HarmonyMethod(typeof(Program), nameof(RandomPrefix)));
        var fl = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(args[0], "mods", "3813818446", "DW2FreighterLogistics.dll"));
        foreach (string name in new[] { "FuelFirst", "TopUp" })
            fl.GetType("DW2FreighterLogistics." + name)!.GetMethod("Install", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [h, (Action<string>)(s => Console.WriteLine("FL " + s))]);
        foreach (string folder in new[] { "BattleLog", "WeaponFX", "GalaxyWaypoints" })
        {
            string path = Path.Combine(args[0], "mods", folder);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "dw2modlauncher.json")));
            string dll = manifest.RootElement.GetProperty("injection").GetProperty("dll").GetString()!;
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(path, dll));
        }
        Entry.Init();
        WriteHookMetadata();
        if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_SHIP_COLORS") == "1")
        {
            VerifyShipColors(args[0], args[1]);
            Check(Hash(args[1]) == original, "Shipset Colors copied source fixture hash unchanged");
            Console.WriteLine($"PASS {checks} Freight Dispatch / actual Shipset Colors save checks; no game or real save writes");
            return;
        }
        if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_SHIP_LOGISTICS") == "1")
        {
            VerifyShipLogistics(args[0], args[1]);
            Check(Hash(args[1]) == original, "Ship Logistics copied source fixture hash unchanged");
            Console.WriteLine($"PASS {checks} Freight Dispatch / actual Ship Logistics checks; no game or real save writes");
            return;
        }
        if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_WARCHEST") == "1")
        {
            VerifyWarChest(args[0], args[1]);
            Check(Hash(args[1]) == original, "War Chest copied source fixture hash unchanged");
            Console.WriteLine($"PASS {checks} Freight Dispatch / actual War Chest contract checks; no game or real save writes");
            return;
        }
        if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_OVERVIEW_ONLY") == "1")
        {
            VerifyFreightOverview(Load(args[1]));
            VerifyUi(Load(args[1]));
            Check(Hash(args[1]) == original, "overview copied source fixture hash unchanged");
            Console.WriteLine($"PASS {checks} Freight Dispatch overview/UI checks; no rendering/live travel claim");
            return;
        }
        if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_UI_ONLY") == "1")
        {
            VerifyUi(Load(args[1]));
            Check(Hash(args[1]) == original, "UI-only copied source fixture hash unchanged");
            Console.WriteLine($"PASS {checks} Freight Dispatch UI-only checks; no native scheduler rerun or rendering claim");
            return;
        }
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Ship), "CommandLoad"))!.Prefixes.Count >= 2, "real installed TopUp and Freight Dispatch loading hooks coexist");
        var g = Load(args[1]); var e = g.Empires.GetPlayer();
        Check(State.Ready(g, e, out var session), "real Matrix native storage ready");
        var source = g.Ships.GetById(936); var destination = e.Capital as Orb ?? throw new Exception("Fixture capital is not an orbital colony");
        Console.WriteLine($"NATIVE-HOLDS free indexed={source.Cargo.IsIndexed} rollup={source.Cargo.RollupAmounts}; reserved indexed={source.CargoReserved.IsIndexed} rollup={source.CargoReserved.RollupAmounts}");
        foreach (var constructor in typeof(CargoList).GetConstructors()) Console.WriteLine("CARGO-CONSTRUCTOR " + string.Join(",", constructor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)));
        Check(Native.ValidSource(g, e, source) && Native.ValidDestination(g, e, destination), "fixture owned Ghost Fleet Base and capital are valid endpoints");
        Check(!Dispatch.Start(g, e, source, source, [new DispatchResource { ResourceId = 7 }], 2, out _), "same endpoint rejected");
        Check(!Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = float.NaN }], 2, out _), "nonfinite reserve rejected");
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 2, out _), "dispatch task persisted");
        Check(!Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 2, out _), "repeated start does not duplicate source task");
        var task = session.Data!.Tasks.Single();
        Check(Dispatch.FleetShare(3, 35, 5) == "Assigned 3 / 35 (8.6%), limit 5", "actual allocation percentage independent of requested quota");
        VerifyFleet(g, e);
        VerifyPolicy(g, e, source, destination, task);
        VerifyContractAndPhysicalCargo(g, e, source, destination, task);
        VerifyState(g, e, session);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        VerifyScheduler(Load(args[1]), args[1]);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        VerifyRejectedCommandRecovery(args[1]);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        VerifyUi(Load(args[1]));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        VerifyFreightOverview(Load(args[1]));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        RoundTrip(args[1]);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        Check(Hash(args[1]) == original, "copied source fixture hash unchanged");
        Console.WriteLine($"PASS {checks} Freight Dispatch checks; no live travel/UI claim");
    }
    static bool RandomPrefix(ref Random __result) { __result = random; return false; }
    static Galaxy Load(string path)
    {
        var host = (Stride.Engine.Game)RuntimeHelpers.GetUninitializedObject(typeof(Stride.Engine.Game));
        typeof(Stride.Games.GameBase).GetField("<Content>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, new ContentManager(new DatabaseFileProviderService(null)));
        Galaxy.Game = host; Galaxy.AllowNewTasks = false; Galaxy.LoadSensitiveRenderingPaused = true;
        using var r = new BinaryReader(File.OpenRead(path)); var g = new Galaxy().ReadFromStream(r, out _, out var errors);
        Check(errors.Count == 0, "native copied fixture load");
        g.SystemPathTimesCached.Clear(g); // Native post-load allocation of per-system cache slots.
        foreach (var ship in g.Ships)
        {
            if (ship == null) continue;
            ship.UpdateBonuses(false); ship.RegenerateSummary();
            if (ship.Summary == null) continue;
            // Native ReDefine's derived cache fields; do not alter the fixture's fuel or hardware.
            AccessTools.Field(typeof(Ship), "SummaryTopSpeed").SetValue(ship, ship.Summary.TopSpeed);
            AccessTools.Field(typeof(Ship), "SummaryHyperDriveJumpRange").SetValue(ship, ship.Summary.HyperDriveJumpRange);
            AccessTools.Field(typeof(Ship), "SummaryHyperDriveSpeed").SetValue(ship, ship.Summary.HyperDriveSpeed);
            if (ship.GetDesign() is { } design) AccessTools.Field(typeof(Ship), "Tactics").SetValue(ship, design.Tactics);
        }
        return g;
    }
    static void BindMatrix(string root)
    {
        var matrix = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "mods", "3833406787", "DW2PositronicMatrix.dll")).GetType("DW2PositronicMatrix.SavedData", true)!;
        LiveChannels.Channels["saved-data.get"] = matrix.GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<object, string, string, string>>();
        LiveChannels.Channels["saved-data.set"] = matrix.GetMethod("Set", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Action<object, string, string, string>>();
    }
    static void VerifyFleet(Galaxy g, Empire e)
    {
        var ships = e.Ships.Where(s => Native.EconomyFreighter(e, s)).ToArray();
        var roleInventory = e.Ships.Where(s => s.EmpireId == e.EmpireId && s.Role is ShipRole.FreighterSmall or ShipRole.FreighterMedium or ShipRole.FreighterLarge).ToArray();
        foreach (var s in roleInventory) Console.WriteLine($"FLEET {s.ShipId} {s.Role} destroyed={s.IsDestroyed} noSummary={s.Summary == null} construction={s.ConstructionLevel} hull={s.HullConstructionLevel}/{s.Summary?.HullSize} unbuilt={s.UnbuiltComponentCount} damaged={s.DamagedComponentCount} mission={s.Mission?.Type} refit={s.RetrofitConstructionSystem != null}");
        int independent = roleInventory.Count(s => !s.IsDestroyed && s.Summary != null && (s.HullConstructionLevel >= s.Summary.HullSize && s.UnbuiltComponentCount == 0 || s.RetrofitConstructionSystem != null || s.Mission?.Type == ShipMissionType.Retrofit));
        Console.WriteLine($"FLEET role-total={roleInventory.Length} independent-completed-or-refit={independent} dispatch-total={ships.Length}");
        Check(independent > 0 && ships.Length == independent, "fleet total agrees with independent hull/component completion inventory");
        var ship = ships[0]; byte damaged = ship.DamagedComponentCount; float hull = ship.HullConstructionLevel; var mission = ship.Mission;
        try
        {
            ship.DamagedComponentCount = 1;
            Check(Native.EconomyFreighter(e, ship), "repair damage does not remove completed ship from fleet total");
            ship.HullConstructionLevel = 0;
            ship.Mission = new ShipMission { Type = ShipMissionType.Undefined };
            Check(!Native.EconomyFreighter(e, ship), "unfinished hull excluded from fleet total");
            ship.Mission.Type = ShipMissionType.Retrofit;
            Check(Native.EconomyFreighter(e, ship), "retrofit remains in total but is not declared available");
            var owner = ship.Empire; ship.Empire = new Item<Empire>(g.GetIndependentEmpire());
            Check(!Native.EconomyFreighter(e, ship), "foreign freighter excluded"); ship.Empire = owner;
            var role = ship.Role; ship.Role = ShipRole.TroopTransport;
            Check(!Native.EconomyFreighter(e, ship), "military troop transport excluded"); ship.Role = role;
        }
        finally { ship.DamagedComponentCount = damaged; ship.HullConstructionLevel = hull; ship.Mission = mission; }
    }
    static void VerifyContractAndPhysicalCargo(Galaxy g, Empire e, Ship source, Orb destination, DispatchTask task)
    {
        var ship = e.Ships.First(s => Native.EconomyFreighter(e, s) && s.CargoCapacity > 0);
        ship.CheckCancelContracts(ship.Mission); ship.Mission = new ShipMission(); ship.Cargo.Clear(); ship.CargoSmuggling.Clear();
        VerifyReserveCeiling(g, e, source, destination, ship);
        source.Cargo = new CargoList(true, true); source.Cargo.Add(new Cargo(7, 2300, new Item<Empire>(e)));
        source.CargoReserved = new CargoList(true, true); source.CargoReserved.Add(new Cargo(7, 500, new Item<Empire>(e)));
        source.StockLevels = new ResourceQuantityList(); source.StockLevels.AddAmount(7, 4000);
        var order = new Order(e.Orders.GetNextId(), 7, 0, g.StarDate, e, new Item(ItemType.Orb, destination.OrbId, destination));
        e.Orders.Add(order); task.Orders.Add(order.OrderId);
        var contextType = typeof(Native).GetNestedType("Context", BindingFlags.NonPublic)!;
        var context = Activator.CreateInstance(contextType, g, e, task, source, destination);
        var active = AccessTools.Field(typeof(Native), "active"); active.SetValue(null, context);
        Contract contract;
        try { contract = g.InitiateContract(g.StarDate, e, null!, order, source, ship, 2000, 0, 0, out _, 0, true); }
        finally { active.SetValue(null, null); }
        Check(contract != null, "native contract accepts manual300 despite automatic4000");
        if (contract == null) throw new Exception("No native contract");
        float amount = contract.AmountToFulfill;
        Console.WriteLine($"RESERVATION player={e.EmpireId} order-owner={order.EmpireId} amount={amount} free-get={source.Cargo.GetAmount(e.EmpireId, 7)} reserved-get={source.CargoReserved.GetAmount(e.EmpireId, 7)} free-total={source.Cargo.TotalAmount} reserved-total={source.CargoReserved.TotalAmount}");
        foreach (var c in source.Cargo) Console.WriteLine($"FREE resource={c.ResourceId} owner={c.EmpireId} amount={(float)c.Amount}");
        foreach (var c in source.CargoReserved) Console.WriteLine($"RESERVED resource={c.ResourceId} owner={c.EmpireId} amount={(float)c.Amount}");
        Check(Math.Abs(amount - Math.Min(2000, ship.Summary.CargoCapacity)) < .01, "native capacity bound; existing500 reservation not subtracted twice");
        float reserved = source.CargoReserved.Where(c => c.EmpireId == e.EmpireId && c.ResourceId == 7).Sum(c => (float)c.Amount);
        Check(Math.Abs(source.Cargo.GetAmount(e.EmpireId, 7) - (2300 - amount)) < .01 && Math.Abs(reserved - (500 + amount)) < .01, "native reservation moves exact free cargo into reserved pool across separate reservation rows");
        Check(source.StockLevels.GetAmount(7) == 4000, "manual override does not edit persistent stock target");
        source.StockLevels = new ResourceQuantityList();
        var ordinary = new Order(e.Orders.GetNextId(), 7, 10000, g.StarDate, e, new Item(ItemType.Orb, destination.OrbId, destination)); e.Orders.Add(ordinary);
        var extra = g.InitiateContract(g.StarDate, e, null!, ordinary, source, ship, 10000, 0, 0, out _, 0, true);
        Check(source.Cargo.GetAmount(e.EmpireId, 7) >= 300 - .01, "ordinary native contracts also respect manual retain300 when automatic target0");
        if (extra != null) g.CancelContract(extra); e.Orders.Remove(ordinary);
        source.StockLevels.AddAmount(7, 4000);
        task.Voyages.Add(new DispatchVoyage { OrderId = order.OrderId, ContractId = contract.ContractId, ShipId = ship.ShipId, ResourceId = 7 });
        var missionCargo = new CargoList(); missionCargo.Add(new Cargo(7, amount, new Item<Empire>(e)));
        var stubs = new ContractStubList { new ContractStub(order.OrderId, contract.ContractId, e.EmpireId) };
        // Isolate native physical loading/unloading at existing docks. This does not simulate travel.
        ship.Mission = new ShipMission(g, ShipMissionType.Transport, source, destination, null!, -1, -1, -1, -1, missionCargo, null!, stubs, null!, false);
        ship.Mission.CommandProgress = 2; // Native transport Load stage, after pickup docking.
        ship.Parent = new Item(ItemType.Ship, source.ShipId, source); ship.ParentIsDockedAt = true;
        ship.DockData ??= (DockData)RuntimeHelpers.GetUninitializedObject(typeof(DockData));
        var load = AccessTools.Method(typeof(Ship), "CommandLoad");
        float partialTime = amount / (4 * source.DockingSystem.Throughput * Math.Max(1, Constants.DockingThroughputWhenExternalFactor));
        Check(float.IsFinite(partialTime) && partialTime > 0, "fixture has positive native docking throughput");
        load.Invoke(ship, [g.Time, partialTime, source.GetLocation(g), missionCargo, ship.Mission, e, false]);
        Check(ship.Cargo.GetAmount(e.EmpireId, 7) > 0 && (float)contract.AmountPickedUp > 0, "real native CommandLoad transfers cargo and updates picked-up contract");
        float picked = contract.AmountPickedUp;
        Check(picked > 0 && picked < amount, "native load stops at a genuine partial pickup before cancellation");
        float afterLoadReserved = source.CargoReserved.Where(c => c.EmpireId == e.EmpireId && c.ResourceId == 7).Sum(c => (float)c.Amount);
        Console.WriteLine($"PARTIAL amount={amount} picked={picked} free-total={source.Cargo.TotalAmount} reserved-total={afterLoadReserved} ship-total={ship.Cargo.TotalAmount} ship-get={ship.Cargo.GetAmount(e.EmpireId, 7)} expected-reserved={500 + amount - picked}");
        foreach (var c in source.CargoReserved) Console.WriteLine($"PARTIAL-RESERVED resource={c.ResourceId} owner={c.EmpireId} amount={(float)c.Amount}");
        Check(Math.Abs(afterLoadReserved - (500 + amount - picked)) < .01 && Math.Abs(ship.Cargo.GetAmount(e.EmpireId, 7) - picked) < .01, "native physical loading conserves exact reserved and ship cargo amounts");
        Check(source.Cargo.GetAmount(e.EmpireId, 7) >= 300 - .01 && source.CargoReserved.Where(c => c.EmpireId == e.EmpireId && c.ResourceId == 7).Sum(c => (float)c.Amount) >= 500 - .01, "TopUp cannot consume manual reserve or pre-existing500 reservation");
        var allocated = Dispatch.Assigned(g, e, [task, task]);
        Check(allocated.Count == 1, "same ship deduplicated across repeated task enumeration");
        Check(Dispatch.Cancel(g, e, task.Id, out _), "cancel transitions loaded voyage without teleportation");
        Check(Math.Abs((float)contract.AmountToFulfill - picked) < .01 && Math.Abs(source.CargoReserved.Where(c => c.EmpireId == e.EmpireId && c.ResourceId == 7).Sum(c => (float)c.Amount) - 500) < .01, "partial cancellation releases only unpicked balance and preserves unrelated reservation500");
        Check(Math.Abs(source.Cargo.Where(c => c.EmpireId == e.EmpireId && c.ResourceId == 7).Sum(c => (float)c.Amount) + source.CargoReserved.Where(c => c.EmpireId == e.EmpireId && c.ResourceId == 7).Sum(c => (float)c.Amount) + ship.Cargo.GetAmount(e.EmpireId, 7) - 2800) < .01, "loaded cancellation conserves source free, reserved, and aboard cargo");
        Check(Dispatch.Assigned(g, e, [task]).Contains(ship.ShipId), "cancelled loaded delivery still counts as allocated");
        load.Invoke(ship, [g.Time, 10000f, source.GetLocation(g), missionCargo, ship.Mission, e, false]);
        Check(Math.Abs(ship.Cargo.GetAmount(e.EmpireId, 7) - picked) < .01 && Math.Abs((float)contract.AmountPickedUp - picked) < .01, "cancelled partial voyage cannot pick up additional cargo");
        float before = destination.Colony!.Cargo.GetAmount(e.EmpireId, 7);
        ship.Parent = new Item(ItemType.Orb, destination.OrbId, destination); ship.ParentIsDockedAt = true;
        ship.Mission.CommandProgress = 7; // Native transport Unload stage, after destination docking.
        var unload = AccessTools.Method(typeof(Ship), "CommandUnload");
        unload.Invoke(ship, [g.Time, 10000f, destination.GetLocation(g), ship.Mission, e, g.StarDate, false]);
        Check(destination.Colony.Cargo.GetAmount(e.EmpireId, 7) > before, "real native CommandUnload delivers to destination hold");
        Check(Math.Abs(destination.Colony.Cargo.GetAmount(e.EmpireId, 7) - before - picked) < .01 && ship.Cargo.GetAmount(e.EmpireId, 7) < .01, "native final delivery transfers exactly the picked-up cargo");
        Native.Reconcile(g, e, task);
        Check(task.Resources.Single().Delivered > 0, "ledger observes actual native delivered amount");
        ship.Mission.CompleteMission(e, ship);
        Native.Reconcile(g, e, task);
        Check(Dispatch.Assigned(g, e, [task]).Count == 0, "loaded cancellation releases allocation after native delivery and mission completion");
    }
    static void VerifyState(Galaxy g, Empire e, State.Session session)
    {
        State.Persist(g, session); string key = SavedData.ObserverPrefix(e.EmpireId);
        string json = SavedData.Read(g, key)!;
        JsonSerializer.Deserialize<DispatchState>(json)!.Validate(e.EmpireId);
        Check(json.Contains("\"SourceId\":936", StringComparison.Ordinal), "Matrix preserves source and contract identities");
        var invalid = JsonSerializer.Deserialize<DispatchState>(json)!; invalid.Tasks[0].Resources[0].Retain = float.NaN;
        bool rejected = false; try { invalid.Validate(e.EmpireId); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "corrupt reserve rejected before state mutation");
        Check(!State.For(g, (short)(e.EmpireId + 100)).Data!.Tasks.Any(), "separate observer does not inherit tasks");
    }
    static void RoundTrip(string fixture)
    {
        using var reader = new BinaryReader(File.OpenRead(fixture)); var g = new Galaxy().ReadFromStream(reader, out var metadata, out var errors);
        Check(errors.Count == 0, "roundtrip independent fixture loaded"); var e = g.Empires.GetPlayer();
        Check(Dispatch.Start(g, e, g.Ships.GetById(936), e.Capital ?? throw new Exception("Capital missing"), [new DispatchResource { ResourceId = 7, Retain = 300 }], 5, out _), "roundtrip task started");
        var output = Path.Combine(AppContext.BaseDirectory, "freight-dispatch-check-" + Guid.NewGuid().ToString("N") + ".DWGame");
        using (var writer = new BinaryWriter(File.Open(output, FileMode.CreateNew))) g.WriteToStream(writer, metadata);
        var reloaded = Load(output); var restored = State.For(reloaded, e.EmpireId).Data!;
        Check(restored.Tasks.Single().Limit == 5 && restored.Tasks.Single().Resources.Single().Retain == 300, "real native save/load preserves task settings");
        var isolated = Load(fixture);
        string prefix = SavedData.ObserverPrefix(e.EmpireId);
        Check(SavedData.Read(isolated, prefix) == null, "separate campaign has no task leakage");
        SavedData.Write(isolated, "{", prefix);
        var isolatedPlayer = isolated.Empires.GetPlayer();
        Check(!State.Ready(isolated, isolatedPlayer, out _), "corrupt Matrix task payload fails closed");
        Check(!Dispatch.Start(isolated, isolatedPlayer, isolated.Ships.GetById(936), isolatedPlayer.Capital!, [new DispatchResource { ResourceId = 7 }], 1, out _), "corrupt saved state rejects new task mutation");
        State.BeforeSave(isolated);
        Check(SavedData.Read(isolated, prefix) == "{", "save barrier preserves corrupt payload for recovery instead of overwriting it");
        Console.WriteLine("Retained isolated roundtrip artifact: " + output);
    }
}
