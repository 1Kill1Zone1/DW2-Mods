using System.Runtime.CompilerServices;
using System.Text.Json;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace CommunityPatch;

internal static class SalvageOrders
{
    internal sealed class Order
    {
        public int Ship { get; set; }
        public short Owner { get; set; }
        public int Original { get; set; }
        public int Design { get; set; }
        public int Builder { get; set; } = -1;
        public bool Submitted { get; set; }
        public List<Study> Studies { get; set; } = new();
        public float Work { get; set; }
        public long LastWork { get; set; }
    }

    internal const int FullDismantle = -1;
    internal const int RecoverBays = -2;
    private const string Module = "CommunityPatch";
    private const string SaveKey = "salvage.orders.v1";
    private static Func<object,string,string,string> read = null!;
    private static Action<object,string,string,string> write = null!;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<Galaxy, Campaign> Galaxies = new();

    private sealed class Campaign
    {
        internal string? Raw;
        internal bool Invalid;
        internal List<Order> Orders = new();
        internal DateTime NextReview;
    }

    internal static void Install(Func<object,string,string,string> get, Action<object,string,string,string> set)
    {
        read = get;
        write = set;
        var h = new Harmony("local.dw2.community-patch.salvage");
        try
        {
            h.Patch(AccessTools.DeclaredMethod(typeof(Empire), nameof(Empire.DoTasks), [typeof(DateTime),typeof(Galaxy)]),
                postfix:new HarmonyMethod(typeof(SalvageOrders),nameof(Review)));
            h.Patch(AccessTools.DeclaredMethod(typeof(ConstructionSystem),nameof(ConstructionSystem.PrepareShipComponentsForRetrofit)),
                prefix:new HarmonyMethod(typeof(SalvageOrders),nameof(BeforeStrip)),
                postfix:new HarmonyMethod(typeof(SalvageOrders),nameof(AfterStrip)));
            h.Patch(AccessTools.DeclaredMethod(typeof(Ship),nameof(Ship.CheckAutoAssignMission)),
                prefix:new HarmonyMethod(typeof(SalvageOrders),nameof(KeepWorking)));
            foreach(var args in new Type[][] {
                [typeof(Galaxy),typeof(Random),typeof(ComponentCategory),typeof(bool)], [typeof(Random),typeof(bool)] })
                h.Patch(AccessTools.DeclaredMethod(typeof(ResearchProjectList),nameof(ResearchProjectList.SelectRandomVisibleProject),args),
                    postfix:new HarmonyMethod(typeof(SalvageOrders),nameof(SelectedProject)));
            SalvageDismantle.Install(h,get,set);
            SalvagePanel.Install(h);
        }
        catch { h.UnpatchAll(h.Id); throw; }
    }

    // Read through Matrix so loading an older save into the same Galaxy restores its own orders.
    private static Campaign Load(Galaxy g)
    {
        var state = Galaxies.GetOrCreateValue(g);
        string raw = read(g,Module,SaveKey) ?? "";
        if (raw == state.Raw) return state;
        try
        {
            state.Orders = string.IsNullOrEmpty(raw) ? new() : JsonSerializer.Deserialize<List<Order>>(raw) ?? throw new JsonException();
            if (state.Orders.Any(o=>o==null || o.Studies==null || o.Design<RecoverBays)) throw new JsonException();
            state.Invalid=false;
        }
        catch(JsonException)
        {
            Console.Error.WriteLine("Community Patch: invalid saved salvage orders. Salvage is unavailable for this campaign.");
            state.Orders=new();state.Invalid=true;
        }
        state.Raw = raw;
        state.NextReview = default;
        return state;
    }

    private static void Save(Galaxy g, Campaign state)
    {
        string raw = JsonSerializer.Serialize(state.Orders);
        state.Raw=null;
        write(g,Module,SaveKey,raw);
        state.Raw = raw;
    }

    internal static bool HasOrder(Galaxy g, Ship s)
    {
        lock (Gate) return Load(g).Orders.Exists(o=>o.Ship==s.ShipId);
    }

    internal static bool Eligible(Empire e,Ship s) =>
        e.IsPlayer && s.EmpireId==e.EmpireId && !s.IsDestroyed && !s.IsUnderConstructionOrRepair &&
        s.Components.Any(c=>CanStudy(e,c));

