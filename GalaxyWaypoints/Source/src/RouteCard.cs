using DistantWorlds.Types;
using DistantWorlds.UI;
using LocalModUi;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static DWButton? routePairButton;
    private static readonly List<DWButton> routeCardRows = new(), routeCardButtons = new();
    private const int RoutePageLines = 7;

    private static void OpenRouteCard(Campaign campaign, bool pair)
    {
        campaign.RoutePairView = pair; campaign.RouteCardOpen = true;
        campaign.RouteDetails = false; campaign.RouteCardPage = campaign.RouteProfile = 0;
        campaign.RouteShowGen = pair || campaign.Selection is { } s && campaign.GenerationProofs.Any(p => p.Node == s.Point);
        NativeToolDock.Select(NativeToolDock.Waypoints);
    }

    private static void CreateRouteControls(WaypointToolMenu menu)
    {
        routePairButton = CreateTransitControl("GalaxyWaypoints_CompareRoute", (_, _) => BeginRoutePicker());
        menu.AddControl(routePairButton);
        for (int i = 0; i < RoutePageLines; i++)
        {
            var row = CreateTransitControl("GalaxyWaypoints_RouteLine" + i, null);
            row.Enabled = false; row.TextAlignment = TextAlignment.Left;
            routeCardRows.Add(row); menu.AddControl(row);
        }
        foreach (var action in new[] { "Mode", "Profile", "Details", "Page", "Tools", "Remove" })
        {
            string captured = action;
            var button = CreateTransitControl("GalaxyWaypoints_Route" + action, (_, _) => RouteCardAction(captured));
            routeCardButtons.Add(button); menu.AddControl(button);
        }
    }

    private static void BeginRoutePicker()
    {
        if (!TransitUiActive() || toolGalaxy == null || GetCampaign(toolGalaxy) is not { } campaign ||
            MapUiGate.CurrentRenderer?.SceneRenderType != SceneRenderType.Galaxy) return;
        campaign.RoutePicker = 1; campaign.RoutePendingFrom = null; campaign.SurveyPicker = campaign.TransitPicker = false;
        campaign.RouteCardOpen = false; HideWaypointMenu();
        MapUiGate.CurrentRenderer.SetBriefMessage("Compare route: click START, then END star (Escape/right-click cancels)", 7);
    }

    private static void CancelRoutePicker(Campaign campaign)
    { campaign.RoutePicker = 0; campaign.RoutePendingFrom = null; }

    private static bool RoutePickerClick(Campaign campaign, StarSystem? star, bool left)
    {
        if (campaign.RoutePicker == 0) return false;
        if (!left) { CancelRoutePicker(campaign); return true; }
        SelectRouteEndpoint(campaign, star); return true;
    }

    private static void SelectRouteEndpoint(Campaign campaign, StarSystem? star)
    {
        if (campaign.RoutePicker == 0 || star == null || !ReferenceEquals(campaign.Observer.Galaxy.Systems.GetById(star.SystemId), star)) return;
        if (campaign.RoutePicker == 1)
        {
            campaign.RoutePendingFrom = star.SystemId; campaign.RoutePicker = 2;
            MapUiGate.CurrentRenderer?.SetBriefMessage($"START: {SurveySystemLabel(campaign, star.SystemId)}. Now click a different END star.", 7);
            return;
        }
        if (campaign.RoutePendingFrom is not { } from || from == star.SystemId) return;
        campaign.RouteFrom = from; campaign.RouteTo = star.SystemId; CancelRoutePicker(campaign);
        StartPairAnalysis(campaign); OpenRouteCard(campaign, true);
    }

    private static void RouteCardAction(string action)
    {
        if (!TransitUiActive() || toolGalaxy == null || GetCampaign(toolGalaxy) is not { RouteCardOpen: true } campaign) return;
        switch (action)
        {
            case "Mode": campaign.RouteShowGen = !campaign.RouteShowGen; campaign.RouteProfile = campaign.RouteCardPage = 0; break;
            case "Profile": campaign.RouteProfile++; campaign.RouteCardPage = 0; break;
            case "Details": campaign.RouteDetails = !campaign.RouteDetails; campaign.RouteCardPage = 0; break;
            case "Page": campaign.RouteCardPage++; break;
            case "Tools": campaign.RouteCardOpen = false; break;
            case "Remove":
                if (!campaign.RoutePairView && campaign.Selection is { } selected && campaign.Points.Contains(selected.Point) && MapUiGate.CurrentRenderer is { } renderer)
                { RemoveWaypoint(renderer, campaign, selected.Point); campaign.RouteCardOpen = false; CloseWaypointMenu(); }
                break;
        }
        UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
    }

    private static string[] WrapRouteLines(IEnumerable<string> lines, float width)
    {
        float size = Math.Max(11, UserInterfaceHelper.CalculateScaledValue(13));
        var font = FontManager.SelectFont(size, false);
        if (font == null) return ["UI fonts unavailable - reopen this card after loading"];
        return WrapRouteText(lines, width, text => font.MeasureString(text, size).X);
    }

    private static string[] WrapRouteText(IEnumerable<string> lines, float width, Func<string, float> measure)
    {
        var result = new List<string>();
        foreach (string line in lines)
        {
            string rest = line;
            while (rest.Length > 0)
            {
                int count = rest.Length;
                while (count > 1 && measure(rest[..count]) > width) count--;
                int space = rest.LastIndexOf(' ', count - 1, count);
                if (count < rest.Length && space > 0) count = space;
                result.Add(rest[..count]); rest = rest[count..].TrimStart();
            }
        }
        return result.ToArray();
    }

    private static void LayoutRouteCard(Campaign campaign, int screenWidth, int screenHeight, float gap, float header)
    {
        var menu = waypointToolMenu!;
        float width = Math.Min(UserInterfaceHelper.CalculateScaledValue(320), screenWidth - 2 * gap);
        float height = Math.Min(UserInterfaceHelper.CalculateScaledValue(200), screenHeight - 2 * gap);
        var anchor = waypointToolButton!.CalculateAbsoluteRectangle();
        var position = new Vector2(Math.Clamp(anchor.Right - width, gap, Math.Max(gap, screenWidth - width - gap)), Math.Max(gap, anchor.Top - height - gap));
        if (campaign.Selection is { } selection && !campaign.RoutePairView && MapUiGate.CurrentRenderer is { CurrentCamera: { } camera } renderer &&
            MapProjection.FromMap(renderer, camera, screenWidth, screenHeight) is { } map && map.TryProject(selection.Point, out var marker))
        {
            if (CardAt(marker, screenWidth, screenHeight) is not { } bounds) { menu.Visible = false; return; }
            position = new(bounds.X, bounds.Y);
        }
        menu.SetSizeAndPosition(new(width, height), position);
        float footer = Math.Max(22, UserInterfaceHelper.CalculateScaledValue(25));
        float lineHeight = Math.Max(1, (height - header - footer - gap) / RoutePageLines);
        var lines = WrapRouteLines(RouteCardLines(campaign), width - 2 * gap);
        int pages = Math.Max(1, (lines.Length + RoutePageLines - 1) / RoutePageLines);
        campaign.RouteCardPage %= pages;
        for (int i = 0; i < routeCardRows.Count; i++)
        {
            PlaceMenuRow(routeCardRows[i], null, new(gap, header + i * lineHeight), new(width - 2 * gap, lineHeight));
            routeCardRows[i].Text = lines.ElementAtOrDefault(campaign.RouteCardPage * RoutePageLines + i) ?? "";
        }
        bool remove = campaign.RouteDetails && !campaign.RoutePairView && campaign.Selection is { } chosen && campaign.Points.Contains(chosen.Point);
        string[] labels = [campaign.RouteShowGen ? "GEN" : "CURRENT", "Profile", campaign.RouteDetails ? "Summary" : "Details", $"{campaign.RouteCardPage + 1}/{pages} >", "Tools", "Remove"];
        int count = remove ? 6 : 5; float buttonWidth = (width - 2 * gap) / count;
        for (int i = 0; i < count; i++)
        {
            PlaceMenuRow(routeCardButtons[i], null, new(gap + i * buttonWidth, height - footer - gap / 2), new(buttonWidth, footer));
            routeCardButtons[i].Text = labels[i]; routeCardButtons[i].Enabled = i != 3 || pages > 1;
        }
        var close = menu.Controls[0]; close.SetSizeAndPosition(new(26, 26), new(width - 30, 3));
    }
}
