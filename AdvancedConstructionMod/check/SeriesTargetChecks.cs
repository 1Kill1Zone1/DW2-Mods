using System.Text.Json;
using AdvancedConstructionMod;

internal static partial class Checks
{
    private static void SeriesDomain()
    {
        var state=new ConstructionSnapshot();string a=ConstructionGraph.CreateClass(state,"Hura"),b=ConstructionGraph.CreateClass(state,"Devourer");
        foreach(int id in new[]{1,2,5,7})state.DesignClasses[id]=a;state.DesignClasses[4]=b;
        var facts=new[]{Fact(1,2,true,date:1),Fact(2,-2,date:20),Fact(4,-2,date:10),Fact(5,int.MinValue,date:2),Fact(7,-2,true,role:3,date:3)};
        string key=ConstructionSnapshot.TargetKey(a,2),bkey=ConstructionSnapshot.TargetKey(b,2);
        var chosen=state.Clone();chosen.Targets[key]=1;
        var preview=ConstructionGraph.TryPreview(state,facts,2,a,[],Compatible,chosen);
        Require(preview.Accepted&&preview.ObsoleteValues?.GetValueOrDefault(1)==false,"manual old target previews explicit reactivation");
        var active=facts.Select(f=>f with {Obsolete=preview.ObsoleteValues?.GetValueOrDefault(f.DesignId,f.Obsolete)??f.Obsolete}).ToArray();
        Require(Resolve(preview.Next!,active.Single(f=>f.DesignId==2),active,4,Compatible).DesignId==1,"explicit old target overrides creation date and permits return to older equipment");
        Require(Resolve(preview.Next!,active.Single(f=>f.DesignId==1),active,4,Compatible).DesignId<0,"selected series target does not retrofit into itself");
        Require(Resolve(preview.Next!,active.Single(f=>f.DesignId==5),active,4,Compatible).DesignId<0,"disabled source path remains disabled with a manual target");
        Require(facts[0].Obsolete&&!state.Targets.Any(),"preview never activates a real object or changes original metadata");
        var none=state.Clone();none.Targets[key]=ConstructionGraph.Disabled;
        Require(Resolve(none,facts[1],facts,4,Compatible).DesignId<0,"explicit no-target does not fall back to global or automatic latest");
        var invalid=state.Clone();invalid.Targets[key]=4;
        Require(!ConstructionGraph.TryPreview(state,facts,2,a,[],Compatible,invalid).Accepted,"foreign-series explicit target rejected before commit");
        Require(Resolve(invalid,facts[1],facts,4,Compatible).DesignId<0,"invalid saved target fails closed instead of choosing another series");
        state.Targets[key]=2;
        string before=Serialized(state);
        var moved=SeriesPlans.Move(state,facts,[2],b);
        Require(moved.DesignClasses[2]==b&&moved.DesignClasses[1]==a&&state.DesignClasses[2]==a,"single transfer changes only the selected project's staged membership");
        Require(moved.Targets[key]==ConstructionGraph.Disabled&&moved.Targets[bkey]==4,"transfer pins the existing destination target and records the departed source target as missing");
        preview=ConstructionGraph.TryPreview(state,facts,2,b,[],Compatible,moved);
        Require(preview.Accepted&&preview.UpgradeValues[1]==-2&&preview.UpgradeValues[5]==int.MinValue,"transfer reconciles incoming enabled links without disabling or enabling other paths");
        Require(Resolve(preview.Next!,facts[1],facts,4,Compatible).DesignId==4,"newer incoming Hura does not replace the selected Devourer destination");
        var all=SeriesPlans.Move(state,facts,new[]{1,2,5,7},b);
        Require(new[]{1,2,5,7}.All(id=>all.DesignClasses[id]==b)&&all.Classes.ContainsKey(a),"whole-series transfer includes obsolete generations and retains empty source identity");
        Require(all.Targets[ConstructionSnapshot.TargetKey(b,3)]==7,"mixed-role series receives independent compatible targets per native role");
        preview=ConstructionGraph.TryPreview(state,facts,2,b,[],Compatible,all);
        Require(preview.Accepted&&preview.ObsoleteValues!.ContainsKey(7),"obsolete destination for another role is explicitly activated in whole-series preview");
        bool refused=false;
        try{SeriesPlans.Move(state,facts.Select(f=>f.DesignId==7?f with {Buildable=false}:f).ToArray(),[2,7],b);}catch(ArgumentException){refused=true;}
        Require(refused&&Serialized(state)==before,"one incompatible role refuses the whole staged transfer without partial membership changes");
        refused=false;try{SeriesPlans.Move(state,facts,[999],b);}catch(ArgumentException){refused=true;}
        Require(refused,"stale transfer project refused");
        refused=false;try{SeriesPlans.Move(state,facts,[2],a);}catch(ArgumentException){refused=true;}
        Require(refused,"transfer to the same series cannot create a duplicate operation");
        foreach(int bad in new[]{ConstructionGraph.Disabled,999,2})
        {
            var broken=state.Clone();broken.Targets[bkey]=bad;
            bool blocked=false;try{SeriesPlans.Move(broken,facts,[1],b);}catch(ArgumentException){blocked=true;}
            Require(blocked,"destination explicit invalid/no-target is never silently replaced: "+bad);
            var correction=SeriesPlans.Move(broken,facts,[1],b,new Dictionary<string,int?>{{bkey,4}});
            Require(correction.Targets[bkey]==4,"deliberate destination correction can recover an invalid/no-target plan");
        }
        var oldDestination=state.Clone();oldDestination.Targets[bkey]=4;
        var obsoleteFacts=facts.Select(f=>f.DesignId==4?f with {Obsolete=true}:f).ToArray();
        var retained=SeriesPlans.Move(oldDestination,obsoleteFacts,[2],b);
        Require(retained.Targets[bkey]==4,"obsolete explicit destination is retained instead of silently selecting incoming newer design");
        var legacy=JsonSerializer.Deserialize<ConstructionSnapshot>("{\"Version\":1,\"Revision\":0,\"Classes\":{\""+a+"\":\"Hura\"},\"DesignClasses\":{\"1\":\""+a+"\",\"2\":\""+a+"\"}}")!;
        legacy.Validate();Require(legacy.Targets.Count==0&&Resolve(legacy,facts[0],facts,4,Compatible).DesignId==2,"version1 metadata without Targets preserves automatic latest behavior");
        var roundtrip=JsonSerializer.Deserialize<ConstructionSnapshot>(Serialized(all))!;roundtrip.Validate();
        Require(roundtrip.Targets.OrderBy(p=>p.Key).SequenceEqual(all.Targets.OrderBy(p=>p.Key)),"explicit per-role target and no-target state serialize exactly");
        var malformed=state.Clone();malformed.Targets[a+":02"]=1;
        bool invalidKey=false;try{malformed.Validate();}catch(InvalidDataException){invalidKey=true;}
        Require(invalidKey,"noncanonical native-role target key fails closed instead of becoming automatic");
    }
}
