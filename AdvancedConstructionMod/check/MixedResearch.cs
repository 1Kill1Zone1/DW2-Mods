using AdvancedConstructionMod;
using DistantWorlds.Types;
using DW2ModLauncher.Loader;
using HarmonyLib;
using System.Runtime.Loader;

internal static partial class Checks
{
    private static void MixedResearch(Galaxy galaxy, Empire empire, string candidate)
    {
        Entry.Init();
        var coherent=AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
        string options=System.Text.Json.JsonSerializer.Serialize(new { ShipDesign=true, DesignLineages=true,
            OutputDirectory=Environment.GetEnvironmentVariable("DW2_TEST_OUTPUT_DIR") });
        coherent.GetType("DW2CoherentResearch.Entry",true)!.GetMethod("InitWithOptions")!.Invoke(null,[options,new Dictionary<string,string>()]);
        var lines=coherent.GetType("DW2CoherentResearch.DesignLines",true)!;
        Console.WriteLine("Lineages requested=True effective="+AccessTools.Field(lines,"Enabled").GetValue(null));
        var source=empire.Designs.First(d=>!d.Obsolete && d.GetRole(galaxy)==ShipRole.Frigate && RetrofitRuntime.Fact(galaxy,empire,d).Buildable);
        var hull=source.GetShipHull(galaxy);
        var session=new EditorSession(galaxy,empire,source,null);
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var previous,out _),"series registry available");
        var state=previous.Clone();
        foreach(string key in state.AutoUpgrade.Keys.ToArray())state.AutoUpgrade[key]=false;
        var series=ConstructionGraph.CreateClass(state,"Mixed research Picket");
        var setMission=(Func<object,int,string,bool>)LiveChannels.Channels["coherent-research.save-mission"];
        var getMission=(Func<object,int,string>)LiveChannels.Channels["coherent-research.design-mission"];
        Design Publish(string mission)
        {
            var design=CoherentMissions.Preview(session,mission)!;
            design.DesignId=galaxy.Designs.GetNextId();
            design.StarDate=galaxy.StarDate;
            design.Name="Research fixture "+mission;
            design.Obsolete=false;
            design.UpgradeDesignId=-2;
            galaxy.Designs.Add(design);empire.Designs.Add(design);
            Require(setMission(galaxy,design.DesignId,mission)&&getMission(galaxy,design.DesignId)==mission,"fixture mission "+mission);
            return design;
        }
        foreach(var old in empire.Designs.Where(d=>d.HullId==hull.ShipHullId))old.Obsolete=true;
        var picket=Publish("Picket");var ranged=Publish("Long");
        var part=picket.Components.First(c=>!c.IsEmpty && ranged.Components.Any(other=>!other.IsEmpty&&other.ComponentId==c.ComponentId&&other.Level==c.Level)
            && c.Level+1<c.Component.Values.Count);
        var technology=empire.Research.ResearchedComponents.First(c=>c.ComponentId==part.ComponentId);
        technology.Level=(byte)(part.Level+1);
        Console.WriteLine($"Hull={hull.ShipHullId} role={hull.Role} level={hull.Level} component={part.Component.Name} level {part.Level}->{technology.Level}");
        state.DesignClasses[picket.DesignId]=series;
        string keyForHull=ConstructionSnapshot.TargetKey(series,(int)hull.Role);
        state.AutoUpgrade[keyForHull]=true;state.Targets[keyForHull]=picket.DesignId;
        Require(registry.TryCommit(empire,state,out _),"commit Picket series");
        empire.Policy.SetAutomationLevel(AutomationCategory.ShipDesigns,AutomationLevel.Automated);
        empire.Policy.SetShipDesignAutomationByHull(hull.Role,hull.Level,true);
        empire.ReviewLatestDesignsPerRole();empire.ReviewLatestDesignsPerShipHull();
        var ids=empire.Designs.Select(d=>d.DesignId).ToHashSet();
        empire.CreateShipDesigns(galaxy.StarDate+1,false);
        Require(registry.TryGet(empire,out var after,out _),"read results");
        var added=empire.Designs.Where(d=>!ids.Contains(d.DesignId)&&d.HullId==hull.ShipHullId).ToArray();
        foreach(var d in added)
            Console.WriteLine($"new=#{d.DesignId} series={after.DesignClasses.GetValueOrDefault(d.DesignId)??"none"} mission={getMission(galaxy,d.DesignId)??"none"} component level={d.Components.Where(c=>c.ComponentId==part.ComponentId).Select(c=>(int)c.Level).DefaultIfEmpty(-1).Max()}");
        Console.WriteLine($"origins: Picket #{picket.DesignId} obsolete={picket.Obsolete}, Long #{ranged.DesignId} obsolete={ranged.Obsolete}; new on hull={added.Length}");
        Require(added.Length==2 && added.Count(d=>after.DesignClasses.GetValueOrDefault(d.DesignId)==series&&getMission(galaxy,d.DesignId)=="Picket")==1 &&
            added.Count(d=>!after.DesignClasses.ContainsKey(d.DesignId)&&getMission(galaxy,d.DesignId)=="Long")==1 &&
            added.All(d=>!d.Obsolete&&d.Components.Any(c=>c.ComponentId==part.ComponentId&&c.Level==technology.Level)),
            "both missions each get one upgraded version on the same hull");
        Require(picket.Obsolete&&ranged.Obsolete&&after.Targets[keyForHull]==added.Single(d=>getMission(galaxy,d.DesignId)=="Picket").DesignId&&
            ranged.UpgradeDesignId==added.Single(d=>getMission(galaxy,d.DesignId)=="Long").DesignId,"each origin points to its own next generation");
        var beforeRepeat=empire.Designs.Where(d=>d.HullId==hull.ShipHullId).Select(d=>d.DesignId).Order().ToArray();
        empire.CreateShipDesigns(galaxy.StarDate+2,false);
        Require(beforeRepeat.SequenceEqual(empire.Designs.Where(d=>d.HullId==hull.ShipHullId).Select(d=>d.DesignId).Order()),"repeat review adds no frigate");
        Console.WriteLine("Repeated review: zero new frigates.");
        var nextPicket=added.Single(d=>getMission(galaxy,d.DesignId)=="Picket");
        var nextLong=added.Single(d=>getMission(galaxy,d.DesignId)=="Long");
        Design CopyForSecondSeries(Design original)
        {
            var copy=original.Clone(galaxy);
            copy.DesignId=galaxy.Designs.GetNextId();
            copy.Name="Second series "+original.Name;
            galaxy.Designs.Add(copy);
            empire.Designs.Add(copy);
            return copy;
        }
        var second=CopyForSecondSeries(picket);
        var secondTarget=CopyForSecondSeries(nextPicket);
        Require(registry.TryGet(empire,out var live,out _),"read live series for retrofit test");
        var multi=live.Clone();
        var otherSeries=ConstructionGraph.CreateClass(multi,"Second automatic series");
        multi.DesignClasses[second.DesignId]=otherSeries;
        multi.DesignClasses[secondTarget.DesignId]=otherSeries;
        multi.Targets[ConstructionSnapshot.TargetKey(otherSeries,(int)hull.Role)]=secondTarget.DesignId;
        Require(registry.TryCommit(empire,multi,out _),"commit second automatic series");
        var ship=galaxy.Ships.First(s=>s!=null&&!s.IsDestroyed&&s.EmpireId==empire.EmpireId&&!s.IsBase);
        ship.SetGalaxy(galaxy);
        ship.Role=hull.Role;
        foreach(var (origin,own,foreign) in new[]{(picket,nextPicket,secondTarget),(second,secondTarget,nextPicket)})
        {
            origin.AllowAutoRetrofit=true;
            origin.UpgradeDesignId=-2;
            ship.Design=origin;
            ship.UpdateBonuses();
            ship.RegenerateSummary();
            Require(origin.ResolveRetrofitDesignId(galaxy,empire)==own.DesignId,"automatic target stays within series");
            Require(ship.ShouldRetrofit(empire,own,false),"native ship accepts own automatic target");
            Require(!ship.ShouldRetrofit(empire,foreign,false)&&!ship.ShouldRetrofit(empire,nextLong,false),"native ship rejects other series and unclassified Long");
            origin.UpgradeDesignId=ConstructionGraph.Disabled;
            Require(origin.ResolveRetrofitDesignId(galaxy,empire)<0&&!ship.ShouldRetrofit(empire,own,false),"disabled route prevents automatic retrofit");
            Console.WriteLine($"Auto retrofit #{origin.DesignId} -> #{own.DesignId}; other series #{foreign.DesignId} and unclassified #{nextLong.DesignId} rejected; disabled route rejected.");
            origin.UpgradeDesignId=-2;
        }
    }
}