    private static bool CanStudy(Empire e, ShipComponent c) =>
        !c.IsEmpty && c.Status>0 && !e.Research.ResearchedComponents.ContainsId(c.ComponentId);

    internal static IEnumerable<(GameTask Task,Design Design)> Choices(Galaxy g,Empire e,Ship s)
    {
        foreach(var task in GameTask.GenerateTaskRetrofitShip(g,e,s,true,false,false) ?? new GameTaskList())
        {
            if(!task.Enabled || task.Type!=GameTaskType.RetrofitShip || task.Values is not {Count:>0}) continue;
            var d=e.Designs.GetById(task.Values[0]);
            if(d!=null && d.DesignId!=s.DesignId && d.CheckEmpireHasResearchedAllTech(g,e)) yield return (task,d);
        }
    }

    internal static bool Start(Galaxy g,Empire e,Ship s,int design)
    {
        lock(Gate)
        {
            var state=Load(g);
            if(state.Invalid || !e.IsPlayer || s.EmpireId!=e.EmpireId || s.IsDestroyed || s.IsUnderConstructionOrRepair ||
               state.Orders.Exists(o=>o.Ship==s.ShipId))return false;
            if(design==RecoverBays)
            {
                if(!s.IsBase || !SalvageDismantle.CanRecover(g,e,s))return false;
            }
            else if(!Eligible(e,s))return false;
            if(design<0 ? !s.IsBase || (design!=FullDismantle && design!=RecoverBays) : !Choices(g,e,s).Any(p=>p.Design.DesignId==design))return false;
            state.Orders.Add(new Order { Ship=s.ShipId, Owner=e.EmpireId, Original=s.DesignId, Design=design });
            Save(g,state);
            return true;
        }
    }

    internal static bool Cancel(Galaxy g,Empire e,Ship s)
    {
        lock(Gate)
        {
            var state=Load(g);
            var order=state.Orders.Find(o=>o.Ship==s.ShipId && o.Owner==e.EmpireId);
            if(order==null || !e.IsPlayer || s.EmpireId!=e.EmpireId)return false;
            if(order.Submitted)
            {
                // Only cancel this exact mobile retrofit approach. Once native work starts the yard owns it.
                if(s.Mission?.Type!=ShipMissionType.Retrofit || s.Mission.BuildDesignId!=order.Design) return false;
                e.ExecuteTaskClearShipMission(GameTask.GenerateTaskClearMission(g,e,s),false);
            }
            Remove(g,e,state,order);
            if(order.Studies.Count>0) Report(g,e,s,order);
            return true;
        }
    }

    private static bool OwnMove(Ship builder,Order o)=>builder.Mission?.Type==ShipMissionType.Move &&
        builder.Mission.PrimaryTarget.Type==ItemType.Ship && builder.Mission.PrimaryTarget.Id==o.Ship &&
        !builder.Mission.IsManuallyAssigned;

    private static void Remove(Galaxy g,Empire e,Campaign state,Order order)
    {
        var builder=order.Builder<0?null:g.Ships.GetById(order.Builder);
        if(builder!=null && builder.EmpireId==e.EmpireId && OwnMove(builder,order))
            e.ExecuteTaskClearShipMission(GameTask.GenerateTaskClearMission(g,e,builder),false);
        state.Orders.Remove(order);
        Save(g,state);
    }

    private static bool Near(Ship a,Ship b) => a.LocationId==b.LocationId &&
        Vector3.Distance(a.GetPositionRaw(),b.GetPositionRaw()) <= a.CollisionSphereRadius+b.CollisionSphereRadius+500f;

    private static bool Idle(Ship s)=>s.Mission==null || s.Mission.Type==ShipMissionType.Undefined;

    private static bool Ready(Ship b,Empire e)=> b.EmpireId==e.EmpireId && !b.IsDestroyed && !b.IsUnderConstructionOrRepair &&
        b.MissionAssignmentType==ShipMissionAssignmentType.FullyAutomate && b.Role==ShipRole.ConstructionShip &&
        b.Summary?.ConstructionBuildSpeed>0 && b.ConstructionSystem!=null && b.ConstructionSystem.WaitQueue.Count==0 &&
        b.ConstructionSystem.Yards.All(y=>y.ShipUnderConstructionId<0);

