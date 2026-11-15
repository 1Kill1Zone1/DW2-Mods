using System.Text.Json;
using System.Text.Json.Serialization;
using DistantWorlds.Types;

namespace BattleLog;

static class Ledger
{
    const int Capacity = 100;
    // Simulation DateTime, never wall-clock time. An encounter only closes
    // after this idle interval AND after tracked ordnance has left the location.
    internal const double QuietSimulationSeconds = 12;
    static Galaxy? galaxy;
    static short observerId = -1;
    static readonly object gate = new();
    static readonly List<Encounter> reports = [];
    static readonly Dictionary<int, Encounter> open = [];
    static bool dirty;
    static long reportRevision;
    static long displayRevision;
    static Galaxy? faultedCampaign;
    static short faultedObserverId = -1;
    static Exception? campaignFault;
    public static IReadOnlyList<Encounter> Reports { get { lock (gate) return reports.Select(Clone).ToArray(); } }
    internal static Encounter[]? ReportsSince(ref long revision)
    {
        lock (gate)
        {
            if (revision == reportRevision) return null;
            var snapshot = reports.Select(Clone).Reverse().ToArray();
            revision = reportRevision; return snapshot;
        }
    }
    internal readonly record struct DisplayEncounter(Encounter Report, bool InProgress);
    // The UI receives defensive copies of both stores.  Open encounters retain
    // their identity when they move to reports on closure, so the panel can keep
    // the same selected encounter rather than showing a duplicate row.
    internal static DisplayEncounter[]? DisplaySince(ref long revision)
    {
        lock (gate)
        {
            if (revision == displayRevision) return null;
            var snapshot = reports.Select(x => new DisplayEncounter(Clone(x), false))
                .Concat(open.Values.Select(x => new DisplayEncounter(Clone(x), true)))
                .OrderByDescending(x => x.Report.Last).ThenByDescending(x => x.Report.Started).ToArray();
            revision = displayRevision; return snapshot;
        }
    }
    internal static bool HasOpenEncounters { get { lock (gate) return open.Count != 0; } }
    static void ChangedDisplay() => displayRevision++;
    static short PlayerId(Galaxy? current) => current?.Empires.GetPlayer()?.EmpireId ?? (short)-1;
    static bool Faulted(Galaxy? current, short id) => current != null && ReferenceEquals(faultedCampaign, current) && faultedObserverId == id;
    public static bool HasFault(Galaxy? current) => Faulted(current, PlayerId(current));
    internal static bool IsCurrent(Galaxy current, short observer) => ReferenceEquals(galaxy, current) && observerId == observer && PlayerId(current) == observer;
    public static bool LegacyHistoryPreserved { get; private set; }
    public static string FaultMessage => campaignFault?.Message ?? "Battle Log campaign data is unavailable.";
    public static bool TryEnsure(Galaxy? current)
    {
        if (current == null) { Ensure(null); return true; }
        if (HasFault(current)) return false;
        try { Ensure(current); return true; }
        catch (Exception ex)
        {
            faultedCampaign = current; faultedObserverId = PlayerId(current); campaignFault = ex;
            Console.Error.WriteLine("Battle Log disabled for this campaign: saved history could not be read. " + ex.Message);
            return false;
        }
    }
    public static void LatchFault(Galaxy? current, Exception ex)
    {
        if (current == null) return;
        lock (gate)
        {
            if (HasFault(current)) return;
            faultedCampaign = current; faultedObserverId = PlayerId(current); campaignFault = ex;
            Console.Error.WriteLine("Battle Log disabled for this campaign: " + ex.Message);
        }
    }
    public static void LatchActiveFault(Exception ex) => LatchFault(galaxy, ex);
    // Isolated harness only; no UI or mod code relies on this mutable open state.
    internal static IReadOnlyList<WeaponRow> OpenRowsForCheck() { lock (gate) return open.Values.SelectMany(x => x.Weapons.Select(Clone)).ToArray(); }
    internal static int OpenCountForCheck { get { lock (gate) return open.Count; } }
    internal static void SeedForCheck(Galaxy current, IEnumerable<Encounter> completed, Encounter? active = null)
    {
        lock (gate) { Ensure(current); reports.Clear(); open.Clear();
        foreach (var item in completed) reports.Add(item);
        if (active != null) open[active.LocationId] = active;
        dirty = true; reportRevision++; ChangedDisplay(); EnforceCapacity(); }
    }
    internal static Encounter OpenForCheck(Location location, DateTime time)
    {
        lock (gate) { Ensure(galaxy); return Open(location, time); }
    }
    internal static void SeedOpenForCheck(Galaxy current, IEnumerable<Encounter> active)
    {
        lock (gate) { Ensure(current); reports.Clear(); open.Clear();
        foreach (var item in active) open[item.LocationId] = item;
        dirty = true; reportRevision++; ChangedDisplay(); EnforceCapacity(); }
    }
    internal static void AddForConcurrencyCheck(Galaxy current, Location location, DateTime time)
    {
        lock (gate) { Ensure(current); var row = Open(location, time).Row(1, 1, "check", "check"); row.Shots++; dirty = true; ChangedDisplay(); }
    }
    sealed class CampaignState { public int Version { get; set; } = 3; public short ObserverEmpireId { get; set; } = -1; public List<Encounter> Reports { get; set; } = []; public List<Encounter> Open { get; set; } = []; }

