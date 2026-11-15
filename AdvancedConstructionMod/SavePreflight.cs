using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;

namespace AdvancedConstructionMod;

internal static class SavePreflight
{
    [ThreadStatic] internal static Galaxy? Prepared;
    private static readonly ConditionalWeakTable<DWGame,Notice> Notices=new();
    internal static void Install(Harmony harmony)
    {
        var save=AccessTools.Method(typeof(DWGame),"SaveGame",[typeof(string)])??throw new MissingMethodException("DWGame.SaveGame");
        // Run after peer preflights, before native file opening; do not hold our gate while FD takes its own.
        harmony.Patch(save,prefix:new HarmonyMethod(typeof(SavePreflight),nameof(BeforeGameSave)){priority=Priority.Last},
            finalizer:new HarmonyMethod(typeof(SavePreflight),nameof(FinishGameSave)));
        harmony.Patch(AccessTools.Method(typeof(UserInterfaceController),"Update"),postfix:new HarmonyMethod(typeof(SavePreflight),nameof(UpdateUi)));
    }
    internal static void BeforeGameSave(DWGame __instance,out Scope? __state)
    {
        __state=null;var galaxy=__instance.Galaxy;if(galaxy==null)return;
        var scope=new Scope(Prepared,ConstructionCampaigns.Get(galaxy).Gate);__state=scope;
        try
        {
            Monitor.Enter(scope.Gate);scope.Held=true;
            if(!ReferenceEquals(Prepared,galaxy))ConstructionCampaigns.Get(galaxy).PersistAll();
            Prepared=galaxy;
        }
        catch(Exception ex)
        {
            scope.Release();
            Notices.GetOrCreateValue(__instance).Text="Advanced Construction: save blocked before opening the file; previous save preserved. "+ex.Message;
            // A false prefix would still run companion-file success postfixes.
            throw new Rejected(ex);
        }
    }
    internal static Exception? FinishGameSave(Exception? __exception,Scope? __state)
    {__state?.Release();return __exception is Rejected?null:__exception;}
    internal static void UpdateUi()
    {
        if(Galaxy.Game is not DWGame game)return;
        if(NativeWindow.Active?.Campaign is { } campaign&&!ReferenceEquals(game.Galaxy,campaign))NativeWindow.Active.Close();
        if(game.Status!=GameStatus.Running||!Notices.TryGetValue(game,out var notice)||notice.Text==null)return;
        if(AccessTools.Field(typeof(DWGame),"_Renderer").GetValue(game) is not ScaledRenderer renderer)return;
        renderer.SetBriefMessage(notice.Text,20f);notice.Text=null;
    }
    internal sealed class Scope(Galaxy? previous,object gate)
    {
        internal readonly object Gate=gate;internal bool Held;
        internal void Release(){if(!Held)return;Held=false;Prepared=previous;Monitor.Exit(Gate);}
    }
    private sealed class Rejected(Exception cause):Exception("Advanced Construction save preflight rejected",cause);
    private sealed class Notice {internal volatile string? Text;}
}
