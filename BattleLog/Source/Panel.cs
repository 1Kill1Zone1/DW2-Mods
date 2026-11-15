using DistantWorlds2;
using DistantWorlds.Core;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using System.Reflection;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace BattleLog;

// The native frame remains responsible for the opaque DW2 styling. This class
// owns only its content, geometry and guarded pointer gestures.
sealed class Panel : LocalModUi.NativeToolWindow
{
    const float WindowMargin = 12, DefaultWidth = 852, DefaultHeight = 560, MinimumWidth = 640, MinimumHeight = 340, HeaderHeight = 34, GripSize = 22, RowMinimumHeight = 180;
    readonly DWButton[] encounters = new DWButton[5], rows = new DWButton[8];
    readonly DWButton legacyNotice, emptyNotice, summaryNotice, encounterPrevious, encounterNext, rowPrevious, rowNext, encounterPages, rowPages;
    readonly WindowGestureControl header, resizeGrip;
    int encounterPage, rowPage, selected;
    long snapshotRevision = long.MinValue, lastOpenRefreshAt = long.MinValue;
    Ledger.DisplayEncounter[] shownReports = [];
    WeaponRow[] shownWeapons = [];
    int renderedEncounterPage = -1, renderedSelection = -1, renderedRowPage = -1, renderedEncounterSlots = -1, renderedWeaponSlots = -1, visibleEncounterRows = 5, visibleWeaponRows = 8;
    bool geometryLoaded, selectedIdentityKnown, compactRows;
    float fittedMinimumHeight = MinimumHeight;
    int selectedLocationId;
    DateTime selectedStarted;
    Gesture gesture;
    Point gestureOrigin;
    Vector2 gesturePosition, gestureSize;

    enum Gesture { None, Drag, Resize }

    public Panel(Action close) : base("Battle Log", opaque: true)
    {
        Name = "BattleLogPanel"; Layer = 970; PostScene = Visible = true; Initialize(FontSize.Small, FontSize.Small, null!);
        Add("X", 0, 0, 30, 26, close);
        encounterPrevious = Add("<", 0, 0, 34, 24, () => ChangeEncounterPage(-1));
        encounterNext = Add(">", 0, 0, 34, 24, () => ChangeEncounterPage(1));
        encounterPages = Add("", 0, 0, 134, 24);
        for (var i = 0; i < encounters.Length; i++) { var n = i; encounters[i] = Add("", 0, 0, 210, 48, () => SelectEncounter(n)); }
        rowPrevious = Add("< rows", 0, 0, 72, 24, () => rowPage--);
        rowNext = Add("rows >", 0, 0, 72, 24, () => rowPage++);
        rowPages = Add("", 0, 0, 180, 24);
        for (var i = 0; i < rows.Length; i++) rows[i] = Add("", 0, 0, 598, 48);
        legacyNotice = Add("Legacy 0.1.0 history is preserved in this save; observer is unknown.", 0, 0, 820, 22);
        summaryNotice = Add("", 0, 0, 820, 22);
        emptyNotice = Add("No observed encounters yet. Ongoing fights appear here.", 0, 0, 598, 48);
        header = new WindowGestureControl(this, Gesture.Drag) { Name = "BattleLogDrag", Text = "", Visible = true, PostScene = true };
        resizeGrip = new WindowGestureControl(this, Gesture.Resize) { Name = "BattleLogResize", Text = "+", Visible = true, PostScene = true };
        foreach (var control in new DWButton[] { header, resizeGrip })
        {
            control.ShowTexture = control.ShowTextureHovered = false;
            control.ButtonColor = control.ButtonColorHover = Color.Transparent;
            control.Initialize(FontSize.Small, FontSize.Small, null!); AddControl(control);
        }
    }

    DWButton Add(string text, float x, float y, float width, float height, Action? click = null)
    {
        var button = new MapControl { Name = text, Text = text, Visible = true, PostScene = true };
        button.SetSizeAndPosition(new Vector2(width, height), new Vector2(x, y)); button.Initialize(FontSize.Small, FontSize.Small, null!);
        if (click != null) button.ClickEvent += (_, _) => click(); AddControl(button); return button;
    }

