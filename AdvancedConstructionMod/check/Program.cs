using AdvancedConstructionMod;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

[assembly:System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]

string gameRoot = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "C:/Games/Steam/steamapps/common/Distant Worlds 2");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string file = Path.Combine(gameRoot, name.Name + ".dll");
    return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
};
return Checks.Run(args.Skip(1).ToArray(), gameRoot);

internal static partial class Checks
{
    private static int assertions;
    internal static void Require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        assertions++;
    }
    private static bool Compatible(DesignFact source, DesignFact target) => source.Role == target.Role;
    private static DesignFact Fact(int id, int path = -2, bool obsolete = false, short owner = 1,
        int role = 2, short hull = 10, long date = 1, bool buildable = true) =>
        new(id, owner, hull, role, obsolete, buildable, date, path);
    private static string Serialized(ConstructionSnapshot state) => JsonSerializer.Serialize(state);
    private static RetrofitResolution Resolve(ConstructionSnapshot state, DesignFact source, IEnumerable<DesignFact> candidates,
        int nativeId, Func<DesignFact,DesignFact,bool> compatible) => ConstructionGraph.ResolveCandidate(state,source,candidates,nativeId,compatible);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string[] modes, string gameRoot)
    {
        if(modes.Length==3&&modes[0]=="--audit-save"){AuditSave(gameRoot,Path.GetFullPath(modes[1]),Path.GetFullPath(modes[2]));return 0;}
        if(modes.Length==3&&modes[0]=="--audit-auto"){AuditSave(gameRoot,Path.GetFullPath(modes[1]),Path.GetFullPath(modes[2]),true);return 0;}
        Domain();
        SeriesDomain();
        if(modes.Length==1&&modes[0]=="--visibility")
        {
            RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.Types.Galaxy).Module.ModuleHandle);
            RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds2.DWGame).Module.ModuleHandle);
            RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.UI.DWControl).Module.ModuleHandle);
            using var metrics=new FontMetrics();
            var button=new DistantWorlds.UI.DWButton();
            DistantWorlds.UI.DWControl.SetupButton(ref button,"ACM.Probe",new(180,28),new(0,0),"Construction Class",(_,_)=>{},false);
            Console.WriteLine($"Native SetupButton: Visible={button.Visible}; Opacity={button.Opacity}; TargetOpacity={button.TargetOpacity}; Enabled={button.Enabled}; ShowTexture={button.ShowTexture}; CheckVisible={button.CheckVisible()}");
            return 0;
        }
        if(modes.Length==1 && modes[0]=="--metadata")
        {
            RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.Types.Galaxy).Module.ModuleHandle);
            foreach(var f in typeof(DistantWorlds.Types.ConstructionYard).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))Console.WriteLine("YARD "+f);
            foreach(var f in typeof(DistantWorlds.Types.ConstructionSystem).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))Console.WriteLine("SYSTEM "+f);
            foreach(var method in typeof(DistantWorlds.Types.ConstructionSystem).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Where(m=>m.Name.Contains("Retrofit")||m.Name=="AddShipToConstruct"))Console.WriteLine(method+" PARAMS "+string.Join(",",method.GetParameters().Select(p=>p.Name+":"+p.ParameterType.Name)));
            return 0;
        }
        if(modes.Length==2 && modes[0]=="--native")Native(gameRoot,Path.GetFullPath(modes[1]));
        Console.WriteLine($"PASS: {assertions} Advanced Construction assertions ({(modes.Length>0?"domain + native":"domain")}).");
        return 0;
    }

    private static void Domain()
    {
        var state = new ConstructionSnapshot();
        string hura = ConstructionGraph.CreateClass(state, "Ion Escort");
        string devourer = ConstructionGraph.CreateClass(state, "Beam Escort");
        Require(hura != devourer, "class identities differ");
        state.DesignClasses[1] = hura;
        state.DesignClasses[2] = hura;
        state.DesignClasses[3] = devourer;
        state.DesignClasses[4] = devourer;
        var facts = new[] { Fact(1, 2, obsolete:true), Fact(2,date:2), Fact(3,4,obsolete:true), Fact(4,date:4), Fact(5), Fact(6,owner:2), Fact(7,role:3) };
        string before = Serialized(state);
        var incoming = ConstructionGraph.TryPreview(state, facts, 2, hura,
            [new(1,2,true), new(5,2,true)], Compatible);
        Require(incoming.Accepted, "old and unclassified sources accepted");
        Require(incoming.Next!.DesignClasses[5] == hura, "unclassified source adopts target class");
        Require(incoming.UpgradeValues[1] == 2 && incoming.UpgradeValues[5] == 2, "both incoming links point to Hura II");
        Require(incoming.UpgradeValues[3] == 4, "Little Devourer branch unaffected");
        Require(Serialized(state) == before, "preview and cancel leave original graph intact");
        var repeated = ConstructionGraph.TryPreview(incoming.Next, facts.Select(f=>f with { UpgradeDesignId=incoming.UpgradeValues[f.DesignId] }), 2,hura,
            [new(1,2,true),new(5,2,true)],Compatible);
        Require(repeated.Accepted && Serialized(repeated.Next!) == Serialized(incoming.Next), "repeat selection idempotent");
        var uncheckedSource = ConstructionGraph.TryPreview(state,facts,2,hura,[new(1,2,false)],Compatible);
        Require(uncheckedSource.Accepted && uncheckedSource.UpgradeValues[1] == int.MinValue, "unchecked incoming path disabled");
        Require(uncheckedSource.Next!.DesignClasses[1] == hura, "unchecking retains class");
        Require(uncheckedSource.UpgradeValues[5] == -2, "initially unchecked source untouched");
        Reject([new(3,2,true)], "another class");
        Reject([new(6,2,true)], "foreign source");
        Reject([new(7,2,true)], "other native role");
        Reject([new(2,2,true)], "self cycle");
        Reject([new(999,2,true)], "missing source");
        Require(!ConstructionGraph.TryPreview(state,facts,999,hura,[],Compatible).Accepted, "missing target rejected");
        Require(!ConstructionGraph.TryPreview(state,facts,1,hura,[],Compatible).Accepted, "obsolete target rejected");
        Require(!ConstructionGraph.TryPreview(state,facts,2,"missing",[],Compatible).Accepted, "deleted or stale class rejected");
        Require(!ConstructionGraph.TryPreview(state,facts,2,null,[new(5,2,true)],Compatible).Accepted, "link cannot silently use no class");
        Require(!ConstructionGraph.TryPreview(state,facts,2,hura,[new(5,2,true)],(_,_)=>false).Accepted, "native hull incompatibility respected");
        Require(Serialized(state) == before, "all rejected previews leave class data intact");

        var cycleFacts = new[] { Fact(1,2),Fact(2,3),Fact(3,int.MinValue) };
        var cycleState = new ConstructionSnapshot();
        string cycleClass=ConstructionGraph.CreateClass(cycleState,"Cycle");
        Require(!ConstructionGraph.TryPreview(cycleState,cycleFacts,1,cycleClass,[new(3,1,true)],Compatible).Accepted,"transitive cycle rejected");

        var direct = Resolve(state,Fact(1,2),facts,4,Compatible);
        Require(direct.DesignId == 2,"explicit Hura remains Hura despite latest Beam");
        Require(Resolve(state,Fact(1,4),facts,4,Compatible).DesignId==2,"series latest overrides a stored cross-class target without crossing series");
        Require(Resolve(state,Fact(1,int.MinValue),facts,4,Compatible).DesignId==int.MinValue,"disabled remains disabled");
        Require(Resolve(state,Fact(5),facts,4,Compatible).DesignId==4,"unclassified preserves native result");
        Require(Resolve(state,Fact(1,2),facts.Select(f=>f.DesignId==2?f with { Buildable=false }:f),4,Compatible).DesignId==int.MinValue,"unbuildable target rejected");
        Require(Resolve(state,Fact(1,2),facts,4,(_,_)=>false).DesignId==int.MinValue,"runtime hull compatibility respected");
        var implicitSource=Fact(1,-2);
        var latest=ConstructionGraph.ResolveCandidate(state,implicitSource,facts,4,Compatible);
        Require(latest.DesignId==2,"latest policy selects a compatible same-series project");
        var newestFacts=facts.Append(Fact(8,date:8)).ToArray();var newestState=state.Clone();newestState.DesignClasses[8]=hura;
        Require(Resolve(newestState,Fact(1,2),newestFacts,4,Compatible).DesignId==8,"series policy overrides the old explicit same-series target");
        Require(Resolve(newestState,Fact(1,-1),newestFacts,4,Compatible).DesignId==8,"series policy overrides native latest-same-hull mode");
        Require(Resolve(newestState,Fact(8,-3,date:8),newestFacts,4,Compatible).DesignId<0,"latest design never retrofits to itself");
        Require(Resolve(newestState,Fact(8,2,date:9),newestFacts,4,Compatible).DesignId<0,"newest source cannot downgrade when every active target is older");
        Require(Resolve(newestState,Fact(8,2,date:2),facts,4,Compatible).DesignId<0,"obsolete source cannot downgrade to a lower ID with the same creation date");
        var adopted=newestState.Clone();adopted.DesignClasses[5]=hura;
        Require(Resolve(adopted,Fact(1,2),newestFacts.Select(f=>f.DesignId==5?f with {StarDate=9}:f),4,Compatible).DesignId==5,"adopting a newer project makes it the effective latest series target");
        var uncheckedImplicit=ConstructionGraph.TryPreview(state,[implicitSource,Fact(2)],2,hura,[new(1,2,false,true)],Compatible);
        Require(uncheckedImplicit.Accepted && uncheckedImplicit.UpgradeValues[1]==int.MinValue,"uncheck effective native incoming disables path");
        Require(!ConstructionGraph.TryPreview(state,facts,2,hura,[new(5,4,true)],Compatible).Accepted,"mismatched target rejects stale selection");
        var generations=state.Clone();generations.DesignClasses[8]=hura;
        var generationFacts=new[]{Fact(1,2,true),Fact(2,8,true),Fact(8,int.MinValue,date:8)};
        Require(Resolve(generations,generationFacts[0],generationFacts,4,Compatible).DesignId==8,"oldest generation follows obsolete intermediate to active same-class target");
        generations.DesignClasses[2]=devourer;
        Require(Resolve(generations,generationFacts[0],generationFacts,4,Compatible).DesignId==8,"series latest ignores an obsolete cross-series native intermediate");

        string savedId=hura;
        Require(ConstructionGraph.TryRename(state,hura,"Ion Escort Mk II",out _),"rename accepted");
        Require(state.DesignClasses[1]==savedId && state.Classes[hura]=="Ion Escort Mk II","rename keeps stable class links");
        var restored=JsonSerializer.Deserialize<ConstructionSnapshot>(Serialized(state))!;
        restored.Validate();
        Require(restored.DesignClasses[3]==devourer && restored.Classes[hura]=="Ion Escort Mk II","JSON class mapping roundtrip");
        Require(!ConstructionGraph.TryRename(state,hura,"  ",out _),"empty rename rejected");
        Require(!ConstructionGraph.TryRename(state,"missing","X",out _),"missing class rename rejected");
        bool invalid=false;
        try { ConstructionGraph.CreateClass(state,new string('x',81)); } catch(ArgumentException) { invalid=true; }
        Require(invalid,"oversized name rejected");
        var corrupt=state.Clone();corrupt.DesignClasses[100]="unknown";
        invalid=false;try{corrupt.Validate();}catch(InvalidDataException){invalid=true;}
        Require(invalid,"dangling mapping rejects corrupt data");
        invalid=false;try{new ConstructionSnapshot{Classes=null!}.Validate();}catch(InvalidDataException){invalid=true;}
        Require(invalid,"null class dictionary rejected as invalid metadata");
        Require(!ConstructionGraph.TryPreview(state,[Fact(1),Fact(1),Fact(2)],2,hura,[],Compatible).Accepted,"duplicate native design identity rejected without mutation");
        var chain=Enumerable.Range(1,5000).Select(id=>Fact(id,id==5000?int.MinValue:id+1)).ToArray();
        Require(ConstructionGraph.TryPreview(new ConstructionSnapshot(),chain,5000,null,[],Compatible).Accepted,"long acyclic native path validates without recursive stack growth");
        chain[^1]=chain[^1] with {UpgradeDesignId=1};
        Require(!ConstructionGraph.TryPreview(new ConstructionSnapshot(),chain,5000,null,[],Compatible).Accepted,"long transitive cycle rejected");

        void Reject(ConstructionChange[] changes,string name)
        {
            var rejected=ConstructionGraph.TryPreview(state,facts,2,hura,changes,Compatible);
            Require(!rejected.Accepted && rejected.Next==null && rejected.UpgradeValues.Count==0,name+" rejected atomically");
        }
    }
}
