using DistantWorlds.UI;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static DWButton? analyzeNetworkButton, analysisReportButton, analysisPageButton;
    private static readonly List<DWButton> analysisRows = new();
    private static bool analysisOpen;
    private static int analysisPage;

    private static bool DiagnosticAnalysisRequested(Campaign campaign) =>
        campaign.DisplayMode == WaypointDisplayMode.AllDebug &&
        (campaign.ActivePoints.Length == 0 || campaign.Selection is { } selected && !campaign.ContainsActive(selected.Point));

    private static bool CanAnalyzeNetwork(Campaign campaign) => campaign.Analysis?.Running == true ||
        (DiagnosticAnalysisRequested(campaign) ? campaign.DisplayPoints.Length > 0 : campaign.ActivePoints.Length > 0);

    private static bool TryGetDisplayedAnalysis(NetworkAnalysis? report, out NetworkExample example, out int index, out int start, out int count)
    {
        example = null!; index = start = count = 0;
        if (report == null || report.Stale || report.Examples.Count == 0) return false;
        foreach (var item in report.Examples) count += Math.Max(1, (item.SharedNodes.Length + 1) / 2);
        int requested = ((analysisPage % count) + count) % count;
        foreach (var item in report.Examples)
        {
            int pages = Math.Max(1, (item.SharedNodes.Length + 1) / 2);
            if (requested < pages) { example = item; start = requested * 2; return true; }
            requested -= pages; index++;
        }
        return false;
    }

    private static void CreateAnalysisControls(WaypointToolMenu menu)
    {
        analyzeNetworkButton = CreateTransitControl("GalaxyWaypoints_AnalyzeNetwork", (_, _) =>
        {
            if (!TransitUiActive() || toolGalaxy == null || GetCampaign(toolGalaxy) is not { } campaign) return;
            if (campaign.Analysis is { Running: true } running) { running.Running = false; running.Cancelled = true; }
            else
            {
                bool diagnostic = DiagnosticAnalysisRequested(campaign);
                Waypoint? focus = campaign.Selection?.Point;
                campaign.Analysis = new NetworkAnalysis(campaign, diagnostic, focus);
            }
            analysisOpen = true; surveyQueueOpen = false; transitMenuSystem = null; analysisPage = 0;
        });
        menu.AddControl(analyzeNetworkButton);
        analysisReportButton = CreateTransitControl("GalaxyWaypoints_AnalysisReport", (_, _) =>
        {
            if (!TransitUiActive()) return;
            analysisOpen = !analysisOpen; surveyQueueOpen = false; transitMenuSystem = null;
        });
        menu.AddControl(analysisReportButton);
        for (int i = 0; i < 5; i++)
        {
            var row = CreateTransitControl("GalaxyWaypoints_AnalysisRow_" + i, null); row.Enabled = false;
            analysisRows.Add(row); menu.AddControl(row);
        }
        analysisPageButton = CreateTransitControl("GalaxyWaypoints_AnalysisPage", (_, _) =>
        {
            if (!TransitUiActive()) return;
            analysisPage++; UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
        });
        menu.AddControl(analysisPageButton);
    }

    private static void LayoutAnalysis(Campaign campaign, float gap, float header, float rowHeight, float width, int baseRows)
    {
        var report = campaign.Analysis;
        if (report?.Diagnostic == true && campaign.DisplayMode != WaypointDisplayMode.AllDebug) report = null;
        string[] rows = [report?.Status ?? "Analyze the existing empire-wide network", "Full-fuel, functional current designs; no selected-ship assumption",
            "Current-ship fuel, threats and service remain mandatory for automation", "No verified example in this bounded scan", "Every active node and current capability profile contributes candidates"];
        if (report?.Diagnostic == true)
            rows[4] = "Selected DEBUG node is hypothetical; report-only and unavailable to ships";
        if (report?.Reference == true)
        {
            rows[1] = report.Profiles[0].Names;
            rows[2] = ReferenceParameters(report.Profiles[0]);
        }
        bool hasPage = TryGetDisplayedAnalysis(report, out var example, out int exampleIndex, out int start, out int count);
        string pageLabel = "Examples appear as analysis progresses";
        if (report != null && hasPage && !report.Stale)
        {
            analysisPage %= count;
            var profile = report.Profiles[example.Profile];
            rows[1] = $"{SurveySystemLabel(campaign, example.From)} → {SurveySystemLabel(campaign, example.To)}";
            rows[2] = example.NewRoute ? $"Before: no fuel-feasible route; after: {example.Seconds:N0} s" :
                $"Before {example.BeforeSeconds:N0} s → after {example.Seconds:N0} s; saves {example.SecondsSaved:N0} s";
            rows[3] = profile.Reference ? $"{profile.Names}: {ReferenceParameters(profile)}" : $"Full fuel / functional: {profile.Names}";
            rows[4] = (report.Focus is { } focus ? $"Without / with {NodeId(campaign, focus)}. " : "Without / with network. ") +
                "Nodes: " + string.Join(" → ", example.SharedNodes.Skip(start).Take(2).Select(p => NodeId(campaign, p)));
            pageLabel = $"Example {exampleIndex + 1}/{report.Examples.Count}; nodes {start + 1}–{Math.Min(start + 2, example.SharedNodes.Length)}/{example.SharedNodes.Length} — next";
        }
        for (int i = 0; i < rows.Length; i++)
        {
            PlaceMenuRow(analysisRows[i], null, new(gap, header + (baseRows + i) * rowHeight), new(width - 2 * gap, rowHeight));
            analysisRows[i].Text = rows[i];
        }
        PlaceMenuRow(analysisPageButton!, null, new(gap, header + (baseRows + 5) * rowHeight), new(width - 2 * gap, rowHeight));
        analysisPageButton!.Enabled = count > 1;
        analysisPageButton.Text = pageLabel;
    }
}
