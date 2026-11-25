using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;

namespace RetirementResearchReport;

internal static class SavePreflight
{
    [ThreadStatic] internal static Galaxy? Prepared;
    [ThreadStatic] static Scope? current;
    static readonly ConditionalWeakTable<DWGame, Notice> Notices = new();

    internal static void Install(Harmony harmony)
    {
        var save = AccessTools.DeclaredMethod(typeof(DWGame), "SaveGame", [typeof(string)]) ?? throw new MissingMethodException("DWGame.SaveGame");
        harmony.Patch(save, prefix: new HarmonyMethod(typeof(SavePreflight), nameof(BeforeGameSave))
                { priority = Priority.First, before = ["local.dw2.advanced-construction", "local.dw2.freight-dispatch"] },
            transpiler: new HarmonyMethod(typeof(SavePreflight), nameof(BeforeFileOpen)),
            finalizer: new HarmonyMethod(typeof(SavePreflight), nameof(FinishGameSave)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(UserInterfaceController), "Update"), postfix: new HarmonyMethod(typeof(SavePreflight), nameof(UpdateUi)));
    }
    static void BeforeGameSave(DWGame __instance, out Scope __state)
    {
        __state = new(__instance, current, Prepared); current = __state;
        if (__instance.Galaxy is not { } galaxy) return;
        // Verified beta PauseGame only stops stopwatches; it does not wait for workers.
        // Take the same RRR -> ACM order as construction, before peer save prefixes acquire ACM.
        __state.Gate = Entry.State(galaxy).Gate;
        Monitor.Enter(__state.Gate); __state.Held = true;
    }
    internal static IEnumerable<CodeInstruction> BeforeFileOpen(IEnumerable<CodeInstruction> instructions)
    {
        var input = instructions.ToList();
        var starts = input.Select((code, index) => (code, index)).Where(x => x.code.opcode == OpCodes.Ldsfld &&
            x.code.operand is FieldInfo field && field.DeclaringType?.FullName == "Stride.Core.IO.VirtualFileSystem" && field.Name == "ApplicationData" &&
            x.code.blocks.Any(b => b.blockType == ExceptionBlockType.BeginExceptionBlock)).Select(x => x.index).ToArray();
        if (starts.Length != 1 || starts[0] < 3 || input[starts[0] + 1].opcode != OpCodes.Ldarg_1 || input[starts[0] + 2].opcode != OpCodes.Ldc_I4_2)
            throw new NotSupportedException("Native SaveGame pre-open exception boundary changed");
        for (int i = 0; i < input.Count; i++)
        {
            if (i == starts[0])
            {
                var first = new CodeInstruction(OpCodes.Ldarg_0); first.labels.AddRange(input[i].labels); input[i].labels.Clear();
                yield return first; yield return CodeInstruction.Call(typeof(SavePreflight), nameof(GuardBeforeOpen));
                // Keep BeginException on the ORIGINAL instruction. Our typed refusal must escape its catch,
                // otherwise native SaveGame would return normally and run Shipset Colors' success postfix.
            }
            yield return input[i];
        }
    }
    internal static void GuardBeforeOpen(DWGame game)
    {
        var scope = current;
        if (scope == null || !ReferenceEquals(scope.Game, game)) throw new InvalidOperationException("Retirement report save scope missing");
        var galaxy = game.Galaxy; if (galaxy == null) return;
        try
        {
            var state = Entry.State(galaxy);
            if (!scope.Held || !ReferenceEquals(scope.Gate, state.Gate)) throw new InvalidOperationException("Retirement report save galaxy changed during preparation");
            Entry.Flush(galaxy, state); Prepared = galaxy;
        }
        catch (Exception ex)
        {
            scope.Release();
            Notices.GetOrCreateValue(game).Text = "Retirement Research Report: save blocked before opening the file; previous save preserved. " + ex.Message;
            Console.Error.WriteLine("Retirement Research Report: save rejected before file open: " + ex.Message);
            Galaxy.AllowNewTasks = true; // The native SaveGame epilogue is skipped on this typed refusal.
            throw new Rejected(ex);
        }
    }
    static Exception? FinishGameSave(Exception? __exception, Scope? __state)
    { __state?.Release(); return __exception is Rejected ? null : __exception; }
    static void UpdateUi()
    {
        try
        {
            if (Galaxy.Game is not DWGame game || game.Status != GameStatus.Running || !Notices.TryGetValue(game, out var notice) || notice.Text == null) return;
            if (AccessTools.Field(typeof(DWGame), "_Renderer").GetValue(game) is not ScaledRenderer renderer) return;
            renderer.SetBriefMessage(notice.Text, 20f); notice.Text = null;
        }
        catch (Exception ex) { Console.Error.WriteLine("Retirement Research Report: save notice deferred: " + ex.Message); }
    }
    internal sealed class Scope(DWGame game, Scope? previous, Galaxy? previousPrepared)
    {
        internal readonly DWGame Game = game;
        internal object? Gate;
        internal bool Held;
        bool released;
        internal void Release()
        {
            if (released) return; released = true;
            Prepared = previousPrepared; current = previous;
            if (Held) { Held = false; Monitor.Exit(Gate!); }
        }
    }
    sealed class Rejected(Exception cause) : Exception("Retirement report rejected save before file open", cause);
    sealed class Notice { internal volatile string? Text; }
}
