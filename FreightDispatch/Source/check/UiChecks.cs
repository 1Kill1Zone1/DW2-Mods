using DistantWorlds.Types;
using DistantWorlds.UI;
using FreightDispatch;
using HarmonyLib;
using Stride.Input;
using UiChecks;

namespace DW2ModLauncher.Loader;
static partial class Program
{
    static void UpdateUi() => AccessTools.Method(typeof(Entry), "UpdateUi").Invoke(null, null);
    static Panel? CurrentPanel => (Panel?)AccessTools.Field(typeof(Entry), "panel").GetValue(null);
    static void VerifyUi(Galaxy g)
    {
        WindowPreferences.UsePathForCheck(Path.Combine(AppContext.BaseDirectory, "fd-window-" + Guid.NewGuid().ToString("N") + ".json"));
        using var ui = new NativeUi(g);
        var activeField = AccessTools.Field(ui.Host.GetType(), "<IsActive>k__BackingField");
        activeField.SetValue(ui.Host, true); // Same active-host bootstrap as the native Battle Log fixture.
        using var hud = new NativeHud(ui, g);
        UserInterfaceController.SelectionPanel.Item = g.Ships.GetById(936);
        Check(ReferenceEquals(UserInterfaceController.ResolveSelectedObject(), g.Ships.GetById(936)), "native selection resolves Ghost Fleet Base for panel workflow");
        UpdateUi();
        var button = (DWButton)AccessTools.Field(typeof(Entry), "button").GetValue(null)!;
        Check(button.Visible, "Freight Dispatch entry visible on unobstructed native map");
        NativeUi.Click(button);
        var panel = CurrentPanel ?? throw new Exception("Panel did not open");
        Check(panel.Controls.Select(c => c.Name).Distinct().Count() == panel.Controls.Count && panel.Controls.OfType<DWButton>().Count(c => c.Position.X == 12 && c.Position.Y >= 162 && c.Position.Y <= 290) == 5, "panel retains five resource rows and unique names for every native child");
        Check(panel.Visible && UserInterfaceController.CustomControls.ContainsKey("FreightDispatchPanel"), "real native mouse click opens Freight Dispatch panel");
        var bounds = panel.CalculateAbsoluteRectangle();
        Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 1280 && bounds.Bottom <= 720 && panel.Controls.All(c =>
        { var r = c.CalculateAbsoluteRectangle(); return r.Left >= bounds.Left && r.Right <= bounds.Right && r.Top >= bounds.Top && r.Bottom <= bounds.Bottom; }), "panel controls fit1280x720");
        int fleet = g.Empires.GetPlayer().Ships.Count(s => Native.EconomyFreighter(g.Empires.GetPlayer(), s));
        Check(panel.Controls.OfType<DWButton>().Any(c => c.Text.Contains($"Assigned 0 / {fleet}", StringComparison.Ordinal)), "before-start display distinguishes actual zero allocation from requested quota");
        VerifyPanelWorkflow(g, panel);
        VerifyFreightersUi(g, panel);
        panel = VerifyWindowAndEmptyUi(g, panel, button);
        VerifyPeerButtons(g, panel);
        var limit = panel.Controls.OfType<DWTextBox>().Single(c => c.Name == "FD Freighter limit");
        NativeUi.Click(limit);
        Check(panel.HasTypingFocus, "native numeric field takes typing focus");
        activeField.SetValue(ui.Host, false); UpdateUi();
        Check(!panel.Visible && !panel.HasTypingFocus && !ReferenceEquals(UserInterfaceController.ActiveTextBox, limit), "native host focus loss hides panel and releases numeric input");
        NativeUi.Click(button); Check(CurrentPanel == panel && !panel.Visible, "inactive host cannot reopen panel through stale dock click");
        activeField.SetValue(ui.Host, true); UpdateUi();
        NativeUi.Click(limit);
        var gate = AccessTools.Method(typeof(Entry), "AllowGameKey");
        Check(!(bool)gate.Invoke(null, [new KeyMapping { Key = Keys.D1 }])!, "number input cannot trigger native fleet hotkey while editing");
        NativeUi.EachBlocker(typeof(Entry).Assembly, _ =>
        {
            UpdateUi();
            Check(!button.Visible && !panel.Visible && !ReferenceEquals(UserInterfaceController.ActiveTextBox, limit), "native blocker hides controls and releases numeric focus");
            bool result = false; object?[] args = [limit, result];
            Check(!(bool)AccessTools.Method(typeof(Panel), "AllowKeys").Invoke(null, args)!, "hidden numeric field refuses keyboard input");
        }, () => { UpdateUi(); Check(button.Visible, "map entry restored after blocker closes"); });
        UpdateUi();
        var close = panel.Controls.OfType<DWButton>().Single(c => c.Text == "X");
        UserInterfaceController.HoveredControl = close;
        UserInterfaceController.DoMouseUp(new GameSettings(), NativeUi.Center(close), true, null!, false, false, .1f);
        Check(CurrentPanel == panel, "mouse release without owned press cannot close panel");
        NativeUi.Click(close, false); Check(CurrentPanel == panel, "right click cannot activate close");
        Check(!(bool)gate.Invoke(null, [new KeyMapping { Key = Keys.Escape }])! && CurrentPanel == null, "Escape closes tool and is consumed");
        Check((bool)gate.Invoke(null, [new KeyMapping { Key = Keys.Escape }])!, "next Escape retains native action");
        NativeUi.Click(button); panel = CurrentPanel ?? throw new Exception("Panel reopen failed");
        close = panel.Controls.OfType<DWButton>().Single(c => c.Text == "X"); NativeUi.Click(close);
        Check(CurrentPanel == null, "native close button releases panel");
        NativeUi.Click(button); panel = CurrentPanel ?? throw new Exception("Panel reopen failed");
        UserInterfaceController.RemoveCustomControl(panel); UpdateUi();
        Check(CurrentPanel == null && button.Visible, "native custom-control reset releases detached panel and preserves usable entry");
        NativeUi.Click(button); panel = CurrentPanel ?? throw new Exception("Panel reopen failed");
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Freighters")); UpdateUi();
        var priorSnapshot = AccessTools.Field(typeof(Panel), "fleetSnapshot").GetValue(panel);
        var search = panel.Controls.OfType<DWTextBox>().Single(c => c.Name == "FD Search name"); NativeUi.Click(search);
        AccessTools.Field(ui.Host.GetType(), "_Galaxy").SetValue(ui.Host, null); UpdateUi();
        Check(CurrentPanel == null && !panel.HasTypingFocus && !UserInterfaceController.CustomControls.ContainsKey(panel.Name), "campaign context loss closes fleet tab and clears search focus");
        AccessTools.Field(ui.Host.GetType(), "_Galaxy").SetValue(ui.Host, g); UpdateUi();
        NativeUi.Click(button); panel = CurrentPanel ?? throw new Exception("Panel did not reopen after context reset");
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Freighters")); UpdateUi();
        Check(!ReferenceEquals(priorSnapshot, AccessTools.Field(typeof(Panel), "fleetSnapshot").GetValue(panel)), "new campaign context gets a fresh fleet snapshot instead of prior panel cache");
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "X"));
    }
    static void VerifyPanelWorkflow(Galaxy g, Panel panel)
    {
        var e = g.Empires.GetPlayer(); var capital = e.Capital as Orb ?? throw new Exception("UI fixture capital is not a colony"); string resource = g.Resources.GetById(7).Name;
        DWButton? row = null;
        for (int page = 0; page < 52 && row == null; page++)
        {
            row = panel.Controls.OfType<DWButton>().FirstOrDefault(c => c.Visible && c.Text.StartsWith("[ ] " + resource + " /", StringComparison.Ordinal));
            if (row == null)
            {
                if (page < 3) Console.WriteLine("UI-RESOURCE-PAGE " + string.Join(" | ", panel.Controls.OfType<DWButton>().Where(c => c.Position.Y >= 162 && c.Position.Y <= 290).Select(c => $"{c.Name} visible={c.Visible} text={c.Text}")));
                var next = panel.Controls.OfType<DWButton>().Single(c => c.Text == ">" && c.Position.Y == 326);
                int beforePage = (int)AccessTools.Field(typeof(Panel), "page").GetValue(panel)!;
                if (page < 3) Console.WriteLine($"UI-PAGE before={AccessTools.Field(typeof(Panel), "page").GetValue(panel)} enabled={next.Enabled} visible={next.CheckVisible()} canInteract={AccessTools.Property(typeof(Panel), "CanInteract").GetValue(panel)} bounds={next.CalculateAbsoluteRectangle()}");
                NativeUi.Click(next); UpdateUi();
                int count = ((short[])AccessTools.Field(typeof(Panel), "resourceIds").GetValue(panel)!).Length;
                Check((int)AccessTools.Field(typeof(Panel), "page").GetValue(panel)! == Math.Min(beforePage + 1, Math.Max(0, (count - 1) / 5)), "one native next-page click advances exactly one page");
                if (page < 3) Console.WriteLine("UI-PAGE after=" + AccessTools.Field(typeof(Panel), "page").GetValue(panel));
            }
        }
        Check(row != null, "owned source resources can be selected through panel paging");
        if (row == null) throw new Exception("Silicon resource row missing");
        NativeUi.Click(row);
        var reserve = panel.Controls.OfType<DWTextBox>().Single(c => c.Position.Y == row.Position.Y);
        NativeUi.Click(reserve); reserve.Text = "300";
        var limit = panel.Controls.OfType<DWTextBox>().Single(c => c.Name == "FD Freighter limit");
        NativeUi.Click(limit); limit.Text = "2";
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Start task")); UpdateUi();
        Check(panel.Controls.OfType<DWTextBox>().All(c => !c.IsActive && !ReferenceEquals(UserInterfaceController.ActiveTextBox, c)), "starting task releases numeric focus before fields become read-only");
        var task = State.For(g, e.EmpireId).Data!.Tasks.Single();
        Check(task.SourceId == 936 && task.DestinationId == capital.OrbId && task.Limit == 2 && task.Resources.Single().ResourceId == 7 && task.Resources.Single().Retain == 300, "native Start click binds selected source, default capital, resource, reserve300 and limit2 exactly");
        Check(panel.Controls.OfType<DWButton>().Any(c => c.Text == Dispatch.FleetShare(0, e.Ships.Count(s => Native.EconomyFreighter(e, s)), 2)), "active panel displays actual zero assignments separately from limit2");
        var priority = panel.Controls.OfType<DWButton>().Single(c => c.Text.StartsWith("Priority: ", StringComparison.Ordinal));
        NativeUi.Click(priority); UpdateUi();
        Check(panel.Controls.OfType<DWButton>().Any(c => c.Position.Y == 488 && c.Text == task.Status), "successful native Priority click leaves active task status visible");
        // Controlled status transition isolates display precedence, not autonomous scheduler timing.
        task.Status = "Waiting: destination has no cargo acceptance/space";
        for (int refresh = 0; refresh < 2; refresh++)
        {
            AccessTools.Field(typeof(Panel), "lastStatusRefresh").SetValue(panel, 0L); UpdateUi();
            Check(panel.Controls.OfType<DWButton>().Any(c => c.Position.Y == 488 && c.Text == task.Status), "subsequent waiting status stays visible instead of stale Priority confirmation, refresh " + refresh);
        }
        NativeUi.Click(priority); UpdateUi();
        NativeUi.Click(panel.Controls.OfType<DWButton>().Single(c => c.Text == "Cancel task")); UpdateUi();
        Check(task.Phase == DispatchPhase.Cancelling && task.Voyages.Count == 0, "native Cancel click stops task without allocating a ship");
        Tick(g, e);
        AccessTools.Field(typeof(Panel), "lastStatusRefresh").SetValue(panel, 0L); UpdateUi();
        Check(task.Phase == DispatchPhase.Cancelled, "panel-started empty task reaches cancelled state and restores editing");
        Check(reserve.Text == "300" && panel.Controls.OfType<DWTextBox>().Where(c => c.Name.StartsWith("FD Reserve ", StringComparison.Ordinal) && c.Visible).All(c => c.Text != "-"), "task end restores editable draft reserves including selected300 instead of active-view placeholders");
    }
}
