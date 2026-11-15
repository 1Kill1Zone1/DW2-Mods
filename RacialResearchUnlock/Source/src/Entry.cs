using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Sprites;

namespace RacialResearchUnlock;

public static class Entry
{
    private const long ThresholdInclusive = 3_000_000_000L;
    private const int RefreshMilliseconds = 2_000;
    private const string Owner = "racial-research-unlock";
    private const string KeyPrefix = "unlocked-races.";
    private static readonly ConditionalWeakTable<Galaxy, Campaign> Campaigns = new();
    private static readonly ConditionalWeakTable<ResearchTree, TreeRevision> TreeRevisions = new();
    private static readonly ConcurrentDictionary<string, byte> Reported = new();
    [ThreadStatic] private static Empire? currentEmpire;
    [ThreadStatic] private static int nativeCallDepth;
    [ThreadStatic] private static HashSet<List<byte>>? renderFilteredLists;

    public static void Init()
    {
        var harmony = new Harmony("local.dw2.racial-research-unlock");
        foreach (var method in typeof(ResearchSystem).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(m => m.Name is "CanEmpireResearch" or "CanEmpireResearchFailFast"))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Entry), nameof(EnterResearchGate)), finalizer: new HarmonyMethod(typeof(Entry), nameof(ExitResearchGate)));
        MethodInfo? performResearch = AccessTools.Method(typeof(Empire), "PerformResearch", new[] { typeof(float), typeof(long) });
        if (performResearch != null)
            harmony.Patch(performResearch, prefix: new HarmonyMethod(typeof(Entry), nameof(EnterEmpireResearch)), finalizer: new HarmonyMethod(typeof(Entry), nameof(ExitResearchGate)));
        MethodInfo? updateResearch = AccessTools.Method(typeof(ResearchSystem), "Update", new[] { typeof(Galaxy), typeof(Empire), typeof(Race), typeof(EmpirePolicy), typeof(bool), typeof(bool), typeof(bool).MakeByRefType() });
        if (updateResearch != null)
            harmony.Patch(updateResearch, postfix: new HarmonyMethod(typeof(Entry), nameof(ResearchUpdated)));

        MethodInfo projectRaceGate = AccessTools.Method(typeof(ResearchProject), nameof(ResearchProject.CanRaceResearch), new[] { typeof(Race) })
            ?? throw new MissingMethodException("ResearchProject.CanRaceResearch(Race)");
        harmony.Patch(projectRaceGate, postfix: new HarmonyMethod(typeof(Entry), nameof(ProjectRacePostfix)));
        foreach (MethodInfo method in typeof(ResearchSystem).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(m => m.Name == nameof(ResearchSystem.CanRaceResearch) && m.GetParameters().Length >= 2 &&
                                 m.GetParameters()[0].ParameterType == typeof(byte) && m.GetParameters()[1].ParameterType == typeof(ResearchProject)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Entry), nameof(StaticRacePrefix)));
        MethodInfo? instanceRace = AccessTools.Method(typeof(ResearchSystem), nameof(ResearchSystem.CanRaceResearch), new[] { typeof(Race), typeof(ResearchProject) });
        if (instanceRace != null)
            harmony.Patch(instanceRace, postfix: new HarmonyMethod(typeof(Entry), nameof(SystemRacePostfix)));

        MethodInfo? treeUpdate = AccessTools.Method(typeof(ResearchTree), nameof(ResearchTree.Update), new[] { typeof(float), typeof(Stride.Core.Mathematics.Point) });
        if (treeUpdate != null)
            harmony.Patch(treeUpdate, postfix: new HarmonyMethod(typeof(Entry), nameof(ResearchTreeUpdated)));
        MethodInfo? treeRender = AccessTools.Method(typeof(ResearchTree), nameof(ResearchTree.Render), new[] { typeof(Stride.Rendering.RenderDrawContext), typeof(SpriteBatch), typeof(Point), typeof(bool) });
        if (treeRender != null)
            harmony.Patch(treeRender, prefix: new HarmonyMethod(typeof(Entry), nameof(EnterResearchRender)), postfix: new HarmonyMethod(typeof(Entry), nameof(DrawFrontierLabels)), finalizer: new HarmonyMethod(typeof(Entry), nameof(ExitResearchRender)), transpiler: new HarmonyMethod(typeof(Entry), nameof(FilterResearchRenderRaceLists)));

        bool coherent = InstallCoherentCompatibility(harmony);
        Console.WriteLine($"Racial Research Unlock ready 0.2.1: permanent owned-colony race unlocks at least {ThresholdInclusive:N0}; Coherent R&D compatibility={(coherent ? "applied" : "not present")}");
    }

    private static bool InstallCoherentCompatibility(Harmony harmony)
    {
        Type? type = AccessTools.TypeByName("DW2CoherentResearch.ResearchBlock");
        MethodInfo? work = type == null ? null : AccessTools.Method(type, "Work", new[] { typeof(Empire) });
        if (work != null)
        {
            harmony.Patch(work, prefix: new HarmonyMethod(typeof(Entry), nameof(EnterCoherentWork)), finalizer: new HarmonyMethod(typeof(Entry), nameof(ExitResearchGate)));
            return true;
        }
        return false;
    }

    private static void EnterResearchGate(object[] __args, out Scope __state)
    {
        Empire? empire = __args.OfType<Empire>().FirstOrDefault();
        __state = Enter(empire);
    }

    private static void EnterEmpireResearch(Empire __instance, out Scope __state) => __state = Enter(__instance);

    private static void EnterCoherentWork(Empire empire, out Scope __state) => __state = Enter(empire);

    private static void ExitResearchGate(Scope __state) => __state.Dispose();

    private static Scope Enter(Empire? empire)
    {
        Empire? previous = currentEmpire;
        currentEmpire = empire;
        try
        {
            if (empire?.Galaxy != null)
                Ensure(empire.Galaxy, empire);
        }
        catch (Exception error)
        {
            Report("research eligibility refresh", error);
        }
        return new Scope(previous);
    }

    private sealed class Scope(Empire? previous) : IDisposable
    {
        public void Dispose() => currentEmpire = previous;
    }

    private static void ResearchTreeUpdated(ResearchTree __instance)
    {
        try
        {
            if (__instance.Galaxy != null && __instance.Empire != null)
            {
                Ensure(__instance.Galaxy, __instance.Empire);
                if (!__instance.Initialized) return;
                UnlockState state = Campaigns.GetValue(__instance.Galaxy, static g => new Campaign(g)).For(__instance.Empire);
                long revision = Interlocked.Read(ref state.VisibilityRevision);
                TreeRevision view = TreeRevisions.GetOrCreateValue(__instance);
                if (ReferenceEquals(view.Galaxy, __instance.Galaxy) && view.EmpireId == __instance.Empire.EmpireId && view.Revision == revision) return;
                // The game's row cache is independent of Visible; only the UI thread rebuilds native layout.
                __instance.UpdateProjects();
                __instance.RefreshSuggestedProject();
                view.Galaxy = __instance.Galaxy; view.EmpireId = __instance.Empire.EmpireId; view.Revision = revision;
            }
        }
        catch (Exception error)
        {
            Report("research tree refresh", error);
        }
    }

    private static void ResearchUpdated(Galaxy galaxy, Empire empire)
    {
        try
        {
            if (empire != null)
            {
                UnlockState state = Campaigns.GetValue(galaxy, static g => new Campaign(g)).For(empire);
                if (Reconcile(galaxy, empire, state)) InvalidateCoherentResearchBlock(empire);
            }
        }
        catch (Exception error) { Report("post-completion frontier refresh", error); }
    }

    private static void EnterResearchRender(ResearchTree __instance, out HashSet<List<byte>>? __state)
    {
        __state = renderFilteredLists;
        renderFilteredLists = [];
        if (__instance.Galaxy == null || __instance.Empire == null) return;
        try
        {
            foreach (ResearchProject project in __instance.Empire.Research.Projects)
                if (IsFrontier(__instance.Galaxy, __instance.Empire, project)) renderFilteredLists.Add(project.DisallowedRaceIds);
        }
        catch (Exception error)
        {
            renderFilteredLists.Clear();
            Report("research render eligibility", error);
        }
    }

    private static Exception? ExitResearchRender(Exception? __exception, HashSet<List<byte>>? __state)
    {
        renderFilteredLists = __state;
        return __exception;
    }

    private static List<byte> FilterDisallowedRaceIds(List<byte> original) => renderFilteredLists?.Contains(original) == true ? [] : original;

    private static IEnumerable<CodeInstruction> FilterResearchRenderRaceLists(IEnumerable<CodeInstruction> instructions)
    {
        FieldInfo? field = AccessTools.Field(typeof(ResearchProject), nameof(ResearchProject.DisallowedRaceIds));
        MethodInfo? getter = AccessTools.PropertyGetter(typeof(ResearchProject), nameof(ResearchProject.DisallowedRaceIds));
        bool LoadsRestrictions(CodeInstruction instruction) =>
            (field != null && instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, field)) ||
            (getter != null && instruction.Calls(getter));
        MethodInfo filter = AccessTools.Method(typeof(Entry), nameof(FilterDisallowedRaceIds))!;
        var code = instructions.ToList();
        if (!code.Any(LoadsRestrictions))
            throw new InvalidOperationException("ResearchTree.Render no longer reads racial restrictions; unlock UI patch must be reviewed.");
        foreach (CodeInstruction instruction in code)
        {
            yield return instruction;
            if (LoadsRestrictions(instruction))
                yield return new CodeInstruction(OpCodes.Call, filter);
        }
    }

    private static void DrawFrontierLabels(ResearchTree __instance, SpriteBatch spriteBatch)
    {
        try
        {
            if (!__instance.Initialized || !__instance.Visible || __instance.Galaxy == null ||
                __instance.Empire?.Research == null || spriteBatch == null || __instance.Size == Vector2.Zero)
                return;
            var viewport = __instance.CalculateAbsoluteRectangle();
            var font = __instance.FontNormalScaled;
            float margin = __instance.MarginScaled;
            float width = __instance.ProjectDrawSizeScaled.X - 2 * margin;
            float height = font.Size * 3.1f;
            if (width <= 0 || height >= __instance.ProjectDrawSizeScaled.Y) return;
            foreach (ResearchProject project in __instance.Empire.Research.Projects)
            {
                if (!IsFrontier(__instance.Galaxy, __instance.Empire, project) || !__instance.CheckShowProject(project))
                    continue;
                Vector2 card = __instance.CalculateProjectPosition(project);
                var label = new RectangleF(card.X + margin,
                    card.Y + __instance.ProjectDrawSizeScaled.Y - margin - height, width, height);
                if (label.Left < viewport.Left || label.Right > viewport.Right ||
                    label.Top < viewport.Top || label.Bottom > viewport.Bottom)
                    continue;
                DrawingHelper.DrawStringDropShadowWordWrapWithSize(spriteBatch, "Additional Unlock Available", font,
                    Color.Gold, Color.Black, label.Left, label.Top, TextAlignment.Left, width, 2);
            }
        }
        catch (Exception error) { Report("research unlock labels", error); }
    }

    private static void ProjectRacePostfix(ResearchProject __instance, Race race, ref bool __result)
    {
        if (__result || nativeCallDepth != 0 || currentEmpire?.Galaxy == null || race?.RaceId != currentEmpire.DominantRaceId)
            return;
        try
        {
            __result = CanUseUnlockedRace(currentEmpire.Galaxy, currentEmpire, __instance);
        }
        catch (Exception error)
        {
            Report("project race gate", error);
        }
    }

    private static void SystemRacePostfix(Race race, ResearchProject project, ref bool __result)
    {
        if (__result || nativeCallDepth != 0 || currentEmpire?.Galaxy == null || race?.RaceId != currentEmpire.DominantRaceId)
            return;
        try
        {
            __result = CanUseUnlockedRace(currentEmpire.Galaxy, currentEmpire, project);
        }
        catch (Exception error)
        {
            Report("research system race gate", error);
        }
    }

    private static void StaticRacePrefix(ref byte raceId, ResearchProject project)
    {
        if (nativeCallDepth != 0 || currentEmpire?.Galaxy == null || raceId != currentEmpire.DominantRaceId)
            return;
        try
        {
            if (NativeCanRaceResearch(raceId, project))
                return;
            if (IsFrontier(currentEmpire.Galaxy, currentEmpire, project) &&
                TryQualifiedRace(currentEmpire.Galaxy, currentEmpire, project, out byte qualified))
                raceId = qualified;
        }
        catch (Exception error)
        {
            Report("static race gate", error);
        }
    }

    private static void Ensure(Galaxy galaxy, Empire empire)
    {
        // Empire 0 is the shared independent-colony umbrella, not a normal empire.
        if (empire.EmpireId <= 0 || empire.IsPirate || empire.IsLonePirate || empire.Research?.Projects == null)
            return;
        Campaign campaign = Campaigns.GetValue(galaxy, static g => new Campaign(g));
        UnlockState state = campaign.For(empire);
            bool reconcile = true;
        lock (state.Gate)
        {
            long now = Environment.TickCount64;
            if (state.LastRefresh != 0 && now - state.LastRefresh < RefreshMilliseconds)
                return;
            state.LastRefresh = now;
            var newlyQualified = new HashSet<byte>();
            foreach (Population population in empire.GetColonyRaces())
            {
                if (population.Amount >= ThresholdInclusive && !state.Races.Contains(population.RaceId))
                    newlyQualified.Add(population.RaceId);
            }
            if (newlyQualified.Count > 0)
            {
                var persisted = new HashSet<byte>(state.Races);
                persisted.UnionWith(newlyQualified);
                // Never grant an in-memory unlock that failed to enter the campaign save data.
                if (!campaign.Store(empire, persisted))
                    throw new InvalidOperationException("Positronic Matrix did not retain racial research unlock data");
                state.Races.UnionWith(newlyQualified);
                Console.WriteLine($"Racial Research Unlock: persisted empire {empire.EmpireId} ({empire.Name}) races [{string.Join(',', newlyQualified.Order())}]");
                state.InitialReconciled = false;
                reconcile = true;
            }
            if (reconcile)
            {
                // Serialize the one-off activation with unlock persistence. A failure stays pending for retry.
                if (Reconcile(galaxy, empire, state)) InvalidateCoherentResearchBlock(empire);
                state.InitialReconciled = true;
            }
        }
    }

    private static bool CanUseUnlockedRace(Galaxy galaxy, Empire empire, ResearchProject project)
    {
        return IsFrontier(galaxy, empire, project) && TryQualifiedRace(galaxy, empire, project, out _);
    }

    private static bool TryQualifiedRace(Galaxy galaxy, Empire empire, ResearchProject project, out byte qualified)
    {
        qualified = 0;
        if (empire.EmpireId <= 0 || empire.IsPirate || empire.IsLonePirate || !IsRaciallyBlockedFor(empire, project))
            return false;
        Campaign campaign = Campaigns.GetValue(galaxy, static g => new Campaign(g));
        byte[] races = campaign.For(empire).Snapshot();
        foreach (byte raceId in races)
            if (NativeCanRaceResearch(raceId, project))
            {
                qualified = raceId;
                return true;
            }
        return false;
    }

    private static bool IsFrontier(Galaxy galaxy, Empire empire, ResearchProject? project)
    {
        if (project?.Definition == null || project.Researched || !project.Definition.EnabledByDefault) return false;
        Campaign campaign = Campaigns.GetValue(galaxy, static g => new Campaign(g));
        return campaign.For(empire).ContainsFrontier(project.ResearchProjectId);
    }

    private static short[] FrontierForCheck(Galaxy galaxy, Empire empire) => Campaigns.GetValue(galaxy, static g => new Campaign(g)).For(empire).Frontier.ToArray();

    private static bool Reconcile(Galaxy galaxy, Empire empire, UnlockState state)
    {
        if (empire.EmpireId <= 0 || empire.IsPirate || empire.IsLonePirate || empire.Research?.Projects == null) return false;
        byte[] races = state.Snapshot();
        var frontier = new HashSet<short>();
        bool visibilityChanged = false;
        foreach (ResearchProject project in empire.Research.Projects.GetReadOnlySpan().ToArray())
        {
            if (project?.Definition == null || project.Researched || NativeCanRaceResearch(empire.DominantRaceId, project) ||
                !project.Definition.EnabledByDefault || !TryQualifiedRaceUnscoped(races, project) ||
                !empire.Research.CheckAllPrerequisitesResearched(project, considerProjectsInQueue: false) ||
                !empire.Research.CheckProjectRequirements(galaxy, empire, project.Definition.AdditionalRequirements))
                continue;
            frontier.Add(project.ResearchProjectId);
        }
        bool changed;
        lock (state.Gate)
        {
            changed = !state.Frontier.SetEquals(frontier);
            state.Frontier = frontier;
        }
        foreach (ResearchProject project in empire.Research.Projects.GetReadOnlySpan().ToArray())
        {
            if (project?.Definition == null || project.Researched || !project.Definition.EnabledByDefault || NativeCanRaceResearch(empire.DominantRaceId, project) || !TryQualifiedRaceUnscoped(races, project)) continue;
            bool available = frontier.Contains(project.ResearchProjectId);
            if (empire.Research.ResearchQueue.ContainsId(project.ResearchProjectId)) continue;
            visibilityChanged |= project.Visible != available;
            project.Enabled = available;
            project.Visible = available;
        }
        if (visibilityChanged) Interlocked.Increment(ref state.VisibilityRevision);
        return changed;
    }

    private static bool TryQualifiedRaceUnscoped(IEnumerable<byte> races, ResearchProject project) => races.Any(raceId => NativeCanRaceResearch(raceId, project));

    private static bool IsRaciallyBlockedFor(Empire empire, ResearchProject project)
    {
        if (project.AllowedRaceIds is { Count: > 0 })
            return true;
        return project.DisallowedRaceIds?.Contains(empire.DominantRaceId) == true;
    }

    private static bool NativeCanRaceResearch(byte raceId, ResearchProject project)
    {
        nativeCallDepth++;
        try { return ResearchSystem.CanRaceResearch(raceId, project); }
        finally { nativeCallDepth--; }
    }

    private static void InvalidateCoherentResearchBlock(Empire empire)
    {
        // Coherent R&D caches the computed lineage block by empire. A new permanent race unlock changes its tree.
        try
        {
            Type? type = AccessTools.TypeByName("DW2CoherentResearch.ResearchBlock");
            object? cache = type?.GetField("state", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            Type[]? arguments = cache?.GetType().GetGenericArguments();
            if (cache == null || arguments is not { Length: 2 }) return;
            object key = Convert.ChangeType(empire.EmpireId, arguments[0]);
            MethodInfo? remove = cache.GetType().GetMethod("TryRemove", new[] { arguments[0], arguments[1].MakeByRefType() });
            remove?.Invoke(cache, [key, null]);
        }
        catch (Exception error)
        {
            Report("Coherent R&D cache invalidation", error);
        }
    }

    private static void Report(string where, Exception error)
    {
        if (Reported.TryAdd(where, 0))
        {
            string text = $"Racial Research Unlock: {where}: {error}";
            System.Diagnostics.Trace.WriteLine(text);
            Console.WriteLine(text);
        }
    }

    private sealed class Campaign(Galaxy galaxy)
    {
        private readonly Galaxy galaxy = galaxy;
        private readonly ConcurrentDictionary<short, UnlockState> states = new();

        public UnlockState For(Empire empire) => states.GetOrAdd(empire.EmpireId, _ => Load(empire));

        private UnlockState Load(Empire empire)
        {
            var state = new UnlockState();
            string? saved = SavedData.Get(galaxy, Owner, KeyPrefix + empire.EmpireId);
            state.Races.UnionWith(Parse(saved));
            return state;
        }

        private HashSet<byte> Parse(string? saved)
        {
            var races = new HashSet<byte>();
            if (saved == null || saved.Length == 0) return races;
            foreach (string item in saved.Split(','))
                if (!byte.TryParse(item, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out byte race) ||
                    galaxy.Races.GetById(race) == null || !races.Add(race))
                    throw new InvalidDataException("Invalid permanent racial research record; original value retained without partial loading or overwrite.");
            return races;
        }

        public bool Store(Empire empire, HashSet<byte> races)
        {
            // Also guard a cached campaign if the backing record became unreadable after initial load.
            _ = Parse(SavedData.Get(galaxy, Owner, KeyPrefix + empire.EmpireId));
            string value = string.Join(',', races.Order());
            SavedData.Set(galaxy, Owner, KeyPrefix + empire.EmpireId, value);
            return SavedData.Get(galaxy, Owner, KeyPrefix + empire.EmpireId) == value;
        }
    }

    private sealed class UnlockState
    {
        public readonly object Gate = new();
        public readonly HashSet<byte> Races = [];
        public long LastRefresh;
        public bool InitialReconciled;
        public long VisibilityRevision;
        public HashSet<short> Frontier = [];
        public byte[] Snapshot() { lock (Gate) return Races.ToArray(); }
        public bool ContainsFrontier(short id) { lock (Gate) return Frontier.Contains(id); }
    }

    private sealed class TreeRevision { public Galaxy? Galaxy; public short EmpireId = -1; public long Revision = -1; }

    private static class SavedData
    {
        private const int Contract = 1;
        private static Func<object, string, string, string>? getter;
        private static Action<object, string, string, string>? setter;
        private static long retryAt;

        public static string? Get(Galaxy galaxy, string owner, string key)
        {
            var channels = Bind();
            if (channels?.getter == null)
                throw new InvalidOperationException("Positronic Matrix is required for permanent racial research unlocks");
            string? value = channels.Value.getter(galaxy, owner, key);
            if (value == null && galaxy.GlobalVariables.ContainsKey("mod." + owner + "." + key))
                throw new InvalidDataException("Permanent racial research record exists but could not be read; original value retained.");
            return value;
        }
        public static void Set(Galaxy galaxy, string owner, string key, string value)
        {
            var channels = Bind();
            if (channels?.setter == null)
                throw new InvalidOperationException("Positronic Matrix is required for permanent racial research unlocks");
            channels.Value.setter(galaxy, owner, key, value);
        }

        private static (Func<object, string, string, string>? getter, Action<object, string, string, string>? setter)? Bind()
        {
            if (getter != null && setter != null) return (getter, setter);
            if (Environment.TickCount64 < Interlocked.Read(ref retryAt)) return null;
            try
            {
                Type? live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")?.GetType("DW2ModLauncher.Loader.LiveChannels");
                MethodInfo? get = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, binder: null,
                    types: [typeof(string), typeof(string), typeof(int), typeof(Type)], modifiers: null);
                if (get == null) return null;
                string consumer = typeof(Entry).Assembly.GetName().Name ?? Owner;
                getter = (Func<object, string, string, string>?)get.Invoke(null, [consumer, "saved-data.get", Contract, typeof(Func<object, string, string, string>)]);
                setter = (Action<object, string, string, string>?)get.Invoke(null, [consumer, "saved-data.set", Contract, typeof(Action<object, string, string, string>)]);
                return (getter, setter);
            }
            catch
            {
                Interlocked.Exchange(ref retryAt, Environment.TickCount64 + RefreshMilliseconds);
                return null;
            }
        }
    }
}
