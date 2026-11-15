using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using DistantWorlds.Types;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

AssemblyLoadContext.Default.Resolving += (_, n) =>
{
    var candidate = Path.Combine(args[0], n.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

Probe.Run(Path.GetFullPath(args[1]));

static class Probe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(string savePath)
    {
        var beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(savePath)));
        RuntimeHelpers.RunModuleConstructor(typeof(Galaxy).Module.ModuleHandle);
        var host = (Stride.Engine.Game)RuntimeHelpers.GetUninitializedObject(typeof(Stride.Engine.Game));
        typeof(Stride.Games.GameBase).GetField("<Content>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, new ContentManager(new DatabaseFileProviderService(null)));
        Galaxy.Game = host;
        Galaxy.AllowNewTasks = false;
        Galaxy.LoadSensitiveRenderingPaused = true;
        Galaxy galaxy;
        using (var reader = new BinaryReader(File.OpenRead(savePath)))
            galaxy = new Galaxy().ReadFromStream(reader, out _, out var errors);
        var empire = galaxy.Empires.Single(e => e.IsPlayer);
        Console.WriteLine($"save={Path.GetFileName(savePath)} date={galaxy.StarDate} player={empire.Name} dominantRace={empire.DominantRaceId}");
        foreach (var population in empire.GetColonyRaces())
            Console.WriteLine($"population race={population.RaceId} amount={population.Amount} qualifies500M={population.Amount >= 500_000_000}");
        foreach (var id in new short[] { 17, 5387, 42, 67, 68 })
        {
            var project = empire.Research.Projects.FirstOrDefault(p => p.ResearchProjectId == id);
            Console.WriteLine(project == null
                ? $"project={id} absent"
                : $"project={id} name={project.Definition.Name} visible={project.Visible} enabled={project.Enabled} researched={project.Researched} directIkkuro={project.CanRaceResearch(empire.GetDominantRace())}");
        }
        var afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(savePath)));
        if (beforeHash != afterHash) throw new Exception("save file changed");
        Console.WriteLine("save hash unchanged");
    }
}
