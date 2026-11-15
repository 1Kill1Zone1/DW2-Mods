using System.Runtime.CompilerServices;
using System.Text.Json;
using DistantWorlds2;
using DistantWorlds.Types;
using FreightDispatch;
using HarmonyLib;

namespace DW2ModLauncher.Loader;
static partial class Program
{
    static void VerifyPersistenceFailure(Galaxy g, Empire e, Ship source, StellarObject destination, Ship[] freeShips, string fixture)
    {
        foreach (var ship in freeShips) { ship.CheckCancelContracts(ship.Mission); ship.Mission = new ShipMission(); ship.MissionAssignmentType = ShipMissionAssignmentType.FullyAutomate; }
        float room = (float)AccessTools.Method(typeof(Native), "DestinationRoom").Invoke(null, [g, e, destination, (short)7])!;
        Check(room > freeShips.Max(s => s.Summary.CargoCapacity), "persistence-failure fixture retains native destination acceptance for a full and a partial allocation");
        source.StockLevels = new ResourceQuantityList(); // Make an accidental TopUp able to consume retained cargo.
        // Isolate the failed task's resource accounting while retaining all existing committed orders.
        foreach (var order in e.Orders.Where(o => o.ResourceId == 7)) order.Amount = order.AmountFulfilled;
        Check(Dispatch.Start(g, e, source, destination, [new DispatchResource { ResourceId = 7, Retain = 300 }], 1, out _), "persistence-failure task started");
        var session = State.For(g, e.EmpireId); var task = session.Data!.Tasks.Last(); Tick(g, e);
        var voyage = task.Voyages.Single(); var loaded = g.Ships.GetById(voyage.ShipId);
        loaded.Parent = new Item(ItemType.Ship, source.ShipId, source); loaded.ParentIsDockedAt = true;
        loaded.DockData ??= (DockData)RuntimeHelpers.GetUninitializedObject(typeof(DockData));
        loaded.Mission.CommandProgress = 2;
        var load = AccessTools.Method(typeof(Ship), "CommandLoad");
        load.Invoke(loaded, [g.Time, .1f, source.GetLocation(g), loaded.Mission.Cargo, loaded.Mission, e, false]);
        float picked = loaded.Cargo.GetAmount(e.EmpireId, 7);
        Check(picked > 0, "persistence-failure fixture has actual native loaded cargo to preserve");
        task.Limit = 2; State.Persist(g, session);
        var loadedContract = e.Orders.GetByOrderId(voyage.OrderId).Contracts.GetByContractId(voyage.ContractId);
        float otherReserved = source.CargoReserved.GetAmount(e.EmpireId, 7) - ((float)loadedContract.AmountToFulfill - (float)loadedContract.AmountPickedUp);
        float conserved = source.Cargo.GetAmount(e.EmpireId, 7) + source.CargoReserved.GetAmount(e.EmpireId, 7) + picked;
        string prefix = SavedData.ObserverPrefix(e.EmpireId); string before = SavedData.Read(g, prefix)!;
        var setterField = AccessTools.Field(typeof(SavedData), "setter");
        var original = (Action<object, string, string, string>)setterField.GetValue(null)!;
        int failedShip = -1; bool sawNewContract = false;
        setterField.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
        {
            if (ReferenceEquals(campaign, g) && owner == "freight-dispatch")
            {
                var added = task.Voyages.FirstOrDefault(v => v.ShipId != loaded.ShipId);
                sawNewContract = added != null && e.Orders.GetByOrderId(added.OrderId)?.Contracts.GetByContractId(added.ContractId) != null;
                failedShip = added?.ShipId ?? -1;
                throw new IOException("Injected Matrix write failure after allocation");
            }
            original(campaign, owner, key, value);
        }));
        try { Tick(g, e); }
        finally { setterField.SetValue(null, original); }
        Check(sawNewContract && session.Fault != null, "Matrix failure injected only after scheduler created a second real native contract");
        Check(task.Voyages.Count == 1 && task.Voyages[0].ShipId == loaded.ShipId && failedShip >= 0 && g.Ships.GetById(failedShip).Mission?.Type is null or ShipMissionType.Undefined, "failed persistence rolls back new unloaded allocation while retaining the loaded voyage");
        Check(Math.Abs(loaded.Cargo.GetAmount(e.EmpireId, 7) - picked) < .01 && Dispatch.Assigned(g, e, [task]).SetEquals([loaded.ShipId]), "persistence failure preserves aboard cargo and its actual allocation");
        Check(Math.Abs(source.CargoReserved.GetAmount(e.EmpireId, 7) - otherReserved) < .01 && Math.Abs(source.Cargo.GetAmount(e.EmpireId, 7) + source.CargoReserved.GetAmount(e.EmpireId, 7) + picked - conserved) < .01, "failed commit releases all task pickup reservations and conserves source plus aboard cargo");
        float freeBeforeBlockedLoad = source.Cargo.GetAmount(e.EmpireId, 7), reservedBeforeBlockedLoad = source.CargoReserved.GetAmount(e.EmpireId, 7);
        Check(!(bool)AccessTools.Method(typeof(Native), "AllowTopUp").Invoke(null, [loaded, loaded.Mission.Cargo, loaded.Mission, e])!, "known faulted task voyage still suppresses installed TopUp prefix");
        load.Invoke(loaded, [g.Time, 10000f, source.GetLocation(g), loaded.Mission.Cargo, loaded.Mission, e, false]);
        Check(Math.Abs(loaded.Cargo.GetAmount(e.EmpireId, 7) - picked) < .01, "faulted task state blocks further native pickups instead of falling through");
        Check(Math.Abs(source.Cargo.GetAmount(e.EmpireId, 7) - freeBeforeBlockedLoad) < .01 && Math.Abs(source.CargoReserved.GetAmount(e.EmpireId, 7) - reservedBeforeBlockedLoad) < .01 && loaded.Mission.Cargo.TotalAmount < .01, "actual faulted Load with installed TopUp cannot enlarge pending cargo or move retained source cargo into reservations");
        Check(SavedData.Read(g, prefix) == before, "failed Matrix commit preserves previous complete bank");
        bool refusedFaultedCommit = false;
        try { State.Persist(g, session); } catch (InvalidOperationException) { refusedFaultedCommit = true; }
        Check(refusedFaultedCommit && SavedData.Read(g, prefix) == before, "ordinary commit cannot silently report success if the session faulted before acquiring its lock");
        using var reader = new BinaryReader(File.OpenRead(fixture));
        _ = new Galaxy().ReadFromStream(reader, out var metadata, out _);
        string output = Path.Combine(AppContext.BaseDirectory, "freight-dispatch-failed-commit-" + Guid.NewGuid().ToString("N") + ".DWGame");
        using (var writer = new BinaryWriter(File.Open(output, FileMode.CreateNew))) g.WriteToStream(writer, metadata);
        var restored = Load(output); var player = restored.Empires.GetPlayer(); var restoredTask = State.For(restored, player.EmpireId).Data!.Tasks.Single(t => t.Id == task.Id);
        Native.Reconcile(restored, player, restoredTask);
        Check(restoredTask.Voyages.Count == 1 && restoredTask.Voyages[0].ShipId == loaded.ShipId && Dispatch.Assigned(restored, player, [restoredTask]).Count == 1 && restored.Ships.GetById(failedShip).Mission?.Type is null or ShipMissionType.Undefined, "native save/reload after failed commit contains only recorded loaded voyage and no hidden quota allocation");
        Console.WriteLine("Retained failed-commit roundtrip artifact: " + output);

        var destinationOrb = (Orb)destination;
        double ledgerBeforeDelivery = task.Resources.Single(r => r.ResourceId == 7).Delivered;
        float destinationBefore = destinationOrb.Colony!.Cargo.GetAmount(e.EmpireId, 7);
        loaded.Parent = new Item(ItemType.Orb, destinationOrb.OrbId, destinationOrb); loaded.ParentIsDockedAt = true;
        loaded.Mission.CommandProgress = 7;
        AccessTools.Method(typeof(Ship), "CommandUnload").Invoke(loaded,
            [g.Time, 10000f, destination.GetLocation(g), loaded.Mission, e, g.StarDate, false]);
        float delivered = destinationOrb.Colony.Cargo.GetAmount(e.EmpireId, 7) - destinationBefore;
        Check(Math.Abs(delivered - picked) < .01 && loaded.Cargo.GetAmount(e.EmpireId, 7) < .01, "faulted native unload delivers exactly the loaded cargo without requiring scheduler recovery");
        Check(Math.Abs(task.Resources.Single(r => r.ResourceId == 7).Delivered - ledgerBeforeDelivery - delivered) < .01, "faulted session records genuine native delivery before contract removal");
        AccessTools.Method(typeof(Native), "AfterUnload").Invoke(null, [loaded, e]);
        AccessTools.Method(typeof(Native), "AfterUnload").Invoke(null, [loaded, e]);
        Check(Math.Abs(task.Resources.Single(r => r.ResourceId == 7).Delivered - ledgerBeforeDelivery - delivered) < .01, "repeated unload reconciliation after fault does not double-count delivery");
        loaded.Mission.CompleteMission(e, loaded); Native.Reconcile(g, e, task);
        Check(task.Voyages.Count == 0 && Dispatch.Assigned(g, e, [task]).Count == 0, "finished faulted delivery releases voyage and native contract before save");
        string bankBeforeFailedSave = SavedData.Read(g, prefix)!;
        string blocked = Path.Combine(AppContext.BaseDirectory, "freight-dispatch-blocked-save-" + Guid.NewGuid().ToString("N") + ".DWGame");
        bool blockedSave = false;
        setterField.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
        {
            if (ReferenceEquals(campaign, g) && owner == "freight-dispatch") throw new IOException("Injected persistent Matrix outage at save barrier");
            original(campaign, owner, key, value);
        }));
        try
        {
            using var writer = new BinaryWriter(File.Open(blocked, FileMode.CreateNew));
            try { g.WriteToStream(writer, metadata); }
            catch (IOException ex) when (ex.Message.Contains("Injected persistent Matrix outage", StringComparison.Ordinal)) { blockedSave = true; }
        }
        finally { setterField.SetValue(null, original); }
        Check(blockedSave && new FileInfo(blocked).Length == 0 && SavedData.Read(g, prefix) == bankBeforeFailedSave,
            "persistent Matrix outage aborts native serialization before bytes and preserves the prior complete bank");
        using (var ui = new UiChecks.NativeUi(g))
        {
            string existing = Path.Combine(AppContext.BaseDirectory, "freight-dispatch-existing-save-" + Guid.NewGuid().ToString("N") + ".DWGame");
            File.WriteAllText(existing, "Previously completed save must remain unchanged"); string existingHash = Hash(existing);
            setterField.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
            {
                if (ReferenceEquals(campaign, g) && owner == "freight-dispatch") throw new IOException("Injected outage before native file open");
                original(campaign, owner, key, value);
            }));
            try { AccessTools.Method(typeof(DWGame), "SaveGame", [typeof(string)]).Invoke(ui.Host, [existing]); }
            finally { setterField.SetValue(null, original); }
            Check(Hash(existing) == existingHash && session.PendingSaveError?.Contains("previous save preserved", StringComparison.Ordinal) == true &&
                AccessTools.Field(typeof(State), "preparedGameSave").GetValue(null) == null,
                "actual native SaveGame hook skips file opening on Matrix failure and preserves an existing save byte-for-byte");
            ui.Host.Status = GameStatus.Saving; State.NotifySaveFailure(ui.Host, session);
            Check(session.PendingSaveError != null, "save failure notice waits until native saving finishes so EndSaveGame cannot erase it");
            ui.Host.Status = GameStatus.Running; State.NotifySaveFailure(ui.Host, session);
            Check(session.PendingSaveError == null, "return to running state publishes the queued native save-failure notice once");
        }
        string recoveredOutput = Path.Combine(AppContext.BaseDirectory, "freight-dispatch-delivered-after-fault-" + Guid.NewGuid().ToString("N") + ".DWGame");
        using (var ui = new UiChecks.NativeUi(g))
        {
            Check(State.BeforeGameSave(ui.Host, out var previousScope), "recovered Matrix preflight verifies the complete ledger before native file creation");
            int duplicateCommit = 0;
            setterField.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
            {
                if (ReferenceEquals(campaign, g) && owner == "freight-dispatch") { duplicateCommit++; throw new IOException("No second commit is safe after opening the save file"); }
                original(campaign, owner, key, value);
            }));
            try { using var writer = new BinaryWriter(File.Open(recoveredOutput, FileMode.CreateNew)); g.WriteToStream(writer, metadata); }
            finally { setterField.SetValue(null, original); State.FinishGameSave(null, previousScope); }
            Check(duplicateCommit == 0 && AccessTools.Field(typeof(State), "preparedGameSave").GetValue(null) == null,
                "prepared native save performs no fallible second commit after file opening and releases its thread scope");
            var sentinel = new IOException("Unrelated native save exception");
            Check(ReferenceEquals(State.FinishGameSave(sentinel, null), sentinel), "save-scope finalizer leaves unrelated native exceptions intact");
        }
        Check(session.Fault != null && !State.Ready(g, e, out _), "verified save retry preserves the runtime fault latch and does not silently resume pickups");
        var deliveredRestored = Load(recoveredOutput); var deliveredPlayer = deliveredRestored.Empires.GetPlayer();
        var recoveredSession = State.For(deliveredRestored, deliveredPlayer.EmpireId);
        var deliveredTask = recoveredSession.Data!.Tasks.Single(t => t.Id == task.Id);
        Native.Reconcile(deliveredRestored, deliveredPlayer, deliveredTask);
        double recoveredLedger = deliveredTask.Resources.Single(r => r.ResourceId == 7).Delivered;
        Check(Math.Abs(recoveredLedger - ledgerBeforeDelivery - delivered) < .01 &&
            Math.Abs(deliveredRestored.Orbs.GetById(destinationOrb.OrbId).Colony!.Cargo.GetAmount(deliveredPlayer.EmpireId, 7) - destinationBefore - delivered) < .01,
            "real save/load after completed faulted voyage preserves Delivered and physical destination cargo together");
        Check(recoveredSession.Fault == null && deliveredTask.Voyages.Count == 0 && Dispatch.Assigned(deliveredRestored, deliveredPlayer, [deliveredTask]).Count == 0 &&
            deliveredPlayer.Orders.GetByOrderId(voyage.OrderId) == null && deliveredRestored.Ships.GetById(failedShip).Mission?.Type is null or ShipMissionType.Undefined,
            "fault recovery reload has no lost/duplicate delivery, stale contract or hidden allocation");
        Console.WriteLine($"FIXED-LEDGER actualDelivered={delivered:R} restoredLedger={recoveredLedger:R} expectedLedger={ledgerBeforeDelivery + delivered:R}; " + recoveredOutput);
    }

    static void VerifyRejectedCommandRecovery(string fixture)
    {
        foreach (bool priority in new[] { true, false })
        {
            var g = Load(fixture); var e = g.Empires.GetPlayer(); var source = g.Ships.GetById(936); var session = State.For(g, e.EmpireId);
            State.Persist(g, session); string before = JsonSerializer.Serialize(session.Data);
            var setterField = AccessTools.Field(typeof(SavedData), "setter");
            var original = (Action<object, string, string, string>)setterField.GetValue(null)!;
            bool accepted;
            setterField.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
            {
                if (ReferenceEquals(campaign, g) && owner == "freight-dispatch") throw new IOException("Injected command commit failure");
                original(campaign, owner, key, value);
            }));
            try
            {
                accepted = priority ? Dispatch.SetPriority(g, e, source, true, out _) :
                    Dispatch.Start(g, e, source, e.Capital!, [new DispatchResource { ResourceId = 7, Retain = 300 }], 2, out _);
            }
            finally { setterField.SetValue(null, original); }
            Check(!accepted && session.Fault != null && JsonSerializer.Serialize(session.Data) == before,
                "failed " + (priority ? "Priority" : "Start") + " rolls back local command before a possible save retry");
            State.BeforeSave(g);
            Check(SavedData.Read(g, SavedData.ObserverPrefix(e.EmpireId)) == before,
                "recovered save cannot resurrect rejected " + (priority ? "Priority" : "Start") + " command");
        }
    }
}
