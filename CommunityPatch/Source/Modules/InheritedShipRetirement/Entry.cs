using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DistantWorlds.Types;
using HarmonyLib;

[assembly: InternalsVisibleTo("InheritedShipRetirement.Check")]

namespace InheritedShipRetirement;

public static class Entry
{
    internal const string PatchId = "local.dw2.inherited-ship-retirement";
    internal const string EngineHash = "62F03F35A7EE8BBC7BF93AC7D184D614209631A4F68C488224E6CE9C9E2A0E23";
    private const string PreviousEngineHash = "787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254";
    static readonly Harmony Hooks = new(PatchId);
    static readonly ConditionalWeakTable<Galaxy, Campaign> Campaigns = new();
    [ThreadStatic] static int assigning;
    static bool installed;

    internal sealed class Campaign
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<int, Intent> Pending;
        internal readonly Dictionary<int, DateTime> NextCheck = new();
        internal readonly bool Enabled;
        internal Campaign(Galaxy galaxy)
        {
            try { Pending = Store.Load(galaxy); Enabled = true; }
            catch (Exception ex) { Pending = new(); Report("Metadata unavailable; retirement disabled for this galaxy", ex); }
        }
    }
    internal sealed record Acquisition(Empire Previous, Ship[] Ships);
    static Campaign State(Galaxy galaxy) => Campaigns.GetValue(galaxy, g => new(g));

    public static void Init()
    {
        if (installed) return;
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Ship).Assembly.Location))) is not (EngineHash or PreviousEngineHash))
            throw new NotSupportedException("Inherited Ship Retirement 0.1.1 requires a verified Steam beta engine.");
        Store.Require();
        try
        {
            var conquest = AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "ChangeEmpire" && m.GetParameters().Length == 12);
            Hooks.Patch(conquest, prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeConquest)), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterConquest)));
            Hooks.Patch(AccessTools.DeclaredMethod(typeof(Ship), "DoTasks", [typeof(DateTime), typeof(Galaxy), typeof(bool)]), prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeShipTasks)));
            Hooks.Patch(AccessTools.DeclaredMethod(typeof(Ship), "CheckAutoAssignMission"), prefix: new HarmonyMethod(typeof(Entry), nameof(AllowAutomaticAssignment)) { priority = Priority.First });
            var repairRefuelRetrofit = AccessTools.DeclaredMethod(typeof(Ship), "CheckRepairRefuelRetrofit",
                [typeof(Empire), typeof(DateTime), typeof(bool), typeof(bool).MakeByRefType(), typeof(bool)]);
            Hooks.Patch(repairRefuelRetrofit, prefix: new HarmonyMethod(typeof(Entry), nameof(AllowPendingRetirementRepairRefuel)) { priority = Priority.First });
            var core = AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m => m.Name == "AssignMissionCore" && m.GetParameters()[0].ParameterType == typeof(ShipMission));
            Hooks.Patch(core, prefix: new HarmonyMethod(typeof(Entry), nameof(ManualOverride)) { priority = Priority.First });
            Hooks.Patch(AccessTools.DeclaredMethod(typeof(Ship), "QueueMission"), prefix: new HarmonyMethod(typeof(Entry), nameof(ManualOverride)) { priority = Priority.First });
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Ship)).Where(m => m.Name == "CompleteTeardown"))
                Hooks.Patch(method, postfix: new HarmonyMethod(typeof(Entry), nameof(AfterShipTeardown)));
            installed = true;
        }
        catch { Hooks.UnpatchAll(PatchId); throw; }
    }

    static void BeforeConquest(Colony __instance, Galaxy galaxy, Empire empire, bool isConquered,
        bool allowEmpireTeardownWhenNoColonies, bool completeSilentIntegrationToConqueror, out Acquisition? __state)
    {
        __state = null;
        try
        {
            if (!isConquered || !allowEmpireTeardownWhenNoColonies || completeSilentIntegrationToConqueror || empire == null || __instance.EmpireId == empire.EmpireId) return;
            var previous = galaxy.Empires.GetById(__instance.EmpireId);
            if (previous is not { Active: true } || previous.Colonies.Count != 1 || !State(galaxy).Enabled) return;
            __state = new(previous, previous.Ships.ToArray().Where(s => s != null && s.EmpireId == previous.EmpireId).ToArray());
        }
        catch (Exception ex) { Report("Inspect conquest", ex); }
    }

    static void AfterConquest(Galaxy galaxy, Acquisition? __state)
    {
        if (__state == null || __state.Previous.Active) return;
        foreach (var ship in __state.Ships)
        {
            try
            {
                var owner = ship.GetEmpire();
                if (ship.EmpireId == __state.Previous.EmpireId || !Eligible(ship, owner)) continue;
                var state = State(galaxy);
                lock (state.Gate)
                {
                    if (!state.Enabled || state.Pending.ContainsKey(ship.ShipId)) continue;
                    var intent = new Intent(1, ship.ShipId, ship.EmpireId, "Waiting for retirement assessment");
                    Store.Save(galaxy, ship.ShipId, intent);
                    state.Pending.Add(ship.ShipId, intent);
                    Console.WriteLine($"Inherited Ship Retirement: ship #{ship.ShipId} queued for base disassembly.");
                }
            }
            catch (Exception ex) { Report("Queue inherited ship " + ship.ShipId, ex); }
        }
    }

    internal static bool Eligible(Ship ship, Empire? owner)
    {
        if (ship.IsDestroyed || ship.IsBase || ship.Role is ShipRole.FighterBomber or ShipRole.FighterInterceptor ||
            owner is not { Active: true } || owner.EmpireId <= 0 || owner.IsPirate ||
            owner.SpecialMode is EmpireMode.HiveMenace or EmpireMode.HiveRemnant || owner.Colonies.Count == 0 || owner.Policy == null) return false;
        bool military = Galaxy.CheckRoleIsMilitary(ship.Role);
        return (military ? owner.Policy.CaptureEnlistMilitaryShip : owner.Policy.CaptureEnlistCivilianShip) == ShipCaptureEnlistment.Never &&
            (military ? owner.Policy.CaptureDisassembleMilitaryShip : owner.Policy.CaptureDisassembleCivilianShip) == ShipCaptureDisassemble.AlwaysDisassembleAtBaseForTechAndResources;
    }

    static void BeforeShipTasks(Ship __instance, Galaxy galaxy, DateTime time) => Review(__instance, galaxy, time);
    internal static void Review(Ship ship, Galaxy galaxy, DateTime time)
    {
        var state = State(galaxy);
        if (!state.Enabled) return;
        lock (state.Gate)
        {
            if (!state.Pending.TryGetValue(ship.ShipId, out var intent)) return;
            try
            {
                var owner = ship.GetEmpire();
                if (ship.EmpireId != intent.EmpireId || !Eligible(ship, owner)) { Remove(galaxy, state, ship.ShipId); return; }
                if (ship.Mission?.IsManuallyAssigned == true || ship.MissionAssignmentType == ShipMissionAssignmentType.Manual)
                { Remove(galaxy, state, ship.ShipId); return; }
                if (state.NextCheck.TryGetValue(ship.ShipId, out var next) && time < next) return;
                state.NextCheck[ship.ShipId] = time.AddSeconds(10);
                if (ship.Mission?.Type is ShipMissionType.Retire or ShipMissionType.UnloadCargo or ShipMissionType.Escape or ShipMissionType.Repair or ShipMissionType.Refuel)
                { Reason(galaxy, state, intent, "Current order: " + ship.Mission.Type); return; }
                var yard = FindYard(ship, owner!, out string? blocked);
                if (yard == null) { Reason(galaxy, state, intent, "Waiting: " + blocked); return; }
                if (ship.Cargo == null) { Reason(galaxy, state, intent, "Waiting: cargo data unavailable"); return; }
                if (ship.Cargo.Any(c => c == null || !float.IsFinite(c.Amount) || c.Amount < 0 || c.Amount > 0 && c.EmpireId != ship.EmpireId))
                { Reason(galaxy, state, intent, "Waiting: foreign or invalid cargo requires manual handling"); return; }
                assigning++;
                try
                {
                    if (ship.Cargo.TotalAmount > 0)
                    {
                        bool assigned = ship.AssignMissionUnloadCargo(yard, ship.Cargo.GetAsResourceQuantities(ship.EmpireId), false, null!, false);
                        Reason(galaxy, state, intent, assigned ? "Unloading cargo before retirement" : "Waiting: cargo unload route unavailable");
                    }
                    else
                    {
                        ship.AssignMissionRetire(yard, null!, false);
                        Reason(galaxy, state, intent, ship.Mission?.Type == ShipMissionType.Retire ? "Retiring at construction base" : "Waiting: retirement route unavailable");
                    }
                }
                finally { assigning--; }
            }
            catch (Exception ex) { Report("Assess inherited ship " + ship.ShipId, ex); }
        }
    }

    internal static StellarObject? FindYard(Ship ship, Empire owner, out string? blocked)
    {
        blocked = "no own construction base";
        var nearest = ship.FindNearestConstructionBase(owner);
        // Try another real yard when the nearest is hazardous or unreachable.
        var candidates = owner.ConstructionBasesPerSystem.Values.SelectMany(v => v.ToArray()).Where(p => p != null)
            .Prepend(nearest!).Where(p => p != null).Distinct().ToArray();
        var wp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "GalaxyWaypoints");
        foreach (var yard in candidates)
        {
            if (yard.GetEmpireId() != ship.EmpireId || yard is Ship { IsDestroyed: true } ||
                yard is Ship p && (p.IsUnderConstructionOrRepair || p.ConstructionSystem?.Yards.Count is not > 0)) continue;
            // System path tables can omit their own system; local travel needs the coordinate fuel check.
            var source = ship.GetSystem(owner.Galaxy); var destination = yard.GetSystem(owner.Galaxy);
            bool local = source != null && destination?.SystemId == source.SystemId;
            bool inRange = local ? ship.WithinFuelRange(owner, yard.GalaxyX, yard.GalaxyY, 0f, ShipMissionType.Undefined) : ship.WithinFuelRangeBestPath(owner, yard, 0f);
            if (!inRange) { blocked = "insufficient fuel or no usable route"; continue; }
            if (!ship.CheckCanHandleLocationEffects(yard)) { blocked = "unsafe destination environment"; continue; }
            if (wp != null && owner.IsPlayer)
            {
                var check = wp.GetType("GalaxyWaypoints.Entry")?.GetMethod("AutomaticEnvironmentBlockReason", BindingFlags.Public | BindingFlags.Static);
                string? refusal = check == null ? "Waypoints assessment unavailable" : (string?)check.Invoke(null, [ship, yard]);
                if (refusal != null) { blocked = refusal; continue; }
            }
            blocked = null; return yard;
        }
        return null;
    }

    static bool AllowAutomaticAssignment(Ship __instance, ref bool __result)
    {
        var galaxy = __instance.GetEmpire()?.Galaxy;
        if (galaxy == null) return true;
        var state = State(galaxy);
        lock (state.Gate)
        {
            if (!state.Pending.ContainsKey(__instance.ShipId)) return true;
            __result = false; return false;
        }
    }
    // Keep the native repair/refuel path intact, but do not let its shared automatic path replace a pending
    // doctrine-driven retirement with Retrofit. Manual orders and changed policies remove or disqualify the intent.
    static void AllowPendingRetirementRepairRefuel(Ship __instance, ref bool allowRetrofit)
    {
        if (!allowRetrofit) return;
        try
        {
            var owner = __instance.GetEmpire();
            if (!Eligible(__instance, owner)) return;
            var state = State(owner!.Galaxy);
            lock (state.Gate)
            {
                if (state.Enabled && state.Pending.TryGetValue(__instance.ShipId, out var intent) && intent.EmpireId == __instance.EmpireId)
                    allowRetrofit = false;
            }
        }
        catch (Exception ex) { Report("Protect pending retirement from automatic retrofit", ex); }
    }
    static void ManualOverride(Ship __instance, bool manuallyAssigned)
    {
        if (!manuallyAssigned || assigning != 0) return;
        var galaxy = __instance.GetEmpire()?.Galaxy;
        if (galaxy == null) return;
        var state = State(galaxy);
        lock (state.Gate)
        {
            if (!state.Pending.ContainsKey(__instance.ShipId)) return;
            try { Remove(galaxy, state, __instance.ShipId); }
            catch (Exception ex) { Report("Persist manual retirement cancellation", ex); state.Pending.Remove(__instance.ShipId); }
        }
    }
    static void AfterShipTeardown(Ship __instance)
    {
        if (!__instance.IsDestroyed) return;
        var galaxy = AccessTools.Field(typeof(Ship), "_Galaxy").GetValue(__instance) as Galaxy;
        if (galaxy == null || !Campaigns.TryGetValue(galaxy, out var state)) return;
        lock (state.Gate)
        {
            if (!state.Pending.ContainsKey(__instance.ShipId)) return;
            try { Remove(galaxy, state, __instance.ShipId); }
            catch (Exception ex) { Report("Clean completed retirement", ex); }
        }
    }
    static void Remove(Galaxy galaxy, Campaign state, int id)
    {
        Store.Save(galaxy, id, null);
        state.Pending.Remove(id); state.NextCheck.Remove(id);
    }
    static void Reason(Galaxy galaxy, Campaign state, Intent previous, string reason)
    {
        reason = reason.Length > 200 ? reason[..200] : reason;
        if (previous.Reason == reason) return;
        var changed = previous with { Reason = reason };
        Store.Save(galaxy, previous.ShipId, changed);
        state.Pending[previous.ShipId] = changed;
        Console.WriteLine($"Inherited Ship Retirement: ship #{previous.ShipId}: {reason}");
    }
    // Optional BCL-only admission bridge: freight dispatch must not borrow a ship awaiting disassembly.
    public static string? AutomaticFreightBlockReason(object vessel)
    {
        if (vessel is not Ship ship) return "Invalid freight eligibility request";
        if (!installed) return "Inherited Ship Retirement is not initialized";
        try
        {
            var owner = ship.GetEmpire();
            if (!Eligible(ship, owner)) return null;
            var state = State(owner!.Galaxy);
            lock (state.Gate)
            {
                if (!state.Enabled) return "Retirement metadata is unavailable";
                return state.Pending.TryGetValue(ship.ShipId, out var intent) && intent.EmpireId == ship.EmpireId
                    ? "Ship is reserved for base disassembly" : null;
            }
        }
        catch (Exception ex) { Report("Freight eligibility", ex); return "Retirement eligibility is unavailable"; }
    }
    internal static Intent? Pending(Galaxy galaxy, int id) { var s = State(galaxy); lock (s.Gate) return s.Pending.GetValueOrDefault(id); }
    internal static void Forget(Galaxy galaxy) => Campaigns.Remove(galaxy);
    static void Report(string action, Exception ex) => Console.Error.WriteLine("Inherited Ship Retirement: " + action + ": " + ex);
}
