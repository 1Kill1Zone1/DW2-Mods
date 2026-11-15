using System.Reflection;
using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;

namespace LocalModUi;

// Compiled into each mod: no runtime dependency or change to another mod's DLL.
internal static class MapUiGate
{
    internal static readonly string[] BlockerNames = ["VictoryConditionsDialog", "GameSettingsDialog", "GameModsDialog", "GameMenuPanel", "TourListPanel", "TextPromptMessageDialog", "EmpireMessageDialog", "ResourceStockDialog", "WeaponsReportDialog", "TacticsDialog", "GalactopediaDialog", "TourDialog", "TroopReportDialog", "GameEditorDialog", "EditItemDialog", "ColonyPopulationPolicyDialog", "FileDialog", "WaitDialog", "DesignsDialog", "FleetTemplateListDialog", "FleetTemplateDialog", "ArmyTemplateListDialog", "ArmyTemplateDialog", "KeyMappingDialog", "GameControllerDialog", "ShipHullDialog", "DiplomacyConversationPanel", "ColonyPopulationDialog", "LocationMissionDialog", "AllianceDialog", "AllianceVoteProposalDialog", "AllianceProposeDialog", "QuestContractDialog", "EquipmentDialog", "ResourceTradeDialog", "BuyShipHullDialog"];
    static readonly FieldInfo? RendererField = AccessTools.Field(typeof(DWGame), "_Renderer");
    static readonly object MetadataLock = new();
    internal static FieldInfo[]? Blockers;
    static bool metadataMissing;
    static readonly Type? RendererType = AccessTools.TypeByName("DistantWorlds2.DWRendererBase");
    // Native HUD panels also live in CustomControls; identity, not their public Name, distinguishes them.
    static readonly string[] HudNames = ["MessageCategoryButtons", "SelectionPanel", "SelectionNavigationButtons", "ActionButtons", "CockpitButtons", "MapOverlays", "MessageFilterPanel", "EmpireSectionList", "MainViewHeader", "MessageListView", "MessageLog", "MessageButtonStrip"];
    static readonly FieldInfo[] HudFields = HudNames.SelectMany(name => new[] { AccessTools.Field(typeof(UserInterfaceController),name), RendererType == null ? null : AccessTools.Field(RendererType,name) }).OfType<FieldInfo>().Distinct().ToArray();
    // ScaledRenderer owns live instances which can differ from DWRendererBase's static aliases.
    static readonly FieldInfo[] RendererHudFields = new[] { "ActionButtons", "CockpitButtons", "SelectionNavigation" }
        .Select(name => typeof(ScaledRenderer).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)).OfType<FieldInfo>().ToArray();
    static readonly FieldInfo[] SpeedFields = new[] { "PauseButton", "SpeedSlowerButton", "SpeedFasterButton" }.Select(name => RendererType == null ? null : AccessTools.Field(RendererType,name)).OfType<FieldInfo>().ToArray();

    static bool NativeHud(DWControl c, ScaledRenderer? renderer)
    {
        foreach (var field in HudFields) if (ReferenceEquals(field.GetValue(null),c)) return true;
        if (renderer != null)
            foreach (var field in RendererHudFields) if (ReferenceEquals(field.GetValue(renderer), c)) return true;
        if (c.Name == "GameControlButtons")
            foreach (var field in SpeedFields)
            {
                var parent = (field.GetValue(null) as DWControl)?.Parent;
                // Bound malformed ancestry; a matching public Name alone never grants an exemption.
                for (int depth = 0; parent != null && depth < 16; depth++, parent = parent.Parent)
                    if (ReferenceEquals(parent,c)) return true;
            }
        return false;
    }
    static long nextTrace;
    static string? lastTrace;
    static int traceLines;

    // Native Dialog() starts Visible=true with Size=0; allocated placeholders draw no window.
    internal static bool Displayed(DWControl? c) => c != null && c.Visible && c.CheckVisible() &&
        c.Size.X > 0 && c.Size.Y > 0 && (c.Opacity > 0 || c.TargetOpacity > 0);

    internal static void TraceAvailability()
    {
        long now = Environment.TickCount64;
        if (now < nextTrace || traceLines >= 24) return;
        nextTrace = now + 5_000;
        try
        {
            string status;
            if (!TryGetContext(out var game, out _))
                status = $"map context unavailable; host={Galaxy.Game?.GetType().Name}; galaxy={game?.Galaxy != null}; input={game?.Input != null}; renderer={RendererField?.GetValue(game!)?.GetType().Name}; scene={(RendererField?.GetValue(game!) as ScaledRenderer)?.SceneRenderType}";
            else
            {
                bool native = NativeWindowsClear();
                var windows = new List<string>();
                if (Blockers != null) foreach (var field in Blockers)
                    if (field.GetValue(null) is DWControl control && Displayed(control)) windows.Add(field.DeclaringType!.Name+"."+field.Name);
                if (Displayed(UserInterfaceController.ResearchScreen) && !UserInterfaceController.ResearchScreen.Minimized) windows.Add("ResearchScreen");
                if (Displayed(UserInterfaceController.MessageDialog)) windows.Add("MessageDialog");
                if (Displayed(UserInterfaceController.PopupList)) windows.Add("PopupList");
                lock (UserInterfaceController.CustomControlLock)
                {
                    var renderer = RendererField?.GetValue(game) as ScaledRenderer;
                    foreach (var c in UserInterfaceController.CustomControls.Values)
                        if (!NativeToolDock.IsToolWindow(c.Name) && Displayed(c) && c.Controls.Count > 0 && !NativeHud(c, renderer)) windows.Add("custom:"+c.Name);
                }
                status = $"nativeClear={native}; missingMetadata={metadataMissing}; fileModal={UserInterfaceController.ModalFileDialogActive}; messageModal={UserInterfaceController.ModalMessageDialogActive}; blockers=[{string.Join(",",windows)}]; screen={UserInterfaceController.ScreenWidth}x{UserInterfaceController.ScreenHeight}";
                lock (UserInterfaceController.CustomControlLock)
                    foreach (var c in UserInterfaceController.CustomControls.Values)
                        if (c.Name?.StartsWith("DW2ToolDock_",StringComparison.Ordinal) == true || NativeToolDock.IsToolWindow(c.Name))
                            status += $"; {c.Name}: visible={c.Visible} pos={c.Position} size={c.Size}"+(c.Name == "DW2ToolDock_State" ? " selected="+(c as DWButton)?.Text : "");
            }
            if (status == lastTrace) return;
            lastTrace = status; traceLines++;
            string folder = Path.Combine(AppContext.BaseDirectory,"data","Logs"); Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder,"local-tools-ui.log"),$"[{DateTime.Now:HH:mm:ss}] {typeof(MapUiGate).Assembly.GetName().Name}: {status}{Environment.NewLine}");
        }
        catch { /* Diagnostics never stop UI or change game state. */ }
    }

    internal static void ReleaseHover(DWControl root)
    {
        for (var hovered = UserInterfaceController.HoveredControl as DWControl; hovered != null; hovered = hovered.Parent)
            if (ReferenceEquals(hovered, root))
            {
                UserInterfaceController.HoveredControl = null!;
                if (UserInterfaceController.HoverInfo != null) UserInterfaceController.HoverInfo.Visible = false;
                return;
            }
    }

    internal static bool TryGetContext(out DWGame game, out Galaxy galaxy)
    {
        game = Galaxy.Game as DWGame ?? null!; galaxy = game?.Galaxy!;
        return galaxy != null && game!.Input != null && galaxy.Empires.GetPlayer() != null &&
            RendererField?.GetValue(game) is ScaledRenderer renderer && renderer.SceneRenderType is SceneRenderType.Galaxy or SceneRenderType.System or SceneRenderType.Location;
    }

    internal static ScaledRenderer? CurrentRenderer => Galaxy.Game is DWGame game ? RendererField?.GetValue(game) as ScaledRenderer : null;

    internal static bool Unobstructed(string? ownPanel = null, string? yieldingPanel = null) =>
        NativeWindowsClear() && CustomWindowsClear(ownPanel, yieldingPanel, false);

    internal static bool ToolsUnobstructed() => NativeWindowsClear() && CustomWindowsClear(null, null, true);

    static bool CustomWindowsClear(string? ownPanel, string? yieldingPanel, bool managedTools)
    {
        var renderer = CurrentRenderer;
        lock (UserInterfaceController.CustomControlLock)
            foreach (var c in UserInterfaceController.CustomControls.Values)
                if (c.Name != ownPanel && c.Name != yieldingPanel && !(managedTools && NativeToolDock.IsToolWindow(c.Name)) && Displayed(c) && c.Controls.Count > 0 && !NativeHud(c, renderer)) return false;
        return true;
    }

    static bool NativeWindowsClear()
    {
        if ((Displayed(UserInterfaceController.ResearchScreen) && !UserInterfaceController.ResearchScreen.Minimized) ||
            Displayed(UserInterfaceController.MessageDialog) || Displayed(UserInterfaceController.PopupList) ||
            UserInterfaceController.ModalFileDialogActive || UserInterfaceController.ModalMessageDialogActive) return false;
        if (Blockers == null) lock (MetadataLock)
        {
            if (metadataMissing) return false;
            if (Blockers == null)
            {
                var renderer = AccessTools.TypeByName("DistantWorlds2.DWRendererBase");
                var required = renderer == null ? [] : BlockerNames.Select(n => AccessTools.Field(renderer, n)).ToArray();
                if (required.Length != BlockerNames.Length || required.Any(f => f == null)) { metadataMissing = true; return false; }
                Blockers = required.Cast<FieldInfo>().Concat(BlockerNames.Select(n => AccessTools.Field(typeof(UserInterfaceController), n)).OfType<FieldInfo>()).Distinct().ToArray();
            }
        }
        if (Blockers.Any(f => f.GetValue(null) is DWControl c && Displayed(c))) return false;
        return true;
    }
}
