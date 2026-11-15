using System.Reflection;
using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace LocalModUi;

// Copies in independent mod DLLs coordinate through native controls/BCL values, not each other's types.
internal static class NativeToolDock
{
    internal const string Battle = "BattleLog", Fx = "WeaponFX", Waypoints = "Waypoints";
    const string Prefix = "DW2ToolDock_", StateName = Prefix + "State";
    static readonly string[] Keys = [Battle, Fx, Waypoints];
    static readonly FieldInfo[] NativeButtons = typeof(UserInterfaceController).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Concat(typeof(DWGame).Assembly.GetType("DistantWorlds2.DWRendererBase")!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        .Concat(typeof(ScaledRenderer).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Where(f => typeof(DWButton).IsAssignableFrom(f.FieldType)).Distinct().ToArray();
    static readonly FieldInfo[] NativeButtonPanels = new[] { "MapOverlays", "ActionButtons", "CockpitButtons", "SelectionNavigationButtons", "SelectionNavigation" }
        .SelectMany(name => new[] { AccessTools.Field(typeof(UserInterfaceController), name), AccessTools.Field(typeof(DWGame).Assembly.GetType("DistantWorlds2.DWRendererBase"), name), typeof(ScaledRenderer).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) })
        .OfType<FieldInfo>().Distinct().ToArray();
    internal static bool IsToolWindow(string? name) => name is "BattleLogPanel" or "DW2WeaponFXPanel" or "GalaxyWaypointsToolMenu";
    internal static bool ToolbarAvailable() => MapUiGate.TryGetContext(out _, out _) && MapUiGate.ToolsUnobstructed();
    static bool SameScope(DWButton control, Galaxy galaxy, short observer) => control.ExtraClickDataValue == observer &&
        control.ExtraData is WeakReference<Galaxy> weak && weak.TryGetTarget(out var prior) && ReferenceEquals(prior, galaxy);
    static DWButton State(Galaxy galaxy)
    {
        lock (UserInterfaceController.CustomControlLock)
        {
            if (!UserInterfaceController.CustomControls.TryGetValue(StateName, out var entry) || entry is not DWButton state)
            { state = new DWButton { Name = StateName, Visible = false }; UserInterfaceController.AddCustomControl(state); }
            short observer = galaxy.Empires.GetPlayer().EmpireId;
            if (!SameScope(state, galaxy, observer)) { state.ExtraData = new WeakReference<Galaxy>(galaxy); state.ExtraClickDataValue = observer; state.Text = ""; }
            return state;
        }
    }
    internal static string Selected => MapUiGate.TryGetContext(out _, out var galaxy) ? State(galaxy).Text : "";
    internal static bool BodyAvailable(string key) => ToolbarAvailable() && Selected == key;
    internal static void Select(string key) { if (ToolbarAvailable() && MapUiGate.TryGetContext(out _, out var galaxy)) State(galaxy).Text = key; }
    internal static void Clear(string key)
    {
        lock (UserInterfaceController.CustomControlLock)
            if (UserInterfaceController.CustomControls.GetValueOrDefault(StateName) is DWButton state && state.Text == key) state.Text = "";
    }
    internal static void Install(Harmony hooks) => hooks.Patch(AccessTools.Method(typeof(UserInterfaceController), nameof(UserInterfaceController.DoMouseDown)), prefix: new HarmonyMethod(typeof(ToolActionButton), nameof(ToolActionButton.ClearPress)));
    internal static DWButton Register(string key, string title, string help, Action click)
    {
        var button = new DockButton(key, title, help, click) { Name = Prefix + key, PostScene = true, Layer = 960, Visible = false, IsToggleButton = true };
        button.Initialize(FontSize.Small, FontSize.Small, UserInterfaceController.MapOverlays?.TilingBackground ?? UserInterfaceController.SelectionPanel?.TilingBackground!);
        return button;
    }
    internal static void Update(DWButton button)
    {
        MapUiGate.TraceAvailability();
        button.Visible = false;
        if (!MapUiGate.TryGetContext(out _, out var galaxy)) { ToolActionButton.CancelPress(button); MapUiGate.ReleaseHover(button); return; }
        short observer = galaxy.Empires.GetPlayer().EmpireId;
        if (!SameScope(button, galaxy, observer)) { button.ExtraData = new WeakReference<Galaxy>(galaxy); button.ExtraClickDataValue = observer; }
        _ = State(galaxy);
        if (UserInterfaceController.CustomControls.GetValueOrDefault(button.Name) != button) ToolActionButton.CancelPress(button);
        UserInterfaceController.AddCustomControl(button);
        if (!ToolbarAvailable()) { ToolActionButton.CancelPress(button); MapUiGate.ReleaseHover(button); return; }
        button.Visible = true;
        Layout(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight, galaxy, observer);
        if (button is DockButton own) own.Refresh();
    }
    internal static void Detach(DWButton? button)
    {
        if (button == null) return;
        ToolActionButton.CancelPress(button); button.Visible = false; button.ExtraData = null!; MapUiGate.ReleaseHover(button); UserInterfaceController.RemoveCustomControl(button);
    }
    static bool Overlap(RectangleF a, RectangleF b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    internal static void Layout(int width, int height, Galaxy galaxy, short observer)
    {
        var renderer = MapUiGate.CurrentRenderer;
        float gap = Math.Max(2, UserInterfaceHelper.CalculateScaledValue(4)), margin = Math.Max(4, UserInterfaceHelper.ScreenEdgePadding);
        var size = UserInterfaceHelper.AngledButtonSize;
        if (!(size.X > 0 && size.Y > 0)) size = new Vector2(UserInterfaceHelper.CalculateScaledValue(40), UserInterfaceHelper.CalculateScaledValue(32));
        size.X = Math.Max(24, size.X); size.Y = Math.Max(24, size.Y);
        lock (UserInterfaceController.CustomControlLock)
        {
            int count = 0;
            foreach (string key in Keys) if (UserInterfaceController.CustomControls.GetValueOrDefault(Prefix + key) is DWButton b && SameScope(b, galaxy, observer)) count++;
            float total = count * size.X + Math.Max(0, count - 1) * gap, left = width - margin - total, top = height - margin - size.Y;
            // Place above any native map-button row/zoom button that intersects our compact strip.
            for (int pass = 0; pass < NativeButtons.Length + NativeButtonPanels.Length + 2; pass++)
            {
                var row = new RectangleF(left, top, total, size.Y); float next = top;
                foreach (var field in NativeButtonPanels)
                    if ((field.IsStatic || renderer != null) && field.GetValue(field.IsStatic ? null : renderer) is DWControl panel && MapUiGate.Displayed(panel) && Overlap(row, panel.CalculateAbsoluteRectangle())) next = Math.Min(next, panel.CalculateAbsoluteRectangle().Top - gap - size.Y);
                foreach (var field in NativeButtons)
                    if ((field.IsStatic || renderer != null) && field.GetValue(field.IsStatic ? null : renderer) is DWButton native && MapUiGate.Displayed(native) && Overlap(row, native.CalculateAbsoluteRectangle())) next = Math.Min(next, native.CalculateAbsoluteRectangle().Top - gap - size.Y);
                if (next == top) break; top = next;
            }
            int rank = 0;
            foreach (string key in Keys)
                if (UserInterfaceController.CustomControls.GetValueOrDefault(Prefix + key) is DWButton b && SameScope(b, galaxy, observer))
                {
                    var position = new Vector2(left + rank++ * (size.X + gap), top);
                    if (b.Size != size || b.Type != DWButtonType.AngledMidRight) b.SetButtonType(DWButtonType.AngledMidRight, size);
                    if (b.Position != position || b.Size != size) b.SetSizeAndPosition(size, position);
                    b.Visible = left >= 0 && top >= margin && ToolbarAvailable(); b.ToggleValue = Selected == key;
                }
        }
    }
    sealed class DockButton(string key, string title, string help, Action click) : ToolActionButton(() => ToolbarAvailable())
    {
        Vector2 tooltipPosition, tooltipSize; bool tooltipReady;
        internal void Refresh()
        {
            Sprite? icon = key == Fx ? UserInterfaceController.GameSettingsButton?.ImageSprite : null;
            var iconName = key == Battle ? "BattleLocation" : "TravelVectors";
            if (icon == null && UserInterfaceHelper.IconImages != null) icon = UserInterfaceHelper.GetIcon(iconName);
            ImageSprite = icon!; Text = icon == null ? key == Battle ? "BL" : key == Fx ? "FX" : "WP" : "";
            if (FontNormal != null && (!tooltipReady || tooltipPosition != Position || tooltipSize != Size))
            {
                HoverTips.Clear(); AddHoverTip(title, help, CalculateAbsoluteRectangle(), 280, Side.Top);
                tooltipReady = true; tooltipPosition = Position; tooltipSize = Size;
            }
        }
        protected override void Click() => click();
        protected override bool ContextAvailable() => MapUiGate.TryGetContext(out _, out var galaxy) && SameScope(this, galaxy, galaxy.Empires.GetPlayer().EmpireId);
    }
}

internal class ToolActionButton(Func<bool> available) : DWButton
{
    static ToolActionButton? pressed;
    internal static void ClearPress() => pressed = null;
    internal static void CancelPress(DWButton button) { if (ReferenceEquals(pressed, button)) ClearPress(); }
    protected virtual bool ContextAvailable() => true;
    protected virtual void Click() { }
    bool Available { get { if (!available() || !ContextAvailable()) { if (pressed == this) ClearPress(); MapUiGate.ReleaseHover(this); return false; } return CheckVisible(); } }
    public override void Render(RenderDrawContext context, SpriteBatch batch, Point point, bool tips) { if (Available) base.Render(context, batch, point, tips); }
    public override bool DoMouseDown(Point point, bool left, Empire empire, bool shift, bool ctrl, bool child)
    {
        if (!Available || !CalculateAbsoluteRectangle().Contains(point)) { ClearPress(); return false; }
        if (left && Enabled && !child) pressed = this;
        return base.DoMouseDown(point, left, empire, shift, ctrl, child);
    }
    public override bool DoMouseUp(Point point, bool left, Empire empire, bool shift, bool ctrl, float held)
    {
        bool owns = pressed == this; if (owns) ClearPress();
        if (!Available || !CalculateAbsoluteRectangle().Contains(point)) return false;
        if (!owns || !left || !Enabled) return true;
        bool result = base.DoMouseUp(point, left, empire, shift, ctrl, held); Click(); return result;
    }
    public override bool DoMouseMove(Vector2 move, Point point, bool left, bool right) => Available && base.DoMouseMove(move, point, left, right);
}
