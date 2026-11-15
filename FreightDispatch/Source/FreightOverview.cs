using System.Globalization;
using System.Reflection;
using DistantWorlds.Types;

namespace FreightDispatch;

internal enum FreightActivity { Idle, Pickup, Delivery, Refuel, Repair, Retrofit, Construction, Other, Unavailable }
internal sealed record FreightCargo(short Id, string Name, double Amount);
internal sealed record FreightMission(string Title, string Primary, string Secondary, FreightCargo[] Planned,
    string[] Commands, string[] Contracts, bool Dispatch, bool Transport, bool Current, string Reason);
internal sealed record FreighterInfo(int Id, string Name, FreightActivity Activity, string Condition,
    FreightCargo[] Cargo, FreightCargo[] Smuggling, FreightMission[] Missions, FreightMission? Revert, string? Error)
{
    internal bool Dispatch => Missions.Any(m => m.Dispatch) || Revert?.Dispatch == true;
    internal IEnumerable<FreightCargo> Resources => Cargo.Concat(Smuggling).Concat(Missions.SelectMany(m => m.Planned)).Concat(Revert?.Planned ?? []);
}
internal sealed record FreightSnapshot(DateTimeOffset Taken, FreighterInfo[] Ships, string? Warning);

// This reader never calls mission assignment, pathfinding, reservation or saved-data setters.
internal static class FreightOverview
{
    sealed record TaskLink(int Ship, int Order, byte Contract, short Resource, int Source, ItemType DestinationType, int Destination,
        int Task, DispatchPhase Phase, string Status);
    static readonly Func<ShipMission, ShipMissionType, ShipCommandList?> Commands =
        typeof(ShipMission).GetMethod("ResolveMissionCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ShipMission, ShipMissionType, ShipCommandList?>>();

    internal static bool OwnFreighter(Empire e, Ship? ship) => ship != null && !ship.IsDestroyed && ship.EmpireId == e.EmpireId &&
        ship.Role is ShipRole.FreighterSmall or ShipRole.FreighterMedium or ShipRole.FreighterLarge;

    internal static FreightSnapshot Read(Galaxy g, Empire e)
    {
        var session = State.For(g, e.EmpireId);
        TaskLink[] links; string? warning;
        lock (session.Gate)
        {
            warning = session.Fault;
            links = session.Data?.Tasks.SelectMany(t => t.Voyages.Select(v =>
                new TaskLink(v.ShipId, v.OrderId, v.ContractId, v.ResourceId, t.SourceId, t.DestinationType, t.DestinationId, t.Id, t.Phase, t.Status))).ToArray() ?? [];
        }
        var result = new List<FreighterInfo>();
        foreach (var ship in e.Ships.ToArray().Where(s => OwnFreighter(e, s)).DistinctBy(s => s.ShipId))
        {
            FreighterInfo? row = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { row = ReadShip(g, e, ship, links); break; }
                catch (Exception ex)
                {
                    row = new(ship.ShipId, ship.Name ?? $"Ship #{ship.ShipId}", FreightActivity.Unavailable,
                        "State unavailable; retrying on next sample", [], [], [], null, ex.Message);
                }
            }
            result.Add(row!);
        }
        return new(DateTimeOffset.Now, result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id).ToArray(),
            warning == null ? null : "FD metadata unavailable or stale: " + warning);
    }

    static FreighterInfo ReadShip(Galaxy g, Empire e, Ship ship, TaskLink[] links)
    {
        var current = ship.Mission;
        var next = ship.NextMissions?.ToArray() ?? [];
        var revert = ship.RevertMission;
        var positions = next.Concat(current == null ? [] : new[] { current }).Concat(revert == null ? [] : new[] { revert })
            .Select(m => (Mission: m, m.Type, m.CommandProgress, m.PrimaryTarget, m.SecondaryTarget)).ToArray();
        var cargo = ReadCargo(g, ship.Cargo);
        var smuggling = ReadCargo(g, ship.CargoSmuggling);
        var missions = new List<FreightMission>();
        FreightActivity activity = FreightActivity.Idle;
        if (current != null && current.Type != ShipMissionType.Undefined)
        {
            missions.Add(ReadMission(g, e, ship, current, links, "Current"));
            var command = current.ResolveCurrentCommand().Type;
            activity = current.Type switch
            {
                ShipMissionType.Refuel => FreightActivity.Refuel,
                ShipMissionType.Repair => FreightActivity.Repair,
                ShipMissionType.Retrofit => FreightActivity.Retrofit,
                ShipMissionType.Transport when command == ShipCommandType.Refuel => FreightActivity.Refuel,
                ShipMissionType.Transport => current.CheckForCurrentOrFutureCommand(ShipCommandType.Load) ? FreightActivity.Pickup : FreightActivity.Delivery,
                _ => FreightActivity.Other
            };
        }
        for (int i = 0; i < next.Length; i++)
            missions.Add(ReadMission(g, e, ship, next[i], links, $"Queued {i + 1}", true));
        var suspended = revert != null && revert.Type != ShipMissionType.Undefined && !ReferenceEquals(revert, current)
            ? ReadMission(g, e, ship, revert, links, "Suspended / revert (not a guaranteed next mission)") : null;
        bool refitting = ship.RetrofitConstructionSystem != null || current?.Type == ShipMissionType.Retrofit;
        bool unfinished = !refitting && (ship.Summary == null || ship.ConstructionLevel < 1f);
        if (unfinished) activity = FreightActivity.Construction;
        else if (refitting) activity = FreightActivity.Retrofit;
        string condition = unfinished ? "Under construction" : "Completed hull";
        if (ship.DamagedComponentCount > 0) condition += $"; damaged components {ship.DamagedComponentCount}";
        if (!OwnFreighter(e, ship) || !ReferenceEquals(g.Ships.GetById(ship.ShipId), ship) || !ReferenceEquals(ship.Mission, current) ||
            !ReferenceEquals(ship.RevertMission, revert) || !(ship.NextMissions?.ToArray() ?? []).SequenceEqual(next) ||
            positions.Any(p => p.Mission.Type != p.Type || p.Mission.CommandProgress != p.CommandProgress ||
                !p.Mission.PrimaryTarget.Equals(p.PrimaryTarget) || !p.Mission.SecondaryTarget.Equals(p.SecondaryTarget)) ||
            !ReadCargo(g, ship.Cargo).SequenceEqual(cargo) || !ReadCargo(g, ship.CargoSmuggling).SequenceEqual(smuggling))
            throw new InvalidOperationException("Ship changed during sample; data unavailable until the next sample");
        return new(ship.ShipId, ship.Name ?? $"Ship #{ship.ShipId}", activity, condition, cargo, smuggling, missions.ToArray(), suspended, null);
    }

    static FreightMission ReadMission(Galaxy g, Empire e, Ship ship, ShipMission mission, TaskLink[] links, string label, bool queued = false)
    {
        var type = mission.Type; byte storedProgress = mission.CommandProgress;
        // CheckAssignNextQueuedMission rebuilds the queued mission through ExecuteTaskAssignShipMission.
        byte progress = queued ? (byte)0 : storedProgress;
        var primaryItem = mission.PrimaryTarget; var secondaryItem = mission.SecondaryTarget;
        string primary = Target(g, primaryItem, mission.PrimaryTargetType, mission.PrimaryGalaxyX, mission.PrimaryGalaxyY);
        string secondary = Target(g, secondaryItem, mission.SecondaryTargetType, mission.SecondaryGalaxyX, mission.SecondaryGalaxyY);
        var definitions = Commands(mission, type)?.ToArray();
        var steps = new List<string>();
        if (definitions == null) steps.Add("Command definitions unavailable");
        else if (progress > definitions.Length) steps.Add("Invalid command position; queue unavailable");
        else if (progress == definitions.Length) steps.Add("No remaining commands");
        else for (int i = progress; i < definitions.Length; i++)
        {
            var command = definitions[i];
            string target = command.TargetType switch
            {
                ShipCommandTargetType.PrimaryTarget => primary,
                ShipCommandTargetType.SecondaryTarget => secondary,
                ShipCommandTargetType.Cargo => command.Type == ShipCommandType.Load ? primary : command.Type == ShipCommandType.Unload ? secondary : "Mission cargo",
                _ => ""
            };
            steps.Add($"{i + 1}. {command.Type}" + (target.Length == 0 ? "" : " -> " + target));
        }
        var planned = ReadCargo(g, mission.Cargo);
        var contracts = new List<string>(); var reasons = new List<string>(); bool dispatch = false;
        var stubs = mission.ContractStubs?.ToArray() ?? [];
        foreach (var stub in stubs.DistinctBy(s => (s.EmpireId, s.OrderId, s.ContractId)))
        {
            var buyer = g.Empires.GetById(stub.EmpireId);
            var order = buyer?.Orders.GetByOrderId(stub.OrderId);
            var contract = order?.Contracts?.GetByContractId(stub.ContractId);
            string id = $"Order {stub.OrderId} / contract {stub.ContractId}";
            bool validContract = contract != null && contract.FreighterId == ship.ShipId && contract.EmpireId == stub.EmpireId;
            if (!validContract)
                contracts.Add(id + ": unavailable or no longer assigned to this ship");
            else
                contracts.Add(FormattableString.Invariant($"{id}: {g.Resources.GetById(order!.ResourceId)?.Name ?? order.ResourceId.ToString()} | contracted {(float)contract!.AmountToFulfill:0.#}, picked {(float)contract.AmountPickedUp:0.#}, delivered {(float)contract.AmountDelivered:0.#}; {Endpoint(g, contract.Supplier)} -> {Endpoint(g, order.Destination)}"));
            var link = stub.EmpireId == e.EmpireId ? links.FirstOrDefault(l => l.Ship == ship.ShipId && l.Order == stub.OrderId && l.Contract == stub.ContractId) : null;
            if (link != null)
            {
                bool confirmed = validContract && order!.ResourceId == link.Resource && contract!.Supplier.Type == ItemType.Ship &&
                    contract.Supplier.Id == link.Source && order.Destination.Type == link.DestinationType && order.Destination.Id == link.Destination;
                dispatch |= confirmed;
                reasons.Add($"{id}: FD task #{link.Task}: manual export; {link.Phase}. {link.Status}" +
                    (!confirmed ? " Retained FD metadata; native assignment unconfirmed." : "") +
                    (link.Phase == DispatchPhase.Cancelling ? " New pickups stopped; loaded delivery continues." : ""));
            }
            else reasons.Add(id + ": native contract; selection reason not recorded");
        }
        if (reasons.Count == 0) reasons.Add(mission.IsManuallyAssigned
            ? "Native mission marked manually assigned; selection reason not recorded"
            : "Native mission/contract; selection reason not recorded");
        if (mission.Type != type || mission.CommandProgress != storedProgress || !mission.PrimaryTarget.Equals(primaryItem) ||
            !mission.SecondaryTarget.Equals(secondaryItem) || !ReadCargo(g, mission.Cargo).SequenceEqual(planned) ||
            !(mission.ContractStubs?.ToArray() ?? []).SequenceEqual(stubs))
            throw new InvalidOperationException("Mission changed during sample; retrying");
        string title = $"{label}: {type}" + (type == ShipMissionType.Transport ? " (pickup -> delivery)" : " (primary / secondary targets)");
        return new(title, primary, secondary, planned, steps.ToArray(), contracts.ToArray(), dispatch, type == ShipMissionType.Transport, label == "Current", string.Join(" | ", reasons.Distinct()));
    }

    static string Target(Galaxy g, Item item, ShipMissionTargetType type, int x, int y) =>
        type == ShipMissionTargetType.GalaxyCoordinates ? $"Coordinates {x}, {y}" : Endpoint(g, item);

    internal static string Endpoint(Galaxy g, Item item)
    {
        if (item.Id < 0 || item.Type == ItemType.Undefined) return "None";
        return item.Type switch
        {
            ItemType.Ship => g.Ships.GetById(item.Id)?.Name ?? $"Missing ship #{item.Id}",
            ItemType.Orb => g.Orbs.GetById(item.Id)?.Name ?? $"Missing orb #{item.Id}",
            _ => $"{item.Type} #{item.Id}"
        };
    }

    internal static FreightCargo[] ReadCargo(Galaxy g, CargoList? cargo)
    {
        var amounts = new Dictionary<short, double>();
        foreach (var c in cargo?.ToArray() ?? [])
        {
            if (c == null) continue;
            float amount = c.Amount;
            if (!float.IsFinite(amount) || amount < 0) throw new InvalidDataException("Invalid cargo amount");
            if (amount > 0) amounts[c.ResourceId] = amounts.GetValueOrDefault(c.ResourceId) + amount;
        }
        return amounts.OrderBy(p => p.Key).Select(p => new FreightCargo(p.Key, g.Resources.GetById(p.Key)?.Name ?? $"Resource #{p.Key}", p.Value)).ToArray();
    }

    internal static FreighterInfo[] Filter(IEnumerable<FreighterInfo> ships, string name, short? resource, FreightActivity? activity, int scope) =>
        ships.Where(s => s.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (resource == null || s.Resources.Any(c => c.Id == resource)) && (activity == null || s.Activity == activity) &&
            (scope == 0 || s.Error == null && (scope == 1 ? s.Dispatch : !s.Dispatch))).ToArray();

    internal static IEnumerable<string> Details(FreighterInfo ship)
    {
        yield return $"{ship.Name} (#{ship.Id}) | {ship.Activity} | {ship.Condition}";
        if (ship.Error != null) { yield return "Unavailable: " + ship.Error; yield break; }
        yield return "ACTUAL HOLD (fuel tanks excluded): " + CargoText(ship.Cargo);
        if (ship.Smuggling.Length > 0) yield return "SEPARATE SMUGGLING HOLD: " + CargoText(ship.Smuggling);
        if (ship.Missions.Length == 0) yield return "No current or queued mission. Next assignment not selected.";
        foreach (var mission in ship.Missions.Concat(ship.Revert == null ? [] : new[] { ship.Revert }))
        {
            yield return mission.Title;
            yield return (mission.Transport ? "Pickup: " : "Primary: ") + mission.Primary + (mission.Transport ? " | Delivery: " : " | Secondary: ") + mission.Secondary;
            yield return "Mission cargo plan (not actual hold): " + CargoText(mission.Planned);
            yield return "Reason: " + mission.Reason;
            foreach (string contract in mission.Contracts) yield return contract;
            yield return mission.Current ? "Current command, then remaining commands (engine may revise):" : "Assigned commands (engine may revise):";
            foreach (string command in mission.Commands) yield return command;
        }
        if (ship.Missions.Length > 0) yield return "Beyond assigned missions: next assignment not selected.";
    }

    internal static string CargoText(IEnumerable<FreightCargo> cargo)
    {
        var parts = cargo.Select(c => c.Name + " " + c.Amount.ToString("0.#", CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 0 ? "Empty / none" : string.Join(", ", parts);
    }
}
