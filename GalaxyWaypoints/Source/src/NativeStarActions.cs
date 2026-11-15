using System.Reflection;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using LocalModUi;
using Stride.Core.Mathematics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly FieldInfo? NativeActionsField = typeof(ScaledRenderer).GetField("ActionButtons", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    private static NativeStarAction? nativeSurveyAction, nativeTransitAction;
    private static StarSystem? lastNativeStar;
    private static Galaxy? lastNativeGalaxy;

    private static StarSystem? NativeSelectedStar(Galaxy galaxy)
    {
        var panel = UserInterfaceController.SelectionPanel;
        if (panel is not { Visible: true } || !panel.CheckVisible() || panel.Opacity <= 0 || !ReferenceEquals(panel.Galaxy, galaxy)) return null;
        var star = panel.Item as StarSystem;
        if (star == null && (panel.Item as Orb ?? panel.ItemAsStellarObject as Orb) is { Category: OrbCategory.Star } orb) star = orb.GetSystem(galaxy);
        return star != null && ReferenceEquals(galaxy.Systems.GetById(star.SystemId), star) ? star : null;
    }

    private static void UpdateNativeStarActions()
    {
        if (nativeSurveyAction != null) nativeSurveyAction.Visible = false;
        if (nativeTransitAction != null) nativeTransitAction.Visible = false;
        try
        {
            if (!MapUiGate.TryGetContext(out _, out var galaxy) || !MapUiGate.ToolsUnobstructed() ||
                NativeSelectedStar(galaxy) is not { } star || GetCampaign(galaxy) is not { } campaign ||
                NativeActionsField?.GetValue(MapUiGate.CurrentRenderer) is not DWPanel row || !row.Visible || !row.CheckVisible()) return;
            if (ReferenceEquals(lastNativeGalaxy, galaxy) && lastNativeStar != null && !ReferenceEquals(lastNativeStar, star)) campaign.TransitSelection = null;
            lastNativeGalaxy = galaxy; lastNativeStar = star;
            nativeSurveyAction ??= new NativeStarAction(true);
            nativeTransitAction ??= new NativeStarAction(false);
            float size = Math.Min(row.Size.Y, Math.Max(32, UserInterfaceHelper.CalculateScaledValue(44)));
            var peers = row.Controls.Where(c => c != nativeSurveyAction && c != nativeTransitAction && c.Visible).ToArray();
            // Native action rows keep empty disabled cells visible. They are space, not actions to displace.
            var occupied = peers.Where(c => c is not DWButton b || b.Enabled || b.Image != null || b.ImageSprite != null ||
                b.ImageSprite2 != null || !string.IsNullOrEmpty(b.Text) || b.HoverTips.Count > 0).ToArray();
            var slots = new List<float>(2);
            if (size < 24) return;
            for (float x = 0; x + size <= row.Size.X && slots.Count < 2; x += size)
                if (!occupied.Any(c => c.Position.X < x + size && c.Position.X + c.Size.X > x)) slots.Add(x);
            if (slots.Count < 2) return;
            int slot = 0;
            foreach (var button in new[] { nativeSurveyAction, nativeTransitAction })
            {
                if (!ReferenceEquals(button.Parent, row))
                {
                    button.Parent?.Controls.Remove(button);
                    row.AddControl(button);
                }
                button.ExtraClickData = star;
                button.ExtraData = galaxy;
                button.ExtraClickDataValue = campaign.Observer.EmpireId;
                button.Layer = peers.Select(c => c.Layer).DefaultIfEmpty(0).Max() + 1;
                button.SetSizeAndPosition(new(size, size), new(slots[slot++], 0));
                button.Enabled = !button.Survey || campaign.SurveyFault == null;
                button.Text = button.Survey ? campaign.Survey.Snapshot.Any(p => p.SystemId == star.SystemId) ? "S✓" : "SURV" : "TR";
                button.Visible = true;
                if (button.FontNormal != null)
                {
                    button.HoverTips.Clear();
                    button.AddHoverTip(button.Survey ? "Prioritize survey" : "Transit rules",
                        button.Survey ? "Toggle this star in the automatic survey queue. Blue SURVEY ring = priority." : "Block or allow our ship groups. Automatic travel also requires safe environmental protection; direct manual orders override with risk.",
                        button.CalculateAbsoluteRectangle(), 340, Side.Top);
                }
            }
        }
        catch (Exception ex) { ReportError("Native star actions", ex); }
    }

    private sealed class NativeStarAction : ToolActionButton
    {
        internal readonly bool Survey;
        private object? pressedStar, pressedGalaxy;
        internal NativeStarAction(bool survey) : base(() => MapUiGate.TryGetContext(out _, out _) && MapUiGate.ToolsUnobstructed())
        {
            Survey = survey; Name = survey ? "GalaxyWaypoints_NativeSurvey" : "GalaxyWaypoints_NativeTransit";
            PostScene = true;
            Initialize(FontSize.Small, FontSize.Small, UserInterfaceController.SelectionPanel?.TilingBackground!);
        }
        protected override bool ContextAvailable() => ExtraData is Galaxy galaxy && ReferenceEquals((Galaxy.Game as DWGame)?.Galaxy, galaxy) &&
            galaxy.Empires.GetPlayer()?.EmpireId == ExtraClickDataValue && ReferenceEquals(NativeSelectedStar(galaxy), ExtraClickData);
        public override bool DoMouseDown(Point p, bool left, Empire empire, bool shift, bool ctrl, bool child)
        {
            pressedStar = ExtraClickData; pressedGalaxy = ExtraData;
            return base.DoMouseDown(p, left, empire, shift, ctrl, child);
        }
        public override bool DoMouseUp(Point p, bool left, Empire empire, bool shift, bool ctrl, float held)
        {
            if (!ReferenceEquals(pressedStar, ExtraClickData) || !ReferenceEquals(pressedGalaxy, ExtraData)) ToolActionButton.CancelPress(this);
            pressedStar = pressedGalaxy = null;
            return base.DoMouseUp(p, left, empire, shift, ctrl, held);
        }
        protected override void Click()
        {
            if (!ContextAvailable() || ExtraData is not Galaxy galaxy || ExtraClickData is not StarSystem star || GetCampaign(galaxy) is not { } campaign) return;
            campaign.TransitSelection = star;
            if (Survey)
            {
                if (campaign.SurveyFault != null) return;
                bool cancelled = campaign.Survey.Remove(star.SystemId);
                var added = cancelled ? SurveyAddResult.Unavailable : campaign.Survey.Add(star.SystemId, true, FullySurveyed(campaign, star.SystemId));
                MapUiGate.CurrentRenderer?.SetBriefMessage(cancelled ? "Survey priority cancelled" : added == SurveyAddResult.Added ?
                    "Survey priority added — blue SURVEY ring" : added == SurveyAddResult.AlreadySurveyed ? "System already fully surveyed" : "Survey priority unavailable", 5);
            }
            else
            {
                NativeToolDock.Select(NativeToolDock.Waypoints);
                surveyQueueOpen = analysisOpen = false;
                transitMenuSystem = star;
                UpdateStarTransitButton(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
            }
        }
    }
}
