using System.Text.Json;
using DistantWorlds.Types;

namespace GalaxyWaypoints;

internal sealed class TransitRestrictions
{
    private readonly string? file;
    private readonly object gate;
    private volatile TransitSnapshot snapshot;
    public TransitRestrictions(string file) { this.file = file; gate = new(); snapshot = new(Read(file)); }
    public TransitRestrictions(Dictionary<int, TransitGroup> initial, object sync) { gate = sync; snapshot = new(new(initial)); }
    public Dictionary<int, TransitGroup> Export() { lock (gate) return new(snapshot.Masks); }

    public IReadOnlySet<int> Systems => snapshot.Systems;
    public object Revision => snapshot;
    public TransitGroup Mask(int systemId) => snapshot.Masks.GetValueOrDefault(systemId);
    public IReadOnlySet<int> For(TransitGroup group) => snapshot.ByGroup.GetValueOrDefault(group, TransitSnapshot.Empty);
    public IReadOnlySet<int> For(Ship ship, ShipMission? mission) =>
        TransitRules.ManualOverride(mission) ? TransitSnapshot.Empty : For(TransitRules.Group(ship.Role));

    public bool Toggle(int systemId)
    {
        lock (gate)
        {
            bool blocked = Mask(systemId) == TransitGroup.None;
            Set(systemId, blocked ? TransitGroup.Default : TransitGroup.None);
            return blocked;
        }
    }

    public void ToggleGroup(int systemId, TransitGroup group)
    {
        lock (gate) Set(systemId, Mask(systemId) ^ group);
    }

    public void Set(int systemId, TransitGroup mask)
    {
        if (systemId < 0 || (mask & ~TransitGroup.All) != 0) throw new ArgumentOutOfRangeException();
        lock (gate)
        {
            var updated = new Dictionary<int, TransitGroup>(snapshot.Masks);
            if (mask == TransitGroup.None) updated.Remove(systemId); else updated[systemId] = mask;
            if (file != null)
            {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && !File.Exists(file + ".v1.backup"))
            {
                string old = File.ReadAllText(file);
                if (old.TrimStart().StartsWith("[")) File.Copy(file, file + ".v1.backup");
            }
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new TransitFile { Version = 2, Systems = updated }));
            File.Move(temp, file, overwrite: true);
            }
            snapshot = new(updated);
        }
    }

    private static Dictionary<int, TransitGroup> Read(string file)
    {
        if (!File.Exists(file)) return new();
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        if (json.RootElement.ValueKind == JsonValueKind.Array)
            return json.RootElement.EnumerateArray().Select(x => x.GetInt32()).Where(x => x >= 0).Distinct()
                .ToDictionary(x => x, _ => TransitGroup.Default);
        var data = json.RootElement.Deserialize<TransitFile>() ?? throw new JsonException("Missing transit rules");
        if (data.Version != 2 || data.Systems == null || data.Systems.Any(x => x.Key < 0 || (x.Value & ~TransitGroup.All) != 0))
            throw new JsonException("Invalid transit rules/version");
        return data.Systems.Where(x => x.Value != TransitGroup.None).ToDictionary(x => x.Key, x => x.Value);
    }

    private sealed class TransitFile
    {
        public int Version { get; set; }
        public Dictionary<int, TransitGroup> Systems { get; set; } = new();
    }
    private sealed class TransitSnapshot
    {
        public static readonly IReadOnlySet<int> Empty = new HashSet<int>();
        public readonly Dictionary<int, TransitGroup> Masks;
        public readonly IReadOnlySet<int> Systems;
        public readonly Dictionary<TransitGroup, IReadOnlySet<int>> ByGroup;
        public TransitSnapshot(Dictionary<int, TransitGroup> masks)
        {
            Masks = masks;
            Systems = masks.Keys.ToHashSet();
            ByGroup = TransitRules.Groups.ToDictionary(g => g.Group,
                g => (IReadOnlySet<int>)masks.Where(x => (x.Value & g.Group) != 0).Select(x => x.Key).ToHashSet());
        }
    }
}

