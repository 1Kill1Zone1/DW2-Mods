using System.Reflection;
using System.Text.Json;
using HarmonyLib;

namespace DW2Mod;

// What every code mod does at start-up and for its log, written once (see the logging convention in dll/README.md).
//
// Shared source: each mod compiles its own internal copy, so a mod stays one self-contained dll. A mod's Entry.cs calls Start from its
// Init / InitWithOptions, reads its own settings in the callback, and installs its features with Feature.
//
//   - the log: <logName>.log in the output folder (blank OutputDirectory setting = the game's data/Logs), or, with the hidden
//     LogToSessionFolder setting, in a per-run yyyy-MM-dd_HH-mm-ss folder (the newest ten are kept) with the options file.
//   - every log line carries a level: Info, Warn, Error or Detail. The level is chosen by the call (ModHost.Info / Warn / Error / Detail), never
//     guessed from the words in the text. Error goes to the launcher's status widget as the red banner and Warn as a yellow line; the message
//     itself is free text.
//   - Detail lines are written only with LogToSessionFolder on.
//   - every problem found here (bad options text, a wrong-typed setting, an unwritable log) is reported, never swallowed.
internal static class ModHost
{
    // the mod's assembly name, e.g. DW2ShipLogistics
    public static readonly string ModName = Assembly.GetExecutingAssembly().GetName().Name;
    public static readonly string ModDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    // The hidden LogToSessionFolder setting (developers only, default off). Off: the log is one file written straight into the output
    // folder (the game's data/Logs for end users) holding the start-up lines and errors. On: a session folder per run, the options file,
    // and every Detail line, uncapped. A mod's settings callback may override it (environment variables used by scripts).
    public static bool LogToSessionFolder;

    // the output folder from the OutputDirectory setting (blank: the game's data/Logs); a mod's settings callback may override it
    public static string OutputDirectory = DefaultOutputDirectory();

    // where the log was written: the session folder when LogToSessionFolder is on, otherwise OutputDirectory. Valid once Start has run its
    // install callback.
    public static string LogDirectory { get; private set; }

    // where errors, warnings and installed features go. Default: the launcher's status widget; DW2FreighterLogistics also feeds its own on-screen line.
    public static Action<string> ErrorSink = LauncherStatus.Error;
    public static Action<string> WarnSink = LauncherStatus.Warn;
    public static Action<string> InstalledSink = LauncherStatus.Installed;

    // a warning is one panel line on the launcher's widget; a mod that warns without end must not fill the panel (the log has them all)
    const int MaxWarnings = 10;
    static int warnings;

    const int KeepSessions = 10;
    const string SessionFormat = "yyyy-MM-dd_HH-mm-ss";

    static StreamWriter log;
    static readonly object gate = new();
    // lines logged before the log file is open (settings problems), written first once it is
    static readonly List<string> pending = new();

    // blank OutputDirectory setting: the game's own log folder, <install>/data/Logs (where SessionLog.txt lives). The game runs from
    // its install folder, so that is the folder of the running exe; the working folder if there is no data folder.
    static string DefaultOutputDirectory()
    {
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        return Directory.Exists(data) ? Path.Combine(data, "Logs") : Directory.GetCurrentDirectory();
    }

