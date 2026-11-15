using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DW2Mod;

/// <summary>
/// Registry of development probes (counters a code mod keeps for the test harness; docs/Test Harness Reference.md, "Probes"). A probe is installed
/// only with <c>ModHost.LogToSessionFolder</c> on, never for players, and registers itself here from its <c>Install</c>: a name, a report (one line of
/// text), a reset, and optionally a count (one number that says how much it has seen, so a lab run can stop after "500 shots").
/// The test bridge finds this class by its full name in every loaded assembly (each code mod compiles its own internal copy, like the rest of
/// Shared) and reads it by reflection: <c>Names</c>, <c>Report</c>, <c>Reset</c>, <c>Count</c>. Keep those four public static signatures as they are.
/// A probe must be safe to read from another thread while the game updates it.
/// </summary>
internal static class Probe
{
    sealed class Entry
    {
        public Func<string> Report;
        public Action Reset;
        public Func<long> Count;
    }

    static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a probe. A later registration of the same name replaces the earlier one (last one wins).</summary>
    public static void Register(string name, Func<string> report, Action reset, Func<long> count = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a probe needs a name", nameof(name));
        Entries[name] = new Entry { Report = report ?? throw new ArgumentNullException(nameof(report)), Reset = reset ?? throw new ArgumentNullException(nameof(reset)), Count = count };
    }

    /// <summary>The registered names, sorted.</summary>
    public static string[] Names() => Entries.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The probe's one-line report.</summary>
    public static string Report(string name) => Find(name).Report();

    /// <summary>Zeroes the probe.</summary>
    public static void Reset(string name) => Find(name).Reset();

    /// <summary>The probe's count, or -1 when it has none.</summary>
    public static long Count(string name)
    {
        Entry e = Find(name);
        return e.Count == null ? -1 : e.Count();
    }

    static Entry Find(string name)
    {
        if (name != null && Entries.TryGetValue(name, out Entry e)) return e;
        throw new KeyNotFoundException($"no probe named '{name}' (registered: {string.Join(", ", Names())})");
    }
}
