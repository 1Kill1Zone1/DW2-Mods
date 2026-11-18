using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using UiChecks;

internal static partial class Checks
{
    private static void ManualOrderDomain()
    {
        var state=new ConstructionSnapshot();var order=new ManualRetrofitOrder(10,20,new string('A',64),0,1,2,42);state.ManualRetrofits[31]=order;state.Validate();
        Require(state.Clone().ManualRetrofits[31]==order,"schema4 clone retains individual order");
        foreach(var invalid in new[]{order with{PreviousControl=int.MaxValue},order with{YardType=999},order with{TargetFingerprint="bad"},order with{TargetDesignId=10},order with{AssignedDate=-1}})
        {state.ManualRetrofits[31]=invalid;ExpectFailure(state.Validate,"malformed schema4 individual order fails closed");}
        state.ManualRetrofits[31]=order;state.Version=3;ExpectFailure(state.Validate,"legacy schema cannot smuggle new individual pins");
    }
    private static int selectedAssignments,fleetAssignments;
    private static readonly List<float> selectedNativeFees=[];
    private static void CountNativeSelectedFee(float amount,ExpenseType type){if((int)type is 6 or 18)selectedNativeFees.Add(amount);}
    private static void CountSelectedAssignment()=>selectedAssignments++;
    private static void RejectFleetAssignment(){fleetAssignments++;throw new InvalidOperationException("Selected retrofit attempted whole-fleet dispatch");}
    private static byte[] ShipBytes(Ship ship)
    {using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);ship.WriteToStream(writer);return stream.ToArray();}
    private static void SelectedRetrofitChecks(Galaxy galaxy,Empire empire,Design source,Design target,Func<Galaxy,Galaxy> roundtrip,string root)
    {
        Console.WriteLine("Native checks: selected 5 of 31 cross-Series retrofit.");
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var original,out _),"selected retrofit has valid series state");
        foreach(var candidate in galaxy.Ships.Where(s=>s.EmpireId==empire.EmpireId&&!s.IsBase&&!s.IsDestroyed))
        {candidate.SetGalaxy(galaxy);candidate.UpdateBonuses();candidate.RegenerateSummary();}
        var position=galaxy.Ships.First(s=>s.EmpireId==empire.EmpireId&&!s.IsBase&&!s.IsDestroyed&&s.GetLocation(galaxy)!=null);
        var basis=new Ship(galaxy,source,"Selected native escort basis",empire,position.GetLocation(galaxy)!,position.GalaxyX,position.GalaxyY,true,null!,false);
        basis.SetGalaxy(galaxy);basis.UpdateBonuses();basis.RegenerateSummary();
        Require(!basis.IsUnderConstructionOrRepair&&basis.Role==ShipRole.Escort&&basis.Summary.TopSpeed>0,"native fully built escort constructor supplies actual mobile components");
        string graph=Serialized(original);var policies=empire.Designs.ToDictionary(d=>d.DesignId,d=>(d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit));
        var fleet=new Fleet((short)(empire.Fleets.Select(f=>(int)f.FleetId).DefaultIfEmpty(-1).Max()+1),-1,empire.EmpireId,(FleetRole)0,galaxy);
        empire.Fleets.Add(fleet);
        var ships=new List<Ship>();int next=galaxy.Ships.Max(s=>s.ShipId)+100;
        for(int i=0;i<31;i++)
        {
            var ship=basis.Clone(galaxy);ship.ShipId=next++;ship.Name="Selected fixture "+i;ship.SetGalaxy(galaxy);ship.Design=source;ship.Role=source.GetRole(galaxy);
            ship.Mission=null!;ship.NextMissions=new();ship.ResourceReservations=new();ship.Parent=default;ship.ParentIsDockedAt=false;ship.ParentIsOnboard=false;
            ship.HyperDriveCountdown=0;ship.HyperdriveCharge=0;ship.RetrofitConstructionSystem=null!;
            ship.MissionAssignmentType=ShipMissionAssignmentType.FullyAutomate;ship.Fleet=fleet;
            galaxy.Ships.Add(ship);empire.Ships.Add(ship);fleet.Ships.Add(ship);ship.UpdateBonuses();ship.RegenerateSummary();ships.Add(ship);
        }
        Require(fleet.Ships.Count==31,"fixture has exactly 31 native fleet members");
        Require(SelectedRetrofitIntegration.Selection(fleet).Ships.Length==31&&!SelectedRetrofitIntegration.Selection(fleet).Preselect,"whole fleet entry has no implicit checks");
        var explicitShips=new ShipList();explicitShips.AddRange(ships.Take(5).ToArray());
        Require(SelectedRetrofitIntegration.Selection(explicitShips).Preselect&&SelectedRetrofitIntegration.Selection(explicitShips).Ships.Length==5,"explicit selection keeps exactly five ships");
        var priorContent=Galaxy.Game.Content;using var nativeHost=new NativeUi(galaxy);using var ruler=new SelectedRulerFixture();
        AccessTools.Field(typeof(Stride.Games.GameBase),"<Content>k__BackingField").SetValue(nativeHost.Host,priorContent);
        string? reason=SelectedRetrofit.Reason(galaxy,empire,ships[0],target);
        Console.WriteLine("SELECTED PREFLIGHT: "+(reason??"eligible"));
        if(reason!=null)try{_=GameTask.GenerateTaskRetrofitShip(galaxy,empire,ships[0],true,true);}catch(Exception ex){Console.WriteLine("SELECTED NATIVE TASK STACK: "+ex);}
        Require(reason==null,"actual native cross-Series compatibility succeeds before intent: "+reason);
        var before=ships.ToDictionary(s=>s.ShipId,ShipBytes);
        var monitor=new Harmony("acm.check.selected-missions");
        foreach(var method in AccessTools.GetDeclaredMethods(typeof(Fleet)).Where(m=>m.Name=="AssignMissionRetrofit"))monitor.Patch(method,prefix:new HarmonyMethod(typeof(Checks),nameof(RejectFleetAssignment)));
        monitor.Patch(AccessTools.Method(typeof(Ship),"AssignMissionRetrofit"),prefix:new HarmonyMethod(typeof(Checks),nameof(CountSelectedAssignment)));
        monitor.Patch(AccessTools.Method(typeof(Economy),"Expense"),prefix:new HarmonyMethod(typeof(Checks),nameof(CountNativeSelectedFee)));
        selectedAssignments=fleetAssignments=0;selectedNativeFees.Clear();
        try
        {
            var result=SelectedRetrofit.Send(galaxy,empire,ships.Take(5),target,SelectedRetrofit.Fingerprint(target));
            Console.WriteLine(string.Join("\n",result));
            Require(selectedAssignments==5&&fleetAssignments==0,"five actual native ship calls and zero fleet calls");
            Require(result.All(s=>s.Contains("Order issued")),"all five native exact orders accepted");
            float nativeCost=target.CalculateRetrofitCost(galaxy,empire,source);
            Require(selectedNativeFees.Count==5&&selectedNativeFees.All(f=>Math.Abs(f-nativeCost)<.01f),"all five accepted native commands charge exactly their native estimated fee once");
            Console.WriteLine("NATIVE RETROFIT FEE: "+nativeCost);
            foreach(var ship in ships.Take(5))Require(ship.Mission?.Type==ShipMissionType.Retrofit&&ship.Mission.BuildDesignId==target.DesignId&&ship.Mission.IsManuallyAssigned&&ReferenceEquals(ship.Fleet,fleet),"selected native mission and fleet retained "+ship.ShipId);
            foreach(var ship in ships.Skip(5))Require(ShipBytes(ship).SequenceEqual(before[ship.ShipId]),"unselected ship native serialization unchanged "+ship.ShipId);
            Require(registry.TryGet(empire,out var live,out _)&&live.ManualRetrofits.Count==5,"five individual pins only");
            var graphOnly=live.Clone();graphOnly.ManualRetrofits.Clear();graphOnly.Revision=original.Revision;
            Require(Serialized(graphOnly)==graph,"source/target Series mappings and policies unchanged");
            Require(empire.Designs.All(d=>policies[d.DesignId]==(d.Obsolete,d.UpgradeDesignId,d.AllowAutoRetrofit)),"all native project flags and arrows unchanged");
            var retry=SelectedRetrofit.Send(galaxy,empire,ships.Take(5),target,SelectedRetrofit.Fingerprint(target));
            Require(selectedAssignments==5&&selectedNativeFees.Count==5&&retry.All(s=>s.Contains("UNCHANGED")),"retry cannot duplicate a mission or reserve twice");
            var partial=ships.Skip(5).Take(5).ToArray();partial[3].IsBase=true;partial[4].Design=target;
            byte[] skipped1=ShipBytes(partial[3]),skipped2=ShipBytes(partial[4]);
            var partialResult=SelectedRetrofit.Send(galaxy,empire,partial,target,SelectedRetrofit.Fingerprint(target));
            Console.WriteLine(string.Join("\n",partialResult));
            Require(selectedAssignments==8&&selectedNativeFees.Count==8&&partialResult.Count(s=>s.Contains("Order issued"))==3&&partialResult.Count(s=>s.Contains("UNCHANGED"))==2,"partial five sends precisely three actual native missions");
            Require(ShipBytes(partial[3]).SequenceEqual(skipped1)&&ShipBytes(partial[4]).SequenceEqual(skipped2),"two excluded native ships remain exactly unchanged");
            Require(fleet.Ships.Count==31&&ships.All(s=>ReferenceEquals(s.Fleet,fleet)),"all 31 fleet memberships preserved");
            SelectedRetrofitPointerChecks(galaxy,empire,fleet,ships);
            partial[3].IsBase=false;partial[4].Design=source;
            SelectedRetrofitLifecycleChecks(galaxy,empire,source,target,ships,roundtrip);
            SelectedRetrofitRollbackChecks(galaxy,empire,target,ships,root);
            SelectedRetrofitYardChecks(galaxy,empire,target,ships,roundtrip,root);
            SelectedRetrofitConfirmationChecks(galaxy,empire,source,target,ships);
            MixedRetrofitChecks(galaxy,empire,source,target,ships,roundtrip);
            Console.WriteLine("PASS SELECTED: individual native retrofit, selection, persistence, rollback and yard acceptance");
        }
        finally{monitor.UnpatchAll(monitor.Id);NativeWindow.Active?.Close();}
    }
    private static void SelectedRetrofitPointerChecks(Galaxy galaxy,Empire empire,Fleet fleet,List<Ship> ships)
    {
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        var window=new SelectedRetrofitWindow(galaxy,empire,ships,false);
        Require(window.Selected.Count==0,"fleet dialog opens with zero checked ships");
        var panel=window.Panel;
        void Click(DWControl control)
        {
            var point=NativeUi.Center(control);var root=control.Ancestors.OfType<DWPanel>().Last();var hit=root.CheckHoveredControl(null!,point);
            if(!ReferenceEquals(hit,control))Console.WriteLine($"POINTER expected={control.Name} actual={hit?.Name}/{hit?.GetType().Name} point={point} rootRect={root.CalculateAbsoluteRectangle()} rect={control.CalculateAbsoluteRectangle()} visible={control.Visible} checkVisible={control.CheckVisible()} opacity={control.Opacity} post={control.PostScene} layer={control.Layer} enabled={(control is DWButton b?b.Enabled:true)} modals={string.Join(",",UserInterfaceController.ModalOverlayLayers)}");
            Require(ReferenceEquals(hit,control),"native pointer reaches "+control.Name+" actual="+hit?.Name);
            UserInterfaceController.HoveredControl=hit!;UserInterfaceController.DoMouseDown(new GameSettings(),point,true,empire,false,false);
            UserInterfaceController.DoMouseUp(new GameSettings(),point,true,empire,false,false,.1f);
        }
        Click(panel.Controls.Single(c=>c.Name=="ACM.RetrofitCancel"));
        Require(NativeWindow.Active==null,"native Cancel closes without dispatch");
        var explicitWindow=new SelectedRetrofitWindow(galaxy,empire,ships.Take(5),true);
        Require(explicitWindow.Selected.Count==5,"explicit native dialog prechecks exactly five");
        var search=(DWTextBox)explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSearch");
        search.Text="no matching selected retrofit project";Click(explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSearchApply"));
        Require(((ScrollableList)explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitShips")).Items.Count==0&&explicitWindow.Selected.Count==5,"native table search with no matches retains all five ship checks");
        search.Text="";Click(explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSearchApply"));
        Click(explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSeries"));
        Require(NativeWindow.Active is SeriesChoiceWindow,"native Series filter opens read-only choices");
        Click(NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.Choice.0"));
        Require(ReferenceEquals(NativeWindow.Active,explicitWindow)&&explicitWindow.Selected.Count==5,"choosing All series returns with ship checks intact");
        Click(explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitClear"));
        Require(explicitWindow.Selected.Count==0,"native clear checks makes empty selection");
        Click(explicitWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitCancel"));
        Require(fleetAssignments==0&&selectedAssignments==8,"opening and cancelling native dialogs sends no order");
        SelectedRetrofitEntryChecks(galaxy,empire,fleet,ships[10],ui,Click);
    }
}
