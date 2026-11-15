using System.Reflection;

namespace DW2Mod;

// Adds entries to the mod launcher's in-game Mods menu (docs/Mod Menu.md in DW2-Mod-Launcher), if it is there.
//
// A soft dependency like LauncherStatus: no compile-time reference to the loader; DW2ModLauncher.Loader.ModMenu is found by name among the
// loaded assemblies and bound once. Without the launcher (the dll injected directly) Present is false and every call is a no-op.
//
// A mod has one button in the Mods menu, its group (Group); the entries it adds (a button per world, say) are listed in the dialog that button
// opens. Each entry has a key of the mod's own, unique within the mod, and the dialog lists them alphabetically by label. The handler runs on
// the game's input thread inside a click, so keep it quick.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class LauncherMenu
{
    public static readonly bool Present;

    // set when the launcher's loader is there but its menu could not be bound
    public static readonly string BindFailure;

    static readonly string mod = typeof(LauncherMenu).Assembly.GetName().Name;
    static readonly Action<string, string> addGroup;
    static readonly Action<string, string, string, Action> addChild;
    static readonly Action<string> remove;
    static readonly Action<string, bool> setEnabled;
    // missing in loaders older than the ReturnToGame request: the click then leaves the Game Menu up, which is all that is lost
    static readonly Action returnToGame;

    static LauncherMenu()
    {
        try
        {
            Type menu = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "DW2ModLauncher.Loader")
                {
                    menu = a.GetType("DW2ModLauncher.Loader.ModMenu");
                    break;
                }
            }
            if (menu == null)
            {
                return;
            }
            addGroup = Bind<Action<string, string>>(menu, "AddGroup");
            addChild = Bind<Action<string, string, string, Action>>(menu, "AddChild");
            remove = Bind<Action<string>>(menu, "Remove");
            setEnabled = Bind<Action<string, bool>>(menu, "SetEnabled");
            MethodInfo ret = menu.GetMethod("ReturnToGame", BindingFlags.Public | BindingFlags.Static);
            returnToGame = ret == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), ret);
            Present = true;
        }
        catch (Exception ex)
        {
            Present = false;
            BindFailure = ex.Message;
        }
    }

    static T Bind<T>(Type type, string name) where T : Delegate
        => (T)Delegate.CreateDelegate(typeof(T), type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(name));

    static string Id(string key) => mod + "/" + key;

    // The mod's one button under Mods, labelled with the mod's name; picking it opens the dialog of its entries. Safe to call again.
    public static void Group(string label) => addGroup?.Invoke(mod, label);

    // Adds the entry to the mod's dialog, or replaces the label and handler of this mod's entry with the same key.
    public static void Add(string key, string label, Action onClick) => addChild?.Invoke(mod, Id(key), label, onClick);

    public static void Remove(string key) => remove?.Invoke(Id(key));

    // A greyed-out entry ignores clicks (a line of text in the dialog, such as "none yet").
    public static void SetEnabled(string key, bool enabled) => setEnabled?.Invoke(Id(key), enabled);

    // Closes the Game Menu and resumes the game; call it from a handler whose job is done in the game itself.
    public static void ReturnToGame() => returnToGame?.Invoke();
}
