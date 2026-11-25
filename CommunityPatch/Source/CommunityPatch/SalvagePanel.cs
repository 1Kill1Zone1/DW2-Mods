using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace CommunityPatch;

internal static class SalvagePanel
{
    private sealed class ActionSlot
    {
        internal DWButton Button=null!;
        internal DWPanel? Parent;
        internal Galaxy? Galaxy;
        internal Empire? Empire;
        internal Ship? Ship;
    }
    private static readonly ConditionalWeakTable<DWButton,ActionSlot> Slots=new();
    private static readonly System.Reflection.FieldInfo Renderer=AccessTools.Field(typeof(DWGame),"_Renderer");

    private static readonly ConditionalWeakTable<GraphicsDevice,Texture> ModuleIcons = new();

    private static Texture LoadModuleIcon(GraphicsDevice device)
    {
        using var stream = typeof(SalvagePanel).Assembly.GetManifestResourceStream("CommunityPatch.SalvageModule.png")!;
        return Texture.Load(device, stream);
    }

    internal static void Install(Harmony h)=>h.Patch(
        AccessTools.Method(typeof(UserInterfaceController),nameof(UserInterfaceController.SetActionButtonsFromTasks)),
        postfix:new HarmonyMethod(typeof(SalvagePanel),nameof(Bind)){priority=Priority.Last});

    internal static int FindSlot(GameTask[] commands,GameTaskList[] menus,int count)
    {
        count=Math.Min(count,Math.Min(commands.Length,menus.Length));
        int scrap=Array.FindIndex(commands,0,count,t=>t?.Type==GameTaskType.ScrapShip);
        int start=scrap>=0?scrap+1:4;
        for(int n=0;n<count;n++)
        {
            int i=(n+start)%count;
            if(commands[i]==null && !(menus[i]?.Count>0))return i;
        }
        return -1;
    }

    private static void Bind(Galaxy galaxy,Empire playerEmpire,object selected,GameTask[] tasks,GameTaskList[] taskLists,DWButton[] buttons)
    {
        if(buttons==null||buttons.Length==0||buttons[0]==null)return;
        var slot=Slots.GetOrCreateValue(buttons[0]);
        if(slot.Button!=null)slot.Button.Visible=false;
        if(selected is not Ship ship||playerEmpire?.IsPlayer!=true||ship.EmpireId!=playerEmpire.EmpireId||ship.IsDestroyed||
           tasks==null||taskLists==null)return;
        bool active=SalvageOrders.HasOrder(galaxy,ship);
        if(!active && !SalvageOrders.Eligible(playerEmpire,ship) && !(ship.IsBase && SalvageDismantle.CanRecover(galaxy,playerEmpire,ship)))return;
        int i=FindSlot(tasks,taskLists,buttons.Length);
        if(i<0||buttons[i]?.Ancestors.OfType<DWPanel>().FirstOrDefault() is not {} parent)return;
        var basis=buttons[i];
        if(slot.Button==null)
        {
            var button=new DWButton();
            DWControl.SetupButton(ref button,"CommunityPatch.Salvage",basis.Size,basis.Position,"",(_,_)=>Open(slot),false);
            slot.Button=button;
        }
        if(slot.Parent!=parent){slot.Parent?.Controls.Remove(slot.Button);parent.AddControl(slot.Button);slot.Parent=parent;}
        slot.Galaxy=galaxy;slot.Empire=playerEmpire;slot.Ship=ship;
        var b=slot.Button;
        b.SetSizeAndPosition(basis.Size,basis.Position);b.Layer=basis.Layer;b.PostScene=basis.PostScene;b.Opacity=b.TargetOpacity=1;
        var device = OperatingSystem.IsWindowsVersionAtLeast(7) ? Galaxy.Game?.GraphicsDevice : null;
        b.Image = !active && device != null ? ModuleIcons.GetValue(device, LoadModuleIcon) : null!;
        b.ImageFillFactor = 0.9f;
        b.ImageTintColor = b.ImageTintColorHover = Color.White;
        b.Text = active ? "…" : b.Image == null ? "SR" : "";
        b.Enabled=true;b.Visible=true;b.ShowDropDownSelector=true;
        b.HoverTips.Clear();b.AddHoverTip("Salvage and Refit",active?"Manage this salvage order.":"Study unfamiliar equipment and rebuild to a compatible design.",b.CalculateAbsoluteRectangle(),360,Side.Right);
    }

