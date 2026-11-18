using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;

namespace AdvancedConstructionMod;

internal static class SeriesAutomation
{
    private static readonly FieldInfo EmpireGalaxy=AccessTools.Field(typeof(Empire),"_Galaxy");
    private static readonly ConditionalWeakTable<Galaxy,Dictionary<string,string>> Reasons=new();
    [ThreadStatic] private static Generation? generating;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Empire),"CreateShipDesigns",[typeof(long),typeof(bool)]),
            transpiler:new HarmonyMethod(typeof(SeriesAutomation),nameof(GuardNativeHull)),
            prefix:new HarmonyMethod(typeof(SeriesAutomation),nameof(BeforeGenerate)),finalizer:new HarmonyMethod(typeof(SeriesAutomation),nameof(FinishGenerate)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(DesignsDialog),"DesignsListItemClicked"),prefix:new HarmonyMethod(typeof(SeriesAutomation),nameof(BeforePolicyClick)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Design),"DistantWorlds.Types.IListDrawable.DrawItem"),transpiler:new HarmonyMethod(typeof(SeriesAutomation),nameof(DrawPolicy)));
    }

    internal static bool FillDefaults(Galaxy galaxy,Empire empire,ConstructionSnapshot state)
    {
        bool changed=false;
        foreach(var design in empire.Designs.OrderBy(d=>d.Obsolete).ThenByDescending(d=>d.StarDate).ThenByDescending(d=>d.DesignId))
        {
            if(!state.DesignClasses.TryGetValue(design.DesignId,out string? series))continue;
            var hull=design.GetShipHull(galaxy);if(hull==null)continue;
            string key=ConstructionSnapshot.TargetKey(series,(int)hull.Role);
            if(state.AutoUpgrade.ContainsKey(key))continue;
            state.AutoUpgrade[key]=NativePolicy(empire.Policy,hull.Role,hull.Level);changed=true;
        }
        if(changed)state.Version=ConstructionSnapshot.CurrentVersion;
        return changed;
    }

    private static bool NativePolicy(EmpirePolicy policy,ShipRole role,byte level)
        => policy.GetShipDesignAutomationByHull(role,level);

    internal static bool EnsureModes(Galaxy galaxy,Empire empire,out ConstructionSnapshot state,out string? error)
    {
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!registry.TryGet(empire,out state,out error))return false;
            var next=state.Clone();if(!FillDefaults(galaxy,empire,next))return true;
            next.Revision=checked(state.Revision+1);
            if(!registry.TryCommit(empire,next,out error))return false;
            state=next;return true;
        }
    }

    internal static bool Mode(Galaxy galaxy,Empire empire,Design design,ConstructionSnapshot state)
    {
        var hull=design.GetShipHull(galaxy);if(hull==null)return false;
        return state.DesignClasses.TryGetValue(design.DesignId,out string? series)&&state.AutoUpgrade.TryGetValue(ConstructionSnapshot.TargetKey(series,(int)hull.Role),out bool enabled)
            ?enabled:NativePolicy(empire.Policy,hull.Role,hull.Level);
    }

    internal static bool SetMode(Galaxy galaxy,Empire empire,Design design,bool enabled,out string? error)
    {
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            var saved=empire.Designs.GetById(design.DesignId);
            if(saved==null||!ReferenceEquals(galaxy.Designs.GetById(design.DesignId),saved)||design.EmpireId!=empire.EmpireId||saved.EmpireId!=empire.EmpireId)
            {error="This project is no longer current.";return false;}
            design=saved;
            if(!EnsureModes(galaxy,empire,out var state,out error))return false;
            if(!state.DesignClasses.TryGetValue(design.DesignId,out var series)){error="Choose a series first.";return false;}
            var next=state.Clone();next.Version=ConstructionSnapshot.CurrentVersion;
            next.AutoUpgrade[ConstructionSnapshot.TargetKey(series,(int)design.GetRole(galaxy))]=enabled;next.Revision=checked(state.Revision+1);
            return registry.TryCommit(empire,next,out error);
        }
    }

    private static bool BeforePolicyClick(DesignsDialog __instance,IListDrawable __0,string __1)
    {
        if(!string.Equals(__1,"AutoDesignRole",StringComparison.OrdinalIgnoreCase)||__0 is not Design design)return true;
        var galaxy=__instance.Galaxy;var empire=__instance.Empire;
        if(galaxy==null||empire==null||!empire.IsPlayer||design.EmpireId!=empire.EmpireId)return true;
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!registry.TryGet(empire,out var state,out string? error)){_ = new PolicyError(error!);return false;}
            if(!state.DesignClasses.ContainsKey(design.DesignId))return true;
            if(!SetMode(galaxy,empire,design,!Mode(galaxy,empire,design,state),out error))_ =new PolicyError(error!);
            return false;
        }
    }

    private static IEnumerable<CodeInstruction> DrawPolicy(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var getter=AccessTools.Method(typeof(EmpirePolicy),"GetShipDesignAutomationByHull");int replaced=0;
        int Argument(Type type)=>Array.FindIndex(__originalMethod.GetParameters(),p=>p.ParameterType==type)+1;
        foreach(var instruction in instructions)
        {
            if(!instruction.Calls(getter)){yield return instruction;continue;}
            var first=new CodeInstruction(OpCodes.Ldarg_0);first.labels.AddRange(instruction.labels);instruction.labels.Clear();yield return first;
            yield return new(OpCodes.Ldarg,Argument(typeof(Galaxy)));yield return new(OpCodes.Ldarg,Argument(typeof(Empire)));
            yield return new(OpCodes.Call,AccessTools.Method(typeof(SeriesAutomation),nameof(DisplayPolicy)));replaced++;
        }
        if(replaced!=1)throw new InvalidOperationException("Native Hull Upgrade display no longer matches the supported game build.");
    }

    private static bool DisplayPolicy(EmpirePolicy policy,ShipRole role,byte level,Design design,Galaxy galaxy,Empire empire)
    {
        if(empire?.IsPlayer==true&&design.EmpireId==empire.EmpireId)
        {
            if(!ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var state,out _))return false;
            if(state.DesignClasses.ContainsKey(design.DesignId))return Mode(galaxy,empire,design,state);
        }
        return policy.GetShipDesignAutomationByHull(role,level);
    }

    private sealed record Generation(Galaxy Galaxy, Empire Empire, Generation? Previous)
    {
        internal readonly HashSet<short> ProtectedHulls = [];
        internal bool Ready;
    }

    private static void BeforeGenerate(Empire __instance, out Generation? __state)
    {
        __state = null;
        if (!__instance.IsPlayer || EmpireGalaxy.GetValue(__instance) is not Galaxy galaxy) return;
        generating = __state = new Generation(galaxy, __instance, generating);
        if (!EnsureModes(galaxy, __instance, out var state, out var error))
        {
            Reason(galaxy, "global", error!);
            return;
        }
        foreach (var design in __instance.Designs)
            if (!design.Obsolete && state.DesignClasses.ContainsKey(design.DesignId))
                __state.ProtectedHulls.Add(design.HullId);
        __state.Ready = true;
    }

    // Only the stock single-design-per-hull loop can retire unrelated designs together.
    // Keep the public policy getter available to other generators, even inside this call.
    private static IEnumerable<CodeInstruction> GuardNativeHull(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var getter = AccessTools.Method(typeof(EmpirePolicy), nameof(EmpirePolicy.GetShipDesignAutomationByHull));
        int index = code.FindIndex(i => i.Calls(getter));
        if (index < 2 || code.Count(i => i.Calls(getter)) != 1 ||
            !code[index-1].LoadsField(AccessTools.Field(typeof(ShipHull), nameof(ShipHull.Level))) ||
            !code[index-2].IsLdloc())
            throw new InvalidOperationException("Unexpected native hull automation lookup.");
        code.InsertRange(index + 1, [code[index-2].Clone(), new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SeriesAutomation), nameof(NativeHullAllowed)))]);
        return code;
    }

    private static bool NativeHullAllowed(bool allowed, ShipHull hull, Empire empire) =>
        allowed && (generating is not { } run || !ReferenceEquals(run.Empire, empire) ||
                    (run.Ready && !run.ProtectedHulls.Contains(hull.ShipHullId)));

    private static Exception? FinishGenerate(Exception? __exception, Generation? __state)
    {
        if (__state == null) return __exception;
        generating = __state.Previous;
        try
        {
            if (__exception == null && __state.Ready) UpgradeAll(__state.Galaxy, __state.Empire);
        }
        catch (Exception ex) { Reason(__state.Galaxy, "global", "Series auto-upgrade paused: " + ex.Message); }
        return __exception;
    }

    internal static void UpgradeAll(Galaxy galaxy,Empire empire)
    {
        if(!empire.IsPlayer||(int)empire.Policy.GetAutomationLevel(AutomationCategory.ShipDesigns)!=0)return;
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        lock(registry.Gate)
        {
            if(!EnsureModes(galaxy,empire,out var initial,out var error)){Reason(galaxy,"global",error!);return;}
            foreach(string key in initial.AutoUpgrade.Where(p=>p.Value).Select(p=>p.Key).ToArray())
            {
                if(!registry.TryGet(empire,out var state,out error)){Reason(galaxy,key,error!);continue;}
                string series=key[..32];int role=int.Parse(key[33..]);
                var source=empire.Designs.Where(d=>!d.Obsolete&&(int)d.GetRole(galaxy)==role&&state.DesignClasses.GetValueOrDefault(d.DesignId)==series)
                    .OrderByDescending(d=>d.StarDate).ThenByDescending(d=>d.DesignId).FirstOrDefault();
                if(source==null){Reason(galaxy,key,"No active project in this series/role.");continue;}
                if(!TryUpgrade(galaxy,empire,source,state,out error))Reason(galaxy,key,error??"No component changes available.");
                else Reason(galaxy,key,"New component-upgrade version saved in this series.");
            }
        }
    }

    private static bool SameFamily(ComponentDefinition before,ComponentDefinition after)=>before.Category==after.Category&&
        (!string.IsNullOrEmpty(before.Family)?string.Equals(before.Family,after.Family,StringComparison.Ordinal):before.ComponentId==after.ComponentId);

    internal static bool PrepareUpgrade(Galaxy galaxy,Empire empire,Design source,out Design? draft,out string? error)
    {
        draft=null;if(!RetrofitRuntime.Fact(galaxy,empire,source).Buildable){error="Latest active project is not currently buildable.";return false;}
        var candidate=source.UpgradeComponents(galaxy,empire,false);
        if(candidate==null){error="No safe upgrade in the same hull.";return false;}
        return MapUpgrade(galaxy,empire,source,candidate,out draft,out error);
    }
    internal static bool MapUpgrade(Galaxy galaxy,Empire empire,Design source,Design candidate,out Design? draft,out string? error)
    {
        draft=null;if(candidate.HullId!=source.HullId||candidate.EmpireId!=source.EmpireId||source.EmpireId!=empire.EmpireId){error="No safe upgrade in the same own hull.";return false;}
        var before=source.Components.Where(c=>!c.IsEmpty).ToArray();var available=candidate.Components.Where(c=>!c.IsEmpty).ToList();
        if(before.Length!=available.Count){error="Native upgrade would change the component count. Upgrade manually.";return false;}
        var definitions=galaxy.Components.Where(d=>d!=null).ToDictionary(d=>d.ComponentId);
        var mapped=new ShipComponentList();bool changed=false;
        foreach(var component in before)
        {
            var definition=definitions[component.ComponentId];
            int index=available.FindIndex(c=>SameFamily(definition,definitions[c.ComponentId]));
            if(index<0){error="Native upgrade would change a component/weapon family. Upgrade manually.";return false;}
            var next=available[index];available.RemoveAt(index);
            // Native UpgradeComponents retains the old level when the component ID is unchanged.
            var researched=empire.Research.ResearchedComponents.Where(c=>c.ComponentId==next.ComponentId).OrderByDescending(c=>c.Level).FirstOrDefault();
            if(researched!=null&&researched.Level<definitions[next.ComponentId].Values.Count&&researched.Level>next.Level)
                next=new ShipComponent(next.ComponentId,ShipComponent.StatusFullHealth,next.ComponentBayId,researched.Level);
            if(next.ComponentId==component.ComponentId&&next.Level<component.Level){error="Native upgrade would downgrade a component.";return false;}
            mapped.Add(new ShipComponent(next.ComponentId,ShipComponent.StatusFullHealth,component.ComponentBayId,next.Level));
            changed|=next.ComponentId!=component.ComponentId||next.Level!=component.Level;
        }
        if(!changed){error="No component changes available.";return false;}
        candidate.Components=mapped;candidate.AllowAutoRetrofit=source.AllowAutoRetrofit;
        candidate.UpgradeDesignId=source.UpgradeDesignId==ConstructionGraph.Disabled?ConstructionGraph.Disabled:-2;
        candidate.Name=TextHelper.GenerateUpgradedDesignName(source.Name);candidate.Obsolete=false;
        if(!RetrofitRuntime.Fact(galaxy,empire,candidate).Buildable){error="Improved components do not fit the original layout. Upgrade manually.";return false;}
        draft=candidate;error=null;return true;
    }

    internal static bool TryUpgrade(Galaxy galaxy,Empire empire,Design source,ConstructionSnapshot current,out string? error)
    {
        lock(ConstructionCampaigns.Get(galaxy,empire).Gate)return CommitUpgrade(galaxy,empire,source,current,out error);
    }
    private static bool CommitUpgrade(Galaxy galaxy,Empire empire,Design source,ConstructionSnapshot current,out string? error)
    {
        if(source.Obsolete||source.EmpireId!=empire.EmpireId||!ReferenceEquals(empire.Designs.GetById(source.DesignId),source)||!ReferenceEquals(galaxy.Designs.GetById(source.DesignId),source)||!current.DesignClasses.ContainsKey(source.DesignId))
        {error="Only a current active own series project can be upgraded.";return false;}
        if(!PrepareUpgrade(galaxy,empire,source,out var draft,out error))return false;
        var registry=ConstructionCampaigns.Get(galaxy,empire);string series=current.DesignClasses[source.DesignId];
        if(!registry.TryGet(empire,out var live,out error)||live.Revision!=current.Revision){error="Series state changed; upgrade deferred.";return false;}
        draft!.DesignId=galaxy.Designs.GetNextId();
        if(galaxy.Designs.GetById(draft.DesignId)!=null||empire.Designs.GetById(draft.DesignId)!=null){error="Design identity changed; upgrade deferred.";return false;}
        var staged=current.Clone();staged.Version=ConstructionSnapshot.CurrentVersion;staged.DesignClasses[draft.DesignId]=series;
        string key=ConstructionSnapshot.TargetKey(series,(int)source.GetRole(galaxy));
        if(staged.Targets.GetValueOrDefault(key,ConstructionGraph.Disabled)==source.DesignId)staged.Targets[key]=draft.DesignId;
        bool oldObsolete=source.Obsolete;int oldArrow=source.UpgradeDesignId;bool committed=false;
        var oldArrows=empire.Designs.ToDictionary(d=>d.DesignId,d=>d.UpgradeDesignId);
        var oldFlags=empire.Designs.ToDictionary(d=>d.DesignId,d=>d.Obsolete);
        var oldRoles=empire.LatestDesignIdPerRole?.ToArray();var oldLargest=empire.LatestDesignIdForLargestHullPerRole?.ToArray();
        var oldHulls=empire.LatestDesignIdPerShipHull==null?null:new Dictionary<int,int>(empire.LatestDesignIdPerShipHull);
        try
        {
            galaxy.Designs.Add(draft);empire.Designs.Add(draft);
            var facts=empire.Designs.Select(d=>RetrofitRuntime.Fact(galaxy,empire,d)).ToArray();
            if(!ConstructionCommands.TryApply(galaxy,empire,draft,current.Revision,series,[],facts,out _,out error,staged))return false;
            committed=true;source.Obsolete=true;if(source.UpgradeDesignId!=ConstructionGraph.Disabled)source.UpgradeDesignId=-2;
            empire.ReviewLatestDesignsPerRole();empire.ReviewLatestDesignsPerShipHull();
            CoherentMissions.CopyAfterUpgrade(galaxy,source,draft);
            error=null;return true;
        }
        catch(Exception ex)
        {
            source.Obsolete=oldObsolete;source.UpgradeDesignId=oldArrow;
            bool published=committed;committed=false;
            if(published&&!registry.TryRestore(empire,current,out var recovery))throw new InvalidOperationException("Series rollback failed: "+recovery,ex);
            error="Automatic version was not saved: "+ex.Message;return false;
        }
        finally
        {
            if(!committed)
            {
                if(ReferenceEquals(empire.Designs.GetById(draft.DesignId),draft))empire.Designs.CheckRemove(draft);
                if(ReferenceEquals(galaxy.Designs.GetById(draft.DesignId),draft))galaxy.Designs.CheckRemove(draft);
                foreach(var design in empire.Designs){if(oldArrows.TryGetValue(design.DesignId,out var arrow))design.UpgradeDesignId=arrow;if(oldFlags.TryGetValue(design.DesignId,out var flag))design.Obsolete=flag;}
                empire.LatestDesignIdPerRole=oldRoles!;empire.LatestDesignIdForLargestHullPerRole=oldLargest!;empire.LatestDesignIdPerShipHull=oldHulls!;
            }
        }
    }

    private static void Reason(Galaxy galaxy,string key,string message)
    {var reasons=Reasons.GetOrCreateValue(galaxy);lock(reasons)reasons[key]=message;}
    internal static string Description(Galaxy galaxy,Empire empire,Design design)
    {
        if(!ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var state,out var error))return error??"Series data unavailable.";
        if(!state.DesignClasses.TryGetValue(design.DesignId,out var series))return "Native hull-design policy.";
        string key=ConstructionSnapshot.TargetKey(series,(int)design.GetRole(galaxy));
        var reasons=Reasons.GetOrCreateValue(galaxy);string status;
        lock(reasons)status=reasons.GetValueOrDefault(key)??reasons.GetValueOrDefault("global")??"Waiting for a normal design review after research.";
        if((int)empire.Policy.GetAutomationLevel(AutomationCategory.ShipDesigns)!=0)status="Global ship-design automation is not Automatic; this series will wait.";
        return "Series: "+state.Classes[series]+". This setting affects only this series and role. Components improve in the same hull; weapons and layout are preserved. No ship is sent to a yard.\n"+status;
    }

    private sealed class PolicyError:NativeWindow
    {internal PolicyError(string message):base("ACM.AutoError","SERIES AUTO UPGRADE",660,260){Status=Label("ACM.AutoErrorText",message,S(16),S(44),Panel.Size.X-S(32),Panel.Size.Y-S(104));Button("ACM.AutoErrorClose","Close",Panel.Size.X-S(130),Panel.Size.Y-S(42),S(114),Close);}}
}
