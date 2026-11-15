namespace AdvancedConstructionMod;

/// <summary>Pure validation and class-aware selection; UI and Harmony hooks share this contract.</summary>
public static class ConstructionGraph
{
    public const int Disabled = int.MinValue;

    public static string CreateClass(ConstructionSnapshot state, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Construction Series must contain 1–80 characters.", nameof(name));
        if(state.Classes.Count>=512)throw new ArgumentException("This campaign already has the maximum of 512 Construction Classes.",nameof(state));
        string id;
        do id = Guid.NewGuid().ToString("N"); while (state.Classes.ContainsKey(id));
        state.Classes.Add(id, name);
        return id;
    }

    public static bool TryRename(ConstructionSnapshot state, string classId, string name, out string? error)
    {
        name = (name ?? string.Empty).Trim();
        if (!state.Classes.ContainsKey(classId)) { error = "Construction Series no longer exists."; return false; }
        if (name.Length is 0 or > 80) { error = "Construction Series must contain 1–80 characters."; return false; }
        state.Classes[classId] = name; error = null; return true;
    }

    public static ConstructionPreview TryPreview(ConstructionSnapshot current, IEnumerable<DesignFact> allDesigns,
        int targetDesignId, string? targetClassId, IEnumerable<ConstructionChange> changes,
        Func<DesignFact, DesignFact, bool> nativeHullCompatible,ConstructionSnapshot? staged=null,IReadOnlySet<int>? activate=null)
    {
        try
        {
            current.Validate();
            var groups=allDesigns.GroupBy(d=>d.DesignId).ToArray();
            if(groups.Any(group=>group.Count()!=1))return Reject("Duplicate design identities; reopen the source list.");
            var facts = groups.ToDictionary(group => group.Key, group => group.Single());
            if (!facts.TryGetValue(targetDesignId, out var target))
                return Reject("Target design is missing or obsolete.");
            if (targetClassId != null && !(staged??current).Classes.ContainsKey(targetClassId)) return Reject("Selected Construction Series no longer exists.");

            var next = (staged??current).Clone();next.Version=ConstructionSnapshot.CurrentVersion;
            next.Validate();
            if(current.Classes.Keys.Any(id=>!next.Classes.ContainsKey(id)))return Reject("Deleting a series is not supported.");
            if(next.DesignClasses.Keys.Where(id=>current.DesignClasses.GetValueOrDefault(id)!=next.DesignClasses[id]).Any(id=>!facts.TryGetValue(id,out var f)||f.EmpireId!=target.EmpireId))return Reject("Only current own projects can be transferred.");
            if (targetClassId == null) next.DesignClasses.Remove(targetDesignId); else next.DesignClasses[targetDesignId] = targetClassId;
            foreach(var old in current.Targets.Where(p=>p.Value>=0))
                if(next.Targets.GetValueOrDefault(old.Key,Disabled)==old.Value&&next.DesignClasses.GetValueOrDefault(old.Value)!=old.Key[..32])next.Targets[old.Key]=Disabled;
            var paths = facts.ToDictionary(pair => pair.Key, pair => pair.Value.UpgradeDesignId);
            foreach (var change in changes.GroupBy(c => c.SourceDesignId).Select(group => group.Last()))
            {
                if (change.TargetDesignId != targetDesignId) return Reject("The source selection belongs to a different target design.");
                if (!facts.TryGetValue(change.SourceDesignId, out var source)) return Reject("A selected source no longer exists.");
                if (source.DesignId == targetDesignId) return Reject("A design cannot upgrade to itself.");
                if (source.EmpireId != target.EmpireId) return Reject("Source and target must belong to the same empire.");
                if (source.Role != target.Role) return Reject("Source and target must have the same native role.");
                if (!nativeHullCompatible(source, target)) return Reject("Source and target hulls are not compatible.");
                if (change.Selected)
                {
                    if (targetClassId == null) return Reject("Choose a Construction Series before linking sources.");
                    if (next.DesignClasses.TryGetValue(source.DesignId, out var sourceClass) && sourceClass != targetClassId)
                        return Reject("A source in another Construction Series must be reclassified explicitly.");
                    next.DesignClasses[source.DesignId] = targetClassId;
                    paths[source.DesignId] = next.Targets.ContainsKey(ConstructionSnapshot.TargetKey(targetClassId,source.Role))?-2:targetDesignId;
                }
                else if (paths[source.DesignId] == targetDesignId || change.WasIncoming)
                    paths[source.DesignId] = Disabled;
            }
            if (HasCycle(paths, facts.Keys)) return Reject("This change creates an upgrade cycle.");
            foreach(var pair in paths.Where(p=>p.Value>=0).ToArray())
            {
                bool affected=current.DesignClasses.GetValueOrDefault(pair.Key)!=next.DesignClasses.GetValueOrDefault(pair.Key)||current.DesignClasses.GetValueOrDefault(pair.Value)!=next.DesignClasses.GetValueOrDefault(pair.Value)||
                    pair.Value!=facts[pair.Key].UpgradeDesignId;
                if(affected&&next.DesignClasses.GetValueOrDefault(pair.Key)!=next.DesignClasses.GetValueOrDefault(pair.Value))paths[pair.Key]=-2;
            }
            if (HasCycle(paths, facts.Keys)) return Reject("This change creates an upgrade cycle.");
            var active=(activate??new HashSet<int>()).Where(id=>next.Targets.ContainsValue(id)).ToHashSet();
            foreach(var pin in next.Targets.Where(p=>p.Value>=0))
            {
                if(!facts.TryGetValue(pin.Value,out var goal)||goal.EmpireId!=target.EmpireId||!goal.Buildable||next.DesignClasses.GetValueOrDefault(goal.DesignId)!=pin.Key[..32]||goal.Role!=int.Parse(pin.Key[33..]))return Reject("Selected series target is missing or incompatible: #"+pin.Value);
                if(current.Targets.GetValueOrDefault(pin.Key,Disabled)!=pin.Value)active.Add(pin.Value);
            }
            if(active.Any(id=>!facts.TryGetValue(id,out var f)||f.EmpireId!=target.EmpireId||!f.Buildable))return Reject("A target cannot be reactivated with current hulls/components.");
            if(target.Obsolete&&!active.Contains(targetDesignId)&&current.DesignClasses.GetValueOrDefault(targetDesignId)==next.DesignClasses.GetValueOrDefault(targetDesignId))return Reject("Current project is obsolete. Open an active project to edit sources, or choose this current project as the series target to reactivate it.");
            next.Validate();
            return new(true, null, next, paths,active.Where(id=>facts[id].Obsolete).ToDictionary(id=>id,_=>false));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return Reject(ex.Message);
        }
    }

