using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Types;
using HarmonyLib;

namespace CommunityPatch;

internal static class ResourceFreightPolicy
{
    internal readonly record struct Policy(int Priority, int ShipLimit, int PercentLimit)
    {
        internal static readonly Policy Default = new(5, 0, 0);
        internal bool HasCeiling => ShipLimit > 0 || PercentLimit > 0;
        internal int Ceiling(int fleet) => Math.Min(
            ShipLimit == 0 ? int.MaxValue : ShipLimit,
            PercentLimit == 0 ? int.MaxValue : (int)((long)fleet * PercentLimit / 100));
    }

    private sealed class Pass(Empire empire, OrderList orders)
    {
        internal readonly Empire Empire = empire;
        internal readonly OrderList Orders = orders;
        internal bool Ordinary;
        internal Order[]? Sorted;
        internal int Count = -1;
    }

    [ThreadStatic] private static Pass? currentPass;
    [ThreadStatic] private static Empire? currentAutomaticBuyer;
    private static Func<object, string, string, string>? read;
    private static Action<object, string, string, string>? write;
    private const string Owner = "community-patch";
    private static readonly MethodInfo ListItem = AccessTools.PropertyGetter(typeof(List<Order>), "Item");
    internal static readonly MethodInfo ContractEntry = AccessTools.DeclaredMethod(typeof(Galaxy), nameof(Galaxy.InitiateContract),
        [typeof(long), typeof(Empire), typeof(Orb), typeof(Order), typeof(StellarObject), typeof(Ship), typeof(float),
         typeof(float), typeof(float), typeof(float).MakeByRefType(), typeof(float), typeof(bool)]);

