using System.Reflection;
using System.Runtime.Loader;
using DistantWorlds.Core;
using DistantWorlds.Types;
using FreightDispatch;
using HarmonyLib;

namespace DW2ModLauncher.Loader;

static partial class Program
{
    static ColonyPopulationPolicy? observedColonyPolicy;
    static ColonyPopulationPolicyType expectedColonyPolicy;
    static bool throwColonyBody;
    static int colonyBodyCalls;
    static bool ColonyBodyBarrier()
    {
        colonyBodyCalls++;
        Check(observedColonyPolicy?.PolicyType == expectedColonyPolicy, "actual colony prefix exposes only the expected scoped policy to the load body");
        if (throwColonyBody) throw new InvalidOperationException("controlled colony load failure");
        return false;
    }

    static void VerifyShipLogistics(string root, string fixture)
    {
        string dll = Path.Combine(root, "mods", "3815304122", "DW2ShipLogistics.dll");
        Console.WriteLine("SHIP LOGISTICS SHA256 " + Hash(dll));
        var sl = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var colony = sl.GetType("DW2ShipLogistics.ColonyPickup", true)!;
        var fuel = sl.GetType("DW2ShipLogistics.FreighterFuel", true)!;
        var host = sl.GetType("DW2Mod.ModHost", true)!;
        var errors = new List<string>();
        AccessTools.Field(host, "ErrorSink").SetValue(null, (Action<string>)(s => { errors.Add(s); Console.WriteLine("SL ERROR " + s); }));
        AccessTools.Field(host, "OutputDirectory").SetValue(null, AppContext.BaseDirectory);
        AccessTools.Field(sl.GetType("DW2ShipLogistics.Colonizers", true)!, "galaxyRef").SetValue(null, AccessTools.FieldRefAccess<Ship, Galaxy>("_Galaxy"));
        var load = AccessTools.Method(typeof(Ship), "CommandLoad");
        var find = typeof(Empire).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "FindFreighterToFulfillContract" && m.GetParameters().Length == 10);
        var fd = new Harmony("local.dw2.freight-dispatch");
        var hooks = new Harmony("local.dw2.freight-shiplogistics-check.actual");
        void InstallSl()
        {
            hooks.Patch(load, prefix: new HarmonyMethod(colony, "BeginLoad"), finalizer: new HarmonyMethod(colony, "EndLoad"));
            AccessTools.Method(fuel, "Install").Invoke(null, [hooks, (Action<string>)Console.WriteLine]);
        }
        foreach (bool slFirst in new[] { true, false })
        {
            fd.Unpatch(load, HarmonyPatchType.Prefix, fd.Id);
            fd.Unpatch(find, HarmonyPatchType.Transpiler, fd.Id);
            hooks.UnpatchAll(hooks.Id);
            if (slFirst) InstallSl();
            fd.Patch(load, prefix: new HarmonyMethod(typeof(Native), "BeforeLoad") { priority = Priority.First });
            fd.Patch(find, transpiler: new HarmonyMethod(typeof(Native), "FilterFreighters"));
            if (!slFirst) InstallSl();
            Console.WriteLine("REGISTRATION " + (slFirst ? "Ship Logistics before FD" : "FD before Ship Logistics"));
            var prefixes = PatchProcessor.GetSortedPatchMethods(load, Harmony.GetPatchInfo(load)!.Prefixes.ToArray());
            Check(prefixes.IndexOf(AccessTools.Method(typeof(Native), "BeforeLoad")) < prefixes.IndexOf(AccessTools.Method(colony, "BeginLoad")),
                "FD Priority.First remains before colony policy scope in either registration order");
            Check(Harmony.GetPatchInfo(find)!.Postfixes.Any(p => p.PatchMethod == AccessTools.Method(fuel, "Postfix")),
                "actual Ship Logistics fuel veto coexists with the FD candidate transpiler");
            if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_SL_VETO_ONLY") != "1")
            {
                VerifyCombinedFreightLoad(fixture);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                VerifyScheduler(Load(fixture), fixture);
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            VerifyRefuelVeto(Load(fixture), sl);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            if (Environment.GetEnvironmentVariable("FREIGHT_CHECK_SL_VETO_ONLY") != "1") VerifyColonyScope(Load(fixture), load);
            Check(errors.Count == 0, "actual SL loading/fuel handlers report no internal errors: " + string.Join(";", errors));
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }
    }

