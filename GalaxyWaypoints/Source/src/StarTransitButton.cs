using System.Reflection;
using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using LocalModUi;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private const string TransitButtonName = "GalaxyWaypoints_StarTransit";
    private static DWButton? waypointToolButton, starTransitButton, generateNetworkButton, displayModeButton, networkStatusRow;
    private static WaypointToolMenu? waypointToolMenu;
    private static readonly List<DWButton> transitOptions = new();
    private static StarSystem? transitMenuSystem;
    private static Galaxy? toolGalaxy;
    private static short toolObserver = -1;

    private static void InstallTransitButton(Harmony harmony)
    {
        NativeToolDock.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(UserInterfaceController), nameof(UserInterfaceController.Update)),
            prefix: new HarmonyMethod(typeof(Entry), nameof(UpdateStarTransitButton)),
            postfix: new HarmonyMethod(typeof(Entry), nameof(UpdateNativeStarActions)));
        harmony.Patch(AccessTools.Method(typeof(UserInterfaceController), nameof(UserInterfaceController.DoMouseDown)), prefix: new HarmonyMethod(typeof(Entry), nameof(ClearTransitPress)));
        harmony.Patch(AccessTools.Method(typeof(InputSystem), nameof(InputSystem.HandleKeyPress)), prefix: new HarmonyMethod(typeof(Entry), nameof(AllowWaypointToolKey)));
    }
    private static bool AllowWaypointToolKey(KeyMapping keyMapping)
    {
        if (keyMapping?.Key == Keys.Escape && MapOverlayUiActive() && toolGalaxy != null &&
            Campaigns.TryGetValue(toolGalaxy, out var campaign) && (campaign.SurveyPicker || campaign.TransitPicker || campaign.RoutePicker != 0))
        { campaign.SurveyPicker = false; campaign.TransitPicker = false; CancelRoutePicker(campaign); return false; }
        if (keyMapping?.Key != Keys.Escape || !TransitUiActive() || waypointToolMenu is not { Visible: true }) return true;
        CloseWaypointMenu(); return false;
    }
    private static void CloseWaypointMenu()
    {
        CancelSurveyPicker();
        if (toolGalaxy != null && Campaigns.TryGetValue(toolGalaxy, out var campaign))
        { campaign.TransitPicker = false; CancelRoutePicker(campaign); campaign.RouteCardOpen = false; }
        NativeToolDock.Clear(NativeToolDock.Waypoints); ClearTransitPress(); transitMenuSystem = null;
        HideWaypointMenu();
    }
    private static void HideWaypointMenu()
    {
        if (waypointToolMenu != null) { waypointToolMenu.Visible = false; MapUiGate.ReleaseHover(waypointToolMenu); }
        if (starTransitButton != null) starTransitButton.Visible = false;
        if (generateNetworkButton != null) generateNetworkButton.Visible = false;
        if (displayModeButton != null) displayModeButton.Visible = false;
        if (networkStatusRow != null) networkStatusRow.Visible = false;
        foreach (var row in transitOptions) row.Visible = false;
        if (surveyPickerButton != null) surveyPickerButton.Visible = false;
        if (surveyQueueButton != null) surveyQueueButton.Visible = false;
        if (surveyNextPage != null) surveyNextPage.Visible = false;
        foreach (var row in surveyRows) row.Visible = false;
        if (analyzeNetworkButton != null) analyzeNetworkButton.Visible = false;
        if (analysisReportButton != null) analysisReportButton.Visible = false;
        if (analysisPageButton != null) analysisPageButton.Visible = false;
        foreach (var row in analysisRows) row.Visible = false;
        if (routePairButton != null) routePairButton.Visible = false;
        foreach (var row in routeCardRows.Concat(routeCardButtons)) row.Visible = false;
    }
    private static void ToggleWaypointMenu()
    {
        if (!NativeToolDock.ToolbarAvailable()) return;
        if (NativeToolDock.Selected == NativeToolDock.Waypoints) { CloseWaypointMenu(); return; }
        NativeToolDock.Select(NativeToolDock.Waypoints);
        UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
    }
    private static void UpdateStarTransitButton(int screenWidth, int screenHeight)
    {
        try
        {
            HideWaypointMenu();
            var current = (Galaxy.Game as DWGame)?.Galaxy;
            short observer = current?.Empires.GetPlayer()?.EmpireId ?? (short)-1;
            if (!ReferenceEquals(current, toolGalaxy) || observer != toolObserver)
            {
                CloseWaypointMenu(); NativeToolDock.Detach(waypointToolButton);
                if (waypointToolMenu != null)
                {
                    UserInterfaceController.RemoveCustomControl(waypointToolMenu);
                    foreach (var control in waypointToolMenu.Controls.OfType<DWButton>()) control.ExtraClickData = null!;
                }
                toolGalaxy = current; toolObserver = observer;
                surveyQueueOpen = false; surveyPage = 0;
                analysisOpen = false; analysisPage = 0;
            }
            if (current == null || observer < 0 || !MapUiGate.TryGetContext(out _, out _))
            { CancelSurveyPicker(); NativeToolDock.Detach(waypointToolButton); ClearTransitPress(); return; }
            var campaign = GetCampaign(current);
            if (campaign == null || campaign.Observer.EmpireId != observer) { NativeToolDock.Detach(waypointToolButton); ClearTransitPress(); return; }
            waypointToolButton ??= NativeToolDock.Register(NativeToolDock.Waypoints, "Galaxy Waypoints", "Generate the galaxy network and manage transit rules for the selected star.", ToggleWaypointMenu);
            NativeToolDock.Update(waypointToolButton);
            if (!MapOverlayUiActive() || MapUiGate.CurrentRenderer?.SceneRenderType != SceneRenderType.Galaxy)
            { CancelSurveyPicker(); campaign.TransitPicker = false; CancelRoutePicker(campaign); }
            if (!TransitUiActive()) { ClearTransitPress(); return; }
            if (campaign.TransitPicker || campaign.RoutePicker != 0) { HideWaypointMenu(); return; }
            if (waypointToolMenu == null) CreateWaypointMenu();
            var menu = waypointToolMenu!;
            if (campaign.RouteCardOpen)
            {
                menu.Visible = true; UserInterfaceController.AddCustomControl(menu);
                LayoutRouteCard(campaign, screenWidth, screenHeight, Math.Max(4, UserInterfaceHelper.CalculateScaledValue(8)), Math.Max(32, UserInterfaceHelper.CalculateScaledValue(34)));
                return;
            }
            bool hasStar = TryGetStarTransit(out _, out var system, out var transit);
            bool canPickTransitStar = MapUiGate.CurrentRenderer?.SceneRenderType == SceneRenderType.Galaxy;
            if (!hasStar || !ReferenceEquals(system, transitMenuSystem)) transitMenuSystem = null;
            float gap = Math.Max(4, UserInterfaceHelper.CalculateScaledValue(8));
            float width = Math.Min(UserInterfaceHelper.CalculateScaledValue(analysisOpen ? 620 : surveyQueueOpen ? 460 : 330), screenWidth - 2 * gap);
            float header = Math.Max(32, UserInterfaceHelper.CalculateScaledValue(34));
            var priorities = campaign.Survey.Snapshot;
            int count = analysisOpen ? 6 : surveyQueueOpen ? Math.Min(SurveyPageSize, priorities.Length) + 1 : transitMenuSystem == null ? 0 : transitOptions.Count;
            const int baseRows = 9;
            float rowHeight = Math.Min(UserInterfaceHelper.CalculateScaledValue(28), (screenHeight - 3 * gap - header) / (count + baseRows));
            float height = header + (count + baseRows) * rowHeight + gap;
            var anchor = waypointToolButton!.CalculateAbsoluteRectangle();
            menu.SetSizeAndPosition(new(width, height), new(Math.Clamp(anchor.Right - width, gap, Math.Max(gap, screenWidth - width - gap)), Math.Max(gap, anchor.Top - height - gap)));
            menu.Visible = true; UserInterfaceController.AddCustomControl(menu);
            PlaceMenuRow(generateNetworkButton!, null, new(gap, header), new(width - 2 * gap, rowHeight));
            generateNetworkButton!.Text = campaign.Generation is { Search.Complete:false } ? "Cancel network generation" : campaign.Generated.Length == 0 ? "Generate useful galaxy network" : "Galaxy network generated";
            generateNetworkButton.Enabled = campaign.Generated.Length == 0 || campaign.Generation is { Search.Complete:false };
            PlaceMenuRow(displayModeButton!, null, new(gap, header + rowHeight), new(width - 2 * gap, rowHeight));
            displayModeButton!.Text = campaign.DisplayMode switch { WaypointDisplayMode.Hidden => "Map: Hidden", WaypointDisplayMode.AllDebug => "Map: All (DEBUG - hidden nodes)", _ => "Map: Discovered" };
            PlaceMenuRow(networkStatusRow!, null, new(gap, header + 2 * rowHeight), new(width - 2 * gap, rowHeight));
            networkStatusRow!.Text = campaign.DisplayStatus;
            PlaceMenuRow(starTransitButton!, hasStar ? system : null, new(gap, header + 3 * rowHeight), new(width - 2 * gap, rowHeight));
            starTransitButton!.Enabled = hasStar || canPickTransitStar;
            int blockedCount = hasStar ? TransitRules.Groups.Count(g => (transit.Mask(system.SystemId) & g.Group) != 0) : 0;
            starTransitButton.Text = hasStar ? $"Transit rules: {TransitSystemLabel(campaign, system)} ({blockedCount})" :
                canPickTransitStar ? "Transit rules: choose a star" : "Transit rules: open galaxy map";
            PlaceMenuRow(surveyPickerButton!, null, new(gap, header + 4 * rowHeight), new(width - 2 * gap, rowHeight));
            surveyPickerButton!.Enabled = campaign.SurveyFault == null && MapUiGate.CurrentRenderer?.SceneRenderType == SceneRenderType.Galaxy;
            surveyPickerButton.Text = surveyPickerButton.Enabled ? "Prioritize Survey: choose a star" : "Prioritize Survey: open galaxy map";
            PlaceMenuRow(surveyQueueButton!, null, new(gap, header + 5 * rowHeight), new(width - 2 * gap, rowHeight));
            surveyQueueButton!.Text = campaign.SurveyFault ?? $"Survey queue: {priorities.Length} systems";
            PlaceMenuRow(analyzeNetworkButton!, null, new(gap, header + 6 * rowHeight), new(width - 2 * gap, rowHeight));
            analyzeNetworkButton!.Text = campaign.Analysis?.Running == true ? "Cancel network analysis" : "Analyze current network";
            analyzeNetworkButton.Enabled = CanAnalyzeNetwork(campaign);
            PlaceMenuRow(analysisReportButton!, null, new(gap, header + 7 * rowHeight), new(width - 2 * gap, rowHeight));
            analysisReportButton!.Text = "Empire-wide route benefits";
            PlaceMenuRow(routePairButton!, null, new(gap, header + 8 * rowHeight), new(width - 2 * gap, rowHeight));
            routePairButton!.Text = "Compare route: click two stars";
            routePairButton.Enabled = canPickTransitStar;
            if (analysisOpen) LayoutAnalysis(campaign, gap, header, rowHeight, width, baseRows);
            if (surveyQueueOpen)
            {
                int pages = Math.Max(1, (priorities.Length + SurveyPageSize - 1) / SurveyPageSize);
                surveyPage %= pages;
                var shown = priorities.Skip(surveyPage * SurveyPageSize).Take(SurveyPageSize).ToArray();
                for (int i = 0; i < shown.Length; i++)
                {
                    var item = shown[i]; var row = surveyRows[i];
                    PlaceMenuRow(row, null, new(gap, header + (baseRows + i) * rowHeight), new(width - 2 * gap, rowHeight));
                    row.ExtraClickData = item.SystemId;
                    row.Text = $"Cancel {surveyPage * SurveyPageSize + i + 1}. {SurveySystemLabel(campaign, item.SystemId)} — {item.Status}";
                }
                PlaceMenuRow(surveyNextPage!, null, new(gap, header + (baseRows + shown.Length) * rowHeight), new(width - 2 * gap, rowHeight));
                surveyNextPage!.Enabled = pages > 1;
                surveyNextPage.Text = priorities.Length == 0 ? "No survey priorities" : $"Page {surveyPage + 1}/{pages} — next";
            }
            for (int i = 0; !surveyQueueOpen && !analysisOpen && i < count; i++)
            {
                var row = transitOptions[i];
                if (i >= 2 && i < 2 + TransitRules.Groups.Length)
                {
                    var group = TransitRules.Groups[i - 2]; bool blocked = (transit.Mask(system.SystemId) & group.Group) != 0;
                    row.ToggleValue = blocked; row.Text = (blocked ? "[x] " : "[ ] ") + group.Label;
                }
                PlaceMenuRow(row, system, new(gap, header + (i + baseRows) * rowHeight), new(width - 2 * gap, rowHeight));
            }
            var close = menu.Controls[0]; close.SetSizeAndPosition(new(26, 26), new(width - 30, 3));
        }
        catch (Exception ex) { HideWaypointMenu(); ClearTransitPress(); ReportError("Waypoint tools", ex); }
    }
    private static void CreateWaypointMenu()
    {
        var menu = waypointToolMenu = new WaypointToolMenu();
        var close = CreateTransitControl("GalaxyWaypoints_MenuClose", (_, _) => CloseWaypointMenu()); close.Text = "X"; menu.AddControl(close);
        generateNetworkButton = CreateTransitControl("GalaxyWaypoints_GenerateNetwork", GenerateNetworkClick); menu.AddControl(generateNetworkButton);
        displayModeButton = CreateTransitControl("GalaxyWaypoints_DisplayMode", CycleWaypointDisplay); menu.AddControl(displayModeButton);
        networkStatusRow = CreateTransitControl("GalaxyWaypoints_NetworkStatus", null); networkStatusRow.Enabled = false; menu.AddControl(networkStatusRow);
        starTransitButton = CreateTransitControl(TransitButtonName, ToggleStarTransit); menu.AddControl(starTransitButton);
        CreateSurveyControls(menu);
        CreateAnalysisControls(menu);
        CreateRouteControls(menu);
        var choose = CreateTransitControl(TransitButtonName + "_choose", ChooseTransitStar); choose.Text = "Choose star on galaxy map"; transitOptions.Add(choose);
        var title = CreateTransitControl(TransitButtonName + "_title", null); title.Text = "Block entry (checked = blocked)"; title.Enabled = false; transitOptions.Add(title);
        foreach (var group in TransitRules.Groups)
        {
            var row = CreateTransitControl(TransitButtonName + "_" + group.Group, ToggleTransitGroup);
            row.ExtraClickDataValue = (int)group.Group; row.IsToggleButton = true; row.TextAlignment = TextAlignment.Left; transitOptions.Add(row);
        }
        var allow = CreateTransitControl(TransitButtonName + "_clear", ClearTransitGroups); allow.Text = "Allow all types"; transitOptions.Add(allow);
        var block = CreateTransitControl(TransitButtonName + "_block", BlockTransitGroups); block.Text = "Block all types"; transitOptions.Add(block);
        var note = CreateTransitControl(TransitButtonName + "_manual", null); note.Text = "Direct manual orders override rules"; note.Enabled = false; transitOptions.Add(note);
        foreach (var row in transitOptions) menu.AddControl(row);
    }
    private static DWButton CreateTransitControl(string name, EventHandler<DWEventArgs>? click)
    {
        var button = new TransitControl { Name = name, PostScene = true, Visible = true, TextAlignment = TextAlignment.Center };
        button.Initialize(FontSize.Small, FontSize.Small, UserInterfaceController.SelectionPanel?.TilingBackground!);
        if (click != null) button.ClickEvent += click;
        return button;
    }
    private static void PlaceMenuRow(DWButton button, StarSystem? system, Vector2 position, Vector2 size)
    {
        button.ExtraClickData = system!;
        if (button.Position != position || button.Size != size) button.SetSizeAndPosition(size, position);
        button.Visible = true;
    }
    private static void GenerateNetworkClick(object? sender, DWEventArgs args)
    {
        try
        {
            if (!TransitUiActive() || sender is not DWButton { Visible: true, Enabled: true } || toolGalaxy is not { } galaxy ||
                !Campaigns.TryGetValue(galaxy, out Campaign? campaign)) return;
            if(campaign.Generated.Length!=0&&campaign.Generation is not { Search.Complete:false })return;
            BeginUsefulGeneration(campaign);
        }
        catch (Exception ex) { ReportError("Generate galaxy network", ex); }
    }
    private static void CycleWaypointDisplay(object? sender, DWEventArgs args)
    {
        if (!TransitUiActive() || sender is not DWButton { Visible: true, Enabled: true } || toolGalaxy == null) return;
        var campaign = GetCampaign(toolGalaxy);
        if (campaign == null) return;
        campaign.DisplayMode = (WaypointDisplayMode)(((int)campaign.DisplayMode + 1) % 3);
        if (campaign.DisplayMode == WaypointDisplayMode.Hidden) campaign.Selection = null;
        UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
    }
    private sealed class WaypointToolMenu : NativeToolWindow
    {
        internal WaypointToolMenu() : base("Galaxy Waypoints") { Name = "GalaxyWaypointsToolMenu"; Layer = 970; PostScene = true; Initialize(FontSize.Small, FontSize.Small, null!); }
        bool Available { get { if (!TransitUiActive()) { ClearTransitPress(); MapUiGate.ReleaseHover(this); return false; } return CheckVisible(); } }
        public override void Render(RenderDrawContext c, SpriteBatch b, Point p, bool tips) { if (Available) base.Render(c, b, p, tips); }
        public override bool DoMouseDown(Point p, bool left, Empire e, bool shift, bool ctrl, bool child) => Available && base.DoMouseDown(p, left, e, shift, ctrl, child);
        public override bool DoMouseUp(Point p, bool left, Empire e, bool shift, bool ctrl, float held) => Available && base.DoMouseUp(p, left, e, shift, ctrl, held);
        public override bool DoMouseMove(Vector2 m, Point p, bool left, bool right) => Available && base.DoMouseMove(m, p, left, right);
    }

    private static bool TransitClick(object? sender, out StarSystem system, out TransitRestrictions rules)
    {
        system = null!; rules = null!;
        return TransitUiActive() && sender is DWButton { Visible: true, Enabled: true } button && TryGetStarTransit(out _, out system, out rules)
            && ReferenceEquals(button.ExtraClickData, system);
    }
    private static void ToggleStarTransit(object? sender, DWEventArgs args)
    {
        try
        {
            if (TransitClick(sender, out var system, out _))
            {
                surveyQueueOpen = false;
                analysisOpen = false;
                transitMenuSystem = ReferenceEquals(transitMenuSystem, system) ? null : system;
            }
            else if (TransitUiActive() && sender is DWButton { Visible: true, Enabled: true } && toolGalaxy is { } galaxy &&
                Campaigns.TryGetValue(galaxy, out var campaign) && MapUiGate.CurrentRenderer?.SceneRenderType == SceneRenderType.Galaxy)
            {
                // Native cartouche clicks populate SystemSummary rather than SelectionPanel.  This explicit
                // picker is the safe fallback when neither native source is available (including unvisited stars).
                campaign.TransitPicker = true;
                campaign.SurveyPicker = false;
                transitMenuSystem = null;
                HideWaypointMenu();
            }
        }
        catch (Exception ex) { ReportError("Open transit rules", ex); }
    }
    private static void ToggleTransitGroup(object? sender, DWEventArgs args)
    {
        try
        {
            if (!TransitClick(sender, out var system, out var rules) || sender is not DWButton button) return;
            rules.ToggleGroup(system.SystemId, (TransitGroup)button.ExtraClickDataValue);
            Log($"Transit rules system {system.SystemId}: blocked={rules.Mask(system.SystemId)}; direct manual orders override.");
        }
        catch (Exception ex) { ReportError("Change transit rule", ex); }
    }
    private static void ChooseTransitStar(object? sender, DWEventArgs args)
    {
        if (!TransitUiActive() || sender is not DWButton { Visible: true, Enabled: true } || toolGalaxy is not { } galaxy ||
            !Campaigns.TryGetValue(galaxy, out var campaign) || MapUiGate.CurrentRenderer?.SceneRenderType != SceneRenderType.Galaxy) return;
        campaign.TransitPicker = true;
        campaign.SurveyPicker = false;
        transitMenuSystem = null;
        HideWaypointMenu();
    }
    private static void ClearTransitGroups(object? sender, DWEventArgs args)
    {
        try
        {
            if (!TransitClick(sender, out var system, out var rules)) return;
            rules.Set(system.SystemId, TransitGroup.None);
            Log($"Transit allowed for all roles in system {system.SystemId}.");
        }
        catch (Exception ex) { ReportError("Clear transit rules", ex); }
    }

    private static void BlockTransitGroups(object? sender, DWEventArgs args)
    {
        if (TransitClick(sender, out var system, out var rules)) rules.Set(system.SystemId, TransitGroup.All);
    }

    private static bool TryGetStarTransit(out SelectionPanel panel, out StarSystem system, out TransitRestrictions transit)
    {
        panel = UserInterfaceController.SelectionPanel;
        system = null!; transit = null!;
        if (toolGalaxy is not { } galaxy || !Campaigns.TryGetValue(galaxy, out Campaign? campaign) ||
            campaign.Observer.EmpireId != galaxy.Empires.GetPlayer()?.EmpireId ||
            campaign.Transit is not { } restrictions) return false;
        // An explicit Waypoints selection wins until the player chooses another star or leaves the campaign.
        StarSystem? selected = campaign.TransitSelection;
        if (selected == null && panel is { } visiblePanel && visiblePanel.CheckVisible() && visiblePanel.Opacity > 0 && ReferenceEquals(visiblePanel.Galaxy, galaxy))
        {
            selected = visiblePanel.Item as StarSystem;
            if (selected == null && (visiblePanel.Item as Orb ?? visiblePanel.ItemAsStellarObject as Orb) is { Category: OrbCategory.Star } star)
                selected = star.GetSystem(galaxy) ?? galaxy.FindNearestSystem(star.GalaxyX, star.GalaxyY);
        }
        // A normal Galaxy cartouche binds this native summary but does not populate SelectionPanel.
        // Require a current visible summary and exact identity in this campaign's system table to reject stale UI.
        if (selected == null && DWRendererBase.SystemSummary is { Visible: true } summary && summary.CheckVisible())
            selected = summary.System;
        if (selected == null || selected.SystemId < 0 || galaxy.Systems.GetById(selected.SystemId) is not { } current || !ReferenceEquals(current, selected)) return false;
        system = selected; transit = restrictions; return true;
    }

    private static string TransitSystemLabel(Campaign campaign, StarSystem system) => SurveySystemLabel(campaign, system.SystemId);
}