    public static void Ensure(Galaxy? current)
    {
        lock (gate)
        {
        short nextObserver = PlayerId(current);
        if (ReferenceEquals(galaxy, current) && observerId == nextObserver) return;
        if (current != null && ReferenceEquals(galaxy, current) && dirty && observerId >= 0 && !Faulted(galaxy, observerId)) StoreActive();
        if (!Faulted(current, nextObserver)) { faultedCampaign = null; faultedObserverId = -1; campaignFault = null; }
        if (current == null || nextObserver < 0) { galaxy = current; observerId = nextObserver; reports.Clear(); open.Clear(); dirty = false; LegacyHistoryPreserved = false; reportRevision++; ChangedDisplay(); return; }
        // Parse before publishing the campaign reference. A corrupted existing
        // state must keep failing closed, never become an empty overwrite.
        var text = SavedData.Read(current, SavedData.ObserverPrefix(nextObserver));
        var state = text == null ? new CampaignState { ObserverEmpireId = nextObserver } : JsonSerializer.Deserialize<CampaignState>(text) ?? throw new InvalidDataException("Empty Battle Log campaign state");
        if (state.Version is not (2 or 3) || state.ObserverEmpireId != nextObserver || state.Reports == null || state.Open == null) throw new InvalidDataException("Unsupported Battle Log observer state");
        galaxy = current; observerId = nextObserver; reports.Clear(); open.Clear(); dirty = false; reportRevision++; ChangedDisplay();
        // v0.1.0 has no observer identity. Preserve its bytes instead of guessing an owner and disclosing another empire's intel.
        LegacyHistoryPreserved = SavedData.HasLegacyHistory(current);
        reports.AddRange(state.Reports.TakeLast(Capacity)); foreach (var item in state.Open) { ClearTransientPending(item); open[item.LocationId] = item; }
        EnforceCapacity();
        }
    }

    public static void Persist(Galaxy current, bool force)
    {
        lock (gate)
        {
        if (HasFault(current)) throw new InvalidOperationException("Battle Log cannot save: " + FaultMessage);
        Ensure(current);
        if (!dirty && !force) return;
        if (observerId >= 0) StoreActive();
        }
    }

    static void StoreActive()
    {
        // Runtime WeaponBlast references are deliberately not persisted.  A
        // save may later be loaded into a fresh native object graph, so encode
        // each live pending projectile as unavailable in the stored snapshot
        // without disturbing the still-running simulation in memory.
        SavedData.Write(galaxy!, JsonSerializer.Serialize(new CampaignState
        {
            ObserverEmpireId = observerId,
            Reports = reports.Select(Clone).ToList(),
            Open = open.Values.Select(CloneForPersistence).ToList()
        }), SavedData.ObserverPrefix(observerId));
        dirty = false;
    }

