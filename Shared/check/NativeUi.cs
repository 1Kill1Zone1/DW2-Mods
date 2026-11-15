using System.Reflection;
using System.Runtime.CompilerServices;
using DistantWorlds2;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Input;

namespace UiChecks;

internal sealed class NativeUi : IDisposable
{
    readonly Game priorHost = Galaxy.Game;
    readonly int priorWidth = UserInterfaceController.ScreenWidth, priorHeight = UserInterfaceController.ScreenHeight;
    internal readonly DWGame Host;
    internal readonly ScaledRenderer Renderer;
    internal readonly InputManager Input = new();
    internal readonly TestKeyboard Keyboard = new();
    readonly List<InputEvent> events = [];
    internal NativeUi(Galaxy galaxy)
    {
        Host = (DWGame)RuntimeHelpers.GetUninitializedObject(typeof(DWGame)); GC.SuppressFinalize(Host);
        Renderer = (ScaledRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ScaledRenderer)); GC.SuppressFinalize(Renderer);
        AccessTools.Field(typeof(DWGame), "_Galaxy").SetValue(Host, galaxy);
        AccessTools.Field(typeof(DWGame), "<IsActive>k__BackingField").SetValue(Host, true);
        AccessTools.Field(typeof(DWGame), "_Renderer").SetValue(Host, Renderer);
        AccessTools.Field(typeof(ScaledRenderer), "_SceneRenderType").SetValue(Renderer, SceneRenderType.Galaxy);
        AccessTools.Property(typeof(Game), "Input").SetValue(Host, Input);
        typeof(InputManager).GetProperty(nameof(InputManager.Keyboard))!.SetValue(Input, Keyboard);
        Galaxy.Game = Host; UserInterfaceController.ScreenWidth = 1280; UserInterfaceController.ScreenHeight = 720;
    }
    internal void Keys(params Keys[] keys)
    {
        foreach (var key in new[] { Stride.Input.Keys.V, Stride.Input.Keys.B, Stride.Input.Keys.LeftCtrl, Stride.Input.Keys.LeftAlt, Stride.Input.Keys.Escape }) Keyboard.HandleKeyUp(key);
        Keyboard.Update(events);
        foreach (var key in keys) Keyboard.HandleKeyDown(key);
        Keyboard.Update(events);
    }
    internal static Point Center(DWControl control) { var r = control.CalculateAbsoluteRectangle(); return new((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); }
    internal static void Click(DWControl control, bool left = true)
    {
        UserInterfaceController.HoveredControl = control;
        var point = Center(control);
        UserInterfaceController.DoMouseDown(new GameSettings(), point, left, null!, false, false);
        UserInterfaceController.DoMouseUp(new GameSettings(), point, left, null!, false, false, .1f);
    }
    internal static void EachBlocker(Assembly module, Action<FieldInfo> blocked, Action restored)
    {
        var gate = module.GetType("LocalModUi.MapUiGate", true)!;
        AccessTools.Method(gate, "Unobstructed").Invoke(null, [null, null]);
        var fields = (FieldInfo[])AccessTools.Field(gate, "Blockers").GetValue(null)!;
        var extras = new[] { "ResearchScreen", "MessageDialog", "PopupList" }.Select(n => AccessTools.Field(typeof(UserInterfaceController), n));
        foreach (var field in fields.Concat(extras).Distinct())
        {
            var prior = field.GetValue(null); var control = (DWControl)RuntimeHelpers.GetUninitializedObject(field.FieldType); GC.SuppressFinalize(control);
            try
            {
                field.SetValue(null, control); control.Visible = true;
                AccessTools.Field(typeof(DWControl), "_Size").SetValue(control, new Vector2(480,320)); control.Opacity = control.TargetOpacity = 1;
                if (control is ResearchTree) AccessTools.Field(typeof(ResearchTree), "_Minimized").SetValue(control, false);
                blocked(field); control.Visible = false; restored();
            }
            finally { field.SetValue(null, prior); }
        }
    }
    public void Dispose()
    {
        Galaxy.Game = priorHost;
        UserInterfaceController.ScreenWidth = priorWidth; UserInterfaceController.ScreenHeight = priorHeight;
        UserInterfaceController.HoveredControl = null!;
    }
}
internal sealed class TestKeyboard : KeyboardDeviceBase
{
    public override string Name => nameof(TestKeyboard);
    public override Guid Id => Guid.Empty;
    public override IInputSource Source => null!;
}
