using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using UiChecks;

internal static partial class Checks
{
    private static float mixedUiScale=1;
    private static Design? captureFaultSource;
    private static bool reviewFault;
    private static void ThrowMixedCapture(Design design){if(ReferenceEquals(design,captureFaultSource))throw new IOException("Injected source loss during snapshot capture");}
    private static void ThrowMixedReview(NativeTextPager __instance)
    {if(reviewFault&&((DWLabel)AccessTools.Field(typeof(NativeTextPager),"label").GetValue(__instance)!).Name=="ACM.RetrofitConfirmation")throw new IOException("Injected confirmation construction failure");}
    private static bool MixedScale(object[] __args,ref float __result){__result=Convert.ToSingle(__args[0])*mixedUiScale;return false;}
    private static void MixedLayoutChecks(Galaxy galaxy,Empire empire,Ship ship,string series)
    {
        int width=UserInterfaceController.ScreenWidth,height=UserInterfaceController.ScreenHeight;
        var sizing=new Harmony("acm.check.mixed-layout");
        foreach(var method in AccessTools.GetDeclaredMethods(typeof(UserInterfaceHelper)).Where(m=>m.Name=="CalculateScaledValue"&&m.ReturnType==typeof(float)&&m.GetParameters().Length==1))
            sizing.Patch(method,prefix:new HarmonyMethod(typeof(Checks),nameof(MixedScale)));
        try
        {
            foreach(var size in new[]{(1024,768,1f),(1920,1080,1.25f),(1280,720,2f)})
            {
                UserInterfaceController.ScreenWidth=size.Item1;UserInterfaceController.ScreenHeight=size.Item2;mixedUiScale=size.Item3;
                var window=new SelectedRetrofitWindow(galaxy,empire,[ship],true);
                var list=window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitShips").CalculateAbsoluteRectangle();
                var details=window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitDetails").CalculateAbsoluteRectangle();
                var next=window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitDetails.Next").CalculateAbsoluteRectangle();
                var status=window.Status.CalculateAbsoluteRectangle();
                var review=window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitReview").CalculateAbsoluteRectangle();
                Require(list.Bottom<=details.Top&&next.Bottom<=status.Top&&status.Bottom<=review.Top,"table/details/status/buttons do not overlap at "+size);
                window.AssignSeries(series);var confirm=new SelectedRetrofitConfirmation(window,window.Plans.Values.ToArray());
                var text=confirm.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitConfirmation").CalculateAbsoluteRectangle();
                var page=confirm.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitConfirmation.Next").CalculateAbsoluteRectangle();
                Require(text.Bottom<=page.Top&&page.Bottom<=confirm.Status.CalculateAbsoluteRectangle().Top,"confirmation text/pager/status do not overlap at "+size);
                confirm.Close();window.Close();
            }
        }
        finally{sizing.UnpatchAll(sizing.Id);UserInterfaceController.ScreenWidth=width;UserInterfaceController.ScreenHeight=height;NativeWindow.Active?.Close();}
    }
    private static void MixedRetrofitChecks(Galaxy galaxy,Empire empire,Design source,Design priorTarget,List<Ship> ships,Func<Galaxy,Galaxy> roundtrip)
    {
        Console.WriteLine("Native checks: stable mixed-role Series assignment and exact mixed dispatch.");
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var state,out _),"mixed fixture reads current metadata");
        var staged=state.Clone();string escorts=ConstructionGraph.CreateClass(staged,"Mixed escort series"),frigates=ConstructionGraph.CreateClass(staged,"Mixed frigate series");
        int nextDesign=galaxy.Designs.Max(d=>d.DesignId)+100;
        Design Copy(Design basis,string name,string series,long date)
        {
            var copy=basis.Clone(galaxy);copy.DesignId=nextDesign++;copy.Name=name;copy.Obsolete=false;copy.StarDate=date;copy.UpgradeDesignId=ConstructionGraph.Disabled;
            galaxy.Designs.Add(copy);empire.Designs.Add(copy);staged.DesignClasses[copy.DesignId]=series;return copy;
        }
        long date=empire.Designs.Max(d=>d.StarDate)+100;
        var escortOld=Copy(source,"Mixed escort old pin",escorts,date);
        var escortTarget=Copy(source,"Mixed escort latest",escorts,date+1);
        var frigateBasis=empire.Designs.FirstOrDefault(d=>d.GetRole(galaxy)==ShipRole.Frigate&&RetrofitRuntime.Fact(galaxy,empire,d).Buildable);
        if(frigateBasis==null)
        {
            var hull=galaxy.ShipHulls.First(h=>h.Role==ShipRole.Frigate&&h.RaceId==empire.DominantRaceId);
            if(!empire.Research.ResearchedShipHulls.ContainsId(hull.ShipHullId))empire.Research.ResearchedShipHulls.Add(hull);
            frigateBasis=Design.GenerateNewDesign(galaxy,galaxy.StarDate,empire,empire.Research,hull,empire.Research.ResearchedComponents,empire.Research.ComponentBestValues,empire.Research.WeaponBestValues,empire.Research.SizeBestValues,empire.Policy.ComponentSelectionFactors,1f,null!);
            frigateBasis.DesignId=nextDesign++;frigateBasis.Name="Mixed native frigate source";frigateBasis.Obsolete=false;
            galaxy.Designs.Add(frigateBasis);empire.Designs.Add(frigateBasis);
        }
        var frigateTarget=Copy(frigateBasis,"Mixed frigate latest",frigates,date+2);
        staged.Targets[ConstructionSnapshot.TargetKey(escorts,(int)ShipRole.Escort)]=escortOld.DesignId;
        staged.Targets[ConstructionSnapshot.TargetKey(frigates,(int)ShipRole.Frigate)]=ConstructionGraph.Disabled;
        staged.Revision++;Require(registry.TryCommit(empire,staged,out _),"mixed fixture stages old pin and No target without changing runtime rules");
        var escort=ships[20];var position=escort.GetLocation(galaxy)!;
        var frigate=new Ship(galaxy,frigateBasis,"Mixed native frigate",empire,position,escort.GalaxyX,escort.GalaxyY,true,null!,false);
        frigate.ShipId=galaxy.Ships.Max(s=>s.ShipId)+100;frigate.SetGalaxy(galaxy);galaxy.Ships.Add(frigate);empire.Ships.Add(frigate);
        var fixtureFleet=escort.Fleet??throw new InvalidOperationException("Mixed escort fixture lost its native fleet");
        frigate.UpdateBonuses();frigate.RegenerateSummary();frigate.Fleet=fixtureFleet;fixtureFleet.Ships.Add(frigate);
        Require(frigate.Role==ShipRole.Frigate&&escort.Role==ShipRole.Escort&&RetrofitRuntime.Fact(galaxy,empire,frigateTarget).Buildable,"mixed fixture uses genuine researched native Frigate and Escort hulls");
        var escortPlan=SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts);
        var frigatePlan=SelectedRetrofitPlan.Resolve(galaxy,empire,frigate,frigates);
        Require(escortPlan.Target==escortTarget&&frigatePlan.Target==frigateTarget,"manual Series selection bypasses shared old pin and No target with latest per native role");
        Require(SelectedRetrofitPlan.Resolve(galaxy,empire,frigate,escorts).Problem!.Contains("native role Frigate"),"wrong-role Series gives an explicit incompatibility reason");
        var current=escort.Design;escort.Design=escortTarget;
        Require(SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts).Target==escortTarget&&SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts).Reason(galaxy,empire)=="Already using this project","latest equal to current reports already using instead of falling back to old pin");escort.Design=current;
        float speed=escort.Speed,hyper=escort.SummaryHyperDriveSpeed,range=escort.SummaryHyperDriveJumpRange;
        escort.SummaryHyperDriveSpeed=100000;escort.SummaryHyperDriveJumpRange=100000;escort.Speed=100000;
        Require(escort.IsHyperjumping(escort.GetDesign()),"native mixed fixture is actually hyperjumping");
        var jumping=SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts);
        Require(jumping.Target==escortTarget&&jumping.Reason(galaxy,empire)!.Contains("hyperjump"),"hyperjump retains latest draft but blocks actual command");
        byte[] jumpBefore=ShipBytes(escort);int assignments=selectedAssignments,fees=selectedNativeFees.Count;
        Require(!SelectedRetrofit.Send(galaxy,empire,[new SelectedRetrofitRequest(jumping)]).Single().Issued&&selectedAssignments==assignments&&ShipBytes(escort).SequenceEqual(jumpBefore),"mixed dispatch never sends or mutates a jumping ship");
        escort.Speed=speed;escort.SummaryHyperDriveSpeed=hyper;escort.SummaryHyperDriveJumpRange=range;
        Require(SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts).Target==escortTarget&&selectedAssignments==assignments,"finishing jump does not send a stored plan automatically");
        var stale=new SelectedRetrofitRequest(escortPlan);short oldHull=source.HullId;source.HullId=-1;
        Require(stale.Reason(galaxy,empire)!.Contains("Current project changed"),"source component/hull fingerprint is checked after review");source.HullId=oldHull;
        var newer=Copy(source,"Mixed newest after review",escorts,date+3);staged.Revision++;Require(registry.TryCommit(empire,staged,out _),"newer project added after confirmation in disposable fixture");
        var refused=SelectedRetrofit.Send(galaxy,empire,[stale]);
        Require(!refused.Single().Issued&&refused.Single().Text.Contains("Latest compatible project changed")&&selectedAssignments==assignments,"new latest after review requires fresh confirmation, never silent substitution");
        newer.Obsolete=true;
        Require(SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts).Target==escortTarget,"obsolete newer candidate is excluded");
        escortTarget.Obsolete=true;escortOld.Obsolete=true;
        Require(SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts).Target==null,"no active project does not guess another Series");escortTarget.Obsolete=false;escortOld.Obsolete=false;
        var researchedHull=empire.Research.ResearchedShipHulls.GetById(escortTarget.HullId);empire.Research.ResearchedShipHulls.Remove(researchedHull);
        try{Require(SelectedRetrofitPlan.Resolve(galaxy,empire,escort,escorts).Target==null,"unresearched candidate is excluded before assignment");}
        finally{empire.Research.ResearchedShipHulls.Add(researchedHull);}
        var changing=ships[25];
        ShipMission Move(Ship targetShip)=>new(galaxy,ShipMissionType.Move,targetShip,null!,null!,-1,-1,-1,-1,new CargoList(),null!,new ContractStubList(),new List<int>(),false);
        var originalMission=changing.Mission;var originalRevert=changing.RevertMission;var originalNext=changing.NextMissions;
        changing.Mission=Move(ships[23]);changing.RevertMission=Move(ships[23]);changing.NextMissions=new();changing.NextMissions.Add(Move(ships[23]));
        var changedPlan=SelectedRetrofitPlan.Resolve(galaxy,empire,changing,escorts);var changedRequest=new SelectedRetrofitRequest(changedPlan);
        Require(changedRequest.Reason(galaxy,empire)==null,"unchanged current/revert/queued native non-retrofit orders remain eligible before mutation");
        var otherTarget=Move(ships[24]).PrimaryTarget;
        foreach(var mission in new[]{changing.Mission,changing.RevertMission,changing.NextMissions[0]})
        {
            var originalTarget=mission.PrimaryTarget;mission.PrimaryTarget=otherTarget;
            Require(changedRequest.Reason(galaxy,empire)=="Ship orders or fleet changed after review","in-place current/revert/queued native mission target change invalidates confirmation");
            mission.PrimaryTarget=originalTarget;
        }
        changing.Mission=originalMission;changing.RevertMission=originalRevert;changing.NextMissions=originalNext;
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        MixedLayoutChecks(galaxy,empire,escort,escorts);
        void Click(DWControl control)
        {
            var root=control.Ancestors.OfType<DWPanel>().Last();var point=NativeUi.Center(control);
            Require(ReferenceEquals(root.CheckHoveredControl(null!,point),control),"mixed native pointer reaches "+control.Name);
            UserInterfaceController.HoveredControl=control;UserInterfaceController.DoMouseDown(new GameSettings(),point,true,empire,false,false);UserInterfaceController.DoMouseUp(new GameSettings(),point,true,empire,false,false,.1f);
        }
        var guarded=new SelectedRetrofitWindow(galaxy,empire,[changing,frigate],false);
        guarded.Selected.Add(changing.ShipId);guarded.AssignSeries(escorts);guarded.Selected.Clear();guarded.Selected.Add(frigate.ShipId);guarded.AssignSeries(frigates);
        var captureFault=new Harmony("acm.check.mixed-capture-failure");
        captureFault.Patch(AccessTools.Method(typeof(SelectedRetrofit),nameof(SelectedRetrofit.Fingerprint)),prefix:new HarmonyMethod(typeof(Checks),nameof(ThrowMixedCapture)));
        captureFaultSource=source;
        try
        {
            Click(guarded.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitReview"));
            var guardedSend=(DWButton)NativeWindow.Active!.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitSend");
            Require(guardedSend.Text=="Send 1 ships to 1 exact targets"&&guardedSend.Enabled,"source loss during capture skips only failing row without escaping native Review callback");
            Click(NativeWindow.Active.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitBack"));
            Require(guarded.Plans.Count==2&&selectedAssignments==assignments,"capture failure and Back keep all drafts without orders");
        }
        finally{captureFaultSource=null;captureFault.UnpatchAll(captureFault.Id);}
        // A native presentation exception after the new panel was installed must restore its parent.
        captureFault.Patch(AccessTools.Method(typeof(NativeTextPager),"SetText"),prefix:new HarmonyMethod(typeof(Checks),nameof(ThrowMixedReview)));reviewFault=true;
        try
        {
            Click(guarded.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitReview"));
            Require(ReferenceEquals(NativeWindow.Active,guarded)&&guarded.Status.Text.Contains("Review unavailable")&&guarded.Plans.Count==2,"unexpected Review presentation failure restores usable parent and retained plans");
        }
        finally{reviewFault=false;captureFault.UnpatchAll(captureFault.Id);guarded.Close();}
        var window=new SelectedRetrofitWindow(galaxy,empire,ships.Append(frigate),false);
        var list=(ScrollableList)window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitShips");
        var objects=list.Items.ToArray();var lower=objects[20];list.ScrollOffset=400;
        // Supply only the geometry normally populated by GPU Render. Native hit resolution,
        // field selection, mouse dispatch and selection callbacks remain real.
        var heights=(List<float>)AccessTools.Field(typeof(ScrollableList),"_ItemHeights").GetValue(list)!;
        for(int i=0;i<heights.Count;i++)heights[i]=24;
        AccessTools.Field(typeof(ScrollableList),"_TotalHeight").SetValue(list,24f*heights.Count);
        AccessTools.Field(typeof(ScrollableList),"ItemsRegion").SetValue(list,list.CalculateAbsoluteRectangle());
        var lowerRect=(RectangleF)AccessTools.Method(typeof(ScrollableList),"ResolveItemRectangle").Invoke(list,[lower])!;
        var lowerPoint=new Point((int)lowerRect.X+8,(int)lowerRect.Y+12);
        Require(ReferenceEquals(window.Panel.CheckHoveredControl(null!,lowerPoint),list)&&ReferenceEquals(AccessTools.Method(typeof(ScrollableList),"ResolveItemAtPoint").Invoke(list,[lowerPoint]),lower),"real native hit resolution reaches row twenty below initial viewport");
        UserInterfaceController.HoveredControl=list;UserInterfaceController.DoMouseDown(new GameSettings(),lowerPoint,true,empire,false,false);UserInterfaceController.DoMouseUp(new GameSettings(),lowerPoint,true,empire,false,false,.1f);
        Require(list.ScrollOffset==400&&list.Items.SequenceEqual(objects)&&list.SelectedItems.Contains(lower)&&window.Selected.SetEquals([escort.ShipId]),"lower-row checkbox preserves viewport identity and native highlight");
        void RowClick(int offset)
        {
            var point=new Point((int)lowerRect.X+offset,lowerPoint.Y);
            UserInterfaceController.HoveredControl=list;
            UserInterfaceController.DoMouseDown(new GameSettings(),point,true,empire,false,false);
            UserInterfaceController.DoMouseUp(new GameSettings(),point,true,empire,false,false,.1f);
        }
        RowClick((int)(list.Size.X*.3));Require(window.Selected.Count==0,"project cell unchecks the ship");
        RowClick((int)(list.Size.X*.55));Require(window.Selected.SetEquals([escort.ShipId]),"assigned project cell checks the ship");
        RowClick((int)(list.Size.X*.85));
        Require(NativeWindow.Active?.Panel.Name=="ACM.RetrofitShipDetails" && window.Selected.SetEquals([escort.ShipId]),"status opens ship details and preserves checks");
        Click(NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.ShipDetailsBack"));
        Require(ReferenceEquals(NativeWindow.Active,window)&&list.ScrollOffset==400,"Back restores list position");
        void Assign(string title)
        {
            Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitAssign"));
            var choice=NativeWindow.Active!.Panel.Controls.OfType<DWButton>().Single(c=>c.Visible&&c.Name.StartsWith("ACM.Choice.")&&c.Text.StartsWith(title+" |",StringComparison.Ordinal));Click(choice);
        }
        Assign("Mixed escort series");
        Require(window.Plans[escort.ShipId].Target==escortTarget&&list.ScrollOffset==400,"native Series button assigns escort without jumping the table");
        Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitClear"));
        Require(window.Selected.Count==0&&window.Plans.Count==1,"Clear checks retains prior group plan");
        var frigateRow=list.Items.Last();list.OnItemClicked(frigateRow,"Select",Vector2.Zero,true,false,null!,null!,[]);Assign("Mixed frigate series");
        Require(window.Plans.Count==2&&window.Plans[frigate.ShipId].Target==frigateTarget&&window.Plans[escort.ShipId].Target==escortTarget,"switching Escort to Frigate assigns a different exact target and keeps previous plan");
        var search=(DWTextBox)window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSearch");search.Text="no matching mixed ship";Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSearchApply"));
        Require(list.Items.Count==0&&window.Plans.Count==2&&window.Selected.Contains(frigate.ShipId),"empty search keeps hidden checks and assignments");
        search.Text="";Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSearchApply"));
        Require(list.Items.SequenceEqual(objects)&&window.Plans.Count==2,"clearing search restores exact stable row objects");
        Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitClearAssignment"));
        Require(window.Plans.Count==1&&window.Plans.ContainsKey(escort.ShipId),"Clear assignment affects only checked group");Assign("Mixed frigate series");
        Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitReview"));
        var send=(DWButton)NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSend");
        Require(send.Text=="Send 2 ships to 2 exact targets"&&send.Enabled,"mixed-role confirmation includes both assigned groups despite only Frigate being checked");
        Click(NativeWindow.Active.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitBack"));
        Require(window.Plans.Count==2&&list.ScrollOffset==400&&selectedAssignments==assignments,"Back retains plans and viewport without sending");
        var untouched=ships.Where(s=>s!=escort).ToDictionary(s=>s.ShipId,ShipBytes);
        Require(registry.TryGet(empire,out var beforeState,out _),"mixed send records pre-send graph");var graph=beforeState.Clone();graph.ManualRetrofits.Clear();graph.Revision=0;
        var nativeFlags=empire.Designs.ToDictionary(d=>d.DesignId,d=>(d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit));
        Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitReview"));send=(DWButton)NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSend");Click(send);
        Require(selectedAssignments==assignments+2&&selectedNativeFees.Count==fees+2&&window.Plans.Count==0,"native mixed confirmation sends and charges exactly two independent commands");
        Require(escort.Mission.BuildDesignId==escortTarget.DesignId&&frigate.Mission.BuildDesignId==frigateTarget.DesignId&&ReferenceEquals(escort.Fleet,frigate.Fleet),"mixed native missions retain different exact targets and fleet membership");
        NativeUi.Click(send);Require(selectedAssignments==assignments+2&&selectedNativeFees.Count==fees+2,"repeat mixed confirmation does not duplicate charge or dispatch");
        Require(ships.Where(s=>s!=escort).All(s=>ShipBytes(s).SequenceEqual(untouched[s.ShipId])),"all other ships remain byte-for-byte unchanged after mixed native send");
        Require(registry.TryGet(empire,out var afterState,out _),"mixed post-send graph available");var afterGraph=afterState.Clone();afterGraph.ManualRetrofits.Clear();afterGraph.Revision=0;
        Require(Serialized(afterGraph)==Serialized(graph)&&empire.Designs.All(d=>nativeFlags[d.DesignId]==(d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit)),"mixed send preserves shared old/No target policies, project membership and all native flags");
        var loaded=roundtrip(galaxy);var owner=loaded.Empires.GetById(empire.EmpireId);
        foreach(var pair in new[]{(escort.ShipId,escortTarget.DesignId),(frigate.ShipId,frigateTarget.DesignId)})
        {var ship=loaded.Ships.GetById(pair.ShipId);ship.SetGalaxy(loaded);Require(SelectedRetrofit.HasOrder(ship,owner,out var pin)&&pin.TargetDesignId==pair.DesignId&&ship.Mission.BuildDesignId==pair.DesignId,"mixed exact per-ship target survives native save/load "+pair.ShipId);}
        NativeWindow.Active?.Close();
        var a=ships[23];var b=ships[24];
        var c=new Ship(galaxy,frigateBasis,"Mixed partial native frigate",empire,position,escort.GalaxyX,escort.GalaxyY,true,null!,false);
        c.ShipId=galaxy.Ships.Max(s=>s.ShipId)+100;c.SetGalaxy(galaxy);galaxy.Ships.Add(c);empire.Ships.Add(c);c.UpdateBonuses();c.RegenerateSummary();
        var partial=new SelectedRetrofitWindow(galaxy,empire,[a,b,c],false);
        partial.Selected.UnionWith([a.ShipId,b.ShipId]);partial.AssignSeries(escorts);partial.Selected.Clear();partial.Selected.Add(c.ShipId);partial.AssignSeries(frigates);
        byte[] bBefore=ShipBytes(b);fees=selectedNativeFees.Count;
        var fault=new Harmony("acm.check.mixed-middle-failure");
        var clear=AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m=>m.Name=="ClearPreviousMissionRequirements"&&m.GetParameters().Length==5);
        fault.Patch(clear,postfix:new HarmonyMethod(typeof(Checks),nameof(ThrowAfterSelectedClear)){priority=Priority.Last});selectedFaultShip=b;selectedFaults=0;
        try
        {
            Click(partial.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitReview"));Click(NativeWindow.Active!.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitSend"));
            Require(selectedFaults==1&&a.Mission?.BuildDesignId==escortTarget.DesignId&&c.Mission?.BuildDesignId==frigateTarget.DesignId&&ShipBytes(b).SequenceEqual(bBefore),"middle native failure rolls back only B and preserves successful A then C of another role");
            Require(partial.Plans.Count==1&&partial.Plans.ContainsKey(b.ShipId)&&selectedNativeFees.Count==fees+2,"mixed partial results retain only failed plan and exactly two successful charges");
        }
        finally{selectedFaultShip=null;fault.UnpatchAll(fault.Id);}
        byte[] aAccepted=ShipBytes(a),cAccepted=ShipBytes(c);
        Click(partial.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitReview"));Click(NativeWindow.Active!.Panel.Controls.Single(control=>control.Name=="ACM.RetrofitSend"));
        Require(partial.Plans.Count==0&&selectedNativeFees.Count==fees+3&&ShipBytes(a).SequenceEqual(aAccepted)&&ShipBytes(c).SequenceEqual(cAccepted),"explicit partial retry sends only failed B without duplicating successful A or C");
        NativeWindow.Active?.Close();
        var zero=new SelectedRetrofitWindow(galaxy,empire,[ships[21]],true);ships[21].Design=escortTarget;zero.AssignSeries(escorts);
        Click(zero.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitReview"));var zeroSend=(DWButton)NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSend");
        var label=(DWLabel)NativeWindow.Active.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitConfirmation");
        Require(!zeroSend.Enabled&&zeroSend.Text=="No eligible ships"&&label.Text.Contains("No ships are ready")&&!label.Text.Contains("0 credits"),"zero-eligible confirmation gives actual reasons without misleading empty cost quote");
        Require(label.Size.Y<300,"short confirmation content is top aligned without giant blank above");
        Click(NativeWindow.Active.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitBack"));Require(zero.Plans.Count==1,"zero-eligible Back retains pending plan");
        Click(zero.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitCancel"));ships[21].Design=source;
    }
}
