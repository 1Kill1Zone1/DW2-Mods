using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DistantWorlds.Types;
using FreightDispatch;
using HarmonyLib;

namespace DW2ModLauncher.Loader;

static partial class Program
{
    static float seenTradeFunds;
    static bool seenOriginalContract;
    static void ObserveContract(float availableForeignResourceTradingFunds, bool __runOriginal)
    { seenTradeFunds = availableForeignResourceTradingFunds; seenOriginalContract = __runOriginal; }

    static void VerifyWarChest(string gameRoot, string fixture)
    {
        var dll = Path.Combine(gameRoot, "mods", "3824731625", "DW2WarChest.dll");
        Console.WriteLine("WAR CHEST SHA256 " + Hash(dll));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var budget = assembly.GetType("DW2WarChest.Budget", true)!;
        var shipBudget = assembly.GetType("DW2WarChest.ShipBudget", true)!;
        var expenses = assembly.GetType("DW2WarChest.ExpenseLog", true)!;
        var ledger = assembly.GetType("DW2WarChest.Ledger", true)!;
        var host = assembly.GetType("DW2Mod.ModHost", true)!;
        var errors = new List<string>();
        AccessTools.Field(host, "ErrorSink").SetValue(null, (Action<string>)(s => { errors.Add(s); Console.WriteLine("WAR CHEST ERROR " + s); }));
        AccessTools.Field(host, "OutputDirectory").SetValue(null, AppContext.BaseDirectory);
        AccessTools.Field(budget, "Enabled").SetValue(null, true);
        AccessTools.Field(expenses, "PlayerEnabled").SetValue(null, true);
        AccessTools.Field(expenses, "AiEnabled").SetValue(null, true);
        var contract = typeof(Galaxy).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "InitiateContract" && m.GetParameters().Length == 20);
        var fd = new Harmony("local.dw2.freight-dispatch");
        var wc = new Harmony("local.dw2.freight-warchest-check.actual");
        var trace = new Harmony("local.dw2.freight-warchest-check.trace");
        var fdPrefix = AccessTools.Method(typeof(Native), "BeforeContract");
        var wcPrefix = AccessTools.Method(shipBudget, "ContractPrefix");
        void PatchContract()
        {
            wc.Patch(contract, prefix: new HarmonyMethod(wcPrefix));
            wc.Patch(contract, prefix: new HarmonyMethod(expenses, "ContractPrefix"), finalizer: new HarmonyMethod(expenses, "NoteFinalizer"));
        }
        wc.Patch(AccessTools.Method(typeof(Galaxy), "PayForResources"),
            prefix: new HarmonyMethod(expenses, "PayForResourcesPrefix"), postfix: new HarmonyMethod(expenses, "PayForResourcesPostfix"));
        wc.Patch(AccessTools.Method(typeof(Economy), "Expense"),
            prefix: new HarmonyMethod(expenses, "ExpensePrefix"), postfix: new HarmonyMethod(expenses, "ExpensePostfix"));
        wc.Patch(AccessTools.Method(typeof(Economy), "Income"), prefix: new HarmonyMethod(expenses, "IncomePrefix"));
        wc.Patch(AccessTools.Method(typeof(Galaxy), "CancelContract", [typeof(Contract)]),
            prefix: new HarmonyMethod(expenses, "CancelContractPrefix"), finalizer: new HarmonyMethod(expenses, "NoteFinalizer"));

