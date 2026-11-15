using System.Reflection;

namespace DW2Mod;

// Reports to the mod launcher's shared in-game status widget (docs/Mod Status Line.md in DW2-Mod-Launcher), if it is there.
//
// A soft dependency: there is no compile-time reference to the loader. The loader's DW2ModLauncher.Loader.ModStatus is found by name
// among the loaded assemblies, and every call is a delegate created once. When the dll is injected directly (no launcher) Present is
// false, every call is a no-op, and the mod does nothing extra. When the launcher is there it draws one line
// and one panel for all code mods.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class LauncherStatus
{
    public static readonly bool Present;

    // set when the launcher's loader is there but its status interface could not be bound
    public static readonly string BindFailure;

    // the loader registers every mod under its dll's file name without the extension, which is the assembly name
    static readonly string id = typeof(LauncherStatus).Assembly.GetName().Name;
    static readonly Action<string, string, string, string> register;
    static readonly Action<string, int, string> setLevel;
    static readonly Action<string, string, string, int> detail;
    static readonly Action<string, string> error;
    static readonly Action<string, string> installed;
    static readonly Action<string, string> setLog;

    static LauncherStatus()
    {
        try
        {
            Type status = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "DW2ModLauncher.Loader")
                {
                    status = a.GetType("DW2ModLauncher.Loader.ModStatus");
                    break;
                }
            }
            if (status == null)
            {
                return;
            }
            register = Bind<Action<string, string, string, string>>(status, "Register");
            setLevel = Bind<Action<string, int, string>>(status, "SetLevel");
            detail = Bind<Action<string, string, string, int>>(status, "Detail");
            error = Bind<Action<string, string>>(status, "Error");
            installed = Bind<Action<string, string>>(status, "Installed");
            setLog = Bind<Action<string, string>>(status, "SetLog");
            Present = true;
        }
        catch (Exception ex)
        {
            // an older or odd loader: act as if it were not there, and say why (ModHost logs it once the log is open)
            Present = false;
            BindFailure = ex.Message;
        }
    }

    static T Bind<T>(Type type, string name) where T : Delegate
        => (T)Delegate.CreateDelegate(typeof(T), type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(name));

    // name is shown in place of the assembly name; version and built are what the loader already read from the dll, so pass null to keep them
    public static void Register(string name, string version, string built) => register?.Invoke(id, name, version, built);

    // level: 0 ok, 1 info, 2 warn, 3 error
    public static void SetLevel(int level, string summary) => setLevel?.Invoke(id, level, summary);

    // A named line in the expanded panel, by symbolic keys (Loc.T): labelKey is the line's name, textKey its sentence with named values. The name is translated like
    // the text, and the same name again replaces the line.
    public static void Line(string labelKey, string textKey, params (string Name, object Value)[] values) => detail?.Invoke(id, Loc.T(labelKey), Loc.T(textKey, values), 0);

    public static void Error(string text) => error?.Invoke(id, text);

    // the launcher has no warning list of its own: a warning is a panel line at level 2, keyed by its text so a repeat is one line, and
    // the launcher raises the mod's overall level to the worst of its lines (yellow)
    public static void Warn(string text) => detail?.Invoke(id, "warning: " + text, text, 2);

    // removes a line Warn put up (the same text again), for a problem that has been put right
    public static void ClearWarn(string text) => detail?.Invoke(id, "warning: " + text, "", 2);

    public static void Installed(string feature) => installed?.Invoke(id, feature);

    public static void SetLog(string path) => setLog?.Invoke(id, path);
}
