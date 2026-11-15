using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using System.Runtime.CompilerServices;
using UiChecks;

internal static partial class Checks
{
    private static DesignsDialog? reviewRefreshDialog;
    private static bool rejectReviewRefresh;
    private static int reviewRefreshAttempts;
    private static void ReviewRefreshFailure(DesignsDialog __instance)
    {
        if(rejectReviewRefresh&&ReferenceEquals(reviewRefreshDialog,__instance))
        {reviewRefreshAttempts++;throw new IOException("Injected Designs refresh failure");}
    }
    private static void ReviewRegressionChecks(Galaxy galaxy,Empire empire,Design basis)
    {
        Console.WriteLine("Native checks: Astra origin exclusion, post-save Series sort and Roman names.");
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var original,out _),"review fixture reads committed metadata");
        original=original.Clone();var created=new List<Design>();
        int nextId=galaxy.Designs.Max(d=>d.DesignId)+100;
        Design Add(string name)
        {
            var d=basis.Clone(galaxy);d.DesignId=nextId++;d.Name=name;d.Obsolete=false;d.UpgradeDesignId=int.MinValue;
            galaxy.Designs.Add(d);empire.Designs.Add(d);created.Add(d);return d;
        }
        ShipDesignRenderer Renderer(Design design,Design? origin=null)
        {
            var r=(ShipDesignRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ShipDesignRenderer));GC.SuppressFinalize(r);
            foreach(var pair in new (string,object?)[]{("_Galaxy",galaxy),("_PlayerEmpire",empire),("_Design",design),("UpgradingDesign",origin)})
                AccessTools.Field(typeof(ShipDesignRenderer),pair.Item1).SetValue(r,pair.Item2);
            return r;
        }
        EditorSession Session(ShipDesignRenderer r)=>(EditorSession)AccessTools.Method(typeof(DesignerIntegration),"Session").Invoke(null,[r])!;
        DWButton Button(NativeWindow w,string name)=>(DWButton)w.Panel.Controls.Single(c=>c.Name==name);
        IListDrawable Row(ScrollableList list,Design design)=>list.Items.Single(item=>ReferenceEquals(AccessTools.Field(item.GetType(),"Design").GetValue(item),design));
        string Text(IListDrawable row)=>(string)AccessTools.Property(row.GetType(),"Text").GetValue(row)!;
        DesignsDialog? dialog=null;
        try
        {
            var origin=Add("Regression origin");var draft=origin.Clone(galaxy);draft.DesignId=Constants.DesignDesignId;draft.Name="Regression upgrade";
            var renderer=Renderer(draft,origin);var session=Session(renderer);
            Require(session.SelectSeriesName("Regression origin series",out _),"origin fixture stages a series");
            var window=new SourcesWindow(session,()=>{});var list=(ScrollableList)window.Panel.Controls.Single(c=>c.Name=="ACM.SourceList");
            var row=Row(list,origin);Require(Text(row).StartsWith("[x]"),"Upgrade origin starts selected");
            list.OnItemClicked(row,"",new Vector2(),true,false,null!,null!,[]);
            Require(Text(row).StartsWith("[ ]"),"native source checkbox visibly removes origin");
            NativeUi.Click(Button(window,"ACM.SourceCancel"));
            Require(session.Sources.Count==0&&origin.UpgradeDesignId==ConstructionGraph.Disabled&&!origin.Obsolete,"Cancel discards origin deselection without touching the original design");
            window=new SourcesWindow(session,()=>{});list=(ScrollableList)window.Panel.Controls.Single(c=>c.Name=="ACM.SourceList");row=Row(list,origin);
            Require(Text(row).StartsWith("[x]"),"canceled origin deselection does not carry into the next selector");
            list.OnItemClicked(row,"",new Vector2(),true,false,null!,null!,[]);
            NativeUi.Click(Button(window,"ACM.SourceApply"));
            Require(session.Sources.Any(c=>c.SourceDesignId==origin.DesignId&&!c.Selected),"Use choices retains explicit origin exclusion");
            window=new SourcesWindow(session,()=>{});list=(ScrollableList)window.Panel.Controls.Single(c=>c.Name=="ACM.SourceList");
            Require(Text(Row(list,origin)).StartsWith("[ ]"),"origin remains unchecked after reopening the selector");
            NativeUi.Click(Button(window,"ACM.SourceApply"));
            Require(DesignerIntegration.BeforeSave(renderer,out var originContext),"Save preflight accepts origin exclusion");
            try
            {
                draft.DesignId=nextId++;galaxy.Designs.Add(draft);empire.Designs.Add(draft);created.Add(draft);origin.Obsolete=true;
                DesignerIntegration.CloseEditor(renderer);DesignerIntegration.AfterSave(renderer,originContext);
            }
            finally{originContext?.Release();}
            Require(origin.UpgradeDesignId==ConstructionGraph.Disabled,"Save leaves excluded origin path disabled");
            Require(registry.TryGet(empire,out var savedOrigin,out _)&&savedOrigin.DesignClasses.ContainsKey(draft.DesignId)&&!savedOrigin.DesignClasses.ContainsKey(origin.DesignId),"excluded unclassified origin is not silently adopted into the new series");

            Require(registry.TryRestore(empire,original,out _),"restore metadata before post-save sorting fixture");
            var first=Add("Regression first");var second=Add("Regression second");
            var state=original.Clone();string a=ConstructionGraph.CreateClass(state,"Regression A series"),m=ConstructionGraph.CreateClass(state,"Regression M series");
            state.DesignClasses[first.DesignId]=a;state.DesignClasses[second.DesignId]=m;
            dialog=new DesignsDialog();dialog.BindGalaxy(galaxy,empire);dialog.SetSizeAndPosition(new(1400,700),new(137,89));dialog.Initialize(FontSize.Normal,FontSize.Large,null!);
            var subset=new DesignList();subset.Add(first);subset.Add(second);dialog.SetSourceDesigns(subset);dialog.Visible=true;
            var status=AccessTools.Field(typeof(DesignsDialog),"_FilterStatusIndex");var type=AccessTools.Field(typeof(DesignsDialog),"_FilterTypeIndex");
            var sort=AccessTools.Field(typeof(DesignsDialog),"_SortField");var direction=AccessTools.Field(typeof(DesignsDialog),"_SortDirection");
            status.SetValue(dialog,8);type.SetValue(dialog,0);sort.SetValue(dialog,DesignsSeriesColumn.FieldName);
            var bind=AccessTools.Method(typeof(DesignsDialog),"BindDesignsList");
            foreach(bool descending in new[]{false,true})
            {
                Require(registry.TryRestore(empire,state,out _),"reset pre-rename series names");
                direction.SetValue(dialog,descending);bind.Invoke(dialog,[null]);
                var editRenderer=Renderer(first);var edit=Session(editRenderer);
                Require(edit.NameClass("Regression Z series",a,out _),"rename is staged before native Save");
                Require(DesignerIntegration.BeforeSave(editRenderer,out var context),"rename Save preflight accepts unchanged native project");
                try
                {
                    // Native DoSave shows Designs before closing the editor and running ACM's commit postfix.
                    bind.Invoke(dialog,[null]);dialog.DesignsList.SelectedItems=[first];
                    DesignerIntegration.CloseEditor(editRenderer);DesignerIntegration.AfterSave(editRenderer,context);
                }
                finally{context?.Release();}
                Require(registry.TryGet(empire,out var renamed,out _)&&renamed.Classes[a]=="Regression Z series","Save commits the new series name");
                Require(ReferenceEquals(dialog.DesignsList.Items[0],descending?first:second),"active Series sort refreshes after Save, descending="+descending);
                Require(dialog.DesignsList.SelectedItems.Contains(first)&&dialog.DesignsList.Items.Count==2,"post-save sort preserves selected native project and bound source subset");
                Require((int)status.GetValue(dialog)! == 8&&(int)type.GetValue(dialog)! == 0&&(bool)direction.GetValue(dialog)! == descending&&(string)sort.GetValue(dialog)! == DesignsSeriesColumn.FieldName,"post-save sort preserves filter and direction");
            }
            var failures=new Harmony("acm.check.review-refresh-failure");
            reviewRefreshDialog=dialog;reviewRefreshAttempts=0;
            failures.Patch(bind,prefix:new HarmonyMethod(typeof(Checks),nameof(ReviewRefreshFailure)));
            try
            {
                rejectReviewRefresh=true;dialog.Visible=false;
                DesignsSeriesColumn.RefreshCommittedSeries(galaxy,empire);dialog.Visible=true;
                var foreign=galaxy.Empires.First(e=>e!=empire);
                DesignsSeriesColumn.RefreshCommittedSeries(galaxy,foreign);
                Require(reviewRefreshAttempts==0,"refresh ignores hidden dialogs and a different empire context");
                rejectReviewRefresh=false;
                foreach(bool refuseMetadata in new[]{true,false})
                {
                    var r=Renderer(first);var edit=Session(r);
                    Require(edit.NameClass("Regression B series",a,out _),"stage rename for failure handling");
                    Require(DesignerIntegration.BeforeSave(r,out var context),"failure-handling Save preflight accepts rename");
                    try
                    {
                        bind.Invoke(dialog,[null]);dialog.DesignsList.SelectedItems=[first];
                        if(refuseMetadata)
                        {
                            string prefix="construction."+empire.EmpireId;
                            string head=getSaved(galaxy,Owner,prefix+".head");
                            string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
                            rejectedKey=prefix+"."+bank+".0";
                        }
                        rejectReviewRefresh=true;DesignerIntegration.CloseEditor(r);DesignerIntegration.AfterSave(r,context);
                    }
                    finally{context?.Release();rejectedKey=null;rejectReviewRefresh=false;}
                    Require(registry.TryGet(empire,out var saved,out _),"registry remains usable after a rejected operation");
                    if(refuseMetadata)
                        Require(saved.Classes[a]=="Regression Z series"&&reviewRefreshAttempts==0&&NativeWindow.Active?.Status.Text.Contains("were not applied")==true,"failed metadata commit leaves the old name and skips the list refresh");
                    else
                        Require(saved.Classes[a]=="Regression B series"&&reviewRefreshAttempts==1&&NativeWindow.Active?.Status.Text.Contains("were saved")==true,"UI refresh failure retains committed metadata and reports the display failure");
                    NativeWindow.Active?.Close();
                }
            }
            finally{rejectReviewRefresh=false;reviewRefreshDialog=null;failures.UnpatchAll(failures.Id);}
            foreach(string roman in new[]{"XXI","XL","XCIX","C","CD","CM","M","MMMCMXCIX"})
                Require(DesignerIntegration.SuggestedSeriesName("Xura "+roman+" v21")=="Xura","valid Roman generation suffix removed: "+roman);
            foreach(string token in new[]{"IIII","IIX","IC","VX","MMMM","Mix","213","EX-2"})
                Require(DesignerIntegration.SuggestedSeriesName("Xura "+token+" v21")=="Xura "+token,"noncanonical suffix or identifier retained: "+token);
            Require(DesignerIntegration.SuggestedSeriesName("XXI")=="XXI","a Roman-looking complete name is preserved");
        }
        finally
        {
            if(dialog!=null)dialog.Visible=false;
            NativeWindow.Active?.Close();
            if(!registry.TryRestore(empire,original,out var error))throw new Exception(error);
            foreach(var d in created){galaxy.Designs.Remove(d);empire.Designs.Remove(d);}
        }
    }
}
