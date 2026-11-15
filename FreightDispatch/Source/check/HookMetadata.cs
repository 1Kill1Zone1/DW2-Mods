using System.Reflection;
using System.Text.Json;
using HarmonyLib;

namespace DW2ModLauncher.Loader;
static partial class Program
{
    static void WriteHookMetadata()
    {
        var hooks = new List<object>();
        foreach (var method in Harmony.GetAllPatchedMethods().OrderBy(m => m.DeclaringType!.FullName).ThenBy(m => m.Name))
        {
            var info = Harmony.GetPatchInfo(method)!;
            foreach (var (kind, patches) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) })
                foreach (var patch in patches.Where(p => p.owner == "local.dw2.freight-dispatch"))
                {
                    var (effect, why) = patch.PatchMethod.Name switch
                    {
                        "Schedule" => ("changes-args", "After installed fuel-first review, allocates bounded owned dispatch contracts and native missions; no uncontracted task demand escapes to the ordinary scheduler."),
                        "OrderSources" => ("changes-args", "Orders eligible owned High sources without changing native path scores; keeps dedicated task orders out of ordinary recruitment."),
                        "FilterFreighters" => ("rewrites", "Within dispatch selection only, filters each candidate for ownership, empty cargo, native docking/path/fuel checks; original eligibility otherwise."),
                        "BeforeContract" => ("skips-original", "Protects active manual reserves via per-call arguments; rejects new reverse replenishment and unauthorised dedicated-task allocations."),
                        "BeforeStockOrders" => ("changes-args", "Filters active exported resources from new source replenishment arguments without modifying user stock settings."),
                        "BeforeLoad" => ("skips-original", "Stops new task pickups after cancellation or endpoint loss while preserving loaded delivery."),
                        "AfterUnload" => ("observes", "Reconciles task delivery totals and allocation identities from native contract outcomes, including while the fault latch stops new pickups."),
                        "BeforeCancelContract" => ("observes", "Captures delivered progress before native contract removal even when task scheduling is faulted."),
                        "AllowTopUp" => ("skips-original", "Suppresses installed Freighter Logistics top-up only for task voyages and selected resource holds at valid owned export sources."),
                        "ClearPress" => ("observes", "Shared native-tool mouse-down ownership reset; leaves original native input handling intact."),
                        "BeforeSave" => ("observes", "Flushes verified Matrix task identities and delivery totals before native serialization, retrying after fault cleanup. Cleanup or retry failure aborts serialization instead of saving a stale ledger. Missing/corrupt payloads with no valid session data remain preserved."),
                        "BeforeGameSave" => ("skips-original", "Verifies and commits FD state before native SaveGame opens its target. A typed preflight rejection skips both native save and success postfixes before any file is opened; its finalizer handles only that rejection and a visible notice is queued."),
                        "FinishGameSave" => ("changes-result", "Restores the prepared-save scope and clears only the FD preflight rejection; unrelated exceptions remain intact."),
                        "RecognizeFreight" => ("changes-result", "Adds only the currently registered, owned FreightDispatchPanel to this older peer's recognized tool windows. Other names, native/modals and foreign controls remain unchanged; targetAssembly identifies the exact peer copy."),
                        "UpdateUi" => ("observes", "Updates the scoped native panel and dock with map/observer/occlusion guards."),
                        "AllowGameKey" => ("skips-original", "Consumes Escape only for the selected visible Freight panel and game hotkeys only while its own numeric or search field has focus; other input remains native."),
                        "AllowSingleKey" or "AllowKeys" => ("skips-original", "Rejects keyboard input only for Freight input fields while hidden, obstructed, or noneditable."),
                        _ => throw new InvalidOperationException("Uncatalogued Freight hook " + patch.PatchMethod.Name)
                    };
                    hooks.Add(new { kind, effect, why, targetAssembly = method.DeclaringType!.Assembly.GetName().Name, target = method.DeclaringType!.FullName + ":" + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")" });
                }
        }
        string path = Path.Combine(AppContext.BaseDirectory, "dw2modlauncher.hooks.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, modVersion = "0.2.4", gameVersion = typeof(Galaxy).Assembly.GetName().Version!.ToString(), hooks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Exact installed Freight Dispatch hook metadata: " + path);
    }
}