        foreach (bool warFirst in new[] { true, false })
        {
          var baseline = new Dictionary<string, string>();
          foreach (bool withFd in new[] { false, true })
          {
            fd.Unpatch(contract, HarmonyPatchType.Prefix, fd.Id);
            wc.Unpatch(contract, HarmonyPatchType.All, wc.Id);
            trace.Unpatch(contract, HarmonyPatchType.All, trace.Id);
            if (warFirst) PatchContract();
            if (withFd) fd.Patch(contract, prefix: new HarmonyMethod(fdPrefix));
            if (!warFirst) PatchContract();
            trace.Patch(contract, prefix: new HarmonyMethod(typeof(Program), nameof(ObserveContract)) { priority = Priority.Last });
            Console.WriteLine("ORDER " + (warFirst ? "War Chest then Freight Dispatch" : "Freight Dispatch then War Chest") + " FD-contract=" + withFd);
            var prefixes = PatchProcessor.GetSortedPatchMethods(contract, Harmony.GetPatchInfo(contract)!.Prefixes.ToArray());
            if (withFd) Check(prefixes.IndexOf(fdPrefix) >= 0 && prefixes.IndexOf(wcPrefix) >= 0 &&
                (prefixes.IndexOf(wcPrefix) < prefixes.IndexOf(fdPrefix)) == warFirst, "actual contract prefixes execute in the requested registration order");
            foreach (string scenario in new[] { "own", "foreign", "small-budget", "floor-budget", "zero-budget", "rejected-task", "reverse", "retain-bound" })
            {
                if (!withFd && scenario is "rejected-task" or "reverse" or "retain-bound") continue;
                var g = Load(fixture); var player = g.Empires.GetPlayer(); var source = g.Ships.GetById(936);
                var destination = player.Capital as Orb ?? throw new Exception("Capital missing");
                var buyer = scenario is "own" or "rejected-task" or "reverse" or "retain-bound" ? player :
                    g.Empires.First(e => e.EmpireId > 0 && e != player && !e.IsPirate && e.Ships.Any(s => Native.EconomyFreighter(e, s)));
                var ship = buyer.Ships.First(s => Native.EconomyFreighter(buyer, s) && s.Summary.CargoCapacity > 0);
                ship.CheckCancelContracts(ship.Mission); ship.Mission = new ShipMission(); ship.Cargo.Clear(); ship.CargoSmuggling.Clear();
                buyer.Economy.EditMoney = scenario == "zero-budget" ? 0 : scenario == "floor-budget" ? 350 : 1_000_000;
                // ReadFromStream omits the live post-load economic cache refresh.
                buyer.CalculateCashflowActual(0, false);
                buyer.CalculateAnnualIncomePrivate(0, false);
                buyer.CalculateCashflowPrivate(0, false);
                PrepareBudgetSnapshot(budget, buyer);
                float freeStock = scenario == "retain-bound" ? 800 : 1800;
                source.Cargo = new CargoList(true, true); source.Cargo.Add(new Cargo(7, freeStock, new Item<Empire>(player)));
                source.CargoReserved = new CargoList(true, true); source.CargoReserved.Add(new Cargo(7, 500, new Item<Empire>(player)));
                source.StockLevels = new ResourceQuantityList();
                player.ResourcePricesSell = new ResourceQuantityList(); player.ResourcePricesSell.AddAmount(7, 10);
                buyer.ResourcePricesBuy = new ResourceQuantityList(); buyer.ResourcePricesBuy.AddAmount(7, 10);
                Check(Dispatch.Start(g, player, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 2, out _), "isolated dispatch reserve established: " + scenario);
                var task = State.For(g, player.EmpireId).Data!.Tasks.Single();
                var order = new Order(buyer.Orders.GetNextId(), 7, 10000, g.StarDate, buyer,
                    scenario == "reverse" ? new Item(ItemType.Ship, source.ShipId, source) : new Item(ItemType.Orb, destination.OrbId, destination));
                buyer.Orders.Add(order);
                if (scenario == "rejected-task") task.Orders.Add(order.OrderId);
                var ledgers = AccessTools.Field(ledger, "ledgers").GetValue(null)!;
                ledgers.GetType().GetMethod("Clear")!.Invoke(ledgers, null);
                AccessTools.Method(expenses, "NoteFinalizer").Invoke(null, null);
                foreach (var empire in new[] { player, buyer }.Distinct()) AccessTools.Method(expenses, "OwnerPrefix").Invoke(null, [empire]);
                float incomingFunds = scenario == "small-budget" ? 20 : float.MaxValue;
                float beforePublic = buyer.Economy.Money, beforePrivate = buyer.Economy.PrivateMoney;
                var seller = scenario == "reverse" ? (StellarObject)destination : source;
                seenTradeFunds = float.NaN; seenOriginalContract = true;
                var result = g.InitiateContract(g.StarDate, buyer, null!, order, seller, ship, 10000, 0, 0, out float cost, incomingFunds, true);
                float debit = beforePublic - buyer.Economy.Money + beforePrivate - buyer.Economy.PrivateMoney;
                Console.WriteLine($"TRADE {scenario} original={seenOriginalContract} funds={seenTradeFunds:R} cost={cost:R} debit={debit:R} units={result?.AmountToFulfill}");
                Check(errors.Count == 0, "actual budget evaluation has no internal error: " + scenario + string.Join(";", errors));
                if (scenario is "rejected-task" or "reverse" or "zero-budget")
                {
                    Check(result == null && Math.Abs(debit) < .01f, "rejected/under-budget trade has no contract or charge: " + scenario);
                    Check(source.Cargo.GetAmount(player.EmpireId, 7) == 1800 && source.CargoReserved.GetAmount(player.EmpireId, 7) == 500,
                        "rejected trade preserves free and reserved cargo: " + scenario);
                    Check(TradeTally(ledger, buyer, g.StarDate).Count == 0, "rejected trade creates no War Chest trade entries: " + scenario);
                }
                else
                {
                    Check(result != null, "native positive contract exists with both mods: " + scenario);
                    float units = result!.AmountToFulfill;
                    Check(units > 0 && units <= 1500 && units <= ship.Summary.CargoCapacity && source.Cargo.GetAmount(player.EmpireId, 7) >= 300,
                        "native cargo capacity and FD retained300 are respected: " + scenario);
                    if (scenario is "own" or "retain-bound") Check(cost == 0 && debit == 0, "own-stock transfer remains free under War Chest");
                    else
                    {
                        Check(float.IsFinite(seenTradeFunds) && seenTradeFunds <= incomingFunds && cost > 0 && units * 10 <= seenTradeFunds + .01f,
                            "real War Chest budget limits the native base-price allowance: " + scenario);
                        Check(Math.Abs(cost - debit) < .02f, "native payment is charged exactly once: " + scenario);
                        var tally = TradeTally(ledger, buyer, g.StarDate);
                        Check(tally.Count == 1 && Math.Abs(tally.Units - units) < .01f && Math.Abs(tally.Amount - cost) < .02f,
                            "actual War Chest ledger records one exact purchase: " + scenario);
                    }
                    g.CancelContract(result);
                    Console.WriteLine($"CANCEL {scenario} contract-owner={result.EmpireId} buyer={buyer.EmpireId} seller={player.EmpireId} free={source.Cargo.GetAmount(player.EmpireId, 7)} reserved={source.CargoReserved.GetAmount(player.EmpireId, 7)} treasury={buyer.Economy.Money} private={buyer.Economy.PrivateMoney}");
                    foreach (var cargo in source.Cargo) Console.WriteLine($"CANCEL-FREE owner={cargo.EmpireId} amount={(float)cargo.Amount} resource={cargo.ResourceId}");
                    foreach (var cargo in source.CargoReserved) Console.WriteLine($"CANCEL-RESERVED owner={cargo.EmpireId} amount={(float)cargo.Amount} resource={cargo.ResourceId}");
                    Check(Math.Abs(source.Cargo.TotalAmount - freeStock) < .01f && Math.Abs(source.CargoReserved.TotalAmount - 500) < .01f,
                        "native cancellation conserves cargo and unrelated reservation: " + scenario);
                    if (scenario is not ("own" or "retain-bound"))
                    {
                        var tally = TradeTally(ledger, buyer, g.StarDate);
                        float netDebit = beforePublic - buyer.Economy.Money + beforePrivate - buyer.Economy.PrivateMoney;
                        Check(tally.Count == 2 && Math.Abs(tally.Units) < .01f && Math.Abs(tally.Amount - netDebit) < .02f,
                            "War Chest ledger matches the actual native refund and retained fees: " + scenario);
                    }
                    if (scenario == "retain-bound") Check(units == 500, "FD retain300 binds the native700-capacity contract to500 with War Chest active");
                }
                Check(AccessTools.Field(expenses, "tradeResourceId").GetValue(null) is short id ? id == -1 : Convert.ToInt32(AccessTools.Field(expenses, "tradeResourceId").GetValue(null)) == -1,
                    "War Chest finalizer clears trade context: " + scenario);
                Check(AccessTools.Field(expenses, "tradeNote").GetValue(null) == null, "War Chest finalizer clears the resource note: " + scenario);
                Check(errors.Count == 0, "real War Chest budget/logging reports no internal failure: " + scenario + string.Join(";", errors));
                if (scenario is not ("rejected-task" or "reverse" or "retain-bound"))
                {
                    var tally = TradeTally(ledger, buyer, g.StarDate);
                    string outcome = JsonSerializer.Serialize(new { units = result == null ? 0 : (float)result.AmountToFulfill, cost,
                        publicMoney = buyer.Economy.Money, privateMoney = buyer.Economy.PrivateMoney,
                        freeOwn = source.Cargo.GetAmount(player.EmpireId, 7), freeBuyer = source.Cargo.GetAmount(buyer.EmpireId, 7),
                        reserved = source.CargoReserved.TotalAmount, count = tally.Count, amount = tally.Amount, tallyUnits = tally.Units, seenTradeFunds });
                    if (!withFd) baseline[scenario] = outcome;
                    else Check(baseline[scenario] == outcome, "FD preserves native/War Chest purchase, cancellation, fees, cargo ownership and ledger: " + scenario);
                }
            }
          }
        }
    }

    static void PrepareBudgetSnapshot(Type budget, Empire empire)
    {
        // Controlled post-refresh input for contract integration; budget settling is not this test's subject.
        AccessTools.Method(budget, "Settled").Invoke(null, [empire]);
        var states = AccessTools.Field(budget, "states").GetValue(null)!;
        var state = states.GetType().GetProperty("Item")!.GetValue(states, [empire.EmpireId])!;
        foreach (var (name, value) in new (string, object)[]
        {
            ("Owner", empire), ("Generation", GameState.Generation), ("ReadAt", Environment.TickCount64 + 60_000),
            ("HasData", true), ("Settled", true), ("Income", 2400f), ("Flow", 1200f),
            ("Spending", 1200f), ("Floor", 300f), ("Months", 3f), ("PrivateFloor", 300f),
            ("PrivateFlow", 1200f), ("PrivateSpending", 1200f), ("War", false)
        }) AccessTools.Field(state.GetType(), name).SetValue(state, value);
    }

    static (int Count, float Units, float Amount) TradeTally(Type ledger, Empire buyer, long date)
    {
        var period = AccessTools.Method(ledger, "Snapshot").Invoke(null, [buyer.EmpireId, date]);
        if (period == null) return default;
        int count = 0; float units = 0, amount = 0;
        foreach (DictionaryEntry entry in (IDictionary)AccessTools.Field(period.GetType(), "Trade").GetValue(period)!)
        {
            if ((bool)AccessTools.Property(entry.Key.GetType(), "Bought").GetValue(entry.Key)! != true) continue;
            var tally = entry.Value!;
            count += (int)AccessTools.Field(tally.GetType(), "Count").GetValue(tally)!;
            units += (float)AccessTools.Field(tally.GetType(), "Units").GetValue(tally)!;
            amount += (float)AccessTools.Field(tally.GetType(), "Amount").GetValue(tally)!;
        }
        return (count, units, amount);
    }
}
