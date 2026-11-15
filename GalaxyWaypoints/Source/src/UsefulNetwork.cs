namespace GalaxyWaypoints;

internal sealed record GenerationProfile(string Name, RouteCapability Capability);
internal sealed record GenerationProof(Waypoint Node, int From, int To, GenerationProfile Profile,
    double BeforeSeconds, double AfterSeconds, double WithoutNodeSeconds, Waypoint[] Before, Waypoint[] After);
internal sealed record ReferencePairResult(GenerationProfile Profile, double BeforeSeconds, double AfterSeconds,
    Waypoint[] Before, Waypoint[] After);

// Pure, resumable search. Ordinary stars remain in every comparison; only automatic nodes are removed.
internal sealed class UsefulNetwork
{
    internal const int MaxCandidates = 512, MaxPairs = 192, MaxNodes = 64;
    private const int MaxLabels = 8192, MaxSearches = 12000, MaxEdges = 600000;
    private readonly RouteAnchor[] stars;
    private readonly Waypoint[] points;
    private readonly GenerationProfile[] profiles;
    private readonly Func<Waypoint, Waypoint, double> cost;
    private readonly List<(int To, double Cost, double Length)>[] edges;
    private readonly IEnumerator<int> work;
    private readonly List<int> selected = [];
    private readonly List<Proposal> proposals = [];
    private int searches, edgeCount;
    internal bool Complete { get; private set; }
    internal bool Cancelled { get; private set; }
    internal string? Error { get; private set; }
    internal string Status { get; private set; } = "Preparing useful corridors";
    internal GenerationProof[] Proofs { get; private set; } = [];
    internal ReferencePairResult[] PairResults { get; private set; } = [];
    internal Waypoint[] Result => Complete && Error == null && !Cancelled ? selected.Select(i => points[i]).ToArray() : [];
    internal double LargestSliceMilliseconds { get; private set; }
    private sealed record Proposal(int From, int To, int Profile, int[] Nodes, double Before, double After);
    private sealed record Path(int[] Nodes, double Cost, double Seconds);
    private sealed record Label(int Node, double Cost, double Seconds, Label? Previous);

