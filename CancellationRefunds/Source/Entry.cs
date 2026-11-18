using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using DistantWorlds.Core;
using DistantWorlds.Types;
using HarmonyLib;

namespace CancellationRefunds;

public static partial class Entry
{
    internal const string HarmonyId = "local.dw2.cancellation-refunds";
    static readonly Harmony Hooks = new(HarmonyId);
    static bool initialized;
    [ThreadStatic] static RecruitFrame? recruit;
    [ThreadStatic] static PaymentFrame? payment;
    [ThreadStatic] static CreditFrame? credit;
    [ThreadStatic] static FacilityCancel? facilityCancel;
    [ThreadStatic] static TroopCancel? troopCancel;
    [ThreadStatic] static int expenseDepth;

    internal sealed class RecruitFrame
    {
        internal RecruitFrame? Previous;
        internal Empire Empire = null!;
        internal Troop? Pending;
        internal int Orb;
    }
    internal sealed class PaymentFrame
    {
        internal PaymentFrame? Previous;
        internal Empire Empire = null!;
        internal ExpenseType Type;
        internal Troop? Troop;
        internal int Orb, ExpectedDepth, Calls;
        internal float Paid;
        internal bool Eligible;
    }
    sealed class CreditFrame
    {
        internal Economy Economy = null!;
        internal ExpenseType Type;
        internal int ExpectedDepth;
        internal float Amount;
        internal bool Applied;
    }
    internal sealed class FacilityCancel
    {
        internal FacilityCancel? Previous;
        internal Empire Empire = null!;
        internal Orb Orb = null!;
        internal PlanetaryFacility? Facility;
        internal Ledger.Bound? Candidate;
    }
    internal sealed class TroopCancel
    {
        internal TroopCancel? Previous;
        internal Empire Empire = null!;
        internal HashSet<Troop> Targets = new(ReferenceEqualityComparer.Instance);
    }

