using DistantWorlds.UI;
using DistantWorlds.Types;
using DistantWorlds.Core;
using Stride.Core.Mathematics;
using System.Runtime.Versioning;
using System.Diagnostics;
using Stride.Graphics;
using Stride.Rendering;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal sealed class Panel : LocalModUi.NativeToolWindow
{
    private const int PanelWidth = 790;
    private const int PanelHeight = 680;
    private readonly DWButton[] _rows = new DWButton[8];
    private readonly DWButton _compare;
    private readonly DWButton _save;
    private readonly DWButton _sample;
    private readonly DWButton _heading;
    private readonly DWButton _pageLabel;
    private readonly DWTextBox _search;
    private readonly LayerSlider[] _sliders = new LayerSlider[12];
    private readonly DWButton[] _labels = new DWButton[12];
    private readonly DWButton[] _resetSliders = new DWButton[12];
    private readonly DWButton[] _channels = new DWButton[4];
    private readonly DWButton _applyFamily;
    private readonly DWButton _copyColors;
    private readonly DWButton _undo;
    private readonly DWButton _revert;
    private readonly DWButton _previewMode;
    private readonly DWButton _weaponScope;
    private readonly DWButton _resetProfile;
    private readonly DWButton _confirmResetProfile;
    private readonly DWButton _trailLink;
    private readonly DWButton _impactLink;
    private readonly DWButton _shieldPreview;
    private readonly DWButton _hullPreview;
    private readonly StudioPreview _studio = new();
    private readonly StudioSurface _studioSurface;
    private readonly DWButton _studioToggle;
    private readonly DWButton _zoomIn;
    private readonly DWButton _zoomOut;
    private bool _studioEnabled = true;
    private bool _confirmingProfileReset;
    private bool _previewShield = true;
    private DWButton? _pressedButton;
    private ComponentDefinition[] _filtered = [];
    private ComponentDefinition? _selected;
    private Ship? _ship;
    private WeaponList? _shipWeapons;
    private bool _allWeapons;
    private long _impactReplay;
    private string _query = "";
    private int _category;
    private int _page;
    private bool _syncing;
    private int _editedSlider = -1;
    private int _chargeMode = 1;
    private int _colorChannel = 1;
    private int ActiveChannel => _selected != null && HasChannel(_selected, _colorChannel) ? _colorChannel : 1;
    private bool StudioActive => _studioEnabled && !_studio.Failed && StudioPreview.Supports(_selected);

    private sealed class ActionButton(Panel panel) : DWButton
    {
        public override bool DoMouseDown(Point clickPoint, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, bool callingFromChild)
        {
            if (!panel.CanInteract || !CheckVisible()) return false;
            if (Enabled && isLeftClick && !callingFromChild && CalculateAbsoluteRectangle().Contains(clickPoint))
                panel._pressedButton = this;
            return base.DoMouseDown(clickPoint, isLeftClick, empire, shiftDown, ctrlDown, callingFromChild);
        }

        public override bool DoMouseUp(Point clickPoint, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, float timeDown)
        {
            if (!panel.CanInteract || !CheckVisible()) return false;
            if (!isLeftClick || panel._pressedButton != this) return true;
            panel.ClearPressedButton();
            return base.DoMouseUp(clickPoint, isLeftClick, empire, shiftDown, ctrlDown, timeDown);
        }
    }

    private sealed class LayerSlider(Panel panel) : DWSlider
    {
        internal bool Locked;
        public override bool DoMouseDown(Point point, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, bool callingFromChild) =>
            panel.CanInteract && CheckVisible() && !Locked && base.DoMouseDown(point, isLeftClick, empire, shiftDown, ctrlDown, callingFromChild);

        public override bool DoMouseMove(Vector2 movement, Point point, bool leftDown, bool rightDown) =>
            panel.CanInteract && CheckVisible() && !Locked && base.DoMouseMove(movement, point, leftDown, rightDown);
        public override bool DoMouseUp(Point point, bool left, Empire empire, bool shift, bool ctrl, float held) =>
            panel.CanInteract && CheckVisible() && !Locked && base.DoMouseUp(point, left, empire, shift, ctrl, held);
    }

    private sealed class SearchBox(Panel panel) : DWTextBox
    {
        internal bool CanType => panel.CanInteract && CheckVisible();
        public override bool DoMouseDown(Point point, bool left, Empire empire, bool shift, bool ctrl, bool fromChild) => panel.CanInteract && CheckVisible() && base.DoMouseDown(point, left, empire, shift, ctrl, fromChild);
        public override bool DoMouseUp(Point point, bool left, Empire empire, bool shift, bool ctrl, float held) => panel.CanInteract && CheckVisible() && base.DoMouseUp(point, left, empire, shift, ctrl, held);
        public override bool DoMouseMove(Vector2 movement, Point point, bool left, bool right) => panel.CanInteract && CheckVisible() && base.DoMouseMove(movement, point, left, right);
    }
    internal static bool AllowSearchKey(DWTextBox __instance, ref bool __result)
    {
        if (__instance is not SearchBox search || search.CanType) return true;
        __result = false; return false;
    }
    internal static bool AllowSingleSearchKey(DWTextBox __instance, ref bool __result, ref bool __5)
    {
        if (AllowSearchKey(__instance, ref __result)) return true;
        __5 = false; return false;
    }

    private sealed class StudioSurface(Panel panel, StudioPreview studio) : DWButton
    {
        internal bool Dragging;

        public override bool DoMouseDown(Point point, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, bool callingFromChild)
        {
            if (!panel.CanInteract || !CheckVisible()) { Dragging = false; return false; }
            Dragging = isLeftClick && !callingFromChild && CalculateAbsoluteRectangle().Contains(point);
            return Dragging;
        }

        public override bool DoMouseMove(Vector2 movement, Point point, bool leftDown, bool rightDown)
        {
            if (!panel.CanInteract || !CheckVisible()) { Dragging = false; return false; }
            if (!leftDown) Dragging = false;
            if (!Dragging) return false;
            studio.Orbit(movement);
            return true;
        }

        public override bool DoMouseUp(Point point, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, float timeDown)
        {
            if (!panel.CanInteract || !CheckVisible()) { Dragging = false; return false; }
            if (!isLeftClick) return false;
            Dragging = false;
            return true;
        }

        public override void Render(RenderDrawContext context, SpriteBatch batch, Point mousePoint, bool renderHoverTips) { }
    }

    public Panel(Action close) : base("Weapon FX Tuner")
    {
        Name = "DW2WeaponFXPanel";
        Layer = 970f;
        PostScene = true;
        Visible = true;
        Initialize(FontSize.Small, FontSize.Small, null!);
        Button("X", 746, 10, 30, 26, close);
        var categories = new[] { "All", "Beams", "Direct", "Seeking", "Area" };
        for (var i = 0; i < categories.Length; i++)
        {
            var category = i;
            Button(categories[i], 12 + 61 * i, 45, 59, 26, () => { _category = category; Filter(); });
        }
        _search = new SearchBox(this) { Name = "WeaponFX Search", PromptTextWhenEmpty = "Search name or component ID", Visible = true,
            PostScene = true, EnterDeactivates = true, Text = "" };
        _search.SetSizeAndPosition(new Vector2(302, 28), new Vector2(12, 77));
        _search.Initialize(FontSize.Small, FontSize.Small, null!);
        AddControl(_search);
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = i;
            _rows[i] = Button("", 12, 111 + i * 28, 302, 26, () => Select(_page * _rows.Length + row));
        }
        Button("<", 12, 341, 40, 27, () => { _page = Math.Max(0, _page - 1); List(); });
        _pageLabel = Button("", 56, 341, 214, 27);
        Button(">", 274, 341, 40, 27, () => { _page = Math.Min(Math.Max(0, (_filtered.Length - 1) / _rows.Length), _page + 1); List(); });
        _heading = Button("", 331, 45, 445, 48);
        var channels = new[] { ("Body / halo", "Main beam glow or the visible projectile body."),
            ("Trail", "The effect behind a moving projectile."),
            ("Impact", "The flash or ring at the point of contact, where supported."),
            ("White core", "The narrow bright line inside the beam. Its tint follows the beam.") };
        for (var i = 0; i < _channels.Length; i++)
        {
            var channel = i + 1;
            _channels[i] = Button(channels[i].Item1, 331 + 112 * i, 104, 109, 26, () => { _colorChannel = channel; Sync(); });
            AddHelp(_channels[i], channels[i].Item1, channels[i].Item2);
        }
        _applyFamily = Button("Apply to family", 12, 411, 147, 28, () =>
        {
            if (_selected == null) return;
            var count = Entry.ApplyFamily(_selected.ComponentId);
            Sync();
            SetText(_applyFamily!, $"Applied to {count}");
        });
        AddHelp(_applyFamily, "Apply to family", "Copy the full visual profile to the weapon family. Changes are written to disk when you press Save.");
        _copyColors = Button("Copy family colors", 164, 411, 150, 28, () =>
        {
            if (_selected == null) return;
            var count = Entry.CopyFamilyColors(_selected.ComponentId);
            Sync();
            SetText(_copyColors!, count > 0 ? $"Colors copied to {count}" : "No color changes");
        });
        AddHelp(_copyColors, "Copy family colors", "Copy the Body, Trail and Impact colors. Other settings and color links stay per weapon.");
        Button("Profiles apply to all ships", 12, 443, 302, 26);
        Button("Shield trace: global; 100% = RVE", 12, 473, 302, 26);
        _weaponScope = Button("All weapons", 12, 503, 302, 28, () =>
        {
            if (_ship == null) return;
            _allWeapons = !_allWeapons;
            ClearFilters();
            Filter();
        });
        AddHelp(_weaponScope, "Weapon list", "Switch between installed weapon types and the full catalogue. Edits apply to that weapon type on all ships.");
        _resetProfile = Button("Reset profile", 12, 537, 142, 27, () =>
        {
            _confirmingProfileReset = !_confirmingProfileReset;
            Sync();
        });
        _confirmResetProfile = Button("Confirm reset", 160, 537, 154, 27, () =>
        {
            if (!_confirmingProfileReset) return;
            _confirmingProfileReset = false;
            Entry.ResetProfile();
            Sync();
            SetText(_resetProfile!, "Reset done. Save");
        });
        AddHelp(_resetProfile, "Reset profile", "Restore every weapon and Shield trace in the current XL or Vanilla profile. Save keeps the changes.");
        _trailLink = Button("Trail follows Body", 12, 569, 302, 27, () => ToggleColorLink(2));
        _impactLink = Button("Impact follows Body", 12, 600, 302, 27, () => ToggleColorLink(3));
        AddHelp(_trailLink, "Trail follows Body", "Use the Body color for Trail. Turn off to restore the previous manual Trail color.");
        AddHelp(_impactLink, "Impact follows Body", "Use the Body color for Impact. Turn off to restore the previous manual Impact color.");
        _undo = Button("Undo", 12, 633, 96, 27, () => { Entry.Undo(); Sync(); });
        _revert = Button("Revert to last Save", 113, 633, 201, 27, () => { Entry.RevertSaved(); Sync(); });
        AddHelp(_undo, "Undo", "Undo one edit, one slider drag or one family action in this catalogue.");
        AddHelp(_revert, "Revert to last Save", "Restore the saved profile in this catalogue. Undo can bring the draft back.");
        var parameters = new[]
        {
            ("Red", "Red channel of the selected layer. 0 removes red, 255 is full red."),
            ("Green", "Green channel of the selected layer. 0 removes green, 255 is full green."),
            ("Blue", "Blue channel of the selected layer. 0 removes blue, 255 is full blue."),
            ("Brightness", "Adjusts the brightness of the active layer only."),
            ("Beam pulse", "Strength of the beam's visual pulsation. 0 removes modulation where supported."),
            ("Trail length", "Length of the tracer, ribbon or smoke tail, depending on the weapon. 0 hides a supported torpedo trail."),
            ("Halo / size", "Size of the beam's outer glow or the projectile body. Some projectile trails also use this scale."),
            ("3D turret", "Scale of the visible turret model on the ship."),
            ("Core width", "Thickness of the inner white beam line. Opacity on the White core tab controls its visibility."),
            ("Fallback gap", "Separation between beam strands for supported effects using approximate mounts."),
            ("Shield trace %", "Global duration of ordinary Refreshed shield hit marks. 100% is the original duration. Look at a shielded target."),
            ("Opacity %", "Visibility of the active layer tab. 0% hides that layer and 100% gives its full contribution.")
        };
        for (var i = 0; i < parameters.Length; i++)
        {
            var row = i;
            var y = 134 + 30 * i;
            var (name, help) = parameters[i];
            _labels[i] = Button(name, 331, y, 112, 27);
            AddHelp(_labels[i], name, help);
            var slider = new LayerSlider(this)
            {
                Name = "WeaponFX " + name, PostScene = true, Visible = true,
                Minimum = i < 3 || i is 4 or 9 or 11 ? 0f : i == 3 ? .2f : i == 7 ? .75f : i is 6 or 8 ? .1f : i == 10 ? 10f : .5f,
                Maximum = i < 3 ? 255f : i == 4 ? 1f : i == 7 ? 1.5f : i is 8 or 9 ? 3f : i == 10 ? 200f : i == 11 ? 100f : i == 6 ? 4f : 2f,
                RoundingDigits = i < 3 || i is 10 or 11 ? 0 : 2, NumericFormatString = i < 3 || i is 10 or 11 ? "0" : "0.00",
                ThumbSize = new Vector2(14, 14), SliderHeight = 5, ValueTextWidth = 53
            };
            slider.SetSizeAndPosition(new Vector2(282, 27), new Vector2(455, y));
            slider.Initialize(FontSize.Small, FontSize.Small, null!);
            slider.ValueChangedEvent += (_, _) => Changed(row);
            AddControl(slider);
            AddHelp(slider, name, help);
            _sliders[i] = slider;
            _resetSliders[i] = Button("R", 742, y, 34, 27, () => ResetSlider(row));
            AddHelp(_resetSliders[i], "Reset " + name, "Restore only this setting to the original Weapon FX profile value.");
        }
        _compare = Button("", 331, 501, 107, 28, () => { Entry.Bypass = !Entry.Bypass; Sync(); });
        var resetWeapon = Button("Reset weapon", 442, 501, 108, 28, () => { if (_selected != null) Entry.Reset(_selected.ComponentId); Sync(); });
        AddHelp(resetWeapon, "Reset weapon", "Restore all settings and color links for the selected weapon type.");
        _sample = Button("", 554, 501, 110, 28, () =>
        {
            if (_selected == null) return;
            var old = Entry.Get(_selected.ComponentId);
            if (old.FollowsBody(ActiveChannel)) return;
            Entry.Set(_selected.ComponentId, old.WithChannel(ActiveChannel, -1));
            Sync();
        });
        _save = Button("Save", 668, 501, 108, 28, () =>
        {
            SetText(_save!, Entry.Save() ? "Saved" : "Save failed");
            SyncHistoryButtons();
        });
        AddHelp(_save, "Save", "Write the current visual profiles to disk for this game catalogue.");
        Button("Presets", 12, 376, 77, 26);
        Button("Calm", 92, 376, 68, 26, () => Preset(0));
        Button("Bright", 165, 376, 68, 26, () => Preset(1));
        Button("Heavy", 238, 376, 76, 26, () => Preset(2));
        _studioToggle = Button("2D preview", 331, 536, 84, 26, () =>
        {
            if (!StudioPreview.Supports(_selected) || _studio.Failed) return;
            _studioEnabled = !_studioEnabled;
            _studio.Replay();
            Sync();
        });
        _zoomOut = Button("-", 419, 536, 25, 26, () => _studio.Zoom(1.5f));
        _zoomIn = Button("+", 448, 536, 25, 26, () => _studio.Zoom(-1.5f));
        _shieldPreview = Button("Test shield", 478, 536, 88, 26, () => PreviewImpact(true));
        _hullPreview = Button("Test hull", 570, 536, 82, 26, () => PreviewImpact(false));
        AddHelp(_studioToggle, "Preview mode", "Switch between 3D and 2D for Fusion and Epsilon. Drag the 3D view to orbit. Use + or - to zoom.");
        AddHelp(_zoomIn, "Zoom in", "Move the 3D preview camera closer.");
        AddHelp(_zoomOut, "Zoom out", "Move the 3D preview camera farther away.");
        AddHelp(_shieldPreview, "Test shield", "Replay a shield hit. In 3D mode the shot stays inside the preview.");
        AddHelp(_hullPreview, "Test hull", "Replay a hull hit. In 3D mode the shot stays inside the preview.");
        _previewMode = Button("Charge: focus", 656, 536, 120, 26, () =>
        {
            if (_selected == null) return;
            if (Entry.RveAvailable && ChargeEffects.Supports(_selected))
            {
                var style = Entry.Get(_selected.ComponentId);
                Entry.Set(_selected.ComponentId, style with { ChargeStyle = (style.ChargeStyle + 1) % 4 });
            }
            else _chargeMode = (_chargeMode + 1) % 4;
            Sync();
        });
        _studioSurface = new StudioSurface(this, _studio) { Name = "WeaponFX 3D preview", PostScene = true };
        _studioSurface.Initialize(FontSize.Small, FontSize.Small, null!);
        _studioSurface.SetSizeAndPosition(new Vector2(445, 98), new Vector2(331, 567));
        AddControl(_studioSurface);
        Filter();
    }

    internal void Place(int height)
    {
        if (_studio.Failed && _studioSurface.Visible) Sync();
        var panelHeight = StudioActive ? Math.Clamp(height - 16, PanelHeight, 750) : PanelHeight;
        var point = new Vector2(12, Math.Max(8, Math.Min(110, height - panelHeight - 8)));
        if (Position != point || Size.X != PanelWidth || Size.Y != panelHeight)
            SetSizeAndPosition(new Vector2(PanelWidth, panelHeight), point);
        if (_studioSurface.Size.Y != panelHeight - 582)
            _studioSurface.SetSizeAndPosition(new Vector2(445, panelHeight - 582), new Vector2(331, 567));
        var ship = UserInterfaceController.SelectionPanel != null ? UserInterfaceController.ResolveSelectedObject() as Ship : null;
        Galaxy? galaxy = ship?.GetGalaxy();
        if (!CanInspectMounted(ship, galaxy?.Empires.GetPlayer(), galaxy)) ship = null;
        if (!ReferenceEquals(ship, _ship))
        {
            _ship = ship;
            _shipWeapons = ship?.Weapons;
            _allWeapons = false;
            ClearFilters();
            Filter();
        }
        else if (!ReferenceEquals(ship?.Weapons, _shipWeapons))
        {
            _shipWeapons = ship?.Weapons;
            if (!_allWeapons) Filter();
        }
        if (_search.Text != _query) { _query = _search.Text ?? ""; Filter(); }
    }

    private void ClearFilters()
    {
        _search.Text = _query = "";
        _category = 0;
    }

    internal void Close()
    {
        Suspend();
        _studio.Dispose();
    }

    internal bool CanInteract
    {
        get { if (!Entry.UiAvailable()) { Suspend(); return false; } return CheckVisible(); }
    }
    internal void Suspend()
    {
        ClearPressedButton();
        LocalModUi.MapUiGate.ReleaseHover(this);
        if (_search != null) { _search.IsActive = false; if (ReferenceEquals(UserInterfaceController.ActiveTextBox, _search)) UserInterfaceController.ActiveTextBox = null!; }
        if (Controls != null && Controls.Contains(UserInterfaceController.ActivelySlidingSlider)) UserInterfaceController.ActivelySlidingSlider = null!;
    }
    public override bool DoMouseDown(Point point, bool left, Empire empire, bool shift, bool ctrl, bool fromChild) => CanInteract && base.DoMouseDown(point, left, empire, shift, ctrl, fromChild);
    public override bool DoMouseUp(Point point, bool left, Empire empire, bool shift, bool ctrl, float held) => CanInteract && base.DoMouseUp(point, left, empire, shift, ctrl, held);
    public override bool DoMouseMove(Vector2 movement, Point point, bool left, bool right) => CanInteract && base.DoMouseMove(movement, point, left, right);

    internal void ClearPressedButton()
    {
        _pressedButton = null;
        _editedSlider = -1;
        if (_studioSurface != null) _studioSurface.Dragging = false;
    }

    private static void AddHelp(DWControl control, string title, string text) =>
        control.AddHoverTip(title, text, control.CalculateAbsoluteRectangle(), 320, Side.Right);

    private void Filter()
    {
        var current = _selected;
        var ids = _ship == null || _allWeapons ? null : MountedWeaponIds(_shipWeapons);
        _filtered = Entry.Components.Where(component =>
            (ids == null || ids.Contains(component.ComponentId)) &&
            (_query.Length == 0 || component.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) || component.ComponentId.ToString() == _query) &&
            (_category == 0 || _category == 4 && IsArea(component) || _category is >= 1 and <= 3 &&
                component.Values.Any(stats => stats.WeaponFireType == (_category == 1 ? WeaponFireType.FullLengthBeam :
                    _category == 2 ? WeaponFireType.DirectFire : WeaponFireType.Seeking)))).ToArray();
        var index = Array.IndexOf(_filtered, current);
        _page = index < 0 ? 0 : index / _rows.Length;
        _selected = index < 0 ? _filtered.FirstOrDefault() : _filtered[index];
        List();
        Sync();
    }

    internal static bool CanInspectMounted(Ship? ship, Empire? observer, Galaxy? galaxy) =>
        ship != null && observer != null && galaxy != null &&
        (ship.EmpireId == observer.EmpireId || observer.VisibilityMap?.DetermineVisibilityTypeForShipAllEmpires(ship, observer, galaxy, galaxy.StarDate) == VisibilityType.FullyVisible);

    internal static HashSet<short> MountedWeaponIds(WeaponList? weapons) =>
        weapons == null ? [] : weapons.OfType<Weapon>().Select(weapon => weapon.ComponentId).ToHashSet();

    private void List()
    {
        for (var i = 0; i < _rows.Length; i++)
        {
            var index = _page * _rows.Length + i;
            _rows[i].Visible = index < _filtered.Length;
            if (index >= _filtered.Length) continue;
            var component = _filtered[index];
            SetText(_rows[i], $"{component.ComponentId}  {component.Name}");
            _rows[i].ButtonColor = component == _selected ? ColorHelper.ButtonColorToggle : ColorHelper.ButtonColor;
        }
        var scope = _ship != null && !_allWeapons ? "mounted" : "weapons";
        SetText(_pageLabel, $"{_page + 1}/{Math.Max(1, (_filtered.Length + _rows.Length - 1) / _rows.Length)}   |   {_filtered.Length} {scope}");
    }

    private void Select(int index)
    {
        if (index >= _filtered.Length) return;
        _selected = _filtered[index];
        List();
        Sync();
    }

    private DWButton Button(string title, int x, int y, int width, int height, Action? click = null)
    {
        DWButton control = click == null ? new DWButton() : new ActionButton(this);
        control.Name = "WeaponFX " + Controls.Count;
        control.Text = title;
        control.PostScene = true;
        control.Visible = true;
        control.ButtonColor = click == null ? ButtonColor : ColorHelper.ButtonColor;
        control.ButtonColorHover = click == null ? ButtonColor : ColorHelper.ButtonColorHover;
        control.SetSizeAndPosition(new Vector2(width, height), new Vector2(x, y));
        control.Initialize(FontSize.Small, FontSize.Small, null!);
        if (click != null) control.ClickEvent = (_, _) => { if (CanInteract && control.CheckVisible()) click(); };
        AddControl(control);
        return control;
    }

    private void Sync()
    {
        _syncing = true;
        SetText(_heading, _selected == null ? _ship != null && !_allWeapons && _query.Length == 0 && _category == 0
            ? "No supported weapons installed" : "No matching weapons" : $"{_selected.Name}   [ID {_selected.ComponentId}]");
        _weaponScope.Visible = _ship != null;
        SetText(_weaponScope, _allWeapons ? "Show installed weapons" : "All weapons");
        var style = _selected == null ? new Entry.Style(-1) : Entry.Get(_selected.ComponentId);
        var selectedColor = style.ChannelColor(ActiveChannel);
        var color = selectedColor < 0 ? OriginalColor(_selected, ActiveChannel) : selectedColor;
        var linked = style.FollowsBody(ActiveChannel);
        var values = new[] { (float)((color >> 16) & 255), (color >> 8) & 255, color & 255,
            ActiveChannel == 3 ? PreviewImpactBrightness(_selected, style) : style.ChannelBrightness(ActiveChannel),
            style.Pulse, style.Length, style.Width, style.MountScale, style.CoreWidth, style.TwinGap, Entry.ShieldTraceDuration * 100, style.Opacity(ActiveChannel) * 100 };
        SetText(_labels[3], ActiveChannel switch { 2 => "Trail brightness", 3 => "Impact brightness", 4 => "Core brightness", _ => "Body brightness" });
        _sliders[5].Minimum = TrailMinimum(_selected);
        for (var i = 0; i < _sliders.Length; i++)
        {
            _sliders[i].Value = values[i];
            var visible = i == 10 && Entry.RveAvailable || _selected != null && i != 10 && (i >= 3 || ActiveChannel != 4) && (i != 4 || IsBeam(_selected) && ProjectileEffects.HasBeamPulse(_selected)) &&
                (i != 5 || !IsBeam(_selected) && !IsArea(_selected)) && (i != 7 || Entry.RveAvailable && _selected.WeaponMeshType != default) && (i != 8 || Entry.RveAvailable && IsBeam(_selected)) && (i != 9 || Entry.RveAvailable && BeamShape.Supports(_selected));
            _sliders[i].Visible = _labels[i].Visible = _resetSliders[i].Visible = visible;
            _sliders[i].Locked = i < 3 && linked;
            _resetSliders[i].Enabled = !_sliders[i].Locked;
        }
        SetText(_compare, Entry.Bypass ? "Original style" : "Custom style");
        UpdateSample(selectedColor);
        _sample.Visible = ActiveChannel != 4;
        _sample.Enabled = !linked;
        if (linked) SetText(_sample, "From Body");
        _trailLink.Visible = _selected != null && HasChannel(_selected, 2);
        _impactLink.Visible = _selected != null && HasChannel(_selected, 3);
        SetText(_trailLink, "Trail follows Body: " + (style.TrailFollowsBody ? "On" : "Off"));
        SetText(_impactLink, "Impact follows Body: " + (style.ImpactFollowsBody ? "On" : "Off"));
        SetText(_resetProfile, _confirmingProfileReset ? "Cancel" : $"Reset {Entry.CatalogLabel} profile");
        SetText(_confirmResetProfile, $"Confirm {Entry.CatalogLabel} reset");
        _confirmResetProfile.Visible = _confirmingProfileReset;
        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i].Visible = _selected != null && HasChannel(_selected, i + 1);
            _channels[i].ButtonColor = ActiveChannel == i + 1 ? ColorHelper.ButtonColorToggle : ColorHelper.ButtonColor;
        }
        SetText(_applyFamily, "Apply to family");
        SetText(_copyColors, "Copy family colors");
        var liveCharge = _selected != null && Entry.RveAvailable && ChargeEffects.Supports(_selected);
        var studio = StudioActive;
        _studioToggle.Enabled = !_studio.Failed && StudioPreview.Supports(_selected);
        SetText(_studioToggle, studio ? "3D preview" : "2D preview");
        _zoomIn.Visible = _zoomOut.Visible = _studioSurface.Visible = studio;
        _shieldPreview.Visible = _hullPreview.Visible = studio || _selected != null && ActiveChannel == 3;
        _shieldPreview.ButtonColor = _previewShield ? ColorHelper.ButtonColorToggle : ColorHelper.ButtonColor;
        _hullPreview.ButtonColor = !_previewShield ? ColorHelper.ButtonColorToggle : ColorHelper.ButtonColor;
        _previewMode.Visible = _selected != null && ActiveChannel != 3 && (liveCharge || IsBeam(_selected));
        var charge = liveCharge ? style.ChargeStyle : _chargeMode;
        SetText(_previewMode, "Charge: " + new[] { "off", "focus", "pulse", "ring" }[charge]);
        SetText(_save, "Save");
        SyncHistoryButtons();
        _syncing = false;
    }

    private void SyncHistoryButtons()
    {
        _undo.Enabled = Entry.CanUndo;
        _revert.Enabled = Entry.CanRevert;
    }

    private void ToggleColorLink(int channel)
    {
        if (_selected == null) return;
        var id = _selected.ComponentId;
        var style = Entry.Get(id);
        Entry.Set(id, channel == 2 ? style with { TrailFollowsBody = !style.TrailFollowsBody } :
            style with { ImpactFollowsBody = !style.ImpactFollowsBody });
        Sync();
    }

    private void PreviewImpact(bool shield)
    {
        if (_selected == null) return;
        _previewShield = shield;
        _impactReplay = Stopwatch.GetTimestamp();
        if (StudioActive) _studio.Replay();
        else ImpactPreview.Queue(_ship, _selected, shield);
        Sync();
    }

    private void ResetSlider(int row)
    {
        if (row == 10) Entry.ChangeTrace(1, recordUndo: true);
        else if (_selected != null)
        {
            var id = _selected.ComponentId;
            var defaults = Entry.Default(id);
            var style = ResetParameter(Entry.Get(id), defaults, row, ActiveChannel,
                OriginalColor(_selected, ActiveChannel), PreviewImpactBrightness(_selected, defaults), IsBeam(_selected));
            Entry.Set(id, style);
        }
        Sync();
    }

    internal static Entry.Style ResetParameter(Entry.Style style, Entry.Style defaults, int row, int channel,
        int originalColor, float impactBrightness, bool beam)
    {
        if (row < 3)
        {
            if (style.FollowsBody(channel)) return style;
            int current = style.ChannelColor(channel);
            int defaultColor = defaults.ChannelColor(channel);
            int basis = defaultColor < 0 ? originalColor : defaultColor;
            int mask = 255 << ((2 - row) * 8);
            int result = ((current < 0 ? originalColor : current) & ~mask) | (basis & mask);
            if (result == basis) result = defaultColor;
            return channel switch
            {
                2 => style with { TrailColor = result },
                3 => style with { ImpactColor = result },
                _ => style with { BodyColor = result }
            };
        }
        return row switch
        {
            3 => style.WithLayerBrightness(channel, channel == 3 ? impactBrightness : defaults.ChannelBrightness(channel)),
            4 => style with { Pulse = defaults.Pulse },
            5 => style with { Length = defaults.Length },
            6 => style with { Width = defaults.Width, BeamCoreWidth = beam ? style.CoreWidth : style.BeamCoreWidth },
            7 => style with { MountScale = defaults.MountScale },
            8 => style with { BeamCoreWidth = defaults.CoreWidth },
            9 => style with { TwinGap = defaults.TwinGap },
            11 => channel switch
            {
                2 => style with { TrailOpacity = defaults.TrailOpacity, LegacyTrailAlpha = defaults.LegacyTrailAlpha },
                3 => style with { ImpactOpacity = defaults.ImpactOpacity },
                4 => style with { CoreOpacity = defaults.CoreOpacity },
                _ => style with { BodyOpacity = defaults.BodyOpacity, LegacyBodyAlpha = defaults.LegacyBodyAlpha }
            },
            _ => style
        };
    }

    private static int OriginalColor(ComponentDefinition? component, int channel)
    {
        var effect = component?.WeaponEffect;
        Color? tint = channel switch
        {
            2 => effect?.TrailEffectTintColor,
            3 when !string.IsNullOrEmpty(effect?.HullImpactEffectFilepath) => effect.HullImpactEffectTintColor,
            3 => effect?.ArmorImpactEffectTintColor,
            _ => effect?.TextureTintColor
        };
        return tint is { A: > 0 } color ? (color.R << 16) | (color.G << 8) | color.B : 0xFFFFFF;
    }

    private void Changed(int row)
    {
        if (_syncing) return;
        if (row == 10)
        {
            if (Entry.ChangeTrace(_sliders[row].Value / 100f, _editedSlider != row))
            {
                _editedSlider = row;
                SetText(_save, "Save");
                SyncHistoryButtons();
            }
            return;
        }
        if (_selected == null) return;
        var old = Entry.Get(_selected.ComponentId);
        if (row < 3 && old.FollowsBody(ActiveChannel)) return;
        var updated = row switch
        {
            < 3 => old.WithChannel(ActiveChannel, ((int)_sliders[0].Value << 16) | ((int)_sliders[1].Value << 8) | (int)_sliders[2].Value),
            3 => old.WithLayerBrightness(ActiveChannel, _sliders[3].Value),
            4 => old with { Pulse = _sliders[4].Value },
            5 => old with { Length = _sliders[5].Value },
            6 => old with { Width = _sliders[6].Value, BeamCoreWidth = IsBeam(_selected) ? old.CoreWidth : old.BeamCoreWidth },
            7 => old with { MountScale = _sliders[7].Value },
            8 => old with { BeamCoreWidth = _sliders[8].Value },
            9 => old with { TwinGap = _sliders[9].Value },
            11 => old.WithOpacity(ActiveChannel, _sliders[11].Value / 100f),
            _ => old
        };
        if (!Entry.ChangeStyle(_selected.ComponentId, updated, _editedSlider != row)) return;
        _editedSlider = row;
        UpdateSample(updated.ChannelColor(ActiveChannel));
        SetText(_save, "Save");
        SyncHistoryButtons();
    }

    private void UpdateSample(int color) => SetText(_sample, color < 0 ? "Original color" : $"#{color:X6} | Clear");

    private void Preset(int preset)
    {
        if (_selected == null) return;
        var style = Entry.Get(_selected.ComponentId);
        var updated = preset switch
        {
            0 => style.WithPresetBrightness(.75f) with { Pulse = .35f, Width = .9f, Length = 1f },
            1 => style.WithPresetBrightness(1.4f) with { Pulse = 1f, Width = 1.05f, Length = 1.2f },
            _ => style.WithPresetBrightness(1.15f) with { Pulse = .8f, Width = 1.4f, Length = 1.45f }
        };
        Entry.Set(_selected.ComponentId, updated);
        Sync();
    }

    public override void Render(RenderDrawContext renderDrawContext, SpriteBatch spriteBatch, Point mousePoint, bool renderHoverTips)
    {
        if (!CanInteract) return;
        base.Render(renderDrawContext, spriteBatch, mousePoint, renderHoverTips);
        if (_selected == null) return;
        var rect = CalculateAbsoluteRectangle();
        var area = new RectangleF(rect.X + 331, rect.Y + 567, 445, 98);
        var style = Entry.Bypass ? new Entry.Style(-1) : Entry.Get(_selected.ComponentId);
        var impactBrightness = PreviewImpactBrightness(_selected, style);
        var liveCharge = Entry.RveAvailable && ChargeEffects.Supports(_selected);
        var charge = liveCharge ? style.ChargeStyle : _chargeMode;
        if (StudioActive)
        {
            var viewport = new RectangleF(area.X, area.Y, _studioSurface.Size.X, _studioSurface.Size.Y);
            DrawingHelper.FillRectangle(spriteBatch, viewport, new Color(8, 13, 24, 255));
            var imageHeight = Math.Min(viewport.Height, viewport.Width * StudioFrame.Height / StudioFrame.Width);
            var imageWidth = imageHeight * StudioFrame.Width / StudioFrame.Height;
            var image = new RectangleF(viewport.X + (viewport.Width - imageWidth) * .5f,
                viewport.Y + (viewport.Height - imageHeight) * .5f, imageWidth, imageHeight);
            if (_studio.Draw(renderDrawContext, spriteBatch, image, _selected, style, _previewShield, impactBrightness, charge)) return;
        }
        DrawingHelper.FillRectangle(spriteBatch, area, new Color(8, 13, 24, 255));
        var tint = style.ChannelTint(1) ?? _selected.WeaponEffect?.TextureTintColor.ToVector3() ?? new Vector3(.65f, .83f, 1f);
        if (tint.LengthSquared() < .00001f && style.ChannelColor(1) < 0) tint = new Vector3(.65f, .83f, 1f);
        var x = area.X + 61;
        var y = area.Y + 49;
        var target = area.Right - 45;
        var time = (float)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency % 1.7);
        var beam = IsBeam(_selected);
        var wave = IsArea(_selected);
        var seeking = IsSeeking(_selected);
        var trailTint = style.ChannelTint(2) ?? _selected.WeaponEffect?.TrailEffectTintColor.ToVector3() ?? tint;
        if (trailTint.LengthSquared() < .00001f && style.ChannelColor(2) < 0) trailTint = tint;
        var impactTint = style.ChannelTint(3) ?? (seeking ? new Vector3(.1f, 1, 1) : tint);
        if (ActiveChannel == 3)
        {
            var elapsed = _impactReplay == 0 ? 1 : Stopwatch.GetElapsedTime(_impactReplay).TotalSeconds;
            var progress = elapsed < .6 ? (float)(elapsed / .6) : .2f;
            var power = .5f * (1 - progress) * impactBrightness * style.ImpactOpacity;
            var center = area.X + area.Width * .5f;
            if (_previewShield)
                Ring(spriteBatch, center, y, 35, new Color(41, 77, 103, 255), .68f);
            else
                DrawingHelper.FillRectangle(spriteBatch, new RectangleF(center - 30, y - 12, 60, 24), new Color(47, 58, 73, 255));
            if (power > 0)
            {
                var glow = Glow(impactTint, power);
                if (_previewShield)
                    Ring(spriteBatch, center, y, 8 + progress * 29, glow, .68f);
                else
                    for (var i = 0; i < 7; i++)
                    {
                        var angle = MathF.Tau * i / 7;
                        var reach = (i % 2 == 0 ? 15 : 10) * (1 + progress);
                        DrawingHelper.DrawLine(spriteBatch, center, y, center + MathF.Cos(angle) * reach,
                            y + MathF.Sin(angle) * reach, glow, 2);
                    }
                DrawingHelper.FillRectangle(spriteBatch, new RectangleF(center - 2, y - 2, 4, 4), glow);
            }
            return;
        }
        DrawingHelper.FillRectangle(spriteBatch, new RectangleF(x - 32, y - 7, 32, 14), new Color(66, 85, 113, 255));
        DrawingHelper.FillRectangle(spriteBatch, new RectangleF(target, y - 21, 17, 42), new Color(53, 66, 94, 255));
        if (style.BodyOpacity > 0 && (beam || liveCharge) && charge != 0 && time < ChargeEffects.Window)
        {
            var progress = time / ChargeEffects.Window;
            var size = (5f + progress * 16f) * style.Width;
            var color = Glow(tint, progress * style.ChannelBrightness(1) * style.BodyOpacity);
            if (charge == 1)
            {
                DrawingHelper.DrawLine(spriteBatch, x + 17 * (1 - progress), y - size, x, y, color, 2);
                DrawingHelper.DrawLine(spriteBatch, x + 17 * (1 - progress), y + size, x, y, color, 2);
            }
            else if (charge == 3) Ring(spriteBatch, x, y, size, color);
            else DrawingHelper.FillRectangle(spriteBatch, new RectangleF(x - size / 2, y - size / 2, size, size), color);
        }
        if (time is >= .3f and < .94f)
        {
            var elapsed = (time - .3f) / .64f;
            var pulse = beam ? 1f - style.Pulse * .23f * (1f + MathF.Sin(elapsed * 28)) : 1f;
            var color = Glow(tint, style.ChannelBrightness(1) * pulse * style.BodyOpacity);
            var width = 3f * style.Width;
            if (wave && style.BodyOpacity > 0) Ring(spriteBatch, x + elapsed * (target - x), y, (4 + elapsed * 21) * style.Width, color);
            else if (beam)
            {
                var gap = Entry.RveAvailable && BeamShape.Supports(_selected) ? style.TwinGap * width : 0f;
                foreach (var sign in gap > 0 ? new[] { -1f, 1f } : new[] { 0f })
                {
                    var last = new Vector2(x, y);
                    for (var i = 1; i <= 24; i++)
                    {
                        var along = i / 24f;
                        var taper = Math.Clamp(along / .08f, 0, 1) * Math.Clamp((1 - along) / .08f, 0, 1);
                        var point = new Vector2(x + along * (target - x), y + sign * gap * taper);
                        if (style.BodyOpacity > 0) DrawingHelper.DrawLine(spriteBatch, last.X, last.Y, point.X, point.Y, color, width * 2f);
                        if (Entry.RveAvailable && style.CoreOpacity > 0) DrawingHelper.DrawLine(spriteBatch, last.X, last.Y, point.X, point.Y, Glow(Vector3.One * Math.Max(tint.X, Math.Max(tint.Y, tint.Z)), style.ChannelBrightness(4) * pulse * style.CoreOpacity), style.CoreWidth);
                        last = point;
                    }
                }
            }
            else
            {
                var head = x + elapsed * (target - x);
                if (style.Length > 0 && (seeking ? style.TrailOpacity : style.BodyOpacity) > 0)
                    DrawingHelper.DrawLine(spriteBatch, Math.Max(x, head - 45 * style.Length), y, head, y,
                        seeking ? Glow(trailTint, style.ChannelBrightness(2) * style.TrailOpacity) : color, width);
                if (!seeking && HasChannel(_selected, 2) && style.TrailOpacity > 0 && style.Length > 0)
                    DrawingHelper.DrawLine(spriteBatch, Math.Max(x, head - 60 * style.Length), y + width + 2, head, y + width + 2, Glow(trailTint, style.ChannelBrightness(2) * style.TrailOpacity), width);
                if (seeking && style.BodyOpacity > 0) DrawingHelper.FillRectangle(spriteBatch, new RectangleF(head - width, y - width, width * 2, width * 2), color);
            }
        }
        if (style.ImpactOpacity > 0 && time is >= .94f and < 1.2f) Ring(spriteBatch, target, y, 7 + (time - .94f) * 70, Glow(impactTint, (1.2f - time) * 4 * impactBrightness * style.ImpactOpacity));
    }

    private static float PreviewImpactBrightness(ComponentDefinition? component, Entry.Style style) =>
        component != null && Entry.RveAvailable && IsBeam(component) ? style.BeamImpactBrightness :
        Entry.RveAvailable && component != null && ProjectileEffects.HasTrail(component) ? style.RveImpactBrightness :
        style.NativeImpactBrightness;

    private static void Ring(SpriteBatch batch, float x, float y, float radius, Color color, float height = 1f)
    {
        const int sides = 16;
        for (var i = 0; i < sides; i++)
        {
            var a = MathF.Tau * i / sides;
            var b = MathF.Tau * (i + 1) / sides;
            DrawingHelper.DrawLine(batch, x + radius * MathF.Cos(a), y + radius * height * MathF.Sin(a),
                x + radius * MathF.Cos(b), y + radius * height * MathF.Sin(b), color, 2f);
        }
    }

    private static Color Glow(Vector3 tint, float power) => new(new Vector4(Vector3.Clamp(tint * power, Vector3.Zero, Vector3.One), 1));
    internal static bool HasChannel(ComponentDefinition component, int channel) => channel switch
    {
        1 => true,
        2 => Entry.RveAvailable && IsSeeking(component) && ProjectileEffects.HasTrail(component) || component.WeaponEffect?.TrailEffectSpawnRate > 0,
        3 => Entry.RveAvailable && (IsBeam(component) || IsSeeking(component) || component.Values.Any(s => s.WeaponFireType == WeaponFireType.DirectFire)) ||
            !string.IsNullOrEmpty(component.WeaponEffect?.ArmorImpactEffectFilepath) || !string.IsNullOrEmpty(component.WeaponEffect?.HullImpactEffectFilepath),
        4 => Entry.RveAvailable && IsBeam(component),
        _ => false
    };
    internal static float TrailMinimum(ComponentDefinition? component) =>
        component?.WeaponEffect is { } effect &&
        (effect.FlipbookTextureFilepath?.Contains("PulseTorpedoFlipbook", StringComparison.Ordinal) == true ||
            string.Equals(component.Family, "Torpedoes", StringComparison.OrdinalIgnoreCase) && effect.TrailEffectSpawnRate > 0) ? 0f : .5f;
    internal static bool IsSeeking(ComponentDefinition component) => component.Values.Any(stats => stats.WeaponFireType == WeaponFireType.Seeking);
    internal static bool IsBeam(ComponentDefinition component) => component.Values.Any(stats => stats.WeaponFireType == WeaponFireType.FullLengthBeam);
    internal static bool IsArea(ComponentDefinition component) => component.Values.Any(stats => stats.WeaponAreaEffectRange > 0);
    private static void SetText(DWButton button, string text)
    {
        if (button.Text == text) return;
        button.Text = text;
        button.ResetTextLayout();
    }
}
