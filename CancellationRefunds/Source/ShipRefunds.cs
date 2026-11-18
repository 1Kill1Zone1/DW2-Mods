using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Types;
using HarmonyLib;

namespace CancellationRefunds;

public static partial class Entry
{
    static void InstallShipRefunds()
    {
        Patch(AccessTools.GetDeclaredMethods(typeof(Empire)).Single(m => m.Name == nameof(Empire.ExecuteTaskBuildShips) &&
            m.GetParameters().Length == 7), transpiler: nameof(ShipPaymentInstructions));
        Patch(AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m => m.Name == "CommandBuild"),
            transpiler: nameof(ShipPaymentInstructions));
        Patch(typeof(Empire), nameof(Empire.ExecuteTaskCancelShipConstruction), [typeof(Ship), typeof(StellarObject)],
            prefix: nameof(BeginYardCancel), postfix: nameof(EndShipCancel));
        Patch(typeof(Empire), nameof(Empire.ExecuteTaskScrapShip), [typeof(Ship)],
            prefix: nameof(BeginScuttle), postfix: nameof(EndShipCancel));
        Patch(typeof(Empire), nameof(Empire.ExecuteTaskScrapShips), [typeof(ShipList)],
            transpiler: nameof(GroupScuttleInstructions));
        Patch(typeof(Ship), nameof(Ship.ChangeEmpire), [typeof(short), typeof(bool), typeof(CharacterHandlingBehavior), typeof(float), typeof(bool)],
            prefix: nameof(BeginShipOwnerChange), finalizer: nameof(EndShipOwnerChange));
        Patch(AccessTools.GetDeclaredMethods(typeof(ConstructionSystem)).Single(m => m.Name == "BuildShip"),
            postfix: nameof(ObserveShipConstruction));
    }

    static IEnumerable<CodeInstruction> ShipPaymentInstructions(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var expense = AccessTools.Method(typeof(Economy), nameof(Economy.Expense), [typeof(float), typeof(ExpenseType), typeof(long), typeof(short)]);
        var record = AccessTools.Method(typeof(Entry), nameof(RecordShipExpense));
        var buildCost = AccessTools.Field(typeof(Design), nameof(Design.BuildCost));
        bool yard = __originalMethod.DeclaringType == typeof(Empire);
        // These are the newly constructed hulls in the hash-pinned Steam build.
        int[] paidShipLocals = yard ? [26] : [23, 39];
        var locals = __originalMethod.GetMethodBody()!.LocalVariables;
        int matched = 0;
        for (int i = 0; i < code.Count; i++)
        {
            var instruction = code[i];
            if (!instruction.Calls(expense) || i < 4 || !code[i - 4].LoadsField(buildCost) ||
                !code[i - 3].LoadsConstant((long)ExpenseType.ShipBuilding) || !code[i - 1].LoadsConstant(-1L))
            {
                yield return instruction;
                continue;
            }
            if (matched >= paidShipLocals.Length || locals[paidShipLocals[matched]].LocalType != typeof(Ship))
                throw new InvalidOperationException("Unsupported native state ship purchase.");
            yield return new CodeInstruction(OpCodes.Ldloc_S, (byte)paidShipLocals[matched++]).MoveLabelsFrom(instruction);
            yield return yard ? new CodeInstruction(OpCodes.Ldarg_0) : new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
            yield return new CodeInstruction(OpCodes.Call, record).MoveBlocksFrom(instruction);
        }
        if (matched != paidShipLocals.Length) throw new InvalidOperationException("Unsupported native state ship purchase count.");
    }

    static void RecordShipExpense(Economy economy, float amount, ExpenseType type, long starDate, short chargingEmpireId,
        Ship ship, Empire payer)
    {
        var frame = new PaymentFrame { Previous = payment, Empire = payer, Type = type,
            ExpectedDepth = expenseDepth + 1, Eligible = type == ExpenseType.ShipBuilding };
        payment = frame;
        try { economy.Expense(amount, type, starDate, chargingEmpireId); }
        finally { payment = frame.Previous; }
        if (frame.Calls == 1 && frame.Paid > 0)
            Ledger.Guard(payer.Galaxy, () => Ledger.For(payer.Galaxy).Track(payer, ship, frame.Paid));
    }

    static void BeginYardCancel(Empire __instance, Ship ship, StellarObject parent, out Ledger.Bound? __state)
    {
        __state = null;
        if (ship == null || parent?.GetEmpireId() != __instance.EmpireId) return;
        if (parent.GetConstructionSystem()?.ContainsShipWithConstructionType(ship, out var type) == true &&
            type == ConstructionType.Construction)
            __state = Candidate(ship, __instance);
    }

    static void BeginScuttle(Empire __instance, Ship ship, out Ledger.Bound? __state) =>
        __state = ship == null ? null : Candidate(ship, __instance);

    static void EndShipCancel(Empire __instance, Ship ship, Ledger.Bound? __state)
    {
        if (__state != null && (ship.IsDestroyed || !ReferenceEquals(__instance.Galaxy.Ships.GetById(ship.ShipId), ship)))
            PayRefund(__instance, __state);
    }

    static IEnumerable<CodeInstruction> GroupScuttleInstructions(IEnumerable<CodeInstruction> instructions)
    {
        var destroy = AccessTools.Method(typeof(Ship), nameof(Ship.EnsureDestroyShip), [typeof(Galaxy), typeof(DateTime), typeof(StellarObject), typeof(bool)]);
        var scuttle = AccessTools.Method(typeof(Entry), nameof(ScuttleOne));
        int matched = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(destroy))
            {
                matched++;
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call, scuttle).MoveBlocksFrom(instruction);
            }
            else yield return instruction;
        }
        if (matched != 1) throw new InvalidOperationException("Unsupported native group scuttle.");
    }

    static void ScuttleOne(Ship ship, Galaxy galaxy, DateTime time, StellarObject attacker, bool inView, Empire empire)
    {
        var candidate = Candidate(ship, empire);
        ship.EnsureDestroyShip(galaxy, time, attacker, inView);
        EndShipCancel(empire, ship, candidate);
    }

    static void BeginShipOwnerChange(Ship __instance, out short __state) => __state = __instance.EmpireId;

    static Exception? EndShipOwnerChange(Ship __instance, short __state, Exception? __exception)
    {
        if (__state != __instance.EmpireId) Ledger.Forget(__instance);
        return __exception;
    }

    static void ObserveShipConstruction(Ship ship, bool shipCompleted)
    {
        if (shipCompleted) Ledger.Forget(ship);
        else Ledger.Observe(ship);
    }
}
