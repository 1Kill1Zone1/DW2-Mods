using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;

namespace AdvancedConstructionMod;

public static class Entry
{
    private const string PatchId = "local.dw2.advanced-construction";
    private static readonly object InitGate = new();
    private static bool initialized;

    public static void Init()
    {
        lock (InitGate)
        {
            if (initialized) return;
            var harmony = new Harmony(PatchId);
            try
            {
                PatchOnce(harmony, AccessTools.DeclaredMethod(typeof(Design), nameof(Design.ResolveRetrofitDesignId),
                    [typeof(Galaxy), typeof(Empire), typeof(Design).MakeByRefType()]), nameof(ResolveRetrofitDesignId));
                harmony.Patch(AccessTools.Method(typeof(Galaxy),nameof(Galaxy.WriteToStream)),
                    prefix:new HarmonyMethod(typeof(Entry),nameof(BeforeGalaxySave)),
                    finalizer:new HarmonyMethod(typeof(Entry),nameof(FinishGalaxySave)));
                PatchOnce(harmony, AccessTools.Method(typeof(Galaxy), nameof(Galaxy.ReadFromStream)), nameof(AfterGalaxyLoad));
                SavePreflight.Install(harmony);
                RetrofitAdmission.Install(harmony);
                PatchBoth(harmony, AccessTools.DeclaredMethod(typeof(ShipDesignRenderer), "DoSave", [typeof(bool)]),
                    typeof(DesignerIntegration), nameof(DesignerIntegration.BeforeSave), nameof(DesignerIntegration.AfterSave));
                DesignerIntegration.Install(harmony);
                initialized = true;
            }
            catch
            {
                harmony.UnpatchAll(PatchId);
                throw;
            }
        }
    }

    private static void PatchOnce(Harmony harmony, System.Reflection.MethodInfo? target, string handler, bool prefix = false)
    {
        if (target == null) throw new MissingMethodException("Steam beta 1.3.8.7 API method was not found for Advanced Construction Mod.");
        if (Harmony.GetPatchInfo(target)?.Owners.Contains(PatchId) == true) return;
        var patch = new HarmonyMethod(typeof(Entry), handler);
        if (prefix) harmony.Patch(target, prefix: patch); else harmony.Patch(target, postfix: patch);
    }

    private static void PatchBoth(Harmony harmony, System.Reflection.MethodInfo? target, Type owner, string prefix, string postfix)
    {
        if (target == null) throw new MissingMethodException("Steam beta 1.3.8.7 API method was not found for Advanced Construction Mod.");
        if (Harmony.GetPatchInfo(target)?.Owners.Contains(PatchId) == true) return;
        harmony.Patch(target, prefix: new HarmonyMethod(owner, prefix), postfix: new HarmonyMethod(owner, postfix));
    }

    private static void ResolveRetrofitDesignId(Design __instance, Galaxy galaxy, Empire empire, ref Design design, ref int __result)
    {
        try
        {
            var registry = ConstructionCampaigns.Get(galaxy, empire);
            if (!registry.TryGet(empire, out ConstructionSnapshot state, out string? stateError))
            {
                // A corrupt/missing mapped class may not degrade into the global newest design.
                __result = ConstructionGraph.Disabled; design = null!; return;
            }
            if (!state.DesignClasses.TryGetValue(__instance.DesignId, out string? classId)) return;
            var source = RetrofitRuntime.Fact(galaxy, empire, __instance);
            var candidates = empire.Designs.ToArray().Where(candidate => candidate != null &&
                state.DesignClasses.TryGetValue(candidate.DesignId, out string? candidateClass) && candidateClass == classId)
                .Select(candidate => RetrofitRuntime.Fact(galaxy, empire, candidate)).ToArray();
            RetrofitResolution resolution = ConstructionGraph.ResolveCandidate(state, source, candidates, __result, RetrofitRuntime.NativeCompatible);
            __result = resolution.DesignId;
            design = resolution.DesignId >= 0 ? galaxy.Designs.GetById(resolution.DesignId)! : null!;
        }
        catch
        {
            // Resolver failures must never route a classified branch to a foreign latest design.
            __result = ConstructionGraph.Disabled; design = null!;
        }
    }

    private static void BeforeGalaxySave(Galaxy __instance,out object? __state)
    {
        __state=null;if(ReferenceEquals(SavePreflight.Prepared,__instance))return;
        var registry=ConstructionCampaigns.Get(__instance);Monitor.Enter(registry.Gate);
        try { registry.PersistAll();__state=registry.Gate; }
        catch (Exception ex) {Monitor.Exit(registry.Gate);throw new InvalidOperationException("Advanced Construction Mod could not store series metadata; save stopped to avoid silent loss.", ex); }
    }
    private static Exception? FinishGalaxySave(Exception? __exception,object? __state)
    {if(__state!=null)Monitor.Exit(__state);return __exception;}

    private static void AfterGalaxyLoad(Galaxy __instance) => ConstructionCampaigns.Forget(__instance);

}

internal static class RetrofitRuntime
{
    public static DesignFact Fact(Galaxy galaxy, Empire empire, Design design)
    {
        ShipHull? hull = design.GetShipHull(galaxy);
        bool buildable = hull != null && design.CheckEmpireHasResearchedAllTech(galaxy, empire, hull) &&
            Design.CheckDesignIsValid(galaxy, empire, design);
        return new DesignFact(design.DesignId, design.EmpireId, design.HullId, (int)design.GetRole(galaxy), design.Obsolete,
            buildable, design.StarDate, design.UpgradeDesignId);
    }

    public static bool NativeCompatible(DesignFact source, DesignFact target) => source.Role == target.Role && target.Buildable;

}