    // This is deliberately before snapshots. It is the only decision which may
    // access a live foreign object, and it never confers source identity.
    public static bool CanObserve(Galaxy g, StellarObject? firer, StellarObject? target)
    {
        var player = g.Empires.GetPlayer(); if (player == null) return false;
        return IsOwn(player, firer) || IsOwn(player, target) || FullyVisible(player, firer, g) || FullyVisible(player, target, g);
    }
    static bool IsOwn(Empire player, StellarObject? o) => o?.GetEmpireId() == player.EmpireId;
    static bool FullyVisible(Empire player, StellarObject? o, Galaxy g) => o is Ship ship && player.VisibilityMap.DetermineVisibilityTypeForShipAllEmpires(ship, player, g, g.StarDate) == VisibilityType.FullyVisible;

    static (short Side, int Component, string Weapon, string SideName) Source(Galaxy g, StellarObject? firer, int component, string? name)
    {
        var player = g.Empires.GetPlayer();
        if (player != null && IsOwn(player, firer)) return ((short)player.EmpireId, component, string.IsNullOrWhiteSpace(name) ? "Unknown weapon" : name!, "Our forces");
        if (player != null && FullyVisible(player, firer, g)) return ((short)(firer?.GetEmpireId() ?? -1), component, string.IsNullOrWhiteSpace(name) ? "Unknown weapon" : name!, firer?.GetEmpire(g)?.Name ?? "Observed forces");
        return (-1, 0, "Unknown weapon", "Unknown source"); // observed impact does not expose hidden attacker/loadout
    }

    // Runtime-only identity.  Location and Started distinguish encounters at
    // one system; the campaign/observer guard rejects callbacks from a save or
    // observer context that has since changed.
    internal readonly record struct DetailKey(Galaxy Campaign, short Observer, int LocationId, DateTime Started, short Side, int ComponentId);
    internal enum ProjectileOutcome { Hit, Miss, LostTarget, Intercepted }

    internal static DetailKey? RegisterLaunch(Galaxy g, Location? location, StellarObject? firer, StellarObject? target, int component, string? name, DateTime time, bool intercepting, float cadence, WeaponFireType fireType, bool area, float? expectedChance, bool forced, bool targetKnown)
    {
        if (!TryEnsure(g) || location == null || !CanObserve(g, firer, target)) return null;
        lock (gate) { var source = Source(g, firer, component, name); var encounter = Open(location, time);
        // Only an identified observed weapon may affect persisted cadence.
        if (source.Component != 0 && float.IsFinite(cadence) && cadence > 0) encounter.QuietSeconds = Math.Max(encounter.QuietSeconds, 2d * cadence);
        var row = encounter.Row(source.Side, source.Component, source.Weapon, source.SideName);
        if (intercepting) { row.PdShots++; row.DetailedVersion = 1; dirty = true; ChangedDisplay(); return null; }
        row.Shots++;
        if (source.Component == 0 || !targetKnown) { dirty = true; ChangedDisplay(); return null; }
        row.DetailedVersion = 1;
        if (area)
        {
            row.AreaLaunches++;
            dirty = true; ChangedDisplay(); return new DetailKey(g, observerId, location.LocationId, encounter.Started, source.Side, source.Component);
        }
        if (fireType == WeaponFireType.FullLengthBeam)
        {
            row.BeamLaunches++;
            dirty = true; ChangedDisplay(); return new DetailKey(g, observerId, location.LocationId, encounter.Started, source.Side, source.Component);
        }
        if (fireType is WeaponFireType.DirectFire or WeaponFireType.Seeking)
        {
            row.ProjectileLaunches++; row.ProjectilePending++; row.PendingKnown = true;
            if (forced) row.ForcedLaunches++;
            else if (expectedChance is float chance && float.IsFinite(chance)) { row.ExpectedChanceTotal += chance; row.ExpectedChanceSamples++; }
            dirty = true; ChangedDisplay(); return new DetailKey(g, observerId, location.LocationId, encounter.Started, source.Side, source.Component);
        }
        dirty = true; ChangedDisplay(); return null;
        }
    }