    public static void Init()
    {
        if (initialized) return;
        var engine=typeof(Galaxy).Assembly;
        var version=engine.GetName().Version;
        bool beta=version==new Version(1,3,8,7);
        if(version!=new Version(1,3,8,4)&&!beta)
            throw new NotSupportedException("Cancellation Refunds requires DW2 1.3.8.4 or the verified Steam beta 1.3.8.7.");
        if(beta&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine.Location)))!="787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254")
            throw new NotSupportedException("Cancellation Refunds: unsupported Steam beta engine hash; no economic hooks installed.");
        SavedData.RequireChannels();
        try
        {
            Patch(typeof(Economy), nameof(Economy.Expense), [typeof(float), typeof(ExpenseType), typeof(long), typeof(short)],
                prefix: nameof(BeginExpense), finalizer: nameof(EndExpense), transpiler: nameof(ExpenseInstructions));
            Patch(typeof(Empire), nameof(Empire.BuildFacility), [typeof(Orb), typeof(PlanetaryFacilityDefinition), typeof(long), typeof(float), typeof(byte), typeof(bool), typeof(string)],
                prefix: nameof(BeginBuild), postfix: nameof(EndBuild), finalizer: nameof(EndPayment));
            Patch(typeof(Empire), nameof(Empire.ExecuteTaskRecruitTroops), [typeof(Orb), typeof(TroopDefinition), typeof(Troop)],
                prefix: nameof(BeginRecruit), finalizer: nameof(EndRecruit));
            foreach (var name in new[] { "RecruitAttackTroopsByType", "CheckRecruitDefenseTroop" })
                Patch(AccessTools.GetDeclaredMethods(typeof(Empire)).Single(m => m.Name == name), prefix: nameof(BeginRecruit), finalizer: nameof(EndRecruit));
            Patch(typeof(TroopDefinition), nameof(TroopDefinition.GenerateTroop),
                [typeof(Galaxy), typeof(Empire), typeof(Colony), typeof(Ship), typeof(TroopDefinition), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)],
                postfix: nameof(Generated));
            Patch(typeof(Empire), nameof(Empire.PayPrice), [typeof(Price), typeof(ExpenseType)],
                prefix: nameof(BeginPay), postfix: nameof(EndPay), finalizer: nameof(EndPayment));
            Patch(typeof(Empire), nameof(Empire.ExecuteTaskScrapPlanetaryFacility), [typeof(Orb), typeof(int)],
                prefix: nameof(BeginFacilityCancel), finalizer: nameof(EndFacilityCancel), transpiler: nameof(FacilityCancelInstructions));
            Patch(typeof(Empire), nameof(Empire.ExecuteTaskDisbandTroop), [typeof(TroopList)], prefix: nameof(BeginTroopCancel), finalizer: nameof(EndTroopCancel));
            Patch(typeof(Troop), nameof(Troop.CompleteTeardown), [typeof(Galaxy)], prefix: nameof(BeginTeardown), postfix: nameof(EndTeardown));
            Patch(AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "FacilityCompleted"), prefix: nameof(FacilityCompleted));
            Patch(AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "ProcessFacilities"), postfix: nameof(FacilitiesProgress));
            Patch(AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "ProcessTroops"), postfix: nameof(TroopsProgress));
            Patch(typeof(Troop), "ChangeEmpire", [typeof(Galaxy), typeof(short)], transpiler: nameof(OwnershipInstructions));
            Patch(AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "ChangeEmpire" && m.GetParameters().Length == 12), transpiler: nameof(OwnershipInstructions));
            Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.WriteToStream)), prefix: nameof(Save));
            Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.ReadFromStream)), postfix: nameof(Loaded));
            if (beta) InstallShipRefunds();
            initialized = true;
        }
        catch { Hooks.UnpatchAll(HarmonyId); throw; }
    }
    static void Patch(Type type, string method, Type[] args, string? prefix = null, string? postfix = null, string? finalizer = null, string? transpiler = null)
        => Patch(AccessTools.Method(type, method, args) ?? throw new MissingMethodException(type.FullName, method), prefix, postfix, finalizer, transpiler);
    static void Patch(MethodBase method, string? prefix = null, string? postfix = null, string? finalizer = null, string? transpiler = null)
    {
        HarmonyMethod? Hook(string? name, int priority) => name == null ? null : new HarmonyMethod(typeof(Entry), name)
        { priority = priority, after = name == nameof(TroopsProgress) ? ["DW2TroopLogistics.TroopHealing"] : [] };
        Hooks.Patch(method, Hook(prefix, Priority.First), Hook(postfix, Priority.Last), Hook(transpiler, Priority.Last), Hook(finalizer, Priority.Last));
    }
    static void BeginRecruit(Empire __instance, out RecruitFrame __state)
    {
        Ledger.Guard(__instance.Galaxy, () => _ = Ledger.For(__instance.Galaxy));
        __state = new RecruitFrame { Previous = recruit, Empire = __instance }; recruit = __state;
    }
    static Exception? EndRecruit(Exception? __exception, RecruitFrame? __state)
    { if (__state != null) recruit = __state.Previous; return __exception; }
    static void Generated(Galaxy galaxy, Empire empire, Colony colony, bool fullyRecruited, bool addToGalaxyEmpire, Troop? __result)
    {
        if (recruit == null || !ReferenceEquals(recruit.Empire, empire)) return;
        recruit.Pending = !fullyRecruited && addToGalaxyEmpire && colony != null && __result?.Recruiting == true &&
            __result.EmpireId == empire.EmpireId && ReferenceEquals(galaxy.Troops.GetById(__result.TroopId), __result) ? __result : null;
        recruit.Orb = colony?.OrbId ?? -1;
    }
    static void BeginBuild(Empire __instance, float constructionLevel, bool shouldPayCosts, out PaymentFrame __state)
    {
        Ledger.Guard(__instance.Galaxy, () => _ = Ledger.For(__instance.Galaxy));
        __state = new PaymentFrame { Previous = payment, Empire = __instance, Type = ExpenseType.FacilityBuilding,
            ExpectedDepth = expenseDepth + 1, Eligible = shouldPayCosts && constructionLevel == 0f };
        payment = __state;
    }
    static void EndBuild(PlanetaryFacility? __result, PaymentFrame __state)
    {
        if (__result == null) return;
        Ledger.Forget(__result);
        if (__state.Eligible && __state.Calls == 1 && __state.Paid > 0)
            Ledger.Guard(__state.Empire.Galaxy, () => Ledger.For(__state.Empire.Galaxy).Track(__state.Empire, __result, __state.Paid));
    }
    static void BeginPay(Empire __instance, Price price, ExpenseType expenseType, out PaymentFrame __state)
    {
        var troop = recruit?.Pending;
        var eligible = expenseType == ExpenseType.TroopRecruitment && recruit != null && ReferenceEquals(recruit.Empire, __instance) &&
            troop != null && ReferenceEquals(troop.Definition?.RecruitmentCost, price);
        __state = new PaymentFrame { Previous = payment, Empire = __instance, Type = expenseType, ExpectedDepth = expenseDepth + 1,
            Troop = eligible ? troop : null, Orb = recruit?.Orb ?? -1, Eligible = eligible };
        if (recruit != null) recruit.Pending = null;
        payment = __state;
    }
    static void EndPay(PaymentFrame __state)
    {
        if (__state.Eligible && __state.Calls == 1 && __state.Paid > 0 && __state.Troop is { } troop)
            Ledger.Guard(__state.Empire.Galaxy, () => Ledger.For(__state.Empire.Galaxy).Track(__state.Empire, troop, __state.Orb, __state.Paid));
    }
    static Exception? EndPayment(Exception? __exception, PaymentFrame? __state)
    { if (__state != null) payment = __state.Previous; return __exception; }

    static void BeginExpense(out int __state) { __state = expenseDepth; expenseDepth++; }
    static Exception? EndExpense(Exception? __exception, int __state) { expenseDepth = __state; return __exception; }
    static float NormalizeExpense(float amount, Economy economy, ExpenseType type)
    {
        if (credit is { Applied: false } c && c.ExpectedDepth == expenseDepth && ReferenceEquals(c.Economy, economy) && c.Type == type)
        {
            c.Applied = true;
            // Native balances are float. Round down to a representable credit
            // rather than allowing a large treasury's ULP to overpay the claim.
            var before = economy.Money; var after = before + c.Amount;
            if (!float.IsFinite(before) || !float.IsFinite(after)) return 0;
            if (after - before > c.Amount) after = MathF.BitDecrement(after);
            return -Math.Max(0, after - before);
        }
        return amount;
    }
    static void SubtractAndObserve(ref AtomicFloat balance, float amount, Economy economy, ExpenseType type)
    {
        // This call remains inside native Economy._LockObject. Other payments
        // cannot contaminate the before/after delta, including float rounding.
        var p = payment;
        var observe = p is { Eligible: true } && p.ExpectedDepth == expenseDepth && ReferenceEquals(p.Empire.Economy, economy) &&
            p.Type == type && !Economy.ExpenseIsPrivate(type) && float.IsFinite(amount) && amount > 0;
        var before = observe ? economy.Money : 0;
        balance.Subtract(amount);
        if (observe) { p!.Calls++; p.Paid += before - economy.Money; }
    }
    static IEnumerable<CodeInstruction> ExpenseInstructions(IEnumerable<CodeInstruction> input)
    {
        var code = input.ToList(); int factors = 0, subtracts = 0;
        var factor = AccessTools.Method(typeof(EconomyFactorSet), nameof(EconomyFactorSet.GetExpenseFactor));
        var subtract = AccessTools.Method(typeof(AtomicFloat), nameof(AtomicFloat.Subtract), [typeof(float)]);
        for (int i = 0; i < code.Count; i++)
        {
            var instruction = code[i];
            if (instruction.Calls(subtract))
            {
                subtracts++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Entry), nameof(SubtractAndObserve)));
                continue;
            }
            yield return instruction;
            if (i >= 2 && code[i - 2].Calls(factor) && code[i - 1].opcode == OpCodes.Mul && instruction.opcode == OpCodes.Starg_S)
            {
                factors++;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Entry), nameof(NormalizeExpense)));
                yield return new CodeInstruction(OpCodes.Starg_S, (byte)1);
            }
        }
        if (factors != 1 || subtracts != 2) throw new InvalidOperationException($"Unsupported Economy.Expense body: factor={factors}, subtract={subtracts}");
    }
    static void PayRefund(Empire payer, Ledger.Bound candidate) => Ledger.Guard(payer.Galaxy, () => PayRefundCore(payer, candidate));
    static void PayRefundCore(Empire payer, Ledger.Bound candidate)
    {
        var amount = Ledger.Consume(candidate);
        if (!(amount > 0) || !float.IsFinite(amount)) return;
        var previous = credit;
        var c = new CreditFrame { Economy = payer.Economy, Type = candidate.Payment.Kind switch
            {
                "ship" => ExpenseType.ShipBuilding,
                "facility" => ExpenseType.FacilityBuilding,
                _ => ExpenseType.TroopRecruitment
            },
            Amount = amount, ExpectedDepth = expenseDepth + 1 };
        credit = c;
        try
        {
            payer.Economy.Expense(-amount, c.Type, payer.Galaxy.StarDate, -1);
            if (!c.Applied) throw new InvalidOperationException("Native refund normalization was not reached.");
        }
        finally { credit = previous; }
    }
    static void BeginFacilityCancel(Empire __instance, Orb orb, int planetaryFacilityId, out FacilityCancel __state)
    {
        Ledger.Guard(__instance.Galaxy, () => _ = Ledger.For(__instance.Galaxy));
        var facility = orb?.Facilities.GetById(planetaryFacilityId);
        __state = new FacilityCancel { Previous = facilityCancel, Empire = __instance, Orb = orb!, Facility = facility,
            Candidate = facility == null ? null : Candidate(facility, __instance) };
        facilityCancel = __state;
    }
    static Exception? EndFacilityCancel(Exception? __exception, FacilityCancel? __state)
    { if (__state != null) facilityCancel = __state.Previous; return __exception; }
    static bool RemoveCancelledFacility(PlanetaryFacilityList list, PlanetaryFacility facility)
    {
        var removed = list.Remove(facility);
        if (removed)
        {
            var c = facilityCancel;
            if (c?.Candidate != null && ReferenceEquals(c.Facility, facility) && ReferenceEquals(c.Orb.Facilities, list)) PayRefund(c.Empire, c.Candidate);
            Ledger.Forget(facility);
        }
        return removed;
    }
    static IEnumerable<CodeInstruction> FacilityCancelInstructions(IEnumerable<CodeInstruction> input)
    {
        int replaced = 0;
        foreach (var instruction in input)
        {
            if (instruction.operand is MethodInfo m && m.Name == "Remove" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(PlanetaryFacility))
            {
                replaced++; instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(Entry), nameof(RemoveCancelledFacility));
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported facility scrap body.");
    }
    static void BeginTroopCancel(Empire __instance, TroopList troops, out TroopCancel __state)
    {
        Ledger.Guard(__instance.Galaxy, () => _ = Ledger.For(__instance.Galaxy));
        __state = new TroopCancel { Previous = troopCancel, Empire = __instance };
        if (troops != null) foreach (var troop in troops) if (troop != null) __state.Targets.Add(troop);
        troopCancel = __state;
    }
    static Exception? EndTroopCancel(Exception? __exception, TroopCancel? __state)
    { if (__state != null) troopCancel = __state.Previous; return __exception; }
    static void BeginTeardown(Troop __instance, Galaxy galaxy, out Ledger.Bound? __state)
    {
        __state = troopCancel != null && ReferenceEquals(troopCancel.Empire.Galaxy, galaxy) && troopCancel.Targets.Contains(__instance)
            ? Candidate(__instance, troopCancel.Empire) : null;
    }
    static void EndTeardown(Troop __instance, Galaxy galaxy, Ledger.Bound? __state)
    {
        if (galaxy == null || ReferenceEquals(galaxy.Troops.GetById(__instance.TroopId), __instance)) return;
        if (__state != null && troopCancel != null && !ReferenceEquals(troopCancel.Empire.Troops.GetById(__instance.TroopId), __instance))
            PayRefund(troopCancel.Empire, __state);
        Ledger.Forget(__instance);
    }
    static void FacilityCompleted(Galaxy galaxy, PlanetaryFacility facility)
    { Ledger.Guard(galaxy, () => { _ = Ledger.For(galaxy); Ledger.Forget(facility); }); }
    static void FacilitiesProgress(Orb colonyOrb)
    { Ledger.Guard(null, () => { if (colonyOrb?.Facilities != null) foreach (var facility in colonyOrb.Facilities) if (facility != null) Ledger.Observe(facility); }); }
    // Troop Logistics1.0.40 recalculates the native first recruit in its
    // postfix, possibly undoing temporary native completion. Observe only the
    // final state at Priority.Last. Finished troops retain Recruiting=false
    // when wounded, so damage cannot restore eligibility.
    static void TroopsProgress(Colony __instance, Empire empire)
    { Ledger.Guard(empire?.Galaxy, () => { if (__instance.Troops != null) foreach (var troop in __instance.Troops) if (troop != null) Ledger.Observe(troop); }); }
    static void SetTroopOwner(Troop troop, short owner)
    { var changed = troop.EmpireId != owner; troop.EmpireId = owner; if (changed) Ledger.Forget(troop); }
    static void SetFacilityOwner(PlanetaryFacility facility, short owner)
    { var changed = facility.EmpireId != owner; facility.EmpireId = owner; if (changed) Ledger.Forget(facility); }
    static void SetColonyOwner(Colony colony, Item<Empire> owner, Galaxy galaxy)
    {
        var previous = colony.EmpireId; colony.Empire = owner;
        if (previous != colony.EmpireId) Ledger.Guard(galaxy, () => Ledger.For(galaxy).InvalidateColony(colony.OrbId));
    }
    static IEnumerable<CodeInstruction> OwnershipInstructions(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
    {
        int changed = 0;
        foreach (var instruction in input)
        {
            if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo f)
            {
                string? wrapper = f.DeclaringType == typeof(Troop) && f.Name == nameof(Troop.EmpireId) ? nameof(SetTroopOwner) :
                    f.DeclaringType == typeof(PlanetaryFacility) && f.Name == nameof(PlanetaryFacility.EmpireId) ? nameof(SetFacilityOwner) :
                    f.DeclaringType == typeof(Colony) && f.Name == nameof(Colony.Empire) ? nameof(SetColonyOwner) : null;
                if (wrapper != null)
                {
                    changed++;
                    if (wrapper == nameof(SetColonyOwner)) yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(instruction);
                    instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(Entry), wrapper);
                }
            }
            yield return instruction;
        }
        if (changed != (__originalMethod.DeclaringType == typeof(Colony) ? 3 : 1)) throw new InvalidOperationException("Unsupported native ownership transition body.");
    }
    static void Save(Galaxy __instance) => Ledger.Guard(__instance, () => Ledger.For(__instance).Save());
    static void Loaded(Galaxy __result) { if (__result != null) Ledger.Guard(__result, () => _ = Ledger.For(__result)); }
    static Ledger.Bound? Candidate(object target, Empire empire)
    {
        Ledger.Bound? result = null; Ledger.Guard(empire.Galaxy, () => result = Ledger.Candidate(target, empire)); return result;
    }
}
