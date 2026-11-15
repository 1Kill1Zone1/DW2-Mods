using DistantWorlds.Types;

namespace FreightDispatch;

static class Dispatch
{
    internal static bool IsOpen(DispatchTask task) => task.Phase is DispatchPhase.Active or DispatchPhase.Cancelling;
    internal static bool SetPriority(Galaxy g, Empire e, Ship source, bool high, out string message)
    {
        message = "Source is unavailable";
        if (!Native.ValidSource(g, e, source) || !State.Ready(g, e, out var session)) return false;
        lock (session.Gate)
        {
            bool wasHigh = session.Data!.HighSources.Contains(source.ShipId);
            if (high) session.Data!.HighSources.Add(source.ShipId); else session.Data!.HighSources.Remove(source.ShipId);
            try { State.Persist(g, session); message = high ? "High source priority" : "Normal source priority"; return true; }
            catch (Exception ex)
            {
                if (wasHigh) session.Data.HighSources.Add(source.ShipId); else session.Data.HighSources.Remove(source.ShipId);
                message = ex.Message; return false;
            }
        }
    }
    internal static bool Start(Galaxy g, Empire e, Ship source, StellarObject destination, IEnumerable<DispatchResource> resources, int limit, out string message)
    {
        message = "Source or destination is unavailable";
        if (!Native.ValidSource(g, e, source) || !Native.ValidDestination(g, e, destination) || Native.SameHold(source, destination)) return false;
        if (!State.Ready(g, e, out var session)) { message = State.Error(g, e) ?? "Saved data is unavailable"; return false; }
        var selected = resources.Select(r => new DispatchResource { ResourceId = r.ResourceId, Retain = r.Retain }).ToList();
        if (limit is < 1 or > 1000 || selected.Count is < 1 or > 256 || selected.Select(r => r.ResourceId).Distinct().Count() != selected.Count ||
            selected.Any(r => g.Resources.GetById(r.ResourceId) == null || !float.IsFinite(r.Retain) || r.Retain < 0))
        { message = "Select resources, nonnegative reserves, and a whole-number limit from 1 to 1000"; return false; }
        lock (session.Gate)
        {
            if (session.Data!.Tasks.Any(t => IsOpen(t) && (t.SourceId == source.ShipId || g.Ships.GetById(t.SourceId) is { } other && SameSourceHold(source, other))))
            { message = "This source already has a task or deliveries finishing after cancellation"; return false; }
            if (session.Data.Tasks.Count >= 512 || session.Data.NextTaskId == int.MaxValue)
            { message = "Task history is full; no task was started"; return false; }
            session.Data.Tasks.Add(new DispatchTask
            {
                Id = session.Data.NextTaskId++, SourceId = source.ShipId,
                DestinationType = destination is Ship ? ItemType.Ship : ItemType.Orb,
                DestinationId = destination is Ship ship ? ship.ShipId : ((Orb)destination).OrbId,
                Limit = limit, Resources = selected
            });
            bool persisted = false;
            try { State.Persist(g, session); persisted = true; Native.RetireReverseDemand(g, e, session.Data.Tasks[^1]); message = "Task created; waiting for native logistics"; return true; }
            catch (Exception ex)
            {
                // A later save retry must not resurrect a Start that failed before any native assignment.
                if (!persisted) { session.Data.Tasks.RemoveAt(session.Data.Tasks.Count - 1); session.Data.NextTaskId--; }
                message = ex.Message; return false;
            }
        }
    }
    static bool SameSourceHold(Ship source, Ship other) => ReferenceEquals(source.Cargo, other.Cargo);
    internal static bool Cancel(Galaxy g, Empire e, int taskId, out string message)
    {
        message = "Task is unavailable";
        if (!State.Ready(g, e, out var session)) return false;
        lock (session.Gate)
        {
            var task = session.Data!.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null || task.Phase != DispatchPhase.Active) return false;
            task.Phase = DispatchPhase.Cancelling;
            task.Status = "Cancelled: stopping pickups; loaded deliveries finish once";
            try { Native.CancelUnloaded(g, e, task); State.Persist(g, session); message = task.Status; return true; }
            catch (Exception ex) { session.Fault = message = ex.Message; return false; }
        }
    }
    internal static HashSet<int> Assigned(Galaxy g, Empire e, IEnumerable<DispatchTask> tasks)
    {
        var ids = new HashSet<int>();
        foreach (var task in tasks)
            foreach (var voyage in task.Voyages)
            {
                var ship = g.Ships.GetById(voyage.ShipId);
                if (Native.EconomyFreighter(e, ship) && Native.VoyageStillAssigned(g, e, task, voyage)) ids.Add(voyage.ShipId);
            }
        return ids;
    }
    internal static string FleetShare(int assigned, int fleet, int limit) =>
        FormattableString.Invariant($"Assigned {assigned} / {fleet} ({(fleet == 0 ? 0 : 100d * assigned / fleet):0.0}%), limit {limit}");
    internal static string Overview(Galaxy g, Empire e, DispatchTask task)
    {
        int fleet = e.Ships.Where(s => Native.EconomyFreighter(e, s)).Select(s => s.ShipId).Distinct().Count();
        return FleetShare(Assigned(g, e, [task]).Count, fleet, task.Limit);
    }
}
