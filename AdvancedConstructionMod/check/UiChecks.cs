using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Input;
using System.Reflection;
using System.Runtime.CompilerServices;
using UiChecks;

internal static partial class Checks
{
    private static int uiNextId;
    private static Design? uiPublished;
    private static bool uiSaveClose;
    private static Design? nativeShownDesign;
    private static Design? nativeCopiedDesign;
    private static bool CaptureNativeCopiedDesign(Design design){nativeCopiedDesign=design;return false;}
    private static bool CaptureNativeDesignsDialog(Design selectedDesign){nativeShownDesign=selectedDesign;return false;}
    private static bool SkipNativeEditorClose()=>false;
    private static bool SimulateNativeSave(ShipDesignRenderer __instance,bool closeScreen)
    {
        uiSaveClose=closeScreen;
        var galaxy=(Galaxy)AccessTools.Field(typeof(ShipDesignRenderer),"_Galaxy").GetValue(__instance)!;
        var empire=(Empire)AccessTools.Field(typeof(ShipDesignRenderer),"_PlayerEmpire").GetValue(__instance)!;
        var design=(Design)AccessTools.Field(typeof(ShipDesignRenderer),"_Design").GetValue(__instance)!;
        var origin=(Design?)AccessTools.Field(typeof(ShipDesignRenderer),"UpgradingDesign").GetValue(__instance);
        if(ReferenceEquals(empire.Designs.GetById(design.DesignId),design))
        {uiPublished=design;DesignerIntegration.CloseEditor(__instance);return false;}
        design.DesignId=uiNextId++;galaxy.Designs.Add(design);empire.Designs.Add(design);if(origin!=null)origin.Obsolete=true;
        uiPublished=design;DesignerIntegration.CloseEditor(__instance);return false;
    }
    private static void UiChecks(Galaxy galaxy,Empire empire,Design h1,Design h2)
    {
        using var metrics=new FontMetrics();
        using var ui=new NativeUi(galaxy);
        var popupField=AccessTools.Field(typeof(ShipDesignRenderer),"PopupList");var priorPopup=popupField.GetValue(null);
        var popup=new PopupList();popup.Initialize(FontSize.Normal,FontSize.Normal,null!);popupField.SetValue(null,popup);
        uiNextId=galaxy.Designs.Max(d=>d.DesignId)+100;
        int baselineControls=UserInterfaceController.CustomControls.Count,baselineLayers=UserInterfaceController.ModalOverlayLayers.Count;
        var probe=h2.Clone(galaxy);probe.DesignId=uiNextId++;probe.Name="UI Unclassified Source";probe.UpgradeDesignId=int.MinValue;probe.Obsolete=false;
        galaxy.Designs.Add(probe);empire.Designs.Add(probe);
        ShipDesignRenderer Renderer(Design draft,Design? origin=null,bool componentsEditable=true,bool foreignView=false)
        {
            if(draft.DesignId==Constants.DesignDesignId)draft.StarDate=empire.Designs.Max(d=>d.StarDate)+1;
            var renderer=(ShipDesignRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ShipDesignRenderer));GC.SuppressFinalize(renderer);
            foreach(var pair in new (string,object?)[]{("_Galaxy",galaxy),("_PlayerEmpire",empire),("_Empire",empire),("_Design",draft),("UpgradingDesign",origin),("_DesignIsEditable",componentsEditable),("_ForeignMode",foreignView)})
                AccessTools.Field(typeof(ShipDesignRenderer),pair.Item1).SetValue(renderer,pair.Item2);
            var panel=new DWPanel{Name="ACM.TestNativePanel."+Guid.NewGuid().ToString("N"),HeaderText="Retrofit Path",Visible=true,Opacity=1,TargetOpacity=1};
            panel.Initialize(FontSize.Normal,FontSize.Normal,null!);panel.HeaderHeight=24;panel.SetSizeAndPosition(new(370,280),new(10,110));
            var label=DWLabel.Generate("Upgrades From","TestFrom",new(8,108),new(340,26),0,Color.White,false,FontSize.Normal,Stride.Graphics.TextAlignment.Left,panel,"",Side.Right);
            var from=new ScrollableList{Name="TestNativeFrom"};from.Initialize(FontSize.Normal,FontSize.Normal,null!);from.SetSizeAndPosition(new(340,100),new(8,140));panel.AddControl(from);
            AccessTools.Field(typeof(ShipDesignRenderer),"UpgradePanel").SetValue(renderer,panel);
            AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeFromLabel").SetValue(renderer,label);
            AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeFromList").SetValue(renderer,from);
            var toLabel=DWLabel.Generate("This Design Upgrades To","TestTo",new(8,28),new(340,26),0,Color.White,false,FontSize.Normal,Stride.Graphics.TextAlignment.Left,panel,"",Side.Right);
            AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeToLabel").SetValue(renderer,toLabel);
            var nativeTarget=new DWButton{Text="Native upgrade setting",Name="TestNativeTarget",Visible=true};nativeTarget.SetSizeAndPosition(new(270,44),new(8,55));panel.AddControl(nativeTarget);
            AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeToButton").SetValue(renderer,nativeTarget);
            var view=new DWButton{Text="View",Name="TestNativeView",Visible=true};view.SetSizeAndPosition(new(60,44),new(286,55));panel.AddControl(view);
            AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeToViewButton").SetValue(renderer,view);
            view.Initialize(FontSize.Normal,FontSize.Normal,null!);
            UserInterfaceController.AddCustomControl(panel);
            AccessTools.Method(typeof(DesignerIntegration),"AfterSetup").Invoke(null,[renderer]);
            return renderer;
        }
        DWPanel Panel(ShipDesignRenderer r)=>(DWPanel)AccessTools.Field(typeof(ShipDesignRenderer),"UpgradePanel").GetValue(r)!;
        DWButton Button(DWPanel p,string name)=>(DWButton)p.Controls.Single(c=>c.Name==name);
        EditorSession Session(ShipDesignRenderer r)=>(EditorSession)AccessTools.Method(typeof(DesignerIntegration),"Session").Invoke(null,[r])!;
        void ClickAt(DWControl control,Point? position=null)
        {
            var root=control.Ancestors.OfType<DWPanel>().Last();var point=position??NativeUi.Center(control);
            var hit=root.CheckHoveredControl(null!,point);
            Require(ReferenceEquals(hit,control),"native coordinate click reaches "+control.Name+"; actual="+hit?.Name);
            UserInterfaceController.HoveredControl=hit!;
            UserInterfaceController.DoMouseDown(new GameSettings(),point,true,empire,false,false);
            UserInterfaceController.DoMouseUp(new GameSettings(),point,true,empire,false,false,.1f);
        }
        void ConfirmName(ShipDesignRenderer r,string name)
        {
            ((DWTextBox)Panel(r).Controls.Single(c=>c.Name=="ACM.SeriesName")).Text=name;
            AccessTools.Method(typeof(DesignerIntegration),"UpdateInputState").Invoke(null,[r]);
            var confirm=Button(Panel(r),"ACM.ConfirmSeries");
            if(confirm.Enabled)ClickAt(confirm);else NativeUi.Click(confirm);
        }
        void ClickSave(ShipDesignRenderer renderer)
        {
            var root=new DWPanel{Name="TestSaveRoot",Visible=true,Opacity=1,TargetOpacity=1};root.SetSizeAndPosition(new(200,60),new(500,20));
            var button=new DWButton();DWControl.SetupButton(ref button,"Save",new(180,28),new(10,10),"Save and Exit",
                AccessTools.Method(typeof(ShipDesignRenderer),"SaveClick").CreateDelegate<EventHandler<DWEventArgs>>(renderer),false);
            button.Visible=true;root.AddControl(button);ClickAt(button);
        }
        int originalWidth=UserInterfaceController.ScreenWidth,originalHeight=UserInterfaceController.ScreenHeight;
        try
        {
            foreach(var screen in new[]{(1280,720),(1920,1080),(2560,1440)})
            {
                UserInterfaceController.ScreenWidth=screen.Item1;UserInterfaceController.ScreenHeight=screen.Item2;
                var longDraft=h2.Clone(galaxy);longDraft.DesignId=Constants.DesignDesignId;
                var longRenderer=Renderer(longDraft);var p=Panel(longRenderer);
                string longName=string.Join(" ",Enumerable.Repeat("LongSeries",8))[..80];
                ConfirmName(longRenderer,longName);
                var summary=(DWLabel)p.Controls.Single(c=>c.Name=="ACM.SeriesSummary");
                var measured=DrawingHelper.MeasureStringDropShadowWordWrapWithSize(summary.Text,summary.FontNormal,summary.Size.X-2*summary.MarginScaled,out _);
                Require(summary.Text.Contains(longName)&&measured.Y<=summary.Size.Y-2*summary.MarginScaled,"full 80-character Series fits its native wrapped summary at "+screen);
                Require(p.Position.Y+p.Size.Y<=screen.Item2&&p.Controls.All(c=>c.Position.Y+c.Size.Y<=p.Size.Y+1),"expanded editor controls stay within the screen and panel at "+screen);
                ClickAt(Button(p,"ACM.EditSources"));var sources=NativeWindow.Active!;
                Require(sources.Panel.Position.X>=0&&sources.Panel.Position.Y>=0&&sources.Panel.Position.Y+sources.Panel.Size.Y<=screen.Item2&&sources.Panel.Controls.All(c=>c.Position.Y>=0&&c.Position.Y+c.Size.Y<=sources.Panel.Size.Y+1),"source table and actions remain within the bounded modal at "+screen);
                NativeWindow.Active?.Close();DesignerIntegration.CloseEditor(longRenderer);UserInterfaceController.RemoveCustomControl(p);
            }
        }
        finally{UserInterfaceController.ScreenWidth=originalWidth;UserInterfaceController.ScreenHeight=originalHeight;}
        var renderer=Renderer(h2,componentsEditable:false,foreignView:true);var nativePanel=Panel(renderer);
        var classButton=Button(nativePanel,"ACM.ConstructionClass");var sourcesButton=Button(nativePanel,"ACM.EditSources");
        var input=(DWTextBox)nativePanel.Controls.Single(c=>c.Name=="ACM.SeriesName");
        foreach(var tip in Button(nativePanel,"ACM.ConfirmSeries").HoverTips)CheckTooltip(tip,"editor confirmation tooltip");
        var confirmButton=Button(nativePanel,"ACM.ConfirmSeries");
        Require(input.Position.Y==confirmButton.Position.Y&&classButton.Position.Y==input.Position.Y&&input.Position.X+input.Size.X<confirmButton.Position.X&&confirmButton.Text=="OK","Series input and visible confirmation/dropdown share one non-overlapping row");
        Require(!((DWLabel)nativePanel.Controls.Single(c=>c.Name=="ACM.SeriesSummary")).Visible&&nativePanel.Size.Y<430,"short confirmed Series does not reserve blank summary rows");
        Require(((DWLabel)AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeToLabel").GetValue(renderer)!).Text=="Next Ship in Series"&&((DWLabel)nativePanel.Controls.Single(c=>c.Name=="ACM.SeriesTarget")).Visible,"assigned Series displays Next Ship in Series and its effective target");
        var dropRect=classButton.CalculateAbsoluteRectangle();
        foreach(var point in new[]{new Point((int)dropRect.Left+2,(int)dropRect.Top+2),new Point((int)dropRect.Right-2,(int)dropRect.Bottom-2)})
            Require(ReferenceEquals(nativePanel.CheckHoveredControl(null!,point),classButton),"native hit test reaches dropdown button edges");
        Require(classButton.Visible&&classButton.CheckVisible()&&sourcesButton.Visible&&sourcesButton.CheckVisible(),"real native visibility predicate admits both retrofit controls");
        Require(classButton.Enabled&&sourcesButton.Enabled,"own in-use design permits class/path edits while components remain locked");
        Require(classButton.Position.Y>=nativePanel.HeaderHeight&&classButton.Position.Y+classButton.Size.Y<=nativePanel.Size.Y,"native class control is inside panel below header");
        int controls=nativePanel.Controls.Count;
        AccessTools.Method(typeof(DesignerIntegration),"AfterSetup").Invoke(null,[renderer]);
        Require(nativePanel.Controls.Count==controls,"repeated editor setup does not duplicate controls");
        int openBaseline=UserInterfaceController.CustomControls.Count;
        string before=Serialized(Read(Storage(galaxy,empire.EmpireId))!);
        for(int i=0;i<3;i++)
        {
            ClickAt(Button(nativePanel,"ACM.RenameSeries"));Require(NativeWindow.Active is RenameSeriesWindow,"explicit rename action opens rename-only modal");
            Require(NativeWindow.Active!.Panel.Controls.All(c=>c.Visible),"rename modal input and actions are visible");
            ClickAt(Button(NativeWindow.Active!.Panel,"ACM.RenameCancel"));
            Require(NativeWindow.Active==null&&UserInterfaceController.CustomControls.Count==openBaseline&&UserInterfaceController.ModalOverlayLayers.Count==baselineLayers,"cancel removes native modal and control");
        }
        Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"class picker cancellation does not write metadata");
        ClickAt(Button(nativePanel,"ACM.RenameSeries"));
        Require(UserInterfaceController.AcceptKeyboardInput(new GameSettings(),galaxy.Time,[Keys.Escape],false,false,false)&&NativeWindow.Active==null,"actual native Escape route closes only mod modal");
        ClickAt(sourcesButton);Require(NativeWindow.Active is SourcesWindow,"native button opens source selector");
        var window=NativeWindow.Active!;var list=(ScrollableList)window.Panel.Controls.Single(c=>c.Name=="ACM.SourceList");
        Require(window.Panel.Controls.All(c=>c.Visible)&&list.CheckVisible(),"source search, native scrolling list, preview and actions are visible");
        Require(window.Panel.Controls.Count(c=>c.Name.StartsWith("ACM.SourceHeader."))==7,"source selector separates current and proposed targets into seven column headings");
        var details=(DWLabel)window.Panel.Controls.Single(c=>c.Name=="ACM.SourceDetails");
        Require(details.Position.Y>=list.Position.Y+list.Size.Y,"source details do not overlap the scrolling table");
        var item=list.Items.Single(row=>ReferenceEquals(AccessTools.Field(row.GetType(),"Design").GetValue(row),probe));
        string RowText(IListDrawable row)=>(string)AccessTools.Property(row.GetType(),"Text").GetValue(row)!;
        Require(RowText(item).Contains("[ ]")&&RowText(item).Contains("Series:")&&RowText(item).Contains("Hull:")&&RowText(item).Contains("Built ships:")&&RowText(item).Contains("Current:"),"source row visibly identifies checkbox, series, hull, count and current target");
        list.OnItemClicked(item,"Name",new Vector2(),true,false,null!,null!,[]);
        Require(details.Text.Contains(probe.Name)&&RowText(item).StartsWith("[ ]"),"project-name click opens full details without changing its checkbox");
        ((Delegate)AccessTools.Field(typeof(ScrollableList),"OnItemClicked").GetValue(list)!).DynamicInvoke(item,"",new Vector2(),true,false,null,null,new List<IListDrawable>());
        Require(RowText(item).StartsWith("[x]"),"native row callback toggles visible checkbox");
        ((DWTextBox)window.Panel.Controls.Single(c=>c.Name=="ACM.SourceSearch")).Text="no matching project";
        ClickAt(Button(window.Panel,"ACM.SourceSearchApply"));Require(list.Items.Count==0,"search filters presentation");
        Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"source checks and search remain staged before Apply");
        var apply=Button(window.Panel,"ACM.SourceApply");ClickAt(apply);
        Require(NativeWindow.Active==null&&probe.UpgradeDesignId==h2.DesignId,"Apply preserves checked source hidden by search");
        Require(AccessTools.Field(typeof(ShipDesignRenderer),"_DesignIsEditable").GetValue(renderer) is false,"class and path edits do not unlock in-use ship components");
        var applied=Read(Storage(galaxy,empire.EmpireId))!;
        Require(applied.DesignClasses[probe.DesignId]==applied.DesignClasses[h2.DesignId],"source Apply adopts target class in real Matrix");
        long revision=applied.Revision;NativeUi.Click(apply);
        Require(Read(Storage(galaxy,empire.EmpireId))!.Revision==revision,"second click on closed Apply cannot duplicate commit");
        Require(input.Visible&&input.CheckVisible()&&input.Text==Session(renderer).ClassName,"series is an inline visible textbox with current assigned name");
        ClickAt(classButton);
        Require(popup.Visible&&NativeWindow.Active==null&&popup.Items.Contains(input.Text),"native dropdown lists existing series without a separate window");
        var region=popup.ResolveIndexRegion(popup.Items.IndexOf(input.Text));
        popup.DoMouseUp(new((int)(region.X+4),(int)(region.Y+region.Height/2)),true,empire,false,false,.1f);
        Require(!popup.Visible&&input.Text==Session(renderer).ClassName,"real native popup mouse selection updates the inline field");
        Require(Read(Storage(galaxy,empire.EmpireId))!.Revision==revision,"dropdown choice remains local before Save or Apply");
        var cancelSession=Session(renderer);Require(cancelSession.NameClass("Discard this staged series",null,out _),"stage metadata before editor cancellation");
        ClickAt(classButton);DesignerIntegration.CloseEditor(renderer);
        Require(NativeWindow.Active==null&&!Session(renderer).Dirty&&Session(renderer).ClassName!="Discard this staged series","editor close drops uncommitted metadata and modal when reopening the same Design");
        var rebindSession=Session(renderer);Require(rebindSession.NameClass("Discard on rebind",null,out _),"stage metadata before a new native bind");
        DesignerIntegration.BeforeBindData(renderer);Require(!Session(renderer).Dirty,"native rebind starts a fresh session even for the same Design object");
        var save=AccessTools.Method(typeof(ShipDesignRenderer),"DoSave",[typeof(bool)]);
        var fake=new Harmony("acm.check.native-save-boundary");fake.Patch(save,prefix:new HarmonyMethod(typeof(Checks),nameof(SimulateNativeSave)){priority=-100});
        try
        {
            Require(DesignerIntegration.SuggestedSeriesName("Xura V v5")=="Xura"&&DesignerIntegration.SuggestedSeriesName("Little Devourer v4")=="Little Devourer","series suggestion removes trailing version and Roman generation");
            Require(DesignerIntegration.SuggestedSeriesName("EX-2 Scout v4")=="EX-2 Scout"&&DesignerIntegration.SuggestedSeriesName("X Sector 213")=="X Sector 213","series suggestion preserves identifiers and ordinary numbers");
            var pending=h2.Clone(galaxy);pending.DesignId=Constants.DesignDesignId;pending.Name="Pending Series v1";
            var pendingRenderer=Renderer(pending);var pendingPanel=Panel(pendingRenderer);
            var pendingInput=(DWTextBox)pendingPanel.Controls.Single(c=>c.Name=="ACM.SeriesName");
            string beforePending=Serialized(Read(Storage(galaxy,empire.EmpireId))!);
            pendingInput.Text="Unconfirmed new name";pendingInput.LostFocusEvent?.Invoke(pendingInput,new DWEventArgs(null!));
            Require(Session(pendingRenderer).ClassId==null&&!Session(pendingRenderer).Dirty,"losing focus never creates or assigns a typed series");
            uiPublished=null;ClickSave(pendingRenderer);
            Require(uiPublished==null&&pending.DesignId==Constants.DesignDesignId&&((DWLabel)pendingPanel.Controls.Single(c=>c.Name=="ACM.SeriesSummary")).Text.Contains("Confirm"),"Save refuses unconfirmed input without publishing a design");
            ConfirmName(pendingRenderer,"Unconfirmed new name");int pendingClasses=Session(pendingRenderer).Classes.Classes.Count;
            NativeUi.Click(Button(pendingPanel,"ACM.ConfirmSeries"));
            Require(Session(pendingRenderer).Classes.Classes.Count==pendingClasses&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==beforePending,"confirmation stages one series and repeated click cannot create a duplicate");
            Require(((DWButton)AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeToButton").GetValue(pendingRenderer)!).Text=="Native upgrade setting","separate series policy leaves the native upgrade caption intact");
            DesignerIntegration.CloseEditor(pendingRenderer);UserInterfaceController.RemoveCustomControl(pendingPanel);
            var source=h2.Clone(galaxy);source.DesignId=uiNextId++;source.Name="Little Devourer v6";source.Obsolete=false;source.UpgradeDesignId=int.MinValue;
            galaxy.Designs.Add(source);empire.Designs.Add(source);
            var draft=source.Clone(galaxy);draft.DesignId=Constants.DesignDesignId;draft.Name="Little Devourer v7";draft.UpgradeDesignId=-3;
            var draftRenderer=Renderer(draft,source);var draftPanel=Panel(draftRenderer);
            var draftInput=(DWTextBox)draftPanel.Controls.Single(c=>c.Name=="ACM.SeriesName");
            var editSources=Button(draftPanel,"ACM.EditSources");
            Require(draftInput.Text=="Little Devourer"&&editSources.Enabled&&editSources.Visible,"Devourer Upgrade suggests series and enables incoming source selection before first Save");
            ConfirmName(draftRenderer,"Intermediate unsaved series");
            ConfirmName(draftRenderer,"Little Devourer");
            string priorDraft=Serialized(Read(Storage(galaxy,empire.EmpireId))!);int oldArrow=source.UpgradeDesignId;
            ClickAt(editSources);
            Require(NativeWindow.Active is SourcesWindow&&Button(NativeWindow.Active!.Panel,"ACM.SourceApply").Text=="Use choices","draft source selector explains that choices are confirmed in the editor");
            ClickAt(Button(NativeWindow.Active!.Panel,"ACM.SourceApply"));
            Require(source.UpgradeDesignId==oldArrow&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==priorDraft&&!Session(draftRenderer).Published,"source selection does not publish a draft or alter old paths before Save");
            var previewList=(ScrollableList)AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeFromList").GetValue(draftRenderer)!;
            Require(previewList.Items.Contains(source)&&Button(draftPanel,"ACM.EditSources").Text=="Edit sources...","selected incoming project is shown under Upgrades From without changing the action caption");
            uiSaveClose=false;ClickSave(draftRenderer);var afterDraft=Read(Storage(galaxy,empire.EmpireId))!;
            Require(uiSaveClose,"coordinate Save and Exit invokes native SaveClick with closeScreen=true");
            Require(afterDraft.DesignClasses[draft.DesignId]==afterDraft.DesignClasses[source.DesignId]&&source.UpgradeDesignId==draft.DesignId,"single Save publishes Devourer design, inherited origin series and chosen path");
            Require(!afterDraft.Classes.ContainsValue("Intermediate unsaved series"),"Save does not publish a replaced intermediate series name");
            var reopen=Renderer(draft);var reopenPanel=Panel(reopen);
            Require(((DWTextBox)reopenPanel.Controls.Single(c=>c.Name=="ACM.SeriesName")).Text=="Little Devourer"&&((DWLabel)AccessTools.Field(typeof(ShipDesignRenderer),"UpgradeToLabel").GetValue(reopen)!).Text=="Next Ship in Series","saved Upgrade reopens with its committed Series and series destination caption");
            DesignerIntegration.CloseEditor(reopen);UserInterfaceController.RemoveCustomControl(reopenPanel);
            UserInterfaceController.RemoveCustomControl(draftPanel);
            var cancelDraft=h2.Clone(galaxy);cancelDraft.DesignId=Constants.DesignDesignId;cancelDraft.Name="Xura V v5";
            var cancelRenderer=Renderer(cancelDraft);var cancelPanel=Panel(cancelRenderer);
            var cancelInput=(DWTextBox)cancelPanel.Controls.Single(c=>c.Name=="ACM.SeriesName");
            cancelInput.Text="";UserInterfaceController.ActiveTextBox=cancelInput;cancelInput.IsActive=true;
            cancelInput.AcceptKeyPresses([Keys.I],false,false,false);
            Require(cancelInput.Text=="i","inline Series accepts actual native keyboard input");
            UserInterfaceController.ActiveTextBox=null!;cancelInput.IsActive=false;
            ConfirmName(cancelRenderer,"Custom Ion Series");
            Require(Session(cancelRenderer).ClassName=="Custom Ion Series"&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==Serialized(afterDraft),"typing a custom series stages a new name only");
            var localSession=Session(cancelRenderer);int localCount=localSession.Classes.Classes.Count;
            ConfirmName(cancelRenderer," custom ion series ");
            Require(localSession.ClassName=="Custom Ion Series"&&localSession.Classes.Classes.Count==localCount,"case and whitespace variants reuse the existing series without a duplicate");
            ConfirmName(cancelRenderer,"Replacement Series");
            Require(!localSession.Classes.Classes.ContainsValue("Custom Ion Series")&&localSession.ClassName=="Replacement Series","replacing a typed pending name discards its unused intermediate series");
            ConfirmName(cancelRenderer,"");
            Require(localSession.ClassId==null&&!localSession.Classes.Classes.ContainsValue("Replacement Series"),"clearing a typed pending name removes its unused series");
            ConfirmName(cancelRenderer,new string('A',81));
            Require(((DWLabel)cancelPanel.Controls.Single(c=>c.Name=="ACM.SeriesSummary")).Text.Contains("1–80"),"invalid inline series shows a concrete error beside the field");
            ConfirmName(cancelRenderer,"Custom Ion Series");ClickAt(Button(cancelPanel,"ACM.EditSources"));
            ClickAt(Button(NativeWindow.Active!.Panel,"ACM.SourceApply"));
            DesignerIntegration.CloseEditor(cancelRenderer);
            cancelInput.Text=new string('A',81);cancelInput.LostFocusEvent?.Invoke(cancelInput,new DWEventArgs(null!));
            Require(NativeWindow.Active==null,"late LostFocus after editor close cannot reopen a modal or stage data");
            Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==Serialized(afterDraft)&&!Session(cancelRenderer).Dirty,"editor Cancel discards typed series and source choices together");
            UserInterfaceController.RemoveCustomControl(cancelPanel);
            var failedApplyRenderer=Renderer(h2);var failedApplyPanel=Panel(failedApplyRenderer);
            ClickAt(Button(failedApplyPanel,"ACM.EditSources"));var failedApplyWindow=NativeWindow.Active!;
            var failedList=(ScrollableList)failedApplyWindow.Panel.Controls.Single(c=>c.Name=="ACM.SourceList");
            var rejectedRow=failedList.Items.Single(row=>ReferenceEquals(AccessTools.Field(row.GetType(),"Design").GetValue(row),probe));
            failedList.OnItemClicked(rejectedRow,"",new Vector2(),true,false,null!,null!,[]);
            string beforeApply=Serialized(Read(Storage(galaxy,empire.EmpireId))!);int probeArrow=probe.UpgradeDesignId;
            string applyPrefix="construction."+empire.EmpireId;
            string applyHead=getSaved(galaxy,Owner,applyPrefix+".head");
            string applyBank=System.Text.Json.JsonDocument.Parse(applyHead).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
            rejectedKey=applyPrefix+"."+applyBank+".0";
            try{ClickAt(Button(failedApplyWindow.Panel,"ACM.SourceApply"));}finally{rejectedKey=null;}
            Require(ReferenceEquals(NativeWindow.Active,failedApplyWindow)&&Session(failedApplyRenderer).Sources.Count==0&&!Session(failedApplyRenderer).Dirty,"failed published Apply restores the prior pending choices and dirty flag");
            ClickAt(Button(failedApplyWindow.Panel,"ACM.SourceCancel"));save.Invoke(failedApplyRenderer,[false]);
            Require(probe.UpgradeDesignId==probeArrow&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==beforeApply,"failed Apply then Cancel then Save cannot commit the canceled source removal");
            UserInterfaceController.RemoveCustomControl(failedApplyPanel);
            var upgrade=h1.Clone(galaxy);upgrade.DesignId=Constants.DesignDesignId;upgrade.Name="Hura Upgrade UI";upgrade.Obsolete=false;upgrade.UpgradeDesignId=-2;
            var upgradeRenderer=Renderer(upgrade,h1);
            Require(Session(upgradeRenderer).ClassId==applied.DesignClasses[h1.DesignId],"editor Upgrade preselects inherited class");
            upgrade.Name="Hura Mk15";
            Require(Session(upgradeRenderer).ClassId==applied.DesignClasses[h1.DesignId]&&((DWTextBox)Panel(upgradeRenderer).Controls.Single(c=>c.Name=="ACM.SeriesName")).Text==Session(upgradeRenderer).ClassName,"renaming the upgraded design to Mk15 preserves its inherited series and inline value");
            uiPublished=null;save.Invoke(upgradeRenderer,[false]);
            Require(uiPublished==upgrade,"native save boundary fixture published new design");
            var inherited=Read(Storage(galaxy,empire.EmpireId))!;
            Require(inherited.DesignClasses[upgrade.DesignId]==inherited.DesignClasses[h1.DesignId]&&h1.UpgradeDesignId==upgrade.DesignId,"patched DoSave callback automatically links origin and inherits class");
            UserInterfaceController.RemoveCustomControl(Panel(upgradeRenderer));
            var plain=h2.Clone(galaxy);plain.DesignId=uiNextId++;plain.Name="Unclassified Upgrade Origin";plain.Obsolete=false;plain.UpgradeDesignId=int.MinValue;galaxy.Designs.Add(plain);empire.Designs.Add(plain);
            var first=plain.Clone(galaxy);first.DesignId=Constants.DesignDesignId;first.Name="New Class Upgrade UI";first.UpgradeDesignId=-2;
            var firstRenderer=Renderer(first,plain);var firstSession=Session(firstRenderer);
            ConfirmName(firstRenderer,"New Upgrade Branch");
            save.Invoke(firstRenderer,[false]);var newState=Read(Storage(galaxy,empire.EmpireId))!;
            Require(newState.DesignClasses[first.DesignId]==newState.DesignClasses[plain.DesignId]&&plain.UpgradeDesignId==first.DesignId,"unclassified Upgrade automatically classifies both origin and new version");
            string id=newState.DesignClasses[first.DesignId];var rename=Session(firstRenderer);
            Require(rename.NameClass("Renamed Upgrade Branch",id,out _)&&Read(Storage(galaxy,empire.EmpireId))!.Classes[id]=="New Upgrade Branch","rename remains staged before commit");
            Require(rename.Apply(out _)&&Read(Storage(galaxy,empire.EmpireId))!.Classes[id]=="Renamed Upgrade Branch","class-only Rename commits without source edits");
            UserInterfaceController.RemoveCustomControl(Panel(firstRenderer));
            var failedOrigin=plain.Clone(galaxy);failedOrigin.DesignId=uiNextId++;failedOrigin.Name="Failure Origin";failedOrigin.Obsolete=false;failedOrigin.UpgradeDesignId=int.MinValue;galaxy.Designs.Add(failedOrigin);empire.Designs.Add(failedOrigin);
            var failedDraft=failedOrigin.Clone(galaxy);failedDraft.DesignId=Constants.DesignDesignId;failedDraft.Name="Failed Class Commit";failedDraft.UpgradeDesignId=-2;
            var failedRenderer=Renderer(failedDraft,failedOrigin);var failedSession=Session(failedRenderer);
            ConfirmName(failedRenderer,"Failed Commit Class");
            string beforeFailed=Serialized(Read(Storage(galaxy,empire.EmpireId))!);string prefix="construction."+empire.EmpireId;
            string head=getSaved(galaxy,Owner,prefix+".head");string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
            rejectedKey=prefix+"."+bank+".0";
            try{save.Invoke(failedRenderer,[false]);}finally{rejectedKey=null;}
            Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==beforeFailed&&!failedOrigin.Obsolete&&failedOrigin.UpgradeDesignId==int.MinValue,"failed class commit restores origin status/path and preserves entire old class graph");
            Require(NativeWindow.Active?.Status.Text.Contains("were not applied")==true,"native publication followed by metadata refusal gives an explicit partial-save explanation");NativeWindow.Active?.Close();
            UserInterfaceController.RemoveCustomControl(Panel(failedRenderer));

            // Exercise the real DoSave body as well; only renderer presentation needs a GPU replacement.
            fake.Unpatch(save,HarmonyPatchType.Prefix,fake.Id);
            var presentation=new Harmony("acm.check.native-editor-presentation");
            presentation.Patch(AccessTools.Method(typeof(ScaledRenderer),"ShowDesignsDialog",[typeof(Design)]),prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureNativeDesignsDialog)));
            presentation.Patch(AccessTools.Method(typeof(ScaledRenderer),"CloseShipDesign",Type.EmptyTypes),prefix:new HarmonyMethod(typeof(Checks),nameof(SkipNativeEditorClose)));
            try
            {
                var nativeOrigin=h2.Clone(galaxy);nativeOrigin.DesignId=uiNextId++;nativeOrigin.Name="Native save origin";nativeOrigin.Obsolete=false;nativeOrigin.StarDate=galaxy.StarDate-1;nativeOrigin.UpgradeDesignId=int.MinValue;
                galaxy.Designs.Add(nativeOrigin);empire.Designs.Add(nativeOrigin);
                var nativeDraft=nativeOrigin.Clone(galaxy);nativeDraft.DesignId=Constants.DesignDesignId;nativeDraft.Name="Native save upgrade";nativeDraft.UpgradeDesignId=-3;
                var fullRenderer=Renderer(nativeDraft,nativeOrigin);var fullPanel=Panel(fullRenderer);
                AccessTools.Field(typeof(ShipDesignRenderer),"_Renderer").SetValue(fullRenderer,ui.Renderer);
                var previewShip=(Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));GC.SuppressFinalize(previewShip);
                AccessTools.Field(typeof(ShipDesignRenderer),"_Ship").SetValue(fullRenderer,previewShip);
                AccessTools.Field(typeof(ScaledRenderer),"_RendererShipDesignPostScene").SetValue(ui.Renderer,fullRenderer);
                var textBox=(DWTextBox)fullPanel.Controls.Single(c=>c.Name=="ACM.SeriesName");textBox.Text="";
                ClickAt(textBox);textBox.AcceptKeyPresses([Keys.I,Keys.O,Keys.N],false,false,false);
                Require(textBox.Text=="ion","coordinate input focus accepts native keyboard text");
                AccessTools.Method(typeof(DesignerIntegration),"UpdateInputState").Invoke(null,[fullRenderer]);
                var ok=Button(fullPanel,"ACM.ConfirmSeries");var okRect=ok.CalculateAbsoluteRectangle();
                ClickAt(ok,new Point((int)okRect.Left+2,(int)okRect.Top+2));
                Require(Session(fullRenderer).ClassName=="ion","OK edge click confirms the typed series after text focus");
                ClickAt(Button(fullPanel,"ACM.EditSources"));
                Require(NativeWindow.Active is SourcesWindow,"coordinate Edit sources opens from a confirmed draft");
                ClickAt(Button(NativeWindow.Active!.Panel,"ACM.SourceApply"));
                nativeShownDesign=null;ClickSave(fullRenderer);
                var nativeCommitted=Read(Storage(galaxy,empire.EmpireId))!;
                Require(nativeDraft.DesignId>=0&&ReferenceEquals(galaxy.Designs.GetById(nativeDraft.DesignId),nativeDraft)&&ReferenceEquals(empire.Designs.GetById(nativeDraft.DesignId),nativeDraft),"real native SaveClick/DoSave publishes the design into both native lists");
                Require(nativeOrigin.Obsolete&&AccessTools.Field(typeof(ShipDesignRenderer),"UpgradingDesign").GetValue(fullRenderer)==null&&ReferenceEquals(nativeShownDesign,nativeDraft),"real native Save retires its origin, clears Upgrade state and opens the saved design");
                Require(nativeCommitted.DesignClasses[nativeOrigin.DesignId]==nativeCommitted.DesignClasses[nativeDraft.DesignId]&&nativeOrigin.UpgradeDesignId==nativeDraft.DesignId,"real native Save and ACM postfix commit inherited series and incoming path together");
                UserInterfaceController.RemoveCustomControl(fullPanel);

                ShipDesignRenderer NativeEditor(Design d,Design? origin=null)
                {
                    var r=Renderer(d,origin);AccessTools.Field(typeof(ShipDesignRenderer),"_Renderer").SetValue(r,ui.Renderer);
                    var preview=(Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));GC.SuppressFinalize(preview);
                    AccessTools.Field(typeof(ShipDesignRenderer),"_Ship").SetValue(r,preview);
                    AccessTools.Field(typeof(ScaledRenderer),"_RendererShipDesignPostScene").SetValue(ui.Renderer,r);return r;
                }
                string series=nativeCommitted.DesignClasses[nativeDraft.DesignId],targetKey=ConstructionSnapshot.TargetKey(series,(int)nativeDraft.GetRole(galaxy));
                var pinEditor=NativeEditor(nativeDraft);ClickAt(Button(Panel(pinEditor),"ACM.UseCurrentTarget"));
                Require(Read(Storage(galaxy,empire.EmpireId))!.Targets.Count==0&&Session(pinEditor).Dirty,"Use this project stages explicit target without publishing it");
                ClickSave(pinEditor);Require(Read(Storage(galaxy,empire.EmpireId))!.Targets[targetKey]==nativeDraft.DesignId,"native Save of an existing project publishes explicit target");
                UserInterfaceController.RemoveCustomControl(Panel(pinEditor));
                var selectedUpgrade=nativeDraft.Clone(galaxy);selectedUpgrade.DesignId=Constants.DesignDesignId;selectedUpgrade.Name="Native selected target upgrade";selectedUpgrade.Obsolete=false;
                var selectedRenderer=NativeEditor(selectedUpgrade,nativeDraft);
                Require(Session(selectedRenderer).Projection().Preview.Next!.Targets[targetKey]==Session(selectedRenderer).Projection().TargetId,"selected target Upgrade previews advancement before native publication");
                ClickSave(selectedRenderer);
                Require(Read(Storage(galaxy,empire.EmpireId))!.Targets[targetKey]==selectedUpgrade.DesignId&&nativeDraft.Obsolete,"real native Save advances selected target only after new project publication");
                UserInterfaceController.RemoveCustomControl(Panel(selectedRenderer));

                var switchMethod=AccessTools.Method(typeof(ScaledRenderer),"SwitchToShipDesign",[typeof(Design),typeof(Ship),typeof(Empire),typeof(Ship),typeof(bool),typeof(Ship)]);
                presentation.Patch(switchMethod,prefix:new HarmonyMethod(typeof(Checks),nameof(CaptureNativeCopiedDesign)));
                AccessTools.Field(typeof(ScaledRenderer),"_Galaxy").SetValue(ui.Renderer,galaxy);
                AccessTools.Field(typeof(ScaledRenderer),"_PlayerEmpire").SetValue(ui.Renderer,empire);
                nativeCopiedDesign=null;
                AccessTools.Method(typeof(ScaledRenderer),"CopyAsNewDesignClickEvent").Invoke(ui.Renderer,[null,new DWEventArgs(selectedUpgrade)]);
                Require(nativeCopiedDesign!=null&&nativeCopiedDesign.DesignId==Constants.DesignDesignId&&!ReferenceEquals(nativeCopiedDesign,selectedUpgrade),"actual native Copy as new creates independent unsaved clone");
                var copiedRenderer=NativeEditor(nativeCopiedDesign!);
                Require(Session(copiedRenderer).Origin==null&&Session(copiedRenderer).ClassId==null,"native copied design has no inherited series or Upgrade origin");
                ConfirmName(copiedRenderer,"ion");ClickSave(copiedRenderer);
                Require(Read(Storage(galaxy,empire.EmpireId))!.Targets[targetKey]==selectedUpgrade.DesignId,"Copy saved into existing series preserves explicitly selected target");
                UserInterfaceController.RemoveCustomControl(Panel(copiedRenderer));

                var otherUpgrade=nativeOrigin.Clone(galaxy);otherUpgrade.DesignId=Constants.DesignDesignId;otherUpgrade.Name="Native other branch upgrade";otherUpgrade.Obsolete=false;
                var otherRenderer=NativeEditor(otherUpgrade,nativeOrigin);ClickSave(otherRenderer);
                Require(Read(Storage(galaxy,empire.EmpireId))!.Targets[targetKey]==selectedUpgrade.DesignId,"real native Upgrade of another generation does not advance pinned target");
                UserInterfaceController.RemoveCustomControl(Panel(otherRenderer));

                var failedTarget=selectedUpgrade.Clone(galaxy);failedTarget.DesignId=Constants.DesignDesignId;failedTarget.Name="Native failed target upgrade";failedTarget.Obsolete=false;
                var failedTargetRenderer=NativeEditor(failedTarget,selectedUpgrade);string beforeTarget=Serialized(Read(Storage(galaxy,empire.EmpireId))!);
                head=getSaved(galaxy,Owner,prefix+".head");bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
                rejectedKey=prefix+"."+bank+".0";
                try{ClickSave(failedTargetRenderer);}finally{rejectedKey=null;}
                Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==beforeTarget&&!selectedUpgrade.Obsolete,"failed real native target Upgrade retains exact prior series graph and active target");
                Require(ReferenceEquals(galaxy.Designs.GetById(failedTarget.DesignId),failedTarget)&&!Read(Storage(galaxy,empire.EmpireId))!.DesignClasses.ContainsKey(failedTarget.DesignId),"native component publication can remain unclassified after rejected metadata and is never silently promoted");
                Require(NativeWindow.Active?.Status.Text.Contains("were not applied")==true,"real native partial-save boundary is explicitly reported");NativeWindow.Active?.Close();
                UserInterfaceController.RemoveCustomControl(Panel(failedTargetRenderer));
                var obsoleteRenderer=NativeEditor(nativeOrigin);var obsoletePanel=Panel(obsoleteRenderer);
                ClickAt(Button(obsoletePanel,"ACM.TargetChoice"));
                Require(NativeWindow.Active is SeriesChoiceWindow,"target picker remains accessible while the edited project is obsolete");NativeWindow.Active?.Close();
                ClickAt(Button(obsoletePanel,"ACM.UseCurrentTarget"));
                Require(((DWLabel)obsoletePanel.Controls.Single(c=>c.Name=="ACM.SeriesSummary")).Text.Contains("become Active")&&nativeOrigin.Obsolete,"Use this obsolete project visibly previews activation without applying it");
                DesignerIntegration.CloseEditor(obsoleteRenderer);UserInterfaceController.RemoveCustomControl(obsoletePanel);
                Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==beforeTarget&&nativeOrigin.Obsolete,"canceling obsolete-target choice retains old metadata and native status");
            }
            finally{presentation.UnpatchAll(presentation.Id);}
        }
        finally{fake.UnpatchAll(fake.Id);NativeWindow.Active?.Close();}
        UserInterfaceController.RemoveCustomControl(nativePanel);
        popupField.SetValue(null,priorPopup);
        Require(UserInterfaceController.CustomControls.Count==baselineControls&&UserInterfaceController.ModalOverlayLayers.Count==baselineLayers,"all test editor and modal controls cleaned up");
    }
}
