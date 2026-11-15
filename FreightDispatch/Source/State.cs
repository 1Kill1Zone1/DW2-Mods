using System.Runtime.CompilerServices;
using System.Text.Json;
using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;

namespace FreightDispatch;

public enum DispatchPhase { Active, Cancelling, Complete, Cancelled }
public sealed class DispatchResource
{
    public short ResourceId { get; set; }
    public float Retain { get; set; }
    public double Delivered { get; set; }
}
public sealed class DispatchVoyage
{
    public int OrderId { get; set; }
    public byte ContractId { get; set; }
    public int ShipId { get; set; }
    public short ResourceId { get; set; }
    public float DeliveredSeen { get; set; }
}
public sealed class DispatchTask
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public ItemType DestinationType { get; set; }
    public int DestinationId { get; set; }
    public int Limit { get; set; } = 2;
    public DispatchPhase Phase { get; set; }
    public List<DispatchResource> Resources { get; set; } = [];
    public List<DispatchVoyage> Voyages { get; set; } = [];
    public List<int> Orders { get; set; } = [];
    public string Status { get; set; } = "Waiting for logistics review";
}
public sealed class DispatchState
{
    public int Version { get; set; } = 1;
    public short Observer { get; set; }
    public int NextTaskId { get; set; } = 1;
    public HashSet<int> HighSources { get; set; } = [];
    public List<DispatchTask> Tasks { get; set; } = [];

    public void Validate(short observer)
    {
        if (Version != 1 || Observer != observer || NextTaskId < 1 || HighSources == null || Tasks == null ||
            HighSources.Any(id => id < 0) || Tasks.Count > 512 || HighSources.Count > 4096)
            throw new InvalidDataException("Invalid Freight Dispatch state; preserved without overwriting.");
        var ids = new HashSet<int>(); var contracts = new HashSet<(int, byte)>(); var sources = new HashSet<int>(); var orderIds = new HashSet<int>();
        foreach (var task in Tasks)
        {
            if (task == null || task.Id < 1 || task.Id >= NextTaskId || !ids.Add(task.Id) || task.SourceId < 0 ||
                task.DestinationId < 0 || task.DestinationType is not (ItemType.Ship or ItemType.Orb) ||
                task.DestinationType == ItemType.Ship && task.SourceId == task.DestinationId || task.Limit is < 1 or > 1000 ||
                !Enum.IsDefined(task.Phase) || task.Resources == null || task.Resources.Count is < 1 or > 256 || task.Voyages == null || task.Voyages.Count > 1000 ||
                task.Orders == null || task.Orders.Count > 1000 || task.Orders.Any(id => id < 0 || !orderIds.Add(id)) ||
                Dispatch.IsOpen(task) && !sources.Add(task.SourceId))
                throw new InvalidDataException("Invalid or duplicate dispatch task.");
            var resources = new HashSet<short>();
            foreach (var resource in task.Resources)
                if (resource == null || resource.ResourceId < 0 || !resources.Add(resource.ResourceId) || !float.IsFinite(resource.Retain) || resource.Retain < 0 ||
                    !double.IsFinite(resource.Delivered) || resource.Delivered < 0)
                    throw new InvalidDataException("Invalid dispatch resource reserve or delivery.");
            foreach (var voyage in task.Voyages)
                if (voyage == null || !task.Orders.Contains(voyage.OrderId) || voyage.ShipId < 0 || !resources.Contains(voyage.ResourceId) ||
                    !float.IsFinite(voyage.DeliveredSeen) || voyage.DeliveredSeen < 0 || !contracts.Add((voyage.OrderId, voyage.ContractId)))
                    throw new InvalidDataException("Invalid or duplicate dispatch contract identity.");
        }
    }
}

