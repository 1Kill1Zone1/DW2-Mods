using DistantWorlds.Types;

namespace AdvancedConstructionMod;

internal sealed class EditorSession
{
    internal readonly Galaxy Galaxy;
    internal readonly Empire Empire;
    internal readonly Design Draft;
    internal readonly Design? Origin;
    internal ConstructionSnapshot Classes { get; private set; }
    internal long Revision { get; private set; }
    internal string? ClassId { get; private set; }
    internal DesignFact[] ExpectedFacts { get; private set; }
    internal List<ConstructionChange> Sources { get; private set; }=[];
    internal bool Dirty { get; private set; }
    internal string? Error { get; private set; }
    private readonly HashSet<string> createdClasses=[];
    private ConstructionSnapshot committed;
    private readonly HashSet<int> activateTargets=[];
    private bool targetTouched,draftTarget;
    internal sealed record Plan(ConstructionSnapshot State,string? Class,List<ConstructionChange> Sources,bool Dirty,bool TargetTouched,bool DraftTarget,int[] Activate,string[] Created);
    internal Plan CapturePlan()=>new(Classes.Clone(),ClassId,Sources.ToList(),Dirty,targetTouched,draftTarget,activateTargets.ToArray(),createdClasses.ToArray());
    internal void RestorePlan(Plan p){Classes=p.State.Clone();ClassId=p.Class;Sources=p.Sources.ToList();Dirty=p.Dirty;targetTouched=p.TargetTouched;draftTarget=p.DraftTarget;activateTargets.Clear();activateTargets.UnionWith(p.Activate);createdClasses.Clear();createdClasses.UnionWith(p.Created);}

