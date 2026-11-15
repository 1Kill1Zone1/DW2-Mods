using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using System.Runtime.CompilerServices;
using UiChecks;

internal static partial class Checks
{
    private static void SeriesNativeChecks(Galaxy galaxy,Empire empire,Design basis)
    {
        Console.WriteLine("Native checks: explicit targets, transfers, migration, rollback and correction UI.");
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var state,out _),"series fixture reads metadata");
        state=state.Clone();string a=ConstructionGraph.CreateClass(state,"Target fixture A"),b=ConstructionGraph.CreateClass(state,"Target fixture B");
        int nextId=galaxy.Designs.Max(d=>d.DesignId)+100;
        Design Add(string name,string series,long date,bool obsolete=false,int path=-2)
        {
            var d=basis.Clone(galaxy);d.DesignId=nextId++;d.Name=name;d.StarDate=date;d.Obsolete=obsolete;d.UpgradeDesignId=path;d.AllowAutoRetrofit=false;
            galaxy.Designs.Add(d);empire.Designs.Add(d);state.DesignClasses[d.DesignId]=series;return d;
        }
        var old=Add("Target old",a,10,true);var latest=Add("Target latest",a,20);var disabled=Add("Target disabled",a,12,false,int.MinValue);
        var dest=Add("Destination head",b,15);var destOld=Add("Destination obsolete head",b,11,true);
        string key=ConstructionSnapshot.TargetKey(a,(int)old.GetRole(galaxy)),destKey=ConstructionSnapshot.TargetKey(b,(int)dest.GetRole(galaxy));
        Require(registry.TryRestore(empire,state,out _),"prepare isolated native target fixture");
        var edit=new EditorSession(galaxy,empire,latest,null);string before=Serialized(state);
        Require(edit.SetTarget(old.DesignId,false,out _)&&edit.Projection().Preview.ObsoleteValues!.ContainsKey(old.DesignId),"old target explicitly previews activation");
        Require(old.Obsolete&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"target preview leaves native obsolete flag and Matrix unchanged");
        Require(edit.SetTarget(dest.DesignId,false,out _)==false,"target from another series refused");
        var ship=empire.Ships.First(s=>!s.IsDestroyed);var savedDesign=ship.GetDesign();
        var system=galaxy.Ships.First(s=>s?.ConstructionSystem!=null).ConstructionSystem;
        var queued=(ConstructionYardWait)RuntimeHelpers.GetUninitializedObject(typeof(ConstructionYardWait));
        queued.ShipUnderConstructionId=ship.ShipId;queued.DesignId=latest.DesignId;queued.ConstructionType=ConstructionType.Retrofit;
        ship.Design=latest;system.WaitQueue.Add(queued);
        try
        {
            using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
            var upgrade=latest.Clone(galaxy);upgrade.DesignId=Constants.DesignDesignId;upgrade.Obsolete=false;
            var renderer=(ShipDesignRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ShipDesignRenderer));GC.SuppressFinalize(renderer);
            foreach(var pair in new (string,object)[]{("_Galaxy",galaxy),("_PlayerEmpire",empire),("_Design",upgrade),("UpgradingDesign",latest)})
                AccessTools.Field(typeof(ShipDesignRenderer),pair.Item1).SetValue(renderer,pair.Item2);
            Require(!DesignerIntegration.BeforeSave(renderer,out var blocked)&&blocked==null&&upgrade.DesignId==Constants.DesignDesignId,"queue guard refuses Upgrade before native design publication");
            Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"pre-Save queue refusal preserves complete old metadata");NativeWindow.Active?.Close();
        }
        finally{system.WaitQueue.Remove(queued);ship.Design=savedDesign;}
        string prefix="construction."+empire.EmpireId,head=getSaved(galaxy,Owner,prefix+".head");
        string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
        rejectedKey=prefix+"."+bank+".0";
        try{Require(!edit.Apply(out _),"Matrix failure refuses target activation atomically");}finally{rejectedKey=null;}
        Require(old.Obsolete&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"failed activation retains exact old target metadata and native flag");
        Require(edit.Apply(out _)&&!old.Obsolete&&latest.ResolveRetrofitDesignId(galaxy,empire)==old.DesignId,"retry publishes old target and activates it without changing native equipment");
        Require(!old.AllowAutoRetrofit&&!latest.AllowAutoRetrofit&&disabled.UpgradeDesignId==int.MinValue,"manual target preserves native auto flags and disabled paths");
        Require(old.ResolveRetrofitDesignId(galaxy,empire)<0,"native resolver never retrofits target to itself");
        savedDesign=ship.GetDesign();ship.Design=latest;
        try
        {
            Require(!ship.ShouldRetrofit(empire,old,false),"native automatic-retrofit permission still blocks old explicit target");
            latest.AllowAutoRetrofit=true;
            Require(ship.ShouldRetrofit(empire,old,false),"actual native manual admission accepts the older selected project");
            var choose=AccessTools.Method(typeof(Ship),"ShouldRetrofit",[typeof(Empire),typeof(bool),typeof(bool),typeof(Design).MakeByRefType(),typeof(ResourceQuantityList).MakeByRefType(),typeof(float).MakeByRefType(),typeof(int)]);
            object?[] args=[empire,true,false,null,new ResourceQuantityList(),0f,-1];
            Require(choose.Invoke(ship,args) is true&&ReferenceEquals(args[3],old)&&args[4] is ResourceQuantityList,"native selection computes ordinary retrofit resources for the older explicit target");
        }
        finally{latest.AllowAutoRetrofit=false;ship.Design=savedDesign;}
        var stale=new EditorSession(galaxy,empire,latest,null);
        Require(edit.SetTarget(null,false,out _)&&edit.Apply(out _),"automatic latest can be restored explicitly");
        Require(stale.SetTarget(old.DesignId,false,out _)&&!stale.Apply(out _),"stale target editor refuses commit");
        Require(latest.ResolveRetrofitDesignId(galaxy,empire)<0&&old.ResolveRetrofitDesignId(galaxy,empire)==latest.DesignId,"automatic target uses dates after clearing pin");
        Require(edit.SetTarget(old.DesignId,false,out _)&&edit.SetTarget(null,false,out _)&&edit.Projection().Preview.ObsoleteValues!.Count==0,"replacing a staged target does not retain its activation request");
        edit=new EditorSession(galaxy,empire,latest,null);
        Require(edit.StageTransfer([latest.DesignId],b,out _)&&edit.Projection().Preview.Accepted,"single transfer previews valid destination");
        Require(edit.Classes.Targets[destKey]==dest.DesignId&&edit.Classes.DesignClasses[old.DesignId]==a,"newer incoming project does not replace destination head or move remaining source");
        Require(edit.Apply(out _)&&latest.ResolveRetrofitDesignId(galaxy,empire)==dest.DesignId,"single transfer publishes pinned destination");
        Require(registry.TryGet(empire,out state,out _),"read committed transfer");
        state=state.Clone();state.Targets[destKey]=ConstructionGraph.Disabled;Require(registry.TryRestore(empire,state,out _),"prepare destination with deliberately no target");
        edit=new EditorSession(galaxy,empire,old,null);
        Require(!edit.StageTransfer([old.DesignId],b,out _),"no-target destination requires explicit correction");
        using(var metrics=new FontMetrics())using(var ui=new NativeUi(galaxy))
        {
            var window=new TransferWindow(edit,false,()=>{});
            AccessTools.Field(typeof(TransferWindow),"destination").SetValue(window,b);
            AccessTools.Method(typeof(TransferWindow),"Bind").Invoke(window,null);
            var choose=(DWButton)window.Panel.Controls.Single(c=>c.Name=="ACM.TransferDestinationTarget.0");
            Require(choose.Visible&&choose.Enabled,"invalid destination keeps correction control accessible");
            NativeUi.Click(choose);Require(NativeWindow.Active is SeriesChoiceWindow,"invalid plan can open destination correction picker");
            var pick=(DWButton)NativeWindow.Active!.Panel.Controls.First(c=>c is DWButton button&&button.Text.StartsWith(destOld.Name));
            NativeUi.Click(pick);Require(ReferenceEquals(NativeWindow.Active,window),"target correction returns to same transfer modal");
            var use=(DWButton)window.Panel.Controls.Single(c=>c.Name=="ACM.TransferUse");
            Require(use.Enabled,"corrected obsolete target has a valid visible plan");NativeUi.Click(use);
            Require(edit.Dirty&&destOld.Obsolete,"Use transfer plan remains staged including old destination activation");
        }
        Require(edit.Apply(out _)&&!destOld.Obsolete&&old.ResolveRetrofitDesignId(galaxy,empire)==destOld.DesignId,"corrected old destination activates only with Apply");
        using(var metrics=new FontMetrics())using(var ui=new NativeUi(galaxy))
        {
            string shortName=old.Name;old.Name=string.Join(" ",Enumerable.Repeat("Long complete project name",60))+" FINAL DETAILS TOKEN";
            try
            {
                var sources=new SourcesWindow(new EditorSession(galaxy,empire,dest,null),()=>{});
                Require(sources.Panel.BackColor.A==255&&sources.Panel.Opacity==1,"Sources native panel is opaque");
                var list=(ScrollableList)sources.Panel.Controls.Single(c=>c.Name=="ACM.SourceList");
                var row=list.Items.Single(item=>ReferenceEquals(AccessTools.Field(item.GetType(),"Design").GetValue(item),old));
                list.OnItemClicked(row,"Name",new(),true,false,null!,null!,[]);
                var details=(DWLabel)sources.Panel.Controls.Single(c=>c.Name=="ACM.SourceDetails");
                var next=(DWButton)sources.Panel.Controls.Single(c=>c.Name=="ACM.SourceDetails.Next");
                var previous=(DWButton)sources.Panel.Controls.Single(c=>c.Name=="ACM.SourceDetails.Previous");
                string first=details.Text,all=first;int pages=0;
                Require(next.Enabled&&!previous.Enabled,"long source details expose enabled forward pagination");
                while(next.Enabled&&pages++<100)
                {
                    Require(ReferenceEquals(sources.Panel.CheckHoveredControl(null!,NativeUi.Center(next)),next),"details page button is reachable through native coordinate hit testing");
                    NativeUi.Click(next);all+="\n"+details.Text;
                    var measured=DrawingHelper.MeasureStringDropShadowWordWrapWithSize(details.Text,details.FontNormal,details.Size.X-2*details.MarginScaled,out _);
                    Require(measured.Y<=details.Size.Y,"each source details page fits its visible native label");
                }
                string readable=string.Join(" ",all.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
                Console.WriteLine("DETAILS-PAGING pages="+(pages+1)+" tail="+readable[Math.Max(0,readable.Length-400)..]);
                Require(readable.Contains(old.Name),"paging exposes the complete long project name across wrapped lines");
                Require(readable.Contains("After Apply:"),"paging reaches the proposed-destination details");
                Require(readable.Contains("After Apply: "+destOld.Name),"paged proposed destination matches the explicit series head");
                while(previous.Enabled)NativeUi.Click(previous);
                Require(details.Text==first,"details can return to the original first page");sources.Close();
            }
            finally{old.Name=shortName;NativeWindow.Active?.Close();}
        }
        var whole=new EditorSession(galaxy,empire,disabled,null);
        Require(whole.StageTransfer(whole.Classes.DesignClasses.Where(p=>p.Value==a).Select(p=>p.Key).ToArray(),b,out _)&&whole.Apply(out _),"entire remaining source series transfers atomically");
        Require(registry.TryGet(empire,out state,out _)&&state.Classes.ContainsKey(a)&&!state.DesignClasses.ContainsValue(a)&&disabled.UpgradeDesignId==int.MinValue,"empty source retained and whole-series transfer preserves disabled path");
        Forget(galaxy);Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==Serialized(state),"explicit transfer target persists through registry reload and real Matrix read");
        var legacy=state.Clone();legacy.Version=1;legacy.Targets.Clear();Write(Storage(galaxy,empire.EmpireId),legacy);Forget(galaxy);
        var migration=new EditorSession(galaxy,empire,dest,null);
        Require(migration.Classes.Version==1&&migration.TargetChoice=="Automatic latest","native Matrix loads v1 without changing automatic behavior");
        Require(migration.SetTarget(destOld.DesignId,false,out _)&&migration.Apply(out _),"first explicit target migrates v1 to v2 at commit");
        Require(Read(Storage(galaxy,empire.EmpireId))!.Version==2&&Read(Storage(galaxy,empire.EmpireId))!.Targets[destKey]==destOld.DesignId,"migration persists target exactly for final native save/load check");
    }
}
