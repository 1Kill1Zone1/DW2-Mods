using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace AdvancedConstructionMod;

internal static class DesignerIntegration
{
    private static readonly ConditionalWeakTable<ShipDesignRenderer,EditorState> Editors=new();
    private static readonly ConditionalWeakTable<DWButton,ShipDesignRenderer> Buttons=new();
    internal static void Install(Harmony harmony)
    {
        Patch("SetupUserInterfaceControls",[typeof(bool)],nameof(AfterSetup));
        Patch("BindUpgradeData",Type.EmptyTypes,nameof(AfterBind));
        foreach(var bind in AccessTools.GetDeclaredMethods(typeof(ShipDesignRenderer)).Where(m=>m.Name=="BindData"))
            harmony.Patch(bind,prefix:new HarmonyMethod(typeof(DesignerIntegration),nameof(BeforeBindData)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ScaledRenderer),"CloseShipDesign",Type.EmptyTypes),
            prefix:new HarmonyMethod(typeof(DesignerIntegration),nameof(BeforeCloseEditor)));
        var save=AccessTools.DeclaredMethod(typeof(ShipDesignRenderer),"DoSave",[typeof(bool)])!;
        harmony.Patch(save,finalizer:new HarmonyMethod(typeof(DesignerIntegration),nameof(FinishSave)));
        var choice=AccessTools.DeclaredMethod(typeof(ShipDesignRenderer),"UpgradeTo_SelectedIndexChanged",[typeof(int)])!;
        harmony.Patch(choice,prefix:new HarmonyMethod(typeof(DesignerIntegration),nameof(BeforeForwardChoice)),postfix:new HarmonyMethod(typeof(DesignerIntegration),nameof(AfterForwardChoice)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ShipDesignRenderer),"Update"),postfix:new HarmonyMethod(typeof(DesignerIntegration),nameof(UpdateInputState)));
        NativeWindow.Install(harmony);
        DesignsSeriesColumn.Install(harmony);
        void Patch(string method,Type[] parameters,string handler)
        {
            var target=AccessTools.DeclaredMethod(typeof(ShipDesignRenderer),method,parameters)??throw new MissingMethodException(method);
            harmony.Patch(target,postfix:new HarmonyMethod(typeof(DesignerIntegration),handler));
        }
    }
    private static T? Field<T>(object target,string name) where T:class=>AccessTools.Field(target.GetType(),name)?.GetValue(target) as T;
    internal static void BeforeBindData(ShipDesignRenderer __instance)
    {
        if(!Editors.TryGetValue(__instance,out var state)||state.Saving)return;
        NativeWindow.Active?.Close();ClearInput(state);state.Session=null;state.ClosedDuringSave=false;
    }
    private static void BeforeCloseEditor(ScaledRenderer __instance)
    {
        var editor=Field<ShipDesignRenderer>(__instance,"_RendererShipDesignPostScene");
        if(editor!=null)CloseEditor(editor);
    }
    internal static void CloseEditor(ShipDesignRenderer editor)
    {
        NativeWindow.Active?.Close();
        if(!Editors.TryGetValue(editor,out var state))return;
        // Native DoSave closes the screen before our metadata commit postfix runs.
        if(state.Saving){state.ClosedDuringSave=true;return;}
        ClearInput(state);state.Session=null;
    }
    private static EditorSession? Session(ShipDesignRenderer renderer)
    {
        var state=Editors.GetOrCreateValue(renderer);
        var draft=Field<Design>(renderer,"_Design");var galaxy=Field<Galaxy>(renderer,"_Galaxy");var empire=Field<Empire>(renderer,"_PlayerEmpire");var origin=Field<Design>(renderer,"UpgradingDesign");
        // Component immutability and the component viewer's mode do not forbid metadata edits on an own project.
        if(draft==null||galaxy==null||empire==null||draft.EmpireId!=empire.EmpireId)return null;
        if(origin!=null&&(origin.EmpireId!=empire.EmpireId||!ReferenceEquals(empire.Designs.GetById(origin.DesignId),origin)))origin=null;
        if(state.Session!=null&&(state.Saving||ReferenceEquals(state.Session.Draft,draft)&&ReferenceEquals(state.Session.Origin,origin)))return state.Session;
        NativeWindow.Active?.Close();
        state.Session=new EditorSession(galaxy,empire,draft,origin);return state.Session;
    }
    private static void AfterSetup(ShipDesignRenderer __instance)
    {
        var state=Editors.GetOrCreateValue(__instance);
        var panel=Field<DWPanel>(__instance,"UpgradePanel");if(panel==null)return;
        if(ReferenceEquals(state.Panel,panel)&&state.ClassButton!=null&&panel.Controls.Contains(state.ClassButton)){Refresh(__instance);return;}
        if(state.ClassButton!=null)Buttons.Remove(state.ClassButton);if(state.SourceButton!=null)Buttons.Remove(state.SourceButton);
        state.Panel=panel;
        float pad=Math.Max(6,UserInterfaceHelper.CalculateScaledValue(8)),row=Math.Max(24,UserInterfaceHelper.CalculateScaledValue(28));
        state.NativeLayout=panel.Controls.Select(c=>(c,c.Position,c.Size)).ToArray();state.NativeHeight=panel.Size.Y;state.Pad=pad;state.Row=row;
        state.NativeToCaption=Field<DWLabel>(__instance,"UpgradeToLabel")?.Text??"This Design Upgrades To";
        var from=Field<DWLabel>(__instance,"UpgradeFromLabel");var list=Field<ScrollableList>(__instance,"UpgradeFromList");
        float width=panel.Size.X-2*pad,y=panel.HeaderHeight+pad;
        DWLabel Label(string name,string text,float top,float height)
        {
            var label=DWLabel.Generate(text,name,new(pad,top),new(width,height),0,ColorHelper.ForeColor,false,FontSize.Normal,Stride.Graphics.TextAlignment.Left,panel,string.Empty,Side.Right);
            label.Visible=true;label.PostScene=true;return label;
        }
        float gap=Math.Max(4,UserInterfaceHelper.CalculateScaledValue(4)),labelWidth=UserInterfaceHelper.CalculateScaledValue(52),confirmWidth=UserInterfaceHelper.CalculateScaledValue(32),renameWidth=UserInterfaceHelper.CalculateScaledValue(72);
        var seriesLabel=Label("ACM.SeriesLabel","Series",y,row);seriesLabel.SetSizeAndPosition(new(labelWidth,row),seriesLabel.Position);
        var input=new DWTextBox{Name="ACM.SeriesName",PostScene=true,PromptTextWhenEmpty="Enter series name",EnterDeactivates=true,ClipToRegion=true};
        input.Initialize(FontSize.Normal,FontSize.Normal,null!);input.Visible=true;
        float inputWidth=width-labelWidth-confirmWidth-row-renameWidth-4*gap,actionsX=pad+labelWidth+gap+inputWidth+gap;
        input.SetSizeAndPosition(new(inputWidth,row),new(pad+labelWidth+gap,y));panel.AddControl(input);state.SeriesInput=input;
        input.LostFocusEvent+=(_,_)=>UpdateInputState(__instance);
        var confirm=new DWButton();DWControl.SetupButton(ref confirm,"ACM.ConfirmSeries",new(confirmWidth,row),new(actionsX,y),"OK",(_,_)=>ConfirmSeries(__instance),false);
        confirm.Visible=true;confirm.PostScene=true;panel.AddControl(confirm);state.ConfirmButton=confirm;
        confirm.AddHoverTip("Confirm series","Confirm the entered name. The series is saved with Save / Apply.",confirm.CalculateAbsoluteRectangle(),320,Side.Right);
        var classButton=new DWButton();DWControl.SetupButton(ref classButton,"ACM.ConstructionClass",new(row,row),new(actionsX+confirmWidth+gap,y),"",ClassClick,false);
        classButton.ShowDropDownSelector=true;classButton.PostScene=true;classButton.Visible=true;
        panel.AddControl(classButton);state.ClassButton=classButton;Buttons.Add(classButton,__instance);
        state.Summary=Label("ACM.SeriesSummary","",y+row+gap,row);
        state.Target=Label("ACM.SeriesTarget","",y+row+pad,2*row);
        var rename=new DWButton();DWControl.SetupButton(ref rename,"ACM.RenameSeries",new(renameWidth,row),new(panel.Size.X-pad-renameWidth,y),"Rename...",(_,_)=>Rename(__instance),false);
        rename.Visible=true;rename.PostScene=true;panel.AddControl(rename);state.RenameButton=rename;
        DWButton ActionButton(string name,string text,float left,float top,float buttonWidth,Action action)
        {
            var b=new DWButton();DWControl.SetupButton(ref b,name,new(buttonWidth,row),new(left,top),text,(_,_)=>action(),false);
            b.Visible=true;b.PostScene=true;panel.AddControl(b);return b;
        }
        float firstWidth=width*.62f;
        state.TargetButton=ActionButton("ACM.TargetChoice","Target...",pad,y+row+gap,firstWidth,()=>Targets(__instance));state.TargetButton.ShowDropDownSelector=true;
        state.UseCurrent=ActionButton("ACM.UseCurrentTarget","Use this project",pad+firstWidth+gap,y+row+gap,width-firstWidth-gap,()=>UseCurrentTarget(__instance));
        float half=(width-gap)/2;
        state.MoveProject=ActionButton("ACM.MoveProject","Move this project",pad,y+2*(row+gap),half,()=>Move(__instance,false));
        state.MoveSeries=ActionButton("ACM.MoveSeries","Move all in series",pad+half+gap,y+2*(row+gap),half,()=>Move(__instance,true));
        float sourceWidth=Math.Min(UserInterfaceHelper.CalculateScaledValue(128),width*.48f),sourceY=from?.Position.Y??(y+3*row);
        if(from!=null){from.Text="Upgrades From";from.SetSizeAndPosition(new(Math.Max(30,width-sourceWidth-pad),from.Size.Y),from.Position);}
        var sourceButton=new DWButton();DWControl.SetupButton(ref sourceButton,"ACM.EditSources",new(sourceWidth,Math.Min(row,from?.Size.Y??row)),new(panel.Size.X-pad-sourceWidth,sourceY),"Edit sources...",SourcesClick,false);
        sourceButton.PostScene=true;sourceButton.Visible=true;
        panel.AddControl(sourceButton);state.SourceButton=sourceButton;Buttons.Add(sourceButton,__instance);Refresh(__instance);
    }
    private static void AfterBind(ShipDesignRenderer __instance)=>Refresh(__instance);
    private static void Refresh(ShipDesignRenderer renderer)
    {
        var state=Editors.GetOrCreateValue(renderer);
        try
        {
            var session=Session(renderer);
            if(state.Panel!=null)
                foreach(var control in state.Panel.Controls.Where(c=>c.Name.StartsWith("ACM.",StringComparison.Ordinal)&&c!=state.Summary&&c!=state.Target))control.Visible=session!=null;
            if(session==null)
            {
                if(state.Summary!=null)state.Summary.Visible=false;if(state.Target!=null)state.Target.Visible=false;
                if(Field<DWButton>(renderer,"UpgradeToButton") is { } native)native.Visible=true;
                if(Field<DWLabel>(renderer,"UpgradeToLabel") is { } caption)caption.Text=state.NativeToCaption;
            }
            if(state.SeriesInput!=null)
            {
                state.SeriesInput.Visible=session!=null;
                if(!ReferenceEquals(state.InputSession,session))
                {
                    state.InputSession=session;state.ConfirmedText=session?.ClassId!=null?session.ClassName:string.Empty;
                    state.SeriesInput.Text=session?.ClassId!=null?session.ClassName:SuggestedSeriesName(session?.Draft.Name??string.Empty);state.Error=null;
                }
            }
            if(state.ClassButton!=null)state.ClassButton.Enabled=session!=null;
            if(state.RenameButton!=null)state.RenameButton.Enabled=session?.ClassId!=null;
            if(state.SourceButton!=null){state.SourceButton.Enabled=session!=null&&(!session.Published||!session.Draft.Obsolete);state.SourceButton.Text="Edit sources...";}
            if(state.TargetButton!=null&&session!=null)
            {
                state.TargetButton.Text=DesignsSeriesColumn.Fit("Series target: "+session.TargetChoice,state.TargetButton.FontNormal,state.TargetButton.Size.X-24);
                state.TargetButton.HoverTips.Clear();state.TargetButton.AddHoverTip("Series target",session.TargetChoice,state.TargetButton.CalculateAbsoluteRectangle(),400,Side.Right);
            }
            if(session?.Sources.Count>0&&Field<ScrollableList>(renderer,"UpgradeFromList") is ScrollableList from)
            {
                var selected=session.Sources.Where(c=>c.Selected).Select(c=>session.Empire.Designs.GetById(c.SourceDesignId)).Where(d=>d!=null).Cast<IListDrawable>().ToList();
                from.BindData(selected,from.OnItemClicked,from.OnItemDoubleClicked,from.OnItemHovered,Design.Fields);
            }
            else if(session?.ClassId!=null&&!session.Published&&Field<ScrollableList>(renderer,"UpgradeFromList") is ScrollableList draftSources)
            {
                var projection=session.Projection();
                var selected=projection.Preview.Accepted?session.Empire.Designs.Where(d=>d.DesignId!=session.Draft.DesignId&&session.WillUpgradeHere(d,projection)).Cast<IListDrawable>().ToList():[];
                draftSources.BindData(selected,draftSources.OnItemClicked,draftSources.OnItemDoubleClicked,draftSources.OnItemHovered,Design.Fields);
            }
            var staged=session?.Projection();
            state.ActivationCount=staged?.Preview.ObsoleteValues?.Count??0;
            // Reading an old project must not substitute its edit-validation error for the series destination.
            state.EffectiveText=session?.ClassId==null?"No confirmed series":session.HasSavedIdentity&&!session.Dirty?session.EffectiveTarget(session.Draft):session.EffectiveTarget(session.Draft,staged);
            UpdateInputState(renderer);
            if(Field<DWButton>(renderer,"UpgradeToViewButton") is { } view&&session!=null)
            {
                AccessTools.Method(typeof(ShipDesignRenderer),"ReviewUpgradeToView").Invoke(renderer,null);
                if(session.ClassId!=null&&(session.Dirty||!session.Published))
                {
                    view.Enabled=false;view.HoverTips.Clear();
                    view.AddHoverTip("","Save / Apply the series changes before viewing the destination.",view.CalculateAbsoluteRectangle(),320,Side.Right);
                }
            }
        }
        catch(Exception ex)
        {
            if(state.SourceButton!=null)state.SourceButton.Enabled=false;state.Error=ex.Message;UpdateInputState(renderer);
        }
    }
    private static void UpdateInputState(ShipDesignRenderer __instance)
    {
        if(!Editors.TryGetValue(__instance,out var state)||state.InputSession==null||state.SeriesInput==null)return;
        if(state.LastInput!=state.SeriesInput.Text){state.Error=null;state.LastInput=state.SeriesInput.Text;}
        bool pending=!string.Equals(state.SeriesInput.Text?.Trim(),state.ConfirmedText,StringComparison.Ordinal);
        if(state.ConfirmButton!=null)state.ConfirmButton.Enabled=pending&&!state.Saving;
        string name=state.SeriesInput.Text?.Trim()??string.Empty;
        bool longName=state.SeriesInput.FontNormal.MeasureString(name).X>state.SeriesInput.Size.X-2*state.SeriesInput.MarginScaled;
        string summary=state.Error??(longName?name+ (pending?"\nPress OK to confirm.":state.InputSession.Dirty?"\nUnsaved changes: Save / Apply.":string.Empty):pending?"Press OK to confirm the series name.":state.InputSession.Dirty?"Unsaved changes: Save / Apply.":string.Empty);
        if(state.Error==null&&state.ActivationCount>0)summary+="\n"+state.ActivationCount+" obsolete target(s) become Active after Save / Apply.";
        bool series=state.InputSession.ClassId!=null;
        if(state.TargetButton!=null)state.TargetButton.Visible=series;if(state.UseCurrent!=null)state.UseCurrent.Visible=series;
        if(state.MoveProject!=null)state.MoveProject.Visible=series&&state.InputSession.HasSavedIdentity;if(state.MoveSeries!=null)state.MoveSeries.Visible=series&&state.InputSession.HasSavedIdentity;
        if(state.Target!=null)state.Target.Text=pending?"Confirm the entered series name first":state.EffectiveText;
        if(state.Summary!=null)
        {
            bool layout=state.Summary.Text!=summary||state.SeriesMode!=series||!state.Arranged;
            state.Summary.Text=summary;state.Summary.ForeColor=state.Error==null?ColorHelper.ForeColor:Color.OrangeRed;
            if(layout)
            {
                Arrange(__instance,state,series);
                foreach(var (button,title,text) in new[]{
                    (state.MoveProject,"Move this project","Move only this saved project to another series. Ships built from this project use its new series; other projects stay behind. Equipment and ship orders do not change. Review the plan before Save / Apply."),
                    (state.MoveSeries,"Move all in series","Move every project in this series, including old and obsolete generations, to another series. All ships using those projects follow their new series. Equipment and existing orders do not change; Save / Apply publishes the reviewed plan.")})
                    if(button!=null){button.HoverTips.Clear();button.AddHoverTip(title,text,button.CalculateAbsoluteRectangle(),400,Side.Right);}
            }
        }
    }
    private static void Arrange(ShipDesignRenderer renderer,EditorState state,bool series)
    {
        var panel=state.Panel!;float pad=state.Pad,row=state.Row,gap=Math.Max(4,UserInterfaceHelper.CalculateScaledValue(4));
        var summary=state.Summary!;summary.Visible=summary.Text.Length>0;
        float height=summary.Visible?Math.Max(row,DrawingHelper.MeasureStringDropShadowWordWrapWithSize(summary.Text,summary.FontNormal,summary.Size.X-2*summary.MarginScaled,out _).Y+2*summary.MarginScaled):0;
        summary.SetSizeAndPosition(new(summary.Size.X,height),summary.Position);
        bool moves=series&&state.InputSession!.HasSavedIdentity;
        state.TargetButton!.Visible=state.UseCurrent!.Visible=series;state.MoveProject!.Visible=state.MoveSeries!.Visible=moves;
        int rows=1+(series?1:0)+(moves?1:0);
        summary.SetSizeAndPosition(summary.Size,new(pad,panel.HeaderHeight+pad+rows*(row+gap)));
        float delta=rows*(row+gap)+pad+(summary.Visible?height+gap:0);
        foreach(var (control,position,size) in state.NativeLayout)control.SetSizeAndPosition(size,position+new Vector2(0,delta));
        float available=Math.Max(160,UserInterfaceController.ScreenHeight-panel.Position.Y-pad),newHeight=Math.Min(state.NativeHeight+delta,available);
        float loss=Math.Max(0,state.NativeHeight+delta-newHeight);
        var list=Field<ScrollableList>(renderer,"UpgradeFromList");
        if(list!=null&&loss>0)list.SetSizeAndPosition(new(list.Size.X,Math.Max(30,list.Size.Y-loss)),list.Position);
        panel.SetSizeAndPosition(new(panel.Size.X,newHeight),panel.Position);
        var from=Field<DWLabel>(renderer,"UpgradeFromLabel");
        if(from!=null)
        {
            from.SetSizeAndPosition(new(Math.Max(30,panel.Size.X-3*pad-state.SourceButton!.Size.X),from.Size.Y),from.Position);
            state.SourceButton.SetSizeAndPosition(state.SourceButton.Size,new(panel.Size.X-pad-state.SourceButton.Size.X,from.Position.Y));
        }
        var native=Field<DWButton>(renderer,"UpgradeToButton");var caption=Field<DWLabel>(renderer,"UpgradeToLabel");
        if(caption!=null)caption.Text=series?"Next Ship in Series":state.NativeToCaption;
        if(native!=null)
        {
            native.Visible=!series;
            state.Target!.SetSizeAndPosition(native.Size,native.Position);
        }
        state.Target!.Visible=series;
        var view=Field<DWButton>(renderer,"UpgradeToViewButton");
        if(view!=null&&series)view.Enabled=state.InputSession!.Published&&!state.InputSession.Dirty&&state.InputSession.Draft.ResolveRetrofitDesignId(state.InputSession.Galaxy,state.InputSession.Empire)>=0;
        state.SeriesMode=series;state.Arranged=true;
    }
    private static void Targets(ShipDesignRenderer renderer)
    {
        if(!StageInput(renderer,out _))return;var session=Session(renderer);if(session?.ClassId==null)return;
        var projection=session.Projection();
        _=new SeriesChoiceWindow("TARGET: "+session.ClassName+" / "+session.Draft.GetRole(session.Galaxy),
            SeriesChoiceWindow.Targets(session,projection.Preview.Next??session.Classes,session.ClassId,session.Role,projection.Facts,id=>
            {
                var before=session.CapturePlan();bool useDraft=id==projection.TargetId&&!session.HasSavedIdentity;
                if(!session.SetTarget(id,useDraft,out var error)||!session.Preview(session.Sources).Accepted)
                {error??=session.Preview(session.Sources).Error;session.RestorePlan(before);ShowError(error??"Target is unavailable.");return;}
                Refresh(renderer);
            }));
    }
    private static void UseCurrentTarget(ShipDesignRenderer renderer)
    {
        if(!StageInput(renderer,out _))return;var session=Session(renderer);if(session?.ClassId==null)return;
        var before=session.CapturePlan();
        if(!session.SetTarget(session.Draft.DesignId,!session.HasSavedIdentity,out var error)||!session.Preview(session.Sources).Accepted)
        {error??=session.Preview(session.Sources).Error;session.RestorePlan(before);ShowError(error??"This project is not a valid target.");return;}
        Refresh(renderer);
    }
    private static void Move(ShipDesignRenderer renderer,bool whole)
    {
        if(!StageInput(renderer,out _))return;var session=Session(renderer);if(session?.ClassId==null)return;
        if(!session.HasSavedIdentity){ShowError("Save this new project before transferring saved projects or an entire series.");return;}
        _=new TransferWindow(session,whole,()=>Refresh(renderer));
    }
    private static void ConfirmSeries(ShipDesignRenderer renderer)
    {
        var state=Editors.GetOrCreateValue(renderer);
        UpdateInputState(renderer);
        try
        {
            var session=Session(renderer);if(session==null||!ReferenceEquals(state.InputSession,session)||state.Saving)return;
            if(!session.SelectSeriesName(state.SeriesInput!.Text,out var error)){state.Error=error;UpdateInputState(renderer);return;}
            state.ConfirmedText=session.ClassId==null?string.Empty:session.ClassName;state.SeriesInput.Text=state.ConfirmedText;state.LastInput=state.ConfirmedText;state.Error=null;Refresh(renderer);
        }
        catch(Exception ex){state.Error=ex.Message;UpdateInputState(renderer);}
    }
    private static void ClassClick(object? sender,DWEventArgs args)
    {
        if(sender is not DWButton button||!Buttons.TryGetValue(button,out var renderer))return;
        try
        {
            var session=Session(renderer);if(session==null)return;
            var state=Editors.GetOrCreateValue(renderer);
            var popup=(PopupList?)AccessTools.Field(typeof(ShipDesignRenderer),"PopupList").GetValue(null);
            if(popup==null)throw new InvalidOperationException("Native series dropdown is unavailable.");
            var choices=session.Classes.Classes.OrderBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).ToArray();
            var values=new List<string>{"(no series)"};values.AddRange(choices.Select(p=>p.Value));
            popup.BindData(values,index=>
            {
                if(!ReferenceEquals(state.InputSession,session)||state.Saving||index<0||index>choices.Length)return;
                if(!session.SelectClass(index==0?null:choices[index-1].Key,out var error)){ShowError(error!);return;}
                state.ConfirmedText=session.ClassId==null?string.Empty:session.ClassName;state.SeriesInput!.Text=state.ConfirmedText;state.LastInput=state.ConfirmedText;state.Error=null;Refresh(renderer);
            },UserInterfaceController.ScreenWidth,UserInterfaceController.ScreenHeight,state.SeriesInput!.Size.X+button.Size.X);
            popup.ExtraData=state;state.Popup=popup;popup.PostScene=true;
            popup.SetPositionAndSize(state.SeriesInput.CalculateAbsoluteRectangle(),state.SeriesInput.Size.X+button.Size.X,Side.Bottom,Vector2.Zero);
            popup.Visible=true;
        }
        catch(Exception ex){ShowError(ex.Message);}
    }
    internal static string SuggestedSeriesName(string value)
    {
        string name=value.Trim();
        // Only suffixes are generations; identifiers and numbers elsewhere remain part of the name.
        name=Regex.Replace(name,@"\s+[vV]\s*\d+$",string.Empty).TrimEnd();
        name=Regex.Replace(name,@"\s+(?=[MDCLXVI]+$)M{0,3}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})$",string.Empty).TrimEnd();
        return name.Length is >0 and <=80?name:string.Empty;
    }
    private static bool StageInput(ShipDesignRenderer renderer,out string? error)
    {
        var session=Session(renderer);var state=Editors.GetOrCreateValue(renderer);
        if(session==null){error="Series data is unavailable for this design.";return false;}
        if(state.SeriesInput==null){error=null;return true;}
        if(!string.Equals(state.SeriesInput.Text?.Trim(),state.ConfirmedText,StringComparison.Ordinal))
        {error="Confirm the series name with OK before Save / Apply.";state.Error=error;UpdateInputState(renderer);return false;}
        error=null;return true;
    }
    private static void Rename(ShipDesignRenderer renderer)
    {
        try
        {
            if(!StageInput(renderer,out var error))return;
            var session=Session(renderer);if(session?.ClassId==null)return;
            _=new RenameSeriesWindow(session,()=>
            {
                var state=Editors.GetOrCreateValue(renderer);state.SeriesInput!.Text=session.ClassName;state.ConfirmedText=session.ClassName;state.LastInput=session.ClassName;state.Error=null;Refresh(renderer);
            });
        }
        catch(Exception ex){ShowError(ex.Message);}
    }
    private static void ClearInput(EditorState state)
    {
        if(state.Popup?.ExtraData==state){state.Popup.Visible=false;state.Popup.ExtraData=null!;}
        if(ReferenceEquals(UserInterfaceController.ActiveTextBox,state.SeriesInput))UserInterfaceController.ActiveTextBox=null!;
        state.InputSession=null;
    }
    private static void SourcesClick(object? sender,DWEventArgs args)
    {
        if(sender is not DWButton button||!Buttons.TryGetValue(button,out var renderer))return;
        try
        {
            var session=Session(renderer);if(session==null)return;
            if(!StageInput(renderer,out var error))return;
            if(session.ClassId==null){stateFocus(renderer);return;}
            _=new SourcesWindow(session,()=>Refresh(renderer));
        }
        catch(Exception ex){ShowError(ex.Message);}
        static void stateFocus(ShipDesignRenderer renderer)
        {
            var input=Editors.GetOrCreateValue(renderer).SeriesInput!;
            UserInterfaceController.ActiveTextBox=input;input.IsActive=true;
        }
    }
    internal static bool BeforeSave(ShipDesignRenderer __instance,out SaveContext? __state)
    {
        __state=null;
        try
        {
            var session=Session(__instance);
            if(session==null)return true;
            if(!StageInput(__instance,out var inputError))return false;
            if(!session.Dirty&&(session.Origin==null||session.ClassId==null))return true;
            var registry=ConstructionCampaigns.Get(session.Galaxy,session.Empire);Monitor.Enter(registry.Gate);
            var context=new SaveContext(registry.Gate,session);
            try
            {
                if(!session.Fresh(out var error)){context.Release();ShowError(error!);return false;}
                var changes=session.Sources.ToList();
                if(session.Origin!=null&&session.ClassId!=null&&!changes.Any(c=>c.SourceDesignId==session.Origin.DesignId))changes.Add(new(session.Origin.DesignId,session.Draft.DesignId,true));
                var preview=session.Preview(changes);
                if(!preview.Accepted){context.Release();ShowError(preview.Error!);return false;}
                if(!registry.TryGet(session.Empire,out var current,out error)||!RetrofitAdmission.QueuedChangesAllowed(session.Galaxy,session.Empire,current,preview,out error))
                {context.Release();ShowError(error!);return false;}
                Editors.GetOrCreateValue(__instance).Saving=true;__state=context;return true;
            }
            catch{context.Release();throw;}
        }
        catch(Exception ex){ShowError(ex.Message);return false;}
    }
    internal static void AfterSave(ShipDesignRenderer __instance,SaveContext? __state)
    {
        if(__state==null)return;
        try
        {
            var session=__state.Session;
            if(!session.Published)return; // Native validation kept the draft open; its own message explains the failure.
            if(!session.Commit(session.Draft,session.Capture(),out var error))
            {
                if(session.Origin!=null){session.Origin.UpgradeDesignId=__state.OldArrow;session.Origin.Obsolete=__state.OldObsolete;}
                ShowError("The design was saved, but series/path changes were not applied. Previous source paths were retained. "+error);
            }
            else
            {
                try{DesignsSeriesColumn.RefreshCommittedSeries(session.Galaxy,session.Empire);}
                catch(Exception ex){ShowError("Series and paths were saved, but the Designs list could not refresh. Reopen Designs. "+ex.Message);}
            }
        }
        finally
        {
            var state=Editors.GetOrCreateValue(__instance);state.Saving=false;__state.Release();
            if(state.ClosedDuringSave){ClearInput(state);state.Session=null;state.ClosedDuringSave=false;}
            else Refresh(__instance);
        }
    }
    private static Exception? FinishSave(Exception? __exception,ShipDesignRenderer __instance,SaveContext? __state)
    {
        var state=Editors.GetOrCreateValue(__instance);state.Saving=false;__state?.Release();
        if(state.ClosedDuringSave){ClearInput(state);state.Session=null;state.ClosedDuringSave=false;}
        return __exception;
    }
    private static void BeforeForwardChoice(ShipDesignRenderer __instance,out int __state)=>__state=Field<Design>(__instance,"_Design")?.UpgradeDesignId??int.MinValue;
    private static void AfterForwardChoice(ShipDesignRenderer __instance,int __state)
    {
        var session=Session(__instance);if(session?.ClassId==null||session.Draft.UpgradeDesignId<0)return;
        var target=session.Galaxy.Designs.GetById(session.Draft.UpgradeDesignId);
        if(target!=null&&session.Classes.DesignClasses.TryGetValue(target.DesignId,out var id)&&id==session.ClassId)return;
        session.Draft.UpgradeDesignId=__state;ShowError("That retrofit target belongs to another series or has no series. Choose a target inside "+session.ClassName+".");
        AccessTools.Method(typeof(ShipDesignRenderer),"BindUpgradeData").Invoke(__instance,null);
    }
    private static void ShowError(string text)=>_ = new ErrorWindow(text);
    private sealed class ErrorWindow:NativeWindow
    {
        internal ErrorWindow(string text):base("AdvancedConstructionError","ADVANCED CONSTRUCTION",660,260)
        {Status=Label("ACM.Error",text,S(16),S(48),Panel.Size.X-S(32),Panel.Size.Y-S(108));Button("ACM.ErrorClose","Close",Panel.Size.X-S(136),Panel.Size.Y-S(40),S(120),Close);Message(text,true);}
    }
    internal sealed class SaveContext
    {
        private readonly object gate;private bool held=true;
        internal readonly EditorSession Session;internal readonly int OldArrow;internal readonly bool OldObsolete;
        internal SaveContext(object gate,EditorSession session){this.gate=gate;Session=session;OldArrow=session.Origin?.UpgradeDesignId??int.MinValue;OldObsolete=session.Origin?.Obsolete??false;}
        internal void Release(){if(!held)return;held=false;Monitor.Exit(gate);}
    }
    private sealed class EditorState
    {internal DWPanel? Panel;internal DWButton? ClassButton,SourceButton,RenameButton,ConfirmButton,TargetButton,UseCurrent,MoveProject,MoveSeries;internal DWLabel? Summary,Target;internal string ConfirmedText="",EffectiveText="",LastInput="",NativeToCaption="";internal DWTextBox? SeriesInput;internal PopupList? Popup;internal EditorSession? Session,InputSession;internal bool Saving,ClosedDuringSave,Arranged,SeriesMode;internal int ActivationCount;internal string? Error;internal float Pad,Row,NativeHeight;internal (DWControl Control,Vector2 Position,Vector2 Size)[] NativeLayout=[];}
}
