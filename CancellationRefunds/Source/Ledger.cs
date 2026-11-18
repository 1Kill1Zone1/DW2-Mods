using System.Runtime.CompilerServices;
using System.Text.Json;
using DistantWorlds.Types;

namespace CancellationRefunds;

// Only pending, paid objects are retained. Completing, removing or changing
// ownership deletes eligibility permanently; damaged finished assets cannot
// become refundable again. Object identity is checked as well as native IDs.
internal sealed class Payment
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public int Orb { get; set; }
    public int Definition { get; set; }
    public short Payer { get; set; }
    public float Paid { get; set; }
    public float Progress { get; set; }
    public long BuildDate { get; set; }
    public short PreviousShipOwner { get; set; } = -1;
    public string Name { get; set; } = "";
    internal string Key => Kind + ":" + Orb + ":" + Id;
}

internal sealed class Ledger
{
    sealed record Snapshot(int Version, long SavedTime, List<Payment> Payments);
    internal sealed record Bound(Ledger Book, Payment Payment, object Target);
    static readonly ConditionalWeakTable<Galaxy, Ledger> Books = new();
    static readonly ConditionalWeakTable<object, Bound> Bindings = new();
    static readonly object loadGate = new();
    readonly Dictionary<string, Bound> pending = new();
    readonly object gate = new();
    readonly Galaxy galaxy;
    Exception? fault;

    Ledger(Galaxy g)
    {
        galaxy = g;
        try
        {
            var text = SavedData.Read(g); if (text == null) return;
            var data = JsonSerializer.Deserialize<Snapshot>(text);
            if (data == null || data.Version is not (1 or 2) || data.Payments == null || data.Payments.Count > 100000)
                throw new InvalidDataException("Invalid Cancellation Refunds state; preserved without payouts.");
            // A campaign advanced and saved with this mod disabled cannot reuse
            // old pending identities. No historical price is reconstructed.
            if (data.SavedTime != g.Time.Ticks) return;
            foreach (var p in data.Payments)
            {
                if (!Valid(p) || pending.ContainsKey(p.Key)) throw new InvalidDataException("Invalid or duplicate refund payment.");
                var target = Resolve(p);
                if (target != null && Name(target) == p.Name &&
                    (target is Ship ship && ship.Summary == null ? SameShip(p, ship) : Eligible(p, target))) Bind(p, target);
            }
        }
        catch (Exception ex) { fault = ex; System.Diagnostics.Trace.TraceError("Cancellation Refunds disabled for this campaign: " + ex.Message); }
    }

