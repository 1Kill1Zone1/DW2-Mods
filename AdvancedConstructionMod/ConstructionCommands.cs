using DistantWorlds.Types;

namespace AdvancedConstructionMod;

/// <summary>Single commit boundary used by the editor modal: Matrix first, native arrows second, rollback on either failure.</summary>
public static class ConstructionCommands
{
    public static bool TryApply(Galaxy galaxy, Empire empire, Design target, long expectedRevision, string? targetClassId,
        IEnumerable<ConstructionChange> changes, IReadOnlyList<DesignFact> expectedFacts, out ConstructionPreview preview, out string? error,
        ConstructionSnapshot? stagedClasses = null,IReadOnlySet<int>? activateDesigns=null)
    {
        var registry = ConstructionCampaigns.Get(galaxy);
        lock (registry.Gate)
        {
            if (!registry.TryGet(empire, out ConstructionSnapshot current, out error)) { preview = Reject(error!); return false; }
            if (current.Revision != expectedRevision) { error = "The source list is stale; reopen Edit sources."; preview = Reject(error); return false; }
            if (target == null || target.EmpireId != empire.EmpireId) { error = "Target is no longer an own design."; preview = Reject(error); return false; }

            Design[] live = empire.Designs.ToArray().Where(item => item != null).ToArray();
            if (!ReferenceEquals(empire.Designs.GetById(target.DesignId), target) || !ReferenceEquals(galaxy.Designs.GetById(target.DesignId),target))
            { error = "Target is no longer the current saved design."; preview = Reject(error); return false; }
            var facts = live.Select(item => RetrofitRuntime.Fact(galaxy, empire, item)).ToArray();
            if (!facts.OrderBy(item => item.DesignId).SequenceEqual(expectedFacts.OrderBy(item => item.DesignId)))
            { error = "Design data changed; reopen Edit sources."; preview = Reject(error); return false; }
            ConstructionSnapshot basis=current;
            if(stagedClasses!=null)
            {
                try { stagedClasses.Validate(); } catch(Exception ex) { error=ex.Message;preview=Reject(error);return false; }
                if(current.Classes.Keys.Any(id=>!stagedClasses.Classes.ContainsKey(id)))
                {error="Deleting Construction Classes is not supported.";preview=Reject(error);return false;}
                basis=stagedClasses.Clone();
            }
            preview = ConstructionGraph.TryPreview(current, facts, target.DesignId, targetClassId, changes, RetrofitRuntime.NativeCompatible,basis,activateDesigns);
            if (!preview.Accepted || preview.Next == null) { error = preview.Error; return false; }
            if(!RetrofitAdmission.QueuedChangesAllowed(galaxy,empire,current,preview,out error))
            {preview=Reject(error!);return false;}

            var next = preview.Next;
            SeriesAutomation.FillDefaults(galaxy,empire,next);
            next.Revision = checked(current.Revision + 1);
            var byId = live.ToDictionary(item => item.DesignId);
            var oldArrows = preview.UpgradeValues.Where(pair => byId.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => byId[pair.Key].UpgradeDesignId);
            var oldObsolete=(preview.ObsoleteValues??new Dictionary<int,bool>()).Keys.ToDictionary(id=>id,id=>byId[id].Obsolete);
            try
            {
                if (!registry.TryCommit(empire, next, out error)) return false;
                foreach (var pair in preview.UpgradeValues)
                    if (byId.TryGetValue(pair.Key, out Design? source)) source.UpgradeDesignId = pair.Value;
                foreach(var pair in preview.ObsoleteValues??new Dictionary<int,bool>())byId[pair.Key].Obsolete=pair.Value;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                foreach (var pair in oldArrows) byId[pair.Key].UpgradeDesignId = pair.Value;
                foreach(var pair in oldObsolete)byId[pair.Key].Obsolete=pair.Value;
                _ = registry.TryRestore(empire, current, out _);
                error = "Construction Series changes were not applied: " + ex.Message;
                preview = Reject(error);
                return false;
            }
        }
    }

    public static bool TrySetClass(Galaxy galaxy, Empire empire, Design design, long expectedRevision, string? classId, out string? error)
    {
        var registry = ConstructionCampaigns.Get(galaxy);
        lock (registry.Gate)
        {
            if (!registry.TryGet(empire, out ConstructionSnapshot current, out error)) return false;
            if (current.Revision != expectedRevision) { error = "Construction Series data changed; reopen the design."; return false; }
            if (design == null || design.EmpireId != empire.EmpireId || design.DesignId < 0 ||
                !ReferenceEquals(empire.Designs.GetById(design.DesignId),design) || !ReferenceEquals(galaxy.Designs.GetById(design.DesignId),design))
            { error = "Only a current saved design of this empire can receive a Construction Series."; return false; }
            if (classId != null && !current.Classes.ContainsKey(classId)) { error = "Construction Series no longer exists."; return false; }
            return TryApply(galaxy,empire,design,expectedRevision,classId,[],
                empire.Designs.Where(d=>d!=null).Select(d=>RetrofitRuntime.Fact(galaxy,empire,d)).ToArray(),out _,out error);
        }
    }

    private static ConstructionPreview Reject(string error) => new(false, error, null, new Dictionary<int, int>());
}