    public void Place(int screenWidth, int screenHeight)
    {
        PlaceGeometry(screenWidth, screenHeight);
        LayoutControls();
        RefreshReports();
        RefreshContent();
    }

    void PlaceGeometry(int screenWidth, int screenHeight)
    {
        Vector2 position, size;
        float minimumHeight = fittedMinimumHeight;
        if (!geometryLoaded)
        {
            geometryLoaded = true;
            if (WindowPreferences.TryLoad(out position, out size)) fittedMinimumHeight = minimumHeight = MinimumHeight;
            else
            {
                (position, size, minimumHeight) = InitialGeometry(screenWidth, screenHeight);
                fittedMinimumHeight = minimumHeight;
            }
        }
        else { position = Position; size = Size; }
        ClampGeometry(ref position, ref size, screenWidth, screenHeight, minimumHeight);
        if (Size != size || Position != position) SetSizeAndPosition(size, position);
    }

    static (Vector2 Position, Vector2 Size, float MinimumHeight) InitialGeometry(int screenWidth, int screenHeight)
    {
        var size = new Vector2(DefaultWidth, DefaultHeight);
        var top = WindowMargin;
        var bottom = screenHeight - WindowMargin;
        // First placement sits immediately above the actual lower-left HUD, but
        // never under the native top toolbar. Manual placements are not altered.
        foreach (var control in NativeHudControls("SelectionPanel", "ActionButtons", "MainViewHeader"))
            if (LocalModUi.MapUiGate.Displayed(control))
            {
                var bounds = control.CalculateAbsoluteRectangle();
                if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left >= screenWidth * .55f) continue;
                if (bounds.Top >= screenHeight * .35f) bottom = Math.Min(bottom, bounds.Top - WindowMargin);
                else top = Math.Max(top, bounds.Bottom + WindowMargin);
            }
        var availableHeight = Math.Max(1, bottom - top);
        var effectiveMinimum = Math.Min(MinimumHeight, availableHeight);
        size.Y = Math.Min(size.Y, availableHeight);
        return (new Vector2(WindowMargin, bottom - size.Y), size, effectiveMinimum);
    }

    static IEnumerable<DWControl> NativeHudControls(params string[] names)
    {
        var renderer = typeof(DWGame).Assembly.GetType("DistantWorlds2.DWRendererBase");
        var current = LocalModUi.MapUiGate.CurrentRenderer;
        foreach (var name in names)
        {
            if (AccessTools.Field(typeof(UserInterfaceController), name)?.GetValue(null) is DWControl controller) yield return controller;
            if (renderer != null && AccessTools.Field(renderer, name)?.GetValue(null) is DWControl native) yield return native;
            if (current != null && typeof(ScaledRenderer).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetValue(current) is DWControl instance) yield return instance;
        }
    }

    static void ClampGeometry(ref Vector2 position, ref Vector2 size, int screenWidth, int screenHeight, float minimumHeight = MinimumHeight)
    {
        var availableWidth = Math.Max(1, screenWidth - WindowMargin * 2);
        var availableHeight = Math.Max(1, screenHeight - WindowMargin * 2);
        size.X = Math.Clamp(size.X, Math.Min(MinimumWidth, availableWidth), availableWidth);
        size.Y = Math.Clamp(size.Y, Math.Min(minimumHeight, availableHeight), availableHeight);
        position.X = Math.Clamp(position.X, WindowMargin, Math.Max(WindowMargin, screenWidth - WindowMargin - size.X));
        position.Y = Math.Clamp(position.Y, WindowMargin, Math.Max(WindowMargin, screenHeight - WindowMargin - size.Y));
    }

    void LayoutControls()
    {
        var previousEncounterRows = visibleEncounterRows;
        var previousWeaponRows = visibleWeaponRows;
        var wasCompact = compactRows;
        var width = Size.X;
        var height = Size.Y;
        var leftWidth = Math.Clamp(width * .255f, 164, Math.Max(164, width - 350));
        var rowsLeft = WindowMargin + leftWidth + WindowMargin;
        var rowsWidth = Math.Max(100, width - rowsLeft - WindowMargin);
        var top = HeaderHeight + 10;
        var listTop = top + 30;
        var footerTop = height - 78;
        visibleEncounterRows = Math.Clamp((int)MathF.Floor((footerTop - listTop - 2) / 40), 1, encounters.Length);
        // Detailed metrics intentionally trade density for readable labelled
        // lines; pages retain the same bounded history at small heights.
        visibleWeaponRows = Math.Clamp((int)MathF.Floor((footerTop - listTop - 2) / RowMinimumHeight), 1, rows.Length);
        compactRows = width < 760;
        if (selectedIdentityKnown && visibleEncounterRows != previousEncounterRows) RestoreSelectedIdentity();
        if (visibleWeaponRows != previousWeaponRows) rowPage = Math.Max(0, rowPage * previousWeaponRows / visibleWeaponRows);
        if (compactRows != wasCompact) { renderedRowPage = -1; renderedWeaponSlots = -1; }
        var encounterRowHeight = Math.Max(40, (footerTop - listTop - 2) / visibleEncounterRows);
        var rowHeight = Math.Max(RowMinimumHeight, (footerTop - listTop - 2) / visibleWeaponRows);
        PositionOf(Controls.OfType<DWButton>().Single(x => x.Text == "X"), width - 42, 4, 30, 26);
        PositionOf(header, 0, 0, Math.Max(0, width - 48), HeaderHeight);
        PositionOf(resizeGrip, width - GripSize, height - GripSize, GripSize, GripSize);
        PositionOf(encounterPrevious, WindowMargin, top, 34, 24); PositionOf(encounterNext, WindowMargin + 38, top, 34, 24);
        PositionOf(encounterPages, WindowMargin + 76, top, Math.Max(72, leftWidth - 76), 24);
        PositionOf(rowPrevious, rowsLeft, top, 72, 24); PositionOf(rowNext, rowsLeft + 76, top, 72, 24);
        PositionOf(rowPages, rowsLeft + 152, top, Math.Max(80, rowsWidth - 152), 24);
        for (var i = 0; i < encounters.Length; i++)
            PositionOf(encounters[i], WindowMargin, i < visibleEncounterRows ? listTop + i * encounterRowHeight : footerTop, i < visibleEncounterRows ? leftWidth : 0, i < visibleEncounterRows ? Math.Max(40, encounterRowHeight - 3) : 0);
        for (var i = 0; i < rows.Length; i++)
            PositionOf(rows[i], rowsLeft, i < visibleWeaponRows ? listTop + i * rowHeight : footerTop, i < visibleWeaponRows ? rowsWidth : 0, i < visibleWeaponRows ? Math.Max(24, rowHeight - 3) : 0);
        PositionOf(emptyNotice, rowsLeft, listTop, rowsWidth, Math.Max(24, rowHeight - 3));
        PositionOf(summaryNotice, WindowMargin, footerTop, width - WindowMargin * 2 - GripSize, 44);
        PositionOf(legacyNotice, WindowMargin, footerTop + 48, width - WindowMargin * 2 - GripSize, 22);
    }

    static void PositionOf(DWControl control, float x, float y, float width, float height)
    {
        var size = new Vector2(width, height);
        var position = new Vector2(x, y);
        if (control.Size != size || control.Position != position)
        {
            var resized = control.Size != size;
            control.SetSizeAndPosition(size, position);
            if (resized && control is DWButton button) button.ResetTextLayout();
        }
    }

    void RefreshReports()
    {
        var open = Ledger.HasOpenEncounters;
        var now = Environment.TickCount64;
        if (open && lastOpenRefreshAt != long.MinValue && now - lastOpenRefreshAt < 1000) return;
        var snapshot = Ledger.DisplaySince(ref snapshotRevision);
        if (snapshot == null) return;
        if (open) lastOpenRefreshAt = now;
        shownReports = snapshot;
        if (selectedIdentityKnown)
        {
            RestoreSelectedIdentity();
        }
        renderedEncounterPage = renderedSelection = renderedRowPage = renderedEncounterSlots = renderedWeaponSlots = -1;
    }

    void RefreshContent()
    {
        legacyNotice.Visible = Ledger.LegacyHistoryPreserved;
        var all = shownReports;
        var maxEncounterPage = Math.Max(0, (all.Length - 1) / visibleEncounterRows);
        encounterPage = Math.Clamp(encounterPage, 0, maxEncounterPage);
        var start = encounterPage * visibleEncounterRows;
        selected = Math.Clamp(selected, 0, Math.Max(0, Math.Min(visibleEncounterRows - 1, all.Length - start - 1)));
        if (start + selected < all.Length) RememberSelection(all[start + selected]);
        bool selectionChanged = renderedEncounterPage != encounterPage || renderedSelection != selected || renderedEncounterSlots != visibleEncounterRows;
        if (selectionChanged) shownWeapons = start + selected < all.Length ? all[start + selected].Report.Weapons.OrderBy(x => x.SideName).ThenBy(x => x.Name).ToArray() : [];
        var weapons = shownWeapons;
        rowPage = Math.Clamp(rowPage, 0, Math.Max(0, (weapons.Length - 1) / visibleWeaponRows));
        var rowStart = rowPage * visibleWeaponRows;
        if (!selectionChanged && renderedRowPage == rowPage && renderedWeaponSlots == visibleWeaponRows) return;
        emptyNotice.Visible = all.Length == 0;
        summaryNotice.Visible = all.Length > 0;
        summaryNotice.Text = start + selected < all.Length ? Summary(weapons) : "";
        encounterPages.Text = all.Length == 0 ? "0 reports" : $"{encounterPage + 1} / {maxEncounterPage + 1}";
        encounterPrevious.Enabled = encounterPage > 0; encounterNext.Enabled = encounterPage < maxEncounterPage;
        for (var i = 0; i < encounters.Length; i++)
        {
            var index = start + i; encounters[i].Visible = encounters[i].Enabled = i < visibleEncounterRows && index < all.Length;
            encounters[i].Text = i >= visibleEncounterRows || index >= all.Length ? "" : Title(all[index]);
        }
        rowPages.Text = weapons.Length == 0 ? "0 weapons" : $"{rowPage + 1} / {(weapons.Length - 1) / visibleWeaponRows + 1}";
        rowPrevious.Enabled = rowPage > 0; rowNext.Enabled = rowStart + visibleWeaponRows < weapons.Length;
        for (var i = 0; i < rows.Length; i++) { rows[i].Visible = i < visibleWeaponRows && rowStart + i < weapons.Length; rows[i].Text = i >= visibleWeaponRows || rowStart + i >= weapons.Length ? "" : Row(weapons[rowStart + i]); }
        renderedEncounterPage = encounterPage; renderedSelection = selected; renderedRowPage = rowPage; renderedEncounterSlots = visibleEncounterRows; renderedWeaponSlots = visibleWeaponRows;
    }

    void ChangeEncounterPage(int change) { encounterPage += change; selectedIdentityKnown = false; }
    void SelectEncounter(int index)
    {
        selected = index; rowPage = 0;
        var absolute = encounterPage * visibleEncounterRows + index;
        if (absolute < shownReports.Length) RememberSelection(shownReports[absolute]); else selectedIdentityKnown = false;
    }
    void RememberSelection(Ledger.DisplayEncounter item)
    {
        selectedLocationId = item.Report.LocationId; selectedStarted = item.Report.Started; selectedIdentityKnown = true;
    }
    void RestoreSelectedIdentity()
    {
        var index = Array.FindIndex(shownReports, x => x.Report.LocationId == selectedLocationId && x.Report.Started == selectedStarted);
        if (index >= 0) { encounterPage = index / visibleEncounterRows; selected = index % visibleEncounterRows; }
    }

    bool BeginGesture(Gesture kind, Point point, bool left)
    {
        if (!left || !Available || gesture != Gesture.None) return false;
        gesture = kind; gestureOrigin = point; gesturePosition = Position; gestureSize = Size; return true;
    }
    bool MoveGesture(Point point, bool left)
    {
        if (gesture == Gesture.None) return false;
        if (!left || !Available) { CancelGesture(); return true; }
        var delta = new Vector2(point.X - gestureOrigin.X, point.Y - gestureOrigin.Y);
        var position = gesturePosition;
        var size = gestureSize;
        if (gesture == Gesture.Drag) position += delta; else size += delta;
        ClampGeometry(ref position, ref size, UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
        if (Position != position || Size != size) { SetSizeAndPosition(size, position); LayoutControls(); }
        return true;
    }
    bool EndGesture(bool left)
    {
        if (gesture == Gesture.None) return false;
        var changed = left && (Position != gesturePosition || Size != gestureSize); gesture = Gesture.None;
        if (changed) WindowPreferences.Save(Position, Size);
        return true;
    }
    internal void CancelGesture() => gesture = Gesture.None;
    internal bool GestureActive => gesture != Gesture.None;

    bool Available { get { if (!Entry.UiAvailable()) { CancelGesture(); MapControl.ClearPress(); LocalModUi.MapUiGate.ReleaseHover(this); return false; } return CheckVisible(); } }
    public override void Render(Stride.Rendering.RenderDrawContext context, SpriteBatch batch, Point point, bool tips) { if (Available) base.Render(context, batch, point, tips); }
    public override bool DoMouseDown(Point p, bool left, Empire e, bool shift, bool ctrl, bool child) => Available && base.DoMouseDown(p, left, e, shift, ctrl, child);
    public override bool DoMouseUp(Point p, bool left, Empire e, bool shift, bool ctrl, float held) => Available && base.DoMouseUp(p, left, e, shift, ctrl, held);
    public override bool DoMouseMove(Vector2 move, Point p, bool left, bool right) => Available && base.DoMouseMove(move, p, left, right);
    static string Title(Ledger.DisplayEncounter e) => $"{e.Report.LocationName}\n{(e.InProgress ? "IN PROGRESS · " : "Completed · ")}{e.Report.Started:t} · {e.Report.Weapons.Count} weapon types";
    static double Actual(WeaponRow x) => x.MeasuredImpactActual;
    static string Percent(double value) => double.IsFinite(value) ? value.ToString("0%") : "—";
    static string Detail(WeaponRow x)
    {
        var layers = $"Recorded damage: shields {x.Shield:0.#} · armor {x.Armor:0.#} · hull {x.Hull:0.#}";
        var pd = $"Point defence: shots {x.PdShots} · destroyed {x.PdDestroyed} · weakened {x.PdWeakened}";
        if (x.DetailedVersion == 0) return $"{layers}\n{pd}\n" + (x.ImpactUnknownEvents > 0 ? "Detailed outcomes were not recorded for this impact." : "Detailed outcomes unavailable (older history).");
        var resolved = x.ProjectileHits + x.ProjectileMisses;
        var actualRate = resolved == 0 ? "—" : Percent((double)x.ProjectileHits / resolved);
        var expected = x.ExpectedChanceSamples == 0 ? "unavailable" : $"hit {Percent(x.ExpectedChanceTotal / x.ExpectedChanceSamples)} / miss {Percent(1d - x.ExpectedChanceTotal / x.ExpectedChanceSamples)}";
        var pending = x.PendingKnown ? x.ProjectilePending.ToString() : "?";
        var unavailable = x.ProjectileUnavailable == 0 ? "" : $"; unavailable {x.ProjectileUnavailable}";
        var actual = Actual(x); var potential = x.ImpactPotential;
        var efficiency = x.PartialImpactEvents > 0 ? "unavailable (component damage)" : potential > 0 ? Percent(actual / potential) : "—";
        var coverage = x.PartialImpactEvents > 0 || x.ImpactUnknownEvents > 0 ? " known subset" : "";
        var impacts = potential > 0 || x.ImpactEvents > 0
            ? $"Measured landed impacts ({x.ImpactEvents}): {actual:0.#} / pre-defence {potential:0.#}\nDifference {potential - actual:0.#}; efficiency {efficiency}{coverage}"
            : x.ImpactUnknownEvents > 0 ? "Impacts: hidden or unmeasured; efficiency unavailable" : "Impacts: no visible landed ship-impact data";
        // Keep every line within the narrow right column: labels stay explicit
        // rather than relying on compact P/B/A codes or native text clipping.
        var projectile = $"Projectiles: hits {x.ProjectileHits} · misses {x.ProjectileMisses} · accuracy {actualRate}";
        var forced = x.ForcedLaunches == 0 ? "" : $" · forced {x.ForcedResolved}/{x.ForcedLaunches}";
        var expectedLine = $"Expected chance: {expected}{forced}\nPending {pending}{unavailable} · lost target {x.ProjectileLostTargets} · PD-intercepted {x.ProjectileIntercepted}";
        var effects = $"Beam launches {x.BeamLaunches} · damage ticks {x.BeamTicks} · Area launches {x.AreaLaunches} · damage events {x.AreaTargets}";
        return $"{projectile}\n{expectedLine}\n{effects}\n{pd}\n{layers}\n{impacts}";
    }
    static string Summary(WeaponRow[] weapons)
    {
        var allSides = weapons.Where(x => x.DetailedVersion != 0).GroupBy(x => x.Side).ToArray();
        var known = allSides.Take(2).Select(group =>
        {
            var actual = group.Sum(Actual); var potential = group.Sum(x => x.ImpactPotential); var componentDamage = group.Any(x => x.PartialImpactEvents > 0); var partial = componentDamage || group.Any(x => x.ImpactUnknownEvents > 0);
            var efficiency = componentDamage ? "unavailable (component damage)" : Percent(potential > 0 ? actual / potential : double.NaN);
            return $"{group.First().SideName}: {actual:0.#}/{potential:0.#} {efficiency}{(partial ? " subset" : "")}";
        }).ToArray();
        return known.Length == 0 ? "Detailed metrics unavailable for this report." : "Impact efficiency · " + string.Join(" | ", known) + (allSides.Length > known.Length ? $" | +{allSides.Length - known.Length} more side(s)" : "");
    }
    string Row(WeaponRow x) => Size.X < 760
        ? $"{x.SideName}: {x.Name}\n{Detail(x)}"
        : $"{x.SideName}: {x.Name} | shots {x.Shots} · damage events {x.Hits} · PD shots {x.PdShots}\n{Detail(x)}";

    sealed class WindowGestureControl(Panel panel, Gesture kind) : DWButton
    {
        public override bool DoMouseDown(Point point, bool left, Empire empire, bool shift, bool ctrl, bool child)
        {
            if (!CalculateAbsoluteRectangle().Contains(point) || child || !panel.Available) return false;
            return !left || panel.BeginGesture(kind, point, true);
        }
        public override bool DoMouseUp(Point point, bool left, Empire empire, bool shift, bool ctrl, float held)
        {
            var active = panel.GestureActive;
            if (!panel.Available) return active;
            if (!active && !CalculateAbsoluteRectangle().Contains(point)) return false;
            return panel.EndGesture(left) || CalculateAbsoluteRectangle().Contains(point);
        }
        public override bool DoMouseMove(Vector2 move, Point point, bool left, bool right)
        {
            if (!panel.Available) return false;
            if (panel.MoveGesture(point, left)) return true;
            return CalculateAbsoluteRectangle().Contains(point);
        }
    }
}
