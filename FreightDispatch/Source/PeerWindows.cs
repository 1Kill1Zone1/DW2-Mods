using System.Reflection;
using HarmonyLib;

namespace FreightDispatch;

// Older copies of the shared dock do not know FreightDispatchPanel yet.
static class PeerWindows
{
    internal static void Install(Harmony hooks)
    {
        foreach (string name in new[] { "BattleLog", "WeaponFX", "GalaxyWaypoints" })
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            var method = assembly?.GetType("LocalModUi.NativeToolDock")?.GetMethod("IsToolWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) continue;
            if (method.ReturnType != typeof(bool) || method.GetParameters() is not [{ ParameterType: var argument }] || argument != typeof(string))
                throw new InvalidOperationException("Freight Dispatch cannot adapt incompatible tool dock in " + name);
            hooks.Patch(method, postfix: new HarmonyMethod(typeof(PeerWindows), nameof(RecognizeFreight)));
        }
    }
    static void RecognizeFreight(string? __0, ref bool __result)
    {
        if (!__result && __0 == "FreightDispatchPanel" && Entry.OwnsRegisteredPanel()) __result = true;
    }
}
