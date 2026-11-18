using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace AdvancedConstructionMod;

internal static class SelectedRetrofitIntegration
{
    private sealed class Choice(Galaxy galaxy,Empire empire,object selected)
    {internal readonly Galaxy Galaxy=galaxy;internal readonly Empire Empire=empire;internal readonly object Selected=selected;}
    private sealed class Split
    {internal DWButton Arrow=null!;internal DWPanel Parent=null!;internal Vector2 OriginalSize;internal Choice? Choice;}
    private static readonly ConditionalWeakTable<DWButton,Split> Splits=new();
    private static readonly ConditionalWeakTable<GameTask,Choice> Menus=new();
    internal static void Install(Harmony harmony)
    {
        var set=AccessTools.GetDeclaredMethods(typeof(DWButton)).Single(m=>m.Name=="SetButtonForGameTask"&&m.GetParameters().Any(p=>p.ParameterType==typeof(GameTaskList)));
        harmony.Patch(set,prefix:new HarmonyMethod(typeof(SelectedRetrofitIntegration),nameof(BeforeButton)),postfix:new HarmonyMethod(typeof(SelectedRetrofitIntegration),nameof(AfterButton)));
        harmony.Patch(AccessTools.Method(typeof(ScaledRenderer),"ShowPopupList"),prefix:new HarmonyMethod(typeof(SelectedRetrofitIntegration),nameof(BeforePopup)));
        harmony.Patch(AccessTools.Method(typeof(PopupList),"DoMouseUp"),prefix:new HarmonyMethod(typeof(SelectedRetrofitIntegration),nameof(PopupClick)){priority=Priority.First});
        // A marker is never a native fleet task, even if another UI route attempts to execute it.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Empire),"ExecuteGameTask",[typeof(GameTask),typeof(bool),typeof(bool)]),prefix:new HarmonyMethod(typeof(SelectedRetrofitIntegration),nameof(BeforeExecute)){priority=Priority.First});
    }
    internal static (Ship[] Ships,bool Preselect) Selection(object? selected)=>selected switch
    {
        Fleet fleet=>(fleet.Ships.ToArray(),false),
        ShipList ships=>(ships.ToArray(),true),
        Ship ship=>([ship],true),
        _=>([],false)
    };
    private static bool CanOpen(Galaxy galaxy,Empire empire,object? selected)
    {
        var ships=Selection(selected).Ships;
        return empire.IsPlayer&&ships.Length>0&&ships.Any(s=>s!=null&&s.EmpireId==empire.EmpireId&&!s.IsBase&&!s.IsDestroyed)&&
            (selected is not Fleet fleet||fleet.EmpireId==empire.EmpireId);
    }
    private static void Open(Choice choice)
    {
        if(NativeWindow.Active!=null||!CanOpen(choice.Galaxy,choice.Empire,choice.Selected))return;
        try
        {
            var (ships,preselect)=Selection(choice.Selected);
            _=new SelectedRetrofitWindow(choice.Galaxy,choice.Empire,ships,preselect);
        }
        catch(Exception ex)
        {
            NativeWindow.Active?.Close();
            _=new SeriesChoiceWindow("RETROFIT UNAVAILABLE",[],null,ex.GetBaseException().Message){Campaign=choice.Galaxy};
        }
    }
    private static void BeforeButton(DWButton button)
    {
        if(button!=null&&Splits.TryGetValue(button,out var split))
        {button.SetSizeAndPosition(split.OriginalSize,button.Position);split.Arrow.Visible=false;split.Choice=null;}
    }
    private static void AfterButton(Galaxy galaxy,Empire empire,DWButton button,GameTask task,object selected)
    {
        if(button==null||task==null||!task.Type.ToString().StartsWith("Retrofit",StringComparison.Ordinal)||!CanOpen(galaxy,empire,selected))return;
        var parent=button.Ancestors.OfType<DWPanel>().FirstOrDefault();if(parent==null)return;
        var split=Splits.GetOrCreateValue(button);split.OriginalSize=button.Size;split.Choice=new(galaxy,empire,selected);
        if(split.Arrow==null)
        {
            var arrow=new DWButton();
            DWControl.SetupButton(ref arrow,"ACM.RetrofitDropdown."+button.Name,new(20,button.Size.Y),Vector2.Zero,"",(_,_)=>{if(split.Choice!=null&&split.Arrow?.Visible==true&&split.Arrow.CheckVisible())Open(split.Choice);},false);
            arrow.ShowDropDownSelector=true;arrow.PostScene=button.PostScene;split.Arrow=arrow;
        }
        if(!ReferenceEquals(split.Parent,parent))
        {split.Parent?.Controls.Remove(split.Arrow);parent.AddControl(split.Arrow);split.Parent=parent;}
        float width=Math.Min(UserInterfaceHelper.CalculateScaledValue(22),Math.Max(14,split.OriginalSize.X*.30f));
        button.SetSizeAndPosition(new(split.OriginalSize.X-width-2,split.OriginalSize.Y),button.Position);
        split.Arrow.SetSizeAndPosition(new(width,split.OriginalSize.Y),new(button.Position.X+button.Size.X+2,button.Position.Y));
        split.Arrow.Layer=button.Layer;split.Arrow.Visible=button.Visible;split.Arrow.Enabled=true;split.Arrow.Opacity=split.Arrow.TargetOpacity=1;
        split.Arrow.HoverTips.Clear();split.Arrow.AddHoverTip("Retrofit to design...","Choose individual ships and an exact project. No order is sent until confirmation.",split.Arrow.CalculateAbsoluteRectangle(),420,Side.Right);
    }
    internal static GameTask MenuTask(Galaxy galaxy,Empire empire,object selected)
    {
        var task=new GameTask{Type=(GameTaskType)0,Text="Retrofit to design...",Enabled=true,ManuallyInitiated=true};
        Menus.Add(task,new(galaxy,empire,selected));return task;
    }
    private static void BeforePopup(ScaledRenderer __instance,ref GameTaskList gameTasks,UserInterfaceHelper.TaskSelectedEventHandler eventHandler)
    {
        if(eventHandler?.Method.Name!="OnTaskPopupAssigned"||!ReferenceEquals(eventHandler.Target,__instance))return;
        var galaxy=(Galaxy?)AccessTools.Field(typeof(ScaledRenderer),"_Galaxy").GetValue(__instance);var empire=__instance.PlayerEmpire;
        object selected=__instance.ResolveSelectedObject();
        if(galaxy==null||empire==null||!CanOpen(galaxy,empire,selected))return;
        var copy=new GameTaskList();copy.AddRange(gameTasks);copy.Add(MenuTask(galaxy,empire,selected));gameTasks=copy;
    }
    private static bool PopupClick(PopupList __instance,Point clickPoint,bool isLeftClick,ref bool __result)
    {
        if(!isLeftClick||!__instance.Visible||__instance.ResolveSelectedItem(clickPoint) is not GameTask task||!Menus.TryGetValue(task,out var choice))return true;
        __instance.Visible=false;__result=true;Open(choice);return false;
    }
    private static bool BeforeExecute(GameTask task)=>task==null||!Menus.TryGetValue(task,out _);
}
