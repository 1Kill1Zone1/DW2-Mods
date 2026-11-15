using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DistantWorlds.Types;
using DistantWorlds2;
using FreightDispatch;
using HarmonyLib;
using UiChecks;

namespace DW2ModLauncher.Loader;

static partial class Program
{
    static int colorBodyCalls;
    static Exception? colorBodyError;
    static bool ColorSaveBody(string filePath)
    {
        colorBodyCalls++;
        if (colorBodyError != null) throw colorBodyError;
        File.WriteAllText(filePath, "Controlled successful native-body boundary"); return false;
    }

    static void VerifyShipColors(string root, string fixture)
    {
        string dll = Path.Combine(root, "mods", "3833408745", "DW2R-Shipset-Colors.dll");
        Console.WriteLine("SHIPSET COLORS SHA256 " + Hash(dll));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var saves = assembly.GetType("DW2R_Shipset_Colors.Patches.ShipColorSaves", true)!;
        var variants = assembly.GetType("DW2R_Shipset_Colors.ShipColorVariants") ??
            assembly.GetTypes().Single(t => t.Name == "ShipColorVariants");
        var replace = AccessTools.Method(variants, "ReplaceAll");
        var postfix = AccessTools.Method(saves.GetNestedType("DWGame_SaveGame", BindingFlags.NonPublic)!, "Postfix");
        var method = AccessTools.Method(typeof(DWGame), "SaveGame", [typeof(string)]);
        var colors = new Harmony("local.dw2.freight-shipcolors-check.actual");
        var body = new Harmony("local.dw2.freight-shipcolors-check.body");
        colors.Patch(method, postfix: new HarmonyMethod(postfix));
        body.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(ColorSaveBody)) { priority = Priority.Last });
        var g = Load(fixture); var e = g.Empires.GetPlayer();
        Check(State.Ready(g, e, out var session), "Matrix ready for isolated paired save test");
        State.Persist(g, session);
        var setter = AccessTools.Field(typeof(SavedData), "setter");
        var original = (Action<object, string, string, string>)setter.GetValue(null)!;
        var scratch = Path.Combine(AppContext.BaseDirectory, "shipcolor-save-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        bool reproduce = Environment.GetEnvironmentVariable("FREIGHT_CHECK_COLOR_REPRO") == "1";
        using var ui = new NativeUi(g);
        try
        {
            foreach (bool empty in new[] { true, false })
            {
                var current = empty ? new Dictionary<int, string>() : new Dictionary<int, string> { [e.EmpireId] = "current-campaign-color" };
                replace.Invoke(null, [current]);
                Check(((Dictionary<int, string>)AccessTools.Method(variants, "Snapshot").Invoke(null, null)!).Count == current.Count,
                    "actual Shipset Colors snapshot contains the intended empty/nonempty scenario");
                string main = Path.Combine(scratch, "old-" + empty + ".DWGame");
                string sidecar = (string)AccessTools.Method(saves, "SidecarPath").Invoke(null, [main])!;
                Check(Path.GetDirectoryName(sidecar) == scratch, "actual companion resolver stays inside isolated scratch directory");
                File.WriteAllText(main, "Existing complete save");
                File.WriteAllText(sidecar, "{\"colors\":{\"1\":\"prior-save-color\"}}");
                string beforeMain = Hash(main), beforeSidecar = Hash(sidecar);
                colorBodyCalls = 0;
                session.Data!.NextTaskId++; // A changed Matrix payload must reach the injected writer.
                setter.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
                {
                    if (ReferenceEquals(campaign, g) && owner == "freight-dispatch") throw new IOException("Controlled Matrix outage before native save");
                    original(campaign, owner, key, value);
                }));
                try { method.Invoke(ui.Host, [main]); }
                finally { setter.SetValue(null, original); }
                bool changed = !File.Exists(sidecar) || Hash(sidecar) != beforeSidecar;
                Check(Hash(main) == beforeMain && colorBodyCalls == 0, "actual FD preflight preserves old main save and skips the body");
                Check(session.PendingSaveError != null && AccessTools.Field(typeof(State), "preparedGameSave").GetValue(null) == null,
                    "blocked save retains visible error and releases the prepared scope");
                Console.WriteLine($"COLOR ABORT empty={empty} sidecarExists={File.Exists(sidecar)} sidecarChanged={changed}");
                Check(reproduce ? changed : !changed, reproduce ? "REPRODUCED companion mutation after FD-blocked save" : "blocked save preserves the original color companion byte-for-byte");
            }
            if (reproduce) return;
            string fresh = Path.Combine(scratch, "blocked-new.DWGame");
            string freshSidecar = (string)AccessTools.Method(saves, "SidecarPath").Invoke(null, [fresh])!;
            session.Data!.NextTaskId++; colorBodyCalls = 0;
            setter.SetValue(null, (Action<object, string, string, string>)((campaign, owner, key, value) =>
            {
                if (ReferenceEquals(campaign, g) && owner == "freight-dispatch") throw new IOException("Controlled new-save preflight failure");
                original(campaign, owner, key, value);
            }));
            try { method.Invoke(ui.Host, [fresh]); }
            finally { setter.SetValue(null, original); }
            Check(!File.Exists(fresh) && !File.Exists(freshSidecar) && !File.Exists(freshSidecar + ".tmp") && colorBodyCalls == 0,
                "rejected new save creates neither a main file nor an orphan color companion");
            foreach (bool empty in new[] { true, false })
            {
                string main = Path.Combine(scratch, "success-" + empty + ".DWGame");
                string sidecar = (string)AccessTools.Method(saves, "SidecarPath").Invoke(null, [main])!;
                File.WriteAllText(sidecar, "old colors");
                replace.Invoke(null, [empty ? new Dictionary<int, string>() : new Dictionary<int, string> { [e.EmpireId] = "new-save-color" }]);
                colorBodyCalls = 0; method.Invoke(ui.Host, [main]);
                Check(colorBodyCalls == 1 && File.ReadAllText(main) == "Controlled successful native-body boundary", "accepted preflight permits the controlled save body");
                Check(empty ? !File.Exists(sidecar) : JsonDocument.Parse(File.ReadAllText(sidecar)).RootElement.GetProperty("colors").GetProperty(e.EmpireId.ToString()).GetString() == "new-save-color",
                    "accepted save retains actual Shipset Colors write/delete behaviour");
                Check(AccessTools.Field(typeof(State), "preparedGameSave").GetValue(null) == null, "successful save releases its FD thread scope");
            }
            string unrelated = Path.Combine(scratch, "unrelated-failure.DWGame");
            string unrelatedSidecar = (string)AccessTools.Method(saves, "SidecarPath").Invoke(null, [unrelated])!;
            File.WriteAllText(unrelatedSidecar, "Unchanged prior colors"); string priorSidecar = Hash(unrelatedSidecar);
            var sentinel = new IOException("Unrelated save-body error"); colorBodyError = sentinel;
            Exception? seen = null;
            try { method.Invoke(ui.Host, [unrelated]); }
            catch (TargetInvocationException ex) { seen = ex.InnerException; }
            finally { colorBodyError = null; }
            Check(ReferenceEquals(seen, sentinel), "actual hooked save propagates an unrelated exception unchanged");
            Check(Hash(unrelatedSidecar) == priorSidecar && AccessTools.Field(typeof(State), "preparedGameSave").GetValue(null) == null,
                "unrelated failure skips success postfixes and restores the FD scope");
        }
        finally
        {
            setter.SetValue(null, original); body.UnpatchAll(body.Id); colors.UnpatchAll(colors.Id); replace.Invoke(null, [null]); colorBodyError = null;
        }
        Console.WriteLine("Retained paired-save test artifacts: " + scratch);
    }
}