    private static void Open(ActionSlot slot)
    {
        if(slot.Galaxy is not {} g || slot.Empire is not {} e || slot.Ship is not {} ship ||
           Galaxy.Game is not DWGame game || Renderer.GetValue(game) is not ScaledRenderer renderer ||
           !ReferenceEquals(renderer.ResolveSelectedObject(),ship))return;
        var list=new GameTaskList();
        var actions=new Dictionary<GameTask,Action>();
        void Add(string text,string confirm,Action act)
        {
            var marker=new GameTask{Text=text,Enabled=true};
            list.Add(marker);
            actions.Add(marker,()=>Confirm(ship.Name,confirm,()=>{
                if(ReferenceEquals(renderer.ResolveSelectedObject(),ship) && ship.EmpireId==e.EmpireId && !ship.IsDestroyed)act();
            }));
        }
        if(SalvageOrders.HasOrder(g,ship))
        {
            string missing=SalvageDismantle.Missing(g,e,ship);
            Add("Cancel salvage order","Stop work on "+ship.Name+"? Earned research is kept."+
                (missing.Length==0?"":" These bays will remain empty after repair: "+missing),()=>SalvageOrders.Cancel(g,e,ship));
        }
        else
        {
            if(ship.IsBase && SalvageDismantle.CanRecover(g,e,ship))
            {
                var missing=SalvageDismantle.Missing(g,e,ship);
                Add("Repair dismantled bays","Rebuild the stripped bays on "+ship.Name+" with researched equipment?"+
                    (missing.Length==0?"":" No compatible replacement for: "+missing+". Those bays will stay empty."),
                    ()=>SalvageOrders.Start(g,e,ship,SalvageOrders.RecoverBays));
            }
            if(SalvageOrders.Eligible(e,ship))
            foreach(var p in SalvageOrders.Choices(g,e,ship))
            {
                var d=p.Design;
                Add("Salvage and refit to "+d.Name,"Study the unfamiliar parts removed from "+ship.Name+
                    " and rebuild to "+d.Name+" using the normal retrofit cost?",()=>SalvageOrders.Start(g,e,ship,d.DesignId));
            }
            if(ship.IsBase && SalvageOrders.Eligible(e,ship))Add("Dismantle entire station","Dismantle "+ship.Name+" for research? The station will be lost.",()=>SalvageOrders.Start(g,e,ship,-1));
            if(list.Count==0)list.Add(new GameTask{Text="No compatible retrofit design available",Enabled=false});
        }
        renderer.ShowPopupList(slot.Button.CalculateAbsoluteRectangle(),Side.Right,list,null!,
            task=>{if(task!=null && actions.TryGetValue(task,out var action))action();},true,true,null!);
    }

    private static void Confirm(string name,string text,Action act)
    {
        const int layer=15100;
        var panel=new DWPanel{Name="CommunityPatch.SalvageConfirmation",HeaderText=name,PostScene=true,Layer=layer,UseOverlay=true,UseRenderedBorder=true};
        panel.SetFeatureDefaults();panel.AddCloseButtonToHeader=false;
        var bg=ColorHelper.GalactopediaPanelBackColor;bg.A=255;panel.BackColor=bg;
        panel.Initialize(FontSize.Normal,FontSize.Normal,UserInterfaceController.SelectionPanel?.TilingBackground!);
        float w=Math.Min(UserInterfaceController.ScreenWidth-20,UserInterfaceHelper.CalculateScaledValue(540));
        float h=Math.Min(UserInterfaceController.ScreenHeight-20,UserInterfaceHelper.CalculateScaledValue(210));
        panel.HeaderHeight=32;panel.SetSizeAndPosition(new(w,h),new((UserInterfaceController.ScreenWidth-w)/2,(UserInterfaceController.ScreenHeight-h)/2));
        panel.Visible=true;panel.Opacity=panel.TargetOpacity=1;
        var label=DWLabel.Generate(text,"Salvage.Description",new(16,43),new(w-32,h-103),0,ColorHelper.ForeColor,false,FontSize.Normal,Stride.Graphics.TextAlignment.Left,panel,"",Side.Right);
        label.Visible=true;
        bool closed=false;
        void Button(string title,float x,bool accept)
        {
            var b=new DWButton();
            DWControl.SetupButton(ref b,"Salvage."+title,new(112,30),new(x,h-45),title,(_,_)=>{
                if(closed)return;
                closed=true;panel.Visible=false;UserInterfaceController.RemoveModalLayer(layer);UserInterfaceController.RemoveCustomControl(panel);
                if(accept)act();
            },false);
            b.Visible=true;b.PostScene=true;panel.AddControl(b);
        }
        Button("Confirm",w-247,true);Button("Cancel",w-125,false);
        UserInterfaceController.AddCustomControl(panel);UserInterfaceController.AddModalLayer(layer);
    }
}
