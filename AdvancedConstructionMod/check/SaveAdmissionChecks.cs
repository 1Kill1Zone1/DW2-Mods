using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;
using System.Runtime.CompilerServices;
using UiChecks;

internal static partial class Checks
{
    private static void SaveAdmissionChecks(Galaxy galaxy,Empire empire,Design basis)
    {
        using var metrics=new FontMetrics();using var ui=new NativeUi(galaxy);
        var registry=ConstructionCampaigns.Get(galaxy,empire);
        Require(registry.TryGet(empire,out var current,out _),"save guard reads the current series graph");
        var staged=current.Clone();string series=ConstructionGraph.CreateClass(staged,"Save guard regression");
        var preview=ConstructionGraph.TryPreview(current,Facts(galaxy,empire),basis.DesignId,series,[],RetrofitRuntime.NativeCompatible,staged);
        Require(preview.Accepted&&preview.Next!.DesignClasses[basis.DesignId]==series,"save guard fixture actually changes series membership");
        // A live temporary ship may have no design yet; save/load normalizes it away.
        var transient=(Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));GC.SuppressFinalize(transient);
        transient.ShipId=galaxy.Ships.Max(s=>s.ShipId)+100;empire.Ships.Add(transient);
        var listField=AccessTools.Field(typeof(ShipList),"_List");
        var originalList=(IList<Ship>)listField.GetValue(empire.Ships)!;
        listField.SetValue(empire.Ships,originalList.Concat(new Ship[]{null!}).ToList());
        try
        {
            Require(RetrofitAdmission.QueuedChangesAllowed(galaxy,empire,current,preview,out _),"a null ship entry and unfinished ship do not block series save");
            transient.Mission=new ShipMission{Type=ShipMissionType.Move};
            Require(RetrofitAdmission.QueuedChangesAllowed(galaxy,empire,current,preview,out _),"unrelated non-retrofit mission does not read an unavailable design");
            var renderer=(ShipDesignRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ShipDesignRenderer));GC.SuppressFinalize(renderer);
            foreach(var (name,value) in new (string,object)[]{("_Galaxy",galaxy),("_PlayerEmpire",empire),("_Design",basis)})AccessTools.Field(typeof(ShipDesignRenderer),name).SetValue(renderer,value);
            var session=(EditorSession)AccessTools.Method(typeof(DesignerIntegration),"Session").Invoke(null,[renderer])!;
            Require(session.SelectSeriesName("Save guard staged series",out _),"new series is explicitly confirmed before save admission");
            DesignerIntegration.SaveContext? context=null;
            try{Require(DesignerIntegration.BeforeSave(renderer,out context)&&context!=null,"real ACM BeforeSave accepts a confirmed series with a transient ship present");}
            finally{AccessTools.Method(typeof(DesignerIntegration),"FinishSave").Invoke(null,[null,renderer,context]);DesignerIntegration.CloseEditor(renderer);}
            Require(!Monitor.IsEntered(registry.Gate),"series save admission releases its gate after completion");
            var active=empire.Ships.First(s=>s!=transient&&!s.IsBase&&!s.IsDestroyed);
            var oldDesign=active.GetDesign();var oldMission=active.Mission;
            try
            {
                active.Design=basis;active.Mission=new ShipMission{Type=ShipMissionType.Retrofit};
                Require(!RetrofitAdmission.QueuedChangesAllowed(galaxy,empire,current,preview,out var error)&&error!.Contains("Retrofit order"),"real affected retrofit still blocks a series change");
            }
            finally{active.Design=oldDesign;active.Mission=oldMission;}
            Require(Serialized(Read(Storage(galaxy,empire.EmpireId))!)==Serialized(current),"admission probes do not publish draft metadata");
        }
        finally{listField.SetValue(empire.Ships,originalList);empire.Ships.Remove(transient);NativeWindow.Active?.Close();}
    }
}