    internal EditorSession(Galaxy galaxy,Empire empire,Design draft,Design? origin)
    {
        Galaxy=galaxy;Empire=empire;Draft=draft;Origin=origin;
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!registry.TryGet(empire,out var state,out var error))throw new InvalidDataException(error);
            Classes=state.Clone();committed=state.Clone();Revision=state.Revision;
            state.DesignClasses.TryGetValue(origin?.DesignId??draft.DesignId,out var id);ClassId=id;
            ExpectedFacts=Capture();
        }
    }
    internal bool Published => ReferenceEquals(Galaxy.Designs.GetById(Draft.DesignId),Draft) && ReferenceEquals(Empire.Designs.GetById(Draft.DesignId),Draft);
    internal string ClassName => ClassId!=null&&Classes.Classes.TryGetValue(ClassId,out var name)?name:"—";
    internal string ClassNameFor(int designId)=>Classes.Classes.GetValueOrDefault(Classes.DesignClasses.GetValueOrDefault(designId)??"","Unassigned");
    internal bool HasSavedIdentity=>Origin==null&&ExpectedFacts.Any(f=>f.DesignId==Draft.DesignId);
    internal int Role=>(int)Draft.GetRole(Galaxy);
    internal string TargetChoice=>ClassId==null?"Choose a series":draftTarget?"This project after Save":
        Classes.Targets.TryGetValue(ConstructionSnapshot.TargetKey(ClassId,Role),out int id)?id==ConstructionGraph.Disabled?"No target":Empire.Designs.GetById(id)?.Name??("Missing #"+id):"Automatic latest";
    internal bool SetTarget(int? id,bool useDraft,out string? error)
    {
        if(ClassId==null){error="Choose and confirm a series first.";return false;}
        if(id>=0&&!useDraft)
        {
            var f=ExpectedFacts.FirstOrDefault(f=>f.DesignId==id);
            if(f==null||f.EmpireId!=Empire.EmpireId||f.Role!=Role||!f.Buildable||Classes.DesignClasses.GetValueOrDefault(f.DesignId)!=ClassId)
            {error="Choose a researched, compatible project in this series and role.";return false;}
        }
        activateTargets.Clear();if(id>=0&&!useDraft)activateTargets.Add(id.Value);
        string key=ConstructionSnapshot.TargetKey(ClassId,Role);draftTarget=useDraft;targetTouched=true;Classes.Version=ConstructionSnapshot.CurrentVersion;
        if(!useDraft){if(id.HasValue)Classes.Targets[key]=id.Value;else Classes.Targets.Remove(key);}
        Dirty=true;error=null;return true;
    }
    internal bool StageTransfer(IEnumerable<int> ids,string destination,out string? error,IReadOnlyDictionary<string,int?>? targetChoices=null)
    {
        try
        {
            if(!HasSavedIdentity)throw new ArgumentException("Save this new project before transferring saved projects or a whole series.");
            if(Sources.Count!=0)throw new ArgumentException("Save / Apply the current source choices before preparing a transfer.");
            var moved=ids.ToHashSet();
            Classes=SeriesPlans.Move(Classes,ExpectedFacts,moved,destination,targetChoices);
            foreach(int role in ExpectedFacts.Where(f=>moved.Contains(f.DesignId)).Select(f=>f.Role).Distinct())
                if(Classes.Targets.TryGetValue(ConstructionSnapshot.TargetKey(destination,role),out int target)&&target>=0)activateTargets.Add(target);
            ClassId=Classes.DesignClasses.GetValueOrDefault(Draft.DesignId);draftTarget=false;targetTouched=true;Dirty=true;error=null;return true;
        }
        catch(ArgumentException ex){error=ex.Message;return false;}
    }
    internal DesignFact[] Capture() => Empire.Designs.Where(d=>d!=null).Select(d=>RetrofitRuntime.Fact(Galaxy,Empire,d)).ToArray();
    internal bool Fresh(out string? error)
    {
        var registry=ConstructionCampaigns.Get(Galaxy,Empire);
        if(!registry.TryGet(Empire,out var state,out error))return false;
        // The player is editing the target itself; revalidate it at commit while preserving the source snapshot.
        bool IsSource(DesignFact f)=>!Published||f.DesignId!=Draft.DesignId;
        if(state.Revision!=Revision || !Capture().Where(IsSource).OrderBy(d=>d.DesignId).SequenceEqual(ExpectedFacts.Where(IsSource).OrderBy(d=>d.DesignId)))
        {error="Designs or series data changed. Reopen this design before applying.";return false;}
        error=null;return true;
    }
    internal bool SelectClass(string? id,out string? error)
    {
        if(id!=null&&!Classes.Classes.ContainsKey(id)){error="This series no longer exists.";return false;}
        if(Origin!=null&&Classes.DesignClasses.TryGetValue(Origin.DesignId,out var inherited)&&inherited!=id)
        {error="Upgrade inherits its source series. Change the source design's series explicitly before creating another branch.";return false;}
        if(id==ClassId){error=null;return true;}
        if(HasSavedIdentity&&ClassId!=null&&id!=null)
        {
            try{Classes=SeriesPlans.Move(Classes,ExpectedFacts,[Draft.DesignId],id);}
            catch(ArgumentException ex){error=ex.Message;return false;}
            if(Classes.Targets.TryGetValue(ConstructionSnapshot.TargetKey(id,Role),out int target)&&target>=0)activateTargets.Add(target);
        }
        ClassId=id;draftTarget=false;targetTouched=false;PruneUnusedDraftSeries();Dirty=true;error=null;return true;
    }
    internal bool SelectSeriesName(string? value,out string? error)
    {
        string name=value?.Trim()??string.Empty;
        if(name.Length==0)
        {
            if(ClassId==null){error=null;return true;}
            return SelectClass(null,out error);
        }
        string? existing=Classes.Classes.FirstOrDefault(p=>string.Equals(p.Value,name,StringComparison.OrdinalIgnoreCase)).Key;
        if(existing!=null)
        {
            if(existing==ClassId){error=null;return true;}
            return SelectClass(existing,out error);
        }
        return NameClass(name,null,out error);
    }
    internal bool NameClass(string name,string? renameId,out string? error)
    {
        name=name.Trim();
        if(name.Length is 0 or >80){error="Use a series name of 1–80 characters.";return false;}
        if(Classes.Classes.Any(p=>p.Key!=renameId&&string.Equals(p.Value,name,StringComparison.OrdinalIgnoreCase)))
        {error="A series with this name already exists. Select it in the list.";return false;}
        if(renameId!=null)
        {
            if(!ConstructionGraph.TryRename(Classes,renameId,name,out error))return false;
        }
        else
        {
            if(Origin!=null&&Classes.DesignClasses.ContainsKey(Origin.DesignId)){error="An Upgrade keeps its source series.";return false;}
            string id=ConstructionGraph.CreateClass(Classes,name);createdClasses.Add(id);
            if(!SelectClass(id,out error)){Classes.Classes.Remove(id);createdClasses.Remove(id);return false;}
        }
        Dirty=true;error=null;return true;
    }
    private void PruneUnusedDraftSeries()
    {
        foreach(string id in createdClasses.Where(id=>id!=ClassId&&!Classes.DesignClasses.ContainsValue(id)).ToArray())
        {Classes.Classes.Remove(id);foreach(string key in Classes.Targets.Keys.Where(k=>k.StartsWith(id+":",StringComparison.Ordinal)).ToArray())Classes.Targets.Remove(key);foreach(string key in Classes.AutoUpgrade.Keys.Where(k=>k.StartsWith(id+":",StringComparison.Ordinal)).ToArray())Classes.AutoUpgrade.Remove(key);createdClasses.Remove(id);}
    }
    internal bool ApplySources(IEnumerable<ConstructionChange> changes,out string? error)
    {
        var prior=Sources;bool wasDirty=Dirty;
        if(!StageSources(changes,out error))return false;
        if(Apply(out error))return true;
        Sources=prior;Dirty=wasDirty;return false;
    }
    internal bool StageSources(IEnumerable<ConstructionChange> changes,out string? error)
    {
        var proposed=changes.ToList();
        var preview=Preview(proposed);
        if(!preview.Accepted){error=preview.Error;return false;}
        Sources=proposed;Dirty=true;error=null;return true;
    }
    internal ConstructionPreview Preview(IEnumerable<ConstructionChange> changes)
        =>Projection(changes).Preview;
    internal (ConstructionPreview Preview,DesignFact[] Facts,int TargetId) Projection(IEnumerable<ConstructionChange>? changes=null)
    {
        int targetId=Draft.DesignId;
        if(targetId<0||!Published&&(Origin!=null||!ExpectedFacts.Any(f=>f.DesignId==targetId)))
        {targetId=int.MaxValue;while(ExpectedFacts.Any(f=>f.DesignId==targetId))targetId--;}
        var facts=ExpectedFacts.Where(f=>f.DesignId!=targetId).ToList();
        facts.Add(RetrofitRuntime.Fact(Galaxy,Empire,Draft) with {DesignId=targetId,Obsolete=targetId==Draft.DesignId&&Draft.Obsolete});
        var proposed=(changes??Sources).ToList();
        if(Origin!=null&&ClassId!=null&&!proposed.Any(c=>c.SourceDesignId==Origin.DesignId))proposed.Add(new(Origin.DesignId,targetId,true));
        var staged=Planned(targetId);
        var preview=ConstructionGraph.TryPreview(committed,facts,targetId,ClassId,proposed.Select(c=>c with {TargetDesignId=targetId}),RetrofitRuntime.NativeCompatible,staged,activateTargets);
        if(preview.Accepted)facts=facts.Select(f=>f with {UpgradeDesignId=preview.UpgradeValues[f.DesignId],Obsolete=preview.ObsoleteValues?.GetValueOrDefault(f.DesignId,f.Obsolete)??f.Obsolete}).ToList();
        return (preview,facts.ToArray(),targetId);
    }
    private ConstructionSnapshot Planned(int targetId)
    {
        var next=Classes.Clone();next.Version=ConstructionSnapshot.CurrentVersion;
        if(ClassId!=null&&(draftTarget||!targetTouched&&Origin!=null&&committed.Targets.GetValueOrDefault(ConstructionSnapshot.TargetKey(ClassId,Role),ConstructionGraph.Disabled)==Origin.DesignId))
            next.Targets[ConstructionSnapshot.TargetKey(ClassId,Role)]=targetId;
        return next;
    }
    internal string EffectiveTarget(Design design,(ConstructionPreview Preview,DesignFact[] Facts,int TargetId)? projection=null)
    {
        if(projection is not { } p)
        {
            int current=design.ResolveRetrofitDesignId(Galaxy,Empire,out var target);
            if(committed.DesignClasses.ContainsKey(design.DesignId)&&ExpectedFacts.FirstOrDefault(f=>f.DesignId==design.DesignId) is { } savedFact)
            {
                var resolved=ConstructionGraph.ResolveCandidate(committed,savedFact,ExpectedFacts,current,RetrofitRuntime.NativeCompatible);
                return resolved.DesignId>=0?Empire.Designs.GetById(resolved.DesignId)?.Name??"Design unavailable":resolved.Error??"No target";
            }
            return current>=0&&target!=null?target.Name:design.UpgradeDesignId==ConstructionGraph.Disabled?"Retrofit disabled":"Already latest / no compatible target";
        }
        if(!p.Preview.Accepted)return p.Preview.Error??"Preview unavailable";
        int id=ReferenceEquals(design,Draft)?p.TargetId:design.DesignId;
        var source=p.Facts.FirstOrDefault(f=>f.DesignId==id);if(source==null)return "Design unavailable";
        if(!p.Preview.Next!.DesignClasses.ContainsKey(id))return EffectiveTarget(design);
        var result=ConstructionGraph.ResolveCandidate(p.Preview.Next!,source,p.Facts,-1,RetrofitRuntime.NativeCompatible);
        if(result.DesignId<0)return result.Error??"No compatible target";
        return result.DesignId==p.TargetId?Draft.Name:Empire.Designs.GetById(result.DesignId)?.Name??"Design unavailable";
    }
    internal bool WillUpgradeHere(Design design,(ConstructionPreview Preview,DesignFact[] Facts,int TargetId) p)
    {
        if(!p.Preview.Accepted)return false;
        var source=p.Facts.FirstOrDefault(f=>f.DesignId==design.DesignId);if(source==null)return false;
        return ConstructionGraph.ResolveCandidate(p.Preview.Next!,source,p.Facts,-1,RetrofitRuntime.NativeCompatible).DesignId==p.TargetId;
    }
    internal bool Apply(out string? error)
    {
        if(!Fresh(out error))return false;
        if(!Published){error="Source choices are staged. Save this new design to publish its series and paths.";return true;}
        return Commit(Draft,Capture(),out error);
    }
    internal bool Commit(Design published,DesignFact[] expected,out string? error)
    {
        var changes=Sources.Select(c=>c with {TargetDesignId=published.DesignId}).ToList();
        if(Origin!=null&&ClassId!=null && !changes.Any(c=>c.SourceDesignId==Origin.DesignId))
            changes.Add(new(Origin.DesignId,published.DesignId,true));
        if(!ConstructionCommands.TryApply(Galaxy,Empire,published,Revision,ClassId,changes,expected,out _,out error,Planned(published.DesignId),activateTargets))
        {Error=error;return false;}
        var registry=ConstructionCampaigns.Get(Galaxy,Empire);
        if(!registry.TryGet(Empire,out var state,out error))return false;
        Classes=state.Clone();committed=state.Clone();Revision=state.Revision;ExpectedFacts=Capture();Dirty=false;Sources=[];createdClasses.Clear();activateTargets.Clear();draftTarget=false;targetTouched=false;Error=null;return true;
    }
}
