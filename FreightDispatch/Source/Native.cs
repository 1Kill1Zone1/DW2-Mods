using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Core;
using DistantWorlds.Types;
using HarmonyLib;

namespace FreightDispatch;

static class Native
{
    const float Epsilon = .001f;
    [ThreadStatic] static Context? active;
    sealed record Context(Galaxy Galaxy, Empire Empire, DispatchTask Task, Ship Source, StellarObject Destination)
    { internal string WaitingReason = "Waiting: no free eligible own freighter"; }
    static readonly AccessTools.FieldRef<Ship, Galaxy> ShipGalaxy = AccessTools.FieldRefAccess<Ship, Galaxy>("_Galaxy");
    delegate Ship FindFreighterDelegate(Galaxy galaxy, Empire empire, Orb independentColony, StellarObject source, List<short> knownEmpires, StellarObject destination, float pathTime, out bool available, ulong deadline, bool foreign);
    static readonly FindFreighterDelegate FindFreighter = Method(typeof(Empire), "FindFreighterToFulfillContract", 10).CreateDelegate<FindFreighterDelegate>();
    static MethodInfo Method(Type type, string name, int count) => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Single(m => m.Name == name && m.GetParameters().Length == count);
    internal static void Install(Harmony hooks)
    {
        hooks.Patch(Method(typeof(Empire), nameof(Empire.FulfillOrders), 13), prefix: new HarmonyMethod(typeof(Native), nameof(Schedule)) { priority = Priority.Last });
        hooks.Patch(Method(typeof(Empire), "FulfillSingleOrderSources", 16), prefix: new HarmonyMethod(typeof(Native), nameof(OrderSources)));
        hooks.Patch(Method(typeof(Empire), "FindFreighterToFulfillContract", 10), transpiler: new HarmonyMethod(typeof(Native), nameof(FilterFreighters)));
        hooks.Patch(Method(typeof(Galaxy), "InitiateContract", 20), prefix: new HarmonyMethod(typeof(Native), nameof(BeforeContract)));
        hooks.Patch(Method(typeof(Empire), "OrderResourcesToMeetStockLevels", 9), prefix: new HarmonyMethod(typeof(Native), nameof(BeforeStockOrders)));
        hooks.Patch(Method(typeof(Ship), "CommandLoad", 7), prefix: new HarmonyMethod(typeof(Native), nameof(BeforeLoad)) { priority = Priority.First });
        hooks.Patch(Method(typeof(Ship), "CommandUnload", 7), postfix: new HarmonyMethod(typeof(Native), nameof(AfterUnload)));
        hooks.Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.CancelContract), [typeof(Contract)]), prefix: new HarmonyMethod(typeof(Native), nameof(BeforeCancelContract)));
        // The installed top-up mutates reserved cargo independently of contracts. Task cargo must stay exact.
        var topUp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2FreighterLogistics")?.GetType("DW2FreighterLogistics.TopUp");
        if (topUp != null) hooks.Patch(AccessTools.Method(topUp, "Prefix"), prefix: new HarmonyMethod(typeof(Native), nameof(AllowTopUp)));
    }
    internal static bool ValidSource(Galaxy g, Empire e, Ship? s) => s != null && s.IsBase && !s.IsDestroyed && s.EmpireId == e.EmpireId && ReferenceEquals(g.Ships.GetById(s.ShipId), s) && s.Cargo != null && s.CargoReserved != null;
    internal static bool ValidDestination(Galaxy g, Empire e, StellarObject? d) => d switch
    {
        Ship s => ValidSource(g, e, s) && s.IsSpaceportRole,
        Orb o => o.Colony?.EmpireId == e.EmpireId && ReferenceEquals(g.Orbs.GetById(o.OrbId), o),
        _ => false
    };
    static CargoList? Hold(StellarObject? d) => d is Ship s ? s.Cargo : (d as Orb)?.Colony?.Cargo;
    internal static bool SameHold(Ship s, StellarObject d) => ReferenceEquals(s, d) || ReferenceEquals(s.Cargo, Hold(d));
    internal static StellarObject? Destination(Galaxy g, DispatchTask t) => t.DestinationType == ItemType.Ship ? g.Ships.GetById(t.DestinationId) : g.Orbs.GetById(t.DestinationId);
    internal static float Available(Ship source, Empire e, short resource) => Math.Max(0, source.Cargo?.GetAmount(e.EmpireId, resource) ?? 0);
    internal static bool EconomyFreighter(Empire e, Ship? s)
    {
        if (s == null || s.EmpireId != e.EmpireId || s.IsDestroyed || s.Role is not (ShipRole.FreighterSmall or ShipRole.FreighterMedium or ShipRole.FreighterLarge)) return false;
        return s.Summary != null && (s.ConstructionLevel >= 1f || s.RetrofitConstructionSystem != null || s.Mission?.Type == ShipMissionType.Retrofit);
    }
    static bool HasStub(Ship? ship, short empire, DispatchVoyage v) => ship?.Mission?.Type == ShipMissionType.Transport && ship.Mission.ContractStubs?.Any(s => s.EmpireId == empire && s.OrderId == v.OrderId && s.ContractId == v.ContractId) == true;
    internal static bool VoyageStillAssigned(Galaxy g, Empire e, DispatchTask t, DispatchVoyage v) => HasStub(g.Ships.GetById(v.ShipId), e.EmpireId, v);
    static Contract? Contract(Empire e, DispatchVoyage v) => e.Orders.GetByOrderId(v.OrderId)?.Contracts?.GetByContractId(v.ContractId);

    static void Schedule(Galaxy galaxy, Empire empire, Orb independentColony, long starDate, float foreignResourceSupplyMoneyAvailable, OrderList orders,
        float minimumContractAmount, float money, List<short> empiresAtWarWithIds, EmpireList empiresWeTradeWith, List<short> empiresMetNotAtWarWithIds, OrbList knownIndependentColonies, ulong deadline)
    {
        if (empire == null || !empire.IsPlayer || independentColony != null || !State.Ready(galaxy, empire, out var session)) return;
        lock (session.Gate)
        {
            try
            {
                foreach (var task in session.Data!.Tasks.Where(Dispatch.IsOpen))
                {
                    Reconcile(galaxy, empire, task);
                    var source = galaxy.Ships.GetById(task.SourceId); var destination = Destination(galaxy, task);
                    if (task.Phase == DispatchPhase.Cancelling)
                    { CancelUnloaded(galaxy, empire, task); if (task.Voyages.Count == 0) { task.Phase = DispatchPhase.Cancelled; task.Status = "Cancelled; all task ships released"; } continue; }
                    if (!ValidSource(galaxy, empire, source) || !ValidDestination(galaxy, empire, destination) || SameHold(source!, destination!))
                    { task.Status = "Suspended: source/destination missing, ownership changed, or same cargo hold"; CancelUnloaded(galaxy, empire, task); continue; }
                    RetireReverseDemand(galaxy, empire, task);
                    if (empire.DangerousLocations.ContainsId(source!.LocationId) || empire.DangerousLocations.ContainsId(destination!.LocationId))
                    { task.Status = "Waiting: source or destination is dangerous"; continue; }
                    bool surplus = task.Resources.Any(r => Available(source, empire, r.ResourceId) - r.Retain > Epsilon);
                    if (!surplus)
                    {
                        if (task.Voyages.Count == 0) { task.Phase = DispatchPhase.Complete; task.Status = "Complete: manual reserves reached and deliveries finished"; }
                        else task.Status = "Delivering remaining committed cargo";
                        continue;
                    }
                    int assigned = Dispatch.Assigned(galaxy, empire, [task]).Count;
                    task.Status = assigned >= task.Limit ? "At requested freighter limit; deliveries in progress" : "Waiting for an eligible own freighter and safe route";
                    foreach (var resource in task.Resources)
                    {
                        while (assigned < task.Limit && !TaskHelper.ExitTasksEarly && Timestamp.Read() < deadline)
                        {
                            float amount = Math.Min(Math.Max(0, Available(source, empire, resource.ResourceId) - resource.Retain), DestinationRoom(galaxy, empire, destination, resource.ResourceId));
                            if (amount <= Epsilon) { if (Available(source, empire, resource.ResourceId) - resource.Retain > Epsilon) task.Status = "Waiting: destination has no cargo acceptance/space"; break; }
                            if (!Assign(galaxy, empire, task, source, destination, resource.ResourceId, amount, deadline)) break;
                            assigned = Dispatch.Assigned(galaxy, empire, [task]).Count;
                            task.Status = "Native freighters collecting/delivering";
                        }
                    }
                }
                State.Persist(galaxy, session);
            }
            catch (Exception ex)
            {
                session.Fault = "Dispatch stopped: " + ex.Message;
                try { StopNewPickups(galaxy, empire, session.Data!); }
                catch (Exception rollback) { session.Fault += "; reservation cleanup failed: " + rollback.Message; }
            }
        }
    }
    static float DestinationRoom(Galaxy g, Empire e, StellarObject destination, short resource)
    {
        var cargo = Hold(destination); if (cargo == null) return 0;
        float[] capacities = destination is Ship s ? s.GetCargoCapacities(false, e) : ((Orb)destination).Colony!.GetCargoCapacities(false);
        if (resource < 0 || resource >= capacities.Length) return 0;
        float capacity = capacities[resource]; if (!float.IsFinite(capacity) || capacity <= 0) return 0;
        float inbound = 0;
        foreach (var order in e.Orders)
            if (order != null && order.ResourceId == resource && ReferenceEquals(Hold(order.Destination.ResolveBase<StellarObject>(g)), cargo))
                foreach (var contract in order.Contracts) inbound += Math.Max(0, (float)contract.AmountToFulfill - (float)contract.AmountDelivered);
        return Math.Max(0, capacity - cargo.GetAmount(e.EmpireId, resource) - inbound);
    }
    static bool Assign(Galaxy g, Empire e, DispatchTask task, Ship source, StellarObject destination, short resource, float amount, ulong deadline)
    {
        var cache = new Dictionary<short, IndexFactorList> { [resource] = new IndexFactorList() };
        cache[resource].AddFactorAmount(source.ShipId, amount);
        var candidates = Empire.GetResourceSourcesByDistance(g, e, g.StarDate, cache, resource, Epsilon, destination, 0);
        var nativeSource = candidates.FirstOrDefault(c => ReferenceEquals(c.StellarObject, source));
        if (nativeSource == null) { task.Status = "Waiting: route unavailable or still being calculated"; return false; }
        var previous = active; active = new Context(g, e, task, source, destination);
        try
        {
            var ship = FindFreighter(g, e, null!, source, [], destination, nativeSource.SortTag, out _, deadline, false);
            if (ship == null) { task.Status = active.WaitingReason; return false; }
            // Zero unmet demand outside this call: the normal scheduler cannot recruit extra ships,
            // even if this mod's saved state later becomes unreadable.
            var order = new Order(e.Orders.GetNextId(), resource, 0, g.StarDate, e, new Item(task.DestinationType, task.DestinationId, destination));
            task.Orders.Add(order.OrderId); e.Orders.Add(order);
            Contract? contract = null;
            try
            {
                contract = g.InitiateContract(g.StarDate, e, null!, order, source, ship, amount, 0, 0, out _, 0, true);
                if (contract == null) return false;
                var voyage = new DispatchVoyage { OrderId = order.OrderId, ContractId = contract.ContractId, ShipId = ship.ShipId, ResourceId = resource };
                task.Voyages.Add(voyage);
                var cargo = new CargoList(); cargo.Add(new Cargo(resource, contract.AmountToFulfill, contract.Empire));
                var stubs = new ContractStubList { new ContractStub(order.OrderId, contract.ContractId, e.EmpireId) };
                ship.AssignMissionTransport(source, destination, cargo, stubs, null!, false);
                if (!HasStub(ship, e.EmpireId, voyage)) { g.CancelContract(contract); task.Voyages.Remove(voyage); task.Status = "Waiting: native mission/path assignment failed"; return false; }
                order.Amount = contract.AmountToFulfill;
                return true;
            }
            catch
            {
                if (contract != null && !task.Voyages.Any(v => v.OrderId == order.OrderId && HasStub(ship, e.EmpireId, v)))
                { g.CancelContract(contract); task.Voyages.RemoveAll(v => v.OrderId == order.OrderId); }
                throw;
            }
            finally
            {
                order.Amount = order.AmountFulfilled;
                if (contract == null || !task.Voyages.Any(v => v.OrderId == order.OrderId)) { e.Orders.Remove(order); task.Orders.Remove(order.OrderId); }
            }
        }
        finally { active = previous; }
    }
    static IEnumerable<CodeInstruction> FilterFreighters(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList(); int replaced = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            if (i + 4 < codes.Count && codes[i + 1].LoadsField(AccessTools.Field(typeof(Ship), nameof(Ship.Role))) &&
                codes[i + 3].LoadsField(AccessTools.Field(typeof(Ship), nameof(Ship.Summary))) &&
                codes[i + 4].Calls(AccessTools.Method(typeof(ShipSummary), nameof(ShipSummary.IsFreighterFast))))
            {
                yield return codes[i]; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Native), nameof(EligibleFreighter)));
                i += 4; replaced++;
            }
            else yield return codes[i];
        }
        if (replaced != 2) throw new InvalidOperationException("Native freighter selection changed; expected two eligibility checks");
    }
    static bool EligibleFreighter(Ship ship)
    {
        if (ship.Summary == null || !ShipSummary.IsFreighterFast(ship.Role, ship.Summary)) return false;
        if (active is not { } context) return true;
        if (!EconomyFreighter(context.Empire, ship) || ship.Cargo.TotalAmount > Epsilon || ship.CargoSmuggling.TotalAmount > Epsilon ||
            ship.Mission != null && ship.Mission.Type != ShipMissionType.Undefined || ship.MissionAssignmentType == ShipMissionAssignmentType.Manual) return false;
        if (!ship.CheckCanDockAtTarget(context.Empire, context.Source) || !ship.CheckCanDockAtTarget(context.Empire, context.Destination))
        { context.WaitingReason = "Waiting: free freighter cannot dock at an endpoint"; return false; }
        return SafeTwoLegRoute(context, ship);
    }
    static bool SafeTwoLegRoute(Context context, Ship ship)
    {
        string? environment = EnvironmentalRefusal(ship, context.Source) ?? EnvironmentalRefusal(ship, context.Destination);
        if (environment != null) { context.WaitingReason = "Waiting: " + environment; return false; }
        var pickup = context.Empire.FindBestJumpPathToDestination(ship, context.Source, 0, out float pickupDistance, out float pickupTime);
        if (pickup == null || pickup.Count == 0 && ship.GetSystemId(context.Galaxy) != context.Source.GetSystemId(context.Galaxy)) { context.WaitingReason = "Waiting: native route to pickup unavailable"; return false; }
        var delivery = context.Empire.FindBestJumpPathToDestination(context.Source, context.Destination, ship.Summary.HyperDriveJumpRange, 0, out float deliveryDistance, out float deliveryTime);
        if (delivery == null || (delivery.Count == 0 || ship.Summary.HyperDriveJumpRange <= 0) && context.Source.GetSystemId(context.Galaxy) != context.Destination.GetSystemId(context.Galaxy))
        { context.WaitingReason = "Waiting: native route/jump range to destination unavailable"; return false; }
        // Native fuel checks use path time, which accounts for slowed routes such as nebulae.
        pickupDistance = Math.Max(Math.Max(pickupDistance, pickupTime), Calculations.CalculateDistance(ship, context.Source));
        deliveryDistance = Math.Max(Math.Max(deliveryDistance, deliveryTime), Calculations.CalculateDistance(context.Source, context.Destination));
        float range = ship.CurrentFuelRange(0);
        bool enough = float.IsFinite(pickupDistance) && float.IsFinite(deliveryDistance) && range > 0 && (double)pickupDistance + deliveryDistance <= range;
        if (!enough) context.WaitingReason = "Waiting: insufficient current fuel for pickup plus delivery";
        return enough;
    }
    static Func<object, object, string?>? environmentCheck;
    internal static string? EnvironmentalRefusal(Ship ship, StellarObject target)
    {
        try
        {
            environmentCheck ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "GalaxyWaypoints")?
                .GetType("GalaxyWaypoints.Entry")?.GetMethod("AutomaticEnvironmentBlockReason", BindingFlags.Public | BindingFlags.Static,
                    [typeof(object), typeof(object)])?.CreateDelegate<Func<object, object, string?>>();
            return environmentCheck?.Invoke(ship, target);
        }
        catch { return "environment eligibility provider unavailable"; }
    }
    static void OrderSources(Galaxy galaxy, Empire empire, Order order, ref SortableStellarObjectList sources)
    {
        if (empire == null || !empire.IsPlayer || !State.Ready(galaxy, empire, out var session)) return;
        lock (session.Gate)
        {
            // Dedicated orders are fulfilled only by our bounded scheduler.
            if (session.Data!.Tasks.Any(t => t.Orders.Contains(order.OrderId))) { sources = new SortableStellarObjectList(); return; }
            var sorted = sources.OrderByDescending(s => s.StellarObject is Ship ship && ValidSource(galaxy, empire, ship) && session.Data.HighSources.Contains(ship.ShipId)).ToArray();
            sources = new SortableStellarObjectList(); sources.AddRange(sorted);
        }
    }
    static bool BeforeContract(Galaxy __instance, Empire buyingEmpire, Order order, int sourceId, ItemType sourceType, short sourceEmpireId,
        CargoList cargo, ref ResourceQuantityList stockLevels, Ship freighter, ref float orderAmountOutstanding, ref bool includeStockLevels, ref Contract? __result)
    {
        var player = __instance.Empires.GetPlayer();
        if (player == null || !State.Ready(__instance, player, out var session)) return true;
        bool playerOrder = buyingEmpire?.EmpireId == player.EmpireId && order.EmpireId == player.EmpireId;
        lock (session.Gate)
        {
            var ownerTask = playerOrder ? session.Data!.Tasks.FirstOrDefault(t => t.Orders.Contains(order.OrderId)) : null;
            if (ownerTask != null && active?.Task != ownerTask) { __result = null; return false; }
            var destination = order.Destination.ResolveBase<StellarObject>(__instance);
            if (playerOrder && session.Data!.Tasks.Any(t => t.Phase == DispatchPhase.Active && t.Resources.Any(r => r.ResourceId == order.ResourceId) &&
                __instance.Ships.GetById(t.SourceId) is { } exportSource && ValidSource(__instance, player, exportSource) && destination != null && SameHold(exportSource, destination)))
            { __result = null; return false; }
            var task = session.Data!.Tasks.FirstOrDefault(t => t.Phase == DispatchPhase.Active && __instance.Ships.GetById(t.SourceId) is { } s && ReferenceEquals(s.Cargo, cargo) && t.Resources.Any(r => r.ResourceId == order.ResourceId));
            if (task == null) return true;
            var source = __instance.Ships.GetById(task.SourceId); if (!ValidSource(__instance, player, source)) return true;
            float reserve = task.Resources.Single(r => r.ResourceId == order.ResourceId).Retain;
            bool dispatch = active?.Task == task && task.Orders.Contains(order.OrderId);
            if (dispatch && (buyingEmpire != player || !EconomyFreighter(player, freighter) || Dispatch.Assigned(__instance, player, [task]).Count >= task.Limit)) { __result = null; return false; }
            if (!dispatch) reserve = Math.Max(reserve, stockLevels.GetAmount(order.ResourceId));
            var effective = new ResourceQuantityList(); effective.AddAmount(order.ResourceId, reserve);
            stockLevels = effective; includeStockLevels = true;
            orderAmountOutstanding = Math.Min(orderAmountOutstanding, Math.Max(0, Available(source!, player, order.ResourceId) - reserve));
            return true;
        }
    }
    static void BeforeStockOrders(Empire __instance, int __0, ItemType __1, ref ResourceQuantityList __2)
    {
        var g = (Galaxy?)AccessTools.Field(typeof(Empire), "_Galaxy").GetValue(__instance);
        if (g == null || !__instance.IsPlayer || !State.Ready(g, __instance, out var session)) return;
        lock (session.Gate)
        {
            StellarObject? destination = __1 == ItemType.Ship ? g.Ships.GetById(__0) : __1 == ItemType.Orb ? g.Orbs.GetById(__0) : null;
            if (destination == null) return;
            var resources = session.Data!.Tasks.Where(t => t.Phase == DispatchPhase.Active && g.Ships.GetById(t.SourceId) is { } s && ValidSource(g, __instance, s) && SameHold(s, destination))
                .SelectMany(t => t.Resources).Select(r => r.ResourceId).ToHashSet();
            if (resources.Count == 0) return;
            var filtered = new ResourceQuantityList(); foreach (var r in __2) if (!resources.Contains(r.ResourceId)) filtered.AddAmount(r.ResourceId, r.Amount);
            __2 = filtered;
        }
    }
    internal static void Reconcile(Galaxy g, Empire e, DispatchTask task)
    {
        foreach (int id in task.Orders.ToArray())
        {
            var order = e.Orders.GetByOrderId(id);
            if (order == null) continue;
            if (order.EmpireId != e.EmpireId || order.DestinationId != task.DestinationId || order.DestinationType != task.DestinationType || !task.Resources.Any(r => r.ResourceId == order.ResourceId))
                throw new InvalidDataException("Saved dispatch order identity no longer matches native order");
            foreach (var c in order.Contracts)
            {
                if (c.EmpireId != e.EmpireId || c.SupplierType != ItemType.Ship || c.SupplierId != task.SourceId)
                    throw new InvalidDataException("Saved dispatch contract source/owner mismatch");
                if (!task.Voyages.Any(v => v.OrderId == id && v.ContractId == c.ContractId))
                    task.Voyages.Add(new DispatchVoyage { OrderId = id, ContractId = c.ContractId, ShipId = c.FreighterId, ResourceId = order.ResourceId });
            }
        }
        foreach (var voyage in task.Voyages.ToArray())
        {
            var contract = Contract(e, voyage);
            if (contract != null)
            {
                float delivered = Math.Max(voyage.DeliveredSeen, contract.AmountDelivered);
                task.Resources.Single(r => r.ResourceId == voyage.ResourceId).Delivered += delivered - voyage.DeliveredSeen;
                voyage.DeliveredSeen = delivered;
            }
            if (!EconomyFreighter(e, g.Ships.GetById(voyage.ShipId)) || !HasStub(g.Ships.GetById(voyage.ShipId), e.EmpireId, voyage))
            { if (contract != null) g.CancelContract(contract); task.Voyages.Remove(voyage); }
        }
        foreach (int id in task.Orders.ToArray())
            if (!task.Voyages.Any(v => v.OrderId == id)) { var order = e.Orders.GetByOrderId(id); if (order != null && order.Contracts.Count == 0) e.Orders.Remove(order); task.Orders.Remove(id); }
    }
    internal static void CancelUnloaded(Galaxy g, Empire e, DispatchTask task)
    {
        foreach (var voyage in task.Voyages.ToArray())
        {
            var contract = Contract(e, voyage); var ship = g.Ships.GetById(voyage.ShipId);
            if (contract == null || ship == null || !HasStub(ship, e.EmpireId, voyage)) continue;
            if ((float)contract.AmountPickedUp <= Epsilon)
            { g.CancelContract(contract); ship.Mission.CompleteMission(e, ship); task.Voyages.Remove(voyage); }
            else if ((float)contract.AmountPickedUp < (float)contract.AmountToFulfill - Epsilon)
            {
                var order = e.Orders.GetByOrderId(voyage.OrderId);
                g.CancelContract(contract); contract.AmountToFulfill = contract.AmountPickedUp;
                order.Contracts.Add(contract); order.Amount = contract.AmountToFulfill;
                foreach (var cargo in ship.Mission.Cargo) if (cargo.ResourceId == voyage.ResourceId) cargo.Amount = (AtomicFloat)0f;
            }
        }
        Reconcile(g, e, task);
    }
    internal static void StopNewPickups(Galaxy g, Empire e, DispatchState data)
    {
        foreach (var task in data.Tasks.Where(Dispatch.IsOpen)) CancelUnloaded(g, e, task);
    }
    static bool BeforeLoad(Ship __instance, ShipMission mission, Empire empire, ref float __result)
    {
        var g = ShipGalaxy(__instance); if (!empire.IsPlayer) return true;
        var session = State.For(g, empire.EmpireId);
        lock (session.Gate)
        {
            if (session.Data == null) return true;
            foreach (var task in session.Data.Tasks.Where(t => t.Voyages.Any(v => v.ShipId == __instance.ShipId)))
                if (session.Fault != null || task.Phase != DispatchPhase.Active || !ValidSource(g, empire, g.Ships.GetById(task.SourceId)) || !ValidDestination(g, empire, Destination(g, task)))
                {
                    try
                    {
                        CancelUnloaded(g, empire, task);
                        if (mission.Type == ShipMissionType.Transport) mission.CompleteCurrentCommand(empire, __instance);
                    }
                    catch (Exception ex) { session.Fault = "Pickup stopped: " + ex.Message; }
                    __result = 0; return false;
                }
        }
        return true;
    }
    static bool AllowTopUp(Ship __0, CargoList __1, ShipMission __2, Empire __3)
    {
        if (__0 == null || __3 == null || !__3.IsPlayer) return true;
        var g = ShipGalaxy(__0); var session = State.For(g, __3.EmpireId);
        lock (session.Gate)
            return session.Data == null || !session.Data.Tasks.Any(t => t.Voyages.Any(v => v.ShipId == __0.ShipId) ||
                session.Fault == null && t.Phase == DispatchPhase.Active && g.Ships.GetById(t.SourceId) is { } source && ValidSource(g, __3, source) && __0.Parent.ResolveBase<StellarObject>(g) is { } parent &&
                SameHold(source, parent) && __1.Any(c => t.Resources.Any(r => r.ResourceId == c.ResourceId)));
    }
    static void AfterUnload(Ship __instance, Empire empire)
    {
        var g = ShipGalaxy(__instance); if (!empire.IsPlayer) return;
        var session = State.For(g, empire.EmpireId);
        lock (session.Gate)
        {
            if (session.Data == null) return;
            try
            {
            foreach (var task in session.Data!.Tasks.Where(t => t.Voyages.Any(v => v.ShipId == __instance.ShipId))) Reconcile(g, empire, task);
            }
            catch (Exception ex) { session.Fault = "Delivery reconciliation stopped: " + ex.Message; }
        }
    }
    static void BeforeCancelContract(Galaxy __instance, Contract contract)
    {
        var e = __instance.Empires.GetPlayer(); if (e == null || contract == null) return;
        var session = State.For(__instance, e.EmpireId);
        lock (session.Gate)
        {
            if (session.Data == null) return;
            foreach (var task in session.Data!.Tasks)
                foreach (var voyage in task.Voyages)
                    if (voyage.OrderId == contract.OrderId && voyage.ContractId == contract.ContractId && voyage.ShipId == contract.FreighterId && contract.EmpireId == e.EmpireId)
                    {
                        float delivered = Math.Max(voyage.DeliveredSeen, contract.AmountDelivered);
                        task.Resources.Single(r => r.ResourceId == voyage.ResourceId).Delivered += delivered - voyage.DeliveredSeen;
                        voyage.DeliveredSeen = delivered;
                    }
        }
    }
    internal static void RetireReverseDemand(Galaxy g, Empire e, DispatchTask task)
    {
        var source = g.Ships.GetById(task.SourceId);
        if (task.Phase != DispatchPhase.Active || !ValidSource(g, e, source)) return;
        foreach (var order in e.Orders)
            if (order != null && !order.QuestResourceDeliveryTracking && task.Resources.Any(r => r.ResourceId == order.ResourceId) &&
                order.Destination.ResolveBase<StellarObject>(g) is { } destination && SameHold(source!, destination))
                order.Amount = order.AmountFulfilled;
    }
}