[Flags]
internal enum TransitGroup
{
    None = 0, Freighters = 1, Passengers = 2, Explorers = 4, Constructors = 8,
    Miners = 16, Colonizers = 32, Tankers = 64, Military = 128,
    Default = Freighters | Passengers | Explorers | Constructors | Miners | Colonizers | Tankers,
    All = Default | Military
}

internal static class TransitRules
{
    public static readonly (TransitGroup Group, string Label)[] Groups =
    [
        (TransitGroup.Freighters, "Freighters"), (TransitGroup.Passengers, "Passengers"),
        (TransitGroup.Explorers, "Explorers"), (TransitGroup.Constructors, "Constructors"),
        (TransitGroup.Miners, "Mining ships"), (TransitGroup.Colonizers, "Colony ships"),
        (TransitGroup.Tankers, "Fuel tankers"), (TransitGroup.Military, "Military / troop transports")
    ];
    public static bool ManualOverride(ShipMission? mission) => mission?.IsManuallyAssigned == true;
    public static TransitGroup Group(ShipRole role) => role switch
    {
        ShipRole.FreighterSmall or ShipRole.FreighterMedium or ShipRole.FreighterLarge => TransitGroup.Freighters,
        ShipRole.PassengerShip => TransitGroup.Passengers,
        ShipRole.ExplorationShip => TransitGroup.Explorers,
        ShipRole.ConstructionShip => TransitGroup.Constructors,
        ShipRole.MiningShip => TransitGroup.Miners,
        ShipRole.ColonyShip => TransitGroup.Colonizers,
        ShipRole.FuelTanker => TransitGroup.Tankers,
        ShipRole.Escort or ShipRole.Frigate or ShipRole.Destroyer or ShipRole.Cruiser or ShipRole.CapitalShip
            or ShipRole.Carrier or ShipRole.TroopTransport or ShipRole.PlanetDestroyer => TransitGroup.Military,
        _ => TransitGroup.None
    };
    public static bool IsBlocked(int systemId, int destinationSystemId, IReadOnlySet<int> blocked, bool blockDestination = false) =>
        systemId >= 0 && (blockDestination || systemId != destinationSystemId) && blocked.Contains(systemId);

    public static List<Waypoint>? FindWaypointDetour(Waypoint from, Waypoint to, IReadOnlyList<Waypoint> points,
        double minimum, double range, double fuel, Func<Waypoint, Waypoint, double> lineCost,
        Func<double, int, double>? estimate = null)
    {
        var plan = WaypointRouting.Find([new(0, from), new(1, to)], points, false, minimum, range,
            fuel, 0, _ => true, (a, b) => a == from && b == to ? double.PositiveInfinity : lineCost(a, b),
            allowOrdinaryResult: true, estimate: estimate);
        return plan?.Nodes.Skip(1).SkipLast(1).Select(node => node.Point).ToList();
    }

    public static bool[] Exclude(bool[] original, IReadOnlySet<int> blocked, int sourceId, int destinationId)
    {
        var result = (bool[])original.Clone();
        foreach (int id in blocked)
            if ((uint)id < (uint)result.Length && id != sourceId && id != destinationId)
                result[id] = true;
        return result;
    }

    public static bool RouteFits(IReadOnlyList<int> hops, Waypoint from, double range, double fuel,
        Func<int, Waypoint?> resolveHop, Func<Waypoint, Waypoint, double> lineCost)
    {
        if (hops.Count is 0 or > byte.MaxValue)
            return false;

        foreach (int id in hops)
        {
            if (resolveHop(id) is not { } next || RouteChoice.Distance(from, next) > range)
                return false;
            double cost = lineCost(from, next);
            if (!double.IsFinite(cost) || cost < 0)
                return false;
            from = next;
        }
        return true;
    }
}
