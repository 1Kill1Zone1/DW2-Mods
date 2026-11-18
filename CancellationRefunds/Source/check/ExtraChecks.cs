using System.Reflection;
using System.Runtime.Loader;
using CancellationRefunds;
using DistantWorlds.Types;
using HarmonyLib;

namespace DW2ModLauncher.Loader;
internal static partial class Program
{
    static bool factorActive, injectExpense;
    static float factorValue;
    static ExpenseType factorType;
    static bool overrideTrainRate, sawNativeCompletion;
    static Troop? watchedRecruit;
    static bool NativeTrainRate(ref float __result) { if (!overrideTrainRate) return true; __result = 1e9f; return false; }
    static void ObserveNativeCompletion() { if (overrideTrainRate && watchedRecruit != null) sawNativeCompletion = watchedRecruit.Readiness >= 1 && !watchedRecruit.Recruiting; }
    static bool FactorPrefix(ExpenseType expenseType, ref float __result)
    { if (!factorActive || expenseType != factorType) return true; __result = factorValue; return false; }
    static void NestedExpense(Economy __instance, float amount, ExpenseType type, long starDate)
    {
        if (!injectExpense || amount >= 0 || type != factorType) return;
        injectExpense = false; __instance.Expense(-16f, type, starDate, -1);
    }
    static void Extended(string game, string fixture, Galaxy g, Empire e, Orb orb, TroopDefinition def)
    {
        Harness.Patch(AccessTools.Method(typeof(EconomyFactorSet), "GetExpenseFactor", [typeof(ExpenseType)]), prefix: new HarmonyMethod(typeof(Program), nameof(FactorPrefix)));
        Harness.Patch(AccessTools.Method(typeof(Economy), nameof(Economy.Expense)), prefix: new HarmonyMethod(typeof(Program), nameof(NestedExpense)));
        factorActive = true; factorType = ExpenseType.TroopRecruitment; factorValue = 1.5f;
        var before = e.Economy.Money; var t = Recruit(g, e, orb, def); var paid = before - e.Economy.Money;
        Near(paid, def.RecruitmentCost.Money * 1.5f, "native charge applies controlled actual expense factor");
        t.Readiness = .25f; factorValue = .5f; before = e.Economy.Money;
        var expense = e.Economy.GetExpenseCurrentPeriod(ExpenseType.TroopRecruitment); injectExpense = true;
        Disband(e, t);
        Near(e.Economy.Money - before, paid * .75f + 8f, "changed factor does not multiply refund and nested unrelated expense retains factor");
        Near(e.Economy.GetExpenseCurrentPeriod(ExpenseType.TroopRecruitment) - expense, -paid * .75f - 8f, "native negative transaction records exact refund and separate nested expense");
        before = e.Economy.Money; e.Economy.Expense(-20, ExpenseType.TroopRecruitment, g.StarDate, -1);
        Near(e.Economy.Money - before, 10, "ordinary negative expense outside refund retains native factor");
        factorActive = false;

        t = Recruit(g, e, orb, def); paid = Ledger.Candidate(t, e)!.Payment.Paid; before = e.Economy.Money;
        var task = new GameTask { Type = GameTaskType.DisbandTroop }; task.Entities.AddItem(t); task.Entities.AddItem(t); e.ExecuteTaskDisbandTroop(task);
        Near(e.Economy.Money - before, paid, "duplicate troop references in native task pay once");

        var old = TroopDefinition.GenerateTroop(g, e, orb.Colony!, def, false); e.PayPrice(def.RecruitmentCost, ExpenseType.TroopRecruitment);
        Check(Ledger.Candidate(old, e) == null, "untracked historically paid/event order is not reconstructed");
        before = e.Economy.Money; Disband(e, old); Near(e.Economy.Money, before, "unknown historical payment pays zero");

        t = Recruit(g, e, orb, def); var other = g.Empires.First(x => x.EmpireId != e.EmpireId && !x.IsPirate && x.Colonies.Count > 0);
        t.ChangeEmpire(g, other.EmpireId); t.ChangeEmpire(g, e.EmpireId);
        before = e.Economy.Money; Disband(e, t); Near(e.Economy.Money, before, "captured and recaptured troop remains ineligible");
        var aiOrb = other.Colonies.First(o => o.Colony != null); other.Economy.EditMoney = 1000000;
        before = other.Economy.Money; t = Recruit(g, other, aiOrb, def); paid = before - other.Economy.Money;
        before = other.Economy.Money; Disband(other, t); Near(other.Economy.Money - before, paid, "AI payer receives its own refund");

        var cloneOf = e.Troops.FirstOrDefault(x => !x.Recruiting && x.Type == TroopType.Infantry);
        if (cloneOf == null) { cloneOf = TroopDefinition.GenerateTroop(g, e, orb.Colony!, def, false); cloneOf.Recruiting = false; cloneOf.Readiness = 1; }
        before = e.Economy.Money; var clone = Recruit(g, e, orb, cloneOf.Definition, cloneOf); paid = before - e.Economy.Money;
        Check(clone.IsCloned && Ledger.Candidate(clone, e) != null, "native cloned recruit records its own actual debit");
        before = e.Economy.Money; Disband(e, clone); Near(e.Economy.Money - before, paid, "clone cancellation refunds recorded debit");

        Completion(g, e, orb, def);
        ActualTroopLogistics(game, g, e, orb, def);
        RoundTrip(fixture);
        CorruptNative(fixture);
    }
    static PlanetaryFacilityDefinition Building(Galaxy g, Orb orb) => g.PlanetaryFacilities.First(d => d.BuildCost?.Money > 0 && !d.IsWonder && d.ColonyInfluenceFloor <= 0 &&
        d.MaximumBuildCountPerEmpire <= 0 && d.MaximumBuildCountPerGalaxy <= 0 && orb.Facilities.CountByDefinition(d.PlanetaryFacilityDefinitionId, false) == 0);
    static void Completion(Galaxy g, Empire e, Orb orb, TroopDefinition def)
    {
        var facility = e.BuildFacility(orb, Building(g, orb), g.StarDate, 0, 1, true, "Complete check")!;
        var completion = AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "FacilityCompleted");
        completion.Invoke(orb.Colony, [g, e, orb, facility, g.StarDate, 1.25f]);
        Check(facility.ConstructionProgress == 1 && facility.BuildDate == g.StarDate, "actual native facility completion runs synchronously");
        facility.ConstructionProgress = .25f; var before = e.Economy.Money;
        e.ExecuteTaskScrapPlanetaryFacility(orb, facility.PlanetaryFacilityId); Near(e.Economy.Money, before, "finished then damaged facility has no refund");

