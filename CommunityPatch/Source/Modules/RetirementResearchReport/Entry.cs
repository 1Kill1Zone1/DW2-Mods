using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DistantWorlds.Types;
using HarmonyLib;

[assembly: InternalsVisibleTo("RetirementResearchReport.Check")]

namespace RetirementResearchReport;

public static class Entry
{
    internal const string PatchId = "local.dw2.retirement-research-report";
    internal const string EngineHash = "62F03F35A7EE8BBC7BF93AC7D184D614209631A4F68C488224E6CE9C9E2A0E23";
    private const string PreviousEngineHash = "787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254";
    static readonly Harmony Hooks = new(PatchId);
    static readonly ConditionalWeakTable<Galaxy, Campaign> Campaigns = new();
    [ThreadStatic] static Context? current;
    [ThreadStatic] static Stack<Context?>? scopes;
    [ThreadStatic] static Ship? completing;
    static bool installed;

    internal sealed class Campaign
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<int, Ledger> Ships;
        internal readonly HashSet<int> Dirty = new();
        internal readonly ConcurrentQueue<int> Interruptions = new();
        internal readonly bool Enabled;
        internal long NextReview;
        internal Campaign(Galaxy galaxy)
        {
            try { Ships = Store.Load(galaxy); Enabled = true; }
            catch (Exception ex) { Ships = new(); Error("Unreadable ledger; reports disabled for this galaxy; stored data preserved", ex); }
        }
    }
    internal sealed record Context(Galaxy Galaxy, Empire Owner, Ship Ship, bool RacialBonus, Context? Previous);
    sealed record TaskScope(Campaign State, Context? Previous, int Depth, Ship? Completing);
    internal static Campaign State(Galaxy galaxy) => Campaigns.GetValue(galaxy, g => new(g));

    public static void Init()
    {
        if (installed) return;
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Ship).Assembly.Location))) is not (EngineHash or PreviousEngineHash))
            throw new NotSupportedException("Retirement Research Report requires a verified Steam beta engine.");
        Store.Require();
        try
        {
            var tasks = AccessTools.DeclaredMethod(typeof(ConstructionSystem), "DoTasks", [typeof(float), typeof(DateTime), typeof(Galaxy), typeof(Empire), typeof(Location), typeof(bool)]);
            var progress = AccessTools.DeclaredMethod(typeof(ConstructionSystem), "PerformTechProgress");
            if (tasks == null || progress == null || tasks.GetMethodBody()?.LocalVariables[34].LocalType != typeof(Ship))
                throw new NotSupportedException("Retirement observer native signatures changed");
            Hooks.Patch(tasks, prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeTasks)) { priority = Priority.First, before = ["local.dw2.advanced-construction"] },
                transpiler: Method(nameof(ObserveConstruction)), finalizer: Method(nameof(AfterTasks)));
            Hooks.Patch(progress, transpiler: Method(nameof(ObserveProgress)));
            var breakthrough = AccessTools.DeclaredMethod(typeof(Empire), "DoResearchBreakthrough",
                [typeof(ResearchProject), typeof(Ship), typeof(ConstructionType), typeof(bool), typeof(bool),
                    typeof(string), typeof(string), typeof(string), typeof(EmpireMessageViewMode), typeof(bool)])
                ?? throw new NotSupportedException("Native research breakthrough signature changed");
            Hooks.Patch(breakthrough, postfix: Method(nameof(AfterBreakthrough)));
            Hooks.Patch(AccessTools.DeclaredMethod(typeof(Ship), "DoTasks", [typeof(DateTime), typeof(Galaxy), typeof(bool)]), prefix: Method(nameof(BeforeShipTasks)));
            var mission = AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m => m.Name == "AssignMissionCore" && m.GetParameters()[0].ParameterType == typeof(ShipMission));
            Hooks.Patch(mission, postfix: Method(nameof(AfterMission)));
            var saves = AccessTools.GetDeclaredMethods(typeof(Galaxy)).Where(m => m.Name == "WriteToStream").ToArray();
            if (saves.Length == 0) throw new NotSupportedException("Native galaxy save method missing");
            foreach (var save in saves) Hooks.Patch(save,
                prefix: new HarmonyMethod(typeof(Entry), nameof(BeforeSave)) { priority = Priority.First, before = ["local.dw2.advanced-construction", "local.dw2.freight-dispatch"] },
                finalizer: Method(nameof(AfterSave)));
            Hooks.Patch(AccessTools.DeclaredMethod(typeof(Galaxy), "ReadFromStream",
                [typeof(BinaryReader), typeof(GameGalaxyData).MakeByRefType(), typeof(List<string>).MakeByRefType()]), postfix: Method(nameof(AfterLoad)));
            SavePreflight.Install(Hooks);
            ReportDialog.Install(Hooks);
            installed = true;
        }
        catch { Hooks.UnpatchAll(PatchId); throw; }
    }
    static HarmonyMethod Method(string name) => new(typeof(Entry), name);

    // Only the physical base-disassembly call sites establish an attribution context.
    internal static IEnumerable<CodeInstruction> ObserveConstruction(IEnumerable<CodeInstruction> instructions)
    {
        var input = instructions.ToList();
        var scrap = AccessTools.DeclaredMethod(typeof(ConstructionSystem), "ScrapShip");
        var reward = AccessTools.DeclaredMethod(typeof(ConstructionSystem), "PerformTechProgress");
        var teardown = AccessTools.DeclaredMethod(typeof(Ship), "CompleteTeardown", [typeof(Empire)]);
        if (input.Count(c => c.Calls(scrap)) != 1 || input.Count(c => c.Calls(reward)) != 1 || input.Count(c => c.Calls(teardown)) != 1)
            throw new NotSupportedException("Retirement observer expected one scrap, racial reward and teardown call site");
        foreach (var instruction in input)
        {
            bool isScrap = instruction.Calls(scrap), isReward = instruction.Calls(reward);
            if (isScrap || isReward)
            {
                var first = new CodeInstruction(OpCodes.Ldarg_3); first.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return first; yield return new(OpCodes.Ldarg_S, (byte)4); yield return new(OpCodes.Ldloc_S, (byte)34);
                yield return new(OpCodes.Ldarg_0); yield return new(isReward ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                yield return CodeInstruction.Call(typeof(Entry), nameof(Begin));
            }
            if (instruction.Calls(teardown))
            {
                var first = new CodeInstruction(OpCodes.Ldloc_S, (byte)34); first.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return first; yield return CodeInstruction.Call(typeof(Entry), nameof(MarkCompleting));
            }
            yield return instruction;
            if (isScrap || isReward) yield return CodeInstruction.Call(typeof(Entry), nameof(End));
            if (instruction.Calls(teardown))
            {
                yield return new(OpCodes.Ldarg_3); yield return new(OpCodes.Ldarg_S, (byte)4); yield return new(OpCodes.Ldloc_S, (byte)34);
                yield return CodeInstruction.Call(typeof(Entry), nameof(Completed));
            }
        }
    }
    internal static IEnumerable<CodeInstruction> ObserveProgress(IEnumerable<CodeInstruction> instructions)
    {
        var input = instructions.ToList();
        var field = AccessTools.Field(typeof(ResearchProject), "Progress");
        if (input.Count(c => c.opcode == OpCodes.Stfld && Equals(c.operand, field)) != 1)
            throw new NotSupportedException("Retirement observer expected exactly one native research progress write");
        foreach (var instruction in input)
        {
            if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field))
            {
                var first = new CodeInstruction(OpCodes.Ldarg_0); first.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return first; yield return new(OpCodes.Ldarg_1); yield return new(OpCodes.Ldarg_2); yield return new(OpCodes.Ldarg_S, (byte)5);
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.DeclaredMethod(typeof(Entry), nameof(WriteProgress));
            }
            yield return instruction;
        }
    }
    static void BeforeTasks(ConstructionSystem __instance, Galaxy galaxy, Empire empire, out TaskScope? __state)
    {
        __state = null;
        try
        {
            if (galaxy == null) return;
            if (empire?.IsPlayer != true)
            {
                var player = galaxy.Empires.GetPlayer();
                if (player == null || !__instance.Yards.Any(y => y != null && (int)y.ConstructionType == 4 && y.GetShipUnderConstruction(galaxy)?.EmpireId == player.EmpireId) &&
                    !__instance.WaitQueue.Any(w => w != null && (int)w.ConstructionType == 4 && galaxy.Ships.GetById(w.ShipUnderConstructionId)?.EmpireId == player.EmpireId)) return;
            }
            var state = State(galaxy); Monitor.Enter(state.Gate); __state = new(state, current, scopes?.Count ?? 0, completing);
        }
        catch (Exception ex) { Error("Start construction observation", ex); }
    }
    static void AfterTasks(Galaxy galaxy, TaskScope? __state)
    {
        if (__state == null) return;
        try
        {
            current = __state.Previous; completing = __state.Completing;
            while (scopes?.Count > __state.Depth) scopes.Pop();
            Review(galaxy, force: false);
        }
        catch (Exception ex) { Error("Finish construction observation", ex); }
        finally { Monitor.Exit(__state.State.Gate); }
    }
    internal static void Begin(Galaxy galaxy, Empire owner, Ship ship, ConstructionSystem system, bool racialBonus)
    {
        // A null scope also masks an outer scope during nested non-player/native operations.
        var previous = current; (scopes ??= new()).Push(previous); current = null;
        try
        {
            var yard = system.Owner.ResolveBase<StellarObject>(galaxy);
            var shipOwner = ship == null ? null : galaxy.Empires.GetById(ship.EmpireId);
            if (shipOwner?.IsPlayer != true || ship == null ||
                !(yard is Ship { IsBase: true } || yard is Orb { Colony: not null })) return;
            current = new(galaxy, shipOwner, ship, racialBonus, previous);
            var state = State(galaxy);
            lock (state.Gate)
            {
                if (!state.Enabled) return;
                DrainInterruptions(galaxy, state);
                if (!state.Ships.TryGetValue(ship.ShipId, out var ledger))
                    ledger = new(1, ship.ShipId, shipOwner.EmpireId, ship.Name, 1, false, false, [], [], 0);
                if (ledger.Finished || ledger.EmpireId != shipOwner.EmpireId) return;
                if (!ledger.Active) Set(galaxy, state, ledger with { Active = true, Name = ship.Name });
            }
        }
        catch (Exception ex) { Error("Start ship observation", ex); }
    }
    internal static void End() => current = scopes?.Count > 0 ? scopes.Pop() : null;
    // Keep the original assignment unconditional. Only this retirement-specific store is observed,
    // so lab progress elsewhere (including concurrent research) never enters the ledger.
    internal static void WriteProgress(ResearchProject project, float value, Galaxy galaxy, Empire owner, Ship ship, ConstructionType constructionType)
    {
        float before = project.Progress;
        project.Progress = value;
        try
        {
            var context = current;
            if (context == null || !ReferenceEquals(context.Galaxy, galaxy) || !ReferenceEquals(context.Owner, owner) ||
                !ReferenceEquals(context.Ship, ship) || (int)constructionType != 4 || !float.IsFinite(value) || value <= before) return;
            var state = State(context.Galaxy);
            lock (state.Gate)
            {
                if (!state.Enabled || !state.Ships.TryGetValue(context.Ship.ShipId, out var ledger) || !ledger.Active || ledger.Finished) return;
                float size = project.Definition.CalculateResearchSize(project.RepeatCount);
                if (!float.IsFinite(before) || before < 0 || !float.IsFinite(size) || size <= 0) return;
                var gain = new Gain(project.ResearchProjectId, project.Definition.Name, project.RepeatCount, size, before, value, false, context.RacialBonus);
                Set(context.Galaxy, state, ledger with { Gains = [.. ledger.Gains, gain] });
            }
        }
        catch (Exception ex) { Error("Record applied research; native progress retained", ex); }
    }
    static void AfterBreakthrough(Empire __instance, ResearchProject project)
    {
        try
        {
            var context = current;
            if (context == null || !ReferenceEquals(context.Owner, __instance)) return;
            var state = State(context.Galaxy);
            lock (state.Gate)
            {
                if (!state.Enabled || !state.Ships.TryGetValue(context.Ship.ShipId, out var ledger)) return;
                int index = Array.FindLastIndex(ledger.Gains, g => g.ProjectId == project.ResearchProjectId);
                if (index < 0) return;
                var gain = ledger.Gains[index];
                if (gain.After < gain.Size || !(project.Researched || project.RepeatCount > gain.Repeat)) return;
                var gains = (Gain[])ledger.Gains.Clone(); gains[index] = gain with { Completed = true };
                Set(context.Galaxy, state, ledger with { Gains = gains });
            }
        }
        catch (Exception ex) { Error("Record completed technology", ex); }
    }
    internal static void Completed(Galaxy galaxy, Empire owner, Ship ship)
    {
        try
        {
            if (!ship.IsDestroyed) return;
            var state = State(galaxy);
            lock (state.Gate)
            {
                if (!state.Enabled || !state.Ships.TryGetValue(ship.ShipId, out var ledger) || ledger.Finished ||
                    galaxy.Empires.GetById(ledger.EmpireId)?.IsPlayer != true) return;
                Queue(galaxy, state, ledger, complete: true);
            }
        }
        catch (Exception ex) { Error("Complete ship report", ex); }
        finally { completing = null; }
    }
    static void MarkCompleting(Ship ship) => completing = ship;
    static void Queue(Galaxy galaxy, Campaign state, Ledger ledger, bool complete)
    {
        if (!complete && ledger.Gains.Length == 0) { Set(galaxy, state, ledger with { Active = false }); return; }
        var notice = new Notice(ledger.NextSequence, complete, galaxy.StarDate, ledger.Gains);
        Set(galaxy, state, ledger with { Active = false, Finished = complete, Gains = [], NextSequence = checked(ledger.NextSequence + 1), Pending = [.. ledger.Pending, notice] });
    }
    static void BeforeShipTasks(Galaxy galaxy) { try { Review(galaxy, false); } catch (Exception ex) { Error("Review report delivery", ex); } }
    internal static void AfterMission(Ship __instance)
    {
        try
        {
            if (ReferenceEquals(completing, __instance) || __instance.Mission?.Type == ShipMissionType.Retire) return;
            var galaxy = __instance.GetEmpire()?.Galaxy;
            if (galaxy == null || !Campaigns.TryGetValue(galaxy, out var state) || !state.Enabled) return;
            // The native caller can hold ACM's gate. Capture the observed cancellation without waiting
            // for our gate; next Begin drains it even if the player has already resumed Retire.
            state.Interruptions.Enqueue(__instance.ShipId);
        }
        catch (Exception ex) { Error("Review interrupted retirement", ex); }
    }
    static void DrainInterruptions(Galaxy galaxy, Campaign state)
    {
        while (state.Interruptions.TryDequeue(out int id))
            if (state.Ships.TryGetValue(id, out var ledger) && ledger.Active && !ledger.Finished) Queue(galaxy, state, ledger, false);
    }
    static void Interrupt(Galaxy galaxy, Campaign state, Ship ship)
    {
        if (ReferenceEquals(completing, ship) || !state.Enabled || !state.Ships.TryGetValue(ship.ShipId, out var ledger) || !ledger.Active || ledger.Finished) return;
        if (ship.IsDestroyed || ship.EmpireId != ledger.EmpireId || ship.Mission?.Type != ShipMissionType.Retire) Queue(galaxy, state, ledger, false);
    }
    internal static void Review(Galaxy galaxy, bool force)
    {
        var state = State(galaxy);
        lock (state.Gate)
        {
            if (!state.Enabled || !force && Environment.TickCount64 < state.NextReview) return;
            state.NextReview = Environment.TickCount64 + 1000;
            DrainInterruptions(galaxy, state);
            foreach (var original in state.Ships.Values.ToArray())
            {
                try
                {
                    var ship = galaxy.Ships.GetById(original.ShipId);
                    if (original.Active && ship == null) Queue(galaxy, state, original, false);
                    else if (ship != null) Interrupt(galaxy, state, ship);
                    var ledger = state.Ships[original.ShipId];
                    if (state.Dirty.Contains(ledger.ShipId)) Persist(galaxy, state, ledger);
                    var owner = galaxy.Empires.GetById(ledger.EmpireId);
                    if (owner?.IsPlayer != true) continue;
                    foreach (var pending in ledger.Pending)
                    {
                        if (!Report.Publish(owner, ledger, pending)) break;
                        ledger = ledger with { LastSent = pending.Sequence, Pending = ledger.Pending.Where(n => n.Sequence > pending.Sequence).ToArray() };
                        Set(galaxy, state, ledger);
                        if (state.Dirty.Contains(ledger.ShipId)) break;
                    }
                }
                catch (Exception ex) { Error("Report remains pending", ex); }
            }
        }
    }
    static void Set(Galaxy galaxy, Campaign state, Ledger ledger)
    {
        state.Ships[ledger.ShipId] = ledger; state.Dirty.Add(ledger.ShipId);
        try { Persist(galaxy, state, ledger); } catch (Exception ex) { Error("Ledger persistence deferred", ex); }
    }
    static void Persist(Galaxy galaxy, Campaign state, Ledger ledger) { Store.Save(galaxy, ledger); state.Dirty.Remove(ledger.ShipId); }
    static void BeforeSave(Galaxy __instance, out Campaign? __state)
    {
        __state = null;
        if (ReferenceEquals(SavePreflight.Prepared, __instance)) return;
        var state = State(__instance); Monitor.Enter(state.Gate); __state = state;
        // Low-level callers must own their output stream. Normal game saves also preflight BEFORE opening it.
        Flush(__instance, state);
    }
    internal static void Flush(Galaxy galaxy, Campaign state)
    { DrainInterruptions(galaxy, state); foreach (int id in state.Dirty.ToArray()) Persist(galaxy, state, state.Ships[id]); }
    static void AfterSave(Campaign? __state) { if (__state != null) Monitor.Exit(__state.Gate); }
    static void AfterLoad(Galaxy __instance, Galaxy? __result, List<string>? __2)
    { if (ReferenceEquals(__instance, __result) && __2?.Count == 0) { Forget(__instance); _ = State(__instance); } }
    internal static Ledger? Snapshot(Galaxy galaxy, int shipId) { var state = State(galaxy); lock (state.Gate) return state.Ships.GetValueOrDefault(shipId); }
    internal static void Forget(Galaxy galaxy) => Campaigns.Remove(galaxy);
    static void Error(string action, Exception error) => Console.Error.WriteLine("Retirement Research Report: " + action + ": " + error.GetType().Name + ": " + error.Message);
}
