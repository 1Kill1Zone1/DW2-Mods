using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DistantWorlds.Types;
using DistantWorlds.UI;
using FreightDispatch;
using HarmonyLib;
using UiChecks;

namespace DW2ModLauncher.Loader;
static partial class Program
{
    static Ship? overviewRaceShip;
    static void ChangeOverviewCommand(ShipMission __instance)
    {
        if (ReferenceEquals(overviewRaceShip?.Mission, __instance)) __instance.CommandProgress = __instance.CommandProgress == 2 ? (byte)3 : (byte)2;
    }
    // Independent inventory captures the economic fields the reader must leave unchanged.
    static string EconomicFingerprint(Empire e) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Ships = e.Ships.Select(s => new { s.ShipId, Fuel = (float)s.Fuel, s.MissionAssignmentType,
            Cargo = s.Cargo.Select(c => new { c.ResourceId, c.EmpireId, Amount = (float)c.Amount }).ToArray(),
            Reserved = s.CargoReserved?.Select(c => new { c.ResourceId, c.EmpireId, Amount = (float)c.Amount }).ToArray(),
            Smuggling = s.CargoSmuggling?.Select(c => new { c.ResourceId, c.EmpireId, Amount = (float)c.Amount }).ToArray(),
            Mission = s.Mission == null ? null : new { s.Mission.Type, s.Mission.CommandProgress, Primary = s.Mission.PrimaryTarget.Id, Secondary = s.Mission.SecondaryTarget.Id },
            Next = s.NextMissions?.Select(m => new { m.Type, m.CommandProgress, Primary = m.PrimaryTarget.Id, Secondary = m.SecondaryTarget.Id }).ToArray() }).ToArray(),
        Orders = e.Orders.Select(o => new { o.OrderId, o.ResourceId, o.Amount, o.DestinationId,
            Contracts = o.Contracts.Select(c => new { c.ContractId, c.FreighterId, c.SupplierId, Total = (float)c.AmountToFulfill, Picked = (float)c.AmountPickedUp, Delivered = (float)c.AmountDelivered }).ToArray() }).ToArray()
    }))));

    static void VerifyFreightOverview(Galaxy g)
    {
        var e = g.Empires.GetPlayer(); var session = State.For(g, e.EmpireId);
        string before = EconomicFingerprint(e), stateBefore = JsonSerializer.Serialize(session.Data);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = FreightOverview.Read(g, e);
        Console.WriteLine($"OVERVIEW-SAMPLE ships={snapshot.Ships.Length} elapsedMs={watch.Elapsed.TotalMilliseconds:0.###}; isolated fixture, not live frame time");
        Check(snapshot.Ships.Length == e.Ships.Count(s => !s.IsDestroyed && s.EmpireId == e.EmpireId && s.Role is ShipRole.FreighterSmall or ShipRole.FreighterMedium or ShipRole.FreighterLarge), "overview independently counts all own freight hulls including construction");
        Check(snapshot.Ships.Length > e.Ships.Count(s => Native.EconomyFreighter(e, s)), "construction stays visible without changing the completed-fleet denominator");
        Check(snapshot.Ships.All(s => s.Error == null), "historical native freight missions and queues can all be sampled");
        foreach (var row in snapshot.Ships)
        {
            var ship = g.Ships.GetById(row.Id);
            var independent = ship.Cargo.GroupBy(c => c.ResourceId).ToDictionary(c => c.Key, c => c.Sum(c => (double)(float)c.Amount));
            Check(Math.Abs(row.Cargo.Sum(c => c.Amount) - independent.Values.Sum()) < .001, "overview actual hold agrees with independent ship inventory #" + row.Id);
        }
        _ = FreightOverview.Read(g, e);
        Check(EconomicFingerprint(e) == before && JsonSerializer.Serialize(session.Data) == stateBefore, "repeated overview reads leave native missions, queues, cargo, contracts and FD state unchanged");

        var source = g.Ships.GetById(936); var destination = e.Capital!;
        var test = e.Ships.First(s => Native.EconomyFreighter(e, s) && s.Summary.CargoCapacity >= 400);
        test.CheckCancelContracts(test.Mission); test.NextMissions.Clear();
        source.Cargo = new CargoList(); source.Cargo.Add(new Cargo(7, 1000, new Item<Empire>(e))); source.Cargo.Add(new Cargo(8, 1000, new Item<Empire>(e)));
        source.StockLevels = new ResourceQuantityList();
        var cargo = new CargoList(); var stubs = new ContractStubList(); var orders = new List<Order>();
        foreach (short id in new short[] { 7, 8 })
        {
            var order = new Order(e.Orders.GetNextId(), id, 400, g.StarDate, e, new Item(ItemType.Orb, ((Orb)destination).OrbId, destination));
            e.Orders.Add(order); orders.Add(order);
            var contract = g.InitiateContract(g.StarDate, e, null!, order, source, test, 400, 0, 0, out _, 0, true);
            Check(contract != null, "overview fixture obtains real native contract for resource " + id);
            stubs.Add(new ContractStub(order.OrderId, contract!.ContractId, e.EmpireId));
            cargo.Add(new Cargo(id, contract.AmountToFulfill, new Item<Empire>(e)));
        }
        test.Cargo = new CargoList(); test.Cargo.Add(new Cargo(7, 200, new Item<Empire>(e))); test.Cargo.Add(new Cargo(7, 100, new Item<Empire>(e)));
        test.CargoSmuggling.Clear(); test.CargoSmuggling.Add(new Cargo(9, 25, new Item<Empire>(e)));
        test.Cargo.Add(new Cargo(8, 100, new Item<Empire>(e)));
        test.Mission = new ShipMission(g, ShipMissionType.Transport, source, destination, null!, -1, -1, -1, -1, cargo, null!, stubs, null!, false) { CommandProgress = 2 };
        var queued = new ShipMission { Type = ShipMissionType.Refuel, CommandProgress = 3, PrimaryTarget = new Item(ItemType.Ship, source.ShipId, source), PrimaryTargetType = ShipMissionTargetType.Ship };
        test.NextMissions.Add(queued);
        test.NextMissions.Add(new ShipMission { Type = ShipMissionType.Repair, PrimaryTarget = new Item(ItemType.Ship, source.ShipId, source), PrimaryTargetType = ShipMissionTargetType.Ship });
        test.RevertMission = new ShipMission { Type = ShipMissionType.Move, PrimaryTargetType = ShipMissionTargetType.GalaxyCoordinates, PrimaryGalaxyX = 123, PrimaryGalaxyY = 456 };
        var task = new DispatchTask { Id = session.Data!.NextTaskId++, SourceId = source.ShipId, DestinationType = ItemType.Orb,
            DestinationId = ((Orb)destination).OrbId, Phase = DispatchPhase.Cancelling, Status = "Stopping new pickups", Resources = [new() { ResourceId = 7 }] };
        task.Orders.Add(orders[0].OrderId);
        task.Voyages.Add(new() { ShipId = test.ShipId, OrderId = orders[0].OrderId, ContractId = stubs[0].ContractId, ResourceId = 7 });
        session.Data.Tasks.Add(task);
        FreighterInfo Row() => FreightOverview.Read(g, e).Ships.Single(s => s.Id == test.ShipId);
        var mixed = Row();
        Check(mixed.Error == null && mixed.Cargo.Length == 2 && mixed.Cargo.Single(c => c.Id == 7).Amount == 300 && mixed.Cargo.Single(c => c.Id == 8).Amount == 100, "mixed hold aggregates resource rows once and keeps both actual resources");
        Check(mixed.Missions[0].Planned.Sum(c => c.Amount) == 800 && mixed.Cargo.Sum(c => c.Amount) == 400, "mission plan is separate from actual hold; no double counting");
        Check(mixed.Smuggling.Single().Amount == 25 && FreightOverview.Filter([mixed], "", 9, null, 0).Length == 1 &&
            string.Join("\n", FreightOverview.Details(mixed)).Contains("SEPARATE SMUGGLING HOLD"), "separate smuggling hold is visible and filterable without merging it into ordinary cargo or fuel tanks");
        Check(mixed.Missions.Length == 3 && mixed.Missions[1].Title.Contains("Queued 1: Refuel") && mixed.Missions[2].Title.Contains("Queued 2: Repair"), "next native missions retain queue order");
        Check(mixed.Missions[1].Commands.Length == 4 && mixed.Missions[1].Commands[0].StartsWith("1. Hyper"), "queued mission starts from first command as native reassignment does, ignoring stored progress");
        Check(mixed.Missions[0].Commands.Length == 8 && mixed.Missions[0].Commands[0].StartsWith("3. Load") && mixed.Missions[0].Commands[3].StartsWith("6. Hyper"), "remaining native commands start at actual progress and omit completed pickup approach");
        Check(mixed.Missions[0].Primary == source.Name && mixed.Missions[0].Secondary == destination.GetName() && mixed.Missions[0].Contracts.Length == 2, "multiple contracts retain their resource and pickup/delivery endpoints");
        Check(mixed.Dispatch && mixed.Missions[0].Reason.Contains("FD task #") && mixed.Missions[0].Reason.Contains("loaded delivery continues") && mixed.Missions[0].Reason.Contains("selection reason not recorded"), "cancelled loaded FD task and mixed native contract have distinct factual reasons");
        Check(mixed.Revert?.Primary == "Coordinates 123, 456" && mixed.Revert.Title.Contains("not a guaranteed next mission"), "revert mission is separate from the ordered next-mission queue");
        var details = string.Join("\n", FreightOverview.Details(mixed));
        Check(details.Contains("ACTUAL HOLD") && details.Contains("Mission cargo plan") && details.Contains("next assignment not selected"), "details distinguish hold, planned cargo and unknown future decisions");
        Check(FreightOverview.Filter([mixed], test.Name.ToUpperInvariant(), 7, FreightActivity.Pickup, 1).Length == 1 &&
            FreightOverview.Filter([mixed], "no such name", null, null, 0).Length == 0 && FreightOverview.Filter([mixed], "", null, null, 2).Length == 0,
            "name/resource/activity/FD filters combine without claiming an available ship");
        Check(FreightOverview.Filter([mixed], "", 8, null, 0).Length == 1, "resource filter includes a second mixed resource");
        before = EconomicFingerprint(e); stateBefore = JsonSerializer.Serialize(session.Data);
        _ = Row();
        Check(before == EconomicFingerprint(e) && stateBefore == JsonSerializer.Serialize(session.Data), "mixed-contract/FD/revert detail sampling is read-only");
        var race = new Harmony("local.dw2.freight-overview-race-check");
        try
        {
            overviewRaceShip = test;
            race.Patch(AccessTools.Method(typeof(ShipMission), nameof(ShipMission.ResolveCurrentCommand)), prefix: new HarmonyMethod(typeof(Program), nameof(ChangeOverviewCommand)));
            var raced = FreightOverview.Read(g, e);
            Check(raced.Ships.Single(s => s.Id == test.ShipId).Error != null && raced.Ships.Any(s => s.Id != test.ShipId && s.Error == null), "command changing during both read attempts marks only that ship unavailable instead of displaying torn queue");
        }
        finally { race.UnpatchAll(race.Id); overviewRaceShip = null; test.Mission.CommandProgress = 2; }
        test.Mission.CommandProgress = 5;
        var delivering = Row();
        Check(delivering.Activity == FreightActivity.Delivery && delivering.Missions[0].Commands[0].StartsWith("6. Hyper"), "delivery leg replaces completed pickup commands on next sample");
        test.Mission.CommandProgress = 3;
        Check(Row().Activity == FreightActivity.Refuel, "refuel command within transport is visible as current activity");
        test.Mission.CommandProgress = 255;
        Check(Row().Missions[0].Commands.Single().Contains("Invalid command position"), "invalid command position is explicit instead of fabricated queue");
        test.Mission.CommandProgress = 2;
        test.Mission.SecondaryTarget = new Item(ItemType.Ship, int.MaxValue, null!);
        Check(Row().Missions[0].Secondary == "Missing ship #2147483647", "missing target keeps ship visible with explicit missing endpoint");
        orders[0].ResourceId = 8;
        Check(!Row().Dispatch && Row().Missions[0].Reason.Contains("native assignment unconfirmed"), "reused contract identifiers with a different native resource cannot claim FD ownership");
        orders[0].ResourceId = 7;
        e.Orders.Remove(orders[0]);
        var stale = Row();
        Check(!stale.Dispatch && stale.Missions[0].Contracts[0].Contains("unavailable") && stale.Missions[0].Reason.Contains("native assignment unconfirmed"), "stale FD/contract identity is disclosed and not classified as verified dispatch");
        session.Fault = "Injected Matrix read failure";
        snapshot = FreightOverview.Read(g, e);
        Check(snapshot.Warning?.Contains("Injected Matrix read failure") == true && snapshot.Ships.Any(s => s.Id == test.ShipId && s.Error == null), "Matrix fault leaves native overview readable with explicit FD metadata warning");
        session.Fault = null;
        test.Mission = new ShipMission { Type = ShipMissionType.Repair };
        Check(Row().Activity == FreightActivity.Repair, "repairing freight hull remains in overview");
        test.Mission.Type = ShipMissionType.Retrofit;
        Check(Row().Activity == FreightActivity.Retrofit, "retrofit freight hull remains in overview");
        test.Mission = new ShipMission(); test.NextMissions.Clear(); test.RevertMission = null!; test.Cargo.Clear(); test.CargoSmuggling.Clear();
        Check(Row().Activity == FreightActivity.Idle && Row().Cargo.Length == 0 && Row().Missions.Length == 0, "empty idle freighter stays visible without invented assignment");
        test.HullConstructionLevel = 0;
        Check(Row().Activity == FreightActivity.Construction, "unfinished hull has explicit construction state");
        test.Cargo.Add(new Cargo(7, float.NaN, new Item<Empire>(e)));
        Check(Row().Error?.Contains("Invalid cargo amount") == true, "invalid ship sample is marked unavailable while other ships remain readable");
        var owner = test.Empire; test.Empire = new Item<Empire>(g.GetIndependentEmpire());
        Check(!FreightOverview.Read(g, e).Ships.Any(s => s.Id == test.ShipId), "foreign ownership removes freighter on the next snapshot"); test.Empire = owner;
        var role = test.Role; test.Role = ShipRole.TroopTransport;
        Check(!FreightOverview.Read(g, e).Ships.Any(s => s.Id == test.ShipId), "military troop transport cannot leak into cargo fleet overview"); test.Role = role;
        string longWord = new('W', 240); string sentence = "Very long name " + longWord + " and mixed cargo";
        string joined = string.Join("", Panel.Wrap(sentence, 598)).Replace(" ", "");
        Check(joined == sentence.Replace(" ", "") && Panel.Wrap(longWord, 598).Count() > 1, "full-detail wrapping preserves long names and unbroken identifiers across pages");
    }

    static void VerifyFreightersUi(Galaxy g, Panel panel)
    {
        var e = g.Empires.GetPlayer(); var selected = UserInterfaceController.SelectionPanel.Item;
        UserInterfaceController.SelectionPanel.Item = null!;
        var tab = panel.Controls.OfType<DWButton>().Single(c => c.Text == "Freighters");
        NativeUi.Click(tab); UpdateUi();
        var snapshot = (FreightSnapshot)AccessTools.Field(typeof(Panel), "fleetSnapshot").GetValue(panel)!;
        Check(panel.Visible && snapshot.Ships.Length > 0 && panel.Controls.OfType<DWButton>().Any(c => c.Text == "[x] Freighters"), "native click opens fleet overview without a selected base");
        Check(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Start task").Visible == false, "dispatch mutation controls are hidden on the read-only fleet tab");
        string before = EconomicFingerprint(e), data = JsonSerializer.Serialize(State.For(g, e.EmpireId).Data);
        AccessTools.Field(typeof(Panel), "lastFleetRead").SetValue(panel, Environment.TickCount64);
        UpdateUi(); UpdateUi();
        Check(ReferenceEquals(snapshot, AccessTools.Field(typeof(Panel), "fleetSnapshot").GetValue(panel)), "repeated UI frames reuse the fleet snapshot within the two-second interval");
        var search = panel.Controls.OfType<DWTextBox>().Single(c => c.Name == "FD Search name");
        NativeUi.Click(search); search.Text = snapshot.Ships[0].Name.ToUpperInvariant(); UpdateUi();
        Check(panel.HasTypingFocus && ((FreighterInfo[])AccessTools.Field(typeof(Panel), "filteredShips").GetValue(panel)!).All(s => s.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)), "native name field accepts search and filters without losing typing focus");
        search.Text = "no matching ship test"; UpdateUi();
        Check(panel.Controls.OfType<DWButton>().Any(c => c.Text == "No matching freighters"), "empty filtered list has visible feedback");
        search.Text = ""; UpdateUi();
        var next = panel.Controls.OfType<DWButton>().Single(c => c.Text == "Ships >");
        NativeUi.Click(next); UpdateUi();
        Check((int)AccessTools.Field(typeof(Panel), "fleetPage").GetValue(panel)! == 1, "native list paging reaches additional ships");
        var visibleRows = (DWButton[])AccessTools.Field(typeof(Panel), "shipRows").GetValue(panel)!;
        NativeUi.Click(visibleRows[0]); UpdateUi();
        var detailNext = panel.Controls.OfType<DWButton>().Single(c => c.Text == "Details >");
        if (detailNext.Enabled)
        { NativeUi.Click(detailNext); UpdateUi(); Check((int)AccessTools.Field(typeof(Panel), "detailPage").GetValue(panel)! == 1, "native detail paging reaches the remainder of cargo/contracts/queue"); }
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Position.X == 602 && c.Position.Y == 38)); UpdateUi();
        var resource = (short?)AccessTools.Field(typeof(Panel), "filterResource").GetValue(panel);
        Check(resource != null && ((FreighterInfo[])AccessTools.Field(typeof(Panel), "filteredShips").GetValue(panel)!).All(s => s.Resources.Any(c => c.Id == resource)), "native resource selector filters actual and planned cargo");
        Check(before == EconomicFingerprint(e) && data == JsonSerializer.Serialize(State.For(g, e.EmpireId).Data), "native fleet navigation/search/paging leave economy and dispatch state unchanged");
        var bounds = panel.CalculateAbsoluteRectangle();
        Check(panel.Controls.All(c => { var r = c.CalculateAbsoluteRectangle(); return r.Left >= bounds.Left && r.Right <= bounds.Right && r.Top >= bounds.Top && r.Bottom <= bounds.Bottom; }), "both tab layouts fit native panel bounds at1280x720");
        NativeUi.Click(search);
        NativeUi.EachBlocker(typeof(Entry).Assembly, _ =>
        {
            UpdateUi();
            Check(!panel.Visible && !panel.HasTypingFocus && !ReferenceEquals(UserInterfaceController.ActiveTextBox, search), "native blocker hides fleet and releases search keyboard focus");
        }, UpdateUi);
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Dispatch"));
        UserInterfaceController.SelectionPanel.Item = selected; UpdateUi();
        Check(!search.Visible && !panel.HasTypingFocus && panel.Controls.OfType<DWButton>().Single(c => c.Text == "Start task").Visible, "return to Dispatch hides search, releases focus and restores source controls");
        var cached = AccessTools.Field(typeof(Panel), "fleetSnapshot").GetValue(panel);
        AccessTools.Field(typeof(Panel), "lastFleetRead").SetValue(panel, 0L); UpdateUi();
        Check(ReferenceEquals(cached, AccessTools.Field(typeof(Panel), "fleetSnapshot").GetValue(panel)), "closed fleet tab does not sample ships even after its refresh interval expires");
    }
}