    internal UsefulNetwork(RouteAnchor[] stars, Waypoint[] candidates, GenerationProfile[] profiles,
        Func<Waypoint, Waypoint, double> cost, (int From, int To)? pair = null)
    {
        if (stars.Length > 2000 || candidates.Length > MaxCandidates || profiles.Length is < 1 or > 3 ||
            stars.Select(s => s.Point).Distinct().Count() != stars.Length || profiles.Any(p => !p.Capability.Valid || p.Capability.PoweredRange <= 0))
            throw new ArgumentException("Unsupported generation input; existing network retained");
        this.stars = stars.ToArray(); this.profiles = profiles.ToArray(); this.cost = cost;
        points = stars.Select(s => s.Point).Concat(candidates.Where(p => !stars.Any(s => s.Point == p)).Distinct()).ToArray();
        edges = points.Select(_ => new List<(int, double, double)>()).ToArray();
        work = (pair is { } requested ? CalculatePair(requested.From, requested.To) : Calculate()).GetEnumerator();
    }
    internal void Cancel() { if (Complete) return; Cancelled = Complete = true; Status = "Generation cancelled; existing network retained"; work.Dispose(); }
    internal void Tick(double milliseconds = 6)
    {
        if (Complete) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            do { if (!work.MoveNext()) { Complete = true; break; } } while (timer.Elapsed.TotalMilliseconds < milliseconds);
        }
        catch (Exception ex) { Error = ex.Message; Status = "Generation incomplete: " + ex.Message; Complete = true; }
        finally { LargestSliceMilliseconds = Math.Max(LargestSliceMilliseconds, timer.Elapsed.TotalMilliseconds); if (Complete) work.Dispose(); }
    }
    internal static bool Benefit(double before, double after) => double.IsFinite(after) &&
        (!double.IsFinite(before) || before - after >= 10 - 1e-8 && before - after >= before * .05 - 1e-8);

    private IEnumerable<int> Calculate()
    {
        if(points.Length==stars.Length){Status="No safe candidate positions; existing network retained";yield break;}
        foreach (int step in PrepareEdges()) yield return step;
        foreach (int step in SelectNetwork()) yield return step;
    }
    private IEnumerable<int> PrepareEdges()
    {
        double max = profiles.Max(p => p.Capability.JumpRange), min = profiles.Min(p => p.Capability.MinimumJump);
        for (int a = 0; a < points.Length; a++)
        {
            Status = $"Geometry {a + 1}/{points.Length}; checked edges {edgeCount}";
            for (int b = 0; b < points.Length; b++)
            {
                if (a == b) continue;
                double length = RouteChoice.Distance(points[a], points[b]);
                if (length >= min && length <= max)
                {
                    if (++edgeCount > MaxEdges) throw new InvalidOperationException("edge budget exhausted");
                    double value = cost(points[a], points[b]);
                    if (double.IsNaN(value) || value <= 0) throw new InvalidOperationException("invalid native terrain cost");
                    if (double.IsFinite(value)) edges[a].Add((b, value, length));
                }
                if (b % 32 == 0) yield return 0;
            }
        }
    }
    private IEnumerable<int> CalculatePair(int from, int to)
    {
        if (from == to || (uint)from >= stars.Length || (uint)to >= stars.Length)
            throw new ArgumentException("Choose two different valid systems");
        foreach (int step in PrepareEdges()) yield return step;
        var results = new List<ReferencePairResult>();
        for (int profile = 0; profile < profiles.Length; profile++)
        {
            Status = $"Reference route {profile + 1}/{profiles.Length}";
            var before = Search(from, to, profile, []); foreach (int step in before.Run()) yield return step;
            var after = Search(from, to, profile, null); foreach (int step in after.Run()) yield return step;
            results.Add(new(profiles[profile], before.Result?.Seconds ?? double.PositiveInfinity,
                after.Result?.Seconds ?? double.PositiveInfinity,
                before.Result?.Nodes.Select(i => points[i]).ToArray() ?? [],
                after.Result?.Nodes.Select(i => points[i]).ToArray() ?? []));
        }
        PairResults = results.ToArray(); Status = "Reference pair complete";
    }
    private IEnumerable<int> SelectNetwork()
    {
        var pairs = Pairs();
        for (int p = 0; p < profiles.Length; p++) for (int pair = 0; pair < pairs.Length; pair++)
        {
            Status = $"Checking corridors {p * pairs.Length + pair + 1}/{profiles.Length * pairs.Length}";
            var (a,b) = pairs[pair];
            var baseline = Search(a,b,p,[]); foreach (int step in baseline.Run()) yield return step;
            var proposed = Search(a,b,p,null); foreach (int step in proposed.Run()) yield return step;
            if (proposed.Result is not { } after || !Benefit(baseline.Result?.Seconds ?? double.PositiveInfinity, after.Seconds)) continue;
            int[] nodes = after.Nodes.Where(n => n >= stars.Length).ToArray();
            if (nodes.Length is > 0 and <= MaxNodes) proposals.Add(new(a,b,p,nodes,baseline.Result?.Seconds ?? double.PositiveInfinity,after.Seconds));
        }
        foreach (var proposal in proposals.OrderByDescending(p => !double.IsFinite(p.Before))
            .ThenByDescending(p => double.IsFinite(p.Before) ? (p.Before-p.After)/p.Nodes.Length : 1d/p.Nodes.Length)
            .ThenBy(p => p.From).ThenBy(p => p.To).ThenBy(p => p.Profile))
        {
            var union = selected.Concat(proposal.Nodes).Distinct().Order().ToArray();
            if (union.Length > MaxNodes || union.Length == selected.Count) continue;
            var before = Search(proposal.From,proposal.To,proposal.Profile,selected); foreach (int step in before.Run()) yield return step;
            var after = Search(proposal.From,proposal.To,proposal.Profile,union); foreach (int step in after.Run()) yield return step;
            if (after.Result != null && Benefit(before.Result?.Seconds ?? double.PositiveInfinity,after.Result.Seconds))
            { selected.Clear(); selected.AddRange(union); }
        }
        // Removing a redundant point can change other witnesses: restart until the final set is stable.
        bool removed;
        do
        {
            removed = false;
            var verified = new List<GenerationProof>();
            foreach (int node in selected.ToArray())
            {
                Status = $"Verifying final contribution {verified.Count + 1}/{selected.Count}";
                GenerationProof? proof = null;
                foreach (var proposal in proposals.Where(p => p.Nodes.Contains(node)))
                {
                    var after = Search(proposal.From,proposal.To,proposal.Profile,selected); foreach (int step in after.Run()) yield return step;
                    if (after.Result is not {} path || !path.Nodes.Contains(node)) continue;
                    var baseline = Search(proposal.From,proposal.To,proposal.Profile,[]); foreach (int step in baseline.Run()) yield return step;
                    if (!Benefit(baseline.Result?.Seconds ?? double.PositiveInfinity,path.Seconds)) continue;
                    var without = Search(proposal.From,proposal.To,proposal.Profile,selected.Where(i => i != node)); foreach (int step in without.Run()) yield return step;
                    if (without.Result != null && without.Result.Seconds <= path.Seconds + 1e-6) continue;
                    proof = new(points[node],stars[proposal.From].LocationId,stars[proposal.To].LocationId,profiles[proposal.Profile],
                        baseline.Result?.Seconds ?? -1,path.Seconds,without.Result?.Seconds ?? -1,
                        baseline.Result?.Nodes.Select(i => points[i]).ToArray() ?? [],path.Nodes.Select(i => points[i]).ToArray());
                    break;
                }
                if (proof == null) { selected.Remove(node); removed = true; break; }
                verified.Add(proof);
            }
            if (!removed) Proofs = verified.ToArray();
        } while (removed);
        Status = selected.Count == 0 ? "No verified useful corridors found in bounded search" : $"Verified {selected.Count} useful nodes; {profiles.Length} reference profiles";
    }
    private (int,int)[] Pairs()
    {
        var pairs = new HashSet<(int,int)>();
        foreach (var point in points.Skip(stars.Length))
        {
            int[] nearest = [-1,-1,-1,-1]; double[] distance = [double.MaxValue,double.MaxValue,double.MaxValue,double.MaxValue];
            for(int i=0;i<stars.Length;i++)
            {
                double d = RouteChoice.Distance(point,points[i]);
                int quadrant=(points[i].X<point.X?1:0)+(points[i].Y<point.Y?2:0);
                if(d<distance[quadrant]){distance[quadrant]=d;nearest[quadrant]=i;}
            }
            var ids=nearest.Where(i=>i>=0).Distinct().Order().ToArray();
            for(int a=0;a<ids.Length;a++)for(int b=a+1;b<ids.Length;b++)pairs.Add((ids[a],ids[b]));
        }
        // Coordinate hashing spreads the bounded sample across the map rather than favouring low star IDs.
        return pairs.OrderBy(p => unchecked((uint)(points[p.Item1].X*73856093 ^ points[p.Item2].Y*19349663)))
            .ThenBy(p=>p.Item1).ThenBy(p=>p.Item2).Take(MaxPairs).ToArray();
    }
    private SearchState Search(int from,int to,int profile,IEnumerable<int>? allowed)
    {
        if(++searches>MaxSearches)throw new InvalidOperationException("route budget exhausted");
        return new(this,from,to,profiles[profile].Capability,allowed?.ToHashSet());
    }
    private sealed class SearchState(UsefulNetwork owner,int from,int goal,RouteCapability capability,HashSet<int>? allowed)
    {
        internal Path? Result;
        internal IEnumerable<int> Run()
        {
            var labels=owner.points.Select(_=>new List<Label>()).ToArray();
            var queue=new PriorityQueue<Label,double>();var start=new Label(from,0,0,null);labels[from].Add(start);queue.Enqueue(start,0);
            int created=1,steps=0;
            while(queue.TryDequeue(out var current,out _))
            {
                if(!labels[current.Node].Contains(current))continue;
                if(current.Node==goal)
                {
                    var path=new List<int>();for(var p=current;p!=null;p=p.Previous)path.Add(p.Node);path.Reverse();
                    Result=new(path.ToArray(),current.Cost,current.Seconds);yield break;
                }
                foreach(var edge in owner.edges[current.Node])
                {
                    if(++steps%128==0)yield return 0;
                    if(edge.To>=owner.stars.Length && allowed!=null&&!allowed.Contains(edge.To) ||
                        !RouteChoice.CanJump(edge.Length,capability.MinimumJump,capability.JumpRange))continue;
                    double cost=current.Cost+edge.Cost;
                    if(cost>=capability.PoweredRange)continue;
                    double seconds=current.Seconds+edge.Cost/capability.Speed+capability.Preparation;
                    var arrivals=labels[edge.To];
                    if(arrivals.Any(l=>l.Cost<=cost&&l.Seconds<=seconds))continue;
                    arrivals.RemoveAll(l=>cost<=l.Cost&&seconds<=l.Seconds);
                    var next=new Label(edge.To,cost,seconds,current);arrivals.Add(next);queue.Enqueue(next,seconds);
                    if(++created>MaxLabels)throw new InvalidOperationException("search label budget exhausted");
                }
                yield return 0;
            }
        }
    }
}
