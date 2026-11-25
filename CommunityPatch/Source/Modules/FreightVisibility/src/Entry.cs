using System.Diagnostics;
using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;

namespace FreightVisibility;

public static class Entry
{
    private static readonly object Gate = new();
    private static Galaxy? cachedGalaxy;
    private static Empire? cachedEmpire;
    private static Dictionary<short, Totals>? snapshot;
    private static long attempted, completed;
    private static bool failed;
    private static int logged;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "data", "Logs", "freight-visibility.log");

    public static void Init()
    {
        var target = typeof(Resource).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name.EndsWith(".DrawItem", StringComparison.Ordinal));
        new Harmony("local.dw2.freight-visibility").Patch(target,
            prefix: new HarmonyMethod(typeof(Entry), nameof(Before)),
            postfix: new HarmonyMethod(typeof(Entry), nameof(After)) { priority = Priority.Last });
        Log("Ready 0.1.1. Read-only freight totals in resource Production/Runway tooltip.");
    }

    public static void Before(ref HoverTipList hoverTips, out int __state) => __state = hoverTips?.Count ?? 0;

    public static void After(Resource __instance, DrawDetailType drawType, Empire empire, ref HoverTipList hoverTips, int __state)
    {
        if ((int)drawType != 16 || empire?.IsPlayer != true || hoverTips == null) return;
        try
        {
            var title = TextResolver.GetText("Production");
            for (int i = hoverTips.Count - 1; i >= __state; i--)
            {
                var tip = hoverTips[i];
                if (tip == null || tip.Title != title || tip.Text == null) continue;
                tip.Text += "\n\n" + Text(empire, __instance.ResourceId);
                break;
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    public static string Text(Empire empire, short resource)
    {
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (!ReferenceEquals(cachedGalaxy, empire.Galaxy) || !ReferenceEquals(cachedEmpire, empire))
            {
                cachedGalaxy = empire.Galaxy;
                cachedEmpire = empire;
                snapshot = null;
                attempted = completed = 0;
            }
            if (attempted == 0 || Stopwatch.GetElapsedTime(attempted, now).TotalSeconds >= 2)
            {
                attempted = now;
                try
                {
                    var fresh = Freight.Read(empire);
                    snapshot = fresh;
                    completed = Stopwatch.GetTimestamp();
                    failed = false;
                }
                catch (Exception ex) { failed = true; Report(ex); }
            }
            if (snapshot == null) return "Freight: updating (no complete sample yet)";
            double age = Stopwatch.GetElapsedTime(completed).TotalSeconds;
            return snapshot.GetValueOrDefault(resource, new Totals()).Lines() +
                $"\nSample age: {age:0}s" + (failed ? " (updating; previous sample)" : "");
        }
    }

    private static void Report(Exception ex)
    {
        if (Interlocked.Increment(ref logged) <= 5) Log(ex.ToString());
    }
    private static void Log(string text)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {text}\n"); }
        catch { /* Logging must never interrupt drawing. */ }
    }
}
