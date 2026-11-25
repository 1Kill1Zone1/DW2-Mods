using System.Reflection;
using System.Text.Json;
using DistantWorlds.Types;

namespace RetirementResearchReport;

internal sealed record Gain(int ProjectId, string Name, int Repeat, float Size, float Before, float After, bool Completed, bool RacialBonus);
internal sealed record Notice(int Sequence, bool Complete, long Date, Gain[] Gains);
internal sealed record Ledger(int Version, int ShipId, short EmpireId, string Name, int NextSequence,
    bool Active, bool Finished, Gain[] Gains, Notice[] Pending, int LastSent);

internal static class Store
{
    internal const string Owner = "RetirementResearchReport", Prefix = "ship.", FullPrefix = "mod." + Owner + "." + Prefix;
    static Func<object, string, string, string>? read;
    static Action<object, string, string, string>? write;
    internal static void Require()
    {
        var live = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DW2ModLauncher.Loader")?.GetType("DW2ModLauncher.Loader.LiveChannels");
        var get = live?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, [typeof(string), typeof(string), typeof(int), typeof(Type)], null);
        read = (Func<object, string, string, string>?)get?.Invoke(null, [Owner, "saved-data.get", 1, typeof(Func<object, string, string, string>)]);
        write = (Action<object, string, string, string>?)get?.Invoke(null, [Owner, "saved-data.set", 1, typeof(Action<object, string, string, string>)]);
        if (read == null || write == null) throw new InvalidOperationException("Retirement Research Report requires Positronic Matrix saved-data channels.");
    }
    internal static Dictionary<int, Ledger> Load(Galaxy galaxy)
    {
        var result = new Dictionary<int, Ledger>();
        foreach (string full in galaxy.GlobalVariables.Keys.Where(k => k.StartsWith(FullPrefix, StringComparison.Ordinal)).ToArray())
        {
            string key = full[("mod." + Owner + ".").Length..];
            string? json = read!(galaxy, Owner, key);
            if (json == null || json.Length > 4_000_000) throw new InvalidDataException("Unreadable report ledger: " + key);
            var item = JsonSerializer.Deserialize<Ledger>(json);
            if (item == null || !Valid(item) || key != Key(item.ShipId)) throw new InvalidDataException("Invalid report ledger: " + key);
            result.Add(item.ShipId, item);
        }
        return result;
    }
    internal static bool Valid(Ledger value) => value.Version == 1 && value.ShipId >= 0 && value.EmpireId > 0 &&
        value.Name != null && value.Name.Length <= 1024 && value.NextSequence >= 1 && value.LastSent >= 0 && value.LastSent < value.NextSequence &&
        !(value.Active && value.Finished) && value.Gains != null && value.Gains.Length <= 20_000 && value.Gains.All(ValidGain) &&
        value.Pending != null && value.Pending.Length <= 10_000 && value.Pending.All(n => n != null && n.Sequence > value.LastSent && n.Sequence < value.NextSequence && n.Gains != null && n.Gains.Length <= 20_000 && n.Gains.All(ValidGain)) &&
        value.Pending.Select(n => n.Sequence).SequenceEqual(value.Pending.Select(n => n.Sequence).Distinct().Order());
    static bool ValidGain(Gain g) => g != null && g.ProjectId >= 0 && g.Name != null && g.Name.Length <= 2048 && g.Repeat >= 0 &&
        float.IsFinite(g.Size) && g.Size > 0 && float.IsFinite(g.Before) && g.Before >= 0 && float.IsFinite(g.After) && g.After > g.Before;
    static string Key(int id) => Prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static void Save(Galaxy galaxy, Ledger value)
    {
        if (!Valid(value)) throw new InvalidDataException("Refusing invalid report state");
        string json = JsonSerializer.Serialize(value);
        write!(galaxy, Owner, Key(value.ShipId), json);
        if (read!(galaxy, Owner, Key(value.ShipId)) != json) throw new IOException("Matrix did not retain report state for ship " + value.ShipId);
    }
}
