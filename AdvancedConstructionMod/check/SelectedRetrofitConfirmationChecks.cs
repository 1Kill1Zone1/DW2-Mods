using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using UiChecks;

internal static partial class Checks
{
    private static void ThrowSelectedQuiescence(){Galaxy.AllowNewTasks=false;TaskHelper.ExitTasksEarly=true;throw new IOException("Injected native worker drain failure");}
    private static void SelectedRetrofitConfirmationChecks(Galaxy galaxy,Empire empire,Design source,Design target,List<Ship> ships)
    {
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        void Click(DWControl control)
        {
            var root=control.Ancestors.OfType<DWPanel>().Last();var point=NativeUi.Center(control);var hit=root.CheckHoveredControl(null!,point);
            Require(ReferenceEquals(hit,control),"confirmation native coordinate reaches "+control.Name);
            UserInterfaceController.HoveredControl=hit!;UserInterfaceController.DoMouseDown(new GameSettings(),point,true,empire,false,false);
            UserInterfaceController.DoMouseUp(new GameSettings(),point,true,empire,false,false,.1f);
        }
        void SelectTarget(SelectedRetrofitWindow window)
        {
            var registry=ConstructionCampaigns.Get(galaxy,empire);
            Require(registry.TryGet(empire,out var state,out _),"assignment choice reads native Series state");
            // Earlier paid-hull checks add a later disposable candidate. This scenario explicitly
            // restores its intended latest project; the separate mixed checks cover latest changes.
            target.StarDate=empire.Designs.Max(d=>d.StarDate)+1;
            Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitAssign"));
            string title=state.Classes[state.DesignClasses[target.DesignId]];
            var choice=NativeWindow.Active!.Panel.Controls.OfType<DWButton>().Single(c=>c.Visible&&c.Name.StartsWith("ACM.Choice.")&&c.Text.StartsWith(title+" |",StringComparison.Ordinal));
            Click(choice);
        }
        var chosen=ships.Skip(12).Take(5).ToArray();chosen[3].IsBase=true;chosen[4].Design=target;
        var before=chosen.ToDictionary(s=>s.ShipId,ShipBytes);int assigned=selectedAssignments,fees=selectedNativeFees.Count;
        var window=new SelectedRetrofitWindow(galaxy,empire,ships,false);
        var list=(ScrollableList)window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitShips");
        foreach(var ship in chosen)
        {
            var row=list.Items.Single(r=>ReferenceEquals(AccessTools.Property(r.GetType(),"Ship").GetValue(r),ship));
            list.OnItemClicked(row,"Select",Vector2.Zero,true,false,null!,null!,[]);
        }
        Require(window.Selected.Count==5&&selectedAssignments==assigned,"native row checkbox callbacks select five without issuing orders");
        SelectTarget(window);
        Require(selectedAssignments==assigned&&chosen.All(s=>ShipBytes(s).SequenceEqual(before[s.ShipId])),"choosing target row does not issue orders or mutate ships");
        Click(window.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitReview"));
        Require(NativeWindow.Active is SelectedRetrofitConfirmation,"review opens a distinct native confirmation");
        var send=(DWButton)NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSend");
        Require(send.Text=="Send 3 ships to 1 exact targets","native confirmation explicitly counts three eligible ships and one exact target");
        Click(send);
        Require(selectedAssignments==assigned+3&&selectedNativeFees.Count==fees+3,"native confirmation pointer sends and charges only three selected eligible ships");
        Require(ShipBytes(chosen[3]).SequenceEqual(before[chosen[3].ShipId])&&ShipBytes(chosen[4]).SequenceEqual(before[chosen[4].ShipId]),"confirmation leaves both excluded ships unchanged");
        NativeUi.Click(send);Require(selectedAssignments==assigned+3,"closed confirmation retry cannot duplicate accepted orders");
        Require(window.Plans.Count==2&&window.Plans.Keys.All(id=>id==chosen[3].ShipId||id==chosen[4].ShipId),"partial send removes successful drafts and retains both unissued plans");
        Click(NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitCancel"));chosen[3].IsBase=false;chosen[4].Design=source;
        var fresh=ships[17];var failureWindow=new SelectedRetrofitWindow(galaxy,empire,[fresh],true);SelectTarget(failureWindow);
        Click(failureWindow.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitReview"));var failureSend=NativeWindow.Active!.Panel.Controls.Single(c=>c.Name=="ACM.RetrofitSend");
        byte[] unchanged=ShipBytes(fresh);assigned=selectedAssignments;bool oldTasks=Galaxy.AllowNewTasks,oldExit=TaskHelper.ExitTasksEarly;
        var fault=new Harmony("acm.check.selected-quiescence-failure");fault.Patch(AccessTools.Method(typeof(Galaxy),"WaitAllTasksCompleted"),prefix:new HarmonyMethod(typeof(Checks),nameof(ThrowSelectedQuiescence)));
        try
        {
            Click(failureSend);
            Require(NativeWindow.Active is SelectedRetrofitWindow&&failureWindow.Status.Text.Contains("Command stopped"),"quiescence exception is reported in native UI without escaping handler");
            Require(selectedAssignments==assigned&&ShipBytes(fresh).SequenceEqual(unchanged)&&Galaxy.AllowNewTasks==oldTasks&&TaskHelper.ExitTasksEarly==oldExit,"quiescence failure preserves ship and scheduler flags");
        }
        finally{fault.UnpatchAll(fault.Id);NativeWindow.Active?.Close();}
        var stale=new Dictionary<int,int>{{fresh.ShipId,fresh.DesignId}};fresh.Design=target;
        var staleResult=SelectedRetrofit.Send(galaxy,empire,[fresh],target,SelectedRetrofit.Fingerprint(target),stale);
        Require(staleResult.Single().Contains("Current project changed")&&selectedAssignments==assigned,"confirmation source change fails closed without a native order");fresh.Design=source;
        var pinned=ships[7];Require(SelectedRetrofit.HasOrder(pinned,empire,out var pin),"pending order remains for missing-target test");
        galaxy.Designs.Remove(target);empire.Designs.Remove(target);
        try{Require(!SelectedRetrofit.Allows(pinned,empire,target,pin)&&SelectedRetrofit.Problem(galaxy,pinned.ShipId)!=null,"removed accepted project fails closed with visible reason instead of substitution");}
        finally{galaxy.Designs.Add(target);empire.Designs.Add(target);}
        Require(SelectedRetrofit.Allows(pinned,empire,target,pin),"restored exact project can resume the existing order without a new payment");
        var registry=ConstructionCampaigns.Get(galaxy,empire);var captured=ships[7];var oldOwner=captured.Empire;var oldMode=captured.MissionAssignmentType;
        captured.Empire=galaxy.Empires.First(e=>e.EmpireId!=empire.EmpireId);
        registry.PersistAll();Require(!SelectedRetrofit.HasOrder(captured,empire,out _)&&captured.MissionAssignmentType==oldMode,"save drops captured-ship pin without restoring its former owner's control");captured.Empire=oldOwner;
        var missing=ships[14];Require(SelectedRetrofit.HasOrder(missing,empire,out _),"missing-ship cleanup starts with an active pin");galaxy.Ships.Remove(missing);registry.PersistAll();galaxy.Ships.Add(missing);
        Require(!SelectedRetrofit.HasOrder(missing,empire,out _),"save removes missing-ship pin so a reused ID cannot inherit it");
    }
}
