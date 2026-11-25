using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommunityPatch;

public static class Entry
{
    private sealed record Module(string Id, string Name, string Version, string Dll, string Assembly, string EntryPoint,
        string Sha256, string[] Settings);
    private sealed record Package(Module[] Modules, string EngineSha256);
    private sealed record LaunchEntry(string DllPath, string EntryType, string ModFolder, string? ModKey);
    private sealed record LaunchManifest(LaunchEntry[] Entries, string[] ModFolders, string? ObservedHooksPath);
    private sealed record Start(Module Module, MethodInfo Method, object[]? Arguments);
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void InitWithOptions(string options, IReadOnlyDictionary<string, string> files)
    {
        string ownDll = typeof(Entry).Assembly.Location;
        string root = Path.GetDirectoryName(ownDll)!;
        var loader = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "DW2ModLauncher.Loader");
        var manifest = Read<LaunchManifest>(Path.Combine(Path.GetDirectoryName(loader.Location)!, "manifest.json"));
        var package = Read<Package>(Path.Combine(root, "modules.json"));
        var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "DistantWorlds.Types");
        if (!Names.Equals(Hash(game.Location), package.EngineSha256))
            throw Stop("the installed engine does not match this Community Patch release.");

        int ownIndex = Array.FindIndex(manifest.Entries, row => SamePath(row.DllPath, ownDll));
        if (ownIndex < 0 || manifest.Entries.Count(row => SamePath(row.DllPath, ownDll)) != 1)
            throw Stop("the launch manifest must contain one Community Patch entry.");

        var duplicates = package.Modules.Where(module => manifest.Entries.Any(row => !SamePath(row.DllPath, ownDll)
                && (Matches(row, module) || SamePath(row.ModFolder, Path.Combine(root, "modules", module.Id))))
            || manifest.ModFolders.Any(folder => Names.Equals(Leaf(folder), module.Id))
            || AppDomain.CurrentDomain.GetAssemblies().Any(a => Names.Equals(a.GetName().Name, module.Assembly)))
            .Select(m => m.Name).ToArray();
        if (duplicates.Length > 0)
            throw Stop("disable the separate copies in the active profile before loading: " + string.Join(", ", duplicates));

        int matrixIndex = Array.FindIndex(manifest.Entries, row =>
            Names.Equals(Leaf(row.DllPath), "DW2PositronicMatrix.dll"));
        if (matrixIndex < 0 || matrixIndex >= ownIndex)
            throw Stop("enable Positronic Matrix and order it before Community Patch.");
        var (readSaved, writeSaved) = RequireMatrixChannels(loader);

        var config = JsonNode.Parse(options)?.AsObject() ?? throw Stop("invalid launcher settings.");
        var verified = package.Modules.Select(module =>
        {
            string path = Path.Combine(root, "modules", module.Id, module.Dll);
            if (!File.Exists(path) || !Names.Equals(Hash(path), module.Sha256) ||
                !Names.Equals(AssemblyName.GetAssemblyName(path).Name, module.Assembly))
                throw Stop($"missing or changed module {module.Name} {module.Version}.");
            return (module, path);
        }).ToArray();
        var starts = verified.Where(m => ModuleEnabled(config, m.module.Id))
            .Select(m => Resolve(m.module, m.path, config, files)).ToArray();

        foreach (var start in starts)
        {
            try { start.Method.Invoke(null, start.Arguments); }
            catch (TargetInvocationException ex) { throw Stop($"{start.Module.Name} failed to initialize. Restart after correcting the error.", ex.InnerException ?? ex); }
        }
        if (ModuleEnabled(config, "SalvageRefit")) SalvageOrders.Install(readSaved, writeSaved);
        ResourceSourcePointers.Init();
        ResourceFreightPolicy.Init(readSaved, writeSaved);
        StockpileDetails.Init();
        MapCamera.Init(config);
        CameraSettingsDialog.Install(config, manifest.ObservedHooksPath, manifest.Entries[ownIndex].ModKey, root);
    }

    private static bool ModuleEnabled(JsonObject settings, string id)
    {
        // Older profiles have no module switches. Their ten modules all ran.
        if (settings[id + "__Enabled"] is not JsonValue option) return true;
        if (option.TryGetValue<bool>(out var enabled)) return enabled;
        return !option.TryGetValue<string>(out var text) || !bool.TryParse(text, out enabled) || enabled;
    }

    private static Start Resolve(Module module, string path, JsonObject settings, IReadOnlyDictionary<string, string> files)
    {
        var assembly = Assembly.LoadFrom(path);
        int end = module.EntryPoint.LastIndexOf('.');
        var type = assembly.GetType(module.EntryPoint[..end], throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var options = new JsonObject();
        foreach (string key in module.Settings)
            if (settings.TryGetPropertyValue(module.Id + "__" + key, out var value)) options[key] = value?.DeepClone();
        if (settings.TryGetPropertyValue("Language", out var language)) options["Language"] = language?.DeepClone();

        var both = type.GetMethod("InitWithOptions", flags, [typeof(string), typeof(IReadOnlyDictionary<string, string>)]);
        if (both != null) return new Start(module, both, [options.ToJsonString(), files]);
        var withOptions = type.GetMethod("InitWithOptions", flags, [typeof(string)]);
        if (withOptions != null) return new Start(module, withOptions, [options.ToJsonString()]);
        var init = type.GetMethod(module.EntryPoint[(end + 1)..], flags, Type.EmptyTypes);
        return init != null ? new Start(module, init, null) : throw Stop("unknown entry point for " + module.Name + ".");
    }

    private static (Func<object, string, string, string>, Action<object, string, string, string>) RequireMatrixChannels(Assembly loader)
    {
        var get = loader.GetType("DW2ModLauncher.Loader.LiveChannels")?.GetMethod("Get",
            [typeof(string), typeof(string), typeof(int), typeof(Type)]);
        if (get?.Invoke(null, ["CommunityPatch", "saved-data.get", 1, typeof(Func<object, string, string, string>)]) is not
                Func<object, string, string, string> read ||
            get.Invoke(null, ["CommunityPatch", "saved-data.set", 1, typeof(Action<object, string, string, string>)]) is not
                Action<object, string, string, string> write)
            throw Stop("Positronic Matrix did not provide both save channels.");
        return (read, write);
    }

    private static bool Matches(LaunchEntry row, Module module) =>
        Names.Equals(row.EntryType, module.EntryPoint) ||
        Names.Equals(Leaf(row.DllPath), module.Dll) ||
        (File.Exists(row.DllPath) && Names.Equals(AssemblyName.GetAssemblyName(row.DllPath).Name, module.Assembly));

    private static bool SamePath(string? a, string b) =>
        !string.IsNullOrWhiteSpace(a) && Names.Equals(Path.GetFullPath(a), Path.GetFullPath(b));
    private static string Leaf(string? value) => Path.GetFileName(value?.TrimEnd('/', '\\')) ?? "";
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions)
        ?? throw Stop("could not read " + Path.GetFileName(path) + ".");
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static InvalidOperationException Stop(string reason, Exception? inner = null)
    {
        string message = "Community Patch 1.0 and more: " + reason;
        var loader = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader");
        try
        {
            loader?.GetType("DW2ModLauncher.Loader.ModStatus")?.GetMethod("Error", [typeof(string), typeof(string)])
                ?.Invoke(null, ["CommunityPatch", message]);
        }
        catch (TargetInvocationException) { /* The original error still reaches the loader's log. */ }
        return new InvalidOperationException(message, inner);
    }
}