    public static void Shot(Galaxy g, Location? location, StellarObject? firer, StellarObject? target, int component, string? name, DateTime time, bool intercepting, float cadence)
        => RegisterLaunch(g, location, firer, target, component, name, time, intercepting, cadence, WeaponFireType.Undefined, false, null, false, false);

    internal static void ResolveProjectile(DetailKey key, ProjectileOutcome outcome, bool forced)
    {
        if (galaxy == null || HasFault(galaxy)) return;
        lock (gate)
        {
            var row = FindOpenRow(key); if (row == null) return;
            row.ProjectilePending = Math.Max(0, row.ProjectilePending - 1);
            if (forced) row.ForcedResolved++;
            else if (outcome == ProjectileOutcome.Hit) row.ProjectileHits++;
            else if (outcome == ProjectileOutcome.Miss) row.ProjectileMisses++;
            else if (outcome == ProjectileOutcome.LostTarget) row.ProjectileLostTargets++;
            else row.ProjectileIntercepted++;
            dirty = true; ChangedDisplay();
        }
    }
    // A runtime WeaponBlast identity cannot survive an unexpected removal, a
    // campaign switch, or a reload.  Retain that the launch had no observed
    // terminal outcome, rather than silently converting it into a miss.
    internal static void MarkProjectileUnavailable(DetailKey key)
    {
        if (galaxy == null || HasFault(galaxy)) return;
        lock (gate)
        {
            var row = FindOpenRow(key); if (row == null || row.ProjectilePending <= 0) return;
            row.ProjectilePending--; row.ProjectileUnavailable++; row.PendingKnown = true;
            dirty = true; ChangedDisplay();
        }
    }
    internal static void BeamTick(DetailKey key) => DetailCount(key, static row => row.BeamTicks++);
    internal static void AreaTarget(DetailKey key) => DetailCount(key, static row => row.AreaTargets++);
    static void DetailCount(DetailKey key, Action<WeaponRow> change)
    {
        if (galaxy == null || HasFault(galaxy)) return;
        lock (gate) { var row = FindOpenRow(key); if (row != null) { change(row); dirty = true; ChangedDisplay(); } }
    }
    static WeaponRow? FindOpenRow(DetailKey key)
    {
        if (!ReferenceEquals(galaxy, key.Campaign) || observerId != key.Observer || !open.TryGetValue(key.LocationId, out var report) || report.Started != key.Started) return null;
        foreach (var row in report.Weapons) if (row.Side == key.Side && row.ComponentId == key.ComponentId) return row;
        return null;
    }

    public static bool Hit(DamageSnapshot before)
    {
        if (galaxy == null || HasFault(galaxy) || before.LocationId < 0) return false;
        lock (gate)
        {
        if (!ReferenceEquals(before.Campaign, galaxy) || before.ObserverEmpireId != observerId || observerId != PlayerId(galaxy)) return false;
        // A delayed native damage callback belonging to a blast tracked before
        // an encounter closed/was evicted must not open or modify a new report
        // at the same location. Its current source redaction still takes precedence.
        if (before.DetailKey is DetailKey key && FindOpenRow(key) == null) return false;
        var row = OpenById(before.LocationId, before.Time).Row(before.Side, before.ComponentId, before.Name, before.SideName);
        if (before.Ordnance != null)
        {
            if (before.Ordnance.IsDestroyed) row.PdDestroyed++; else if (before.Ordnance.PowerRatio < before.OrdnancePower) row.PdWeakened++;
            dirty = true; ChangedDisplay(); return false;
        }
        if (before.IsShipImpact) row.Hits++;
        if (before.Ship == null) { if (before.IsShipImpact) row.ImpactUnknownEvents++; dirty = true; ChangedDisplay(); return false; }
        var shield = Math.Max(0, before.Shields - before.Ship.Shields);
        var hull = Math.Max(0, before.Ship.HullDamageLevel - before.Hull);
        var armor = 0f;
        if (before.Armor != null) foreach (var after in before.Ship.Components)
            if (before.Armor.TryGetValue(after.ComponentBayId, out var value)) armor += Math.Max(0, value - ArmorCapacity(after));
        row.Shield += shield; row.Hull += hull; row.Armor += armor;
        if (before.DetailedImpact && float.IsFinite(before.RawDamage) && before.RawDamage >= 0)
        {
            row.DetailedVersion = 1; row.ImpactEvents++; row.ImpactPotential += before.RawDamage; row.MeasuredImpactActual += (double)shield + armor + hull;
            if (before.HasUnmeasuredComponentChange) row.PartialImpactEvents++;
        }
        else if (before.IsShipImpact) row.ImpactUnknownEvents++;
        dirty = true; ChangedDisplay(); return before.DetailedImpact;
        }
    }
    internal static float ArmorCapacity(ShipComponent component) => component.IsEmpty ? 0f : component.GetHealthStatusRatio() * (component.GetComponentValues(component.Level)?.ArmorBlastRating ?? 0f);

