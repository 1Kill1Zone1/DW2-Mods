using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Input;

namespace AdvancedConstructionMod;

internal abstract class NativeWindow
{
    internal static NativeWindow? Active {get;private set;}
    internal readonly DWPanel Panel=new();
    internal Galaxy? Campaign;
    internal DWLabel Status=null!;
    private readonly float scale;
    protected float S(float value)=>value*scale;
    private bool closed;
    protected NativeWindow(string name,string title,int width,int height)
    {
        Active?.Close();Active=this;
        scale=Math.Clamp(UserInterfaceHelper.CalculateScaledValue(100)/100f,1,2);
        Panel.Name=name;Panel.HeaderText=title;Panel.UseOverlay=true;Panel.UseRenderedBorder=true;
        Panel.SetFeatureDefaults();Panel.AddCloseButtonToHeader=false;Panel.PostScene=true;Panel.Layer=15000;Panel.Visible=true;Panel.Opacity=Panel.TargetOpacity=1;
        var background=ColorHelper.GalactopediaPanelBackColor;background.A=255;Panel.BackColor=background;
        Panel.Initialize(FontSize.Normal,FontSize.Normal,UserInterfaceController.SelectionPanel?.TilingBackground!);
        Panel.HeaderHeight=S(30);
        width=(int)MathF.Round(S(width));height=(int)MathF.Round(S(height));
        width=Math.Max(240,Math.Min(width,UserInterfaceController.ScreenWidth-24));
        height=Math.Max(200,Math.Min(height,UserInterfaceController.ScreenHeight-24));
        Panel.SetSizeAndPosition(new(width,height),new((UserInterfaceController.ScreenWidth-width)/2f,(UserInterfaceController.ScreenHeight-height)/2f));
        UserInterfaceController.AddCustomControl(Panel);UserInterfaceController.AddModalLayer(Panel.Layer);
    }
    internal DWButton Button(string name,string text,float x,float y,float width,Action clicked,float height=-1)
    {
        if(height<0)height=S(28);
        var button=new DWButton();DWControl.SetupButton(ref button,name,new(width,height),new(x,y),text,(_,_)=>clicked(),false);
        button.PostScene=true;button.Visible=true;Panel.AddControl(button);return button;
    }
    internal DWLabel Label(string name,string text,float x,float y,float width,float height)
    {
        var label=DWLabel.Generate(text,name,new(x,y),new(width,height),0,ColorHelper.ForeColor,false,FontSize.Normal,Stride.Graphics.TextAlignment.Left,Panel,string.Empty,Side.Right);
        label.Visible=true;return label;
    }
    internal DWTextBox TextBox(string name,float x,float y,float width,string value="")
    {
        var box=new DWTextBox {Name=name,Text=value,PostScene=true};box.Initialize(FontSize.Normal,FontSize.Normal,null!);box.Visible=true;
        box.SetSizeAndPosition(new(width,S(28)),new(x,y));Panel.AddControl(box);return box;
    }
    internal void Message(string? text,bool error=false)
    {if(Status!=null){Status.Text=text??string.Empty;Status.ForeColor=error?Color.OrangeRed:ColorHelper.ForeColor;}}
    internal virtual void Close()
    {
        if(closed)return;closed=true;Panel.Visible=false;
        if(UserInterfaceController.ActiveTextBox?.Ancestors.Contains(Panel)==true)UserInterfaceController.ActiveTextBox=null!;
        if(UserInterfaceController.HoveredControl is DWControl hovered&&hovered.Ancestors.Contains(Panel))UserInterfaceController.HoveredControl=null!;
        UserInterfaceController.RemoveModalLayer(Panel.Layer);UserInterfaceController.RemoveCustomControl(Panel);
        if(ReferenceEquals(Active,this))Active=null;
    }
    internal void Reopen()
    {
        if(ReferenceEquals(Active,this))return;
        Active?.Close();closed=false;Active=this;Panel.Visible=true;
        UserInterfaceController.AddCustomControl(Panel);UserInterfaceController.AddModalLayer(Panel.Layer);
    }
    internal static bool Keyboard(Keys[] keysDown,ref bool __result)
    {
        if(Active==null)return true;
        if(keysDown.Contains(Keys.Escape)){Active.Close();__result=true;return false;}
        return true;
    }
    internal static void Install(Harmony harmony)
    {
        var method=AccessTools.Method(typeof(UserInterfaceController),nameof(UserInterfaceController.AcceptKeyboardInput),
            [typeof(GameSettings),typeof(DateTime),typeof(Keys[]),typeof(bool),typeof(bool),typeof(bool)])??throw new MissingMethodException("Native keyboard route");
        harmony.Patch(method,prefix:new HarmonyMethod(typeof(NativeWindow),nameof(Keyboard)){priority=Priority.First});
        harmony.Patch(AccessTools.Method(typeof(InputSystem),nameof(InputSystem.HandleKeyPress)),prefix:new HarmonyMethod(typeof(NativeWindow),nameof(GameKey)){priority=Priority.First});
    }
    private static bool GameKey(KeyMapping keyMapping)
    {if(Active==null)return true;if(keyMapping.Key==Keys.Escape)Active.Close();return false;}
}
