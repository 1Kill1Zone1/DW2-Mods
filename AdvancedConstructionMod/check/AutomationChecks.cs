using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;

internal static partial class Checks
{
    private static void ThrowDesignCache()=>throw new IOException("Injected design-cache refresh failure");
    private static void AutomationChecks(Galaxy galaxy,Empire empire)
    {
        Console.WriteLine("Native checks: independent series component automation and protected template generation.");
        var registry=ConstructionCampaigns.Get(galaxy,empire);Require(registry.TryGet(empire,out var state,out _),"automation fixture reads metadata");
        var legacy=state.Clone();legacy.Version=2;legacy.AutoUpgrade.Clear();
        string pins=System.Text.Json.JsonSerializer.Serialize(legacy.Targets);
        Require(registry.TryRestore(empire,legacy,out _)&&SeriesAutomation.EnsureModes(galaxy,empire,out state,out _)&&state.Version==3,"schema2 campaign initializes schema3 modes once without dropping series");
        var representatives=empire.Designs.Where(d=>state.DesignClasses.ContainsKey(d.DesignId)).GroupBy(d=>ConstructionSnapshot.TargetKey(state.DesignClasses[d.DesignId],(int)d.GetRole(galaxy)))
            .Select(g=>g.OrderBy(d=>d.Obsolete).ThenByDescending(d=>d.StarDate).ThenByDescending(d=>d.DesignId).First()).ToArray();
        Require(System.Text.Json.JsonSerializer.Serialize(state.Targets)==pins&&representatives.All(d=>SeriesAutomation.Mode(galaxy,empire,d,state)==empire.Policy.GetShipDesignAutomationByHull(d.GetRole(galaxy),d.GetShipHull(galaxy).Level)),"existing modes inherit latest active native Auto/Manual and preserve pinned destinations");
        long initializedRevision=state.Revision;Require(SeriesAutomation.EnsureModes(galaxy,empire,out state,out _)&&state.Revision==initializedRevision,"mode initialization is idempotent");
        state=state.Clone();foreach(string key in state.AutoUpgrade.Keys.ToArray())state.AutoUpgrade[key]=false;
        var basis=empire.Designs.FirstOrDefault(d=>!d.Obsolete&&d.GetRole(galaxy)==ShipRole.Escort&&RetrofitRuntime.Fact(galaxy,empire,d).Buildable)?.Clone(galaxy);
        Require(basis!=null,"fixture has a buildable Escort for isolated research upgrade");
        var seeded=basis!.Components.ToArray();int seed=Array.FindIndex(seeded,c=>!c.IsEmpty&&galaxy.Components.First(d=>d.ComponentId==c.ComponentId).Values.Count>1);
        Require(seed>=0,"fixture component has a real improved level");
        var seedComponent=seeded[seed];empire.Research.ResearchedComponents.First(c=>c.ComponentId==seedComponent.ComponentId).Level=1;
        seeded[seed]=new ShipComponent(seedComponent.ComponentId,ShipComponent.StatusFullHealth,seedComponent.ComponentBayId,1);basis.Components.Clear();basis.Components.AddRange(seeded);
        var hull=basis!.GetShipHull(galaxy);string a=ConstructionGraph.CreateClass(state,"Auto branch"),b=ConstructionGraph.CreateClass(state,"Manual branch");
        int id=galaxy.Designs.Max(d=>d.DesignId)+20;
        Design Lowered(string series,string name)
        {
            id=Math.Max(id,galaxy.Designs.Max(d=>d.DesignId)+20);
            var d=basis.Clone(galaxy);d.DesignId=id++;d.Name=name;d.StarDate=galaxy.StarDate;d.Obsolete=false;d.UpgradeDesignId=-2;d.AllowAutoRetrofit=false;
            var components=d.Components.ToArray();int n=Array.FindIndex(components,c=>!c.IsEmpty&&c.Level>0);var c=components[n];
            components[n]=new ShipComponent(c.ComponentId,ShipComponent.StatusFullHealth,c.ComponentBayId,(byte)(c.Level-1));
            d.Components.Clear();d.Components.AddRange(components);galaxy.Designs.Add(d);empire.Designs.Add(d);return d;
        }
        var auto=Lowered(a,"Series auto fixture");var manual=Lowered(b,"Series manual fixture");
        state.DesignClasses[auto.DesignId]=a;state.DesignClasses[manual.DesignId]=b;
        string ak=ConstructionSnapshot.TargetKey(a,(int)auto.GetRole(galaxy)),bk=ConstructionSnapshot.TargetKey(b,(int)manual.GetRole(galaxy));
        state.AutoUpgrade[ak]=true;state.AutoUpgrade[bk]=false;state.Targets[ak]=auto.DesignId;state.Targets[bk]=manual.DesignId;
        Require(registry.TryRestore(empire,state,out _),"two independent modes and explicit targets prepared");
        bool prepared=SeriesAutomation.PrepareUpgrade(galaxy,empire,auto,out var safe,out var prepareError);
        Require(prepared,"real native component improvement passes same-hull/family guard: "+prepareError);
        Require(safe!.HullId==auto.HullId&&safe.Components.Count==auto.Components.Count&&safe.Components.Select(c=>c.ComponentBayId).SequenceEqual(auto.Components.Select(c=>c.ComponentBayId)),"prepared native upgrade retains exact hull, component count and bay order");
        var definitions=galaxy.Components.Where(c=>c!=null).ToDictionary(c=>c.ComponentId);
        Require(safe.Components.Zip(auto.Components).All(p=>definitions[p.First.ComponentId].Category==definitions[p.Second.ComponentId].Category&&
            (string.IsNullOrEmpty(definitions[p.Second.ComponentId].Family)?p.First.ComponentId==p.Second.ComponentId:definitions[p.First.ComponentId].Family==definitions[p.Second.ComponentId].Family)),"all improved components retain their native families/categories");
        var wrong=auto.Clone(galaxy);var wrongComponents=wrong.Components.ToArray();
        var replacement=galaxy.Components.First(c=>c!=null&&c.Category!=definitions[wrongComponents[0].ComponentId].Category);
        var original=wrongComponents[0];wrongComponents[0]=new ShipComponent(replacement.ComponentId,ShipComponent.StatusFullHealth,original.ComponentBayId,0);
        wrong.Components.Clear();wrong.Components.AddRange(wrongComponents);
        Require(!SeriesAutomation.MapUpgrade(galaxy,empire,auto,wrong,out _,out var bad)&&bad!.Contains("family"),"a new equipment category cannot replace an existing scanner/weapon during auto upgrade");
        Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==Serialized(state)&&!auto.Obsolete,"preparation and rejected layout leave metadata and source active");

