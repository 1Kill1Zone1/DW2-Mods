using AdvancedConstructionMod;
using DistantWorlds.Types;
using HarmonyLib;

internal static partial class Checks
{
    private static bool ThrowSaveBody()=>throw new IOException("Injected native serialization failure");
    private static void SaveLockCheck(Galaxy galaxy,Action write)
    {
        var method=AccessTools.Method(typeof(Galaxy),nameof(Galaxy.WriteToStream));
        var fault=new Harmony("acm.check.serialization-failure");
        fault.Patch(method,prefix:new HarmonyMethod(typeof(Checks),nameof(ThrowSaveBody)){priority=-100});
        try{ExpectFailure(write,"native serialization failure propagated");}
        finally{fault.UnpatchAll(fault.Id);}
        object gate=ConstructionCampaigns.Get(galaxy).Gate;
        bool available=Task.Run(()=>{if(!Monitor.TryEnter(gate,500))return false;Monitor.Exit(gate);return true;}).GetAwaiter().GetResult();
        Require(available,"native save finalizer releases class transaction lock after failure");
    }
    private static void AtomicityChecks(Galaxy galaxy,Empire empire,Design target)
    {
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var current,out _),"class graph available for atomicity cases");
        var facts=Facts(galaxy,empire);long revision=current.Revision;
        current.DesignClasses.TryGetValue(target.DesignId,out var classId);
        using var start=new Barrier(2);
        Task<bool> Attempt()=>Task.Run(()=>
        {
            start.SignalAndWait();
            return ConstructionCommands.TryApply(galaxy,empire,target,revision,classId,[],facts,out _,out _);
        });
        var one=Attempt();var two=Attempt();Task.WaitAll(one,two);
        Require(one.Result!=two.Result,"simultaneous Apply accepts one revision exactly once");
        Require(registry.TryGet(empire,out current,out _)&&current.Revision==revision+1,"concurrent rejected Apply cannot overwrite committed revision");
        var source=target.Clone(galaxy);source.DesignId=galaxy.Designs.Max(d=>d.DesignId)+10;source.Name="Atomic source";source.Obsolete=false;source.UpgradeDesignId=int.MinValue;
        galaxy.Designs.Add(source);empire.Designs.Add(source);
        facts=Facts(galaxy,empire);string before=Serialized(current);int arrow=source.UpgradeDesignId;
        source.UpgradeDesignId=target.DesignId;
        Require(!ConstructionCommands.TryApply(galaxy,empire,target,current.Revision,classId,[new(source.DesignId,target.DesignId,true)],facts,out _,out var stale)&&stale!.Contains("changed",StringComparison.OrdinalIgnoreCase),"native source changes invalidate an older modal snapshot");
        source.UpgradeDesignId=arrow;
        string prefix="construction."+empire.EmpireId;string head=getSaved(galaxy,Owner,prefix+".head");
        string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
        rejectedKey=prefix+"."+bank+".0";
        Require(!ConstructionCommands.TryApply(galaxy,empire,target,current.Revision,classId,[new(source.DesignId,target.DesignId,true)],Facts(galaxy,empire),out _,out _),"Matrix chunk rejection aborts full source group");
        rejectedKey=null;
        Require(source.UpgradeDesignId==arrow&&!Read(Storage(galaxy,empire.EmpireId))!.DesignClasses.ContainsKey(source.DesignId),"failed source group leaves both native arrow and class unmodified");
        Require(registry.TryGet(empire,out current,out _)&&Serialized(current)==before,"verified Matrix rollback keeps registry usable");
        Require(ConstructionCommands.TryApply(galaxy,empire,target,current.Revision,classId,[new(source.DesignId,target.DesignId,true)],Facts(galaxy,empire),out _,out _),"retry after recovered Matrix refusal succeeds");
    }
}
