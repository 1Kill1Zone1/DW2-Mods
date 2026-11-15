namespace GalaxyWaypoints;

internal readonly record struct RouteCapability(double MinimumJump, double JumpRange,
    double PoweredRange, double Speed, double Preparation)
{
    internal bool Valid => double.IsFinite(MinimumJump) && double.IsFinite(JumpRange) &&
        double.IsFinite(PoweredRange) && double.IsFinite(Speed) && double.IsFinite(Preparation) &&
        MinimumJump > 0 && JumpRange >= MinimumJump && PoweredRange >= 0 && Speed > 0 && Preparation >= 0;
    internal double Seconds(double cost, int jumps) => RouteChoice.TravelCost(cost, PoweredRange) / Speed + jumps * Preparation;
}

// Native path costs are distance-equivalent terrain costs; elapsed seconds are deliberately separate.
internal sealed record RouteAssessment(WaypointPlan? Baseline, WaypointPlan? Network, RouteCapability Capability)
{
    internal bool NewlyFeasible => Baseline == null && Network != null;
    internal bool NetworkWithinFuel => Network is { } network && network.Cost < Capability.PoweredRange;
    internal bool BaselineWithinFuel => Baseline is { } baseline && baseline.Cost < Capability.PoweredRange;
    internal double BaselineSeconds => Baseline == null ? double.PositiveInfinity : Capability.Seconds(Baseline.Cost, Baseline.Nodes.Length - 1);
    internal double NetworkSeconds => Network == null ? double.PositiveInfinity : Capability.Seconds(Network.Cost, Network.Nodes.Length - 1);
    internal bool Faster => Network != null && Baseline != null && NetworkSeconds < BaselineSeconds - 0.001;
    internal static RouteAssessment Evaluate(IReadOnlyList<RouteAnchor> anchors, IReadOnlyList<Waypoint> activePoints,
        bool hasOrdinaryPath, RouteCapability capability, Func<Waypoint, bool> safe,
        Func<Waypoint, Waypoint, double> lineCost)
    {
        if (!capability.Valid) return new(null, null, capability);
        var costs = new Dictionary<(Waypoint, Waypoint), double>();
        double Cost(Waypoint a, Waypoint b)
        {
            if (!costs.TryGetValue((a, b), out double value)) costs[(a, b)] = value = lineCost(a, b);
            return value;
        }
        WaypointPlan? Find(IReadOnlyList<Waypoint> points) => WaypointRouting.Find(anchors, points,
            hasOrdinaryPath, capability.MinimumJump, capability.JumpRange, capability.PoweredRange,
            capability.Preparation * capability.Speed, safe, Cost, allowOrdinaryResult: true, estimate: capability.Seconds);
        var baseline = Find(Array.Empty<Waypoint>());
        return new(baseline, activePoints.Count == 0 ? baseline : Find(activePoints), capability);
    }
}
