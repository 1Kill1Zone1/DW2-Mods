namespace GalaxyWaypoints;

internal static class AnalysisCandidates
{
    // Global examples: every active node contributes reachable quadrant endpoints; each connected corridor also contributes extremes.
    internal static (int From, int To)[] Build(IReadOnlyList<RouteAnchor> systems, IReadOnlyList<Waypoint> points, RouteCapability capability,
        Waypoint? focus = null)
    {
        if (focus is { } selected && !points.Contains(selected)) return [];
        // Focus filters accepted examples, not candidate construction: retain the same bounded global
        // component coverage so a multi-hop corridor is never truncated at an immediate neighbour.
        var pairs = new HashSet<(int, int)>();
        var endpoints = new List<int>[points.Count];
        var parent = Enumerable.Range(0, points.Count).ToArray();
        int Root(int node) { while (parent[node] != node) node = parent[node] = parent[parent[node]]; return node; }
        void AddPairs(IEnumerable<int> source)
        {
            int[] ids = source.Distinct().Order().ToArray();
            for (int a = 0; a < ids.Length; a++) for (int b = a + 1; b < ids.Length; b++) pairs.Add((ids[a], ids[b]));
        }
        for (int p = 0; p < points.Count; p++)
        {
            int[] nearest = [-1, -1, -1, -1];
            double[] distance = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
            for (int s = 0; s < systems.Count; s++)
            {
                double length = RouteChoice.Distance(points[p], systems[s].Point);
                if (!RouteChoice.CanJump(length, capability.MinimumJump, capability.JumpRange)) continue;
                int quadrant = (systems[s].Point.X < points[p].X ? 1 : 0) + (systems[s].Point.Y < points[p].Y ? 2 : 0);
                if (length < distance[quadrant]) { distance[quadrant] = length; nearest[quadrant] = s; }
            }
            endpoints[p] = nearest.Where(i => i >= 0).ToList();
            AddPairs(endpoints[p]);
            for (int previous = 0; previous < p; previous++)
                if (RouteChoice.CanJump(RouteChoice.Distance(points[previous], points[p]), capability.MinimumJump, capability.JumpRange))
                    parent[Root(p)] = Root(previous);
        }
        foreach (var component in Enumerable.Range(0, points.Count).GroupBy(Root))
        {
            int[] candidates = component.SelectMany(index => endpoints[index]).Distinct().ToArray();
            if (candidates.Length == 0) continue;
            AddPairs([candidates.MinBy(i => systems[i].Point.X), candidates.MaxBy(i => systems[i].Point.X),
                candidates.MinBy(i => systems[i].Point.Y), candidates.MaxBy(i => systems[i].Point.Y)]);
        }
        // Directed transit rules can make the reverse course differ, so evaluate both directions.
        return pairs.OrderBy(p => p.Item1).ThenBy(p => p.Item2).SelectMany(p => new[] { p, (p.Item2, p.Item1) }).ToArray();
    }
}
