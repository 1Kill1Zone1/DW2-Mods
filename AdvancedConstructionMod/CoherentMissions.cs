using DistantWorlds.Types;
using DW2Mod;

namespace AdvancedConstructionMod;

internal static class CoherentMissions
{
    internal static void RegisterSeriesChannel() =>
        LiveChannel.Provide<Func<object,int,string?>>("advanced-construction.design-series", 1, (campaign, id) =>
            campaign is Galaxy galaxy && id >= 0 ? ConstructionCampaigns.Get(galaxy).SeriesName(id) : null);
    internal static Dictionary<string,string>? Choices(EditorSession session) =>
        LiveChannel.Get<Func<object,int,Dictionary<string,string>>>("coherent-research.hull-missions",1)?.Invoke(session.Galaxy,session.Draft.HullId);

    internal static string? CurrentText(EditorSession session) =>
        LiveChannel.Get<Func<object,int,string>>("coherent-research.design-mission-text",1)?.Invoke(session.Galaxy,session.Origin?.DesignId ?? session.Draft.DesignId);

    internal static bool SaveSelection(EditorSession session) =>
        LiveChannel.Get<Func<object,int,string,bool>>("coherent-research.save-mission",1)?.Invoke(session.Galaxy,session.Draft.DesignId,session.SelectedMission!)==true;

    internal static Design? Preview(EditorSession session,string mission)
    {
        var draft=session.Draft;
        var made=LiveChannel.Get<Func<object,int,string,object>>("coherent-research.preview-mission",1)?.Invoke(session.Galaxy,draft.HullId,mission) as Design;
        if(made?.Components==null || made.HullId!=draft.HullId || made.Tactics==null)return null;
        var preview=draft.Clone(session.Galaxy);
        preview.DesignId=draft.DesignId;
        preview.Components=made.Components;
        preview.Tactics=(draft.Tactics??made.Tactics).Clone();
        preview.Tactics.AttackRangeNormal=made.Tactics.AttackRangeNormal;
        preview.Tactics.AttackRangeStronger=made.Tactics.AttackRangeStronger;
        return preview;
    }
    internal static void CopyAfterUpgrade(Galaxy galaxy, Design source, Design target)
    {
        try
        {
            var copy = LiveChannel.Get<Action<object, int, int>>("coherent-research.design-copy-mission", 1);
            copy?.Invoke(galaxy, source.DesignId, target.DesignId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not inherit the Coherent mission: {0}", ex.Message);
        }
    }
}
