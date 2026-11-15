using DistantWorlds.Types;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private sealed record DesignProfile(string Names, RouteCapability Capability, TransitGroup Group, double ExitMargin)
    { internal bool Reference { get; init; } }
    private sealed record NetworkExample(int From, int To, int Profile, bool NewRoute, double SecondsSaved,
        double Seconds, Waypoint[] SharedNodes)
    {
        internal Waypoint[] Before { get; init; } = [];
        internal Waypoint[] After { get; init; } = [];
        internal double BeforeSeconds { get; init; }
    }
    private sealed record PairOutcome(int Profile, RouteAssessment? Result, string? Error);

    private static DesignProfile[] CurrentDesignProfiles(Campaign campaign)
    {
        var empire = campaign.Observer;
        var used = empire.Ships.Where(s => s != null && !s.IsDestroyed).Select(s => s.DesignId).ToHashSet();
        var profiles = new List<DesignProfile>();
        foreach (var design in empire.Designs)
        {
            if (design?.Summary is not { HyperDriveJumpRange: > 0, HyperDriveSpeed: > 0 } summary ||
                design.Obsolete && !used.Contains(design.DesignId)) continue;
            double minimum = Math.Max(3200, 2.5 * Math.Max(800, summary.HyperDriveJumpAccuracy == 0 ? 2000 : summary.HyperDriveJumpAccuracy));
            var capability = new RouteCapability(minimum, summary.HyperDriveJumpRange, summary.CalculateMaximumFuelRange(),
                summary.HyperDriveSpeed, Math.Max(0, summary.HyperDriveJumpInitiationTime));
            if (!capability.Valid) continue;
            var group = TransitRules.Group(design.GetRole(empire.Galaxy));
            double margin = 20_000 + Positive(summary.HyperDriveJumpAccuracy);
            int matching = profiles.FindIndex(p => p.Capability == capability && p.Group == group && p.ExitMargin == margin);
            if (matching < 0) profiles.Add(new(design.Name, capability, group, margin));
            else profiles[matching] = profiles[matching] with { Names = profiles[matching].Names + ", " + design.Name };
        }
        return profiles.ToArray();
    }

    private sealed class NetworkAnalysis
    {
        internal readonly DesignProfile[] Profiles;
        internal readonly StarSystem[] Systems;
        internal readonly List<NetworkExample> Examples = new();
        internal readonly Waypoint[] Points;
        internal readonly bool Diagnostic;
        internal readonly bool Reference;
        internal readonly string? ReferenceError;
        internal readonly Waypoint? Focus;
        internal readonly (int From, int To)? Pair;
        internal readonly List<PairOutcome> Outcomes = new();
        internal readonly object TransitRevision;
        internal readonly long StartedAt;
        internal readonly int Geometry;
        internal bool Running = true, Stale, Cancelled;
        internal long Examined, FasterCount, NewCount;
        internal double LargestSliceMilliseconds;
        private readonly List<(int From, int To, int Profile)> candidates = new();
        private readonly Dictionary<(Waypoint, Waypoint), double> terrain = new();
        private int preparedProfiles, cursor;
        private long nextSlice, nextValidation;
        internal long Total => candidates.Count;
        internal string Status => (Reference ? "Reference forecast — " : "") + (Stale ? "Results stale — analyze again" : Cancelled ? "Analysis cancelled; partial examples" :
            Profiles.Length == 0 ? ReferenceError ?? "No current design can make hyperjumps" :
            Points.Length == 0 && Pair == null ? "No waypoint nodes available for this analysis" :
            Running && preparedProfiles < Profiles.Length ? $"Preparing global examples: profile {preparedProfiles + 1}/{Profiles.Length}" :
            Running ? $"{(Diagnostic ? Focus == null ? "DEBUG network" : "DEBUG focus" : "Bounded global")} scan {Examined:N0}/{Total:N0}; verified examples {FasterCount + NewCount:N0}" :
                $"{(Diagnostic ? Focus == null ? "DEBUG network" : "DEBUG focus" : "Scan")} complete: faster examples {FasterCount:N0}, newly feasible {NewCount:N0}");

        internal NetworkAnalysis(Campaign campaign) : this(campaign, false, null) { }

        internal NetworkAnalysis(Campaign campaign, bool diagnostic, Waypoint? focus = null)
            : this(campaign, diagnostic, focus, null) { }

        internal NetworkAnalysis(Campaign campaign, bool diagnostic, Waypoint? focus, (int From, int To)? pair)
        {
            Profiles = AnalysisProfiles(campaign, diagnostic, out var referenceError);
            ReferenceError = referenceError;
            Reference = Profiles.Any(profile => profile.Reference);
            Systems = campaign.Observer.Galaxy.Systems.Values.Where(s => s?.Star != null).OrderBy(s => s.SystemId).ToArray();
            Diagnostic = diagnostic;
            Focus = focus;
            Pair = pair;
            // The complete display snapshot is copied only into this explicit, cancellable report.
            // It is never assigned to Campaign.ActivePoints or any native/shared route cache.
            Points = diagnostic ? campaign.DisplayPoints : campaign.ActivePoints; TransitRevision = campaign.Transit!.Revision;
            StartedAt = campaign.Observer.Galaxy.StarDate; Geometry = NetworkGeometry(campaign.Observer.Galaxy);
            if (Profiles.Length == 0 || Systems.Length < 2 || Points.Length == 0 && Pair == null) Running = false;
        }

        internal void Tick(Campaign campaign)
        {
            long now = Environment.TickCount64;
            if (Stale || now < nextSlice) return;
            nextSlice = now + (Running ? 50 : 1_000);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Exact cloud values include permeability. The terrain cache is never reused across a changed geometry snapshot.
            if (!ReferenceEquals(Points, Diagnostic ? campaign.DisplayPoints : campaign.ActivePoints) || !ReferenceEquals(TransitRevision, campaign.Transit!.Revision) ||
                Geometry != NetworkGeometry(campaign.Observer.Galaxy))
            { Invalidate(); return; }
            if (now >= nextValidation)
            {
                nextValidation = now + 1_000;
                if (!Profiles.SequenceEqual(AnalysisProfiles(campaign, Diagnostic, out _))) { Invalidate(); return; }
            }
            if (!Running) return;
            if (preparedProfiles < Profiles.Length)
            {
                var anchors = Systems.Select(s => new RouteAnchor(s.SystemId, new(s.GalaxyX, s.GalaxyY))).ToArray();
                if (Pair is { } requested)
                {
                    int from = Array.FindIndex(Systems, s => s.SystemId == requested.From);
                    int to = Array.FindIndex(Systems, s => s.SystemId == requested.To);
                    if (from >= 0 && to >= 0 && from != to) candidates.Add((from, to, preparedProfiles));
                    else Outcomes.Add(new(preparedProfiles, null, "Choose two different valid systems"));
                }
                else foreach (var pair in AnalysisCandidates.Build(anchors, Points, Profiles[preparedProfiles].Capability, Focus))
                    candidates.Add((pair.From, pair.To, preparedProfiles));
                preparedProfiles++;
            }
            else
            {
                int evaluated = 0;
                while (cursor < candidates.Count && evaluated < 4 && (evaluated == 0 || timer.ElapsedMilliseconds < 8))
                { Evaluate(campaign, candidates[cursor++]); Examined++; evaluated++; }
                if (cursor == candidates.Count) Running = false;
            }
            LargestSliceMilliseconds = Math.Max(LargestSliceMilliseconds, timer.Elapsed.TotalMilliseconds);
        }

        private void Invalidate() { Stale = true; Running = false; Examples.Clear(); Outcomes.Clear(); terrain.Clear(); }

        private void Evaluate(Campaign campaign, (int From, int To, int Profile) pair)
        {
            var a = Systems[pair.From]; var b = Systems[pair.To]; var current = Profiles[pair.Profile];
            if (a.Star.GetLocation(campaign.Observer.Galaxy) is { } start && b.Star.GetLocation(campaign.Observer.Galaxy) is { } goal)
            {
                double Cost(Waypoint from, Waypoint to)
                {
                    if (terrain.TryGetValue((from, to), out double cost)) return cost;
                    cost = LineCost(campaign.Observer.Galaxy, from, to);
                    // Keep memory bounded; an uncached edge is still evaluated accurately.
                    if (terrain.Count < 65_536) terrain[(from, to)] = cost;
                    return cost;
                }
                var result = AssessRoute(campaign, a.Star, start, goal, current.Capability, current.Group, current.ExitMargin, Cost, Points);
                if (Pair != null) Outcomes.Add(new(pair.Profile, result,
                    result == null ? "Route unavailable: check transit, danger and reachability" : null));
                bool networkFuel = result?.NetworkWithinFuel == true;
                // A selected node must earn its own benefit; the rest of the network remains in the baseline.
                var withoutFocus = Focus is { } focus ? AssessRoute(campaign, a.Star, start, goal,
                    current.Capability, current.Group, current.ExitMargin, Cost, Points.Where(p => p != focus).ToArray()) : null;
                var baseline = Focus != null ? withoutFocus?.Network : result?.Baseline;
                bool baselineFuel = baseline != null && baseline.Cost < current.Capability.PoweredRange;
                double beforeSeconds = baseline == null ? double.PositiveInfinity : current.Capability.Seconds(baseline.Cost, baseline.Nodes.Length - 1);
                if (networkFuel && (!baselineFuel || result!.NetworkSeconds < beforeSeconds - .001) && result!.Network!.Nodes.Any(n => n.LocationId < 0) &&
                    (Focus == null || result.Network.Nodes.Any(n => n.LocationId < 0 && n.Point == Focus.Value)))
                {
                    bool newlyFeasible = !baselineFuel;
                    if (newlyFeasible) NewCount++; else FasterCount++;
                    var example = new NetworkExample(a.SystemId, b.SystemId, pair.Profile, newlyFeasible,
                        newlyFeasible ? 0 : beforeSeconds - result.NetworkSeconds, result.NetworkSeconds,
                        result.Network!.Nodes.Where(n => n.LocationId < 0).Select(n => n.Point).ToArray())
                    {
                        Before = baselineFuel ? baseline!.Nodes.Select(n => n.Point).ToArray() : [],
                        After = result.Network.Nodes.Select(n => n.Point).ToArray(), BeforeSeconds = beforeSeconds
                    };
                    Examples.Add(example);
                    if (Examples.Count > 64)
                    {
                        var least = Examples.Where(e => !e.NewRoute).MinBy(e => e.SecondsSaved) ?? Examples[0];
                        Examples.Remove(least);
                    }
                }
            }
            else if (Pair != null) Outcomes.Add(new(pair.Profile, null, "System location unavailable"));
        }
    }

    private static int NetworkGeometry(Galaxy galaxy)
    {
        var hash = new HashCode();
        foreach (var cloud in galaxy.NebulaClouds)
        {
            if (cloud == null) continue;
            hash.Add(cloud.GalaxyX); hash.Add(cloud.GalaxyY); hash.Add(cloud.Radius); hash.Add(cloud.Permeability);
            if (cloud.EffectGroups is not { } effects) continue;
            effects.GetMaximumEffectAmountAll(galaxy, out float a, out float b, out float c, out float d,
                out float e, out float f, out float g, out float h, out float i, out float j);
            hash.Add(a); hash.Add(b); hash.Add(c); hash.Add(d); hash.Add(e); hash.Add(f); hash.Add(g); hash.Add(h); hash.Add(i); hash.Add(j);
        }
        if (galaxy.Empires.GetPlayer()?.PathFindingDangerousSystems is { } danger)
            foreach (bool blocked in danger) hash.Add(blocked);
        return hash.ToHashCode();
    }
}
