using AdvancedConstructionMod;
using DistantWorlds.Types;
using HarmonyLib;
using System.Runtime.Loader;

internal static partial class Checks
{
    private static int selectedYardBuilds;
    private static HashSet<int> selectedYardIds=[];
    private static void CountSelectedNativeBuild(Ship ship){if(selectedYardIds.Contains(ship.ShipId))selectedYardBuilds++;}
    private static void SelectedRetrofitYardChecks(Galaxy galaxy,Empire empire,Design target,List<Ship> ships,Func<Galaxy,Galaxy> roundtrip,string root)
    {
        Console.WriteLine("Native checks: selected retrofit yard, queue and completion.");
        var ship=ships[0];var yard=ship.Mission.PrimaryTarget.ResolveBase<StellarObject>(galaxy)!;
        if(yard is Ship port){port.SetGalaxy(galaxy);port.UpdateBonuses();port.RegenerateSummary();}
        var system=yard is Ship p?p.ConstructionSystem:((Orb)yard).Colony!.ConstructionSystem;
        var yardEmpire=yard.GetEmpire(galaxy)!;var location=yard.GetLocation(galaxy)!;
        var originalWait=system.WaitQueue.ToArray();var originalYards=system.Yards.ToArray();
        system.WaitQueue.Clear();system.Yards.Clear();
        foreach(var slot in originalYards)system.Yards.Add(new ConstructionYard(slot.ComponentId,slot.ComponentBayId,-1,(ConstructionType)1,-1));
        var add=AccessTools.GetDeclaredMethods(typeof(ConstructionSystem)).Where(m=>m.Name=="AddShipToConstruct").OrderByDescending(m=>m.GetParameters().Length).First();
        var installed=Path.Combine(root,"mods","3815304122");var module=AssemblyLoadContext.Default.LoadFromAssemblyPath(Directory.GetFiles(installed,"*.dll").Single());
        var latest=module.GetType("DW2ShipLogistics.LatestDesign",true)!;var sl=new Harmony("acm.check.selected-real-sl");sl.Patch(add,prefix:new HarmonyMethod(AccessTools.Method(latest,"Prefix")));
        var observer=new Harmony("acm.check.selected-native-build");observer.Patch(AccessTools.Method(typeof(ConstructionSystem),"BuildShip"),prefix:new HarmonyMethod(typeof(Checks),nameof(CountSelectedNativeBuild)));
        selectedYardIds=ships.Take(5).Select(s=>s.ShipId).ToHashSet();selectedYardBuilds=0;
        var otherShips=ships.Skip(5).ToDictionary(s=>s.ShipId,ShipBytes);
        try
        {
            foreach(var selected in ships.Take(5))
            {
                selected.Parent=selected.Mission.PrimaryTarget;selected.ParentIsDockedAt=true;
                AccessTools.Method(typeof(Ship),"CommandRetrofit").Invoke(selected,[empire,galaxy.Time,.1f,location,target.DesignId]);
                Require(system.WaitQueue.Any(w=>w.ShipUnderConstructionId==selected.ShipId&&w.DesignId==target.DesignId),"actual native docking command queues exact target with installed SL prefix "+selected.ShipId);
            }
            int queued=system.WaitQueue.Count;
            AccessTools.Method(typeof(Ship),"CommandRetrofit").Invoke(ship,[empire,galaxy.Time,.1f,location,target.DesignId]);
            Require(system.WaitQueue.Count==queued,"native repeated dock command cannot enqueue twice");
            var queuedSave=roundtrip(galaxy);var loadedShip=queuedSave.Ships.GetById(ship.ShipId);loadedShip.SetGalaxy(queuedSave);
            Require(SelectedRetrofit.InYard(queuedSave,loadedShip)&&SelectedRetrofit.HasOrder(loadedShip,queuedSave.Empires.GetById(empire.EmpireId),out _),"native queued yard state and individual pin survive save/load");
            var original=target.Components[0];var changed=original;changed.Status=short.MaxValue;target.Components[0]=changed;
            var before=ShipBytes(ship);int builds=selectedYardBuilds;
            system.DoTasks(.1f,galaxy.Time,galaxy,yardEmpire,location,false);
            Require(selectedYardBuilds==builds&&system.WaitQueue.Any(w=>w.ShipUnderConstructionId==ship.ShipId)&&ShipBytes(ship).SequenceEqual(before),"actual native wait loop refuses changed exact payload before yard mutation or build");
            Require(SelectedRetrofit.Problem(galaxy,ship.ShipId)!=null,"blocked queue exposes an explicit project-change reason");
            target.Components[0]=original;
            bool oldObsolete=target.Obsolete;target.Obsolete=true;
            for(int i=0;i<150&&ships.Take(5).Any(s=>SelectedRetrofit.HasOrder(s,empire,out _));i++)
            {
                try{system.DoTasks(60f,galaxy.Time.AddSeconds(i+1),galaxy,yardEmpire,location,false);}
                catch{Console.WriteLine("YARD FAILURE builds="+selectedYardBuilds+" ships="+string.Join("; ",ships.Take(5).Select(s=>$"{s.ShipId}/design{s.DesignId}/summary{s.Summary!=null}/mode{s.MissionAssignmentType}/parent{s.Parent.Id}/pin{SelectedRetrofit.HasOrder(s,empire,out _)}")));throw;}
            }
            target.Obsolete=oldObsolete;
            Require(selectedYardBuilds>0,"actual native BuildShip executes accepted work");
            foreach(var selected in ships.Take(5))Require(selected.DesignId==target.DesignId&&!SelectedRetrofit.HasOrder(selected,empire,out _)&&selected.MissionAssignmentType==ShipMissionAssignmentType.FullyAutomate,"completion applies target project and restores only previous selected control "+selected.ShipId);
            Require(ships[7].MissionAssignmentType==ShipMissionAssignmentType.Manual&&SelectedRetrofit.HasOrder(ships[7],empire,out _),"unfinished selected order remains manual and pinned");
            Require(ships.Skip(5).All(s=>ShipBytes(s).SequenceEqual(otherShips[s.ShipId])),"all other 26 ships retain exact native binary state through selected yard completion");
            Require(ships.All(s=>ReferenceEquals(s.Fleet,ship.Fleet))&&ship.Fleet!.Ships.Count==31,"native yard completion preserves all 31 fleet memberships");
            var paid=ships[18];var paidTarget=galaxy.Designs.GetById(paid.Mission.BuildDesignId);
            Require(paidTarget.HullId!=target.HullId&&paid.ResourceReservations.Count>0,"separate paid completion case has a different legal native hull and actual reserved resources");
            var paidPort=paid.Mission.PrimaryTarget.ResolveBase<StellarObject>(galaxy)!;
            Require(ReferenceEquals(paidPort,yard),"separate paid case uses the same isolated native yard");
            paid.Parent=paid.Mission.PrimaryTarget;paid.ParentIsDockedAt=true;
            AccessTools.Method(typeof(Ship),"CommandRetrofit").Invoke(paid,[empire,galaxy.Time,.1f,location,paidTarget.DesignId]);
            system.DoTasks(.00001f,galaxy.Time,galaxy,yardEmpire,location,false);
            Require(paid.DesignId==paidTarget.DesignId&&paid.IsUnderConstructionOrRepair&&SelectedRetrofit.InYard(galaxy,paid)&&SelectedRetrofit.HasOrder(paid,empire,out _),"actual native different-hull retrofit starts in a yard and retains its individual order while unfinished");
            var inYardSave=roundtrip(galaxy);var loadedPaid=inYardSave.Ships.GetById(paid.ShipId);loadedPaid.SetGalaxy(inYardSave);var loadedEmpire=inYardSave.Empires.GetById(empire.EmpireId);
            Require(loadedPaid.IsUnderConstructionOrRepair&&SelectedRetrofit.InYard(inYardSave,loadedPaid)&&SelectedRetrofit.HasOrder(loadedPaid,loadedEmpire,out _),"native save/load preserves already-started different-hull work and exact target pin");
            float expense=empire.Economy.GetExpenseCurrentPeriod((ExpenseType)6);
            for(int i=0;i<500&&SelectedRetrofit.HasOrder(paid,empire,out _);i++)system.DoTasks(60f,galaxy.Time.AddSeconds(i+200),galaxy,yardEmpire,location,false);
            Require(paid.DesignId==paidTarget.DesignId&&!paid.IsUnderConstructionOrRepair&&!SelectedRetrofit.HasOrder(paid,empire,out _)&&paid.MissionAssignmentType==ShipMissionAssignmentType.FullyAutomate,"native paid different-hull retrofit completes and restores its previous control mode");
            Require(empire.Economy.GetExpenseCurrentPeriod((ExpenseType)6)==expense&&ReferenceEquals(paid.Fleet,ship.Fleet),"native yard completion does not charge again or remove the paid ship from its fleet");

        }
        finally{sl.UnpatchAll(sl.Id);observer.UnpatchAll(observer.Id);system.WaitQueue.Clear();system.WaitQueue.AddRange(originalWait);system.Yards.Clear();system.Yards.AddRange(originalYards);}
    }
}
