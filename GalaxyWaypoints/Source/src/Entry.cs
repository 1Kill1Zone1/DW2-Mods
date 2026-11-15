using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace GalaxyWaypoints;

[SupportedOSPlatform("windows7.0")]
public static partial class Entry
{
    private static readonly ConditionalWeakTable<Galaxy, Campaign> Campaigns = new();
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DW2GalaxyWaypoints");
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "data", "Logs", "galaxy-waypoints.log");
    private static readonly object LogGate = new();
    private static readonly ConcurrentDictionary<ShipRole, int> LoggedRoutes = new();
    private const string WaypointShortcut = "Ctrl+Alt+left click";
    [ThreadStatic] private static bool skipWaypointRouting;
    private static int errorsLogged;

    public static void Init()
    {
        SaveStorage.RequireChannels();
        MethodInfo path = AccessTools.Method(typeof(Empire), nameof(Empire.FindBestJumpPathToDestination), new[]
        {
            typeof(StellarObject), typeof(float), typeof(float), typeof(bool), typeof(Location), typeof(Location),
            typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(Point), typeof(Point)
        }) ?? throw new MissingMethodException("Empire.FindBestJumpPathToDestination");
        MethodInfo update = AccessTools.Method(typeof(ScaledRenderer), nameof(ScaledRenderer.Update))
            ?? throw new MissingMethodException("ScaledRenderer.Update");
        MethodInfo draw = AccessTools.Method(typeof(ScaledRenderer), nameof(ScaledRenderer.Draw))
            ?? throw new MissingMethodException("ScaledRenderer.Draw");
        MethodInfo mouse = AccessTools.Method(typeof(ScaledRenderer), nameof(ScaledRenderer.DoMouseDown))
            ?? throw new MissingMethodException("ScaledRenderer.DoMouseDown");
        var harmony = new Harmony("local.dw2.galaxy-waypoints");
        harmony.Patch(path, postfix: new HarmonyMethod(typeof(Entry), nameof(ChooseWaypoint)) { priority = Priority.Last, after = ["pathfindingfix.dw2mod"] });
        harmony.Patch(update, postfix: new HarmonyMethod(typeof(Entry), nameof(UpdateMap)));
        harmony.Patch(draw, postfix: new HarmonyMethod(typeof(Entry), nameof(DrawMap)));
        harmony.Patch(mouse, prefix: new HarmonyMethod(typeof(Entry), nameof(ClickWaypoint)));
        InstallStormSafety(harmony);
        InstallEnvironmentSafety(harmony);
        InstallTrafficTracking(harmony);
        InstallTransitRouting(harmony);
        InstallExplorationTransit(harmony);
        InstallSurveyDispatch(harmony);
        InstallTransitButton(harmony);
        InstallWaypointLifetime(harmony);
        InstallSaveStorage(harmony);
        Log($"Ready 0.12.1. Two-click exact system-pair diagnostics and compact native route cards share one CURRENT/GEN result, complete corridor and discovery anchors. Saved networks are not automatically regenerated. Native star actions, survey completion, transit and environmental eligibility remain enabled. Data is stored through Positronic Matrix. {WaypointShortcut} toggles a waypoint.");
    }

    private static void ChooseWaypoint(Empire __instance, StellarObject startPoint, float jumpRange,
        float jumpRangeOverride, bool removeStartSystem, Location? startLocation, Location? destinationLocation,
        ref float totalDistance, ref float totalTime, Point startPointOutsideSystem, Point destinationPointOutsideSystem,
        ref List<int>? __result)
    {
        try
        {
            if (skipWaypointRouting || startPoint is not Ship { IsBase: false, IsFighter: false } ship || ship.GetEmpire() != __instance ||
                startLocation == null || destinationLocation is not { IsTemporary: false } destination ||
                startLocation.SystemId == destination.SystemId || destinationPointOutsideSystem != Point.Zero ||
                ship.Summary is not { } summary || !Campaigns.TryGetValue(__instance.Galaxy, out Campaign? campaign))
                return;

            if (__instance.IsPlayer && ship.Mission?.IsManuallyAssigned != true &&
                ship.MissionAssignmentType != ShipMissionAssignmentType.Manual && destinationLocation != null &&
                Unsafe(UnsafeEnvironment(ship, __instance.Galaxy), Coordinates(destinationLocation), ExitMargin(ship, destinationLocation)))
            { __result = null; totalDistance = totalTime = float.MaxValue; return; }
            Waypoint[] points = campaign.ActivePoints;
            if (points.Length == 0)
                return;

            double range = Math.Min(jumpRange, summary.HyperDriveJumpRange);
            if (jumpRangeOverride > 0)
                range = Math.Min(range, jumpRangeOverride);
            double minimum = Math.Max(1, ship.CalculateHyperjumpThreshold());
            TravelTiming timing = TravelTiming.For(ship);
            double fuelRange = timing.PoweredRange;
            if (!(range >= minimum))
                return;

            Galaxy galaxy = __instance.Galaxy;
            var from = startPointOutsideSystem != Point.Zero
                ? new Waypoint(startPointOutsideSystem.X, startPointOutsideSystem.Y)
                : ship.LocationId == startLocation.LocationId
                    ? new Waypoint(ship.GalaxyX, ship.GalaxyY)
                    : new Waypoint(startLocation.GalaxyX, startLocation.GalaxyY);
            var to = new Waypoint(destination.GalaxyX, destination.GalaxyY);
            double jumpCost = Math.Max(0, summary.HyperDriveJumpInitiationTime) * Math.Max(0, summary.HyperDriveSpeed);
            double directDistance = RouteChoice.Distance(from, to);
            bool directInRange = RouteChoice.CanJump(directDistance, minimum, range);
            bool hasOrdinaryPath = __result is { Count: > 0 } && totalTime is > 0 and < 1e10f;
            if (!hasOrdinaryPath && !directInRange && (__instance.PathFindingDangerousSystems is not { } dangerous ||
                (uint)destination.SystemId >= (uint)dangerous.Length || dangerous[destination.SystemId]))
                return;

            var anchors = new List<RouteAnchor> { new(startLocation.LocationId, from) };
            if (hasOrdinaryPath)
            {
                foreach (int id in __result!)
                {
                    if (id == destination.LocationId)
                        break;
                    if (id == anchors[^1].LocationId)
                        continue;
                    if (galaxy.Locations.GetById(id) is not { } hop)
                        continue;
                    anchors.Add(new(id, Coordinates(hop)));
                }
            }
            anchors.Add(new(destination.LocationId, to));

            Storm[]? storms = null;
            double margin = ExitMargin(ship, null);
            var roleBlocks = __instance.IsPlayer ? campaign.Transit?.For(ship, ship.Mission) : null;
            WaypointPlan? choice = WaypointRouting.Find(anchors, points, hasOrdinaryPath, minimum, range,
                fuelRange, jumpCost, point => !Unsafe(storms ??= UnsafeEnvironment(ship, galaxy), point, margin)
                    && (roleBlocks is not { Count: > 0 } || !IsBlockedCoordinate(galaxy, point, roleBlocks)),
                (a, b) => LineCost(galaxy, a, b), allowOrdinaryResult: true, estimate: timing.Seconds);
            if (choice is not { } best)
                return;

            var route = new List<int>(best.Nodes.Length);
            foreach (var node in best.Nodes.Skip(removeStartSystem ? 1 : 0))
            {
                int id = node.LocationId;
                if (id < 0)
                {
                if (campaign.ResolveLocation(galaxy, node.Point) is not { } waypoint) return;
                    id = waypoint.LocationId;
                }
                route.Add(id);
            }
            if (route.Count > byte.MaxValue)
                return;

            bool changed = __result == null || !__result.SequenceEqual(route);
            if (changed) __result = route;
            totalDistance = (float)best.Distance;
            totalTime = (float)best.Cost;
            if (changed && __instance.IsPlayer && LoggedRoutes.AddOrUpdate(ship.Role, 1, (_, count) => count + 1) <= 12)
                Log($"Calculated route {ship.Role} #{ship.ShipId}: ({from.X},{from.Y}) via " +
                    $"{string.Join(" -> ", best.Nodes.Where(n => n.LocationId < 0).Select(n => $"({n.Point.X},{n.Point.Y})"))} " +
                    $"to {destination.Name}; hops=[{string.Join(",", route)}].");
        }
        catch (Exception ex)
        {
            ReportError("Route", ex);
        }
    }

    private static double LineCost(Galaxy galaxy, Waypoint start, Waypoint end) =>
        PathFindingSystem.CalculateLineCost(new Vector2(start.X, start.Y), new Vector2(end.X, end.Y), galaxy);

    private static void UpdateMap(ScaledRenderer __instance, DWGame ____Game)
    {
        try
        {
            DWGame game = ____Game;
            if (game?.Galaxy is not { } galaxy || __instance.PlayerEmpire is not { IsPlayer: true } empire)
                return;

            if (GetCampaign(galaxy) is { } campaign)
            {
                RefreshGeneratedDiscoveries(galaxy, campaign, force: false);
                RefreshSurveyUi(campaign);
                AdvanceNetworkAnalysis(campaign, __instance);
                AdvancePairAnalysis(campaign);
                AdvanceUsefulGeneration(campaign);
            }
        }
        catch (Exception ex)
        {
            ReportError("Waypoint storage", ex);
            if (Environment.TickCount64 >= Interlocked.Read(ref nextStorageWarning))
            {
                Interlocked.Exchange(ref nextStorageWarning, Environment.TickCount64 + 15_000);
                __instance.SetBriefMessage("Galaxy Waypoints: save data unavailable. Check Positronic Matrix and galaxy-waypoints.log. Saving is blocked to protect your data.", 12);
            }
        }
    }

    private static void AdvanceNetworkAnalysis(Campaign campaign, ScaledRenderer renderer)
    {
        if ((renderer.SceneRenderType is SceneRenderType.Galaxy or SceneRenderType.System or SceneRenderType.Location) && MapOverlayUiActive())
            campaign.Analysis?.Tick(campaign);
    }

    private static void RefreshGeneratedDiscoveries(Galaxy galaxy, Campaign campaign, bool force)
    {
        // UpdateMap is the UI owner: scanning and persistence stay out of route workers, and native messages are sent after state locks.
        _ = campaign.ObserveOwnShipPresence(force);
        GeneratedWaypoint[] pending = campaign.PendingDiscoveryNotifications();
        if (pending.Length == 0) return;
        // Native SendMessageToEmpire drops the message while EvaluationQueue is unavailable during load; retain pending state.
        if (campaign.Observer.EvaluationQueue == null) return;
        if (!campaign.CanRetryDiscoveryNotification(pending)) return;
        try
        {
            var systems = new List<StarSystem>();
            foreach (GeneratedWaypoint node in pending)
                if (galaxy.Systems.TryGetValue(node.AnchorSystemId, out StarSystem? system) && system != null)
                    systems.Add(system);
            if (systems.Count == 0) return;
            string title = "Potential Hyperway Discovered";
            string body = systems.Count == 1
                ? $"During reconnaissance of the {systems[0].Name} system, our navigators triangulated a promising hyperspace waypoint. It may offer an alternative course through this region. The coordinates have been added to our navigation charts."
                : $"During reconnaissance of the {string.Join(", ", systems.Select(system => system.Name))} systems, our navigators triangulated promising hyperspace waypoints. They may offer alternative courses through this region. The coordinates have been added to our navigation charts.";
            campaign.Observer.SendMessageToEmpire(campaign.Observer, EmpireMessageType.Exploration, EmpireMessageSubType.Undefined,
                title, body, systems[0].Star, DrawDetailType.Undefined, EmpireMessagePriority.Normal);
            campaign.MarkDiscoveryNotified(pending);
        }
        catch (Exception ex)
        {
            // Leave Notified untouched: the UI retries this unchanged batch after a short backoff, with no duplicate after success.
            campaign.DeferDiscoveryNotification(pending);
            ReportError("Hyperway discovery notification", ex);
        }
    }

    private static void DrawMap(ScaledRenderer __instance, DWGame ____Game, GraphicsContext graphicsContext)
    {
        try
        {
            DWGame game = ____Game;
            if (game == null)
                return;
            if (game.Galaxy is not { } galaxy || !WaypointMapActive(__instance) ||
                __instance.CurrentCamera is not { } camera || !Campaigns.TryGetValue(galaxy, out Campaign? campaign) ||
                MapProjection.FromMap(__instance, camera, __instance.Width, __instance.Height) is not { } map)
                return;

            DrawWaypointOverlay(game, graphicsContext, __instance.Width, __instance.Height, map, campaign, galaxy);
        }
        catch (Exception ex)
        {
            ReportError("Map markers", ex);
        }
    }

    private static Waypoint? FindMarker(Waypoint[] points, MapProjection? projection, Point mouse)
    {
        if (projection is not { } map)
            return null;

        Waypoint? closest = null;
        float bestDistance = 14 * 14;
        foreach (Waypoint point in points)
        {
            if (!map.TryProject(point, out Vector2 marker) || WaypointMarkerCovered(marker))
                continue;
            float x = marker.X - mouse.X;
            float y = marker.Y - mouse.Y;
            float distance = x * x + y * y;
            if (distance < bestDistance)
            {
                closest = point;
                bestDistance = distance;
            }
        }
        return closest;
    }

    private static void ReportError(string operation, Exception ex)
    {
        if (Interlocked.Increment(ref errorsLogged) <= 8)
            Log($"{operation}: {ex}");
    }

    private static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private enum WaypointDisplayMode { Discovered, AllDebug, Hidden }

    private sealed class Campaign
    {
        public const int MaxPoints = 32;

        private readonly SaveStorage storage;
        private readonly SaveStorage surveyStorage;
        private readonly Galaxy galaxy;
        private readonly object stateGate = new();
        private readonly object locationGate = new();
        private readonly object activeGate = new();
        private readonly Dictionary<Waypoint, Location> locations = new();
        private readonly Dictionary<Waypoint, Location> autoLocations = new();
        private volatile Waypoint[] points;
        private GeneratedWaypoint[] generated = [];
        private int[] discoveredAnchors = [];
        private readonly HashSet<int> visitedGeneratedAnchors = new();
        private readonly HashSet<int> discoveredGeneratedAnchors = new();
        private readonly HashSet<int> notifiedGeneratedAnchors = new();
        private ActiveSnapshot? activeSnapshot;
        private Waypoint[]? allMapPoints;
        public volatile WaypointDisplayMode DisplayMode;
        public string? GenerationStatus;
        public GenerationJob? Generation;
        public GenerationProof[] GenerationProofs = [];
        public string GenerationStamp = "";
        public long NextGenerationValidation;
        public bool GenerationCurrent;
        private GeneratedWaypoint[]? pendingNotificationSnapshot;
        private long activeVersion;
        private long nextPresenceScan;
        private long nextNotificationRetry;
        private int[] failedNotificationAnchors = [];

        private sealed record ActiveSnapshot(long Version, Waypoint[] Points);

        public Campaign(Galaxy galaxy, Empire observer, string? legacyFolder = null)
        {
            Observer = observer;
            this.galaxy = galaxy;
            storage = new SaveStorage(galaxy, observer.EmpireId);
            // A separate prefix survives saves made by older waypoint readers, which only touch state.<empire>.
            surveyStorage = new SaveStorage(galaxy, observer.EmpireId, "survey");
            Survey = new SurveyQueue([]);
            try
            {
                var survey = surveyStorage.Read<SurveySave>() ?? new SurveySave();
                survey.Validate(galaxy);
                Survey = new SurveyQueue(survey.Systems);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or FormatException)
            {
                SurveyFault = "Survey data unavailable â€” original retained; check galaxy-waypoints.log";
                ReportError("Survey data retained; waypoint navigation remains available", ex);
            }
            WaypointSave? saved = storage.Read();
            if (saved == null)
            {
                int seed = galaxy.GameStartSettings?.RandomSeed ?? 0;
                string file = Path.Combine(legacyFolder ?? DataFolder, $"{seed}_{galaxy.SizeX}_{galaxy.SizeY}_{galaxy.TimeStart.Ticks}_{observer.EmpireId}.json");
                // Legacy files are read only. Their current state cannot reconstruct earlier saves.
                Waypoint[] legacyPoints = File.Exists(file)
                    ? JsonSerializer.Deserialize<Waypoint[]>(File.ReadAllText(file)) ?? throw new InvalidDataException("Empty legacy waypoints") : [];
                var legacyTransit = new TransitRestrictions(Path.ChangeExtension(file, ".transit.json"));
                var legacyTraffic = new TrafficLedger(Path.ChangeExtension(file, ".traffic.json"), galaxy.StarDate, legacyPoints);
                saved = new WaypointSave { Points = legacyPoints, Transit = legacyTransit.Export(), Traffic = legacyTraffic.Export(galaxy.StarDate) };
                if (File.Exists(file) || File.Exists(Path.ChangeExtension(file, ".transit.json")) || File.Exists(Path.ChangeExtension(file, ".traffic.json")))
                    Log("Imported legacy waypoint state in memory; original files retained. It will travel with the next game save.");
            }
            saved.Validate(galaxy);
            points = saved.Points;
            Generated = saved.Generated;
            GenerationProofs = saved.GenerationProofs;
            GenerationStamp = saved.GenerationStamp;
            if (saved.Version < 3)
            {
                // The old rule used full survey. Preserve nodes that were already usable without inventing retroactive events.
                foreach (int anchor in Generated.Where(node => node.IsExplored(galaxy, observer)).Select(node => node.AnchorSystemId))
                {
                    discoveredGeneratedAnchors.Add(anchor);
                    notifiedGeneratedAnchors.Add(anchor);
                }
            }
            else
            {
                visitedGeneratedAnchors.UnionWith(saved.VisitedGeneratedAnchors);
                discoveredGeneratedAnchors.UnionWith(saved.DiscoveredGeneratedAnchors);
                notifiedGeneratedAnchors.UnionWith(saved.NotifiedGeneratedAnchors);
            }
            NormalizeGeneratedState();
            Traffic = new TrafficLedger(saved.Traffic, galaxy.StarDate, points.Concat(Generated.Select(node => node.Point)).ToArray(), stateGate);
            Transit = new TransitRestrictions(saved.Transit, stateGate);
        }

        public void Persist()
        {
            lock (stateGate)
            {
                var state = new WaypointSave { Points = points.ToArray(), Generated = Generated, VisitedGeneratedAnchors = visitedGeneratedAnchors.Order().ToArray(),
                    DiscoveredGeneratedAnchors = discoveredGeneratedAnchors.Order().ToArray(), NotifiedGeneratedAnchors = notifiedGeneratedAnchors.Order().ToArray(),
                    Transit = Transit!.Export(), Traffic = Traffic!.Export(galaxy.StarDate),
                    GenerationProofs = GenerationProofs, GenerationStamp = GenerationStamp };
                state.Validate(galaxy);
                storage.Write(state);
                if (SurveyFault == null)
                {
                    var survey = Survey.Export();
                    survey.Validate(galaxy);
                    surveyStorage.Write(survey);
                }
            }
        }

        public Empire Observer { get; }
        public SurveyQueue Survey { get; }
        public string? SurveyFault { get; }
        public bool SurveyPicker;
        // This is a UI-only star choice.  It never creates a ship order and is discarded with its campaign.
        public bool TransitPicker;
        public StarSystem? TransitSelection;
        public readonly object SurveyDispatchGate = new();
        public int SurveyCursor;
        public SurveyRank? SurveyRank;
        public long NextSurveyRefresh;
        public NetworkAnalysis? Analysis;
        public NetworkAnalysis? PairAnalysis;
        public UsefulNetwork? PairForecast;
        public string? PairStamp, PairError;
        public long NextPairValidation;
        public Waypoint[]? PairPoints;
        public int? RouteFrom, RouteTo;
        public int? RoutePendingFrom;
        public int RoutePicker, RouteProfile, RouteCardPage;
        public bool RouteCardOpen, RoutePairView, RouteShowGen = true, RouteDetails;
        public TrafficLedger? Traffic { get; }
        public TransitRestrictions? Transit { get; }
        public volatile MapSelection? Selection;
        public Waypoint[] Points => points;
        public GeneratedWaypoint[] Generated
        {
            get => Volatile.Read(ref generated);
            private set => Volatile.Write(ref generated, value);
        }
        public Waypoint[] ActivePoints
        {
            get
            {
                while (true)
                {
                    ActiveSnapshot? cached = Volatile.Read(ref activeSnapshot);
                    if (cached is not null && cached.Version == Volatile.Read(ref activeVersion))
                        return cached.Points;

                    lock (activeGate)
                    {
                        cached = Volatile.Read(ref activeSnapshot);
                        if (cached is not null && cached.Version == Volatile.Read(ref activeVersion))
                            return cached.Points;

                        // These arrays are replaced, never mutated. Do not take stateGate here: RecordPassage holds it
                        // while checking activity, and SetPoints already takes stateGate before locationGate.
                        long version = Volatile.Read(ref activeVersion);
                        Waypoint[] manual = points;
                        GeneratedWaypoint[] generatedNodes = Generated;
                        int[] discovered = Volatile.Read(ref discoveredAnchors);
                        Waypoint[] active = manual.Concat(generatedNodes.Where(node => Array.IndexOf(discovered, node.AnchorSystemId) >= 0).Select(node => node.Point)).Distinct().ToArray();
                        if (version != Volatile.Read(ref activeVersion))
                            continue;
                        var refreshed = new ActiveSnapshot(version, active);
                        Volatile.Write(ref activeSnapshot, refreshed);
                        return refreshed.Points;
                    }
                }
            }
        }
        // Rendering never feeds the route planner: hidden diagnostic nodes remain unusable.
        public Waypoint[] DisplayPoints
        {
            get
            {
                if (DisplayMode == WaypointDisplayMode.Hidden) return [];
                if (DisplayMode != WaypointDisplayMode.AllDebug) return ActivePoints;
                lock (stateGate) return allMapPoints ??= points.Concat(Generated.Select(node => node.Point)).Distinct().ToArray();
            }
        }
        public string DisplayStatus
        {
            get
            {
                int open = Generated.Count(node => ContainsActive(node.Point));
                if (GenerationStatus != null) return GenerationStatus;
                return DisplayMode == WaypointDisplayMode.AllDebug
                    ? $"Auto: {Generated.Length} / open {open} / hidden {Generated.Length-open}; manual {points.Length}"
                    : $"Manual: {points.Length} / auto available: {open}";
            }
        }
        public bool ContainsActive(Waypoint point) => ActivePoints.Contains(point);
        public GeneratedWaypoint? FindGenerated(Waypoint point)
        {
            foreach (var node in Generated)
                if (node.Point == point) return node;
            return null;
        }
        public void SetGenerated(GeneratedWaypoint[] generated)
        {
            lock (stateGate)
            {
                // Generated nodes never displace an existing manual point; this also makes a re-generated candidate safe to save.
                Generated = generated.Where(node => !points.Contains(node.Point)).ToArray();
                allMapPoints = null;
                pendingNotificationSnapshot = null;
                NormalizeGeneratedState();
                Interlocked.Increment(ref activeVersion);
            }
            Volatile.Write(ref activeSnapshot, null);
        }

        public void ReplaceCertifiedGenerated(GeneratedWaypoint[] nodes,GenerationProof[] proofs,string stamp)
        {
            lock(stateGate)
            {
                // Validate the complete replacement before publishing any part or clearing old traffic.
                var next=new WaypointSave{Points=points,Generated=nodes,GenerationProofs=proofs,GenerationStamp=stamp};
                next.Validate(galaxy);
                var old=Generated;
                SetGenerated(nodes);
                GenerationProofs=proofs;GenerationStamp=stamp;Analysis=null;
                NextGenerationValidation=0;GenerationCurrent=true;
                foreach(var node in old)Traffic!.Remove(node.Point);
            }
        }

        public GeneratedWaypoint[] ObserveOwnShipPresence(bool force)
        {
            long now = Environment.TickCount64;
            if (!force && now < Interlocked.Read(ref nextPresenceScan)) return [];
            Interlocked.Exchange(ref nextPresenceScan, now + 1_000);
            var present = new HashSet<int>();
            foreach (Ship ship in Observer.Ships)
            {
                if (ship == null || ship.IsDestroyed || ship.IsHyperjumping() || ship.EmpireId != Observer.EmpireId) continue;
                if (!IsPhysicalSystemLocation(galaxy, galaxy.Locations.GetById(ship.LocationId))) continue;
                if (ship.GetSystem(galaxy, findNearestIfNotInSystem: false) is { } system) present.Add(system.SystemId);
            }
            lock (stateGate)
            {
                visitedGeneratedAnchors.UnionWith(present);
                var anchors = Generated.Select(node => node.AnchorSystemId).ToHashSet();
                if (anchors.Count == 0) return [];
                var unlocked = present.Where(anchors.Contains).ToArray();
                if (unlocked.Length == 0) return [];
                PublishDiscovered(anchors);
                return Generated.Where(node => unlocked.Contains(node.AnchorSystemId)).ToArray();
            }
        }

        public void ObserveOwnArrival(Ship ship, Location arrival)
        {
            if (ship == null || ship.IsDestroyed || ship.EmpireId != Observer.EmpireId || !IsPhysicalSystemLocation(galaxy, arrival)) return;
            lock (stateGate)
            {
                if (!visitedGeneratedAnchors.Add(arrival.SystemId)) return;
                PublishDiscovered(Generated.Select(node => node.AnchorSystemId).ToHashSet());
            }
        }

        public GeneratedWaypoint[] PendingDiscoveryNotifications()
        {
            lock (stateGate)
                return pendingNotificationSnapshot ??= Generated.Where(node => Array.IndexOf(discoveredAnchors, node.AnchorSystemId) >= 0 && !notifiedGeneratedAnchors.Contains(node.AnchorSystemId)).ToArray();
        }

        public void MarkDiscoveryNotified(IEnumerable<GeneratedWaypoint> nodes)
        {
            lock (stateGate)
            {
                notifiedGeneratedAnchors.UnionWith(nodes.Select(node => node.AnchorSystemId));
                pendingNotificationSnapshot = null;
                failedNotificationAnchors = [];
                nextNotificationRetry = 0;
            }
        }

        public bool CanRetryDiscoveryNotification(GeneratedWaypoint[] nodes)
        {
            int[] anchors = nodes.Select(node => node.AnchorSystemId).Order().ToArray();
            lock (stateGate)
                return Environment.TickCount64 >= nextNotificationRetry || !failedNotificationAnchors.SequenceEqual(anchors);
        }

        public void DeferDiscoveryNotification(GeneratedWaypoint[] nodes)
        {
            lock (stateGate)
            {
                failedNotificationAnchors = nodes.Select(node => node.AnchorSystemId).Order().ToArray();
                nextNotificationRetry = Environment.TickCount64 + 5_000;
            }
        }

        private void NormalizeGeneratedState()
        {
            var anchors = Generated.Select(node => node.AnchorSystemId).ToHashSet();
            visitedGeneratedAnchors.RemoveWhere(anchor => !galaxy.Systems.ContainsKey(anchor));
            discoveredGeneratedAnchors.IntersectWith(anchors);
            notifiedGeneratedAnchors.IntersectWith(anchors);
            PublishDiscovered(anchors);
        }

        private void PublishDiscovered(HashSet<int> anchors)
        {
            discoveredGeneratedAnchors.UnionWith(visitedGeneratedAnchors.Where(anchors.Contains));
            int[] next = discoveredGeneratedAnchors.Order().ToArray();
            if (discoveredAnchors.SequenceEqual(next)) return;
            Volatile.Write(ref discoveredAnchors, next);
            pendingNotificationSnapshot = null;
            Interlocked.Increment(ref activeVersion);
            Volatile.Write(ref activeSnapshot, null);
        }

        public static bool InBounds(Galaxy galaxy, Waypoint point) =>
            point.X > 0 && point.Y > 0 && point.X < galaxy.SizeX && point.Y < galaxy.SizeY;

        public void SetPoints(Waypoint[] updated)
        {
            lock (stateGate)
            {
                // A visible generated node belongs to the network, not to Ctrl+Alt manual editing. Ignore only the attempted overlap.
                Waypoint[] normalized = updated.Where(point => !Generated.Any(node => node.Point == point)).ToArray();
                if (normalized.Length > MaxPoints || normalized.Distinct().Count() != normalized.Length || normalized.Any(p => !InBounds(galaxy, p)))
                    throw new InvalidDataException("Invalid waypoints");
                lock (locationGate)
                {
                    foreach (Waypoint removed in points.Except(normalized))
                    {
                        locations.Remove(removed);
                        Traffic?.Remove(removed);
                    }
                    points = normalized;
                    allMapPoints = null;
                    Interlocked.Increment(ref activeVersion);
                }
            }
            Volatile.Write(ref activeSnapshot, null);
        }

        public void RecordPassage(Waypoint point, int shipId, long starDate, bool freighter, int empireId)
        {
            lock (stateGate)
            {
                if (ContainsActive(point))
                    Traffic?.Record(point, shipId, starDate, freighter, empireId);
            }
        }

        public Location? ResolveLocation(Galaxy galaxy, Waypoint point, bool automaticallyChosen = false)
        {
            // Do this before locationGate so a hidden generated node never creates a visible temporary location.
            if (!automaticallyChosen && !ContainsActive(point))
                return null;
            lock (locationGate)
            {
                Dictionary<Waypoint, Location> cache = automaticallyChosen ? autoLocations : locations;
                if (!cache.TryGetValue(point, out Location? location) || location.IsDestroyed ||
                    !ReferenceEquals(galaxy.Locations.GetById(location.LocationId), location))
                {
                    location = galaxy.Locations.FirstOrDefault(candidate => candidate != null && candidate.IsTemporary &&
                        !candidate.IsDestroyed && candidate.Name == RouteNodeName && Coordinates(candidate) == point)
                        ?? galaxy.CreateTemporaryLocation(point.X, point.Y);
                    if (location == null || !location.IsTemporary || location.LocationId < 0 ||
                        !ReferenceEquals(galaxy.Locations.GetById(location.LocationId), location))
                    {
                        if (Interlocked.Increment(ref errorsLogged) <= 8)
                            Log($"Engine did not register waypoint location ({point.X},{point.Y}); using ordinary routes.");
                        return null;
                    }
                    if (automaticallyChosen && cache.Count >= 128)
                        cache.Clear();
                    cache[point] = location;
                }
                location.Name = RouteNodeName;
                // Cover the interval between planning and assignment, including cached-node reuse.
                NodeLeases.GetOrCreateValue(location).Until = Environment.TickCount64 + 30_000;
                return location;
            }
        }
    }
}


