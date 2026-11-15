using System.Reflection;
using System.Text.Json;
using DistantWorlds.Types;
using DistantWorlds.UI;
using FreightDispatch;
using HarmonyLib;
using Stride.Core.Mathematics;
using UiChecks;

namespace DW2ModLauncher.Loader;
static partial class Program
{
    static void FreshPanel() { if (CurrentPanel is { } p) AccessTools.Field(typeof(Panel), "lastStatusRefresh").SetValue(p, 0L); UpdateUi(); }
    static void Press(DWControl control)
    {
        UserInterfaceController.HoveredControl = control;
        UserInterfaceController.DoMouseDown(new GameSettings(), NativeUi.Center(control), true, null!, false, false);
    }
    static Panel VerifyWindowAndEmptyUi(Galaxy g, Panel panel, DWButton dock)
    {
        var e = g.Empires.GetPlayer(); string economy = EconomicFingerprint(e), state = JsonSerializer.Serialize(State.For(g, e.EmpireId).Data);
        var column = UserInterfaceController.SelectionPanel.CalculateAbsoluteRectangle();
        if (column.Right + panel.Size.X + 24 <= UserInterfaceController.ScreenWidth)
            Check(panel.Position.X >= column.Right + 12, "initial FD placement sits beside the native selection column instead of covering it");
        var header = panel.Controls.Single(c => c.Name == "FD drag handle");
        var origin = NativeUi.Center(header); var before = panel.Position;
        header.DoMouseDown(origin, false, e, false, false, false);
        Check(!panel.Dragging, "right mouse button cannot start window drag");
        NativeUi.Click(panel.Controls.OfType<DWTextBox>().Single(c => c.Name == "FD Freighter limit"));
        Press(header); Check(panel.Dragging && !panel.HasTypingFocus, "native header press starts drag and releases numeric focus");
        var moved = new Point(origin.X + 80, origin.Y - 50);
        panel.DoMouseMove(new Vector2(80, -50), moved, true, false);
        Check(panel.Position != before && panel.Dragging, "drag changes real native window position");
        panel.DoMouseUp(moved, true, e, false, false, .1f); Vector2 remembered = panel.Position;
        Check(!panel.Dragging && WindowPreferences.TryLoad(out var saved) && saved == remembered, "left release finishes drag and records local position");
        FreshPanel(); FreshPanel(); Check(panel.Position == remembered, "periodic refresh does not snap a moved window to its original position");
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "X")); NativeUi.Click(dock); panel = CurrentPanel!;
        Check(panel.Position == remembered, "close and reopen restores moved FD position");
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Freighters")); UpdateUi();
        header = panel.Controls.Single(c => c.Name == "FD drag handle"); origin = NativeUi.Center(header); before = panel.Position;
        Press(header); moved = new Point(origin.X + 20, origin.Y + 20); panel.DoMouseMove(new Vector2(20), moved, true, false); panel.DoMouseUp(moved, true, e, false, false, .1f);
        Check(panel.Position != before && panel.Controls.OfType<DWButton>().Any(c => c.Text == "[x] Freighters"), "header dragging also works on the Freighters tab without changing its selection");
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Dispatch")); FreshPanel();
        Check(!panel.Dragging, "tab button does not begin a window gesture");
        var numeric = panel.Controls.OfType<DWTextBox>().Single(c => c.Name == "FD Freighter limit"); NativeUi.Click(numeric);
        Check(panel.HasTypingFocus && !panel.Dragging, "numeric field remains editable without dragging its window");
        panel.ReleaseInput(); header = panel.Controls.Single(c => c.Name == "FD drag handle");
        Press(header); panel.DoMouseMove(Vector2.Zero, NativeUi.Center(header), false, false);
        Check(!panel.Dragging, "lost left-button state stops the window gesture");
        Press(header); panel.Visible = false; panel.DoMouseMove(Vector2.Zero, NativeUi.Center(header), true, false);
        Check(!panel.Dragging, "hidden panel immediately stops an unfinished drag"); FreshPanel();
        int width = UserInterfaceController.ScreenWidth, height = UserInterfaceController.ScreenHeight;
        panel.SetSizeAndPosition(panel.Size, new Vector2(5000, 5000));
        UserInterfaceController.ScreenWidth = 1024; UserInterfaceController.ScreenHeight = 720; FreshPanel();
        var bounds = panel.CalculateAbsoluteRectangle();
        Check(bounds.Left >= 12 && bounds.Top >= 12 && bounds.Right <= 1012 && bounds.Bottom <= 708, "resolution shrink clamps the entire supported window and close/header controls on screen");
        UserInterfaceController.ScreenWidth = width; UserInterfaceController.ScreenHeight = height; FreshPanel();

        UserInterfaceController.SelectionPanel.Item = e.Capital!; FreshPanel();
        var resourceRows = (DWButton[])AccessTools.Field(typeof(Panel), "resources").GetValue(panel)!;
        Check(resourceRows.All(c => !c.Visible) && panel.Controls.OfType<DWButton>().Any(c => c.Text.Contains("a planet is not a source", StringComparison.Ordinal)),
            "selected colony produces explicit base/spaceport-source instruction instead of blank resource fields");
        var empty = e.Ships.First(s => s.ShipId != 936 && Native.ValidSource(g, e, s)); var cargo = empty.Cargo;
        try
        {
            empty.Cargo = new CargoList(); UserInterfaceController.SelectionPanel.Item = empty; FreshPanel();
            Check(resourceRows.All(c => !c.Visible) && panel.Controls.OfType<DWButton>().Any(c => c.Text == "No own free cargo at this source") &&
                panel.Controls.OfType<DWButton>().Single(c => c.Text == "Start task").Enabled == false, "empty owned base shows no-free-cargo explanation and cannot start an empty export");
        }
        finally { empty.Cargo = cargo; }
        UserInterfaceController.SelectionPanel.Item = g.Ships.GetById(936); FreshPanel();
        Check(resourceRows.Any(c => c.Visible && c.Text.Contains(g.Resources.GetById(7).Name, StringComparison.Ordinal)) ||
            ((short[])AccessTools.Field(typeof(Panel), "resourceIds").GetValue(panel)!).Contains((short)7), "rich base offers its cargo resources without first selecting a destination");
        Check(panel.Controls.OfType<DWButton>().Any(c => c.Text == "To: " + e.Capital!.GetName()), "capital destination is still supplied independently of the resource list");
        Check(EconomicFingerprint(e) == economy && JsonSerializer.Serialize(State.For(g, e.EmpireId).Data) == state, "movement, tab changes and source explanations leave cargo, contracts and FD task data unchanged");

        string path = Path.Combine(AppContext.BaseDirectory, "fd-position-restart-" + Guid.NewGuid().ToString("N") + ".json");
        WindowPreferences.UsePathForCheck(path); WindowPreferences.Save(new Vector2(400, 50)); WindowPreferences.UsePathForCheck(path);
        Check(WindowPreferences.TryLoad(out var reloaded) && reloaded == new Vector2(400, 50), "position survives preference-cache reset by reading the real local JSON file");
        string invalid = "{\"Version\":2,\"X\":500000,\"Y\":1}"; File.WriteAllText(path, invalid); WindowPreferences.UsePathForCheck(path);
        Check(!WindowPreferences.TryLoad(out _) && File.ReadAllText(path) == invalid, "invalid preference version/coordinates fall back without overwriting existing data");
        File.WriteAllText(path, "{"); WindowPreferences.UsePathForCheck(path);
        Check(!WindowPreferences.TryLoad(out _), "malformed local geometry does not prevent opening FD");
        string blocker = path + ".file"; File.WriteAllText(blocker, "not a directory"); WindowPreferences.UsePathForCheck(Path.Combine(blocker, "window.json"));
        WindowPreferences.Save(new Vector2(40, 40));
        Check(WindowPreferences.TryLoad(out var fallback) && fallback == new Vector2(40, 40) && File.ReadAllText(blocker) == "not a directory", "unwritable preference location keeps usable session placement and preserves the blocking file");
        WindowPreferences.UsePathForCheck(path); WindowPreferences.Save(panel.Position);
        return panel;
    }

    static void VerifyPeerButtons(Galaxy g, Panel panel)
    {
        var e = g.Empires.GetPlayer();
        var peers = new[] { ("BattleLog", "BattleLog", "BattleLogPanel"), ("WeaponFX", "WeaponFX", "DW2WeaponFXPanel"), ("GalaxyWaypoints", "Waypoints", "GalaxyWaypointsToolMenu") };
        var buttons = new List<DWButton>();
        string economy = EconomicFingerprint(e);
        foreach (var (assemblyName, key, windowName) in peers)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);
            var dock = assembly.GetType("LocalModUi.NativeToolDock", true)!;
            bool Available() => (bool)AccessTools.Method(dock, "ToolbarAvailable").Invoke(null, null)!;
            Check(Available(), "actual installed " + assemblyName + " dock recognizes the real open FD panel after scoped adaptation");
            DWPanel? other = null;
            // Use the peer's actual native dock/control and selection methods. The body is a controlled fixture, not rendered peer UI.
            var peerButton = (DWButton)AccessTools.Method(dock, "Register").Invoke(null, [key, key, "Controlled peer switch fixture", (Action)(() =>
            {
                AccessTools.Method(dock, "Select").Invoke(null, [key]);
                other = new DWPanel { Name = windowName, Visible = true };
                other.SetSizeAndPosition(new Vector2(300, 200), new Vector2(700, 30));
                other.AddControl(new DWButton { Name = "Controlled peer child", Visible = true }); UserInterfaceController.AddCustomControl(other);
            })])!;
            AccessTools.Method(dock, "Update").Invoke(null, [peerButton]); buttons.Add(peerButton);
            Check(peerButton.Visible, "actual peer dock button stays visible with FD open: " + key);
            NativeUi.Click(peerButton); UpdateUi();
            Check(other != null && !panel.Visible && !Entry.UiAvailable() && !panel.HasTypingFocus,
                "native peer button changes selection and suppresses FD body/input: " + key);
            if (other != null) { other.Visible = false; UserInterfaceController.RemoveCustomControl(other); }
            var ownDock = (DWButton)AccessTools.Field(typeof(Entry), "button").GetValue(null)!;
            NativeUi.Click(ownDock); panel = CurrentPanel!; FreshPanel();
            Check(panel.Visible && Entry.UiAvailable() && Available(), "FD can reopen after selecting another actual peer dock: " + key);
            var unknown = new DWPanel { Name = "Unrelated real modal fixture", Visible = true }; unknown.SetSizeAndPosition(new Vector2(250, 200), new Vector2(700, 30));
            unknown.AddControl(new DWButton { Name = "Unknown child", Visible = true }); UserInterfaceController.AddCustomControl(unknown);
            Check(!Available(), "scoped peer compatibility still blocks unrelated foreground windows: " + key);
            UserInterfaceController.RemoveCustomControl(unknown);
            var method = AccessTools.Method(dock, "IsToolWindow");
            var registered = UserInterfaceController.CustomControls[panel.Name];
            UserInterfaceController.CustomControls[panel.Name] = new DWPanel { Name = panel.Name };
            Check(!(bool)method.Invoke(null, [panel.Name])!, "a foreign control spoofing the FD name is not whitelisted: " + key);
            UserInterfaceController.CustomControls[panel.Name] = registered;
            NativeUi.EachBlocker(typeof(Entry).Assembly, _ => Check(!Available(), "native Research/dialog blocker still blocks adapted peer dock: " + key), () => { });
        }
        Check(buttons.All(b => b.Visible) && ((DWButton)AccessTools.Field(typeof(Entry), "button").GetValue(null)!).Visible, "all four actual dock controls remain visible together after switching back to FD");
        Check(economy == EconomicFingerprint(e), "peer-window recognition and dock selection leave the economy unchanged");
        foreach (var button in buttons) UserInterfaceController.RemoveCustomControl(button);
    }
}
