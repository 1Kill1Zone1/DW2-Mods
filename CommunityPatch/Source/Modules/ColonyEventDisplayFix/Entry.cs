using System.Security.Cryptography;
using DistantWorlds.Types;
using HarmonyLib;

namespace ColonyEventDisplayFix;

public static class Entry
{
    internal const string PatchId = "local.dw2.colony-event-display-fix";
    internal const string EngineHash = "62F03F35A7EE8BBC7BF93AC7D184D614209631A4F68C488224E6CE9C9E2A0E23";
    private const string PreviousEngineHash = "787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254";
    static readonly Harmony Hooks = new(PatchId);
    static bool installed;

    public static void Init()
    {
        if (installed) return;
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(HoverTip).Assembly.Location))) is not (EngineHash or PreviousEngineHash))
            throw new NotSupportedException("Colony Event Display Fix 0.1.1 requires a verified Steam beta engine.");
        var matches = AccessTools.DeclaredMethod(typeof(HoverTip), "CheckExtraDataMatches", [typeof(object), typeof(object), typeof(object), typeof(object)]);
        try
        {
            Hooks.Patch(matches, prefix: new HarmonyMethod(typeof(Entry), nameof(RejectChangedColonyEventGroup)));
            Hooks.Patch(AccessTools.DeclaredMethod(typeof(UserInterfaceHelper), "DrawColonyEventSummary"),
                transpiler: new HarmonyMethod(typeof(BreedingWorldSummary), nameof(BreedingWorldSummary.Transpile)));
            installed = true;
        }
        catch { Hooks.UnpatchAll(PatchId); throw; }
    }

    // Native comparison incorrectly treats unequal grouped colony-event list lengths as equivalent,
    // leaving an old header tooltip cached after a group grows or shrinks.
    static bool RejectChangedColonyEventGroup(object __0, object __1, object __2, object __3, ref bool __result)
    {
        if (__0 is OrbList oldOrbs && __1 is ColonyEventList oldEvents &&
            __2 is OrbList newOrbs && __3 is ColonyEventList newEvents &&
            (oldOrbs.Count != newOrbs.Count || oldEvents.Count != newEvents.Count))
        {
            __result = false;
            return false;
        }
        return true;
    }
}
