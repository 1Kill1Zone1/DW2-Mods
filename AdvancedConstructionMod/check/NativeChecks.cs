using AdvancedConstructionMod;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using DistantWorlds.Types;
using HarmonyLib;

internal static partial class Checks
{
    private const string Owner = "AdvancedConstructionMod";
    private static readonly Assembly Module = typeof(Entry).Assembly;
    private static Func<object,string,string,string> getSaved=null!;
    private static Action<object,string,string,string> setSaved=null!;
    private static string? rejectedKey;
    private static bool throwAfterHead;
    private static object Storage(Galaxy galaxy, short empire) => Activator.CreateInstance(Module.GetType("AdvancedConstructionMod.MatrixStorage",true)!,galaxy,empire)!;
    private static ConstructionSnapshot? Read(object store) => (ConstructionSnapshot?)AccessTools.Method(store.GetType(),"Read").Invoke(store,null);
    private static void Write(object store, ConstructionSnapshot state) => AccessTools.Method(store.GetType(),"Write").Invoke(store,[state]);
    private static void Forget(Galaxy galaxy) => AccessTools.Method(Module.GetType("AdvancedConstructionMod.ConstructionCampaigns"),"Forget").Invoke(null,[galaxy]);
    private static DesignFact[] Facts(Galaxy galaxy,Empire empire) => empire.Designs.Select(d=>(DesignFact)AccessTools.Method(Module.GetType("AdvancedConstructionMod.RetrofitRuntime"),"Fact").Invoke(null,[galaxy,empire,d])!).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Native(string root,string fixture)
    {
        string sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
        RuntimeHelpers.RunModuleConstructor(typeof(Galaxy).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds2.DWGame).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.UI.DWControl).Module.ModuleHandle);
        var matrix=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root,"mods","3833406787","DW2PositronicMatrix.dll"));
        var savedType=matrix.GetType("DW2PositronicMatrix.SavedData",true)!;
        getSaved=AccessTools.Method(savedType,"Get").CreateDelegate<Func<object,string,string,string>>();
        setSaved=AccessTools.Method(savedType,"Set").CreateDelegate<Action<object,string,string,string>>();
        DW2ModLauncher.Loader.LiveChannels.Channels["saved-data.get"]=getSaved;
        DW2ModLauncher.Loader.LiveChannels.Channels["saved-data.set"]=(Action<object,string,string,string>)((g,owner,key,value)=>
        {
            if(key==rejectedKey)return;
            setSaved(g,owner,key,value);
            if(throwAfterHead && key.EndsWith(".head",StringComparison.Ordinal)) {throwAfterHead=false;throw new IOException("Injected header write failure");}
        });
        var host=(Stride.Engine.Game)RuntimeHelpers.GetUninitializedObject(typeof(Stride.Engine.Game));
        GC.SuppressFinalize(host);
        AccessTools.Field(typeof(Stride.Games.GameBase),"<Content>k__BackingField").SetValue(host,new Stride.Core.Serialization.Contents.ContentManager(new Stride.Core.IO.DatabaseFileProviderService(null)));
        Galaxy.Game=host;Galaxy.AllowNewTasks=false;Galaxy.LoadSensitiveRenderingPaused=true;
        using var input=new BinaryReader(File.OpenRead(fixture));
        var galaxy=new Galaxy().ReadFromStream(input,out var header,out var failures);
        Require(failures.Count==0,"native fixture loads without errors");
        galaxy.RefreshSystemArray();
        Galaxy.ComponentsStatic=galaxy.Components;Galaxy.ResearchProjectsStatic=galaxy.ResearchProjects;Galaxy.PlanetaryFacilitiesStatic=galaxy.PlanetaryFacilities;
        var empire=galaxy.Empires.GetPlayer();
        empire.Research.Update(galaxy,empire,galaxy.Races.GetById(empire.DominantRaceId),empire.Policy,empire.IsPirate,empire.IsPlayer,out _);
        var store=Storage(galaxy,empire.EmpireId);
        Require(Read(store)==null,"new campaign has no ACM metadata");
        var state=new ConstructionSnapshot();
        string ion=ConstructionGraph.CreateClass(state,"Hura"),beam=ConstructionGraph.CreateClass(state,"Little Devourer");
        var basis=empire.Designs.First(d=>!d.Obsolete&& d.GetRole(galaxy)==ShipRole.Escort && Design.CheckDesignIsValid(galaxy,empire,d) && d.CheckEmpireHasResearchedAllTech(galaxy,empire,d.GetShipHull(galaxy)));
        var designs=new List<Design>();
        int nextId=galaxy.Designs.Max(d=>d.DesignId)+100;
        for(int i=0;i<5;i++)
        {
            var copy=basis.Clone(galaxy);copy.DesignId=nextId++;copy.Name=i<2?"Hura "+i:"Little Devourer "+i;copy.Obsolete=false;
            copy.StarDate=i==4?basis.StarDate-1:basis.StarDate+i+100;copy.AllowAutoRetrofit=true;copy.UpgradeDesignId=int.MinValue;
            galaxy.Designs.Add(copy);empire.Designs.Add(copy);designs.Add(copy);
        }
        var h1=designs[0];var h2=designs[1];var b1=designs[2];var b2=designs[3];var unclassified=designs[4];
        state.DesignClasses[h1.DesignId]=state.DesignClasses[h2.DesignId]=ion;
        state.DesignClasses[b1.DesignId]=state.DesignClasses[b2.DesignId]=beam;
        Write(store,state);
        Require(Serialized(Read(store)!)==Serialized(state),"real Matrix class mapping roundtrip");
        string prefix="construction."+empire.EmpireId;
        string head=getSaved(galaxy,Owner,prefix+".head");
        var changed=state.Clone();changed.Classes[ion]="Hura Renamed";
        string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
        rejectedKey=$"{prefix}.{bank}.0";
        ExpectFailure(()=>Write(store,changed),"rejected chunk fails instead of committing half metadata");rejectedKey=null;
        Require(getSaved(galaxy,Owner,prefix+".head")==head && Serialized(Read(store)!)==Serialized(state),"rejected chunk preserves published bank");
        throwAfterHead=true;
        ExpectFailure(()=>Write(store,changed),"failure after header write reported");
        Require(getSaved(galaxy,Owner,prefix+".head")==head && Serialized(Read(store)!)==Serialized(state),"partial header commit rolls back to prior complete graph");
        Entry.Init();Entry.Init();
        var resolver=AccessTools.Method(typeof(Design),nameof(Design.ResolveRetrofitDesignId),[typeof(Galaxy),typeof(Empire),typeof(Design).MakeByRefType()]);
        Require(Harmony.GetPatchInfo(resolver)!.Owners.Count(o=>o=="local.dw2.advanced-construction")==1,"runtime hooks initialize idempotently");
        foreach(int mode in new[]{-1,-2,-3})
        {
            h1.UpgradeDesignId=mode;b1.UpgradeDesignId=mode;
            int resolved=h1.ResolveRetrofitDesignId(galaxy,empire,out var hTarget);
            if(resolved!=h2.DesignId)
            {
                Console.WriteLine("RESOLVE-DIAGNOSTIC mode="+mode+" actual="+resolved+" expected="+h2.DesignId);
                foreach(var f in Facts(galaxy,empire).Where(f=>designs.Any(d=>d.DesignId==f.DesignId)))Console.WriteLine(f);
            }
            Require(resolved==h2.DesignId&&hTarget==h2,"native latest mode "+mode+" keeps Hura class");
            Require(b1.ResolveRetrofitDesignId(galaxy,empire,out var bTarget)==b2.DesignId&&bTarget==b2,"native latest mode "+mode+" keeps Little Devourer class");
        }
        h1.UpgradeDesignId=b2.DesignId;
        Require(h1.ResolveRetrofitDesignId(galaxy,empire,out var invalid)==h2.DesignId && invalid==h2,"native stored cross-class target is overridden by latest own-series design");
        h1.UpgradeDesignId=h2.DesignId;
        Require(h1.ResolveRetrofitDesignId(galaxy,empire)==h2.DesignId,"native explicit same-class link works");
        h1.UpgradeDesignId=int.MinValue;
        Require(h1.ResolveRetrofitDesignId(galaxy,empire)<0,"native disabled class path stays disabled");
        Require(ConstructionCommands.TryApply(galaxy,empire,h2,state.Revision,ion,[new(h1.DesignId,h2.DesignId,true),new(unclassified.DesignId,h2.DesignId,true)],Facts(galaxy,empire),out var applied,out var error),"native Apply links group: "+error);
        Require(h1.UpgradeDesignId==h2.DesignId&&unclassified.UpgradeDesignId==h2.DesignId,"native Apply updates exact arrows");
        var after=Read(store)!;
        Require(after.DesignClasses[unclassified.DesignId]==ion,"native Apply persists formerly unclassified source");
        Require(!ConstructionCommands.TryApply(galaxy,empire,h2,state.Revision,ion,[new(b1.DesignId,h2.DesignId,true)],Facts(galaxy,empire),out _,out _),"stale native modal Apply rejected");
        Require(b1.UpgradeDesignId==-3,"stale Apply preserves other branch");
        setSaved(galaxy,"acm-check-other-owner","sentinel","keep-me");
        using var buffer=new MemoryStream();
        using(var writer=new BinaryWriter(buffer,System.Text.Encoding.UTF8,true))galaxy.WriteToStream(writer,header);
        buffer.Position=0;
        using var reader=new BinaryReader(buffer,System.Text.Encoding.UTF8,true);
        var reloaded=new Galaxy().ReadFromStream(reader,out _,out failures);
        Require(failures.Count==0,"actual native campaign save and reload completes");
        var restored=Read(Storage(reloaded,empire.EmpireId))!;
        Require(Serialized(restored)==Serialized(after),"actual save reload retains classes and revision");
        Require(reloaded.Designs.GetById(h1.DesignId).UpgradeDesignId==h2.DesignId,"actual save reload retains native arrows");
        Require(getSaved(reloaded,"acm-check-other-owner","sentinel")=="keep-me","other Matrix owner survives ACM save unchanged");
        RetrofitChecks(galaxy,empire,h1,h2,b2,root);
        GenerationChecks(galaxy,empire,h1,h2);
        AtomicityChecks(galaxy,empire,h2);
        UiChecks(galaxy,empire,h1,h2);
        SeriesColumnChecks(galaxy,empire);
        ReviewRegressionChecks(galaxy,empire,h2);
        SeriesNativeChecks(galaxy,empire,h2);
        using(var finalBytes=new MemoryStream())
        {
            using(var writer=new BinaryWriter(finalBytes,System.Text.Encoding.UTF8,true))galaxy.WriteToStream(writer,header);
            finalBytes.Position=0;using var finalReader=new BinaryReader(finalBytes,System.Text.Encoding.UTF8,true);
            var finalReload=new Galaxy().ReadFromStream(finalReader,out _,out var finalErrors);
            Require(finalErrors.Count==0&&Serialized(Read(Storage(finalReload,empire.EmpireId))!)==Serialized(Read(store)!),"UI-authored classes, renames and paths survive actual native save/load");
        }
        using(var failedBytes=new MemoryStream())using(var failedWriter=new BinaryWriter(failedBytes))SaveLockCheck(galaxy,()=>galaxy.WriteToStream(failedWriter,header));
        SaveChecks(galaxy,empire,root);
        string validHead=getSaved(galaxy,Owner,prefix+".head");
        setSaved(galaxy,Owner,prefix+".head","broken-json");Forget(galaxy);
        Require(h1.ResolveRetrofitDesignId(galaxy,empire,out invalid)<0&&invalid==null,"corrupt storage never falls back to global latest");
        Require(!ConstructionCommands.TryApply(galaxy,empire,h2,after.Revision,ion,[],Facts(galaxy,empire),out _,out _),"corrupt metadata blocks mutation");
        Require(getSaved(galaxy,Owner,prefix+".head")=="broken-json","corrupt metadata retained for recovery");
        setSaved(galaxy,Owner,prefix+".head",validHead);Forget(galaxy);
        var priorVariables=galaxy.GlobalVariables;galaxy.GlobalVariables=new(priorVariables){["mod."+Owner+"."+prefix+".head"]=true};Forget(galaxy);
        Require(h1.ResolveRetrofitDesignId(galaxy,empire,out invalid)<0&&invalid==null,"non-string stored metadata header is corruption, never an empty unclassified campaign");
        galaxy.GlobalVariables=priorVariables;Forget(galaxy);
        Require(sourceHash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))),"source campaign hash remains unchanged");
        Console.WriteLine("Native checks only: no live game, profile, installed mod or real save mutation.");
    }
    private static void ExpectFailure(Action action,string name)
    {
        bool failed=false;try{action();}catch(Exception){failed=true;}Require(failed,name);
    }
}