    // Starts a mod: resolves the mod's own dependencies, reads the options text, lets the mod read its settings, opens the log and runs the
    // install callback. A failure in any step is logged and the rest still runs.
    //   logName          log file name without extension, e.g. "ship-management" (also names the options file)
    //   readSettings     the mod's own settings, read from the ModSettings it is given (plus any environment overrides)
    //   install          installs the features, normally with Feature(...)
    //   writeOptionsFile false when the mod writes a richer <logName>.json itself
    public static void Start(string logName, string json, Action<ModSettings> readSettings, Action install, bool writeOptionsFile = true)
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        ModSettings settings = ModSettings.Parse(json);
        Loc.Use(settings.Text("Language", "en"), settings.Texts("TranslationFiles"));
        LogToSessionFolder = settings.Bool("LogToSessionFolder", false);
        string configured = settings.Text("OutputDirectory", "");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            OutputDirectory = configured;
        }
        try
        {
            readSettings(settings);
        }
        catch (Exception ex)
        {
            Error("reading the settings failed, the rest keep their defaults: " + ex);
        }

        try
        {
            string logsDir = Path.GetFullPath(OutputDirectory);
            OutputDirectory = logsDir;
            if (LogToSessionFolder)
            {
                LogDirectory = Path.Combine(logsDir, System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString(SessionFormat));
                Directory.CreateDirectory(LogDirectory);
                CullOldSessions(logsDir);
                if (writeOptionsFile)
                {
                    WriteOptions(Path.Combine(LogDirectory, logName + ".json"), json);
                }
            }
            else
            {
                LogDirectory = logsDir;
                Directory.CreateDirectory(logsDir);
            }
            string logPath = Path.Combine(LogDirectory, logName + ".log");
            lock (gate)
            {
                log = new StreamWriter(logPath, append: false) { AutoFlush = true };
                foreach (string line in pending)
                {
                    log.WriteLine(line);
                }
                pending.Clear();
            }
            LauncherStatus.SetLog(logPath);
        }
        catch (Exception ex)
        {
            ErrorSink($"{ModName} could not open its log: {ex.Message}");
        }

        Info($"{ModName} started (settings: {json})");
        if (LauncherStatus.BindFailure != null)
        {
            Warn("the launcher status widget could not be used, this mod reports to its log only: " + LauncherStatus.BindFailure);
        }
        if (RealGame.BindFailure != null)
        {
            Warn("this mod cannot tell when a game starts, so it will not warn about the game's data: " + RealGame.BindFailure);
        }
        try
        {
            install();
        }
        catch (Exception ex)
        {
            Error("fatal during startup: " + ex);
        }
        startupDone = true;
    }

    // Set when the mod's start-up (settings, log, features installed) is over. From then until a real game starts (RealGame), the galaxies the
    // mod sees are the menu's background and the new-game wizard's preview: Info and Warn lines raised then are Detail lines, so the mod says
    // nothing about a galaxy nobody is playing. Errors (a caught exception, a feature that failed) are always raised.
    static volatile bool startupDone;

    // Installs one feature with its own Harmony instance. A feature that is off is said to be off; one that fails to install is logged and
    // skipped, so it cannot take the others down with it.
    public static void Feature(string name, bool enabled, Action<Harmony, Action<string>> install)
    {
        if (!enabled)
        {
            Info($"{name} is off");
            return;
        }
        try
        {
            install(new Harmony($"{ModName}.{name}"), Info);
            InstalledSink(name);
        }
        catch (Exception ex)
        {
            Error($"{name} failed to install and is off: {ex}");
        }
    }

    // A caught exception in code the game calls over and over (a tick, a per-ship hook): the first occurrence is logged in full, a repeat of the
    // same failure only when its count reaches 10, 100, 1000 ..., so a failure that fires every tick does not flood the log.
    static readonly Dictionary<string, int> failures = new();

    public static void Failed(string what, Exception ex)
    {
        string key = what + "|" + ex.GetType().Name + "|" + ex.Message;
        int count;
        lock (gate)
        {
            failures[key] = count = failures.GetValueOrDefault(key) + 1;
        }
        if (count == 1)
        {
            Error($"{what} failed: {ex}");
        }
        else if (count == 10 || count == 100 || count == 1000 || count == 10000)
        {
            Error($"{what} failed again, {count} times so far: {ex.Message}");
        }
    }

    public enum LogLevel { Detail, Info, Warn, Error }

    // normal events: start-up lines, installed features, summaries
    public static void Info(string message) => Write(LogLevel.Info, message);

    // something is not as intended but the feature carries on (a missing optional input, a skipped item); log only
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    // A Warn about the game's data or room not being what the mod expects (a family no part belongs to, a project that grants nothing, no free system
    // for a nicety). It is for the developer: nothing is broken for the player, so it is raised only with LogToSessionFolder on (developer machines)
    // and only when a real game exists (RealGame): before one, the data is the menu's or the new-game wizard's and is about to be replaced. Anywhere
    // else it is a Detail, which is not written at all when LogToSessionFolder is off. Without a loader that tells (RealGame.Present false) it never
    // warns: no information answers no.
    public static void DataWarn(string message)
    {
        if (LogToSessionFolder && RealGame.Ready)
        {
            Write(LogLevel.Warn, message);
        }
        else
        {
            Write(LogLevel.Detail, (LogToSessionFolder ? "(no game has started yet, so not a warning) " : "(not a developer run, so not a warning) ") + message);
        }
    }

    // The same for an Error about the game's data (not a caught exception): developer runs only, once a real game exists.
    public static void DataError(string message)
    {
        if (LogToSessionFolder && RealGame.Ready)
        {
            Write(LogLevel.Error, message);
        }
        else
        {
            Write(LogLevel.Detail, (LogToSessionFolder ? "(no game has started yet, so not an error) " : "(not a developer run, so not an error) ") + message);
        }
    }

    // a caught exception or a feature that is off because it could not work: also raised on the launcher's status widget
    public static void Error(string message) => Write(LogLevel.Error, message);

    // development data and per-event lines: written only with LogToSessionFolder on
    public static void Detail(string message) => Write(LogLevel.Detail, message);

    // The game calls into our patches from several threads and StreamWriter is not thread-safe: racing writers leave unfilled (NUL)
    // slots in its buffer, which then get flushed to the file. Hence the lock.
    // Line format: "HH:mm:ss LEVEL message", the level padded to six characters (the longest, DETAIL) and then a space, so lines align,
    // the message never touches the level, and lines can be filtered by level.
    static void Write(LogLevel level, string message)
    {
        if (startupDone && (level == LogLevel.Info || level == LogLevel.Warn) && !RealGame.Ready)
        {
            level = LogLevel.Detail;
            message = "(no game has started yet) " + message;
        }
        if (level == LogLevel.Detail && !LogToSessionFolder)
        {
            return;
        }
        if (level == LogLevel.Error)
        {
            ErrorSink(message);
        }
        else if (level == LogLevel.Warn && Interlocked.Increment(ref warnings) <= MaxWarnings)
        {
            WarnSink(message);
        }
        string line = DateTime.Now.ToString("HH:mm:ss") + " " + level.ToString().ToUpperInvariant().PadRight(6) + " " + message;
        lock (gate)
        {
            if (log != null)
            {
                log.WriteLine(line);
            }
            else
            {
                pending.Add(line);
            }
        }
    }

    // Keeps the newest KeepSessions run folders (the current one included) and deletes older ones. Only folders named like a session are
    // touched, and every code mod runs this on the same folder, which is harmless: the current run is always kept.
    static void CullOldSessions(string logsDir)
    {
        try
        {
            var old = new DirectoryInfo(logsDir).GetDirectories()
                .Where(d => DateTime.TryParseExact(d.Name, SessionFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Skip(KeepSessions)
                .ToList();
            foreach (DirectoryInfo dir in old)
            {
                try
                {
                    dir.Delete(true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // another process has it open (an editor, Explorer's preview, a scanner, a terminal inside it, or a sibling code mod deleting it at once; Windows reports an open file as "access denied" as often as "in use"); not a fault of the mod, and the next run tries again
                    Detail($"old session folder {dir.Name} not removed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Error("removing old session folders failed: " + ex.Message);
        }
    }

    // <logName>.json in the session folder: the options the launcher passed in, so any run's log folder says what was on and off.
    static void WriteOptions(string path, string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            using var stream = File.Create(path);
            using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteString("Started", DateTime.Now.ToString("s"));
            w.WritePropertyName("Options");
            doc.RootElement.WriteTo(w);
            w.WriteEndObject();
        }
        catch (Exception ex)
        {
            Error($"writing {Path.GetFileName(path)} failed: {ex.Message}");
        }
    }

    // a mod's own helper dlls sit next to it (or beside the game exe)
    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        foreach (string dir in new[] { ModDir, AppContext.BaseDirectory })
        {
            string path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
        }
        return null;
    }
}
