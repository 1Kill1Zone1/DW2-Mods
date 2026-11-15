using DistantWorlds.Types;
using DistantWorlds.UI;

namespace FreightDispatch;

sealed partial class Panel
{
    DWControl[] dispatchControls = [], fleetControls = [];
    DWButton dispatchTab = null!, fleetTab = null!, fleetHeader = null!, fleetPages = null!, fleetPrevious = null!, fleetNext = null!;
    DWButton stateFilter = null!, scopeFilter = null!, resourceFilter = null!, detailHeader = null!, detailPages = null!, detailPrevious = null!, detailNext = null!;
    InputBox fleetSearch = null!;
    readonly DWButton[] shipRows = new DWButton[5], detailRows = new DWButton[7];
    bool fleetView, fleetDirty = true;
    int fleetPage, detailPage, selectedShip = -1, filterScope, filterActivity;
    short? filterResource;
    long lastFleetRead, lastFleetHeader;
    string lastSearch = "", sampleError = "";
    FreightSnapshot? fleetSnapshot;
    FreighterInfo[] filteredShips = [];
    FreightCargo[] filterResources = [];

    void InitializeFleet()
    {
        dispatchControls = Controls.Where(c => c.Position.Y >= 38).ToArray();
        dispatchTab = Add("[x] Dispatch", 180, 4, 122, 26, () => SwitchView(false));
        fleetTab = Add("Freighters", 310, 4, 142, 26, () => SwitchView(true));
        int first = Controls.Count;
        Add("Name", 14, 38, 62, 28);
        fleetSearch = Box("Search name", 80, 38, 190, "");
        Add("<", 280, 38, 28, 28, () => CycleResource(-1));
        resourceFilter = Add("All resources", 312, 38, 284, 28);
        Add(">", 602, 38, 28, 28, () => CycleResource(1));
        stateFilter = Add("State: All", 14, 72, 300, 28, () =>
        { filterActivity = (filterActivity + 1) % (Enum.GetValues<FreightActivity>().Length + 1); ResetFleetFilter(); });
        scopeFilter = Add("Assignment: All", 326, 72, 304, 28, () => { filterScope = (filterScope + 1) % 3; ResetFleetFilter(); });
        fleetHeader = Add("", 14, 104, 616, 24);
        for (int i = 0; i < shipRows.Length; i++)
        {
            int slot = i;
            shipRows[i] = Add("", 14, 130 + i * 38, 616, 36, () => SelectShip(slot));
        }
        fleetPrevious = Add("< Ships", 14, 326, 88, 28, () => { fleetPage--; fleetDirty = true; });
        fleetPages = Add("", 108, 326, 420, 28);
        fleetNext = Add("Ships >", 538, 326, 92, 28, () => { fleetPage++; fleetDirty = true; });
        detailHeader = Add("Select a freighter for full details", 14, 360, 616, 24);
        for (int i = 0; i < detailRows.Length; i++) detailRows[i] = Add("", 14, 388 + i * 22, 616, 22);
        detailPrevious = Add("< Details", 14, 552, 102, 28, () => { detailPage--; fleetDirty = true; });
        detailPages = Add("", 122, 552, 398, 28);
        detailNext = Add("Details >", 526, 552, 104, 28, () => { detailPage++; fleetDirty = true; });
        fleetControls = Controls.Skip(first).ToArray();
        foreach (var c in fleetControls) c.Visible = false;
    }

    void SwitchView(bool fleet)
    {
        if (fleetView == fleet) return;
        if (!fleetView) CaptureRows();
        ReleaseInput(); fleetView = fleet;
        foreach (var c in dispatchControls) c.Visible = !fleet;
        foreach (var c in fleetControls) c.Visible = fleet;
        dispatchTab.Text = fleet ? "Dispatch" : "[x] Dispatch";
        fleetTab.Text = fleet ? "[x] Freighters" : "Freighters";
        if (fleet) fleetDirty = true;
        else { FillRows(); lastStatusRefresh = 0; }
    }