    internal static void Init(Func<object, string, string, string> getter, Action<object, string, string, string> setter)
    {
        var orders = typeof(Empire).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Empire.FulfillOrders) && m.GetParameters().Length == 13);
        var single = AccessTools.DeclaredMethod(typeof(Empire), nameof(Empire.FulfillSingleOrder));
        var harmony = new Harmony("DW2.CommunityPatch.ResourceFreightPolicy");
        read = getter;
        write = setter;
        try
        {
            harmony.Patch(orders,
                prefix: new HarmonyMethod(typeof(ResourceFreightPolicy), nameof(BeginPass)) { priority = Priority.Last },
                transpiler: new HarmonyMethod(typeof(ResourceFreightPolicy), nameof(UsePriorityOrder)),
                finalizer: new HarmonyMethod(typeof(ResourceFreightPolicy), nameof(EndPass)));
            harmony.Patch(single, prefix: new HarmonyMethod(typeof(ResourceFreightPolicy), nameof(BeforeOrder)),
                finalizer: new HarmonyMethod(typeof(ResourceFreightPolicy), nameof(AfterOrder)));
            harmony.Patch(ContractEntry, prefix: new HarmonyMethod(typeof(ResourceFreightPolicy), nameof(BeforeContract)));
        }
        catch
        {
            harmony.UnpatchAll(harmony.Id);
            read = null;
            write = null;
            throw;
        }
    }

    internal static Policy Get(Empire empire, short resource)
    {
        string[]? values = read?.Invoke(empire.Galaxy, Owner, Key(empire, resource))?.Split('/');
        if (values is { Length: 3 } && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int priority) &&
            int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ships) &&
            int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out int percent) &&
            priority is >= 1 and <= 10 && percent <= 100)
            return new(priority, ships, percent);
        return Policy.Default;
    }

    internal static bool Set(Empire empire, short resource, Policy policy)
    {
        if (!empire.IsPlayer || write == null) return false;
        string value = FormattableString.Invariant($"{policy.Priority}/{policy.ShipLimit}/{policy.PercentLimit}");
        write(empire.Galaxy, Owner, Key(empire, resource), value);
        return read?.Invoke(empire.Galaxy, Owner, Key(empire, resource)) == value;
    }

    private static string Key(Empire empire, short resource) => FormattableString.Invariant($"freight.{empire.EmpireId}.{resource}");

    private static void BeginPass(Empire? empire, OrderList orders, out Pass? __state)
    {
        __state = currentPass;
        currentPass = empire is { IsPlayer: true } ? new Pass(empire, orders) : null;
    }

    private static void EndPass(Pass? __state) => currentPass = __state;

    private static IEnumerable<CodeInstruction> UsePriorityOrder(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var reads = code.Where(i => i.Calls(ListItem)).ToArray();
        if (reads.Length != 2) throw new InvalidOperationException("Expected a critical and an ordinary freight order loop.");
        // Replace just the indexed read in the ordinary loop. Native indexes, orders and removal lists stay intact.
        reads[1].opcode = OpCodes.Call;
        reads[1].operand = AccessTools.Method(typeof(ResourceFreightPolicy), nameof(NextOrder));
        return code;
    }

    private static Order NextOrder(List<Order> orders, int index)
    {
        if (currentPass is not { } pass || !ReferenceEquals(pass.Orders, orders)) return orders[index];
        pass.Ordinary = true;
        if (pass.Count != orders.Count)
        {
            pass.Sorted = ByPriority(pass.Empire, orders);
            pass.Count = orders.Count;
        }
        return pass.Sorted == null ? orders[index] : pass.Sorted[index];
    }

    internal static Order[]? ByPriority(Empire empire, List<Order> orders)
    {
        if (orders.Count < 2) return null;
        var priorities = new Dictionary<short, int>();
        int Rank(Order? order)
        {
            if (order == null || order.QuestResourceDeliveryTracking) return Policy.Default.Priority;
            if (!priorities.TryGetValue(order.ResourceId, out int rank))
                priorities.Add(order.ResourceId, rank = Get(empire, order.ResourceId).Priority);
            return rank;
        }
        if (!orders.Any(order => Rank(order) != Policy.Default.Priority)) return null;
        return orders.OrderByDescending(Rank).ToArray();
    }

    private static void BeforeOrder(Empire? empire, Order? order, Orb? independentColony, out Empire? __state)
    {
        __state = currentAutomaticBuyer;
        currentAutomaticBuyer = null;
        if (empire is not { IsPlayer: true } || order == null || independentColony != null || order.QuestResourceDeliveryTracking ||
            (IsFuel(empire, order.ResourceId) && currentPass?.Ordinary != true))
            return;
        currentAutomaticBuyer = empire;
    }

    private static void AfterOrder(Empire? __state) => currentAutomaticBuyer = __state;

    private static bool BeforeContract(Empire? buyingEmpire, Order? order, Ship? freighter, ref float cost, ref Contract? __result)
    {
        if (currentAutomaticBuyer == null || !ReferenceEquals(currentAutomaticBuyer, buyingEmpire) ||
            order == null || order.QuestResourceDeliveryTracking || !OwnFreighter(currentAutomaticBuyer, freighter) ||
            (IsFuel(currentAutomaticBuyer, order.ResourceId) && currentPass?.Ordinary != true))
            return true;
        var policy = Get(currentAutomaticBuyer, order.ResourceId);
        if (!policy.HasCeiling || CanAddSlot(currentAutomaticBuyer, order.ResourceId, policy, freighter!.ShipId))
            return true;
        cost = 0;
        __result = null;
        return false;
    }

    private static bool IsFuel(Empire empire, short resource) => empire.Galaxy.Resources.GetById(resource)?.IsFuel == true;

    internal static HashSet<int> Fleet(Empire empire) => empire.Ships.Where(ship => OwnFreighter(empire, ship))
        .Select(ship => ship.ShipId).ToHashSet();

    internal static HashSet<int> Assigned(Empire empire, short resource, HashSet<int> fleet)
    {
        var busy = new HashSet<int>();
        foreach (var order in empire.Orders.GetByResourceId(resource))
        {
            if (order?.Contracts == null) continue;
            foreach (var contract in order.Contracts.ToArray())
                if (contract != null && fleet.Contains(contract.FreighterId) &&
                    (float)contract.AmountToFulfill > (float)contract.AmountDelivered + .001f)
                    busy.Add(contract.FreighterId);
        }
        return busy;
    }

    internal static bool CanAddSlot(Empire empire, short resource, Policy policy, int? freighter)
    {
        var fleet = Fleet(empire);
        var assigned = Assigned(empire, resource, fleet);
        return (freighter.HasValue && assigned.Contains(freighter.Value)) || assigned.Count < policy.Ceiling(fleet.Count);
    }

    private static bool OwnFreighter(Empire empire, Ship? ship) =>
        ship is { IsDestroyed: false, IsUnderConstructionOrRepair: false } && ship.EmpireId == empire.EmpireId &&
        Galaxy.FreighterShipRoles.Contains(ship.Role);
}