    public static void CloseQuiet(DateTime now)
    {
        if (galaxy == null || HasFault(galaxy)) return;
        lock (gate)
        {
        List<int>? completed = null;
        foreach (var item in open)
            if (now.Subtract(item.Value.Last).TotalSeconds >= Math.Max(QuietSimulationSeconds, item.Value.QuietSeconds) && !HasOrdnance(item.Key))
                (completed ??= []).Add(item.Key);
        if (completed != null)
        {
            foreach (var id in completed) { ClearTransientPending(open[id]); reports.Add(open[id]); open.Remove(id); }
            dirty = true; reportRevision++; ChangedDisplay();
        }
        if (reports.Count > Capacity) { reports.RemoveRange(0, reports.Count - Capacity); dirty = true; reportRevision++; }
        // Do not write on each UI update: shots/hits only mark dirty. A completed
        // encounter commits here; an in-progress one commits before native save.
        if (completed != null) Persist(galaxy, false);
        }
    }

    static bool HasOrdnance(int locationId)
    {
        var location = galaxy!.Locations.GetById(locationId);
        if (location != null) foreach (var blast in location.WeaponBlasts)
            if (blast != null && blast.Type == WeaponBlastType.WeaponBlast && !blast.IsDestroyed && !blast.HasMissedTarget && Entry.IncludesCombat(blast.ComponentStats, blast.GetTarget(galaxy!))) return true;
        return false;
    }
    static Encounter Open(Location location, DateTime time)
    {
        var report = OpenById(location.LocationId, time);
        if (report.LocationName == "Unknown location" && !string.IsNullOrWhiteSpace(location.Name)) report.LocationName = location.Name;
        return report;
    }
    static Encounter OpenById(int id, DateTime time)
    {
        if (!open.TryGetValue(id, out var report)) { open[id] = report = new Encounter { LocationId = id, Started = time, Last = time }; ChangedDisplay(); EnforceCapacity(); }
        report.Last = time; return report;
    }
    static void ClearTransientPending(Encounter encounter)
    {
        foreach (var row in encounter.Weapons) if (row.ProjectilePending != 0)
        {
            row.ProjectileUnavailable += row.ProjectilePending;
            row.ProjectilePending = 0; row.PendingKnown = true;
        }
    }
    static void EnforceCapacity()
    {
        while (reports.Count + open.Count > Capacity)
        {
            var closed = reports.Select((item, index) => (item, index, active: false));
            var active = open.Values.Select(item => (item, index: -1, active: true));
            var oldest = closed.Concat(active).OrderBy(x => x.item.Started).ThenBy(x => x.item.Last).First();
            if (oldest.active) { open.Remove(oldest.item.LocationId); ChangedDisplay(); } else { reports.RemoveAt(oldest.index); reportRevision++; ChangedDisplay(); }
            dirty = true;
        }
    }
    static Encounter Clone(Encounter value) => new()
    {
        LocationId = value.LocationId, LocationName = value.LocationName, Started = value.Started, Last = value.Last, QuietSeconds = value.QuietSeconds,
        Weapons = value.Weapons.Select(Clone).ToList()
    };
    static Encounter CloneForPersistence(Encounter value)
    {
        var copy = Clone(value);
        ClearTransientPending(copy);
        return copy;
    }
    static WeaponRow Clone(WeaponRow value) => new()
    {
        Side = value.Side, SideName = value.SideName, ComponentId = value.ComponentId, Name = value.Name,
        Shots = value.Shots, Hits = value.Hits, PdShots = value.PdShots, PdDestroyed = value.PdDestroyed, PdWeakened = value.PdWeakened,
        Shield = value.Shield, Armor = value.Armor, Hull = value.Hull,
        DetailedVersion = value.DetailedVersion, ProjectileLaunches = value.ProjectileLaunches, ProjectileHits = value.ProjectileHits, ProjectileMisses = value.ProjectileMisses,
        ProjectileLostTargets = value.ProjectileLostTargets, ProjectileIntercepted = value.ProjectileIntercepted, ForcedLaunches = value.ForcedLaunches, ForcedResolved = value.ForcedResolved,
        ExpectedChanceTotal = value.ExpectedChanceTotal, ExpectedChanceSamples = value.ExpectedChanceSamples, BeamLaunches = value.BeamLaunches, BeamTicks = value.BeamTicks,
        AreaLaunches = value.AreaLaunches, AreaTargets = value.AreaTargets, ImpactEvents = value.ImpactEvents, ImpactPotential = value.ImpactPotential,
        PartialImpactEvents = value.PartialImpactEvents, ImpactUnknownEvents = value.ImpactUnknownEvents, MeasuredImpactActual = value.MeasuredImpactActual, ProjectileUnavailable = value.ProjectileUnavailable, ProjectilePending = value.ProjectilePending, PendingKnown = value.PendingKnown
    };
}

