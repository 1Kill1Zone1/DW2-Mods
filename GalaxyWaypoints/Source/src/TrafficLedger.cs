using System.Text.Json;

namespace GalaxyWaypoints;

internal readonly record struct Passage(Waypoint Point, int ShipId, long StarDate, bool Freighter = false, int EmpireId = -1);
internal readonly record struct TrafficCount(int Ships, int Passages, int Freighters = 0);

internal sealed class TrafficLedger
{
    // DW2 uses 12 months of 50,000 star-date units per in-game year.
    public const long Year = 600_000;
    private readonly string? file;
    private readonly object gate = new();
    private readonly Dictionary<Waypoint, List<Passage>> passages = new();
    private readonly Dictionary<Waypoint, (long TimeBucket, TrafficCount Count)> counts = new();
    private readonly Dictionary<Waypoint, (long ExpiresAt, long StarDate, TrafficCount Count)> visibleCounts = new();
    private bool dirty;
    private long nextFlush;

    public TrafficLedger(string file, long starDate, IReadOnlyList<Waypoint> activePoints)
    {
        this.file = file;
        if (!File.Exists(file))
            return;

        foreach (Passage passage in JsonSerializer.Deserialize<Passage[]>(File.ReadAllText(file)) ?? [])
        {
            if (!activePoints.Contains(passage.Point) || !InYear(passage.StarDate, starDate))
            {
                dirty = true;
                continue;
            }
            Add(passage);
        }
    }

    public TrafficLedger(IEnumerable<Passage> saved, long starDate, IReadOnlyList<Waypoint> activePoints, object sync)
    {
        gate = sync;
        foreach (Passage passage in saved)
            if (activePoints.Contains(passage.Point) && InYear(passage.StarDate, starDate)) Add(passage);
    }

    public Passage[] Export(long starDate)
    {
        lock (gate) return passages.Values.SelectMany(list => list).Where(p => InYear(p.StarDate, starDate))
            .OrderBy(p => p.StarDate).ThenBy(p => p.ShipId).ThenBy(p => p.Point.X).ThenBy(p => p.Point.Y).ToArray();
    }

    public static Waypoint? MatchArrival(IReadOnlyList<Waypoint> points, Waypoint? planned, Waypoint? actual,
        Waypoint exit, double margin)
    {
        if (actual is { } arrived && points.Contains(arrived))
            return arrived;
        if (planned is { } expected && points.Contains(expected) && RouteChoice.Distance(exit, expected) <= margin)
            return expected;
        return null;
    }

    public void Record(Waypoint point, int shipId, long starDate, bool freighter = false, int empireId = -1)
    {
        lock (gate)
        {
            if (Add(new Passage(point, shipId, starDate, freighter, empireId)))
            {
                counts.Remove(point);
                visibleCounts.Remove(point);
                dirty = true;
            }
        }
    }

    private bool Add(Passage passage)
    {
        if (!passages.TryGetValue(passage.Point, out List<Passage>? list))
            passages.Add(passage.Point, list = new List<Passage>());

        // Several ships can arrive in the same simulation tick, but each arrival is counted once.
        for (int i = list.Count - 1; i >= 0 && list[i].StarDate == passage.StarDate; i--)
            if (list[i].ShipId == passage.ShipId)
                return false;

        list.Add(passage);
        return true;
    }

    public TrafficCount Count(Waypoint point, long starDate)
    {
        lock (gate)
        {
            if (!passages.TryGetValue(point, out List<Passage>? list))
                return default;
            long bucket = starDate / 1000;
            if (counts.TryGetValue(point, out var cached) && cached.TimeBucket == bucket)
                return cached.Count;

            list.RemoveAll(item => !InYear(item.StarDate, starDate));
            var count = new TrafficCount(list.Select(item => item.ShipId).Distinct().Count(), list.Count,
                list.Where(item => item.Freighter).Select(item => item.ShipId).Distinct().Count());
            counts[point] = (bucket, count);
            return count;
        }
    }

    public TrafficCount VisibleCount(Waypoint point, long starDate, Func<Passage, bool> visible)
    {
        lock (gate)
        {
            if (!passages.TryGetValue(point, out List<Passage>? list)) return default;
            long now = Environment.TickCount64;
            // One ledger belongs to one observing empire. A short wall-clock cache avoids repeated sensor checks for every ring,
            // while still responding to sensor loss/redetection without retaining historical foreign traffic.
            if (visibleCounts.TryGetValue(point, out var cached) && cached.ExpiresAt > now && cached.StarDate <= starDate)
                return cached.Count;
            list.RemoveAll(item => !InYear(item.StarDate, starDate));
            Passage[] current = list.Where(visible).ToArray();
            var count = new TrafficCount(current.Select(item => item.ShipId).Distinct().Count(), current.Length,
                current.Where(item => item.Freighter).Select(item => item.ShipId).Distinct().Count());
            visibleCounts[point] = (now + 250, starDate, count);
            return count;
        }
    }

    public void Remove(Waypoint point)
    {
        lock (gate)
        {
            dirty |= passages.Remove(point);
            counts.Remove(point);
            visibleCounts.Remove(point);
        }
    }

    public void Flush(long starDate, bool force = false)
    {
        if (file == null) return;
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (!dirty || (!force && now < nextFlush))
                return;
            nextFlush = now + 10_000;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            Passage[] snapshot = passages.Values.SelectMany(list => list)
                .Where(passage => InYear(passage.StarDate, starDate)).ToArray();
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, file, overwrite: true);
            dirty = false;
        }
    }

    private static bool InYear(long passage, long now) => passage > now - Year && passage <= now;
}
