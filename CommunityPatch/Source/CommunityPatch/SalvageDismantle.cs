using System.Text.Json;
using DistantWorlds.Types;
using HarmonyLib;

namespace CommunityPatch;

internal static class SalvageDismantle
{
    internal sealed record Removed(byte Bay, short Type, byte Level);
    private static Func<object,string,string,string> read=null!;
    private static Action<object,string,string,string> write=null!;
    private static string Key(Ship s)=> $"salvage.repair.{s.ShipId}";
    private const string Owner="CommunityPatch";
    [ThreadStatic] private static ConstructionSystem? currentYard;

    internal static void Install(Harmony h,Func<object,string,string,string> get,Action<object,string,string,string> set)
    {
        read=get;write=set;
        h.Patch(AccessTools.Method(typeof(ConstructionSystem),nameof(ConstructionSystem.DoTasks)),
            prefix:new HarmonyMethod(typeof(SalvageDismantle),nameof(EnterYard)),
            finalizer:new HarmonyMethod(typeof(SalvageDismantle),nameof(LeaveYard)));
        h.Patch(AccessTools.Method(typeof(ConstructionSystem),nameof(ConstructionSystem.BuildShip)),
            prefix:new HarmonyMethod(typeof(SalvageDismantle),nameof(Repair)));
    }

    private static void EnterYard(ConstructionSystem __instance,out ConstructionSystem? __state)
    { __state=currentYard;currentYard=__instance; }
    private static void LeaveYard(ConstructionSystem? __state)=>currentYard=__state;

    private sealed record RepairData(short OwnerId,int Design,List<Removed> Parts);

    private static RepairData? Read(Galaxy g,Ship s)
    {
        var raw=read(g,Owner,Key(s));
        if(string.IsNullOrEmpty(raw))return null;
        try
        {
            var data=JsonSerializer.Deserialize<RepairData>(raw);
            return data?.OwnerId==s.EmpireId && data.Design==s.DesignId && data.Parts!=null ? data:null;
        }
        catch(JsonException){return null;}
    }

    private static Component? Replacement(Galaxy g,Empire e,Ship s,Removed part)
    {
        var bay=s.GetShipHull()?.ComponentBays.FirstOrDefault(b=>b.ComponentBayId==part.Bay);
        var old=g.Components.FirstOrDefault(d=>d.ComponentId==part.Type);
        if(bay==null || old==null)return null;
        return e.Research.ResearchedComponents.GetByBayTypeAndSize(bay.Type,bay.MaximumComponentSize)
            .Where(c=>g.Components.FirstOrDefault(d=>d.ComponentId==c.ComponentId)?.Category==old.Category)
            .OrderByDescending(c=>c.ComponentId==part.Type).ThenByDescending(c=>c.Level).ThenBy(c=>c.ComponentId).FirstOrDefault();
    }

    internal static bool CanRecover(Galaxy g,Empire e,Ship s) =>
        Read(g,s)?.Parts.Any(p=>!s.Components.Any(c=>!c.IsEmpty && c.ComponentBayId==p.Bay) && Replacement(g,e,s,p)!=null)==true;

    private static void Accrue(SalvageOrders.Order order,Ship builder,DateTime time)
    {
        long now=time.Ticks;
        double elapsed=order.LastWork==0?0:Math.Clamp(TimeSpan.FromTicks(now-order.LastWork).TotalSeconds,0,1);
        order.LastWork=now;
        order.Work+=(float)elapsed*builder.Summary.ConstructionBuildSpeed;
    }

    private static void Store(Galaxy g,Ship s,RepairData data) =>
        write(g,Owner,Key(s),data.Parts.Count==0?"":JsonSerializer.Serialize(data));

    internal static bool Restore(Galaxy g,Empire e,Ship s,Ship builder,SalvageOrders.Order order,DateTime time)
    {
        var data=Read(g,s);
        if(data==null)return true;
        Accrue(order,builder,time);
        foreach(var part in data.Parts.ToArray())
        {
            if(s.Components.Any(c=>!c.IsEmpty && c.ComponentBayId==part.Bay))
            {
                data.Parts.Remove(part);
                continue;
            }
            var known=Replacement(g,e,s,part);
            if(known==null)continue;
            int size=Math.Max(1,(int)g.Components.First(c=>c.ComponentId==known.ComponentId).Size);
            if(order.Work<size)return false;
            order.Work-=size;
            s.Components.Add(new ShipComponent(known.ComponentId,ShipComponent.StatusFullHealth,part.Bay,known.Level));
            data.Parts.Remove(part);
            Store(g,s,data);
            s.ReDefine(g);
            return false;
        }
        Store(g,s,data);
        return true;
    }

    internal static string Missing(Galaxy g,Empire e,Ship s)
    {
        var data=Read(g,s);
        if(data==null)return "";
        var missing=data.Parts.Where(p=>Replacement(g,e,s,p)==null)
            .Select(p=>g.Components.FirstOrDefault(d=>d.ComponentId==p.Type) is {} d ? d.Name+" ("+d.Category+")" : "Unknown component").Distinct();
        return string.Join(", ",missing);
    }

    private static void Repair(Ship ship,Galaxy galaxy,Empire empire)
    {
        if(!ship.IsBase || !empire.IsPlayer || ship.EmpireId!=empire.EmpireId || currentYard==null ||
           !currentYard.Yards.Any(y=>y.ShipUnderConstructionId==ship.ShipId && y.ConstructionType==ConstructionType.Repair))return;
        var data=Read(galaxy,ship);
        if(data==null)return;
        foreach(var p in data.Parts.ToArray())
        {
            if(ship.Components.Any(c=>!c.IsEmpty && c.ComponentBayId==p.Bay))continue;
            var known=Replacement(galaxy,empire,ship,p);
            if(known==null)continue;
            ship.Components.Add(new ShipComponent(known.ComponentId,ShipComponent.StatusDestroyed,p.Bay,known.Level));
            data.Parts.Remove(p);
        }
        Store(galaxy,ship,data);
    }

    internal static void Step(Galaxy g,Empire e,Ship s,Ship builder,SalvageOrders.Order order,DateTime time)
    {
        Accrue(order,builder,time);
        for(int i=s.Components.Count-1;i>=0;i--)
        {
            var c=s.Components[i];
            if(c.IsEmpty||c.Status<=0)continue;
            if(order.Work<Math.Max(1,c.Component.Size))return;
            order.Work-=Math.Max(1,c.Component.Size);
            var data=Read(g,s)??new RepairData(e.EmpireId,s.DesignId,new());
            data.Parts.Add(new Removed(c.ComponentBayId,c.ComponentId,c.Level));
            write(g,Owner,Key(s),JsonSerializer.Serialize(data));
            s.Components.RemoveAt(i);
            SalvageOrders.StudyPart(g,e,s,c,order);
            s.ReDefine(g);
            return;
        }
        if(order.Work<Math.Max(1,s.HullHealth))return;
        SalvageOrders.Report(g,e,s,order);
        // Transfer materials only on completion. A canceled strip/repair cycle cannot mint cargo.
        var dismantled=Read(g,s);
        if(dismantled!=null && builder.Cargo!=null)
        {
            foreach(var part in dismantled.Parts)
            {
                var definition=g.Components.FirstOrDefault(c=>c.ComponentId==part.Type);
                if(definition!=null)builder.Cargo.AddResources(definition.ResourcesRequired,e.EmpireId);
            }
        }
        write(g,Owner,Key(s),"");
        e.ExecuteTaskScrapShip(s);
    }
}
