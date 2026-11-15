namespace DW2Mod;

// Two small tests every mod that holds spending to a budget needs: whether a cached figure of the game is real, and whether the player's own hand is behind a call.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class Spending
{
    // the game's cached figures are float.MinValue / MaxValue placeholders until it has filled them (just after a load)
    public static bool Plausible(float value) => Math.Abs(value) < 1e30f;

    // true when the call chain runs through the game's interface: the player's own button, click or accepted suggestion
    public static bool ByPlayersHand()
    {
        foreach (var frame in new System.Diagnostics.StackTrace(2, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
        {
            string space = frame.GetMethod()?.DeclaringType?.Namespace;
            if (space == "DistantWorlds.UI" || space == "DistantWorlds2")
            {
                return true;
            }
        }
        return false;
    }
}
