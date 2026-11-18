using DistantWorlds.Types;
using DistantWorlds.UI;

namespace AdvancedConstructionMod;

internal sealed class TransferWindow:NativeWindow
{
    private readonly EditorSession session;
    private readonly Action changed;
    private readonly string source;
    private readonly int[] ids,roles;
    private readonly Dictionary<string,int?> targets=new();
    private string? destination;
    private readonly DWButton destinationButton,apply;
    private readonly DWButton[] sourceTargets,destinationTargets;
    private readonly DWButton previousRoles,nextRoles;
    private readonly DWLabel guidance;
    private readonly NativeTextPager preview;
    private int rolePage;
    internal TransferWindow(EditorSession session,bool wholeSeries,Action changed):base("ACM.Transfer",wholeSeries?"TRANSFER ENTIRE SERIES":"TRANSFER PROJECT",1000,720)
    {
        this.session=session;this.changed=changed;Campaign=session.Galaxy;
        source=session.ClassId??throw new ArgumentException("Choose a source series first.");
        ids=wholeSeries?session.Classes.DesignClasses.Where(p=>p.Value==source).Select(p=>p.Key).ToArray():[session.Draft.DesignId];
        roles=session.ExpectedFacts.Where(f=>ids.Contains(f.DesignId)).Select(f=>f.Role).Distinct().Order().ToArray();
        Label("ACM.TransferSource",(wholeSeries?$"Move all {ids.Length} designs from {session.ClassName}, including obsolete versions.":$"Move {session.Draft.Name} from {session.ClassName}. Other versions stay in {session.ClassName}."),S(12),S(40),Panel.Size.X-S(24),S(38));
        destinationButton=Button("ACM.TransferDestination","1. Choose receiving series...",S(12),S(82),Panel.Size.X-S(24),ChooseDestination);
        float columnWidth=(Panel.Size.X-S(36))/2;
        Label("ACM.TransferRemainingHelp","2. Projects left behind: their retrofit target",S(12),S(118),columnWidth,S(28));
        Label("ACM.TransferIncomingHelp","3. Projects being moved: their retrofit target",S(24)+columnWidth,S(118),columnWidth,S(28));
        sourceTargets=new DWButton[2];destinationTargets=new DWButton[2];
        for(int i=0;i<2;i++)
        {
            int slot=i;float y=S(150+i*40);
            sourceTargets[i]=Button("ACM.TransferSourceTarget."+i,"",S(12),y,columnWidth,()=>ChooseTarget(slot,false),S(32));
            destinationTargets[i]=Button("ACM.TransferDestinationTarget."+i,"",S(24)+columnWidth,y,columnWidth,()=>ChooseTarget(slot,true),S(32));
        }
        guidance=Label("ACM.TransferGuidance","",S(12),S(232),Panel.Size.X-S(24),S(40));
        previousRoles=Button("ACM.TransferRolePrevious","Previous roles",S(12),S(276),S(150),()=>{if(rolePage>0){rolePage--;Bind();}});
        nextRoles=Button("ACM.TransferRoleNext","Next roles",S(174),S(276),S(150),()=>{if((rolePage+1)*2<roles.Length){rolePage++;Bind();}});
        preview=new NativeTextPager(this,"ACM.TransferPreview",S(12),S(312),Panel.Size.X-S(24),Math.Max(S(100),Panel.Size.Y-S(442)),S(26));
        Status=Label("ACM.TransferStatus","",S(12),Panel.Size.Y-S(118),Panel.Size.X-S(24),S(64));
        apply=Button("ACM.TransferUse","Use transfer plan",Panel.Size.X-S(300),Panel.Size.Y-S(42),S(156),Use);
        Button("ACM.TransferCancel","Cancel",Panel.Size.X-S(132),Panel.Size.Y-S(42),S(120),Close);Bind();
    }
    private ConstructionSnapshot Plan(bool allowUnresolved=false)
    {
        if(destination==null)throw new ArgumentException("Choose a destination series.");
        var next=SeriesPlans.Move(session.Classes,session.ExpectedFacts,ids,destination,targets,allowUnresolved);
        foreach(int role in roles)
        {
            string key=ConstructionSnapshot.TargetKey(destination,role);
            if(!allowUnresolved&&(!next.Targets.TryGetValue(key,out int goal)||goal<0))throw new ArgumentException("Select a destination target for "+(ShipRole)role+". No partial transfer is applied.");
        }
        return next;
    }
    private void ChooseDestination()
    {
        _=new SeriesChoiceWindow("CHOOSE RECEIVING SERIES",session.Classes.Classes.Where(p=>p.Key!=source).OrderBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).Select(p=>(p.Value,(Action)(()=>{destination=p.Key;targets.Clear();Bind();}))),Reopen,
            "Select the series to move these designs into. If the list is empty, create a second series in the design editor first.");
    }
    private void ChooseTarget(int slot,bool dest)
    {
        int n=rolePage*2+slot;if(destination==null||n>=roles.Length)return;
        if(!(dest?destinationTargets[slot]:sourceTargets[slot]).Enabled)return;
        try
        {
            var state=Plan(true);int role=roles[n];string series=dest?destination:source,key=ConstructionSnapshot.TargetKey(series,role);
            _=new SeriesChoiceWindow((dest?"AFTER MOVE, UPGRADE TO: ":"LEFT BEHIND, UPGRADE TO: ")+session.Classes.Classes[series]+" / "+(ShipRole)role,
                SeriesChoiceWindow.Targets(session,state,series,role,session.ExpectedFacts,id=>{targets[key]=id;Bind();},!dest,!dest),Reopen,
                dest?"Choose the project that moved ships will use for future Retrofit. The target is pinned; newer incoming projects will not replace it. No ship is sent to a yard now.":
                    "Choose the future Retrofit target for projects remaining in the original series. Automatic latest picks the newest compatible active project. No target pauses this series; it does not remove projects.");
        }
        catch(ArgumentException ex){Message(ex.Message,true);}
    }
    private void Bind()
    {
        destinationButton.Text=destination==null?"1. Choose receiving series...":"1. Receiving series: "+session.Classes.Classes[destination];apply.Enabled=destination!=null;
        Tip(destinationButton,"Receiving series","Choose the new series for the designs listed above.");
        Tip(apply,"Review before Save","This stages the complete transfer plan and returns to the editor. Save / Apply publishes it; editor Cancel discards it. Ship equipment and current orders do not change.");
        guidance.Text=destination==null?"First choose the series these designs should belong to.":
            "Left: target for projects that stay in "+session.Classes.Classes[source]+". Right: target after joining "+session.Classes.Classes[destination]+". Equipment and orders stay unchanged.";
        previousRoles.Visible=nextRoles.Visible=roles.Length>2;previousRoles.Enabled=rolePage>0;nextRoles.Enabled=(rolePage+1)*2<roles.Length;
        ConstructionSnapshot? plan=null;
        if(destination!=null)try{plan=Plan(true);}catch(ArgumentException ex){Message(ex.Message,true);apply.Enabled=false;}
        for(int i=0;i<2;i++)
        {
            int n=rolePage*2+i;sourceTargets[i].Visible=destinationTargets[i].Visible=n<roles.Length;
            if(n>=roles.Length)continue;int role=roles[n];
            bool remains=session.ExpectedFacts.Any(f=>f.Role==role&&!ids.Contains(f.DesignId)&&session.Classes.DesignClasses.GetValueOrDefault(f.DesignId)==source);
            destinationTargets[i].Enabled=destination!=null;sourceTargets[i].Enabled=destination!=null&&remains;
            string Describe(string series)
            {
                if(plan==null)return "choose receiving series first";
                string key=ConstructionSnapshot.TargetKey(series,role);
                return plan.Targets.TryGetValue(key,out int id)?id<0?"No target":session.Empire.Designs.GetById(id)?.Name??("Unavailable #"+id):"Automatic latest";
            }
            string left=(ShipRole)role+": "+(!remains?"No projects remain":Describe(source)),right=(ShipRole)role+": "+(destination==null?"choose receiving series first":Describe(destination));
            sourceTargets[i].Text=DesignsSeriesColumn.Fit(left,sourceTargets[i].FontNormal,sourceTargets[i].Size.X-S(20));
            destinationTargets[i].Text=DesignsSeriesColumn.Fit(right,destinationTargets[i].FontNormal,destinationTargets[i].Size.X-S(20));
            Tip(sourceTargets[i],"Projects left behind",left+"\n"+(!remains?"All projects of this role are being moved; no source target is needed. The empty series is retained.":"Choose a target for projects remaining in "+session.Classes.Classes[source]+". Automatic latest follows the newest compatible active project. No target pauses future Retrofit."));
            Tip(destinationTargets[i],"Projects being moved",right+"\nChoose the future Retrofit project after joining the receiving series. This does not order immediate Retrofit. Select the receiving series above first.");
        }
        if(plan==null){preview.SetText("After you choose a series, the preview will list the affected designs and ships.");if(destination==null)Message("");return;}
        var before=session.CapturePlan();
        try
        {
            if(!session.StageTransfer(ids,destination!,out var error,targets)){Message(error,true);preview.SetText("Transfer unavailable: "+error+" Choose a compatible destination target above.");apply.Enabled=false;return;}
            var projected=session.Projection();
            if(!projected.Preview.Accepted){Message(projected.Preview.Error,true);apply.Enabled=false;}
            else Message("Choose Use transfer plan, then save in the design editor to complete the move. Ships keep their equipment and current orders.");
            // Live lists can contain empty slots, unfinished ships and stale empire membership.
            // Count only current owned ships with an actual project; preview never repairs native lists.
            var counts=session.Galaxy.Ships.Where(s=>s!=null&&!s.IsDestroyed&&s.EmpireId==session.Empire.EmpireId)
                .Select(s=>s.GetDesign()).Where(d=>d!=null).GroupBy(d=>d!.DesignId).ToDictionary(g=>g.Key,g=>g.Count());
            var lines=session.ExpectedFacts.Where(f=>ids.Contains(f.DesignId)||before.State.DesignClasses.GetValueOrDefault(f.DesignId)==source||before.State.DesignClasses.GetValueOrDefault(f.DesignId)==destination)
                .Select(f=>session.Empire.Designs.GetById(f.DesignId)).Where(d=>d!=null).Select(d=>
                $"{d!.Name} [#{d.DesignId}] | ships: {counts.GetValueOrDefault(d.DesignId)} | "+
                $"{before.State.Classes.GetValueOrDefault(before.State.DesignClasses.GetValueOrDefault(d.DesignId)??"","Unassigned")} -> {session.ClassNameFor(d.DesignId)}\n"+
                $"Current target: {session.EffectiveTarget(d)} | After Apply: {session.EffectiveTarget(d,projected)} | "+
                $"Native path: {d.UpgradeDesignId} -> {projected.Preview.UpgradeValues.GetValueOrDefault(d.DesignId,d.UpgradeDesignId)} | Auto retrofit: {d.AllowAutoRetrofit}");
            string activation=string.Join(", ",(projected.Preview.ObsoleteValues??new Dictionary<int,bool>()).Keys.Select(id=>session.Empire.Designs.GetById(id)?.Name));
            preview.SetText(string.Join("\n",lines)+(activation.Length>0?"\nBecomes Active after Save: "+activation:""));
        }
        finally{session.RestorePlan(before);}
    }
    private void Tip(DWControl control,string title,string text)
    {control.HoverTips.Clear();control.AddHoverTip(title,text,control.CalculateAbsoluteRectangle(),S(440),Side.Right);}
    private void Use()
    {
        if(!ReferenceEquals(Active,this)||destination==null)return;
        if(!session.Fresh(out var error)){Message(error,true);return;}
        var before=session.CapturePlan();
        if(!session.StageTransfer(ids,destination,out error,targets)){session.RestorePlan(before);Message(error,true);return;}
        var preview=session.Projection().Preview;
        if(!preview.Accepted){session.RestorePlan(before);Message(preview.Error,true);return;}
        changed();Close();
    }
}
