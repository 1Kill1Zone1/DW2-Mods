using System.Reflection;
using System.Runtime.CompilerServices;
using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace UiChecks;

// Real native types/field identities and registered child-bearing HUD roots, without native/GPU Init.
internal sealed class NativeHud : IDisposable
{
    static readonly string[] Names = ["MessageCategoryButtons", "SelectionPanel", "SelectionNavigationButtons", "ActionButtons", "CockpitButtons", "MapOverlays", "MessageFilterPanel", "EmpireSectionList", "MainViewHeader", "MessageListView", "MessageLog", "MessageButtonStrip"];
    static readonly Type RendererBase = typeof(DWGame).Assembly.GetType("DistantWorlds2.DWRendererBase", true)!;
    readonly Dictionary<string, DWControl> priorControls;
    readonly List<(FieldInfo Field, object? Target, object? Prior)> priorFields = [];
    readonly List<ReaderWriterLockSlim> controlLocks = [];
    internal readonly List<DWControl> Roots = [];
    internal readonly List<(FieldInfo Field, object? Target, DWControl Control)> Bindings = [];
    internal readonly DWPanel GameControls;
    internal readonly MapOverlayButtons MapOverlays;
    bool disposed;

    internal NativeHud(NativeUi ui, Galaxy galaxy) : this(ui.Renderer, galaxy) { }

    internal NativeHud(ScaledRenderer renderer, Galaxy galaxy)
    {
        lock (UserInterfaceController.CustomControlLock)
        {
            priorControls = new(UserInterfaceController.CustomControls);
            UserInterfaceController.CustomControls.Clear();
            try
            {
                foreach (string name in Names)
                {
                    var fields = new[] { AccessTools.Field(typeof(UserInterfaceController), name), AccessTools.Field(RendererBase, name) }
                        .OfType<FieldInfo>().Distinct().ToArray();
                    if (fields.Length == 0) throw new MissingFieldException("Native HUD field: " + name);
                    var control = Create(fields[0].FieldType, name == "MapOverlays" ? "MapOverlayButtons" : name, new(10, 10), new(120, 40), true);
                    foreach (var field in fields) Bind(field, null, control);
                    Register(name, control);
                }
                // Deliberately use different objects from the static aliases, as the live renderer can.
                foreach (string name in new[] { "ActionButtons", "CockpitButtons", "SelectionNavigation" })
                {
                    var field = typeof(ScaledRenderer).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        ?? throw new MissingFieldException(typeof(ScaledRenderer).FullName, name);
                    var control = Create(field.FieldType, name, new(10, 70), new(120, 40), true);
                    Bind(field, renderer, control); Register("instance:" + name, control);
                }
                MapOverlays = UserInterfaceController.MapOverlays;
                GameControls = (DWPanel)Create(typeof(DWPanel), "GameControlButtons", new(1018, 668), new(250, 40), false);
                int index = 0;
                foreach (string name in new[] { "PauseButton", "SpeedSlowerButton", "SpeedFasterButton" })
                {
                    var field = AccessTools.Field(RendererBase, name) ?? throw new MissingFieldException(RendererBase.FullName, name);
                    var button = Create(field.FieldType, name, new(124 + index++ * 40, 0), new(40, 40), false);
                    GameControls.AddControl(button); Bind(field, null, button);
                }
                Register(GameControls.Name, GameControls);
                UserInterfaceController.SelectionPanel.BindGalaxy(galaxy, galaxy.Empires.GetPlayer());
                Place(UserInterfaceController.ScreenWidth, UserInterfaceController.ScreenHeight);
            }
            catch { Dispose(); throw; }
        }
    }

    DWControl Create(Type type, string name, Vector2 position, Vector2 size, bool child)
    {
        var control = (DWControl)RuntimeHelpers.GetUninitializedObject(type); GC.SuppressFinalize(control);
        var controlLock = new ReaderWriterLockSlim(); controlLocks.Add(controlLock);
        AccessTools.Field(typeof(DWControl), "_ControlLock").SetValue(control, controlLock); control.Controls = [];
        control.Name = name; control.Visible = true; control.Opacity = control.TargetOpacity = 1;
        if (control is DWButton button) button.Enabled = true;
        control.Position = position; AccessTools.Field(typeof(DWControl), "_Size").SetValue(control, size);
        if (child) control.AddControl(Create(typeof(DWButton), name + "Child", new(4, 4), new(24, 24), false));
        return control;
    }

    void Bind(FieldInfo field, object? target, DWControl control)
    {
        priorFields.Add((field, target, field.GetValue(target)));
        field.SetValue(target, control); Bindings.Add((field, target, control));
    }

    void Register(string key, DWControl control)
    {
        Roots.Add(control); UserInterfaceController.CustomControls[key] = control;
    }

    internal void Place(int width, int height)
    {
        GameControls.Position = new(width - 262, height - 52);
        MapOverlays.Position = new(width - 262, height - 100);
        AccessTools.Field(typeof(DWControl), "_Size").SetValue(MapOverlays, new Vector2(250, 40));
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        lock (UserInterfaceController.CustomControlLock)
        {
            for (int i = priorFields.Count - 1; i >= 0; i--)
            {
                var (field, target, prior) = priorFields[i]; field.SetValue(target, prior);
            }
            UserInterfaceController.CustomControls.Clear();
            foreach (var item in priorControls) UserInterfaceController.CustomControls.Add(item.Key, item.Value);
        }
        foreach (var controlLock in controlLocks) controlLock.Dispose();
    }
}
