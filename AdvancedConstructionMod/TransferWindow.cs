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
    private readonly NativeTextPager preview;
    private int rolePage;
    internal TransferWindow(EditorSession session,bool wholeSeries,Action changed):base("ACM.Transfer",wholeSeries?"TRANSFER ENTIRE SERIES":"TRANSFER PROJECT",1000,720)
    {
        this.session=session;this.changed=changed;Campaign=session.Galaxy;
        source=session.ClassId??throw new ArgumentException("Choose a source series first.");
        ids=wholeSeries?session.Classes.DesignClasses.Where(p=>p.Value==source).Select(p=>p.Key).ToArray():[session.Draft.DesignId];
        roles=session.ExpectedFacts.Where(f=>ids.Contains(f.DesignId)).Select(f=>f.Role).Distinct().Order().ToArray();
        Label("ACM.TransferSource","From: "+session.ClassName+" | "+ids.Length+" project(s). Components and ship orders stay unchanged.",S(12),S(40),Panel.Size.X-S(24),S(38));
        destinationButton=Button("ACM.TransferDestination","Choose destination series...",S(12),S(82),Panel.Size.X-S(24),ChooseDestination);
        sourceTargets=new DWButton[2];destinationTargets=new DWButton[2];
        for(int i=0;i<2;i++)
        {
            int slot=i;float y=S(122+i*36),width=(Panel.Size.X-S(36))/2;
            sourceTargets[i]=Button("ACM.TransferSourceTarget."+i,"",S(12),y,width,()=>ChooseTarget(slot,false));
            destinationTargets[i]=Button("ACM.TransferDestinationTarget."+i,"",S(24)+width,y,width,()=>ChooseTarget(slot,true));
        }
        Button("ACM.TransferRolePrevious","Previous roles",S(12),S(200),S(150),()=>{if(rolePage>0){rolePage--;Bind();}});
        Button("ACM.TransferRoleNext","Next roles",S(174),S(200),S(150),()=>{if((rolePage+1)*2<roles.Length){rolePage++;Bind();}});
        preview=new NativeTextPager(this,"ACM.TransferPreview",S(12),S(240),Panel.Size.X-S(24),Math.Max(S(120),Panel.Size.Y-S(370)),S(26));
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
        _=new SeriesChoiceWindow("DESTINATION SERIES",session.Classes.Classes.Where(p=>p.Key!=source).OrderBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).Select(p=>(p.Value,(Action)(()=>{destination=p.Key;targets.Clear();Bind();}))),Reopen);
    }
    private void ChooseTarget(int slot,bool dest)
    {
        int n=rolePage*2+slot;if(destination==null||n>=roles.Length)return;
        try
        {
            var state=Plan(true);int role=roles[n];string series=dest?destination:source,key=ConstructionSnapshot.TargetKey(series,role);
            _=new SeriesChoiceWindow((dest?"DESTINATION TARGET: ":"REMAINING SOURCE TARGET: ")+session.Classes.Classes[series]+" / "+(ShipRole)role,
                SeriesChoiceWindow.Targets(session,state,series,role,session.ExpectedFacts,id=>{targets[key]=id;Bind();},!dest,!dest),Reopen);
        }
        catch(ArgumentException ex){Message(ex.Message,true);}
    }
    private void Bind()
    {
        destinationButton.Text=destination==null?"Choose destination series...":"To: "+session.Classes.Classes[destination];apply.Enabled=destination!=null;
        ConstructionSnapshot? plan=null;
        try{plan=Plan(true);}catch(ArgumentException ex){Message(ex.Message,true);apply.Enabled=false;}
        for(int i=0;i<2;i++)
        {
            int n=rolePage*2+i;sourceTargets[i].Visible=destinationTargets[i].Visible=n<roles.Length;
            if(n>=roles.Length)continue;int role=roles[n];
            string Describe(string series)
            {
                if(plan==null)return "choose destination";
                string key=ConstructionSnapshot.TargetKey(series,role);
                return plan.Targets.TryGetValue(key,out int id)?id<0?"none":session.Empire.Designs.GetById(id)?.Name??("#"+id):"automatic";
            }
            sourceTargets[i].Text="Source "+(ShipRole)role+": "+Describe(source);
            destinationTargets[i].Text="Destination "+(ShipRole)role+": "+(destination==null?"choose":Describe(destination));
        }
        if(plan==null){preview.SetText("Choose a destination to preview every project and its target.");return;}
        var before=session.CapturePlan();
        try
        {
            if(!session.StageTransfer(ids,destination!,out var error,targets)){Message(error,true);preview.SetText("Transfer unavailable: "+error+" Choose a compatible destination target above.");apply.Enabled=false;return;}
            var projected=session.Projection();
            if(!projected.Preview.Accepted){Message(projected.Preview.Error,true);apply.Enabled=false;}
            else Message("Preview only. Use transfer plan returns to the editor; Save / Apply publishes every change together. No ship is sent to a yard.");
            var counts=session.Empire.Ships.Where(s=>!s.IsDestroyed).GroupBy(s=>s.DesignId).ToDictionary(g=>g.Key,g=>g.Count());
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
