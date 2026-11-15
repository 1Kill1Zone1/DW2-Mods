namespace AdvancedConstructionMod;

public static class SeriesPlans
{
    public static DesignFact? Target(ConstructionSnapshot state,string series,int role,IEnumerable<DesignFact> facts)
    {
        string key=ConstructionSnapshot.TargetKey(series,role);
        var eligible=facts.Where(f=>f.Role==role&&f.Buildable&&!f.Obsolete&&state.DesignClasses.GetValueOrDefault(f.DesignId)==series);
        return state.Targets.TryGetValue(key,out int id)?eligible.FirstOrDefault(f=>f.DesignId==id):
            eligible.OrderByDescending(f=>f.StarDate).ThenByDescending(f=>f.DesignId).FirstOrDefault();
    }

    public static ConstructionSnapshot Move(ConstructionSnapshot current,IReadOnlyList<DesignFact> facts,IEnumerable<int> designIds,string destination,
        IReadOnlyDictionary<string,int?>? choices=null,bool allowUnresolved=false)
    {
        current.Validate();if(!current.Classes.ContainsKey(destination))throw new ArgumentException("Choose an existing destination series.");
        var ids=designIds.ToHashSet();if(ids.Count==0)throw new ArgumentException("No projects selected for transfer.");
        if(ids.All(id=>current.DesignClasses.GetValueOrDefault(id)==destination))throw new ArgumentException("These projects already belong to the destination series.");
        var byId=facts.ToDictionary(f=>f.DesignId);
        if(ids.Any(id=>!byId.ContainsKey(id)))throw new ArgumentException("A transferred project no longer exists.");
        var next=current.Clone();next.Version=2;
        foreach(int role in ids.Select(id=>byId[id].Role).Distinct())
        {
            string key=ConstructionSnapshot.TargetKey(destination,role);
            bool chosen=choices!=null&&choices.TryGetValue(key,out _);
            int? selected=chosen?choices![key]:current.Targets.TryGetValue(key,out int pin)?pin:null;
            var target=selected.HasValue?facts.FirstOrDefault(f=>f.DesignId==selected&&f.Role==role&&f.Buildable&&
                (current.DesignClasses.GetValueOrDefault(f.DesignId)==destination||ids.Contains(f.DesignId))):Target(current,destination,role,facts);
            // Only an empty automatic destination can acquire its initial head from incoming projects.
            if(target==null&&!selected.HasValue&&!facts.Any(f=>f.Role==role&&current.DesignClasses.GetValueOrDefault(f.DesignId)==destination))
                target=ids.Select(id=>byId[id]).Where(f=>f.Role==role&&f.Buildable).OrderBy(f=>f.Obsolete).ThenByDescending(f=>f.StarDate).ThenByDescending(f=>f.DesignId).FirstOrDefault();
            if(target==null)
            {
                if(!allowUnresolved)throw new ArgumentException("Choose a researched, buildable destination target for role "+role+". The existing target was not replaced. Nothing was transferred.");
                next.Targets[key]=selected??ConstructionGraph.Disabled;continue;
            }
            foreach(int id in ids.Where(id=>byId[id].Role==role))
                if(!allowUnresolved&&(!byId[id].Buildable||!ConstructionGraphCompatible(byId[id],target)))throw new ArgumentException("Incompatible or unresearched transferred project: #"+id+". Nothing was transferred.");
            next.Targets[key]=target.DesignId;
        }
        foreach(int id in ids)next.DesignClasses[id]=destination;
        foreach(var pair in current.Targets.Where(p=>p.Value>=0&&ids.Contains(p.Value)))
            if(next.DesignClasses.GetValueOrDefault(pair.Value)!=pair.Key[..32])next.Targets[pair.Key]=ConstructionGraph.Disabled;
        if(choices!=null)foreach(var pair in choices.Where(p=>!p.Key.StartsWith(destination+":",StringComparison.Ordinal)))
        {if(pair.Value.HasValue)next.Targets[pair.Key]=pair.Value.Value;else next.Targets.Remove(pair.Key);}
        next.Validate();return next;
    }
    private static bool ConstructionGraphCompatible(DesignFact a,DesignFact b)=>a.EmpireId==b.EmpireId&&a.Role==b.Role&&b.Buildable;
}
