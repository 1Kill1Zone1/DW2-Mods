using System.Reflection;

namespace DW2Mod;

// Whether a real game exists yet, from the launcher's loader (DW2ModLauncher.Loader.GameState, docs/DLL Injection.md in DW2-Mod-Launcher,
// "Game state"). The game loads its data and builds galaxies for more than the game: the menu's background, the new-game wizard's preview.
// A mod that checks the game's data against its own expectations, and says so when they differ, would be judging data that is about to be
// replaced. The loader counts the real games: Generation is 0 until a real game starts (a new game, after its data is loaded and before
// its galaxy is generated; a saved game, before it is read), and goes up by one at each start.
//
//   - warn about the game's data only when Ready, with ModHost.DataWarn;
//   - keep the Generation with anything cached from the game's data, and build it again when it changes.
//
// A soft dependency like LiveChannel: found by name among the loaded assemblies and bound once. Without the launcher's loader, or with one
// that has no game state, Present is false and Generation stays 0: a gate that lacks the information answers no, so nothing warns about
// data (ModHost reports the old loader once).
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class RealGame
{
    public static readonly bool Present;

    // set when the launcher's loader is there but has no game state (an older loader)
    public static readonly string BindFailure;

    static readonly Func<int> generation;

    static RealGame()
    {
        try
        {
            Type state = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "DW2ModLauncher.Loader")
                {
                    state = a.GetType("DW2ModLauncher.Loader.GameState");
                    if (state == null)
                    {
                        BindFailure = "the launcher's loader does not tell when a game starts (update the DW2 Mod Launcher)";
                    }
                    break;
                }
            }
            if (state == null)
            {
                return;
            }
            MethodInfo getter = state.GetProperty("Generation", BindingFlags.Public | BindingFlags.Static)?.GetMethod
                ?? throw new MissingMemberException("GameState.Generation");
            generation = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), getter);
            Present = true;
        }
        catch (Exception ex)
        {
            Present = false;
            BindFailure = ex.Message;
        }
    }

    // 0 until a real game has started, then 1 for the first, 2 for the next, and so on
    public static int Generation => generation?.Invoke() ?? 0;

    public static bool Ready => Generation > 0;
}
