using System.Globalization;
using DistantWorlds.Types;
using DistantWorlds.UI;
using LocalModUi;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace FreightDispatch;

sealed partial class Panel : NativeToolWindow
{
    readonly DWButton sourceLabel, priority, destinationLabel, status, taskLabel, share, totals, pageLabel, start, cancel, resourceHint;
    readonly DWButton[] resources = new DWButton[5];
    readonly InputBox[] reserves = new InputBox[5];
    readonly InputBox limit;
    readonly Dictionary<short, (bool Selected, string Reserve)> draft = [];
    Galaxy? galaxy; Empire? empire; Ship? source; StellarObject? destination;
    short[] resourceIds = [];
    int page; string message = ""; DispatchTask? task; long lastResourceRefresh, lastStatusRefresh;
    bool wasTaskOpen;
    bool CanInteract => Entry.UiAvailable() && CheckVisible();
    internal bool HasTypingFocus => CanInteract && UserInterfaceController.ActiveTextBox is InputBox box && box.CanType;

    internal Panel(Action close) : base("Freight Dispatch", true)
    {
        Name = "FreightDispatchPanel"; Layer = 970; PostScene = Visible = true;
        Initialize(FontSize.Small, FontSize.Small, null!);
        Add("X", 608, 4, 28, 26, close);
        sourceLabel = Add("Select an owned base", 12, 38, 620, 26);
        priority = Add("Priority: Normal", 12, 68, 220, 28, TogglePriority);
        Add("Capital", 238, 68, 110, 28, () => { if (empire != null) destination = empire.Capital; });
        Add("<", 12, 102, 32, 26, () => MoveDestination(-1));
        destinationLabel = Add("Destination", 48, 102, 546, 26);
        Add(">", 598, 102, 34, 26, () => MoveDestination(1));
        resourceHint = Add("Select an owned base/spaceport first", 12, 135, 425, 24);
        Add("Retain at source", 444, 135, 188, 24);
        for (int i = 0; i < resources.Length; i++)
        {
            int slot = i;
            resources[i] = Add("", 12, 162 + i * 32, 425, 28, () => ToggleResource(slot));
            reserves[i] = Box("Reserve " + i, 444, 162 + i * 32, 188, "0");
        }
        Add("<", 12, 326, 34, 26, () => ChangePage(-1));
        pageLabel = Add("", 50, 326, 330, 26);
        Add(">", 384, 326, 34, 26, () => ChangePage(1));
        Add("Freighter limit", 12, 362, 150, 28);
        limit = Box("Freighter limit", 166, 362, 90, "2");
        start = Add("Start task", 272, 362, 170, 28, Start);
        cancel = Add("Cancel task", 454, 362, 178, 28, Cancel);
        taskLabel = Add("", 12, 398, 620, 26);
        share = Add("", 12, 428, 620, 26);
        totals = Add("", 12, 458, 620, 26);
        status = Add("", 12, 488, 620, 44);
        Add("Fleet total includes repair/retrofit; it is not the idle fleet.", 12, 536, 620, 24);
        Add("Manual reserves replace automatic retention only for this task.", 12, 562, 620, 24);
        InitializeFleet();
        FillRows(); InitializeMovement();
    }
    DWButton Add(string text, float x, float y, float width, float height, Action? click = null)
    {
        var button = new PanelButton(() => CanInteract, () => { if (CanInteract) { click?.Invoke(); lastStatusRefresh = 0; } }) { Name = "FD control " + Controls.Count, Text = text, Visible = true, PostScene = true };
        button.Initialize(FontSize.Small, FontSize.Small, null!); button.SetSizeAndPosition(new Vector2(width, height), new Vector2(x, y));
        AddControl(button); return button;
    }
    sealed class PanelButton(Func<bool> available, Action click) : ToolActionButton(available)
    { protected override void Click() => click(); }
    InputBox Box(string name, float x, float y, float width, string text)
    {
        var box = new InputBox(this) { Name = "FD " + name, Text = text, EnterDeactivates = true, Visible = true, PostScene = true };
        box.Initialize(FontSize.Small, FontSize.Small, null!); box.SetSizeAndPosition(new Vector2(width, 28), new Vector2(x, y)); AddControl(box); return box;
    }
    internal void Refresh(Galaxy g, Empire e)
    {
        galaxy = g; empire = e;
        Place();
        if (fleetView) { RefreshFleet(g, e); return; }
        var selected = UserInterfaceController.ResolveSelectedObject() as Ship;
        if (selected != null && !Native.ValidSource(g, e, selected)) selected = null;
        if (!ReferenceEquals(source, selected))
        {
            ReleaseInput(); source = selected; page = 0; draft.Clear(); message = ""; lastStatusRefresh = 0;
            destination = e.Capital; limit.Text = "2";
            resourceIds = source?.Cargo?.Where(c => c.EmpireId == e.EmpireId && (float)c.Amount > 0).Select(c => c.ResourceId).Distinct().Order().ToArray() ?? [];
            foreach (short id in resourceIds) draft[id] = (false, "0");
            FillRows();
        }
        long refreshAt = Environment.TickCount64;
        if (refreshAt - lastStatusRefresh < 1000) return;
        lastStatusRefresh = refreshAt;
        var session = State.For(g, e.EmpireId);
        lock (session.Gate)
        {
            task = session.Data?.Tasks.LastOrDefault(t => t.SourceId == source?.ShipId);
            long now = Environment.TickCount64;
            if (source != null && now - lastResourceRefresh >= 1000)
            {
                lastResourceRefresh = now;
                var ids = source.Cargo.Where(c => c.EmpireId == e.EmpireId && (float)c.Amount > 0).Select(c => c.ResourceId)
                    .Concat(task?.Resources.Select(r => r.ResourceId) ?? []).Concat(draft.Keys).Distinct().Order().ToArray();
                if (!ids.SequenceEqual(resourceIds))
                {
                    CaptureRows(); ReleaseInput(); resourceIds = ids;
                    foreach (short id in ids) draft.TryAdd(id, (false, "0"));
                    page = Math.Clamp(page, 0, Math.Max(0, (ids.Length - 1) / resources.Length)); FillRows();
                }
            }
            bool open = task != null && Dispatch.IsOpen(task);
            if (!wasTaskOpen && open) ReleaseInput();
            if (wasTaskOpen && !open) FillRows();
            wasTaskOpen = open;
            sourceLabel.Text = source == null ? "Select an owned base on the map" : "Source: " + source.Name;
            priority.Text = "Priority: " + (source != null && session.Data?.HighSources.Contains(source.ShipId) == true ? "High" : "Normal");
            priority.Enabled = source != null && session.Fault == null;
            start.Enabled = source != null && !open && resourceIds.Length > 0 && session.Fault == null;
            cancel.Enabled = task?.Phase == DispatchPhase.Active && session.Fault == null;
            destinationLabel.Text = "To: " + (open ? Native.Destination(g, task!)?.GetName() ?? "Missing destination" : destination?.GetName() ?? "No owned destination");
            resourceHint.Text = source == null ? "Select an owned base/spaceport first" : resourceIds.Length == 0 ? "No own free cargo at this source" : "Select resource / free cargo";
            taskLabel.Text = task == null ? "No task for this source" : $"Task {task.Id}: {task.Phase}";
            int fleet = e.Ships.Where(s => Native.EconomyFreighter(e, s)).Select(s => s.ShipId).Distinct().Count();
            share.Text = task != null && open ? Dispatch.FleetShare(Dispatch.Assigned(g, e, [task]).Count, fleet, task.Limit) :
                int.TryParse(limit.Text, out int requested) && requested is >= 1 and <= 1000 ?
                    FormattableString.Invariant($"Assigned 0 / {fleet}; requested up to {requested} ({(fleet == 0 ? 0 : 100d * requested / fleet):0.0}%)") : $"Fleet total {fleet}; enter a valid freighter limit";
            double delivered = task?.Resources.Sum(r => r.Delivered) ?? 0;
            double remaining = source == null || task == null ? 0 : task.Resources.Sum(r => Math.Max(0, Native.Available(source, e, r.ResourceId) - r.Retain));
            totals.Text = task == null ? "" : $"Delivered {delivered:0.#}; remaining at source {remaining:0.#}";
            status.Text = session.Fault ?? (message.Length > 0 ? message : task?.Status ??
                (source == null ? "Select an owned base/spaceport as source; a planet is not a source." :
                 resourceIds.Length == 0 ? "No own free cargo here; destination does not determine the resource list." : "Choose resources and reserves, then start"));
            for (int i = 0; i < resources.Length; i++)
            {
                int index = page * resources.Length + i;
                resources[i].Enabled = index < resourceIds.Length && !open && session.Fault == null;
                reserves[i].Editable = resources[i].Enabled;
                if (index >= resourceIds.Length) continue;
                short id = resourceIds[index];
                var activeResource = open ? task!.Resources.FirstOrDefault(r => r.ResourceId == id) : null;
                bool chosen = open ? activeResource != null : draft[id].Selected;
                resources[i].Text = $"{(chosen ? "[x]" : "[ ]")} {g.Resources.GetById(id)?.Name ?? id.ToString()} / {Native.Available(source!, e, id):0.#}";
                if (open) reserves[i].Text = activeResource?.Retain.ToString(CultureInfo.InvariantCulture) ?? "-";
            }
            limit.Editable = !open && session.Fault == null;
            if (open) limit.Text = task!.Limit.ToString(CultureInfo.InvariantCulture);
        }
    }
    void CaptureRows()
    {
        if (task != null && Dispatch.IsOpen(task)) return;
        for (int i = 0; i < reserves.Length; i++)
        { int index = page * reserves.Length + i; if (index < resourceIds.Length) { short id = resourceIds[index]; draft[id] = (draft[id].Selected, reserves[i].Text); } }
    }
    void FillRows()
    {
        for (int i = 0; i < reserves.Length; i++)
        { int index = page * reserves.Length + i; bool exists = index < resourceIds.Length; resources[i].Visible = reserves[i].Visible = exists; reserves[i].Text = exists ? draft[resourceIds[index]].Reserve : ""; }
        pageLabel.Text = $"Resources {page + 1}/{Math.Max(1, (resourceIds.Length + 4) / 5)}";
    }
    void ChangePage(int delta) { CaptureRows(); ReleaseInput(); page = Math.Clamp(page + delta, 0, Math.Max(0, (resourceIds.Length - 1) / 5)); FillRows(); }
    void ToggleResource(int slot)
    { CaptureRows(); int i = page * resources.Length + slot; if (i < resourceIds.Length) { short id = resourceIds[i]; draft[id] = (!draft[id].Selected, draft[id].Reserve); } }
    void MoveDestination(int delta)
    {
        if (galaxy == null || empire == null || task != null && Dispatch.IsOpen(task)) return;
        var choices = empire.Colonies.Cast<StellarObject>().Concat(empire.Spaceports.Cast<StellarObject>()).Where(d => Native.ValidDestination(galaxy, empire, d) && (source == null || !Native.SameHold(source, d))).ToArray();
        if (choices.Length == 0) { destination = null; return; }
        int index = Array.IndexOf(choices, destination); destination = choices[(index + delta + choices.Length) % choices.Length];
    }
    void TogglePriority()
    {
        if (galaxy == null || empire == null || source == null) return;
        var session = State.For(galaxy, empire.EmpireId);
        if (Dispatch.SetPriority(galaxy, empire, source, session.Data?.HighSources.Contains(source.ShipId) != true, out message)) message = "";
    }
    void Start()
    {
        if (galaxy == null || empire == null || source == null || destination == null) return;
        CaptureRows(); var selected = new List<DispatchResource>();
        foreach (var pair in draft.Where(p => p.Value.Selected))
        {
            if (!float.TryParse(pair.Value.Reserve, NumberStyles.Float, CultureInfo.InvariantCulture, out float reserve) || !float.IsFinite(reserve) || reserve < 0)
            { message = "Reserve must be a nonnegative number (use a decimal point)"; return; }
            selected.Add(new DispatchResource { ResourceId = pair.Key, Retain = reserve });
        }
        if (!int.TryParse(limit.Text, out int count)) { message = "Freighter limit must be a whole number"; return; }
        if (Dispatch.Start(galaxy, empire, source, destination, selected, count, out message)) { ReleaseInput(); message = ""; }
    }
    void Cancel() { if (galaxy != null && empire != null && task != null && Dispatch.Cancel(galaxy, empire, task.Id, out message)) { ReleaseInput(); message = ""; } }
    internal void ReleaseInput()
    {
        dragging = false;
        ToolActionButton.ClearPress(); MapUiGate.ReleaseHover(this);
        foreach (var box in Controls.OfType<InputBox>())
        { if (box == null) continue; box.IsActive = false; if (ReferenceEquals(UserInterfaceController.ActiveTextBox, box)) UserInterfaceController.ActiveTextBox = null!; }
    }
    public override void Render(RenderDrawContext c, SpriteBatch b, Point p, bool tips) { if (CanInteract) base.Render(c, b, p, tips); }
    public override bool DoMouseDown(Point p, bool left, Empire e, bool shift, bool ctrl, bool child) => CanInteract && base.DoMouseDown(p, left, e, shift, ctrl, child);
    public override bool DoMouseUp(Point p, bool left, Empire e, bool shift, bool ctrl, float time) => dragging ? EndDrag(left) : CanInteract && base.DoMouseUp(p, left, e, shift, ctrl, time);
    public override bool DoMouseMove(Vector2 move, Point p, bool left, bool right) => dragging ? MoveDrag(p, left) : CanInteract && base.DoMouseMove(move, p, left, right);
    sealed class InputBox(Panel panel) : DWTextBox
    {
        internal bool Editable = true;
        internal bool CanType => panel.CanInteract && CheckVisible() && Editable;
        public override bool DoMouseDown(Point p, bool left, Empire e, bool shift, bool ctrl, bool child) => CanType && base.DoMouseDown(p, left, e, shift, ctrl, child);
        public override bool DoMouseUp(Point p, bool left, Empire e, bool shift, bool ctrl, float time) => CanType && base.DoMouseUp(p, left, e, shift, ctrl, time);
        public override bool DoMouseMove(Vector2 move, Point p, bool left, bool right) => CanType && base.DoMouseMove(move, p, left, right);
    }
    internal static bool AllowKeys(DWTextBox __instance, ref bool __result)
    { if (__instance is not InputBox box || box.CanType) return true; __result = false; return false; }
    internal static bool AllowSingleKey(DWTextBox __instance, ref bool __result, ref bool __5)
    { if (AllowKeys(__instance, ref __result)) return true; __5 = false; return false; }
}
