using AdvancedConstructionMod;
using DistantWorlds.Types;
using HarmonyLib;

internal static partial class Checks
{
    private static Design PaidRetrofitTarget(Galaxy galaxy,Empire empire,Ship ship,Design seriesTarget)
    {
        var source=ship.GetDesign();var registry=ConstructionCampaigns.Get(galaxy,empire);
        foreach(var hull in galaxy.ShipHulls.Where(h=>h!=null&&h.Role==source.GetRole(galaxy)&&h.RaceId==empire.DominantRaceId&&h.ShipHullId!=source.HullId).OrderBy(h=>h.Size))
        {
            if(!empire.Research.ResearchedShipHulls.ContainsId(hull.ShipHullId))empire.Research.ResearchedShipHulls.Add(hull);
            var design=Design.GenerateNewDesign(galaxy,galaxy.StarDate,empire,empire.Research,hull,empire.Research.ResearchedComponents,empire.Research.ComponentBestValues,empire.Research.WeaponBestValues,empire.Research.SizeBestValues,empire.Policy.ComponentSelectionFactors,1f,null!);
            design.DesignId=galaxy.Designs.Max(d=>d.DesignId)+10;design.Name="Native paid alternate-hull retrofit";design.Obsolete=false;
            galaxy.Designs.Add(design);empire.Designs.Add(design);
            string? reason=SelectedRetrofit.Reason(galaxy,empire,ship,design);float cost=design.CalculateRetrofitCost(galaxy,empire,source);
            Console.WriteLine($"PAID TARGET hull={hull.ShipHullId} cost={cost} reason={reason}");
            if(reason==null&&cost>0)
            {
                Require(registry.TryGet(empire,out var state,out _),"paid target fixture reads existing Series");var next=state.Clone();next.DesignClasses[design.DesignId]=state.DesignClasses[seriesTarget.DesignId];next.Revision++;
                Require(registry.TryCommit(empire,next,out _),"native alternate hull target is classified only in disposable fixture setup");return design;
            }
            galaxy.Designs.Remove(design);empire.Designs.Remove(design);
        }
        throw new InvalidOperationException("No legal positive-cost native retrofit target available in fixture");
    }
    private static Ship? selectedFaultShip;
    private static bool selectedExpenseFault;
    private static byte[] EconomyBytes(Economy economy){using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes);economy.WriteToStream(writer);return bytes.ToArray();}
    private static void ThrowAfterSelectedExpense(ExpenseType type){if(selectedExpenseFault&&(int)type is 6 or 18)throw new IOException("Injected failure after actual native retrofit payment");}
    private static int selectedFaults;
    private static void ThrowAfterSelectedClear(Ship __instance)
    {if(ReferenceEquals(__instance,selectedFaultShip)){selectedFaults++;throw new IOException("Injected failure after real native mission cleanup");}}
    private static void SelectedRetrofitRollbackChecks(Galaxy galaxy,Empire empire,Design target,List<Ship> ships,string root)
    {
        var ship=ships[10];var enemy=galaxy.Ships.First(s=>s.EmpireId!=empire.EmpireId&&!s.IsDestroyed&&!s.IsBase);enemy.SetGalaxy(galaxy);
        ship.Mission=new ShipMission(galaxy,ShipMissionType.Attack,enemy,null!,null!,-1,-1,-1,-1,new CargoList(),null!,new ContractStubList(),new List<int>(),false);
        ship.CurrentTarget=enemy;enemy.BattleData.AddAttacker(ship);enemy.BattleData.AddPursuer(ship);
        ship.DockData.HaveReachedEntryPoint=true;ship.Cargo.Add(new Cargo(0,7,empire));
        Require(enemy.BattleData.AttackerShips.Contains(ship)&&enemy.BattleData.PursuerShips.Contains(ship),"rollback fixture has native attacker and pursuer registrations");
        byte[] before=ShipBytes(ship);var oldMission=ship.Mission;int attackers=enemy.BattleData.AttackerCount,pursuers=enemy.BattleData.PursuerCount;
        var yard=empire.GetFastestConstructionYardRetrofit(ship,target,true,null!,out _);var cargo=yard.GetCargo().First();float expected=(float)cargo.Amount+11;
        bool priorTasks=Galaxy.AllowNewTasks,priorExit=TaskHelper.ExitTasksEarly;Galaxy.AllowNewTasks=true;TaskHelper.ExitTasksEarly=false;
        var work=Task.Run(()=>{if(!SpinWait.SpinUntil(()=>TaskHelper.ExitTasksEarly,3000))throw new TimeoutException("Native quiescence never began");cargo.Amount=(DistantWorlds.Core.AtomicFloat)((float)cargo.Amount+11f);});
        var tasks=(List<Task>)AccessTools.Field(typeof(Galaxy),"Tasks").GetValue(null)!;
        lock(AccessTools.Field(typeof(Galaxy),"_TaskLock").GetValue(null)!)tasks.Add(work);
        var fault=new Harmony("acm.check.selected-after-clear-failure");
        var clear=AccessTools.GetDeclaredMethods(typeof(Ship)).Single(m=>m.Name=="ClearPreviousMissionRequirements"&&m.GetParameters().Length==5);
        fault.Patch(clear,postfix:new HarmonyMethod(typeof(Checks),nameof(ThrowAfterSelectedClear)){priority=Priority.Last});selectedFaultShip=ship;selectedFaults=0;
        try
        {
            var result=SelectedRetrofit.Send(galaxy,empire,[ship],target,SelectedRetrofit.Fingerprint(target));
            Console.WriteLine("ROLLBACK: "+string.Join("; ",result));
            Require(selectedFaults==1&&result.Single().Contains("UNCHANGED"),"native assignment reaches real cleanup then reports failure");
            Require(work.IsCompletedSuccessfully&&Math.Abs((float)cargo.Amount-expected)<.01f,"native worker cargo change drains before capture and survives rollback");
            Require(Galaxy.AllowNewTasks&&!TaskHelper.ExitTasksEarly,"command restores native scheduler flags after failed assignment");
            Require(ReferenceEquals(ship.Mission,oldMission)&&ShipBytes(ship).SequenceEqual(before),"failure restores exact native old order, cargo, mode and reservations");
            Require(ship.DockData.HaveReachedEntryPoint&&enemy.BattleData.AttackerCount==attackers&&enemy.BattleData.PursuerCount==pursuers&&enemy.BattleData.AttackerShips.Contains(ship)&&enemy.BattleData.PursuerShips.Contains(ship),"failure restores selected DockData and native attack target registrations");
            Require(!SelectedRetrofit.HasOrder(ship,empire,out _),"failed native command leaves no accepted pin");
        }
        finally{fault.UnpatchAll(fault.Id);selectedFaultShip=null;Galaxy.AllowNewTasks=priorTasks;TaskHelper.ExitTasksEarly=priorExit;}
        var paymentShip=ships[11];var paidTarget=PaidRetrofitTarget(galaxy,empire,paymentShip,target);byte[] priorShip=ShipBytes(paymentShip),priorEconomy=EconomyBytes(empire.Economy);
        var paidYard=empire.GetFastestConstructionYardRetrofit(paymentShip,paidTarget,true,null!,out _);var yardEconomy=paidYard.GetEmpire(galaxy)!.Economy;byte[] priorYardEconomy=EconomyBytes(yardEconomy);
        fault.Patch(AccessTools.Method(typeof(Economy),"Expense"),postfix:new HarmonyMethod(typeof(Checks),nameof(ThrowAfterSelectedExpense)){priority=Priority.Last});selectedExpenseFault=true;
        try
        {
            var refused=SelectedRetrofit.Send(galaxy,empire,[paymentShip],paidTarget,SelectedRetrofit.Fingerprint(paidTarget));
            Require(refused.Single().Contains("UNCHANGED")&&refused.Single().Contains("payment"),"failure after real native expense is reported without accepting an order");
            Require(ShipBytes(paymentShip).SequenceEqual(priorShip)&&EconomyBytes(empire.Economy).SequenceEqual(priorEconomy)&&EconomyBytes(yardEconomy).SequenceEqual(priorYardEconomy),"native money and complete binary ledgers of both owners roll back with the ship order");
            Require(!SelectedRetrofit.HasOrder(paymentShip,empire,out _),"payment exception leaves no individual target pin");
        }
        finally{selectedExpenseFault=false;fault.UnpatchAll(fault.Id);}
        float money=empire.Economy.Money,expense=empire.Economy.GetExpenseCurrentPeriod((ExpenseType)6);int fees=selectedNativeFees.Count;
        var paidShip=ships[18];var paidResult=SelectedRetrofit.Send(galaxy,empire,[paidShip],paidTarget,SelectedRetrofit.Fingerprint(paidTarget));
        float charged=empire.Economy.GetExpenseCurrentPeriod((ExpenseType)6)-expense;
        Require(paidResult.Single().Contains("Order issued")&&charged>0&&Math.Abs(money-empire.Economy.Money-charged)<.05f,"positive-cost native cross-Series order debits exactly the native expense ledger amount");
        Require(selectedNativeFees.Count==fees+1&&paidShip.ResourceReservations.Count>0,"positive-cost order reserves actual yard resources and records one native charge");
        var slModule=System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Directory.GetFiles(Path.Combine(root,"mods","3815304122"),"*.dll").Single());
        var routes=slModule.GetType("DW2ShipLogistics.FuelRoutes",true)!;var enabled=AccessTools.Field(routes,"Enabled");bool wasEnabled=(bool)enabled.GetValue(null)!;enabled.SetValue(null,true);
        byte[] reservedOrder=ShipBytes(paidShip);var reservations=paidShip.ResourceReservations;
        try
        {
            Require((bool)AccessTools.Method(routes,"Eligible").Invoke(null,[empire])!,"installed Ship Logistics route planner is enabled for the paid-order fixture");
            Require((bool)AccessTools.Method(routes,"Run").Invoke(null,[paidShip,paidShip.Mission,reservations,false,true,false])!,"actual installed Ship Logistics route planner lets native Core retain a reserved retrofit");
            Require(ReferenceEquals(reservations,paidShip.ResourceReservations)&&ShipBytes(paidShip).SequenceEqual(reservedOrder),"Ship Logistics reserved-order branch leaves the native reservation owner and exact mission unchanged");
        }
        finally{enabled.SetValue(null,wasEnabled);}
        byte[] acceptedPaid=ShipBytes(paidShip),paidLedger=EconomyBytes(empire.Economy);
        SelectedRetrofit.Send(galaxy,empire,[paidShip],paidTarget,SelectedRetrofit.Fingerprint(paidTarget));
        Require(selectedNativeFees.Count==fees+1&&ShipBytes(paidShip).SequenceEqual(acceptedPaid)&&EconomyBytes(empire.Economy).SequenceEqual(paidLedger),"paid-order retry cannot duplicate mission, reservation or native debit");
    }
}
