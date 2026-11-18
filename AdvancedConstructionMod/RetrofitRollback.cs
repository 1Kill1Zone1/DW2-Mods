using System.Collections;
using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;

namespace AdvancedConstructionMod;

// Bounded undo of the objects native mission replacement may touch; never traverse Galaxy or ship ownership graphs.
internal sealed class RetrofitRollback
{
    private readonly HashSet<object> seen=new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> undo=[];
    internal RetrofitRollback(Galaxy galaxy,Empire empire,Ship ship,StellarObject yard)
    {
        Capture(ship,true);
        Capture(empire.Economy,true);
        Capture(yard.GetEmpire(galaxy)?.Economy,true);
        CaptureCargo(yard);
        foreach(var reservation in ship.ResourceReservations)
        {
            object? owner=(int)reservation.ItemType switch{1=>galaxy.Orbs.GetById(reservation.ItemId),2=>galaxy.Ships.GetById(reservation.ItemId),8=>galaxy.Colonies.GetById(reservation.ItemId),_=>null};
            CaptureCargo(owner);
        }
        Capture(empire.BoardingShips);
        if(ship.Fleet!=null)Field(ship.Fleet,"WaitingForFuelTankers");
        var targets=new List<StellarObject?>{ship.CurrentTarget,ship.Parent.ResolveBase<StellarObject>(galaxy),ship.DockDataDockTarget.ResolveBase<StellarObject>(galaxy)};
        if(ship.DockData!=null)targets.Add(ship.DockData.DockTarget.ResolveBase<StellarObject>(galaxy));
        if(ship.Mission!=null)
        {
            targets.Add(ship.Mission.PrimaryTarget.ResolveBase<StellarObject>(galaxy));
            targets.Add(ship.Mission.SecondaryTarget.ResolveBase<StellarObject>(galaxy));
        }
        foreach(var target in targets.Where(t=>t!=null).Distinct())
        {
            if(target is Ship other)
            {
                Capture(other.BattleData);Capture(other.DockingSystem);
                if(other.Fleet!=null)Field(other.Fleet,"WaitingForFuelTankers");
            }
            else if(target is Creature creature)Capture(creature.BattleData);
            else if(target is Orb orb&&orb.Colony!=null)
            {
                foreach(var troop in orb.Colony.Troops.Concat(orb.Colony.TroopsInvading).Where(t=>t!=null))Field(troop,"ReservedPickupShipId");
            }
        }
    }
    private void CaptureCargo(object? owner)
    {
        if(owner is Orb orb)owner=orb.Colony;
        if(owner==null)return;
        foreach(string name in new[]{"Cargo","CargoReserved"})Capture(AccessTools.Field(owner.GetType(),name)?.GetValue(owner));
    }
    private void Field(object target,string name)
    {
        var field=AccessTools.Field(target.GetType(),name)??throw new MissingFieldException(target.GetType().Name,name);
        object? value=field.GetValue(target);undo.Add(()=>field.SetValue(target,value));
    }
    private static bool Owned(object value)
    {
        var type=value.GetType();if(type.IsValueType||value is string)return false;
        if(value is Array||value is IDictionary||value is IList)return true;
        if(type.Namespace?.StartsWith("System.Collections",StringComparison.Ordinal)==true)return value is IEnumerable;
        if(type.Namespace?.StartsWith("DistantWorlds.Core.NonBlocking",StringComparison.Ordinal)==true)return true;
        return type.Namespace==typeof(Ship).Namespace&&(type.Name.EndsWith("List",StringComparison.Ordinal)||type.Name is
            "Cargo" or "CargoReservation" or "ShipMission" or "DockData" or "DockingSystem" or "BattleData" or "ContractStub" or "ShipComponent" or "Transaction" or "EmpireTradeVolume");
    }
    private void Capture(object? value,bool root=false)
    {
        if(value==null||!seen.Add(value)||!root&&!Owned(value))return;
        if(seen.Count>4096)throw new InvalidOperationException("This ship's active mission is too complex to replace safely. Cancel it explicitly first.");
        if(value is Array array)
        {
            var copy=(Array)array.Clone();undo.Add(()=>Array.Copy(copy,array,copy.Length));
            foreach(var item in array)if(item!=null&&Owned(item))Capture(item);
            return;
        }
        for(Type? type=value.GetType();type!=null&&type!=typeof(object);type=type.BaseType)
        foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
        {
            object? captured=field.GetValue(value);
            if(!field.IsInitOnly)undo.Add(()=>field.SetValue(value,captured));
            if(captured!=null&&Owned(captured))Capture(captured);
        }
    }
    internal void Restore()
    {
        for(int i=undo.Count-1;i>=0;i--)undo[i]();
    }
}
