using System.Reflection;
using LocalModUi;
using System.Reflection.Emit;
using System.Text.Json;
using System.Runtime.Versioning;
using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Input;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
public static class Entry
{
    private const string PatchId = "local.dw2.weaponfx";
    private const string Rve = "DW2R_Visual_Effects.Patches.";
    private static readonly Harmony Hooks = new(PatchId);
    private static readonly string ConfigFolder = Path.Combine(Path.Combine(AppContext.BaseDirectory, "data", "SteamBetaProfile"), "DW2WeaponFX");
    private static string ConfigPath => Path.Combine(ConfigFolder, "settings-v4-" + (_catalogKey ?? "vanilla") + ".json");
    private static readonly string V3ConfigPath = Path.Combine(ConfigFolder, "settings-v3.json");
    private static string? _catalogKey;
    private sealed record Draft(Settings Current, Settings Saved, List<Settings> Undo);
    private static readonly Dictionary<string, Draft> Profiles = new();
    internal static bool RveAvailable { get; private set; }
    private static readonly string LegacyConfigPath = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "settings.json");
    private static readonly string LogPath = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "WeaponFX.log");
    private static bool _initialized;
    private static bool _attempted;
    private static bool _ready;
    private static Panel? _panel;
    private static DWButton? _mapButton;
    private static Galaxy? _galaxy;
    private static short _observer = -1;
    internal static ComponentDefinition[] Components { get; private set; } = [];

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        NativeToolDock.Install(Hooks);
        MissileDepth.Install(Hooks);
        Hooks.Patch(AccessTools.Method(typeof(UserInterfaceController), "Update"), postfix: Hook(nameof(UpdateUi)));
        Hooks.Patch(AccessTools.Method(typeof(UserInterfaceController), nameof(UserInterfaceController.DoMouseDown)),
            prefix: Hook(nameof(ClearPanelPress)));
        Hooks.Patch(AccessTools.Method(typeof(InputSystem), nameof(InputSystem.HandleKeyPress)), prefix: Hook(nameof(AllowGameKey)));
        Hooks.Patch(AccessTools.Method(typeof(DWTextBox), nameof(DWTextBox.AcceptKeyPress)), prefix: new HarmonyMethod(typeof(Panel), nameof(Panel.AllowSingleSearchKey)));
        Hooks.Patch(AccessTools.Method(typeof(DWTextBox), nameof(DWTextBox.AcceptKeyPresses)), prefix: new HarmonyMethod(typeof(Panel), nameof(Panel.AllowSearchKey)));
        Log("Loaded. Ctrl+Alt+V opens Weapon FX.");
    }

    private static void ClearPanelPress() => _panel?.ClearPressedButton();

    private static void UpdateUi(int screenHeight)
    {
        try
        {
            var game = Galaxy.Game as DWGame;
            var observer = game?.Galaxy?.Empires.GetPlayer()?.EmpireId ?? (short)-1;
            if (_galaxy != game?.Galaxy || _observer != observer)
            {
                HidePanel();
                _galaxy = game?.Galaxy;
                _observer = observer;
                ActivateCatalog(_galaxy);
            }
            if (_galaxy == null || observer < 0 || game?.Input is not { } input) { HidePanel(); NativeToolDock.Detach(_mapButton); return; }
            if (!_attempted)
            {
                _attempted = true;
                Install(FindRve());
            }
            if (!_ready) { NativeToolDock.Detach(_mapButton); return; }
            _mapButton ??= NativeToolDock.Register(NativeToolDock.Fx, "Weapon FX Tuner", "Visual settings and weapon previews. Ctrl+Alt+V", TogglePanel);
            NativeToolDock.Update(_mapButton);
            if (_panel != null && (!UserInterfaceController.CustomControls.TryGetValue(_panel.Name, out var registered) || !ReferenceEquals(registered, _panel))) HidePanel();
            if (NativeToolDock.ToolbarAvailable() && IsPanelShortcut(input)) TogglePanel();
            if (!UiAvailable()) { if (_panel != null) { _panel.Visible = false; _panel.Suspend(); } return; }
            if (_panel != null) _panel.Visible = true;
            if (_panel != null && input.IsKeyPressed(Keys.Escape)) { HidePanel(); return; }
            _panel?.Place(screenHeight);
        }
        catch (Exception ex)
        {
            _ready = false;
            HidePanel();
            NativeToolDock.Detach(_mapButton);
            Log("Panel stopped: " + ex);
        }
    }

    private static bool IsPanelShortcut(InputManager input) => input.IsKeyPressed(Keys.V) &&
        (input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl)) &&
        (input.IsKeyDown(Keys.LeftAlt) || input.IsKeyDown(Keys.RightAlt));

    internal static bool UiAvailable() => _ready && LocalModUi.MapUiGate.TryGetContext(out _, out var galaxy) &&
        ReferenceEquals(galaxy, _galaxy) && galaxy.Empires.GetPlayer()?.EmpireId == _observer &&
        NativeToolDock.BodyAvailable(NativeToolDock.Fx);

    private static bool AllowGameKey(KeyMapping keyMapping)
    {
        // The reserved chord must not fall through to Victory Conditions behind another window.
        if (_ready && keyMapping?.Key == Keys.V && (Galaxy.Game as DWGame)?.Input is { } input && IsPanelShortcut(input)) return false;
        if (!UiAvailable()) return true;
        if (keyMapping?.Key == Keys.Escape && _panel is { Visible: true }) { HidePanel(); return false; }
        return true;
    }

    private static void TogglePanel()
    {
        if (!NativeToolDock.ToolbarAvailable() || !_ready) return;
        if (_panel != null && NativeToolDock.Selected == NativeToolDock.Fx) { HidePanel(); return; }
        NativeToolDock.Select(NativeToolDock.Fx);
        _panel ??= new Panel(HidePanel);
        _panel.Visible = true; _panel.Place(UserInterfaceController.ScreenHeight);
        UserInterfaceController.AddCustomControl(_panel);
    }

    private static void HidePanel()
    {
        NativeToolDock.Clear(NativeToolDock.Fx);
        if (_panel == null) return;
        _panel.Visible = false;
        _panel.Suspend();
        _panel.Close();
        if (_panel.Controls.Contains(UserInterfaceController.ActivelySlidingSlider))
            UserInterfaceController.ActivelySlidingSlider = null!;
        UserInterfaceController.RemoveCustomControl(_panel);
        _panel = null;
    }

    internal static Assembly? FindRve() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(assembly => assembly.GetType(Rve + "BeamCore", false) != null);

    internal static void Install(Assembly? assembly)
    {
        _attempted = true;
        try
        {
            Patch(typeof(BillboardRenderItem), "Update", postfix: nameof(FixBeamLength));
            NativeEffects.Install(Hooks);
            MuzzleAnchors.Install(Hooks);
            MuzzleBeams.Install(Hooks, assembly);
            if (assembly != null)
            {
                Patch(assembly.GetType(Rve + "BeamCore", true)!, "Add", transpiler: nameof(BeamCode));
                Patch(assembly.GetType(Rve + "BoltCore", true)!, "Add", transpiler: nameof(BoltCode));
                BeamLayers.Install(Hooks, assembly);
                ProjectileEffects.Install(Hooks, assembly);
                Turrets.Install(Hooks, assembly);
                ChargeEffects.Install(Hooks, assembly);
                ShieldSurface.Install(Hooks, assembly);
                ShieldTrace.Install(Hooks);
                RveAvailable = true;
            }
            _ready = true;
            Log(RveAvailable ? "Refreshed and native visual controls installed." : "Native visual controls installed; Refreshed-only controls unavailable.");
        }
        catch (Exception ex)
        {
            foreach (var method in Hooks.GetPatchedMethods().ToArray())
                if (method.DeclaringType != typeof(UserInterfaceController)) Hooks.Unpatch(method, HarmonyPatchType.All, PatchId);
            Log("Weapon FX disabled: " + ex);
            throw;
        }

        void Patch(Type type, string name, string? postfix = null, string? transpiler = null)
        {
            var method = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
            Hooks.Patch(method, postfix: postfix == null ? null : Hook(postfix), transpiler: transpiler == null ? null : Hook(transpiler));
        }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(Entry), name);

    private static IEnumerable<CodeInstruction> BeamCode(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToList();
        var calls = code.Select((operation, index) => (operation, index))
            .Where(x => x.operation.operand is MethodInfo method && method.DeclaringType == original.DeclaringType && method.Name == "AddQuad").ToArray();
        var locals = original.GetMethodBody()!.LocalVariables;
        if (calls.Length != 2 || code[calls[0].index - 10].opcode != OpCodes.Ldarg_2 ||
            locals[5].LocalType != typeof(Vector4) || locals[8].LocalType != typeof(Vector4) || locals[10].LocalType != typeof(Vector3))
            throw new InvalidOperationException("Unrecognized Refreshed beam renderer.");
        code.InsertRange(calls[0].index - 10, [new(OpCodes.Ldarg_0), new(OpCodes.Ldarga_S, (byte)2), new(OpCodes.Ldarg_3), CodeInstruction.LoadLocal(5, true),
            CodeInstruction.LoadLocal(7, true), CodeInstruction.LoadLocal(8, true), CodeInstruction.LoadLocal(10, true), CodeInstruction.Call(typeof(Entry), nameof(TuneBeam))]);
        return code;
    }

    private static IEnumerable<CodeInstruction> BoltCode(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToList();
        var firstWrite = code.FindIndex(op => op.opcode == OpCodes.Stfld && op.operand is FieldInfo field &&
            field.Name == "Tail" && field.DeclaringType?.DeclaringType == original.DeclaringType);
        var indexStore = code.FindIndex(op => (op.opcode == OpCodes.Stloc || op.opcode == OpCodes.Stloc_S) &&
            op.operand is LocalBuilder local && local.LocalIndex == 9);
        var locals = original.GetMethodBody()!.LocalVariables;
        if (firstWrite < 0 || indexStore < 0 || indexStore >= firstWrite || locals[2].LocalType != typeof(Vector3) ||
            locals[6].LocalType != typeof(Vector4) || locals[7].LocalType != typeof(Vector3) || locals[8].LocalType != typeof(Vector3))
            throw new InvalidOperationException("Unrecognized Refreshed bolt renderer.");
        code.InsertRange(indexStore + 1, [new(OpCodes.Ldarg_0), CodeInstruction.LoadLocal(2, true), CodeInstruction.LoadLocal(6, true),
            CodeInstruction.LoadLocal(7, true), CodeInstruction.LoadLocal(8, true), CodeInstruction.Call(typeof(Entry), nameof(TuneBolt))]);
        return code;
    }

    private static void TuneBeam(WeaponBlast blast, ref Vector3 tail, Vector3 tip, ref Vector4 color, ref Vector4 extra, ref Vector4 firing, ref Vector3 pulse)
    {
        tail = Turrets.BeamMuzzle(blast, tail, tip);
        if (StyleOf(blast.FiringWeaponComponentId) is not { } style) return;
        var tint = style.ChannelTint(1) ?? new Vector3(color.X, color.Y, color.Z);
        var body = style.ChannelBrightness(1);
        color = new Vector4(tint, color.W * body);
        if (MissileDepth.BeamShapeReady)
        {
            extra.W = BeamShape.Pack(extra.W, style.CoreWidth / style.Width, BeamShape.Supports(blast.Component) ? style.TwinGap : 0);
            extra.Z = BeamShape.PackCoreGain(extra.Z, style.ChannelBrightness(4) / body);
            firing.X = BeamShape.PackOpacity(firing.X, style.CoreOpacity, style.BodyOpacity);
        }
        extra.X *= style.Width;
        firing.Y *= Math.Min(1f, style.Pulse);
        pulse.Y *= style.Pulse;
        pulse.Z *= style.Pulse;
    }

    private static void TuneBolt(WeaponBlast blast, ref Vector3 tint, ref Vector4 data, ref Vector3 tail, ref Vector3 tip)
    {
        if (StyleOf(blast.FiringWeaponComponentId) is not { } style) return;
        tint = style.ChannelTint(1) ?? tint;
        data.X *= style.Width;
        data.W *= style.ChannelBrightness(1) * style.BodyOpacity;
        tail = tip + (tail - tip) * style.Length;
        var offset = Turrets.LaunchOffset(blast);
        tip += offset;
        tail += offset;
    }

    private static void FixBeamLength(BillboardRenderItem __instance, bool __result, CameraInfo camera, Location? location)
    {
        if (__result || camera == null || location == null || __instance.ParentType != ItemType.WeaponBlast ||
            __instance.Parent.Resolve(location.WeaponBlasts) is not { } blast ||
            blast.ComponentStats?.WeaponFireType != WeaponFireType.FullLengthBeam ||
            blast.Component?.WeaponEffect is not { } effect || effect.BodyScalingAbsolute != Vector2.Zero) return;
        var scale = effect.BodyScaling.Y;
        if (scale > 0 && scale != 1 && float.IsFinite(scale)) __instance.Scale.Y /= scale;
    }

    private static Dictionary<short, Style> _styles = [];
    private static Dictionary<short, Style> _profileDefaults = [];
    private static volatile bool _bypass;
    private static readonly Style Stock = new(-1);
    private static readonly Style Particle = new(NormalizeColor(0xFFA431));
    private static readonly Style Fusion = new(NormalizeColor(0x20E870));
    private static readonly Style IonLance = new(NormalizeColor(0xAC5AFF));
    private static readonly Style Maxos = new(NormalizeColor(0xA5E650), 1.1f, 1.4f, 1.15f);
    private static readonly Style Repeating = new(NormalizeColor(0x5DFBB0), 1.1f, 1.4f, 1.15f);

    internal sealed record Style(int Color, float Brightness = 1, float Length = 1, float Width = 1, float Pulse = 1, float MountScale = 1)
    {
        internal Vector3? Tint => Color < 0 ? null : ToTint(Color);
        public int ChargeStyle { get; init; } = 1;
        // Null inherits the legacy shared color; -1 explicitly restores this channel only.
        public int? BodyColor { get; init; }
        public int? TrailColor { get; init; }
        public int? ImpactColor { get; init; }
        public bool TrailFollowsBody { get; init; }
        public bool ImpactFollowsBody { get; init; }
        public float? BeamCoreWidth { get; init; }
        public float TwinGap { get; init; }
        public float BodyOpacity { get; init; } = 1;
        public float CoreOpacity { get; init; } = 1;
        public float TrailOpacity { get; init; } = 1;
        public float ImpactOpacity { get; init; } = 1;
        public float? ImpactBrightness { get; init; }
        public float? BodyBrightness { get; init; }
        public float? TrailBrightness { get; init; }
        public float? CoreBrightness { get; init; }
        public float? ImpactInheritedBrightness { get; init; }
        public float? NativeImpactBrightnessOverride { get; init; }
        // Migration-only compatibility: old RVE tied smoke alpha to brightness and did not scale impact brightness.
        public bool LegacyBodyAlpha { get; init; }
        public bool LegacyTrailAlpha { get; init; }
        public bool LegacyImpactBrightness { get; init; }
        internal float ImpactBase => ImpactInheritedBrightness ?? Brightness;
        internal float BeamImpactBrightness => ImpactBrightness ?? ImpactBase;
        internal float RveImpactBrightness => ImpactBrightness ?? (LegacyImpactBrightness ? 1f : ImpactBase);
        internal float NativeImpactBrightness => ImpactBrightness ?? NativeImpactBrightnessOverride ??
            (ImpactColor == null && !ImpactFollowsBody ? 1f : ImpactBase);
        internal float ChannelBrightness(int channel) => (channel switch
        {
            2 => TrailBrightness, 4 => CoreBrightness, _ => BodyBrightness
        }) ?? Brightness;
        internal Style WithLayerBrightness(int channel, float value) => channel switch
        {
            2 => this with { TrailBrightness = value },
            3 => this with { ImpactBrightness = value },
            4 => this with { CoreBrightness = value },
            _ => this with { BodyBrightness = value }
        };
        internal Style WithPresetBrightness(float value) => this with
        {
            Brightness = value, ImpactInheritedBrightness = ImpactBase,
            BodyBrightness = null, TrailBrightness = null, CoreBrightness = null
        };
        internal float CoreWidth => BeamCoreWidth ?? Math.Min(Width, 3f);
        internal float Opacity(int channel) => channel switch { 2 => TrailOpacity, 3 => ImpactOpacity, 4 => CoreOpacity, _ => BodyOpacity };
        internal Style WithOpacity(int channel, float opacity) => channel switch
        {
            2 => this with { TrailOpacity = opacity, LegacyTrailAlpha = false }, 3 => this with { ImpactOpacity = opacity, LegacyImpactBrightness = false },
            4 => this with { CoreOpacity = opacity }, _ => this with { BodyOpacity = opacity, LegacyBodyAlpha = false }
        };
        internal bool FollowsBody(int channel) => channel == 2 ? TrailFollowsBody : channel == 3 && ImpactFollowsBody;
        internal int ChannelColor(int channel) => (FollowsBody(channel) ? BodyColor : channel switch
            { 1 => BodyColor, 2 => TrailColor, 3 => ImpactColor, _ => Color }) ?? Color;
        internal Vector3? ChannelTint(int channel) => ChannelColor(channel) is var color && color >= 0 ? ToTint(color) : null;
        internal Style WithChannel(int channel, int? color) => channel switch
        {
            1 => this with { BodyColor = color }, 2 => this with { TrailColor = color },
            3 => this with { ImpactColor = color, LegacyImpactBrightness = false }, _ => this with { Color = color ?? -1 }
        };
    }

    private static Vector3 ToTint(int color)
    {
        var rgb = new Vector3((color >> 16) & 255, (color >> 8) & 255, color & 255);
        return rgb / 255f;
    }

    private static int NormalizeColor(int color)
    {
        if (color < 0) return color;
        var r = (color >> 16) & 255; var g = (color >> 8) & 255; var b = color & 255;
        var peak = Math.Max(r, Math.Max(g, b));
        return peak == 0 ? 0 : ((int)Math.Round(r * 255d / peak) << 16) | ((int)Math.Round(g * 255d / peak) << 8) | (int)Math.Round(b * 255d / peak);
    }

    internal static Style Get(short id) => Volatile.Read(ref _styles).TryGetValue(id, out var style) ? style : Default(id);
    internal static bool Bypass { get => _bypass; set => _bypass = value; }
    internal static void Set(short id, Style style) => ChangeStyle(id, style, recordUndo: true);

    internal static bool ChangeStyle(short id, Style style, bool recordUndo)
    {
        if (style == Get(id)) return false;
        if (recordUndo) RecordUndo();
        var copy = new Dictionary<short, Style>(Volatile.Read(ref _styles)) { [id] = style };
        Volatile.Write(ref _styles, copy);
        return true;
    }
    internal static void Reset(short id)
    {
        var copy = new Dictionary<short, Style>(Volatile.Read(ref _styles));
        if (!copy.Remove(id)) return;
        RecordUndo();
        Volatile.Write(ref _styles, copy);
    }

    internal static string CatalogLabel => _catalogKey == "xl" ? "XL" : "Vanilla";

    internal static void ResetProfile()
    {
        if (_styles.Count == 0 && ShieldTraceDuration == 1) return;
        RecordUndo();
        Volatile.Write(ref _styles, new Dictionary<short, Style>());
        ShieldTraceDuration = 1;
    }

    private const int UndoLimit = 32;
    private static List<Settings> _undo = [];
    private static Settings? _saved;
    internal static bool CanUndo => _undo.Count != 0;
    internal static bool CanRevert => _saved != null && _saved != CaptureSettings();

    private static void RecordUndo()
    {
        if (_undo.Count == UndoLimit) _undo.RemoveAt(0);
        _undo.Add(CaptureSettings());
    }

    internal static void Undo()
    {
        if (!CanUndo) return;
        RestoreSettings(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    internal static void RevertSaved()
    {
        if (!CanRevert || _saved == null) return;
        RecordUndo();
        RestoreSettings(_saved);
    }

    internal static bool ChangeTrace(float value, bool recordUndo)
    {
        if (ShieldTraceDuration == value) return false;
        if (recordUndo) RecordUndo();
        ShieldTraceDuration = value;
        return true;
    }

    internal static Style? StyleOf(short id)
    {
        if (_bypass) return null;
        return Get(id);
    }

    internal static Style Default(short id) => Volatile.Read(ref _profileDefaults).TryGetValue(id, out var style) ? style : Stock;

    private static void SetCatalog(Galaxy? galaxy)
    {
        Components = galaxy?.Components.Where(component => component.Values != null && (WeaponBlast.WeaponCategories.Contains(component.Category) ||
            component.Category is ComponentCategory.TractorBeam or ComponentCategory.PlanetaryDetonator or ComponentCategory.WeaponPsychic))
            .OrderBy(component => component.Name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var defaults = new Dictionary<short, Style>();
        foreach (var component in Components)
        {
            var scale = SizeScale(component.Name);
            var initial = Initial(component.Name);
            defaults[component.ComponentId] = initial with { Width = initial.Width * scale, MountScale = scale };
        }
        Volatile.Write(ref _profileDefaults, defaults);
    }

    private static Style Initial(string name) => Family(name) switch
    {
        "Beam, Particle" or "Particle Beam" => Particle,
        "Beam, Fusion" or "Fusion Beam" => Fusion,
        "Ion Lance" or "Electromagnetic Lance" => IonLance,
        "Blaster, Maxos" or "Maxos Blaster" => Maxos,
        "Defense, Repeating Blasters" or "Buckler Repeating Blaster" => Repeating,
        _ => Stock
    };

    internal static string CatalogKey(Galaxy galaxy) => galaxy.Components.Any(c => c.ComponentId == 638 && c.Name.Contains("Maxos", StringComparison.OrdinalIgnoreCase)) &&
        galaxy.Components.Any(c => c.ComponentId == 203 && c.Name.Contains("Obliterator", StringComparison.OrdinalIgnoreCase)) ? "xl" : "vanilla";

    private static void ActivateCatalog(Galaxy? galaxy)
    {
        SetCatalog(galaxy);
        if (galaxy == null) return;
        var key = CatalogKey(galaxy);
        if (key == _catalogKey) return;
        if (_catalogKey != null) Profiles[_catalogKey] = new Draft(CaptureSettings(), _saved ?? CaptureSettings(), _undo);
        _catalogKey = key;
        if (Profiles.TryGetValue(key, out var profile))
        {
            RestoreSettings(profile.Current);
            _saved = profile.Saved;
            _undo = profile.Undo;
        }
        else
        {
            Volatile.Write(ref _styles, new Dictionary<short, Style>());
            ShieldTraceDuration = 1;
            _undo = [];
            _saved = CaptureSettings();
            Load();
        }
    }

    internal static string SizeSuffix(string name)
    {
        var start = name.LastIndexOf('[');
        if (start < 0 || !name.EndsWith(']')) return "";
        var suffix = name[(start + 1)..^1].ToUpperInvariant();
        return suffix is "S" or "M" or "L" or "H" or "XL" or "FI" or "FB" or "FTR" or "PD" or "B" or "X" ? suffix : "";
    }

    internal static string Family(string name) => SizeSuffix(name).Length == 0 ? name.Trim() : name[..name.LastIndexOf('[')].TrimEnd();
    internal static float SizeScale(string name) => SizeSuffix(name) switch { "M" => 1.08f, "L" => 1.16f, "H" or "XL" => 1.25f, _ => 1f };

    internal static int ApplyFamily(short sourceId)
    {
        var source = Components.FirstOrDefault(c => c.ComponentId == sourceId);
        if (source == null) return 0;
        var family = Family(source.Name);
        var profile = Get(sourceId);
        var basis = Default(sourceId);
        var copy = new Dictionary<short, Style>(Volatile.Read(ref _styles));
        var count = 0;
        foreach (var component in Components.Where(c => string.Equals(Family(c.Name), family, StringComparison.OrdinalIgnoreCase)))
        {
            var target = Default(component.ComponentId);
            copy[component.ComponentId] = component.ComponentId == sourceId ? profile : profile with
            {
                Width = Math.Clamp(profile.Width / basis.Width * target.Width, .1f, 4f),
                BeamCoreWidth = profile.BeamCoreWidth == null ? null : Math.Clamp(profile.CoreWidth / basis.CoreWidth * target.CoreWidth, .1f, 3f),
                Length = Math.Clamp(profile.Length / basis.Length * target.Length, 0f, 2f),
                MountScale = Math.Clamp(profile.MountScale / basis.MountScale * target.MountScale, .75f, 1.5f)
            };
            count++;
        }
        RecordUndo();
        Volatile.Write(ref _styles, copy);
        return count;
    }

    internal static int CopyFamilyColors(short sourceId)
    {
        var source = Components.FirstOrDefault(component => component.ComponentId == sourceId);
        if (source == null) return 0;
        var family = Family(source.Name);
        var palette = Get(sourceId);
        var copy = new Dictionary<short, Style>(Volatile.Read(ref _styles));
        int count = 0;
        foreach (var component in Components)
        {
            if (component.ComponentId == sourceId || !string.Equals(Family(component.Name), family, StringComparison.OrdinalIgnoreCase)) continue;
            var original = Get(component.ComponentId);
            var colored = original with
            {
                BodyColor = palette.ChannelColor(1),
                TrailColor = palette.ChannelColor(2),
                ImpactColor = palette.ChannelColor(3)
            };
            if (colored == original) continue;
            // Preserve the old default brightness when adding an explicit native impact color.
            if (original.ImpactColor == null && !original.ImpactFollowsBody && original.ImpactBrightness == null &&
                original.ImpactBase != 1)
                colored = colored with { NativeImpactBrightnessOverride = 1 };
            copy[component.ComponentId] = colored;
            count++;
        }
        if (count == 0) return 0;
        RecordUndo();
        Volatile.Write(ref _styles, copy);
        return count;
    }

    private static float _shieldTraceDuration = 1;
    internal static float ShieldTraceDuration { get => Volatile.Read(ref _shieldTraceDuration); set => Volatile.Write(ref _shieldTraceDuration, value); }
    internal sealed record Settings(int Version, float ShieldTraceDuration, Dictionary<short, Style> Components);
    private static Settings CaptureSettings() => new(4, ShieldTraceDuration, Volatile.Read(ref _styles));
    private static void RestoreSettings(Settings settings)
    {
        Volatile.Write(ref _styles, settings.Components);
        ShieldTraceDuration = settings.ShieldTraceDuration;
    }
    internal static string SerializeSettings() => JsonSerializer.Serialize(CaptureSettings(), new JsonSerializerOptions { WriteIndented = true });

    internal static void ReadSettings(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Weapon FX settings must be an object.");
        var modern = document.RootElement.TryGetProperty("Components", out _);
        var settings = modern ? JsonSerializer.Deserialize<Settings>(json) : null;
        var values = modern ? settings?.Components : JsonSerializer.Deserialize<Dictionary<short, Style>>(json);
        var duration = settings?.ShieldTraceDuration ?? 1f;
        static bool ColorValid(int? color) => color is null or >= -1 and <= 0xFFFFFF;
        static bool BrightnessValid(float? brightness) => brightness is null or >= .2f and <= 2f;
        if (modern && settings?.Version is not (3 or 4) || !float.IsFinite(duration) || duration is < .1f or > 2f || values == null ||
            values.Any(pair => pair.Key < 0 || pair.Value is not { } style || !ColorValid(style.Color) || !ColorValid(style.BodyColor) ||
                !ColorValid(style.TrailColor) || !ColorValid(style.ImpactColor) ||
                !BrightnessValid(style.ImpactBrightness) || !BrightnessValid(style.ImpactInheritedBrightness) ||
                !BrightnessValid(style.BodyBrightness) || !BrightnessValid(style.TrailBrightness) || !BrightnessValid(style.CoreBrightness) ||
                !BrightnessValid(style.NativeImpactBrightnessOverride) ||
                !float.IsFinite(style.Brightness) || !float.IsFinite(style.Length) || !float.IsFinite(style.Width) || !float.IsFinite(style.Pulse) || !float.IsFinite(style.MountScale) ||
                !float.IsFinite(style.CoreWidth) || !float.IsFinite(style.TwinGap) || style.CoreWidth is < .1f or > 3f || style.TwinGap is < 0 or > 3f ||
                style.Brightness is < .2f or > 2f || style.Length is < 0f or > 2f || style.Width is < .1f or > 4f || style.Pulse is < 0 or > 1f ||
                style.MountScale is < .75f or > 1.5f || style.ChargeStyle is < 0 or > 3 ||
                new[] { style.BodyOpacity, style.CoreOpacity, style.TrailOpacity, style.ImpactOpacity }.Any(v => !float.IsFinite(v) || v < 0 || v > 1)))
            throw new JsonException("Invalid Weapon FX settings.");
        if (settings?.Version != 4)
            values = values.ToDictionary(p => p.Key, p => p.Value with
            {
                Color = NormalizeColor(p.Value.Color),
                BodyColor = p.Value.BodyColor is { } body ? NormalizeColor(body) : null,
                TrailColor = p.Value.TrailColor is { } trail ? NormalizeColor(trail) : null,
                ImpactColor = p.Value.ImpactColor is { } impact ? NormalizeColor(impact) : null,
                LegacyBodyAlpha = true, LegacyTrailAlpha = true, LegacyImpactBrightness = true
            });
        _saved = new Settings(4, duration, values);
        RestoreSettings(_saved);
        _undo.Clear();
    }

    internal static bool Save() => SaveSettings(ConfigPath);

    private static bool SaveSettings(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", SerializeSettings());
            File.Move(path + ".tmp", path, overwrite: true);
            _saved = CaptureSettings();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Settings save failed: " + ex.Message);
            return false;
        }
    }

    private static void Load()
    {
        var path = File.Exists(ConfigPath) ? ConfigPath : _catalogKey == "xl" ? File.Exists(V3ConfigPath) ? V3ConfigPath : LegacyConfigPath : ConfigPath;
        if (!File.Exists(path)) return;
        try { ReadSettings(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Log("Using default Weapon FX settings: " + ex.Message); }
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
