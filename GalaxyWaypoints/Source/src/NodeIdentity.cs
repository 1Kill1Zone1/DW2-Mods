namespace GalaxyWaypoints;

public static partial class Entry
{
    // Derived from immutable saved identity, never from the number of undiscovered nodes.
    private static string NodeId(Campaign campaign, Waypoint point)
    {
        if (campaign.FindGenerated(point) is { } generated && !campaign.GenerationProofs.Any(p=>p.Node==point)) return $"A-{generated.AnchorSystemId:X4}";
        ulong value = ((ulong)(uint)point.X << 32) | (uint)point.Y;
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        Span<char> buffer = stackalloc char[13];
        int cursor = buffer.Length;
        do { buffer[--cursor] = digits[(int)(value % 36)]; value /= 36; } while (value != 0);
        return (campaign.FindGenerated(point)!=null?"G-":"M-") + new string(buffer[cursor..]);
    }
}
