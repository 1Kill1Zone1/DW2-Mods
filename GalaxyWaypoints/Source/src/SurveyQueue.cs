using System.Text.Json.Serialization;
using DistantWorlds.Types;

namespace GalaxyWaypoints;

internal sealed class SurveySave
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public int[] Systems { get; set; } = [];

    internal void Validate(Galaxy galaxy)
    {
        if (Version != 1 || Systems == null || Systems.Length > galaxy.Systems.Count ||
            Systems.Any(id => id < 0 || !galaxy.Systems.ContainsKey(id)) || Systems.Distinct().Count() != Systems.Length)
            throw new InvalidDataException("Invalid survey priority state; existing data retained.");
    }
}

internal enum SurveyAddResult { Added, AlreadyQueued, AlreadySurveyed, Unavailable }
internal sealed record SurveyPriority(int SystemId, int? ShipId, string Status);

// Only ordered system IDs are persistent. Ship reservations are reconciled with actual missions after load.
internal sealed class SurveyQueue
{
    private readonly object gate = new();
    private readonly List<SurveyPriority> entries;
    private SurveyPriority[]? snapshot;
    internal SurveyQueue(IEnumerable<int> systems) => entries = systems.Select(id => new SurveyPriority(id, null, "Waiting for an available explorer")).ToList();
    internal SurveyPriority[] Snapshot { get { lock (gate) return snapshot ??= entries.ToArray(); } }
    internal SurveySave Export() { lock (gate) return new() { Systems = entries.Select(e => e.SystemId).ToArray() }; }

    internal SurveyAddResult Add(int systemId, bool visible, bool surveyed)
    {
        lock (gate)
        {
            if (!visible || systemId < 0) return SurveyAddResult.Unavailable;
            if (surveyed) return SurveyAddResult.AlreadySurveyed;
            if (entries.Any(e => e.SystemId == systemId)) return SurveyAddResult.AlreadyQueued;
            entries.Add(new(systemId, null, "Waiting for an available explorer")); snapshot = null;
            return SurveyAddResult.Added;
        }
    }

    internal bool Remove(int systemId)
    {
        lock (gate)
        {
            bool removed = entries.RemoveAll(e => e.SystemId == systemId) != 0;
            if (removed) snapshot = null;
            return removed;
        }
    }

    internal bool Reserve(SurveyPriority expected, int shipId)
    {
        lock (gate)
        {
            int index = entries.FindIndex(e => ReferenceEquals(e, expected));
            if (index < 0 || expected.ShipId is { } other && other != shipId ||
                entries.Any(e => e.SystemId != expected.SystemId && e.ShipId == shipId)) return false;
            entries[index] = new(expected.SystemId, shipId, "Survey assigned"); snapshot = null; return true;
        }
    }

    internal bool SetStatus(SurveyPriority expected, string status) => Update(expected, expected.ShipId, status);
    internal bool Release(SurveyPriority expected, string status) => Update(expected, null, status);

    private bool Update(SurveyPriority expected, int? shipId, string status)
    {
        lock (gate)
        {
            int index = entries.FindIndex(e => ReferenceEquals(e, expected));
            if (index < 0) return false;
            var updated = new SurveyPriority(expected.SystemId, shipId, status);
            if (entries[index] == updated) return true;
            entries[index] = updated; snapshot = null; return true;
        }
    }
}
