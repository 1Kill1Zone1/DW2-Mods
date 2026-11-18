using System.Runtime.CompilerServices;
using DistantWorlds.Types;

namespace AdvancedConstructionMod;

internal sealed class ConstructionRegistry
{
    private readonly Galaxy galaxy;
    private readonly object gate = new();
    private readonly Dictionary<short, EmpireState> states = new();

    public ConstructionRegistry(Galaxy galaxy) => this.galaxy = galaxy;
    internal object Gate => gate;

    public bool TryGet(Empire empire, out ConstructionSnapshot snapshot, out string? error)
    {
        lock (gate)
        {
            if (!states.TryGetValue(empire.EmpireId, out var state))
            {
                state = new EmpireState(new MatrixStorage(galaxy, empire.EmpireId));
                states.Add(empire.EmpireId, state);
            }
            if (!state.Loaded)
            {
                try { state.Snapshot = state.Storage.Read() ?? new ConstructionSnapshot(); state.Loaded = true;state.Error=null; }
                catch (Exception ex) { state.Error = "Construction Series metadata is unavailable: " + ex.Message; }
            }
            snapshot = state.Snapshot ?? new ConstructionSnapshot();
            error = state.Error;
            return error == null;
        }
    }

    public bool TryCommit(Empire empire, ConstructionSnapshot next, out string? error)
    {
        lock (gate)
        {
            if (!TryGet(empire, out _, out error)) return false;
            EmpireState state = states[empire.EmpireId];
            var beforeMemory=state.Snapshot;
            ConstructionSnapshot beforeStored;
            try { beforeStored=state.Storage.Read()??new ConstructionSnapshot(); }
            catch(Exception ex){error="Cannot verify the stored metadata before commit: "+ex.Message;return false;}
            try { state.Storage.Write(next); state.Snapshot = next; error = null; return true; }
            catch (Exception ex)
            {
                // MatrixStorage restores the former header before rethrowing. Keep that verified old graph usable.
                try
                {
                    ConstructionSnapshot? recovered = state.Storage.Read();
                    recovered ??= new ConstructionSnapshot();
                    if (!Same(beforeStored, recovered))
                        throw new InvalidDataException("The previously published series graph could not be recovered exactly.");
                    state.Snapshot = beforeMemory;
                    state.Error = null;
                    error = "Construction Series metadata commit failed; the previous snapshot was retained: " + ex.Message;
                    return false;
                }
                catch (Exception recovery)
                {
                    state.Error = "Construction Series metadata commit and recovery failed: " + recovery.Message;
                    error = state.Error;
                    return false;
                }
            }
        }
    }

    // Native orders and their metadata share the save gate; regular save publishes them together.
    internal bool SetManualOrder(Empire empire,int shipId,ManualRetrofitOrder? order,out string? error)
    {
        lock(gate)
        {
            if(!TryGet(empire,out var current,out error))return false;
            if(order==null&&!current.ManualRetrofits.ContainsKey(shipId))return true;
            var next=current.Clone();next.Version=ConstructionSnapshot.CurrentVersion;
            if(order==null)next.ManualRetrofits.Remove(shipId);else next.ManualRetrofits[shipId]=order;
            next.Revision=checked(current.Revision+1);next.Validate();states[empire.EmpireId].Snapshot=next;return true;
        }
    }

    private static bool Same(ConstructionSnapshot left, ConstructionSnapshot right) => left.Version == right.Version &&
        left.Revision == right.Revision && left.Classes.OrderBy(p=>p.Key).SequenceEqual(right.Classes.OrderBy(p=>p.Key)) &&
        left.DesignClasses.OrderBy(p=>p.Key).SequenceEqual(right.DesignClasses.OrderBy(p=>p.Key))&&left.Targets.OrderBy(p=>p.Key).SequenceEqual(right.Targets.OrderBy(p=>p.Key))&&left.AutoUpgrade.OrderBy(p=>p.Key).SequenceEqual(right.AutoUpgrade.OrderBy(p=>p.Key))&&left.ManualRetrofits.OrderBy(p=>p.Key).SequenceEqual(right.ManualRetrofits.OrderBy(p=>p.Key));

    internal bool TryRestore(Empire empire, ConstructionSnapshot knownGood, out string? error)
    {
        lock (gate)
        {
            if (!states.TryGetValue(empire.EmpireId, out var state)) { error = "Construction Series state was not loaded."; return false; }
            try { state.Storage.Write(knownGood); state.Snapshot = knownGood; state.Error = null; error = null; return true; }
            catch (Exception ex) { state.Error = "Construction Series rollback failed: " + ex.Message; error = state.Error; return false; }
        }
    }

    public void PersistAll()
    {
        lock (gate)
        {
            foreach (var pair in states)
            {
                var state=pair.Value;
                if (state.Error != null) throw new InvalidOperationException(state.Error);
                if(state.Loaded&&state.Snapshot is ConstructionSnapshot current&&current.ManualRetrofits.Count>0)
                {
                    var removed=current.ManualRetrofits.Keys.Where(id=>galaxy.Ships.GetById(id) is not Ship ship||ship.IsDestroyed||ship.EmpireId!=pair.Key).ToArray();
                    if(removed.Length>0)
                    {
                        var next=current.Clone();foreach(int id in removed)next.ManualRetrofits.Remove(id);
                        next.Version=ConstructionSnapshot.CurrentVersion;next.Revision=checked(current.Revision+1);next.Validate();state.Snapshot=next;
                    }
                }
                if (state.Loaded && state.Snapshot != null && (state.Snapshot.Classes.Count!=0||state.Snapshot.DesignClasses.Count!=0||state.Snapshot.Revision!=0||state.Snapshot.ManualRetrofits.Count!=0))state.Storage.Write(state.Snapshot);
            }
        }
    }

    private sealed class EmpireState(MatrixStorage storage)
    {
        public readonly MatrixStorage Storage = storage;
        public ConstructionSnapshot? Snapshot;
        public bool Loaded;
        public string? Error;
    }
}

internal static class ConstructionCampaigns
{
    private static readonly ConditionalWeakTable<Galaxy, ConstructionRegistry> Registries = new();
    private static readonly ConditionalWeakTable<Empire, Galaxy> EmpireGalaxies = new();
    public static ConstructionRegistry Get(Galaxy galaxy) => Registries.GetValue(galaxy, static item => new ConstructionRegistry(item));
    public static ConstructionRegistry Get(Galaxy galaxy, Empire empire)
    {
        _ = EmpireGalaxies.GetValue(empire, _ => galaxy);
        return Get(galaxy);
    }
    public static bool TryGetGalaxy(Empire empire, out Galaxy galaxy) => EmpireGalaxies.TryGetValue(empire, out galaxy!);
    public static void Forget(Galaxy galaxy) => Registries.Remove(galaxy);
}