    // The caller holds Gate. A manual order takes precedence even if the builder is already nearby.
    private static Ship? Builder(Galaxy g,Empire e,Campaign state,Order o,Ship target)
    {
        if(o.Builder>=0)
        {
            var b=g.Ships.GetById(o.Builder);
            if(b!=null && Ready(b,e) && (OwnMove(b,o)||Idle(b)&&Near(b,target)))
                return Near(b,target)?b:null;
            o.Builder=-1;
            Save(g,state);
        }
        var system=target.GetSystem(g);
        if(system!=null && e.DangerousSystems?.Any(x=>x?.SystemId==system.SystemId)==true)return null;
        var candidates=e.ConstructionShips.Where(b=>b!=null && Ready(b,e) && Idle(b) &&
                !state.Orders.Exists(x=>x.Builder==b.ShipId))
            .OrderBy(b=>Math.Pow((double)b.GalaxyX-target.GalaxyX,2)+Math.Pow((double)b.GalaxyY-target.GalaxyY,2));
        foreach(var b in candidates)
        {
            if(!Near(b,target))
            {
                List<int>? path=null;
                if(b.LocationId!=target.LocationId)
                {
                    var origin=b.GetLocation(g);
                    if(origin==null || target.GetLocation(g)==null)continue;
                    path=e.FindBestJumpPathToDestination(b,origin,target,out float distance,out _);
                    if(path==null || path.Count==0 || !float.IsFinite(distance) || distance>b.CurrentFuelRange())continue;
                }
                if(!b.AssignMissionMove(target,path!,false))continue;
            }
            o.Builder=b.ShipId;
            Save(g,state);
            return Near(b,target)?b:null;
        }
        return null;
    }

    // Only suppress ordinary idle assignment during work at the target. Explicit missions still win.
    private static bool KeepWorking(Ship __instance,Empire empire, ref bool __result)
    {
        if(!empire.IsPlayer || __instance.Role!=ShipRole.ConstructionShip || !Idle(__instance) ||
           !Ready(__instance,empire) || !Galaxies.TryGetValue(empire.Galaxy,out var state))return true;
        lock(Gate)
        {
            var order=state.Orders.Find(o=>o.Builder==__instance.ShipId && !o.Submitted);
            var target=order==null?null:empire.Galaxy.Ships.GetById(order.Ship);
            if(target==null || target.IsDestroyed || target.EmpireId!=empire.EmpireId || !Near(__instance,target))return true;
            __result=false;
            return false;
        }
    }

    private static void Review(Empire __instance,Galaxy galaxy,DateTime time)
    {
        if(!__instance.IsPlayer)return;
        lock(Gate)
        {
            var state=Load(galaxy);
            if(state.Orders.Count==0 || time<state.NextReview && time>=state.NextReview.AddSeconds(-2)) return;
            state.NextReview=time.AddSeconds(1);
            foreach(var o in state.Orders.ToArray()) Advance(galaxy,__instance,state,o,time);
        }
    }

    private static void Advance(Galaxy g,Empire e,Campaign state,Order o,DateTime time)
    {
        var s=g.Ships.GetById(o.Ship);
        if(s==null || s.IsDestroyed || s.EmpireId!=o.Owner || o.Owner!=e.EmpireId || s.DesignId!=o.Original)
        { Remove(g,e,state,o); return; }
        if(o.Submitted)
        {
            if(s.IsUnderConstructionOrRepair || (s.Mission?.Type==ShipMissionType.Retrofit && s.Mission.BuildDesignId==o.Design) ||
               s.RetrofitConstructionSystem?.ContainsShip(s)==true) return;
            Remove(g,e,state,o);
            return;
        }
        if(s.IsUnderConstructionOrRepair)return;
        var builder=s.IsBase?Builder(g,e,state,o,s):null;
        if(s.IsBase && builder==null)
        {
            if(o.LastWork!=0){o.LastWork=0;Save(g,state);}
            return;
        }
        if(o.Design==RecoverBays)
        {
            if(SalvageDismantle.Restore(g,e,s,builder!,o,time)) Remove(g,e,state,o);
            else Save(g,state);
            return;
        }
        if(o.Design<0)
        {
            SalvageDismantle.Step(g,e,s,builder!,o,time);
            Save(g,state);
            if(s.IsDestroyed) Remove(g,e,state,o);
            return;
        }
        var choice=Choices(g,e,s).FirstOrDefault(p=>p.Design.DesignId==o.Design);
        if(choice.Task==null){Remove(g,e,state,o);return;}
        o.Submitted=true;
        Save(g,state);
        e.ExecuteTaskRetrofitShip(choice.Task);
    }

