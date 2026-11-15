using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using LocalModUi;
using Stride.Input;

namespace FreightDispatch;

public static class Entry
{
    static readonly Harmony hooks = new("local.dw2.freight-dispatch");
    static bool initialized;
    static Panel? panel;
    static DWButton? button;
    static Galaxy? campaign;
    static short observer = -1;
    public static void Init()
    {
        if (initialized) return;
        SavedData.RequireChannels();
        try
        {
        Native.Install(hooks);
        NativeToolDock.Install(hooks);
        PeerWindows.Install(hooks);
        hooks.Patch(AccessTools.Method(typeof(Galaxy), nameof(Galaxy.WriteToStream)), prefix: new HarmonyMethod(typeof(State), nameof(State.BeforeSave)) { priority = Priority.First });
        hooks.Patch(AccessTools.Method(typeof(DWGame), "SaveGame", [typeof(string)]),
            prefix: new HarmonyMethod(typeof(State), nameof(State.BeforeGameSave)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(State), nameof(State.FinishGameSave)));
        hooks.Patch(AccessTools.Method(typeof(UserInterfaceController), "Update"), postfix: new HarmonyMethod(typeof(Entry), nameof(UpdateUi)));
        hooks.Patch(AccessTools.Method(typeof(InputSystem), nameof(InputSystem.HandleKeyPress)), prefix: new HarmonyMethod(typeof(Entry), nameof(AllowGameKey)));
        hooks.Patch(AccessTools.Method(typeof(DWTextBox), nameof(DWTextBox.AcceptKeyPress)), prefix: new HarmonyMethod(typeof(Panel), nameof(Panel.AllowSingleKey)));
        hooks.Patch(AccessTools.Method(typeof(DWTextBox), nameof(DWTextBox.AcceptKeyPresses)), prefix: new HarmonyMethod(typeof(Panel), nameof(Panel.AllowKeys)));
        initialized = true;
        }
        catch { hooks.UnpatchAll(hooks.Id); throw; }
    }
    static void UpdateUi()
    {
        if (panel != null && (!UserInterfaceController.CustomControls.TryGetValue(panel.Name, out var registered) || !ReferenceEquals(registered, panel))) Close();
        if (panel != null) panel.Visible = false;
        if (button != null) button.Visible = false;
        var game = Galaxy.Game as DWGame;
        var current = game?.Galaxy;
        short player = current?.Empires.GetPlayer()?.EmpireId ?? (short)-1;
        if (!ReferenceEquals(current, campaign) || player != observer)
        { Close(); campaign = current; observer = player; }
        if (current == null || player < 0) { NativeToolDock.Detach(button); return; }
        var session = State.For(current, player);
        button ??= NativeToolDock.Register(NativeToolDock.Freight, "Freight Dispatch", "Source priority, physical freight tasks and cargo/mission queues for all own freighters.", Toggle);
        NativeToolDock.Update(button);
        if (!UiAvailable()) { panel?.ReleaseInput(); return; }
        State.NotifySaveFailure(game!, session);
        if (panel != null)
        {
            panel.Visible = true;
            panel.Refresh(current, current.Empires.GetPlayer());
            if (game?.Input?.IsKeyPressed(Keys.Escape) == true) Close();
        }
    }
    static bool AllowGameKey(KeyMapping keyMapping)
    {
        if (!UiAvailable()) return true;
        if (keyMapping?.Key == Keys.Escape && panel is { Visible: true }) { Close(); return false; }
        if (panel?.HasTypingFocus == true) return false;
        return true;
    }
    static void Toggle()
    {
        if ((Galaxy.Game as DWGame)?.IsActive != true || !NativeToolDock.ToolbarAvailable() || !MapUiGate.TryGetContext(out _, out var g) || !ReferenceEquals(g, campaign)) return;
        if (panel != null && NativeToolDock.Selected == NativeToolDock.Freight) { Close(); return; }
        NativeToolDock.Select(NativeToolDock.Freight);
        panel ??= new Panel(Close);
        panel.Visible = true; panel.Refresh(g, g.Empires.GetPlayer());
        UserInterfaceController.AddCustomControl(panel);
    }
    static void Close()
    {
        NativeToolDock.Clear(NativeToolDock.Freight);
        if (panel == null) return;
        panel.ReleaseInput(); panel.Visible = false;
        MapUiGate.ReleaseHover(panel); UserInterfaceController.RemoveCustomControl(panel); panel = null;
    }
    internal static bool UiAvailable() => (Galaxy.Game as DWGame)?.IsActive == true &&
        MapUiGate.TryGetContext(out _, out var g) && ReferenceEquals(g, campaign) && g.Empires.GetPlayer()?.EmpireId == observer &&
        NativeToolDock.BodyAvailable(NativeToolDock.Freight);
    internal static bool OwnsRegisteredPanel() => panel != null && UserInterfaceController.CustomControls.TryGetValue(panel.Name, out var control) && ReferenceEquals(control, panel);
}