static class State
{
    internal sealed class Session
    {
        internal readonly object Gate = new();
        internal DispatchState? Data;
        internal string? Fault;
        internal bool Loaded;
        internal string? PendingSaveError;
    }
    static readonly ConditionalWeakTable<Galaxy, Dictionary<short, Session>> sessions = new();
    [ThreadStatic] static Galaxy? preparedGameSave;
    static readonly AccessTools.FieldRef<DWGame, ScaledRenderer> Renderer = AccessTools.FieldRefAccess<DWGame, ScaledRenderer>("_Renderer");
    internal static Session For(Galaxy galaxy, short observer)
    {
        var observers = sessions.GetOrCreateValue(galaxy);
        Session session;
        lock (observers)
        {
            if (!observers.TryGetValue(observer, out session!)) observers.Add(observer, session = new Session());
        }
        lock (session.Gate)
            {
                if (session.Loaded) return session;
                try
                {
                    var json = SavedData.Read(galaxy, SavedData.ObserverPrefix(observer));
                    var data = json == null ? new DispatchState { Observer = observer } :
                        JsonSerializer.Deserialize<DispatchState>(json) ?? throw new InvalidDataException("Empty dispatch payload");
                    data.Validate(observer); session.Data = data; session.Loaded = true;
                }
                catch (Exception ex) { session.Fault = ex.Message; session.Loaded = true; }
            }
        return session;
    }
    internal static bool Ready(Galaxy g, Empire e, out Session session)
    {
        session = For(g, e.EmpireId);
        return e.IsPlayer && e.Active && session.Data != null && session.Fault == null;
    }
    internal static void Persist(Galaxy galaxy, Session session, bool allowFaulted = false)
    {
        lock (session.Gate)
        {
            if (session.Data == null) return;
            if (session.Fault != null && !allowFaulted) throw new InvalidOperationException("Freight Dispatch is stopped: " + session.Fault);
            try
            {
                session.Data.Validate(session.Data.Observer);
                SavedData.Write(galaxy, JsonSerializer.Serialize(session.Data), SavedData.ObserverPrefix(session.Data.Observer));
            }
            catch (Exception ex) { session.Fault = ex.Message; throw; }
        }
    }
    internal static void BeforeSave(Galaxy __instance)
    {
        if (ReferenceEquals(preparedGameSave, __instance)) return;
        var player = __instance.Empires.GetPlayer();
        if (player == null) return;
        var session = For(__instance, player.EmpireId);
        lock (session.Gate)
        {
            // Reconcile and verify the latest delivery ledger even after a latched fault.
            // A failed retry aborts serialization rather than saving a stale Matrix bank.
            if (session.Fault != null && session.Data != null) Native.StopNewPickups(__instance, player, session.Data);
            Persist(__instance, session, allowFaulted: true);
        }
    }
    internal static bool BeforeGameSave(DWGame __instance, out Galaxy? __state)
    {
        __state = preparedGameSave;
        var galaxy = __instance.Galaxy;
        if (galaxy == null) return true;
        try
        {
            // SaveGame opens/truncates its target before Galaxy.WriteToStream. Commit before that happens.
            BeforeSave(galaxy);
            preparedGameSave = galaxy;
            return true;
        }
        catch (Exception ex)
        {
            if (galaxy.Empires.GetPlayer() is { } player)
            {
                var session = For(galaxy, player.EmpireId);
                lock (session.Gate) session.PendingSaveError = "Freight Dispatch: save blocked; previous save preserved. " + ex.Message;
            }
            TextHelper.SessionLog("Freight Dispatch blocked save before opening its file: " + ex);
            Galaxy.AllowNewTasks = true;
            // A false prefix still runs other mods' success postfixes (including companion-file writes).
            throw new SaveRejectedException(ex);
        }
    }
    sealed class SaveRejectedException(Exception inner) : Exception("Freight Dispatch rejected save preflight", inner);
    internal static Exception? FinishGameSave(Exception? __exception, Galaxy? __state)
    {
        preparedGameSave = __state;
        return __exception is SaveRejectedException ? null : __exception;
    }
    internal static void NotifySaveFailure(DWGame game, Session session)
    {
        string? message;
        lock (session.Gate) message = session.PendingSaveError;
        if (message == null || game.Status != GameStatus.Running || Renderer(game) is not { } renderer) return;
        renderer.SetBriefMessage(message, 15f);
        lock (session.Gate) if (session.PendingSaveError == message) session.PendingSaveError = null;
    }
    internal static string? Error(Galaxy galaxy, Empire empire) => For(galaxy, empire.EmpireId).Fault;
}
