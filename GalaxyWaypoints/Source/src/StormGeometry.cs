namespace GalaxyWaypoints;

internal readonly record struct Storm(Waypoint Center, double Radius)
{
    public bool Contains(Waypoint point, double margin) => RouteChoice.Distance(Center, point) <= Radius + margin;

    public IEnumerable<Waypoint> Candidates(Waypoint from, Waypoint to, double range, double margin)
    {
        double rim = Radius + margin + 10_000;
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.Tau / 24;
            yield return OnRim(rim, angle);
        }

        foreach (Waypoint point in RimIntersections(from, range, rim))
            yield return point;
        foreach (Waypoint point in RimIntersections(to, range, rim))
            yield return point;
    }

    private IEnumerable<Waypoint> RimIntersections(Waypoint anchor, double range, double rim)
    {
        double distance = RouteChoice.Distance(Center, anchor);
        if (!(distance > 0) || distance > rim + range || distance < Math.Abs(rim - range))
            yield break;

        double heading = Math.Atan2((double)anchor.Y - Center.Y, (double)anchor.X - Center.X);
        double arc = Math.Acos(Math.Clamp((rim * rim + distance * distance - range * range) / (2 * rim * distance), -1, 1));
        yield return OnRim(rim, heading + arc * .999);
        yield return OnRim(rim, heading - arc * .999);
    }

    private Waypoint OnRim(double radius, double angle) => new(
        (int)Math.Round(Center.X + radius * Math.Cos(angle)),
        (int)Math.Round(Center.Y + radius * Math.Sin(angle)));
}