    private sealed record Strip(Galaxy Galaxy,Empire Owner,Order Order,ShipComponent[] Parts);

    private static void BeforeStrip(Galaxy galaxy,Ship ship,Design newDesign,out Strip? __state)
    {
        __state=null;
        if(ship==null || newDesign==null || ship.GetEmpire() is not {IsPlayer:true} e)return;
        lock(Gate)
        {
            var o=Load(galaxy).Orders.Find(x=>x.Ship==ship.ShipId && x.Submitted && x.Design==newDesign.DesignId &&
                x.Owner==e.EmpireId && x.Original==ship.DesignId);
            if(o!=null)__state=new Strip(galaxy,e,o,ship.Components.Where(c=>CanStudy(e,c)).ToArray());
        }
    }

    private static void AfterStrip(Ship ship,Design newDesign,Strip? __state)
    {
        if(__state==null || ship.DesignId!=newDesign.DesignId || ship.EmpireId!=__state.Owner.EmpireId)return;
        lock(Gate)
        {
            var state=Load(__state.Galaxy);
            if(!state.Orders.Contains(__state.Order))return;
            Remove(__state.Galaxy,__state.Owner,state,__state.Order);
            foreach(var part in __state.Parts)
            {
                if(ship.Components.Any(c=>!c.IsEmpty && c.ComponentBayId==part.ComponentBayId && c.ComponentId==part.ComponentId))continue;
                StudyPart(__state.Galaxy,__state.Owner,ship,part,__state.Order);
            }
            Report(__state.Galaxy,__state.Owner,ship,__state.Order);
        }
    }

    internal sealed record Study(short Project, float Before, float After);
    private sealed class Selection(ResearchProjectList list)
    {
        internal readonly ResearchProjectList List=list;
        internal ResearchProject? Project;
        internal float Before,Size;
        internal int Repeat;
    }
    [ThreadStatic] private static Selection? observing;

    private static void SelectedProject(ResearchProjectList __instance,ResearchProject? __result)
    {
        if(observing is not {} selection || __result==null || !ReferenceEquals(__instance,selection.List))return;
        selection.Project=__result;
        selection.Before=__result.Progress;
        selection.Repeat=__result.RepeatCount;
        selection.Size=__result.Definition.CalculateResearchSize(selection.Repeat);
    }

    internal static void StudyPart(Galaxy g,Empire e,Ship s,ShipComponent part,Order o)
    {
        if(!CanStudy(e,part))return;
        var outer=observing;
        var sample=new Selection(e.Research.NextProjectsIncludingQueued);
        observing=sample;
        try { Award(g,e,s,part); }
        finally { observing=outer; }
        if(sample.Project is not {} project)return;
        float after=project.RepeatCount==sample.Repeat ? Math.Min(sample.Size,project.Progress) : sample.Size;
        if(after>sample.Before)o.Studies.Add(new Study(project.ResearchProjectId,sample.Before,after));
    }

    internal static void Report(Galaxy g,Empire e,Ship s,Order o)
    {
        var lines=o.Studies.GroupBy(p=>p.Project).Select(group=>
            (e.Research.Projects.GetById(group.Key)?.Definition.Name ?? "Research") + "  +" +
            group.Sum(p=>(double)p.After-p.Before).ToString("0.##"));
        string report=o.Studies.Count==0?"No research gained.":string.Join(Environment.NewLine,lines);
        e.SendMessageToEmpire(e,new EmpireMessage(e.EmpireId,EmpireMessageType.Research,EmpireMessageSubType.Undefined,false,
            g.StarDate,"Salvage research: "+s.Name,report,null!,DrawDetailType.Undefined,EmpireMessagePriority.Normal));
    }

    internal static void Award(Galaxy galaxy,Empire empire,Ship ship,ShipComponent part)
    {
        if(!CanStudy(empire,part))return;
        var project=empire.Research.GetLowestProjectForComponent(part.ComponentId);
        float amount=project==null?50f:0.05f*(0.1f+(float)Galaxy.Rnd.NextDouble())*project.Definition.Size;
        float racial=empire.GetDominantRace()?.RetireAdvancedTechBoostFactor ?? 0f;
        ConstructionSystem.PerformTechProgress(galaxy,empire,ship,part.Component.Category,Math.Max(0,amount*(1+racial)),ConstructionType.Retire,racial,0);
    }
}
