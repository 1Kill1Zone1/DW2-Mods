using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using LocalModUi;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static DWButton? surveyPickerButton, surveyQueueButton, surveyNextPage;
    private static readonly List<DWButton> surveyRows = new();
    private static bool surveyQueueOpen;
    private static int surveyPage;
    private const int SurveyPageSize = 4;

    private static void CreateSurveyControls(WaypointToolMenu menu)
    {
        surveyPickerButton = CreateTransitControl("GalaxyWaypoints_PrioritizeSurvey", StartSurveyPicker);
        surveyPickerButton.Text = "Prioritize Survey: choose a star"; menu.AddControl(surveyPickerButton);
        surveyQueueButton = CreateTransitControl("GalaxyWaypoints_SurveyQueue", (_, _) =>
        {
            if (!TransitUiActive()) return;
            surveyQueueOpen = !surveyQueueOpen; analysisOpen = false; transitMenuSystem = null;
        });
        menu.AddControl(surveyQueueButton);
        for (int i = 0; i < SurveyPageSize; i++)
        {
            var row = CreateTransitControl("GalaxyWaypoints_SurveyPriority_" + i, CancelSurveyPriority);
            surveyRows.Add(row); menu.AddControl(row);
        }
        surveyNextPage = CreateTransitControl("GalaxyWaypoints_SurveyPage", (_, _) =>
        {
            if (!TransitUiActive()) return;
            surveyPage++; UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
        });
        menu.AddControl(surveyNextPage);
    }

    private static void StartSurveyPicker(object? sender, DWEventArgs args)
    {
        if (!TransitUiActive() || sender is not DWButton { Visible: true, Enabled: true } ||
            toolGalaxy == null || GetCampaign(toolGalaxy) is not { SurveyFault: null } campaign ||
            MapUiGate.CurrentRenderer is not { SceneRenderType: SceneRenderType.Galaxy } renderer) return;
        CloseWaypointMenu();
        campaign.SurveyPicker = true;
        renderer.SetBriefMessage("Select a visible star for priority survey. Unknown names remain hidden. Escape cancels.", 12);
    }

    private static void CancelSurveyPriority(object? sender, DWEventArgs args)
    {
        if (!TransitUiActive() || sender is not DWButton { Visible: true, Enabled: true, ExtraClickData: int systemId } ||
            toolGalaxy == null || GetCampaign(toolGalaxy) is not { } campaign) return;
        // Serialize cancellation with dispatch, without holding the queue's own gate across native calls.
        lock (campaign.SurveyDispatchGate) campaign.Survey.Remove(systemId);
        UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
    }

    private static string SurveySystemLabel(Campaign campaign, int id)
    {
        if (!campaign.Observer.Galaxy.Systems.TryGetValue(id, out var system)) return "Unavailable system";
        var visibility = campaign.Observer.VisibilityMap?.GetSystemVisibilityStatus(id);
        return visibility is VisibilityStatus.Explored or VisibilityStatus.Visible ? system.Name : "Unexplored star";
    }

    private static void CancelSurveyPicker()
    {
        if (toolGalaxy != null && Campaigns.TryGetValue(toolGalaxy, out var campaign)) campaign.SurveyPicker = false;
    }
}
