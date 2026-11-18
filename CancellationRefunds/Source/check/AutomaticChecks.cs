using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using CancellationRefunds;
using DistantWorlds.Types;
using HarmonyLib;

namespace DW2ModLauncher.Loader;
internal static partial class Program
{
    static bool spawnOnRefund;
    static Galaxy? nestedGalaxy;
    static Empire? nestedPayer;
    static Orb? nestedOrb;
    static TroopDefinition? nestedDefinition;
    static Troop? nestedTroop;
    static void NestedRecruitExpense(Economy __instance, float amount)
    {
        if (!spawnOnRefund || amount >= 0 || !ReferenceEquals(__instance, nestedPayer?.Economy)) return;
        spawnOnRefund = false;
        nestedTroop = Recruit(nestedGalaxy!, nestedPayer!, nestedOrb!, nestedDefinition!);
    }
    static void NestedPaidOrder(Galaxy g, Empire e, Orb orb, TroopDefinition definition)
    {
        var method = AccessTools.Method(typeof(Economy), nameof(Economy.Expense));
        var callback = AccessTools.Method(typeof(Program), nameof(NestedRecruitExpense));
        Harness.Patch(method, prefix: new HarmonyMethod(callback));
        try
        {
            var original = Recruit(g, e, orb, definition); var originalPaid = Ledger.Candidate(original, e)!.Payment.Paid;
            nestedGalaxy = g; nestedPayer = e; nestedOrb = orb; nestedDefinition = definition; nestedTroop = null; spawnOnRefund = true;
            var before = e.Economy.Money; Disband(e, original);
            Check(nestedTroop != null && Ledger.Candidate(nestedTroop, e) != null, "new positive paid order inside refund callback retains independent attribution");
            var nestedPaid = Ledger.Candidate(nestedTroop!, e)!.Payment.Paid;
            Near(e.Economy.Money - before, originalPaid - nestedPaid, "nested paid order and outer refund have separate native balance effects");
            before = e.Economy.Money; Disband(e, nestedTroop!); Near(e.Economy.Money - before, nestedPaid, "nested paid recruit subsequently refunds its own payment");
        }
        finally { Harness.Unpatch(method, callback); spawnOnRefund = false; nestedGalaxy = null; nestedPayer = null; nestedOrb = null; nestedDefinition = null; nestedTroop = null; }
    }
    // These are actual installed1.0.40 payment implementations. Only isolated
    // demand/population/treasury fixtures are configured; no mod Entry.Start,
    // logging configuration, live orders, save files or settings are touched.
    static void AutomaticPaths(string game, string fixture)
    {
        var g = Load(fixture);
        var e = g.Empires.First(x => x.IsPlayer && !x.IsPirate);
        var orb = e.Colonies.First(o => o.Colony != null && o.Colony.GetBestTroopCanRecruit(TroopType.Infantry, true) != null);
        e.Economy.EditMoney = 10000000;
        foreach (var troop in orb.Colony!.Troops.ToArray()) troop.CompleteTeardown(g);
        foreach (var colony in e.Colonies) if (colony.Colony != null) colony.Colony.AutomateTroopRecruitmentGarrison = ReferenceEquals(colony, orb);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "mods", "3821203666", "DW2TroopLogistics.dll"));
        var host = assembly.GetType("DW2Mod.ModHost", true)!;
        host.GetField("ErrorSink")!.SetValue(null, (Action<string>)(s => Console.WriteLine("OFFICIAL-ERROR|" + s)));
        host.GetField("WarnSink")!.SetValue(null, (Action<string>)(s => Console.WriteLine("OFFICIAL-WARN|" + s)));
        var warChest = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "mods", "3824731625", "DW2WarChest.dll"));
        var expenseLog = warChest.GetType("DW2WarChest.ExpenseLog", true)!;
        var wh = new Harmony("DW2WarChest.ExpenseLog");
        expenseLog.GetMethod("OwnerPrefix")!.Invoke(null, [e]);
        wh.Patch(AccessTools.Method(typeof(Economy), nameof(Economy.Expense)),
            prefix: new HarmonyMethod(expenseLog.GetMethod("ExpensePrefix")!), postfix: new HarmonyMethod(expenseLog.GetMethod("ExpensePostfix")!));
        try
        {
        var garrison = assembly.GetType("DW2TroopLogistics.Garrison", true)!;
        var nativeDefence = AccessTools.GetDeclaredMethods(typeof(Empire)).Single(m => m.Name == "CheckRecruitDefenseTroop");
        var gh = new Harmony("DW2TroopLogistics.Garrison");
        gh.Patch(nativeDefence, prefix: new HarmonyMethod(garrison.GetMethod("RecruitPrefix")!));
        try
        {
            var before = e.Economy.Money;
            var result = (Troop?)nativeDefence.Invoke(e, [orb, orb.Colony, 1f, 0f, 1000000f, g.StarDate, 0f, 0f, 0f]);
            Check(result != null && result.Recruiting, "actual installed Garrison replacement creates a recruit");
            var paid = before - e.Economy.Money;
            Check(paid > 0 && Ledger.Candidate(result!, e) != null, "Garrison skip-original path records its actual native charge");
            before = e.Economy.Money; Disband(e, result!); Near(e.Economy.Money - before, paid, "actual automatic Garrison recruit refunds once");
        }
        finally { gh.UnpatchAll(gh.Id); }

        var staging = assembly.GetType("DW2TroopLogistics.Staging", true)!;
        // Seed a finite outstanding invasion demand in the official mod's
        // ordinary plan cache, instead of modifying ships or simulating wars.
        var planType = staging.GetNestedType("Plan", BindingFlags.NonPublic)!;
        var plan = Activator.CreateInstance(planType, true)!;
        planType.GetField("BuiltAt")!.SetValue(plan, g.StarDate);
        ((IDictionary)planType.GetField("ByFleetId")!.GetValue(plan)!)[(short)1] = orb;
        var stageType = staging.GetNestedType("Stage", BindingFlags.Public | BindingFlags.NonPublic)!;
        var stage = Activator.CreateInstance(stageType, true)!;
        stageType.GetField("Orb")!.SetValue(stage, orb);
        ((IDictionary)stageType.GetField("Target")!.GetValue(stage)!)[TroopType.Infantry] = 10000;
        ((IDictionary)planType.GetField("ByOrbId")!.GetValue(plan)!)[orb.OrbId] = stage;
        ((IDictionary)staging.GetField("plans", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)[e.EmpireId] = plan;
        var nativeAttack = AccessTools.GetDeclaredMethods(typeof(Empire)).Single(m => m.Name == "RecruitAttackTroopsByType");
        var sh = new Harmony("DW2TroopLogistics.Staging");
        sh.Patch(nativeAttack, prefix: new HarmonyMethod(staging.GetMethod("RecruitPrefix")!));
        try
        {
            var prior = g.Troops.ToHashSet(ReferenceEqualityComparer.Instance); var before = e.Economy.Money;
            nativeAttack.Invoke(e, [TroopType.Infantry, 0f, 0, 1f, 1, g.StarDate, 1000000f, 0f, 0f]);
            var created = g.Troops.Where(t => !prior.Contains(t)).ToArray();
            Check(created.Length > 0 && created.All(t => Ledger.Candidate(t, e) != null), "actual installed Staging replacement records all new paid recruits");
            var paid = before - e.Economy.Money;
            before = e.Economy.Money; Disband(e, created); Near(e.Economy.Money - before, paid, "actual Staging automatic payments refund once");
        }
        finally { sh.UnpatchAll(sh.Id); }
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Economy), nameof(Economy.Expense)))!.Postfixes.Any(p => p.owner == wh.Id),
            "actual War Chest observer coexists with native automatic charges and refunds");
        }
        finally { wh.UnpatchAll(wh.Id); }
    }
    static void UpgradeAndCapture(string fixture)
    {
        var g = Load(fixture); var e = g.Empires.First(x => x.IsPlayer && !x.IsPirate);
        var orb = e.Colonies.First(o => o.Colony != null); e.Economy.EditMoney = 10000000;
        var troopDef = g.TroopDefinitions.First(d => d.Type == TroopType.Infantry && d.RecruitmentCost?.Money > 0);
        NestedPaidOrder(g, e, orb, troopDef);
        var troop = Recruit(g, e, orb, troopDef);
        troop.ChangeEmpire(g, e.EmpireId);
        Check(Ledger.Candidate(troop, e) != null, "no-op ownership command preserves paid eligibility");
        troop.ChangeEmpire(g, short.MaxValue);
        // The native invalid-ID path removes old membership before returning
        // without changing EmpireId. Repair only that disposable fixture's
        // membership to isolate whether our observer revoked the payment.
        e.Troops.CheckAdd(troop);
        Check(troop.EmpireId == e.EmpireId && Ledger.Candidate(troop, e) != null, "failed ownership store does not revoke payment history");
        var troopOther = g.Empires.First(x => x.EmpireId != e.EmpireId && !x.IsPirate && x.Colonies.Count > 0);
        troop.ChangeEmpire(g, troopOther.EmpireId); troop.ChangeEmpire(g, e.EmpireId);
        var troopCash = e.Economy.Money; Disband(e, troop);
        Near(e.Economy.Money, troopCash, "actual ownership-store hooks reject captured and recaptured troop");
        var definitions = g.PlanetaryFacilities.Where(d => d.BuildCost?.Money > 0 && !d.IsWonder && d.FacilityFamilyId >= 0 &&
            d.MaximumBuildCountPerEmpire <= 0 && d.MaximumBuildCountPerGalaxy <= 0 && orb.Facilities.CountByDefinition(d.PlanetaryFacilityDefinitionId, false) == 0).ToArray();
        var pair = definitions.SelectMany(lo => definitions.Where(hi => hi.FacilityFamilyId == lo.FacilityFamilyId && hi.FacilityFamilyLevel > lo.FacilityFamilyLevel && hi.BuildCost.Money > lo.BuildCost.Money)
            .Select(hi => (Low: lo, High: hi))).First();
        var original = e.BuildFacility(orb, pair.Low, g.StarDate, 1f, 1, false, "Upgrade baseline")!;
        var before = e.Economy.Money; e.ExecuteTaskUpgradePlanetaryFacility(orb, original.PlanetaryFacilityId, pair.High.PlanetaryFacilityDefinitionId, true);
        var replacement = orb.Facilities.First(f => f.PlanetaryFacilityDefinitionId == pair.High.PlanetaryFacilityDefinitionId);
        Check(e.Economy.Money < before && replacement.ConstructionProgress < 1, "native upgrade charged its actual price difference");
        Check(Ledger.Candidate(replacement, e) == null, "upgrade replacement is excluded despite being unfinished");
        before = e.Economy.Money; e.ExecuteTaskScrapPlanetaryFacility(orb, replacement.PlanetaryFacilityId); Near(e.Economy.Money, before, "upgrade cancellation pays no invented full-price refund");

        var facility = e.BuildFacility(orb, Building(g, orb), g.StarDate, 0, 1, true, "Captured check")!;
        Check(Ledger.Candidate(facility, e) != null, "new paid facility eligible before ownership change");
        var other = g.Empires.First(x => x.EmpireId != e.EmpireId && !x.IsPirate && x.Colonies.Count > 0);
        var change = AccessTools.GetDeclaredMethods(typeof(Colony)).Single(m => m.Name == "ChangeEmpire" && m.GetParameters().Length == 12);
        change.Invoke(orb.Colony, [g, other, false, false, CharacterHandlingBehavior.ChangeEmpire, 1f, 1f, true, false, false, false, false]);
        Check(orb.Colony!.EmpireId == other.EmpireId, "native colony ownership transfer occurred");
        change.Invoke(orb.Colony, [g, e, false, false, CharacterHandlingBehavior.ChangeEmpire, 1f, 1f, true, false, false, false, false]);
        Check(orb.Colony.EmpireId == e.EmpireId && Ledger.Candidate(facility, e) == null, "recaptured building does not regain refund eligibility");
        before = e.Economy.Money; e.ExecuteTaskScrapPlanetaryFacility(orb, facility.PlanetaryFacilityId); Near(e.Economy.Money, before, "recaptured facility cancellation pays zero");
    }
}
