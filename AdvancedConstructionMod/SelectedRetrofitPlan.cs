using DistantWorlds.Types;

namespace AdvancedConstructionMod;

// A window-local draft, never a queued or automatically dispatched command.
internal sealed record SelectedRetrofitPlan(Ship Ship,string Series,Design? Target,string? Fingerprint,string? Problem)
{
    internal static SelectedRetrofitPlan Resolve(Galaxy galaxy,Empire empire,Ship ship,string series)
    {
        try
        {
            var registry=ConstructionCampaigns.Get(galaxy,empire);
            if(!registry.TryGet(empire,out var state,out var error))return new(ship,series,null,null,error);
            if(!state.Classes.ContainsKey(series))return new(ship,series,null,null,"Assigned Series no longer exists");
            var source=ship.GetDesign();
            if(source==null||source.EmpireId!=empire.EmpireId)return new(ship,series,null,null,"Current project is unavailable");
            // Native mobile-ship choices use GetAllForRole, independently of readiness, yards and
            // shared target pins. In particular a jumping ship can still have a draft assignment.
            var native=empire.Designs.GetAllForRole(source.GetRole(galaxy),galaxy,false,true,false,empire);
            var target=native.Where(d=>d!=null&&d.EmpireId==empire.EmpireId&&!d.Obsolete&&
                    ReferenceEquals(galaxy.Designs.GetById(d.DesignId),d)&&state.DesignClasses.GetValueOrDefault(d.DesignId)==series&&
                    RetrofitRuntime.NativeCompatible(RetrofitRuntime.Fact(galaxy,empire,source),RetrofitRuntime.Fact(galaxy,empire,d)))
                .OrderByDescending(d=>d.StarDate).ThenByDescending(d=>d.DesignId).FirstOrDefault();
            if(target==null)
            {
                bool rolePresent=empire.Designs.Any(d=>d!=null&&state.DesignClasses.GetValueOrDefault(d.DesignId)==series&&d.GetRole(galaxy)==source.GetRole(galaxy));
                return new(ship,series,null,null,rolePresent?"No active, researched, valid compatible project in this Series":"No project for native role "+source.GetRole(galaxy)+" in this Series");
            }
            return new(ship,series,target,SelectedRetrofit.Fingerprint(target),null);
        }
        catch(Exception ex){return new(ship,series,null,null,"Target selection unavailable: "+ex.GetBaseException().Message);}
    }
    internal string? Reason(Galaxy galaxy,Empire empire)
    {
        if(Problem!=null||Target==null)return Problem??"No compatible target";
        var current=Resolve(galaxy,empire,Ship,Series);
        if(current.Target?.DesignId!=Target.DesignId||current.Fingerprint!=Fingerprint)return "Latest compatible project changed; review the plan again";
        return SelectedRetrofit.Reason(galaxy,empire,Ship,Target);
    }
}

// Snapshot exactly what the player reviewed. Readiness is rechecked; changed orders/projects
// require a new review instead of replacing work the player issued after opening this modal.
internal sealed class SelectedRetrofitRequest(SelectedRetrofitPlan plan)
{
    internal readonly SelectedRetrofitPlan Plan=plan;
    private readonly int sourceId=plan.Ship.DesignId;
    private readonly string sourceFingerprint=SelectedRetrofit.Fingerprint(plan.Ship.GetDesign());
    private readonly MissionIdentity mission=Identity(plan.Ship.Mission),revert=Identity(plan.Ship.RevertMission);
    private readonly MissionIdentity[] next=plan.Ship.NextMissions.Select(Identity).ToArray();
    private readonly ShipMissionAssignmentType mode=plan.Ship.MissionAssignmentType;
    private readonly Fleet? fleet=plan.Ship.Fleet;
    private sealed record MissionIdentity(ShipMission? Mission,ShipMissionType Type,int TargetType,int TargetId,int DesignId,bool Manual);
    private static MissionIdentity Identity(ShipMission? mission)=>mission==null?new(null,default,0,-1,-1,false):
        new(mission,mission.Type,(int)mission.PrimaryTargetType,mission.PrimaryTarget.Id,mission.BuildDesignId,mission.IsManuallyAssigned);
    internal static SelectedRetrofitRequest? Capture(SelectedRetrofitPlan plan,out string? error)
    {
        try{var request=new SelectedRetrofitRequest(plan);error=null;return request;}
        catch(Exception ex){error="Ship state changed or became unavailable during review: "+ex.GetBaseException().Message;return null;}
    }
    internal string? Reason(Galaxy galaxy,Empire empire)
    {
        var ship=Plan.Ship;
        if(ship.DesignId!=sourceId||SelectedRetrofit.Fingerprint(ship.GetDesign())!=sourceFingerprint)return "Current project changed after review";
        if(Identity(ship.Mission)!=mission||Identity(ship.RevertMission)!=revert||!ship.NextMissions.Select(Identity).SequenceEqual(next)||ship.MissionAssignmentType!=mode||!ReferenceEquals(ship.Fleet,fleet))return "Ship orders or fleet changed after review";
        return Plan.Reason(galaxy,empire);
    }
}

internal sealed record SelectedRetrofitResult(int ShipId,bool Issued,string Text);