public sealed class Encounter
{
    public int LocationId { get; set; }
    public string LocationName { get; set; } = "Unknown location";
    public DateTime Started { get; set; }
    public DateTime Last { get; set; }
    public double QuietSeconds { get; set; } = Ledger.QuietSimulationSeconds;
    public List<WeaponRow> Weapons { get; set; } = [];
    public WeaponRow Row(short side, int id, string name, string sideName)
    {
        foreach (var row in Weapons)
            if (row.Side == side && row.ComponentId == id)
            { if (row.Name == "Unknown weapon" && name != row.Name) row.Name = name; return row; }
        var created = new WeaponRow { Side = side, ComponentId = id, Name = name, SideName = sideName }; Weapons.Add(created); return created;
    }
}
public sealed class WeaponRow
{
    public short Side { get; set; } public string SideName { get; set; } = "Unknown source"; public int ComponentId { get; set; } public string Name { get; set; } = "";
    public long Shots { get; set; } public long Hits { get; set; } public long PdShots { get; set; } public long PdDestroyed { get; set; } public long PdWeakened { get; set; }
    public float Shield { get; set; } public float Armor { get; set; } public float Hull { get; set; }
    public int DetailedVersion { get; set; }
    public long ProjectileLaunches { get; set; } public long ProjectileHits { get; set; } public long ProjectileMisses { get; set; } public long ProjectileLostTargets { get; set; } public long ProjectileIntercepted { get; set; } public long ProjectileUnavailable { get; set; }
    public long ForcedLaunches { get; set; } public long ForcedResolved { get; set; }
    public double ExpectedChanceTotal { get; set; } public long ExpectedChanceSamples { get; set; }
    public long BeamLaunches { get; set; } public long BeamTicks { get; set; } public long AreaLaunches { get; set; } public long AreaTargets { get; set; }
    public long ImpactEvents { get; set; } public long PartialImpactEvents { get; set; } public long ImpactUnknownEvents { get; set; } public double ImpactPotential { get; set; } public double MeasuredImpactActual { get; set; }
    [JsonIgnore] public long ProjectilePending { get; set; }
    [JsonIgnore] public bool PendingKnown { get; set; } = true;
}
