using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;

namespace ResourceIntelligence;
public static class Entry
{
    static readonly Harmony hooks = new("local.dw2.resource-intelligence");
    static bool initialized;
    public static void Init()
    {
        if (initialized) return;
        try
        {
            hooks.Patch(AccessTools.Method(typeof(DiplomacyTalkSystem), nameof(DiplomacyTalkSystem.BindData)), postfix: new HarmonyMethod(typeof(Dialogue), nameof(Dialogue.Bound)));
            hooks.Patch(AccessTools.Method(typeof(DiplomacyTalkSystem), nameof(DiplomacyTalkSystem.ResolveOptions)), postfix: new HarmonyMethod(typeof(Dialogue), nameof(Dialogue.AddEntry)) { priority = Priority.Last });
            hooks.Patch(AccessTools.Method(typeof(DiplomacyTalkSystem), nameof(DiplomacyTalkSystem.ProcessOption)), prefix: new HarmonyMethod(typeof(Dialogue), nameof(Dialogue.Process)));
            hooks.Patch(AccessTools.Method(typeof(DiplomacyConversation), "TalkOptionClicked"), postfix: new HarmonyMethod(typeof(Dialogue), nameof(Dialogue.AfterClick)));
            // Native survey/merge/save operations must not interleave with rollback of a paid discovery.
            var synchronized = new[] {
                AccessTools.Method(typeof(ExplorationMap), nameof(ExplorationMap.SetExplorationLevel), [typeof(Orb), typeof(byte), typeof(bool)]),
                AccessTools.Method(typeof(ExplorationMap), nameof(ExplorationMap.MergeMap)),
                AccessTools.Method(typeof(ExplorationMap), nameof(ExplorationMap.MergeSystemMap)),
                AccessTools.Method(typeof(ExplorationMap), nameof(ExplorationMap.SetMapDirectly)),
                AccessTools.Method(typeof(VisibilityMap), nameof(VisibilityMap.SetSystemExplored)),
                AccessTools.Method(typeof(VisibilityMap), nameof(VisibilityMap.SetSystemUnexplored)),
                AccessTools.Method(typeof(VisibilityMap), nameof(VisibilityMap.MergeSystemExplorationLevels)),
                AccessTools.Method(typeof(VisibilityMap), nameof(VisibilityMap.UpdateKnownData)),
                AccessTools.Method(typeof(Galaxy), nameof(Galaxy.WriteToStream))
            };
            foreach (var method in synchronized) hooks.Patch(method,
                prefix: new HarmonyMethod(typeof(Entry), nameof(Enter)), finalizer: new HarmonyMethod(typeof(Entry), nameof(Leave)));
            initialized = true;
        }
        catch { hooks.UnpatchAll(hooks.Id); throw; }
    }
    static void Enter(out bool __state) { __state = false; Monitor.Enter(Market.Gate); __state = true; }
    static Exception? Leave(bool __state, Exception? __exception) { if (__state) Monitor.Exit(Market.Gate); return __exception; }
}
