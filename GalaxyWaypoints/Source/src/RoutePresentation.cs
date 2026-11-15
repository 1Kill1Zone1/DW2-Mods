using DistantWorlds.Types;

namespace GalaxyWaypoints;

public static partial class Entry
{
    // One result supplies the card, path, highlighted nodes and discovery anchors.
    private sealed record RouteView(string Mode, int From, int To, string Profile, string Parameters,
        Waypoint[] Before, Waypoint[] After, double BeforeSeconds, double AfterSeconds, string? Error = null);

    private static void StartPairAnalysis(Campaign campaign)
    {
        campaign.PairForecast?.Cancel();
        campaign.PairAnalysis = null; campaign.PairForecast = null; campaign.PairError = null;
        campaign.NextPairValidation = 0;
        campaign.RouteProfile = campaign.RouteCardPage = 0;
        if (campaign.RouteFrom is not { } from || campaign.RouteTo is not { } to) return;
        if (from == to) { campaign.PairError = "Choose two different systems"; return; }
        var galaxy = campaign.Observer.Galaxy;
        bool debug = campaign.DisplayMode == WaypointDisplayMode.AllDebug;
        campaign.PairPoints = debug ? campaign.DisplayPoints : campaign.ActivePoints;
        campaign.PairAnalysis = new(campaign, debug, null, (from, to));
        try
        {
            var hazards = ReadStorms(galaxy);
            var stars = galaxy.Systems.Values.Where(s => s?.Star != null &&
                !Unsafe(hazards, new(s.GalaxyX, s.GalaxyY), 25_000))
                .OrderBy(s => s.SystemId).GroupBy(s => new Waypoint(s.GalaxyX, s.GalaxyY)).Select(g => g.First())
                .Select(s => new RouteAnchor(s.SystemId, new(s.GalaxyX, s.GalaxyY))).ToArray();
            int a = Array.FindIndex(stars, s => s.LocationId == from), b = Array.FindIndex(stars, s => s.LocationId == to);
            if (a < 0 || b < 0) { campaign.PairError = "Reference unavailable: endpoint inside an unsafe region"; return; }
            campaign.PairStamp = GenerationGeometry(galaxy);
            campaign.PairForecast = new(stars, campaign.PairPoints.Where(p => !Unsafe(hazards, p, 25_000)).ToArray(),
                GenerationProfiles(galaxy), (x, y) => LineCost(galaxy, x, y), (a, b));
        }
        catch (Exception ex) { campaign.PairError = "Reference unavailable: " + ex.Message; }
    }

    private static void AdvancePairAnalysis(Campaign campaign)
    {
        if (!MapOverlayUiActive()) return;
        if (campaign.PairPoints != null && !ReferenceEquals(campaign.PairPoints,
            campaign.DisplayMode == WaypointDisplayMode.AllDebug ? campaign.DisplayPoints : campaign.ActivePoints))
        {
            campaign.PairForecast?.Cancel(); campaign.PairError = "Network/display changed - choose endpoint again";
            campaign.PairAnalysis = null; campaign.PairForecast = null; return;
        }
        try { campaign.PairAnalysis?.Tick(campaign); campaign.PairForecast?.Tick(); }
        catch (Exception ex)
        { campaign.PairAnalysis = null; campaign.PairForecast?.Cancel(); campaign.PairError = "Analysis failed: " + ex.Message; return; }
        // A completed reference must not survive changed geometry/definitions as a current result.
        long now = Environment.TickCount64;
        if (now < campaign.NextPairValidation) return;
        campaign.NextPairValidation = now + 1000;
        if (campaign.PairForecast is { Cancelled: false, Error: null } &&
            campaign.PairStamp != GenerationGeometry(campaign.Observer.Galaxy))
        { campaign.PairForecast.Cancel(); campaign.PairForecast = null; campaign.PairError = "Reference stale - choose endpoint again"; }
    }

