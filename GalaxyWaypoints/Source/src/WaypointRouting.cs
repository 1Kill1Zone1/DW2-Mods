namespace GalaxyWaypoints;

internal readonly record struct RouteAnchor(int LocationId, Waypoint Point);
internal sealed record WaypointPlan(RouteAnchor[] Nodes, double Cost, double Distance);

internal static class WaypointRouting
{
    public static WaypointPlan? Find(IReadOnlyList<RouteAnchor> anchors, IReadOnlyList<Waypoint> points,
        bool hasOrdinaryPath, double minimum, double range, double fuel, double jumpCost,
        Func<Waypoint, bool> isSafe, Func<Waypoint, Waypoint, double> lineCost,
        bool allowOrdinaryResult = false, Func<double, int, double>? estimate = null)
    {
        if (anchors.Count < 2 || !(range >= minimum) || !isSafe(anchors[^1].Point))
            return null;
        estimate ??= (cost, hops) => RouteChoice.TravelCost(cost, fuel) + hops * jumpCost;
        var nodes = anchors.ToList();
        var known = anchors.Select(a => a.Point).ToHashSet();
        foreach (var point in points)
            if (known.Add(point) && isSafe(point))
                nodes.Add(new(-1, point));
        int goal = anchors.Count - 1;
        bool[] safe = nodes.Select((n, i) => i == 0 || isSafe(n.Point)).ToArray();
        var edges = new Dictionary<(int, int), (double Distance, double Cost)>();
        // Native nebula costs are memoized only within this request, including baseline legs.
        (double Distance, double Cost) Edge(int from, int to)
        {
            if (edges.TryGetValue((from, to), out var edge)) return edge;
            double distance = RouteChoice.Distance(nodes[from].Point, nodes[to].Point);
            double cost = RouteChoice.CanJump(distance, minimum, range) ? lineCost(nodes[from].Point, nodes[to].Point) : double.PositiveInfinity;
            return edges[(from, to)] = (distance, cost);
        }
        double baseline = double.PositiveInfinity;
        if (hasOrdinaryPath)
        {
            double cost = 0, distance = 0;
            bool valid = true;
            for (int i = 1; i <= goal; i++)
            {
                var edge = Edge(i - 1, i);
                double leg = edge.Distance;
                double time = edge.Cost;
                valid &= safe[i] && RouteChoice.CanJump(leg, minimum, range) && double.IsFinite(time) && time > 0;
                distance += leg; cost += time;
            }
            if (valid) baseline = estimate(cost, goal);
        }
        // Direct travel is also a competitor: a longer safe chain must not displace a safe direct jump.
        double directDistance = RouteChoice.Distance(nodes[0].Point, nodes[goal].Point);
        if (RouteChoice.CanJump(directDistance, minimum, range))
        {
            double direct = Edge(0, goal).Cost;
            if (double.IsFinite(direct) && direct > 0)
                baseline = Math.Min(baseline, estimate(direct, 1));
        }
        var labels = Enumerable.Range(0, nodes.Count).Select(_ => new List<Label>()).ToArray();
        var queue = new PriorityQueue<Label, double>();
        var start = new Label(0, 0, 0, 0, null);
        labels[0].Add(start); queue.Enqueue(start, 0);
        int created = 1;
        while (queue.TryDequeue(out var current, out double score))
        {
            if (!labels[current.Node].Contains(current)) continue;
            if (!allowOrdinaryResult && score >= baseline) return null;
            if (current.Node == goal)
            {
                var path = new List<RouteAnchor>();
                for (var step = current; step != null; step = step.Previous) path.Add(nodes[step.Node]);
                path.Reverse();
                if (!allowOrdinaryResult && !path.Any(n => n.LocationId == -1)) return null;
                return new(path.ToArray(), current.Cost, current.Distance);
            }
            if (current.Hops >= byte.MaxValue - 1) continue;
            for (int next = 1; next < nodes.Count; next++)
            {
                if (next == current.Node || !safe[next]) continue;
                // Native anchors retain their order; waypoints can connect any two reachable nodes.
                if (current.Node < anchors.Count && next < anchors.Count &&
                    next != current.Node + 1 && !(current.Node == 0 && next == goal)) continue;
                var edge = Edge(current.Node, next);
                double total = current.Cost + edge.Cost;
                double length = current.Distance + edge.Distance;
                if (!(edge.Cost > 0) || !double.IsFinite(total)) continue;
                double candidateScore = estimate(total, current.Hops + 1);
                if (!double.IsFinite(candidateScore)) continue;
                if (!allowOrdinaryResult && candidateScore >= baseline) continue;
                // Keep cheaper travel with more jumps: the fuel penalty can change which arrival wins later.
                var arrivals = labels[next];
                bool dominated = false;
                foreach (var label in arrivals)
                    if (label.Cost <= total && label.Hops <= current.Hops + 1) { dominated = true; break; }
                if (dominated) continue;
                for (int i = arrivals.Count - 1; i >= 0; i--)
                    if (total <= arrivals[i].Cost && current.Hops + 1 <= arrivals[i].Hops) arrivals.RemoveAt(i);
                var candidate = new Label(next, total, length, current.Hops + 1, current);
                labels[next].Add(candidate); queue.Enqueue(candidate, candidateScore);
                // Bound work on the simulation thread; exhaustion fails closed without touching the mission.
                if (++created > 8192) return null;
            }
        }
        return null;
    }

    private sealed record Label(int Node, double Cost, double Distance, int Hops, Label? Previous);
}