    void RefreshFleet(Galaxy g, Empire e)
    {
        long now = Environment.TickCount64;
        if (lastFleetRead == 0 || now - lastFleetRead >= 2000)
        {
            lastFleetRead = now;
            try
            {
                fleetSnapshot = FreightOverview.Read(g, e); sampleError = "";
                filterResources = fleetSnapshot.Ships.SelectMany(s => s.Resources).DistinctBy(c => c.Id).OrderBy(c => c.Name).ToArray();
                if (filterResource != null && !filterResources.Any(c => c.Id == filterResource)) filterResource = null;
            }
            catch (Exception ex) { sampleError = "Snapshot stale/unavailable: " + ex.Message; }
            fleetDirty = true;
        }
        if (fleetSearch.Text != lastSearch)
        { lastSearch = fleetSearch.Text; ResetFleetFilter(); }
        if (fleetDirty || now - lastFleetHeader >= 1000)
        {
            lastFleetHeader = now;
            string timestamp = fleetSnapshot == null ? "No snapshot" : $"Sample {fleetSnapshot.Taken:HH:mm:ss} / age {Math.Max(0, (DateTimeOffset.Now - fleetSnapshot.Taken).TotalSeconds):0}s";
            int unavailable = fleetSnapshot?.Ships.Count(s => s.Error != null) ?? 0;
            fleetHeader.Text = Fit($"{timestamp} | {fleetSnapshot?.Ships.Length ?? 0} ships | " +
                (sampleError.Length > 0 ? "STALE - details below" : unavailable > 0 ? $"{unavailable} unavailable" : fleetSnapshot?.Warning != null ? "FD metadata warning" : "read only"), 598);
        }
        if (!fleetDirty) return;
        fleetDirty = false;
        var activity = filterActivity == 0 ? (FreightActivity?)null : (FreightActivity)(filterActivity - 1);
        filteredShips = FreightOverview.Filter(fleetSnapshot?.Ships ?? [], lastSearch, filterResource, activity, filterScope);
        fleetPage = Math.Clamp(fleetPage, 0, Math.Max(0, (filteredShips.Length - 1) / shipRows.Length));
        stateFilter.Text = "State: " + (activity?.ToString() ?? "All");
        scopeFilter.Text = "Assignment: " + new[] { "All", "FD task", "Native / other" }[filterScope];
        resourceFilter.Text = Fit(filterResources.FirstOrDefault(c => c.Id == filterResource)?.Name ?? "All resources (hold + plan)", 268);
        fleetPages.Text = $"{filteredShips.Length} matching | page {fleetPage + 1}/{Math.Max(1, (filteredShips.Length + 4) / 5)}";
        fleetPrevious.Enabled = fleetPage > 0; fleetNext.Enabled = (fleetPage + 1) * shipRows.Length < filteredShips.Length;
        for (int i = 0; i < shipRows.Length; i++)
        {
            int index = fleetPage * shipRows.Length + i;
            shipRows[i].Enabled = index < filteredShips.Length;
            shipRows[i].Text = index >= filteredShips.Length ? (i == 0 ? "No matching freighters" : "") :
                ShipSummary(filteredShips[index], filteredShips[index].Id == selectedShip);
        }
        var selected = filteredShips.FirstOrDefault(s => s.Id == selectedShip);
        if (selected == null)
        {
            selected = filteredShips.ElementAtOrDefault(fleetPage * shipRows.Length);
            selectedShip = selected?.Id ?? -1; detailPage = 0;
        }
        detailHeader.Text = selected == null ? "No matching freighters" : Fit($"#{selected.Id} {selected.Name} | full details below", 598);
        var lines = new List<string>();
        if (sampleError.Length > 0) lines.Add(sampleError);
        if (fleetSnapshot?.Warning != null) lines.Add(fleetSnapshot.Warning);
        if (filterResource != null) lines.Add("Resource filter: " + filterResources.First(c => c.Id == filterResource).Name);
        lines.AddRange(selected == null ? ["Change filters or wait for the next sample."] : FreightOverview.Details(selected));
        var wrapped = lines.SelectMany(line => Wrap(line, 598)).ToArray();
        detailPage = Math.Clamp(detailPage, 0, Math.Max(0, (wrapped.Length - 1) / detailRows.Length));
        for (int i = 0; i < detailRows.Length; i++) detailRows[i].Text = wrapped.ElementAtOrDefault(detailPage * detailRows.Length + i) ?? "";
        detailPages.Text = $"Details {detailPage + 1}/{Math.Max(1, (wrapped.Length + detailRows.Length - 1) / detailRows.Length)} | current state; no history";
        detailPrevious.Enabled = detailPage > 0; detailNext.Enabled = (detailPage + 1) * detailRows.Length < wrapped.Length;
    }

    void ResetFleetFilter() { fleetPage = detailPage = 0; fleetDirty = true; }
    void CycleResource(int delta)
    {
        int current = filterResource == null ? 0 : Array.FindIndex(filterResources, c => c.Id == filterResource) + 1;
        int index = (current + delta + filterResources.Length + 1) % (filterResources.Length + 1);
        filterResource = index == 0 ? null : filterResources[index - 1].Id;
        ResetFleetFilter();
    }
    void SelectShip(int slot)
    {
        var ship = filteredShips.ElementAtOrDefault(fleetPage * shipRows.Length + slot);
        if (ship != null) { selectedShip = ship.Id; detailPage = 0; fleetDirty = true; }
    }

    static string ShipSummary(FreighterInfo ship, bool selected)
    {
        if (ship.Error != null) return Fit(ship.Name + " | Unavailable", 598) + "\n" + Fit("State unavailable; open details", 598);
        var mission = ship.Missions.FirstOrDefault();
        string goal = mission == null ? "No assigned goal" : (mission.Current ? "" : "Queued: ") +
            (mission.Transport ? mission.Primary + " -> " + mission.Secondary : "Goal " + mission.Primary);
        string hold = FreightOverview.CargoText(ship.Cargo) + (ship.Smuggling.Length > 0 ? " + separate smuggling hold" : "");
        return Fit($"{(selected ? "> " : "")}{ship.Name} | {ship.Activity} | {(ship.Dispatch ? "FD link" : "Native / other")}", 598) +
            "\n" + Fit("Hold " + hold + " | " + goal, 598);
    }

    static float TextWidth(string text) => UserInterfaceHelper.SelectFont(FontSize.Small, false)?.MeasureString(text).X ?? text.Length * 9f;
    static int FitLength(string text, float width)
    {
        int low = 0, high = text.Length;
        while (low < high) { int mid = (low + high + 1) / 2; if (TextWidth(text[..mid]) <= width) low = mid; else high = mid - 1; }
        return Math.Max(1, low);
    }
    internal static string Fit(string text, float width) => TextWidth(text) <= width ? text : text[..FitLength(text, Math.Max(1, width - TextWidth("...")))] + "...";
    internal static IEnumerable<string> Wrap(string text, float width)
    {
        if (text.Length == 0) { yield return ""; yield break; }
        while (text.Length > 0)
        {
            int take = FitLength(text, width);
            if (take < text.Length)
            {
                int space = text.LastIndexOf(' ', take - 1, take);
                if (space > 0) take = space;
            }
            yield return text[..take]; text = text[take..].TrimStart();
        }
    }
}
