using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;
using UiChecks;

internal static partial class Checks
{
    private static bool SelectedWidth(ref int __result){__result=1280;return false;}
    private static bool SelectedHeight(ref int __result){__result=720;return false;}
    private static void SelectedRetrofitEntryChecks(Galaxy galaxy,Empire empire,Fleet fleet,Ship eligible,NativeUi ui,Action<DWControl> click)
    {
        var root=new DWPanel{Name="Selected.EntryRoot",Visible=true,Opacity=1,TargetOpacity=1};root.Initialize(FontSize.Normal,FontSize.Normal,null!);root.SetSizeAndPosition(new(260,70),new(20,20));
        int ordinaryClicks=0;var button=new DWButton();DWControl.SetupButton(ref button,"Native.Retrofit",new(210,36),new(12,12),"Retrofit",(_,_)=>ordinaryClicks++,false);button.Visible=true;button.Opacity=button.TargetOpacity=1;root.AddControl(button);UserInterfaceController.AddCustomControl(root);
        var task=GameTask.GenerateTaskRetrofitShip(galaxy,empire,eligible,true,true).First(t=>t.Enabled);
        var set=AccessTools.GetDeclaredMethods(typeof(DWButton)).Single(m=>m.Name=="SetButtonForGameTask"&&m.GetParameters().Any(p=>p.ParameterType==typeof(GameTaskList)));
        set.Invoke(null,[galaxy,empire,button,task,fleet,400f,Side.Right,Color.White,"",new GameTaskList()]);
        var arrow=(DWButton)root.Controls.Single(c=>c.Name.StartsWith("ACM.RetrofitDropdown.",StringComparison.Ordinal));
        Require(arrow.Position.X>=button.Position.X+button.Size.X&&arrow.Position.X+arrow.Size.X<=222,"actual native task button leaves a separate nonoverlapping arrow within its original footprint");
        click(arrow);Require(NativeWindow.Active is SelectedRetrofitWindow fleetWindow&&fleetWindow.Selected.Count==0&&fleetWindow.Ships.Length==31,"native arrow coordinate click opens fleet chooser with no checks");
        click(NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitCancel"));
        click(button);Require(ordinaryClicks==1&&NativeWindow.Active==null,"ordinary native button handler remains unchanged and does not open chooser");
        root.Visible=false;UserInterfaceController.CustomControls.Remove(root.Name);
        var oldPopup=UserInterfaceController.PopupList;var oldSelection=UserInterfaceController.SelectionPanel;
        var rendererSelection=AccessTools.Field(typeof(DWRendererBase),"SelectionPanel");var oldRendererSelection=rendererSelection.GetValue(null);
        var presentation=new Harmony("acm.check.selected-layout-only");
        presentation.Patch(AccessTools.PropertyGetter(typeof(ScaledRenderer),"Width"),prefix:new HarmonyMethod(typeof(Checks),nameof(SelectedWidth)));
        presentation.Patch(AccessTools.PropertyGetter(typeof(ScaledRenderer),"Height"),prefix:new HarmonyMethod(typeof(Checks),nameof(SelectedHeight)));
        var bind=AccessTools.GetDeclaredMethods(typeof(PopupList)).Single(m=>m.Name=="BindData"&&m.GetParameters().Length==10&&m.GetParameters()[0].ParameterType==typeof(Galaxy));
        try
        {
            AccessTools.Field(typeof(ScaledRenderer),"_Galaxy").SetValue(ui.Renderer,galaxy);AccessTools.Field(typeof(ScaledRenderer),"_PlayerEmpire").SetValue(ui.Renderer,empire);
            var selection=new SelectionPanel();selection.SubItem=fleet;UserInterfaceController.SelectionPanel=selection;rendererSelection.SetValue(null,selection);
            var popup=new PopupList();popup.Initialize(FontSize.Normal,FontSize.Normal,null!);popup.UpdateFontSizing(true);UserInterfaceController.PopupList=popup;
            var callback=(UserInterfaceHelper.TaskSelectedEventHandler)AccessTools.Method(typeof(ScaledRenderer),"OnTaskPopupAssigned").CreateDelegate(typeof(UserInterfaceHelper.TaskSelectedEventHandler),ui.Renderer);
            AccessTools.Method(typeof(ScaledRenderer),"ShowPopupList").Invoke(ui.Renderer,[new RectangleF(30,100,10,10),Side.Right,new GameTaskList(),null,callback,false,false,null]);
            // ShowPopupList resets the popup to zero size; live Draw sizes it on the next frame.
            // Use the same native text-binding layout here without GPU rendering or fabricated hit regions.
            var displayed=new GameTaskList();displayed.AddRange(popup.Items.OfType<GameTask>().ToArray());
            bind.Invoke(popup,[galaxy,empire,displayed,callback,false,1280,720,0f,false,false]);
            popup.Update(1f,new Point(40,100));
            var marker=popup.Items.OfType<GameTask>().Single(t=>t.Text=="Retrofit to design...");var region=popup.ResolveIndexRegion(popup.Items.IndexOf(marker));
            var point=new Point((int)(region.X+4),(int)(region.Y+region.Height/2));
            Require(ReferenceEquals(popup.ResolveSelectedItem(point),marker),"actual RMB popup coordinate resolves only selected-retrofit entry");
            popup.DoMouseUp(point,true,empire,false,false,.1f);
            Require(!popup.Visible&&NativeWindow.Active is SelectedRetrofitWindow menuWindow&&menuWindow.Ships.Length==31&&menuWindow.Selected.Count==0,"actual native RMB callback path opens fleet selection without dispatch");
            click(NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitCancel"));
            Require(fleetAssignments==0,"dropdown and RMB never call fleet retrofit");
        }
        finally{presentation.UnpatchAll(presentation.Id);UserInterfaceController.PopupList=oldPopup;UserInterfaceController.SelectionPanel=oldSelection;rendererSelection.SetValue(null,oldRendererSelection);NativeWindow.Active?.Close();}
    }
}

// Native task text queries a ruler owned by GPU scenes. Supply its disabled cold instance only.
internal sealed class SelectedRulerFixture:IDisposable
{
    private readonly Harmony hooks=new("acm.check.selected-ruler-presentation");
    private static IDistanceRulerSystem ruler=null!;
    private readonly SpriteSheet priorIcons=UserInterfaceHelper.IconImages;
    internal SelectedRulerFixture()
    {
        var native=(DistanceRulerSystem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DistanceRulerSystem));GC.SuppressFinalize(native);ruler=native;
        hooks.Patch(AccessTools.PropertyGetter(typeof(DWGame),"DistanceRulerSystem"),prefix:new HarmonyMethod(typeof(SelectedRulerFixture),nameof(Get)));
        UserInterfaceHelper.IconImages=new SpriteSheet();
        hooks.Patch(AccessTools.Method(typeof(SpriteSheet),"get_Item",[typeof(string)]),prefix:new HarmonyMethod(typeof(SelectedRulerFixture),nameof(Icon)));
    }
    private static bool Get(ref IDistanceRulerSystem __result){__result=ruler;return false;}
    private static bool Icon(ref Sprite __result){__result=null!;return false;}
    public void Dispose(){hooks.UnpatchAll(hooks.Id);UserInterfaceHelper.IconImages=priorIcons;}
}