        var troop = Recruit(g, e, orb, def); troop.Readiness = .9999f;
        orb.Colony!.Troops.Sort(Comparer<Troop>.Create((a, b) => ReferenceEquals(a, b) ? 0 : ReferenceEquals(a, troop) ? -1 : ReferenceEquals(b, troop) ? 1 : 0));
        AccessTools.Method(typeof(Colony), "ProcessTroops").Invoke(orb.Colony, [1000f, e]);
        Check(!troop.Recruiting && troop.Readiness == 1f, "actual native training completes");
        troop.Readiness = .25f; before = e.Economy.Money; Disband(e, troop); Near(e.Economy.Money, before, "native completed-then-wounded troop cannot regain eligibility");

        facility = e.BuildFacility(orb, Building(g, orb), g.StarDate, 0, 1, true, "Failure check")!;
        before = e.Economy.Money; e.ExecuteTaskScrapPlanetaryFacility(orb, int.MaxValue);
        Near(e.Economy.Money, before, "missing facility cancellation pays zero");
        Check(Ledger.Candidate(facility, e) != null, "failed unrelated cancellation preserves actual paid object");
        e.ExecuteTaskScrapPlanetaryFacility(orb, facility.PlanetaryFacilityId);
    }
    static void ActualTroopLogistics(string game, Galaxy g, Empire e, Orb orb, TroopDefinition def)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "mods", "3821203666", "DW2TroopLogistics.dll"));
        var type = assembly.GetType("DW2TroopLogistics.TroopHealing", true)!;
        var process = AccessTools.Method(typeof(Colony), "ProcessTroops");
        var official = new Harmony("DW2TroopLogistics.TroopHealing");
        // Load only these actual installed callbacks, never Entry/ModHost.Start:
        // no settings, UI, session directory, or game-log writes are initialized.
        official.Patch(process, prefix: new HarmonyMethod(type.GetMethod("Prefix")!), postfix: new HarmonyMethod(type.GetMethod("Postfix")!));
        Harness.Patch(AccessTools.PropertyGetter(typeof(Colony), nameof(Colony.TroopRecruitmentRate)), prefix: new HarmonyMethod(typeof(Program), nameof(NativeTrainRate)));
        Harness.Patch(process, postfix: new HarmonyMethod(typeof(Program), nameof(ObserveNativeCompletion)) { priority = Priority.High });
        var own = Harmony.GetPatchInfo(process)!.Postfixes.Single(p => p.owner == Entry.HarmonyId);
        Check(own.after.Contains("DW2TroopLogistics.TroopHealing"), "explicit ordering follows installed TroopHealing correction");
        foreach (var officialFirst in new[] { false, true })
        {
            if (officialFirst)
            {
                var ours = new Harmony(Entry.HarmonyId); var observer = AccessTools.Method(typeof(Entry), "TroopsProgress");
                ours.Unpatch(process, observer);
                ours.Patch(process, postfix: new HarmonyMethod(observer) { priority = Priority.Last, after = [official.Id] });
            }
            var t = Recruit(g, e, orb, def); t.Readiness = .1f; watchedRecruit = t; sawNativeCompletion = false;
            orb.Colony!.Troops.Sort(Comparer<Troop>.Create((a, b) => ReferenceEquals(a, b) ? 0 : ReferenceEquals(a, t) ? -1 : ReferenceEquals(b, t) ? 1 : 0));
            overrideTrainRate = true;
            try { process.Invoke(orb.Colony, [.01f, e]); } finally { overrideTrainRate = false; }
            Check(sawNativeCompletion && t.Recruiting && t.Readiness < 1 && Ledger.Candidate(t, e) != null,
                "actual installed correction restores temporary native completion without losing payment; official-first=" + officialFirst);
            process.Invoke(orb.Colony, [100000f, e]);
            Check(!t.Recruiting && Ledger.Candidate(t, e) == null, "actual installed healing completion removes eligibility");
            var before = e.Economy.Money; t.Readiness = .25f; Disband(e, t); Near(e.Economy.Money, before, "installed-mod finished wounded troop pays zero");
        }
        watchedRecruit = null;
        official.UnpatchAll(official.Id);
    }
    static void RoundTrip(string fixture)
    {
        var path = Path.Combine(TestOutput.Root, "refund-roundtrip-" + Guid.NewGuid().ToString("N") + ".DWGame");
        try
        {
            using var reader = new BinaryReader(File.OpenRead(fixture));
            var g = new Galaxy().ReadFromStream(reader, out var data, out var errors); Check(errors.Count == 0, "fresh source for native roundtrip");
            var e = g.Empires.First(e => e.IsPlayer && !e.IsPirate); var orb = e.Colonies.First(o => o.Colony != null);
            e.Economy.EditMoney = 1000000;
            var def = g.TroopDefinitions.First(d => d.Type == TroopType.Infantry && d.RecruitmentCost?.Money > 0);
            var before = e.Economy.Money; var t = Recruit(g, e, orb, def); var paid = before - e.Economy.Money; t.Readiness = .25f;
            var id = t.TroopId;
            set(g, "other-owner", "sentinel", "keep-this-unchanged");
            using (var writer = new BinaryWriter(File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))) g.WriteToStream(writer, data);
            var loaded = Load(path); var payer = loaded.Empires.GetById(e.EmpireId); var restored = loaded.Troops.GetById(id);
            Check(get(loaded, "other-owner", "sentinel") == "keep-this-unchanged", "other Matrix owner's native-saved data remains unchanged");
            // Native load hook binds at load time; no intervening purchase.
            loaded.Time = loaded.Time.AddDays(10);
            before = payer.Economy.Money; Disband(payer, restored); Near(payer.Economy.Money - before, paid * .75f, "first cancellation after load and time advance refunds persisted payment");
            using (var writer = new BinaryWriter(File.Create(path))) loaded.WriteToStream(writer, data);
            var again = Load(path); var p2 = again.Empires.GetById(e.EmpireId); before = p2.Economy.Money;
            Check(again.Troops.GetById(id) == null && Ledger.For(again).Count == 0, "cancelled record and object remain absent after second native reload");
            var separate = Load(fixture); Check(Ledger.For(separate).Count == 0, "separate campaign does not inherit payment history");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    static void CorruptNative(string fixture)
    {
        // Create corruption in a separately deserialized fixture BEFORE eager
        // binding by temporarily removing only this mod's read postfix.
        var read = AccessTools.Method(typeof(Galaxy), nameof(Galaxy.ReadFromStream));
        var loader = AccessTools.Method(typeof(Entry), "Loaded");
        var own = new Harmony(Entry.HarmonyId); own.Unpatch(read, loader);
        Galaxy g;
        try { g = Load(fixture); }
        finally { own.Patch(read, postfix: new HarmonyMethod(loader) { priority = Priority.Last }); }
        set(g, "cancellation-refunds", "state.head", "corrupt");
        var e = g.Empires.First(e => e.IsPlayer && !e.IsPirate); var orb = e.Colonies.First(o => o.Colony != null);
        var def = g.TroopDefinitions.First(d => d.Type == TroopType.Infantry && d.RecruitmentCost?.Money > 0);
        var t = Recruit(g, e, orb, def); var cash = e.Economy.Money; Disband(e, t);
        Near(e.Economy.Money, cash, "corrupt campaign does not block native recruit/disband and pays no invented refund");
        Ledger.For(g).Save(); Check(get(g, "cancellation-refunds", "state.head") == "corrupt", "corrupt payload survives save preparation byte for byte");
    }
}
