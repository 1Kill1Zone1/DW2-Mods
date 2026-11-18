using AdvancedConstructionMod;
using DistantWorlds.Types;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class Checks
{
    private static void AuditSave(string root,string fixture,string output,bool simulateAuto=false)
    {
        Directory.CreateDirectory(output);string before=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
        RuntimeHelpers.RunModuleConstructor(typeof(Galaxy).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds2.DWGame).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.UI.DWControl).Module.ModuleHandle);
        var matrix=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root,"mods","3833406787","DW2PositronicMatrix.dll"));
        var saved=matrix.GetType("DW2PositronicMatrix.SavedData",true)!;
        DW2ModLauncher.Loader.LiveChannels.Channels["saved-data.get"]=AccessTools.Method(saved,"Get").CreateDelegate<Func<object,string,string,string>>();
        DW2ModLauncher.Loader.LiveChannels.Channels["saved-data.set"]=AccessTools.Method(saved,"Set").CreateDelegate<Action<object,string,string,string>>();
        var host=(Stride.Engine.Game)RuntimeHelpers.GetUninitializedObject(typeof(Stride.Engine.Game));GC.SuppressFinalize(host);
        AccessTools.Field(typeof(Stride.Games.GameBase),"<Content>k__BackingField").SetValue(host,new Stride.Core.Serialization.Contents.ContentManager(new Stride.Core.IO.DatabaseFileProviderService(null)));
        Galaxy.Game=host;Galaxy.AllowNewTasks=false;Galaxy.LoadSensitiveRenderingPaused=true;
        using var input=new BinaryReader(File.OpenRead(fixture));var galaxy=new Galaxy().ReadFromStream(input,out _,out var failures);
        if(failures.Count!=0)throw new InvalidDataException(string.Join(";",failures));
        var empire=galaxy.Empires.GetPlayer();var store=new MatrixStorage(galaxy,empire.EmpireId);var state=store.Read();
        foreach(var port in empire.Ships.Where(s=>s.Role.ToString().Contains("SpacePort",StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"PORT {port.Name} role={port.Role} hullBuilt={port.HullConstructionLevel:P1} underConstructionOrRepair={port.IsUnderConstructionOrRepair} location={port.LocationId}");
        foreach(var m in empire.Messages.Concat(empire.RecentMessages).Concat(empire.EvaluationQueue).Distinct())
        {
            string summary=$"{m.Title} {m.Text} {m.GameTask?.Text}";
            if(summary.Contains("spaceport",StringComparison.OrdinalIgnoreCase) || summary.Contains("space port",StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"PORT MESSAGE {m.GameTaskExecution} {m.GameTask?.Type} {summary}");
        }
        Console.WriteLine("ACM saved series="+(state?.Classes.Count??0));
        var designs=empire.Designs.Where(d=>d.Name.Contains("Xura",StringComparison.OrdinalIgnoreCase)||d.Name.Contains("Hura",StringComparison.OrdinalIgnoreCase)||d.Name.Contains("Devour",StringComparison.OrdinalIgnoreCase)).Select(d=>
        {
            string? series=state?.DesignClasses.GetValueOrDefault(d.DesignId);var hull=d.GetShipHull(galaxy);int role=(int)d.GetRole(galaxy);
            return new {d.DesignId,d.Name,d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit,d.StarDate,d.HullId,Role=role,HullLevel=hull?.Level,
                AutomaticHull=hull!=null&&empire.Policy.GetShipDesignAutomationByHull(hull.Role,hull.Level),SeriesId=series,Series=series==null?null:state?.Classes.GetValueOrDefault(series),
                ExplicitTarget=series!=null&&state!.Targets.TryGetValue(ConstructionSnapshot.TargetKey(series,role),out int target)?(int?)target:null};
        }).ToArray();
        var report=new {FixtureSha256=before,galaxy.StarDate,Player=empire.Name,MetadataVersion=state?.Version,Revision=state?.Revision,Designs=designs};
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(output,"save-audit.json"),json);Console.WriteLine(json);
        if(simulateAuto)
        {
            galaxy.RefreshSystemArray();Galaxy.ComponentsStatic=galaxy.Components;Galaxy.ResearchProjectsStatic=galaxy.ResearchProjects;Galaxy.PlanetaryFacilitiesStatic=galaxy.PlanetaryFacilities;
            empire.Research.Update(galaxy,empire,galaxy.Races.GetById(empire.DominantRaceId),empire.Policy,empire.IsPirate,empire.IsPlayer,out _);
            var oldIds=empire.Designs.Select(d=>d.DesignId).ToHashSet();var oldFlags=empire.Designs.ToDictionary(d=>d.DesignId,d=>d.Obsolete);
            foreach(var hull in empire.Research.ResearchedShipHulls)empire.Policy.SetShipDesignAutomationByHull(hull.Role,hull.Level,false);
            empire.Policy.SetShipDesignAutomationByHull(ShipRole.Escort,0,true);
            Entry.Init();
            Require(SeriesAutomation.EnsureModes(galaxy,empire,out var modes,out _),"owner copy loads and initializes independent modes");
            var ownSeries=empire.Designs.Where(d=>!d.Obsolete&&modes.DesignClasses.ContainsKey(d.DesignId)).ToArray();
            var xura=ownSeries.Where(d=>d.Name.Contains("Xura",StringComparison.OrdinalIgnoreCase)||d.Name.Contains("Hura",StringComparison.OrdinalIgnoreCase)).OrderByDescending(d=>d.StarDate).ThenByDescending(d=>d.DesignId).FirstOrDefault();
            var devourer=ownSeries.Where(d=>d.Name.Contains("Devour",StringComparison.OrdinalIgnoreCase)).OrderByDescending(d=>d.StarDate).ThenByDescending(d=>d.DesignId).FirstOrDefault();
            Require(xura!=null&&devourer!=null,"owner copy has both active classified branches");
            Require(SeriesAutomation.SetMode(galaxy,empire,xura!,true,out _)&&SeriesAutomation.SetMode(galaxy,empire,devourer!,false,out _),"owner copy selects Xura Auto and Devourer Manual without changing native policy");
            empire.Policy.SetAutomationLevel(AutomationCategory.ShipDesigns,(AutomationLevel)0);
            var beforeComponents=ownSeries.ToDictionary(d=>d.DesignId,d=>d.Components.ToArray());
            empire.CreateShipDesigns(galaxy.StarDate+1,true);
            var afterState=store.Read();
            var result=new {InMemoryOnly=true,MetadataUnchanged=JsonSerializer.Serialize(state)==JsonSerializer.Serialize(afterState),
                NewDesigns=empire.Designs.Where(d=>!oldIds.Contains(d.DesignId)).Select(d=>new{d.DesignId,d.Name,d.Obsolete,Series=afterState?.DesignClasses.GetValueOrDefault(d.DesignId)}).ToArray(),
                ChangedFlags=empire.Designs.Where(d=>oldFlags.TryGetValue(d.DesignId,out bool was)&&was!=d.Obsolete).Select(d=>new{d.DesignId,d.Name,Before=oldFlags[d.DesignId],After=d.Obsolete}).ToArray(),
                Targets=afterState?.Targets,AutoUpgrade=afterState?.AutoUpgrade,XuraStatus=SeriesAutomation.Description(galaxy,empire,xura!),DevourerStatus=SeriesAutomation.Description(galaxy,empire,devourer!)};
            Require(empire.Designs.Where(d=>!oldIds.Contains(d.DesignId)).All(d=>afterState!.DesignClasses.ContainsKey(d.DesignId)),"native review on owner copy creates no unclassified template");
            Require(!devourer!.Obsolete&&devourer.Components.SequenceEqual(beforeComponents[devourer.DesignId]),"owner copy Manual Devourer remains active with unchanged equipment");
            string simulation=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(output,"automatic-simulation.json"),simulation);Console.WriteLine(simulation);
        }
        foreach(var value in Enum.GetValues<AutomationCategory>())Console.WriteLine("AUTOMATION "+(int)value+" "+value+" = "+empire.Policy.GetAutomationLevel(value));
        foreach(var field in typeof(Design).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(f=>f.Name.Contains("Component")||f.Name.Contains("Bay")))Console.WriteLine("DESIGN FIELD "+field);
        foreach(var type in new[]{typeof(Empire),typeof(Design)})
        foreach(var method in AccessTools.GetDeclaredMethods(type).Where(m=>type==typeof(Empire)?m.Name is "CreateShipDesigns" or "CheckAddDesign" or "ExecuteTaskUpdateShipDesign":m.Name is "Upgrade" or "UpgradeComponents" or "GenerateNewDesign"))
            File.WriteAllLines(Path.Combine(output,$"{type.Name}-{method.Name}-{method.GetParameters().Length}.il.txt"),PatchProcessor.GetOriginalInstructions(method).Select((x,i)=>$"{i:D5} {x}"));
        if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)))!=before)throw new IOException("Audit fixture changed");
        Console.WriteLine("PASS read-only campaign audit. No game, profile or real save writes.");
    }
}
