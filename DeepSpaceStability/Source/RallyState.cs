using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DistantWorlds.Types;
using HarmonyLib;

[assembly: InternalsVisibleTo("DW2ModLauncher.Loader")]

namespace DeepSpaceStability;

internal sealed class RallyState
{
    public int Version { get; set; } = 1;
    public int X { get; set; }
    public int Y { get; set; }
    public bool HasGoal { get; set; }
    public bool Active { get; set; }
    public HashSet<int> Excluded { get; set; } = [];
}

internal sealed class RallyRuntime
{
    internal readonly object Gate = new();
    internal RallyState State = new();
    internal bool Faulted;
    internal DateTime LastReview;
    internal string? Stored;
    internal bool Restored;
    internal int Reviewing, Revision;
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<int,int> Claims = new();
}

internal static class RallyStore
{
    private const string Owner = "deep-space-stability-refuel";
    private static readonly ConditionalWeakTable<Fleet, RallyRuntime> States = new();
    internal static readonly AccessTools.FieldRef<Fleet, Galaxy> GalaxyOf = AccessTools.FieldRefAccess<Fleet, Galaxy>("_Galaxy");
    internal static readonly AccessTools.FieldRef<Empire, Galaxy> EmpireGalaxy = AccessTools.FieldRefAccess<Empire, Galaxy>("_Galaxy");
    private static Func<object, string, string, string>? get;
    private static Action<object, string, string, string>? set;
    private static readonly object ChannelGate = new();
    internal static string Key(Fleet fleet) => $"fleet.{fleet.GetEmpire()!.EmpireId}.{fleet.FleetId}";

    private static void Channels()
    {
        lock (ChannelGate)
        {
            if (get != null && set != null) return;
            var live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")?.GetType("DW2ModLauncher.Loader.LiveChannels");
            var method = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string), typeof(int), typeof(Type)], null);
            var reader = (Func<object, string, string, string>?)method?.Invoke(null, ["DeepSpaceStability", "saved-data.get", 1, typeof(Func<object, string, string, string>)]);
            var writer = (Action<object, string, string, string>?)method?.Invoke(null, ["DeepSpaceStability", "saved-data.set", 1, typeof(Action<object, string, string, string>)]);
            if (reader == null || writer == null) throw new InvalidOperationException("Enable Positronic Matrix before using fleet rally/refuelling; saved-data channels are unavailable.");
            get = reader; set = writer;
        }
    }

    internal static RallyRuntime For(Fleet fleet) => States.GetValue(fleet, f =>
    {
        var runtime = new RallyRuntime();
        try
        {
            Channels();
            string? text = get!(GalaxyOf(f), Owner, Key(f));
            if (text == null && GalaxyOf(f).GlobalVariables.ContainsKey("mod."+Owner+"."+Key(f)))
                throw new InvalidDataException("Existing rally record could not be read; it was preserved");
            if (text != null)
            {
                if (System.Text.Encoding.UTF8.GetByteCount(text) > 7000) throw new InvalidDataException("Oversized rally state");
                var state = JsonSerializer.Deserialize<RallyState>(text) ?? throw new InvalidDataException("Missing rally state");
                if (state.Version != 1 || state.Excluded == null || state.Excluded.Count > 1024 || (state.Active && !state.HasGoal) || state.X < 0 || state.Y < 0 || state.X > GalaxyOf(f).SizeX || state.Y > GalaxyOf(f).SizeY)
                    throw new InvalidDataException("Invalid rally state; stored data was preserved");
                runtime.State = state; runtime.Stored = text; runtime.Restored = true;
            }
        }
        catch (Exception error) { Fault(runtime, error); }
        return runtime;
    });

    internal static bool Save(Fleet fleet, RallyRuntime runtime)
    {
        if (runtime.Faulted) return false;
        try
        {
            string json = JsonSerializer.Serialize(runtime.State);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 7000) throw new InvalidDataException("Rally state exceeds its bounded save record");
            if (json == runtime.Stored) return true;
            Channels(); set!(GalaxyOf(fleet), Owner, Key(fleet), json);
            if (get!(GalaxyOf(fleet), Owner, Key(fleet)) != json) throw new IOException("Matrix rejected rally data; fleet coordination was stopped");
            runtime.Stored = json; return true;
        }
        catch (Exception error) { Fault(runtime, error); return false; }
    }

    internal static void Fault(RallyRuntime runtime, Exception error)
    {
        if (runtime.Faulted) return;
        runtime.Faulted = true;
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "data", "Logs"); Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "deep-space-stability.log"), $"{DateTime.UtcNow:O} Fleet coordination disabled: {error}\n");
        }
        catch { /* A logging failure must not issue a replacement order or erase save data. */ }
    }
}