        var nativeModes=empire.Research.ResearchedShipHulls.Select(h=>(h.Role,h.Level)).Distinct().ToDictionary(k=>k,k=>empire.Policy.GetShipDesignAutomationByHull(k.Role,k.Level));
        var globalMode=empire.Policy.GetAutomationLevel(AutomationCategory.ShipDesigns);
        foreach(var key in nativeModes.Keys)empire.Policy.SetShipDesignAutomationByHull(key.Role,key.Level,false);
        try
        {
            empire.Policy.SetAutomationLevel(AutomationCategory.ShipDesigns,(AutomationLevel)1);
            var pausedIds=empire.Designs.Select(d=>d.DesignId).ToHashSet();empire.CreateShipDesigns(galaxy.StarDate,true);
            Require(empire.Designs.Select(d=>d.DesignId).ToHashSet().SetEquals(pausedIds)&&!auto.Obsolete,"global non-Automatic design policy blocks series upgrades even during a forced native review");
            empire.Policy.SetAutomationLevel(AutomationCategory.ShipDesigns,(AutomationLevel)0);
            using(var metrics=new FontMetrics())using(var ui=new global::UiChecks.NativeUi(galaxy))
            {
                var dialog=new DesignsDialog();dialog.BindGalaxy(galaxy,empire);
                AccessTools.Method(typeof(DesignsDialog),"DesignsListItemClicked").Invoke(dialog,[manual,"AutoDesignRole",new Vector2(),true,false,null!,null!,new List<IListDrawable>()]);
                Require(registry.TryGet(empire,out var toggled,out _)&&SeriesAutomation.Mode(galaxy,empire,manual,toggled)&&SeriesAutomation.Mode(galaxy,empire,auto,toggled),"toggling Manual series does not toggle the other series");
                AccessTools.Method(typeof(DesignsDialog),"DesignsListItemClicked").Invoke(dialog,[manual,"AutoDesignRole",new Vector2(),true,false,null!,null!,new List<IListDrawable>()]);
            }
            Require(registry.TryGet(empire,out state,out _),"series settings remain readable before native review");
            var beforeIds=empire.Designs.Select(d=>d.DesignId).ToHashSet();
            empire.CreateShipDesigns(galaxy.StarDate,true);
            Require(registry.TryGet(empire,out state,out _),"native review commits the automated series");
            var next=empire.Designs.Where(d=>!beforeIds.Contains(d.DesignId)).ToArray();
            Require(next.Length==1&&state.DesignClasses[next[0].DesignId]==a&&auto.Obsolete&&!manual.Obsolete,"real native design review publishes exactly one classified Auto version and leaves Manual branch active");
            var upgraded=next[0];Require(state.Targets[ak]==upgraded.DesignId&&state.Targets[bk]==manual.DesignId,"only the upgraded explicit target advances");
            Require(!upgraded.AllowAutoRetrofit&&manual.UpgradeDesignId==-2,"automatic design creation preserves retrofit permission and other branch path");
            Require(upgraded.Components.Select(c=>c.ComponentBayId).SequenceEqual(auto.Components.Select(c=>c.ComponentBayId)),"published automatic version retains exact component bay layout");
            beforeIds=empire.Designs.Select(d=>d.DesignId).ToHashSet();long revision=state.Revision;
            empire.CreateShipDesigns(galaxy.StarDate,true);
            Require(empire.Designs.Select(d=>d.DesignId).ToHashSet().SetEquals(beforeIds)&&Read(Storage(galaxy,empire.EmpireId))!.Revision==revision,"repeated research review with no component changes creates no version or metadata revision");

            var low=Lowered(a,"Series fault fixture");
            Require(registry.TryGet(empire,out state,out _),"fault fixture reads latest state");state=state.Clone();state.DesignClasses[low.DesignId]=a;
            // An explicit target must remain in its own series; use the older active Auto source instead.
            auto.Obsolete=false;state.Targets[ak]=auto.DesignId;Require(registry.TryRestore(empire,state,out _),"older explicit pin prepared");
            low.StarDate=galaxy.StarDate+1;
            string before=Serialized(state);int count=empire.Designs.Count;
            var stale=state.Clone();stale.Revision--;
            Require(!SeriesAutomation.TryUpgrade(galaxy,empire,low,stale,out var staleError)&&staleError!.Contains("changed")&&empire.Designs.Count==count,"stale research review does not publish an automatic project");
            var carrier=empire.Ships.First(s=>!s.IsBase&&!s.IsDestroyed);var previousDesign=carrier.GetDesign();carrier.Design=low;
            var system=galaxy.Ships.First(s=>s?.ConstructionSystem!=null).ConstructionSystem;
            var wait=(ConstructionYardWait)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ConstructionYardWait));
            wait.ShipUnderConstructionId=carrier.ShipId;wait.DesignId=low.DesignId;wait.ConstructionType=ConstructionType.Retrofit;system.WaitQueue.Add(wait);
            try{Require(!SeriesAutomation.TryUpgrade(galaxy,empire,low,state,out var queueError)&&queueError!.Contains("queued"),"automatic version waits for an already queued retrofit");
                Require(empire.Designs.Count==count&&!low.Obsolete&&system.WaitQueue.Contains(wait)&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"queued-refit refusal preserves project, metadata and existing order");}
            finally{system.WaitQueue.Remove(wait);carrier.Design=previousDesign;}
            string prefix="construction."+empire.EmpireId,head=getSaved(galaxy,Owner,prefix+".head");
            string bank=System.Text.Json.JsonDocument.Parse(head).RootElement.GetProperty("Bank").GetString()=="a"?"b":"a";
            rejectedKey=prefix+"."+bank+".0";
            try{Require(!SeriesAutomation.TryUpgrade(galaxy,empire,low,state,out _),"Matrix failure aborts automatic publication");}finally{rejectedKey=null;}
            Require(empire.Designs.Count==count&&!low.Obsolete&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before,"failed automatic version leaves no partial project, source obsolescence or metadata change");
            var cacheBefore=empire.LatestDesignIdPerRole.ToArray();var flagsBefore=empire.Designs.ToDictionary(d=>d.DesignId,d=>(d.Obsolete,d.UpgradeDesignId));
            var cacheFault=new Harmony("acm.check.auto-cache-failure");cacheFault.Patch(AccessTools.Method(typeof(Empire),nameof(Empire.ReviewLatestDesignsPerShipHull)),prefix:new HarmonyMethod(typeof(Checks),nameof(ThrowDesignCache)));
            try{Require(!SeriesAutomation.TryUpgrade(galaxy,empire,low,state,out _),"failure after Matrix commit aborts automatic publication");}finally{cacheFault.UnpatchAll(cacheFault.Id);}
            Require(empire.Designs.Count==count&&Serialized(Read(Storage(galaxy,empire.EmpireId))!)==before&&empire.LatestDesignIdPerRole.SequenceEqual(cacheBefore)&&empire.Designs.All(d=>flagsBefore[d.DesignId]==(d.Obsolete,d.UpgradeDesignId)),"post-commit failure restores metadata, native flags/arrows and latest-design caches");
            Require(SeriesAutomation.TryUpgrade(galaxy,empire,low,state,out var retry),"retry after atomic refusal succeeds: "+retry);
            Require(Read(Storage(galaxy,empire.EmpireId))!.Targets[ak]==auto.DesignId&&!auto.Obsolete,"upgrading the latest active project preserves a different older pinned target");
            var disabled=Lowered(a,"Disabled series path fixture");Require(registry.TryGet(empire,out state,out _),"disabled target fixture reads state");state=state.Clone();state.DesignClasses[disabled.DesignId]=a;state.Targets[ak]=ConstructionGraph.Disabled;disabled.UpgradeDesignId=ConstructionGraph.Disabled;
            Require(registry.TryRestore(empire,state,out _)&&SeriesAutomation.TryUpgrade(galaxy,empire,disabled,state,out _),"component design update can preserve an intentionally disabled retrofit path");
            Require(Read(Storage(galaxy,empire.EmpireId))!.Targets[ak]==ConstructionGraph.Disabled&&empire.Designs.Last(d=>Read(Storage(galaxy,empire.EmpireId))!.DesignClasses.GetValueOrDefault(d.DesignId)==a).UpgradeDesignId==ConstructionGraph.Disabled,"No target and disabled source paths remain disabled after automatic creation");
            auto.Obsolete=true; // Keep the old pinned source deliberately obsolete for the final persistence test.
            Require(registry.TryGet(empire,out state,out _),"final automation state readable");
            Forget(galaxy);Require(ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var reloaded,out _)&&Serialized(reloaded)==Serialized(state),"independent modes and pins survive Matrix registry reload");
        }
        finally{foreach(var pair in nativeModes)empire.Policy.SetShipDesignAutomationByHull(pair.Key.Role,pair.Key.Level,pair.Value);empire.Policy.SetAutomationLevel(AutomationCategory.ShipDesigns,globalMode);}
    }
}