    public static RetrofitResolution ResolveCandidate(ConstructionSnapshot snapshot, DesignFact source,
        IEnumerable<DesignFact> candidates, int nativeCandidateId, Func<DesignFact, DesignFact, bool> nativeHullCompatible)
    {
        snapshot.Validate();
        if (!snapshot.DesignClasses.TryGetValue(source.DesignId, out var classId)) return new(nativeCandidateId, null);
        if (source.UpgradeDesignId == Disabled) return RetrofitResolution.None("Retrofit path is disabled.");
        string key=ConstructionSnapshot.TargetKey(classId,source.Role);
        if(snapshot.Targets.TryGetValue(key,out int selected))
        {
            if(selected==Disabled)return RetrofitResolution.None("No target selected for this series and role.");
            var pinned=candidates.FirstOrDefault(c=>c.DesignId==selected&&c.EmpireId==source.EmpireId&&c.Role==source.Role&&!c.Obsolete&&c.Buildable&&snapshot.DesignClasses.GetValueOrDefault(c.DesignId)==classId&&nativeHullCompatible(source,c));
            if(pinned==null)return RetrofitResolution.None("Selected series target is missing, obsolete or incompatible.");
            return pinned.DesignId==source.DesignId?RetrofitResolution.None("This project is the series target."):new(pinned.DesignId,null);
        }
        var target = candidates.Where(candidate => candidate.EmpireId == source.EmpireId && candidate.Role == source.Role &&
            !candidate.Obsolete && candidate.Buildable &&
            snapshot.DesignClasses.TryGetValue(candidate.DesignId, out var candidateClass) && candidateClass == classId &&
            nativeHullCompatible(source, candidate)).OrderByDescending(candidate => candidate.StarDate)
            .ThenByDescending(candidate => candidate.DesignId).FirstOrDefault();
        if (target == null) return RetrofitResolution.None("No compatible active design in this series.");
        if (target.DesignId == source.DesignId || target.StarDate < source.StarDate ||
            target.StarDate == source.StarDate && target.DesignId < source.DesignId)
            return RetrofitResolution.None("Already the latest design in this series.");
        return new(target.DesignId, null);
    }
    private static bool HasCycle(IReadOnlyDictionary<int, int> paths, IEnumerable<int> ids)
    {
        var complete=new HashSet<int>();
        foreach (int start in ids)
        {
            if(complete.Contains(start))continue;
            var seen = new HashSet<int>();
            int current = start;
            while (!complete.Contains(current) && paths.TryGetValue(current, out int next) && next >= 0)
            {
                if (!seen.Add(current) || next == start) return true;
                current = next;
            }
            complete.UnionWith(seen);
        }
        return false;
    }

    private static ConstructionPreview Reject(string error) => new(false, error, null, new Dictionary<int, int>());
}
