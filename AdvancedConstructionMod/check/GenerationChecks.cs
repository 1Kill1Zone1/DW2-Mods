using AdvancedConstructionMod;
using DistantWorlds.Types;

internal static partial class Checks
{
    private static void GenerationChecks(Galaxy galaxy,Empire empire,Design old,Design middle)
    {
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var state,out _),"class graph ready for old-generation tests");
        var next=middle.Clone(galaxy);next.DesignId=galaxy.Designs.Max(d=>d.DesignId)+10;next.Name="Hura Third Generation";next.StarDate=middle.StarDate+1000;next.Obsolete=false;next.UpgradeDesignId=-2;
        galaxy.Designs.Add(next);empire.Designs.Add(next);
        Require(ConstructionCommands.TrySetClass(galaxy,empire,next,state.Revision,state.DesignClasses[old.DesignId],out _),"new generation receives native-compatible class");
        bool wasObsolete=middle.Obsolete;int oldArrow=old.UpgradeDesignId,middleArrow=middle.UpgradeDesignId;
        old.UpgradeDesignId=middle.DesignId;middle.Obsolete=true;middle.UpgradeDesignId=next.DesignId;
        Require(old.ResolveRetrofitDesignId(galaxy,empire,out var target)==next.DesignId&&target==next,"native oldest generation follows obsolete intermediate inside class");
        middle.UpgradeDesignId=-2;
        Require(old.ResolveRetrofitDesignId(galaxy,empire,out target)==next.DesignId&&target==next,"obsolete intermediate latest-mode resolves only inside its own class");
        middle.UpgradeDesignId=old.DesignId;
        Require(old.ResolveRetrofitDesignId(galaxy,empire,out target)==next.DesignId&&target==next,"series latest does not follow a cyclic native arrow chain");
        middle.Obsolete=wasObsolete;middle.UpgradeDesignId=middleArrow;old.UpgradeDesignId=oldArrow;
        var hull=galaxy.ShipHulls.Where(h=>h!=null&&h.Role==old.GetRole(galaxy)&&h.RaceId==empire.DominantRaceId&&h.ShipHullId!=old.HullId).OrderByDescending(h=>h.Size).First();
        bool already=empire.Research.ResearchedShipHulls.ContainsId(hull.ShipHullId);
        if(!already)empire.Research.ResearchedShipHulls.Add(hull);
        try
        {
            var advanced=Design.GenerateNewDesign(galaxy,galaxy.StarDate,empire,empire.Research,hull,empire.Research.ResearchedComponents,
                empire.Research.ComponentBestValues,empire.Research.WeaponBestValues,empire.Research.SizeBestValues,empire.Policy.ComponentSelectionFactors,1f,null!);
            advanced.DesignId=galaxy.Designs.Max(d=>d.DesignId)+10;advanced.StarDate=next.StarDate+1;advanced.Name="Advanced Hull Class Test";advanced.Obsolete=false;advanced.UpgradeDesignId=-3;
            galaxy.Designs.Add(advanced);empire.Designs.Add(advanced);
            Require(advanced.HullId!=old.HullId&&advanced.GetRole(galaxy)==old.GetRole(galaxy)&&RetrofitRuntime.Fact(galaxy,empire,advanced).Buildable,"native generator makes a valid researched alternative hull of the same role");
            Require(registry.TryGet(empire,out state,out _)&&ConstructionCommands.TrySetClass(galaxy,empire,advanced,state.Revision,state.DesignClasses[old.DesignId],out _),"class assignment permits a different legal native hull");
            old.UpgradeDesignId=advanced.DesignId;
            Require(old.ResolveRetrofitDesignId(galaxy,empire)==advanced.DesignId,"native explicit same-class retrofit accepts another hull of the role");
            old.UpgradeDesignId=-3;
            Require(old.ResolveRetrofitDesignId(galaxy,empire)==advanced.DesignId,"native largest-hull helper chooses legal hull within class");
        }
        finally{old.UpgradeDesignId=oldArrow;if(!already)empire.Research.ResearchedShipHulls.Remove(hull);}
    }
}