    private static RouteView? DisplayedRoute(Campaign campaign)
    {
        if (campaign.RoutePicker != 0) return null;
        if (campaign.RoutePairView)
        {
            if (campaign.RouteFrom is not { } from || campaign.RouteTo is not { } to) return null;
            if (campaign.RouteShowGen)
            {
                var job = campaign.PairForecast;
                if (job is not { Complete: true, Cancelled: false, Error: null } || job.PairResults.Length == 0) return null;
                var choices = job.PairResults.OrderBy(r => !double.IsFinite(r.AfterSeconds))
                    .ThenByDescending(r => double.IsFinite(r.BeforeSeconds) && double.IsFinite(r.AfterSeconds) ? r.BeforeSeconds - r.AfterSeconds : 0).ToArray();
                var result = choices[campaign.RouteProfile % choices.Length];
                return new("GEN forecast", from, to, result.Profile.Name, CapabilityLabel(result.Profile.Capability),
                    result.Before, result.After, result.BeforeSeconds, result.AfterSeconds);
            }
            var report = campaign.PairAnalysis;
            if (report is not { Stale: false, Running: false, Cancelled: false } || report.Outcomes.Count == 0 ||
                report.Diagnostic && campaign.DisplayMode != WaypointDisplayMode.AllDebug) return null;
            var outcomes = report.Outcomes.OrderBy(o => o.Result?.NetworkWithinFuel != true)
                .ThenByDescending(o => o.Result?.Faster == true ? o.Result.BaselineSeconds - o.Result.NetworkSeconds : 0).ToArray();
            var outcome = outcomes[campaign.RouteProfile % outcomes.Length];
            var profile = report.Profiles[outcome.Profile]; var assessment = outcome.Result;
            return new(profile.Reference ? "Reference forecast" : report.Diagnostic ? "CURRENT / DEBUG forecast" : "CURRENT", from, to,
                profile.Names, ReferenceParameters(profile),
                assessment?.BaselineWithinFuel == true ? assessment.Baseline!.Nodes.Select(n => n.Point).ToArray() : [],
                assessment?.NetworkWithinFuel == true ? assessment.Network!.Nodes.Select(n => n.Point).ToArray() : [],
                assessment?.BaselineWithinFuel == true ? assessment.BaselineSeconds : double.PositiveInfinity,
                assessment?.NetworkWithinFuel == true ? assessment.NetworkSeconds : double.PositiveInfinity, outcome.Error);
        }
        if (campaign.Selection is not { } selection || !campaign.DisplayPoints.Contains(selection.Point)) return null;
        if (campaign.RouteShowGen)
        {
            if (!CurrentGenerationEvidence(campaign)) return null;
            if (campaign.GenerationProofs.FirstOrDefault(p => p.Node == selection.Point) is not { } proof) return null;
            if (campaign.DisplayMode != WaypointDisplayMode.AllDebug && proof.After.Any(p => campaign.FindGenerated(p) != null && !campaign.ContainsActive(p))) return null;
            return new("GEN forecast", proof.From, proof.To, proof.Profile.Name, CapabilityLabel(proof.Profile.Capability),
                proof.Before, proof.After, proof.BeforeSeconds < 0 ? double.PositiveInfinity : proof.BeforeSeconds, proof.AfterSeconds);
        }
        var current = campaign.Analysis;
        if (current is not { Stale: false, Cancelled: false } || current.Focus != selection.Point ||
            current.Diagnostic && campaign.DisplayMode != WaypointDisplayMode.AllDebug || current.Examples.Count == 0) return null;
        var example = current.Examples[campaign.RouteProfile % current.Examples.Count];
        var design = current.Profiles[example.Profile];
        return new(design.Reference ? "Reference forecast / without node" : current.Diagnostic ? "CURRENT / DEBUG / without node" : "CURRENT / without node",
            example.From, example.To, design.Names, ReferenceParameters(design), example.Before, example.After,
            example.NewRoute ? double.PositiveInfinity : example.BeforeSeconds, example.Seconds);
    }

    private static string CapabilityLabel(RouteCapability c) =>
        $"Jump {c.JumpRange / 1e6:0.#}M; fuel {c.PoweredRange / 1e6:0.#}M; prep {c.Preparation:0.#}s";

    private static Waypoint[] RouteNodes(Campaign campaign, RouteView? view) =>
        view?.After.Where(p => campaign.DisplayPoints.Contains(p)).Distinct().ToArray() ?? [];

    private static GeneratedWaypoint[] ClosedRouteNodes(Campaign campaign, RouteView? view) =>
        RouteNodes(campaign, view).Where(p => !campaign.ContainsActive(p))
            .Select(campaign.FindGenerated).Where(n => n.HasValue).Select(n => n!.Value).ToArray();

