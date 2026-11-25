using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

if (args.Length != 4) throw new ArgumentException("Check <plan> <fixture> <matrix-report-or-error-text> <mode>");
var plan = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
string engine = plan["nativeRoot"]!.GetValue<string>();
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string path = Path.Combine(engine, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
Run(plan, args[1], args[2], args[3]);

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run(JsonObject plan, string fixture, string result, string mode)
{
    RuntimeHelpers.RunModuleConstructor(typeof(Galaxy).Module.ModuleHandle);
    if (mode is "camera" or "camera-hotkey" or "map-notice" or "camera-dialog")
    {
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds2.DWGame).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(DistantWorlds.UI.EmpireSectionList).Module.ModuleHandle);
    }
    var host = (Stride.Engine.Game)RuntimeHelpers.GetUninitializedObject(typeof(Stride.Engine.Game));
    GC.SuppressFinalize(host);
    typeof(Stride.Games.GameBase).GetField("<Content>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(host, new ContentManager(new DatabaseFileProviderService(null)));
    Galaxy.Game = host;
    Galaxy.AllowNewTasks = false;
    Galaxy.LoadSensitiveRenderingPaused = true;

    string logs = plan["logs"]!.GetValue<string>();
    Directory.CreateDirectory(logs);
    var loader = AssemblyLoadContext.Default.LoadFromAssemblyPath(plan["loader"]!.GetValue<string>());
    var entry = loader.GetType("DW2ModLauncher.Loader.Entry", true)!;
    string log = Path.Combine(logs, "cold-loader.log");
    File.WriteAllText(log, "");
    entry.GetField("_logPath", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, log);
    var load = entry.GetMethod("LoadOne", BindingFlags.Static | BindingFlags.NonPublic)!;
    var descriptor = loader.GetType("DW2ModLauncher.Loader.LoaderManifestEntry", true)!;
    int expected = 0;
    foreach (var node in plan["mods"]!.AsArray())
    {
        var mod = node!.AsObject();
        var options = JsonNode.Parse(File.ReadAllText(mod["preview"]!.GetValue<string>()))!.AsObject();
        foreach (string key in new[] { "OutputDirectory", "LogFolder", "LogDirectory", "StorageFolder" })
            options[key] = logs;
        options["LogToSessionFolder"] = false;
        foreach (var dll in mod["dlls"]!.AsArray())
        {
            object row = Activator.CreateInstance(descriptor)!;
            void Set(string key, object value) => descriptor.GetProperty(key)!.SetValue(row, value);
            string path = dll!["path"]!.GetValue<string>();
            Set("DllPath", path);
            Set("EntryType", dll["entry"]!.GetValue<string>());
            Set("SettingsJson", options.ToJsonString());
            Set("DisplayName", mod["name"]!.GetValue<string>());
            Set("ModKey", mod["key"]!.GetValue<string>());
            Set("ModVersion", mod["meta"]!["version"]!.GetValue<string>());
            Set("ModFolder", Path.GetDirectoryName(path)!);
            load.Invoke(null, [row]);
            expected++;
        }
    }
    string[] lines = File.ReadAllLines(log);
    string[] errors = lines.Where(l => l.Contains("] ERROR ") || l.Contains("] SKIP ")).ToArray();
    var moduleRoots = plan["mods"]!.AsArray().SelectMany(m => m!["dlls"]!.AsArray())
        .Where(d => d!["entry"]!.GetValue<string>() == "CommunityPatch.Entry.InitWithOptions")
        .Select(d => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(d!["path"]!.GetValue<string>())!, "modules")) + Path.DirectorySeparatorChar)
        .ToArray();
    var bundled = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic &&
        moduleRoots.Any(root => a.Location.StartsWith(root, StringComparison.OrdinalIgnoreCase))).ToHashSet();
    var moduleAssemblies = bundled.Count;
    if (mode == "reject")
    {
        Require(errors.Length == 1 && lines.Any(l => l.Contains(result, StringComparison.OrdinalIgnoreCase)), "Expected refusal: " + string.Join("; ", errors));
        Require(moduleAssemblies == 0, "A refused pack loaded a bundled assembly");
        Console.WriteLine("PASS pack refused before loading modules: " + result);
        return;
    }
    Require(errors.Length == 0 && lines.Count(l => l.Contains("] OK ")) == expected, string.Join("; ", errors));
    if (mode == "workshop-release")
    {
        Require(moduleAssemblies == 10, "Expected ten bundled assemblies in the Workshop folder");
        Require(Harmony.HasAnyPatches("local.dw2.community-patch.salvage"), "Salvage actions were not registered");
        var bootstrap = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CommunityPatch");
        var dialog = bootstrap.GetType("CommunityPatch.CameraSettingsDialog", true)!;
        var store = (string?)AccessTools.Field(dialog, "settingsFile").GetValue(null);
        var folder = new DirectoryInfo(Path.GetDirectoryName(bootstrap.Location)!).Name;
        Require(folder.All(char.IsAsciiDigit) && Path.GetFileName(store) == $"steam_{folder}.json", "Camera Workshop settings target mismatch");
    }
    if (mode == "module-switches")
    {
        var expectedNames = plan["expectedModules"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
        var loadedNames = bundled.Select(a => a.GetName().Name!).ToHashSet();
        Require(expectedNames.SetEquals(loadedNames), "Unexpected module selection: " + string.Join(", ", loadedNames));
        var hookOwners = Harmony.GetAllPatchedMethods()
            .SelectMany(m => { var p = Harmony.GetPatchInfo(m); return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers); })
            .Select(p => p.PatchMethod.Module.Assembly).Where(bundled.Contains).Select(a => a.GetName().Name!).ToHashSet();
        Require(expectedNames.SetEquals(hookOwners), "Unexpected active module hooks: " + string.Join(", ", hookOwners));
        Require(Harmony.HasAnyPatches("DW2.CommunityPatch.CameraSettings"), "Built-in camera settings unavailable");
        Console.WriteLine($"PASS module selection and native hooks: {expectedNames.Count} active");
    }
    if (mode is "pack" or "pointers" or "camera" or "camera-hotkey" or "resources")
    {
        Require(moduleAssemblies == 10, "Expected all ten bundled assemblies");
        var hooks = Harmony.GetAllPatchedMethods().SelectMany(target =>
        {
            var info = Harmony.GetPatchInfo(target);
            string member = target.DeclaringType!.FullName + ":" + target.Name + "(" +
                string.Join(",", target.GetParameters().Select(p => p.ParameterType.FullName)) + ")";
            return new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes),
                ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) }
                .SelectMany(kind => kind.Item2.Where(p => bundled.Contains(p.PatchMethod.Module.Assembly))
                    .Select(p => new { target = member, kind = kind.Item1, owner = p.PatchMethod.Module.Assembly.GetName().Name,
                        patchMethod = p.PatchMethod.DeclaringType!.FullName + ":" + p.PatchMethod.Name }));
        }).ToArray();
        Require(hooks.Select(h => h.owner).Distinct().Count() == 10, "A bundled module installed no hooks");
        File.WriteAllText(Path.Combine(logs,"pack-hook-targets.json"), JsonSerializer.Serialize(hooks));
        var refuel = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "DeepSpaceStability");
        const BindingFlags hiddenStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        bool queue = (bool)refuel.GetType("DeepSpaceStability.RefuelPoint", true)!.GetField("Enabled", hiddenStatic)!.GetValue(null)!;
        bool rally = (bool)refuel.GetType("DeepSpaceStability.Rally", true)!.GetField("Enabled", hiddenStatic)!.GetValue(null)!;
        if (mode == "resources")
        {
            const string owner = "DW2.CommunityPatch.ResourceFreightPolicy";
            int policyTargets = Harmony.GetAllPatchedMethods().Count(method => Harmony.GetPatchInfo(method).Owners.Contains(owner));
            Require(policyTargets == 3, "Resources did not register all three scheduler targets");
            Console.WriteLine("PASS Resources, Freighter Logistics and Freight Dispatch initialized together");
        }
        else
            Require(queue && !rally, "The two independent refuelling options were not forwarded correctly");
    }
    using var input = new BinaryReader(File.OpenRead(fixture));
    var galaxy = new Galaxy().ReadFromStream(input, out _, out var issues);
    Require(issues.Count == 0 && galaxy.Ships.Count > 0, "Native save load failed: " + string.Join("; ", issues));
    if (mode == "camera-dialog") CameraDialogChecks.Run(galaxy, logs);
    if (mode == "map-notice")
    {
        var player = galaxy.Empires.GetPlayer();
        var notices = player.Messages.Where(m => m.Title is "Location Maps Recovered" or "System Maps Recovered").TakeLast(8).ToArray();
        foreach (var message in notices)
        {
            var location = galaxy.Locations.GetById(message.LocationId);
            var target = message.RelatedObject ?? (location?.Orbs?.Count > 0 ? location.Orbs[0] : null) ??
                message.RelatedItem.ResolveBase<StellarObject>(galaxy);
            var system = target?.GetSystem(galaxy, false);
            Console.WriteLine($"MAP NOTICE date={message.StarDateDescription} {message.Title} subtype={message.SubType} " +
                $"location={message.LocationId} related={message.RelatedObject?.GetType().Name} item={message.RelatedItem.Type} " +
                $"target={target?.GetType().Name}:{target?.GetName()} hasSystem={system != null} orbs={location?.Orbs?.Count} " +
                $"charted={(system?.Star is { } star ? player.ExplorationMap.CheckExplorationLevelSafely(star.OrbId, 0) : -1)} task={message.GameTask?.Type}");
        }
        Console.WriteLine($"MAP NOTICES {notices.Length}");
        MapCameraChecks.Run(galaxy, logs, replayMapNotice: true);
    }
    if (mode is "pointers" or "camera")
        ResourcePointerChecks.Run(galaxy, plan["nativeRoot"]!.GetValue<string>(), logs);
    if (mode is "camera" or "camera-hotkey")
        MapCameraChecks.Run(galaxy, logs, requireExplorerHotkey: mode == "camera-hotkey");
    var variables = galaxy.GlobalVariables.Where(p => p.Key.StartsWith("mod.", StringComparison.Ordinal))
        .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
    Require(variables.Length > 0, "Fixture has no saved mod state");
    var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(variables)));
    File.WriteAllText(result, JsonSerializer.Serialize(new { fingerprint, variables = variables.Length, ships = galaxy.Ships.Count, date = galaxy.Time.Ticks }));
    Console.WriteLine($"PASS old campaign loads with {variables.Length} saved mod values; {moduleAssemblies} bundled DLLs");
}

static void Require(bool value, string error)
{
    if (!value) throw new InvalidOperationException(error);
}
