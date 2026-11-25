using System.Reflection;
using System.Text.Json;
using DistantWorlds.Types;

namespace InheritedShipRetirement;

internal sealed record Intent(int Version, int ShipId, short EmpireId, string Reason);

// One small Matrix value per ship: a single replacement is also the save commit point.
internal static class Store
{
    internal const string Owner = "InheritedShipRetirement", Prefix = "ship.", FullPrefix = "mod." + Owner + "." + Prefix;
    static Func<object, string, string, string>? read;
    static Action<object, string, string, string>? write;
    internal static void Require()
    {
        var live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")?.GetType("DW2ModLauncher.Loader.LiveChannels");
        var get = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string), typeof(int), typeof(Type)], null);
        read = (Func<object, string, string, string>?)get?.Invoke(null, [Owner, "saved-data.get", 1, typeof(Func<object, string, string, string>)]);
        write = (Action<object, string, string, string>?)get?.Invoke(null, [Owner, "saved-data.set", 1, typeof(Action<object, string, string, string>)]);
        if (read == null || write == null) throw new InvalidOperationException("Inherited Ship Retirement requires Positronic Matrix saved-data channels.");
    }
    internal static Dictionary<int, Intent> Load(Galaxy galaxy)
    {
        var result = new Dictionary<int, Intent>();
        foreach (string full in galaxy.GlobalVariables.Keys.Where(k => k.StartsWith(FullPrefix, StringComparison.Ordinal)).ToArray())
        {
            string key = full[($"mod.{Owner}.").Length..];
            string? json = read!(galaxy, Owner, key);
            if (json == null || json.Length > 2000) throw new InvalidDataException("Unreadable retirement metadata: " + key);
            var intent = JsonSerializer.Deserialize<Intent>(json);
            if (intent == null || intent.Version != 1 || intent.ShipId < 0 || intent.EmpireId < 0 || intent.Reason == null || intent.Reason.Length > 200 || key != Key(intent.ShipId))
                throw new InvalidDataException("Invalid retirement metadata: " + key);
            result.Add(intent.ShipId, intent);
        }
        return result;
    }
    static string Key(int id) => Prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static void Save(Galaxy galaxy, int shipId, Intent? intent)
    {
        string? text = intent == null ? null : JsonSerializer.Serialize(intent);
        write!(galaxy, Owner, Key(shipId), text!);
        if (read!(galaxy, Owner, Key(shipId)) != text) throw new IOException("Matrix rejected retirement state for ship " + shipId);
    }
}