    private static string RouteStatus(Campaign campaign)
    {
        if (!campaign.RoutePairView)
            return campaign.RouteShowGen ? CurrentGenerationEvidence(campaign) ? "GEN unavailable; use All (DEBUG) for closed nodes" : "GEN history stale: geometry changed" :
                campaign.Analysis?.Status ?? "Select a node to analyze";
        if (campaign.RouteFrom == null || campaign.RouteTo == null) return "Choose route start and end in Tools";
        return campaign.RouteShowGen ? campaign.PairError ?? campaign.PairForecast?.Error ?? campaign.PairForecast?.Status ?? "Reference unavailable" :
            campaign.PairAnalysis?.Status ?? campaign.PairError ?? "Current profiles unavailable";
    }

    private static string[] RouteCardLines(Campaign campaign)
    {
        var view = DisplayedRoute(campaign);
        var lines = new List<string>();
        if (view == null) lines.Add(RouteStatus(campaign));
        else
        {
            lines.Add(view.Mode);
            if (!campaign.RoutePairView && campaign.Selection is { } selectedNode) lines.Add("Node: " + NodeId(campaign, selectedNode.Point));
            lines.Add($"{SurveySystemLabel(campaign, view.From)} -> {SurveySystemLabel(campaign, view.To)}");
            string Time(double seconds) => double.IsFinite(seconds) ? $"{seconds:N0}s" : "unreachable";
            lines.Add($"Without: {Time(view.BeforeSeconds)}; with: {Time(view.AfterSeconds)}");
            if (double.IsFinite(view.BeforeSeconds) && double.IsFinite(view.AfterSeconds))
                lines.Add(view.BeforeSeconds > view.AfterSeconds ? $"Saves {view.BeforeSeconds - view.AfterSeconds:N0}s ({100 * (view.BeforeSeconds - view.AfterSeconds) / view.BeforeSeconds:0.#}%)" : "No speed benefit for this profile");
            else if (double.IsFinite(view.AfterSeconds)) lines.Add("New full-fuel route");
            else lines.Add("No fuel-feasible route for this profile");
            if (view.Error != null) lines.Add(view.Error);
            var nodes = RouteNodes(campaign, view);
            lines.Add($"Jumps: {Math.Max(0, view.Before.Length - 1)} -> {Math.Max(0, view.After.Length - 1)}; nodes: {nodes.Length}");
            lines.Add("Nodes: " + (nodes.Length == 0 ? "none" : string.Join(" -> ", nodes.Select(n => NodeId(campaign, n)))));
            var closed = ClosedRouteNodes(campaign, view);
            if (view.After.Length > 0) lines.Add(closed.Length == 0 ? "All route nodes discovered" : "Visit: " + string.Join("; ", closed.Select(n => n.AnchorSystemId).Distinct().Select(id => $"{SurveySystemLabel(campaign, id)} [S-{id:X4}]")));
            if (closed.Length > 0) lines.Add("Purple: visit to open; DEBUG grants no access");
            if (campaign.RouteDetails)
            {
                lines.Add(view.Profile); lines.Add(view.Parameters);
                lines.Add("Full-fuel/functional profile; not a movement order");
                lines.Add("Gold dashed: without; cyan solid: with");
            }
        }
        if (campaign.RouteDetails && !campaign.RoutePairView && campaign.Selection is { } selected)
        {
            lines.Add(NodeId(campaign, selected.Point) + $" ({selected.Point.X}, {selected.Point.Y})");
            if (campaign.ContainsActive(selected.Point))
            {
                var traffic = campaign.Traffic?.VisibleCount(selected.Point, campaign.Observer.Galaxy.StarDate,
                    passage => VisiblePassage(passage, campaign.Observer.Galaxy, campaign.Observer)) ?? default;
                lines.Add($"Last game year: {traffic.Ships} ships; {traffic.Freighters} freighters; {traffic.Passages} passages");
            }
            if (campaign.FindGenerated(selected.Point) is { } node)
                lines.Add($"Opens at: {SurveySystemLabel(campaign, node.AnchorSystemId)} [S-{node.AnchorSystemId:X4}]");
        }
        return lines.ToArray();
    }
}
