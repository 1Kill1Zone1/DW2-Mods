using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using CancellationRefunds;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;

namespace DW2ModLauncher.Loader;
public static class LiveChannels
{
    public static readonly Dictionary<string, Delegate> Channels = new();
    public static Delegate? Get(string consumer, string channel, int version, Type expected) => Channels.TryGetValue(channel, out var value) && expected.IsInstanceOfType(value) ? value : null;
}
internal static class Bootstrap
{
    static void Main(string[] args)
    {
        if (args.Length is not (2 or 3))
            throw new ArgumentException("CancellationRefunds.Check <game-root> <read-only-fixture.DWGame> [--remaining|--missing-matrix|--hooks|--ships]");
        var game = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, n) =>
        {
            var path = Path.Combine(game, n.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly.GetExecutingAssembly().GetType("DW2ModLauncher.Loader.Program", true)!
            .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [args]);
    }
}
internal static partial class Program
{
    static int checks;
    static Func<object, string, string, string> get = null!;
    static Action<object, string, string, string> set = null!;
    static readonly Harmony Harness = new("local.dw2.cancellation-refunds-check");
    static readonly Random Rng = new(2928);
    static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; Console.WriteLine("PASS|" + label); }
    static void Near(float actual, float expected, string label) => Check(Math.Abs(actual - expected) < .125f, $"{label}: actual={actual:R}, expected={expected:R}");
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string[] args)
    {
        var game = Path.GetFullPath(args[0]); var fixture = Path.GetFullPath(args[1]); var hash = Hash(fixture);
        RuntimeHelpers.RunModuleConstructor(typeof(Galaxy).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.UI.DWButton).Module.ModuleHandle);
        if (args.Length == 3 && args[2] == "--missing-matrix")
        {
            var rejected = false; try { Entry.Init(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && Harmony.GetPatchInfo(AccessTools.Method(typeof(Economy), nameof(Economy.Expense)))?.Owners.Contains(Entry.HarmonyId) != true,
                "missing Matrix channels reject initialization before any economic hook");
            Check(Hash(fixture) == hash, "source fixture SHA256 unchanged"); return;
        }
        if (args.Length == 3 && args[2] is not ("--remaining" or "--hooks" or "--ships")) throw new ArgumentException("unknown mode");
        var matrix = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "mods", "3833406787", "DW2PositronicMatrix.dll")).GetType("DW2PositronicMatrix.SavedData", true)!;
        get = (Func<object, string, string, string>)matrix.GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate(typeof(Func<object, string, string, string>));
        set = (Action<object, string, string, string>)matrix.GetMethod("Set", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate(typeof(Action<object, string, string, string>));
        LiveChannels.Channels["saved-data.get"] = get; LiveChannels.Channels["saved-data.set"] = set;
        Entry.Init(); Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(Economy), nameof(Economy.Expense)))!.Transpilers.Any(p => p.owner == Entry.HarmonyId), "real native economic hook installed");
        if (args.Length == 3 && args[2] == "--hooks")
        {
            var entries = new List<object>();
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method)!;
                foreach (var (kind, patches) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) })
                    foreach (var patch in patches.Where(p => p.owner == Entry.HarmonyId))
                        entries.Add(new { target = method.DeclaringType!.FullName + ":" + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")",
                            kind, effect = kind == "transpiler" ? "rewrites" : "observes", method = patch.PatchMethod.DeclaringType!.FullName + ":" + patch.PatchMethod.Name,
                            priority = patch.priority, before = patch.before, after = patch.after });
            }
            Console.WriteLine("HOOKS|" + System.Text.Json.JsonSerializer.Serialize(new { schema = 1, modVersion = typeof(Entry).Assembly.GetName().Version!.ToString(3), gameVersion = typeof(Galaxy).Assembly.GetName().Version!.ToString(), hooks = entries }));
            Check(Hash(fixture) == hash, "source fixture SHA256 unchanged"); return;
        }
        Harness.Patch(AccessTools.PropertyGetter(typeof(Galaxy), nameof(Galaxy.Rnd)), prefix: new HarmonyMethod(typeof(Program), nameof(RandomPrefix)));
        try
        {
            if (args.Length == 3 && args[2] == "--ships")
                ShipCancellationChecks(fixture);
            else if (args.Length == 3)
            {
                AutomaticPaths(game, fixture);
                GC.Collect(); GC.WaitForPendingFinalizers();
                UpgradeAndCapture(fixture);
            }
            else
            {
            var galaxy = Load(fixture); var empire = galaxy.Empires.First(e => e.IsPlayer && !e.IsPirate);
            var orb = empire.Colonies.First(o => o.Colony != null && o.Colony.Population.TotalAmount > 100000000);
            var troopDef = galaxy.TroopDefinitions.First(d => d.Type == TroopType.Infantry && d.RecruitmentCost?.Money > 0);
            empire.Economy.EditMoney = 1000000f;
            Console.WriteLine("FIXTURE|empire=" + empire.EmpireId + "|orb=" + orb.OrbId + "|troop=" + troopDef.TroopDefinitionId);
            Recruitment(galaxy, empire, orb, troopDef);
            Facilities(galaxy, empire, orb);
            Extended(game, fixture, galaxy, empire, orb, troopDef);
            Corruption(fixture, troopDef.TroopDefinitionId);
            }
        }
        finally { Check(Hash(fixture) == hash, "source fixture SHA256 unchanged"); }
        Console.WriteLine($"PASS|{checks} native integration assertions");
    }
    static bool RandomPrefix(ref Random __result) { __result = Rng; return false; }
    static Galaxy Load(string path) => Load(path, out _);
    static Galaxy Load(string path, out GameGalaxyData data)
    {
        var host = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
        typeof(GameBase).GetField("<Content>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, new ContentManager(new DatabaseFileProviderService(null)));
        Galaxy.Game = host; Galaxy.AllowNewTasks = false; Galaxy.LoadSensitiveRenderingPaused = true;
        using var reader = new BinaryReader(File.OpenRead(path));
        var galaxy = new Galaxy().ReadFromStream(reader, out data, out var errors); Check(errors.Count == 0, "native fixture load");
        Hydrate(galaxy);
        return galaxy;
    }
    static void Hydrate(Galaxy galaxy)
    {
        galaxy.RefreshSystemArray();Galaxy.ComponentsStatic=galaxy.Components;Galaxy.ResearchProjectsStatic=galaxy.ResearchProjects;Galaxy.PlanetaryFacilitiesStatic=galaxy.PlanetaryFacilities;
        foreach(var ship in galaxy.Ships)
        {
            ship.SetGalaxy(galaxy);
            if(ship.GetEmpireCached(galaxy)!=null){ship.UpdateBonuses();ship.RegenerateSummary();}
        }
    }
    static Troop Recruit(Galaxy g, Empire e, Orb orb, TroopDefinition def, Troop? clone = null)
    {
        var prior = g.Troops.ToHashSet(ReferenceEqualityComparer.Instance);
        e.ExecuteTaskRecruitTroops(orb, def, clone!);
        var troop = g.Troops.Single(t => !prior.Contains(t));
        Check(troop.Recruiting && troop.Readiness == 0, "native new recruit starts at zero training");
        return troop;
    }
    static void Disband(Empire e, params Troop[] troops)
    {
        var list = new TroopList(); foreach (var troop in troops) list.Add(troop); e.ExecuteTaskDisbandTroop(list);
    }
    static void Recruitment(Galaxy g, Empire e, Orb orb, TroopDefinition def)
    {
        foreach (var progress in new[] { 0f, .25f })
        {
            var start = e.Economy.Money; var troop = Recruit(g, e, orb, def); var paid = start - e.Economy.Money;
            Check(paid > 0 && Ledger.Candidate(troop, e) != null, "actual paid recruit bound to native object");
            troop.Readiness = progress;
            var before = e.Economy.Money; var privateBefore = e.Economy.PrivateMoney;
            Disband(e, troop); Near(e.Economy.Money - before, paid * (1 - progress), "recruit proportional native refund");
            Near(e.Economy.PrivateMoney, privateBefore, "private economy untouched");
            before = e.Economy.Money; Disband(e, troop); Near(e.Economy.Money, before, "stale repeated troop cancellation pays zero");
        }
        var first = Recruit(g, e, orb, def); var firstPaid = Ledger.Candidate(first, e)!.Payment.Paid;
        var second = Recruit(g, e, orb, def); var secondPaid = Ledger.Candidate(second, e)!.Payment.Paid; second.Readiness = .5f;
        var beforeGroup = e.Economy.Money; Disband(e, first, second); Near(e.Economy.Money - beforeGroup, firstPaid + secondPaid * .5f, "group cancellation handles separate payments once");
        var grant = TroopDefinition.GenerateTroop(g, e, orb.Colony!, def, false);
        Check(grant != null && Ledger.Candidate(grant, e) == null, "unpaid native generated recruit has no refund record");
        var beforeGrant = e.Economy.Money; Disband(e, grant!); Near(e.Economy.Money, beforeGrant, "free recruit cancellation pays zero");
        var completed = Recruit(g, e, orb, def); completed.Recruiting = false; completed.Readiness = .25f;
        var beforeComplete = e.Economy.Money; Disband(e, completed); Near(e.Economy.Money, beforeComplete, "wounded completed troop pays zero");
        var death = Recruit(g, e, orb, def); var beforeDeath = e.Economy.Money; death.CompleteTeardown(g); Near(e.Economy.Money, beforeDeath, "non-command troop destruction pays zero");
    }
    static void Facilities(Galaxy g, Empire e, Orb orb)
    {
        var def = g.PlanetaryFacilities.First(d => d.BuildCost?.Money > 0 && !d.IsWonder && d.ColonyInfluenceFloor <= 0 &&
            d.MaximumBuildCountPerEmpire <= 0 && d.MaximumBuildCountPerGalaxy <= 0 && orb.Facilities.CountByDefinition(d.PlanetaryFacilityDefinitionId, false) == 0);
        foreach (var progress in new[] { 0f, .25f })
        {
            var before = e.Economy.Money; var facility = e.BuildFacility(orb, def, g.StarDate, 0, 1, true, "Refund check");
            Check(facility != null, "native paid facility creation"); var paid = before - e.Economy.Money;
            Check(paid > 0 && Ledger.Candidate(facility!, e) != null, "actual paid facility bound to native object");
            facility!.ConstructionProgress = progress; before = e.Economy.Money;
            e.ExecuteTaskScrapPlanetaryFacility(orb, facility.PlanetaryFacilityId);
            Near(e.Economy.Money - before, paid * (1 - progress), "facility proportional native refund");
            before = e.Economy.Money; e.ExecuteTaskScrapPlanetaryFacility(orb, facility.PlanetaryFacilityId);
            Near(e.Economy.Money, before, "stale facility cancellation pays zero");
        }
        var free = e.BuildFacility(orb, def, g.StarDate, 0, 1, false, "Free check")!;
        Check(Ledger.Candidate(free, e) == null, "free building has no paid eligibility");
        var cash = e.Economy.Money; e.ExecuteTaskScrapPlanetaryFacility(orb, free.PlanetaryFacilityId); Near(e.Economy.Money, cash, "free facility cancellation pays zero");
    }
    static void Corruption(string fixture, short definition)
    {
        var g = Load(fixture); var e = g.Empires.First(e => e.IsPlayer && !e.IsPirate); var orb = e.Colonies.First(o => o.Colony != null);
        // Separate object not yet bound, with a genuinely malformed Matrix header.
        var independent = new Galaxy(); independent.GlobalVariables = new Dictionary<string, object>();
        set(independent, "cancellation-refunds", "state.head", "corrupt");
        _ = Ledger.For(independent); Ledger.For(independent).Save();
        Check(get(independent, "cancellation-refunds", "state.head") == "corrupt", "corrupt history is preserved and does not block native save preparation");
    }
}