    internal static Ledger For(Galaxy g) { lock (loadGate) return Books.GetValue(g, static key => new Ledger(key)); }
    internal static void Guard(Galaxy? g, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (g != null && Books.TryGetValue(g, out var book)) lock (book.gate)
            {
                if (book.fault != null) return;
                book.fault = ex;
            }
            System.Diagnostics.Trace.TraceError("Cancellation Refunds disabled after an internal error; native action continues: " + ex.Message);
        }
    }
    static bool Valid(Payment p) => (p.Kind == "ship" ? p.Orb == -1 : p.Kind is "facility" or "troop" && p.Orb >= 0)
        && p.Id >= 0 && p.Definition >= 0 && p.Payer >= 0 && float.IsFinite(p.Paid) && p.Paid > 0
        && float.IsFinite(p.Progress) && p.Progress is >= 0 and < 1 && p.Name != null;

    object? Resolve(Payment p) => p.Kind switch
    {
        "facility" => galaxy.Orbs.GetById(p.Orb)?.Facilities.GetById(p.Id),
        "troop" => galaxy.Troops.GetById(p.Id),
        "ship" => galaxy.Ships.GetById(p.Id),
        _ => null
    };
    static float Progress(object target) => target switch
    {
        Ship ship => ship.Summary == null ? float.NaN : ship.ConstructionLevel,
        Troop troop => troop.Readiness,
        _ => ((PlanetaryFacility)target).ConstructionProgress
    };
    static string Name(object target) => target switch
    {
        Ship ship => ship.Name,
        Troop troop => troop.Name,
        _ => ((PlanetaryFacility)target).Name
    };
    bool Eligible(Payment p, object target)
    {
        if (!ReferenceEquals(Resolve(p), target)) return false;
        var current = Progress(target);
        if (!float.IsFinite(current) || current < 0 || current >= 1) return false;
        if (target is Ship ship) return SameShip(p, ship);
        if (target is Troop troop)
            return troop.Recruiting && !troop.IsGhostTroop && troop.DefectRevertEmpireId < 0 && troop.EmpireId == p.Payer &&
                troop.TroopDefinitionId == p.Definition && galaxy.Empires.GetById(p.Payer)?.Troops.GetById(p.Id) == troop;
        var facility = (PlanetaryFacility)target;
        return facility.EmpireId == p.Payer && facility.PlanetaryFacilityDefinitionId == p.Definition && facility.ParentOrbId == p.Orb &&
            facility.BuildDate == p.BuildDate && galaxy.Orbs.GetById(p.Orb)?.Colony?.EmpireId == p.Payer;
    }
    bool SameShip(Payment p, Ship ship) => !ship.IsDestroyed && ship.EmpireId == p.Payer &&
        ship.DesignId == p.Definition && ship.PreviousEmpireId == p.PreviousShipOwner &&
        ReferenceEquals(galaxy.Empires.GetById(p.Payer)?.Ships.GetById(p.Id), ship);

    void Bind(Payment payment, object target)
    {
        var binding = new Bound(this, payment, target);
        if (pending.Remove(payment.Key, out var old)) Bindings.Remove(old.Target);
        Bindings.Remove(target); Bindings.Add(target, binding); pending.Add(payment.Key, binding);
    }
    void Drop(Bound bound)
    {
        if (pending.TryGetValue(bound.Payment.Key, out var current) && ReferenceEquals(current, bound)) pending.Remove(bound.Payment.Key);
        Bindings.Remove(bound.Target);
    }
    internal void Track(Empire payer, PlanetaryFacility facility, float amount)
    {
        var p = new Payment { Kind = "facility", Id = facility.PlanetaryFacilityId, Orb = facility.ParentOrbId, Definition = facility.PlanetaryFacilityDefinitionId,
            Payer = payer.EmpireId, Paid = amount, Progress = facility.ConstructionProgress, BuildDate = facility.BuildDate, Name = facility.Name };
        lock (gate) { if (fault == null && Valid(p) && Eligible(p, facility)) Bind(p, facility); }
    }
    internal void Track(Empire payer, Troop troop, int orb, float amount)
    {
        var p = new Payment { Kind = "troop", Id = troop.TroopId, Orb = orb, Definition = troop.TroopDefinitionId,
            Payer = payer.EmpireId, Paid = amount, Progress = troop.Readiness, Name = troop.Name };
        lock (gate) { if (fault == null && Valid(p) && Eligible(p, troop)) Bind(p, troop); }
    }
    internal void Track(Empire payer, Ship ship, float amount)
    {
        var payment = new Payment { Kind = "ship", Id = ship.ShipId, Orb = -1, Definition = ship.DesignId,
            Payer = payer.EmpireId, Paid = amount, Progress = Progress(ship), PreviousShipOwner = ship.PreviousEmpireId, Name = ship.Name };
        lock (gate) { if (fault == null && Valid(payment) && Eligible(payment, ship)) Bind(payment, ship); }
    }
    internal static void Forget(object target)
    {
        if (Bindings.TryGetValue(target, out var b)) lock (b.Book.gate) b.Book.Drop(b);
    }
    internal static void Observe(object target)
    {
        if (!Bindings.TryGetValue(target, out var b)) return;
        lock (b.Book.gate)
        {
            if (!b.Book.Eligible(b.Payment, target)) { b.Book.Drop(b); return; }
            b.Payment.Progress = Math.Max(b.Payment.Progress, Progress(target));
        }
    }
    // Called before the native removal: later checks cannot use registry
    // membership because successful cancellation has removed that membership.
    internal static Bound? Candidate(object target, Empire executor)
    {
        if (!Bindings.TryGetValue(target, out var b)) return null;
        lock (b.Book.gate)
        {
            if (b.Book.fault != null) return null;
            if (!ReferenceEquals(executor.Galaxy, b.Book.galaxy) || executor.EmpireId != b.Payment.Payer || !b.Book.Eligible(b.Payment, target)) return null;
            b.Payment.Progress = Math.Max(b.Payment.Progress, Progress(target));
            return b;
        }
    }
    internal static float Consume(Bound b)
    {
        lock (b.Book.gate)
        {
            if (b.Book.fault != null) return 0;
            if (!Bindings.TryGetValue(b.Target, out var current) || !ReferenceEquals(current, b)) return 0;
            b.Book.Drop(b);
            return b.Payment.Paid * (1f - Math.Clamp(b.Payment.Progress, 0f, 1f));
        }
    }
    internal void InvalidateColony(int orb)
    {
        lock (gate) foreach (var b in pending.Values.Where(b => b.Payment.Orb == orb).ToArray()) Drop(b);
    }
    internal void Save()
    {
        lock (gate)
        {
            if (fault != null) return;
            foreach (var b in pending.Values.ToArray())
            {
                if (!Eligible(b.Payment, b.Target)) Drop(b);
                else { b.Payment.Progress = Math.Max(b.Payment.Progress, Progress(b.Target)); b.Payment.Name = Name(b.Target); }
            }
            try { SavedData.Write(galaxy, JsonSerializer.Serialize(new Snapshot(pending.Values.Any(b => b.Payment.Kind == "ship") ? 2 : 1, galaxy.Time.Ticks,
                pending.Values.Select(b => b.Payment).OrderBy(p => p.Key).ToList()))); }
            catch (Exception ex) { fault = ex; System.Diagnostics.Trace.TraceError("Cancellation Refunds persistence disabled; existing history preserved: " + ex.Message); }
        }
    }
    internal int Count { get { lock (gate) return pending.Count; } }
}
