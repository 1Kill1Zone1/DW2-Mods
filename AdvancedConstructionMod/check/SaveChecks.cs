using AdvancedConstructionMod;
using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using UiChecks;

internal static partial class Checks
{
    private static int saveBodyCalls;
    private static Exception? saveBodyFailure;
    private static bool ControlledSaveBody(string filePath)
    {saveBodyCalls++;if(saveBodyFailure!=null)throw saveBodyFailure;File.WriteAllText(filePath,"Controlled native save-body boundary");return false;}
    private static void SaveChecks(Galaxy galaxy,Empire empire,string root)
    {
        Console.WriteLine("Native checks: preflight before file open, actual FD and Shipset Colors.");
        using var ui=new NativeUi(galaxy);
        var save=AccessTools.Method(typeof(DWGame),"SaveGame",[typeof(string)]);
        var fd=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root,"mods","FreightDispatch","FreightDispatch.dll"));
        var fdState=fd.GetType("FreightDispatch.State",true)!;var fdSaved=fd.GetType("FreightDispatch.SavedData",true)!;
        AccessTools.Method(fdSaved,"RequireChannels").Invoke(null,null);
        var peer=new Harmony("acm.check.actual-fd-save");
        peer.Patch(save,prefix:new HarmonyMethod(AccessTools.Method(fdState,"BeforeGameSave")){priority=Priority.First},finalizer:new HarmonyMethod(AccessTools.Method(fdState,"FinishGameSave")));
        var colors=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root,"mods","3833408745","DW2R-Shipset-Colors.dll"));
        var colorSaves=colors.GetType("DW2R_Shipset_Colors.Patches.ShipColorSaves",true)!;
        var variants=colors.GetTypes().Single(t=>t.Name=="ShipColorVariants");var replace=AccessTools.Method(variants,"ReplaceAll");
        peer.Patch(save,postfix:new HarmonyMethod(AccessTools.Method(colorSaves.GetNestedType("DWGame_SaveGame",BindingFlags.NonPublic),"Postfix")));
        var body=new Harmony("acm.check.controlled-native-save");body.Patch(save,prefix:new HarmonyMethod(typeof(Checks),nameof(ControlledSaveBody)){priority=-100});
        var getter=AccessTools.Field(typeof(MatrixStorage),"getter");var originalGetter=getter.GetValue(null);
        var scratch=Path.Combine(TestOutput.Root,"acm-save-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        try
        {
            for(int order=0;order<2;order++)
            {
                foreach(bool empty in new[]{true,false})
                {
                    replace.Invoke(null,[empty?new Dictionary<int,string>():new Dictionary<int,string>{{empire.EmpireId,"current-color"}}]);
                    string file=Path.Combine(scratch,$"existing-{order}-{empty}.DWGame");string companion=(string)AccessTools.Method(colorSaves,"SidecarPath").Invoke(null,[file])!;
                    File.WriteAllText(file,"Previous complete save");File.WriteAllText(companion,"Previous complete ship colors");string oldMain=Hash(file),oldColors=Hash(companion);
                    getter.SetValue(null,(Func<object,string,string,string>)((_,_,_)=>throw new IOException("Injected ACM save preflight outage")));
                    saveBodyCalls=0;
                    try{save.Invoke(ui.Host,[file]);}finally{getter.SetValue(null,originalGetter);}
                    Require(saveBodyCalls==0&&Hash(file)==oldMain&&Hash(companion)==oldColors,"ACM refusal preserves native main and real color companion; order="+order+", empty="+empty);
                    Require(SavePreflight.Prepared==null&&AccessTools.Field(fdState,"preparedGameSave").GetValue(null)==null,"both save scopes restored after typed refusal");
                }
                if(order==0)
                {
                    var info=Harmony.GetPatchInfo(save)!;var prefix=info.Prefixes.Single(p=>p.owner=="local.dw2.advanced-construction");var finalizer=info.Finalizers.Single(p=>p.owner==prefix.owner);
                    var own=new Harmony(prefix.owner);own.Unpatch(save,prefix.PatchMethod);own.Unpatch(save,finalizer.PatchMethod);
                    own.Patch(save,prefix:new HarmonyMethod(prefix.PatchMethod){priority=prefix.priority},finalizer:new HarmonyMethod(finalizer.PatchMethod){priority=finalizer.priority});
                }
            }
            string fresh=Path.Combine(scratch,"rejected-new.DWGame");string freshCompanion=(string)AccessTools.Method(colorSaves,"SidecarPath").Invoke(null,[fresh])!;
            getter.SetValue(null,(Func<object,string,string,string>)((_,_,_)=>throw new IOException("Injected new-save outage")));
            try{save.Invoke(ui.Host,[fresh]);}finally{getter.SetValue(null,originalGetter);}
            Require(!File.Exists(fresh)&&!File.Exists(freshCompanion),"rejected new save creates neither file nor orphan color companion");
            string accepted=Path.Combine(scratch,"accepted.DWGame");saveBodyCalls=0;save.Invoke(ui.Host,[accepted]);
            Require(saveBodyCalls==1&&File.ReadAllText(accepted)=="Controlled native save-body boundary"&&SavePreflight.Prepared==null,"accepted paired preflights reach body once and release scopes");
            var sentinel=new IOException("Unrelated native save failure");saveBodyFailure=sentinel;Exception? seen=null;
            try{save.Invoke(ui.Host,[Path.Combine(scratch,"unrelated.DWGame")]);}catch(TargetInvocationException ex){seen=ex.InnerException;}finally{saveBodyFailure=null;}
            Require(ReferenceEquals(seen,sentinel)&&SavePreflight.Prepared==null,"foreign save exception is propagated unchanged and releases ACM scope");
            Require(SavePreflight.Prepared==null,"prepared marker never escapes paired native save test");
        }
        finally
        {getter.SetValue(null,originalGetter);saveBodyFailure=null;body.UnpatchAll(body.Id);peer.UnpatchAll(peer.Id);replace.Invoke(null,[null]);}
        Console.WriteLine("Paired-save fixture retained in "+scratch);
    }
}