    static void VerifyCombinedFreightLoad(string fixture)
    {
        var g = Load(fixture); var e = g.Empires.GetPlayer(); var source = g.Ships.GetById(936);
        var destination = e.Capital as Orb ?? throw new Exception("Capital missing");
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 2, out _), "combined native freight-load task created");
        var task = State.For(g, e.EmpireId).Data!.Tasks.Single();
        var before = destination.Colony!.PopulationPolicy.Select(p => p.PolicyType).ToArray();
        VerifyContractAndPhysicalCargo(g, e, source, destination, task);
        Check(before.SequenceEqual(destination.Colony.PopulationPolicy.Select(p => p.PolicyType)),
            "native freight pickup/cancellation/unload never changes colony population policies");
    }

    static void VerifyRefuelVeto(Galaxy g, Assembly sl)
    {
        BootstrapRouting(g);
        var e = g.Empires.GetPlayer(); var source = g.Ships.GetById(936); var destination = e.Capital!;
        var destinationSystem = destination.GetSystem(g, true);
        // The fixture's Ghost base is in deep space; SL intentionally ignores a pickup without a real system.
        var sourceOrb = g.Orbs.Where(o => o.GetSystem(g) is { } s && s.SystemId != destinationSystem.SystemId && e.CheckCanOperateInEnemySystem(s))
            .OrderBy(o => Calculations.CalculateDistance(o, destination)).First();
        source.LocationId = sourceOrb.LocationId; source.GalaxyX = sourceOrb.GalaxyX; source.GalaxyY = sourceOrb.GalaxyY;
        source.Parent = new Item(ItemType.Orb, sourceOrb.OrbId, sourceOrb);
        Check(source.GetSystem(g) != null, "refuel veto fixture has a real pickup system rather than a deep-space base");
        var candidates = e.Ships.Where(s => Native.EconomyFreighter(e, s)).ToArray();
        var ship = candidates.First(s => s.Summary.HyperDriveJumpRange > 0);
        foreach (var other in candidates)
        {
            other.CheckCancelContracts(other.Mission); other.Mission = new ShipMission();
            other.MissionAssignmentType = ShipMissionAssignmentType.Manual;
        }
        ship.MissionAssignmentType = ShipMissionAssignmentType.FullyAutomate; ship.Cargo.Clear(); ship.CargoSmuggling.Clear();
        ship.GetLocation(g)?.Ships.Remove(ship);
        ship.LocationId = source.LocationId; ship.GalaxyX = source.GalaxyX; ship.GalaxyY = source.GalaxyY;
        var pickup = source.GetLocation(g) ?? throw new Exception("Pickup location missing");
        pickup.Ships.Add(ship);
        source.Cargo.Add(new Cargo(ship.Summary.ReactorFuelResourceId, 100000, new Item<Empire>(e)));
        var system = source.GetSystem(g, true);
        e.RefuellingPointsPerSystem[system.SystemId] = new StellarObjectList { source };
        float warmed = g.SystemPathTimesCached.GetPathTime(system.SystemId, destinationSystem.SystemId, g, e, g.StarDate, e.BestJumpRange, system, destinationSystem);
        Check(float.IsFinite(warmed) && warmed < float.MaxValue, "actual empire-range scheduler lookup is warmed synchronously");
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 1, out _), "low-fuel veto task established");
        var task = State.For(g, e.EmpireId).Data!.Tasks.Single();
        var context = Activator.CreateInstance(typeof(Native).GetNestedType("Context", BindingFlags.NonPublic)!, g, e, task, source, destination);
        var guard = AccessTools.Method(typeof(Native), "SafeTwoLegRoute");
        float lower = 0, upper = ship.Summary.FuelCapacity;
        ship.Fuel = (AtomicFloat)upper;
        Check((bool)guard.Invoke(null, [context, ship])!, "refuel test route is feasible on a full native tank");
        for (int i = 0; i < 24; i++)
        {
            float middle = (lower + upper) / 2; ship.Fuel = (AtomicFloat)middle;
            if ((bool)guard.Invoke(null, [context, ship])!) upper = middle; else lower = middle;
        }
        ship.Fuel = (AtomicFloat)Math.Min(ship.Summary.FuelCapacity, upper * 1.001f + .001f);
        Check((bool)guard.Invoke(null, [context, ship])!, "current tank passes FD routing before SL's extra reserve check");
        var routeType = sl.GetType("DW2ShipLogistics.FuelRoute", true)!;
        var point = AccessTools.Method(routeType, "PointIn").Invoke(null, [g, e, ship, system, source]);
        Check(point != null, "real SL fuel-point validation accepts the owned supplied pickup base");
        Console.WriteLine($"VETO-RANGE now0={ship.CurrentFuelRange(0)} now10={ship.CurrentFuelRange(.1f)} path={warmed} active={e.Active}");
        var sends = AccessTools.Field(sl.GetType("DW2ShipLogistics.FreighterFuel", true)!, "sends").GetValue(null)!;
        sends.GetType().GetMethod("Clear")!.Invoke(sends, null);
        var fuelBefore = ship.Fuel;
        Tick(g, e);
        Console.WriteLine($"REFUEL-VETO fuel={fuelBefore} mission={ship.Mission?.Type} voyages={task.Voyages.Count} status={task.Status}");
        Check(ship.Mission?.Type == ShipMissionType.Refuel, "actual SL postfix sends the selected native candidate to refuel");
        Check(task.Voyages.Count == 0 && task.Phase == DispatchPhase.Active && State.For(g, e.EmpireId).Fault == null,
            "FD respects SL's null result without a phantom voyage or fault");
        Check(!e.Orders.Any(o => task.Orders.Contains(o.OrderId) && o.Contracts.Count > 0), "refuel veto creates no dispatch contract");
    }

    static void VerifyColonyScope(Galaxy g, MethodInfo load)
    {
        var e = g.Empires.GetPlayer(); var orb = e.Capital as Orb ?? throw new Exception("Capital missing");
        var ship = e.Ships.First(s => Native.EconomyFreighter(e, s));
        var policy = orb.Colony!.PopulationPolicy.First();
        var savedPolicy = policy.PolicyType;
        ship.Role = ShipRole.ColonyShip;
        ship.Parent = new Item(ItemType.Orb, orb.OrbId, orb); ship.ParentIsDockedAt = true;
        ship.Mission = new ShipMission { Type = ShipMissionType.LoadPopulation, BuildDesignId = policy.RaceId };
        observedColonyPolicy = policy;
        var body = new Harmony("local.dw2.freight-shiplogistics-check.colony-body");
        body.Patch(load, prefix: new HarmonyMethod(typeof(Program), nameof(ColonyBodyBarrier)) { priority = Priority.Last });
        try
        {
            foreach (bool fail in new[] { false, true })
            {
                policy.PolicyType = ColonyPopulationPolicyType.Enslave;
                expectedColonyPolicy = ColonyPopulationPolicyType.Assimilate; throwColonyBody = fail;
                colonyBodyCalls = 0;
                bool caught = false;
                try { load.Invoke(ship, [g.Time, .1f, orb.GetLocation(g), new CargoList(), ship.Mission, e, false]); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException { Message: "controlled colony load failure" }) { caught = true; }
                Check(colonyBodyCalls == 1 && caught == fail, "FD allows ordinary colony loading and preserves the controlled body exception");
                Check(policy.PolicyType == ColonyPopulationPolicyType.Enslave, "actual SL finalizer restores Enslave after normal and exceptional load paths");
            }
            throwColonyBody = false;
            policy.PolicyType = ColonyPopulationPolicyType.Exterminate; expectedColonyPolicy = ColonyPopulationPolicyType.Exterminate;
            load.Invoke(ship, [g.Time, .1f, orb.GetLocation(g), new CargoList(), ship.Mission, e, false]);
            Check(policy.PolicyType == ColonyPopulationPolicyType.Exterminate, "neither handler converts Exterminate policy");
            ship.Role = ShipRole.FreighterSmall;
            policy.PolicyType = ColonyPopulationPolicyType.Enslave; expectedColonyPolicy = ColonyPopulationPolicyType.Enslave;
            load.Invoke(ship, [g.Time, .1f, orb.GetLocation(g), new CargoList(), ship.Mission, e, false]);
            Check(policy.PolicyType == ColonyPopulationPolicyType.Enslave, "SL colony scope does not alter a freight hull even with a population mission");
        }
        finally
        {
            body.UnpatchAll(body.Id); policy.PolicyType = savedPolicy; observedColonyPolicy = null; throwColonyBody = false;
        }
    }
}
