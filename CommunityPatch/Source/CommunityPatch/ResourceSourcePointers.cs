using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace CommunityPatch;

internal static class ResourceSourcePointers
{
    private const string HarmonyId = "DW2.CommunityPatch.ResourceSourcePointers";
    private static readonly FieldInfo KnownCounts = AccessTools.Field(typeof(Empire), nameof(Empire.ResourcesKnown));
    private static readonly FieldInfo ResourceId = AccessTools.Field(typeof(Resource), nameof(Resource.ResourceId));
    private static readonly MethodInfo Amount = AccessTools.Method(typeof(ResourceQuantityList), nameof(ResourceQuantityList.GetAmount), [typeof(short)]);
    private static readonly MethodInfo SurveyedCount = AccessTools.Method(typeof(ResourceSourcePointers), nameof(CountSurveyed));
    private static readonly Func<DWControl, Point, HoverTip?> CheckHoveredTip =
        AccessTools.MethodDelegate<Func<DWControl, Point, HoverTip?>>(AccessTools.Method(typeof(DWControl), "CheckHoveredTip", [typeof(Point)]));
    private static readonly AccessTools.FieldRef<Viewport> ViewportRef =
        AccessTools.StaticFieldRefAccess<Viewport>(AccessTools.Field(typeof(DWRendererBase), "Viewport"));
    private static readonly AccessTools.FieldRef<Point> MousePointRef =
        AccessTools.StaticFieldRefAccess<Point>(AccessTools.Field(typeof(DWRendererBase), "_MousePoint"));

    // The resource pane draws each row every frame. Reuse one campaign-local survey snapshot.
    private static (Empire Empire, Galaxy Galaxy, long At, Dictionary<short, List<Orb>> Sources)? snapshot;
    private static readonly Color ArrowColor = new(109, 217, 255, 225);

    internal static void Init()
    {
        var row = typeof(Resource).GetInterfaceMap(typeof(IListDrawable)).TargetMethods.Single(m => m.Name.EndsWith(".DrawItem", StringComparison.Ordinal));
        var callouts = AccessTools.Method(typeof(DWRendererBase), "DrawMapCallouts",
            [typeof(SpriteBatch), typeof(CameraInfo), typeof(int), typeof(int), typeof(float)])
            ?? throw new MissingMethodException(nameof(DWRendererBase), "DrawMapCallouts");
        var harmony = new Harmony(HarmonyId);
        try
        {
            harmony.Patch(row, transpiler: new HarmonyMethod(typeof(ResourceSourcePointers), nameof(ReplaceKnownCount)));
            harmony.Patch(callouts, postfix: new HarmonyMethod(typeof(ResourceSourcePointers), nameof(DrawPointers)));
        }
        catch
        {
            harmony.UnpatchAll(HarmonyId);
            throw;
        }
    }

    private static IEnumerable<CodeInstruction> ReplaceKnownCount(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        int index = Enumerable.Range(0, codes.Count - 3).Single(i => codes[i].LoadsField(KnownCounts) &&
            codes[i + 1].opcode == OpCodes.Ldarg_0 && codes[i + 2].LoadsField(ResourceId) && codes[i + 3].Calls(Amount));
        // The Empire stays on the stack instead of its unfiltered Known Locations list.
        codes[index].opcode = OpCodes.Nop;
        codes[index].operand = null;
        codes[index + 3].opcode = OpCodes.Call;
        codes[index + 3].operand = SurveyedCount;
        return codes;
    }

    internal static float CountSurveyed(Empire empire, short resourceId)
        => empire.IsPlayer ? FindSources(empire, resourceId).Count : empire.ResourcesKnown.GetAmount(resourceId);

    internal static IReadOnlyList<Orb> FindSources(Empire? empire, short resourceId)
    {
        if (empire?.Galaxy is not { } galaxy || empire.ExplorationMap == null) return Array.Empty<Orb>();
        long now = Stopwatch.GetTimestamp();
        if (snapshot is not { } cached || !ReferenceEquals(cached.Empire, empire) || !ReferenceEquals(cached.Galaxy, galaxy) ||
            Stopwatch.GetElapsedTime(cached.At, now) >= TimeSpan.FromSeconds(1))
        {
            var sources = new Dictionary<short, List<Orb>>();
            foreach (var orb in galaxy.Orbs)
            {
                if (orb?.Resources == null) continue;
                byte level = empire.ExplorationMap.CheckExplorationLevelSafely(orb.OrbId, 0);
                if (level == 0) continue;
                foreach (var deposit in orb.Resources)
                {
                    if (deposit.Abundance <= 0 || level < deposit.DiscoveryLevel) continue;
                    if (!sources.TryGetValue(deposit.ResourceId, out var bodies))
                        sources.Add(deposit.ResourceId, bodies = []);
                    if (bodies.Count == 0 || bodies[^1] != orb) bodies.Add(orb);
                }
            }

            snapshot = cached = (empire, galaxy, now, sources);
        }

        return cached.Sources.TryGetValue(resourceId, out var list) ? list : Array.Empty<Orb>();
    }

    internal static HoverTip? FindSourceHover(ScrollableList? list, Point mouse)
    {
        if (list is not { Visible: true, HoveredItem: Resource }) return null;
        var tip = CheckHoveredTip(list, mouse);
        var explanation = TextResolver.GetText("Known Locations Explanation");
        return !string.IsNullOrEmpty(explanation) && tip?.Text?.Contains(explanation, StringComparison.Ordinal) == true ? tip : null;
    }

    private static void DrawPointers(SpriteBatch spriteBatch, CameraInfo camera, int originOffsetX, int originOffsetY,
        float coordinateDivisor, Empire ____PlayerEmpire, Vector3 ___ViewPosition)
    {
        var list = UserInterfaceController.EmpireSectionList?.ActiveSection?.ActiveSubSection?.ListItems;
        if (list?.HoveredItem is not Resource resource || !ReferenceEquals(UserInterfaceController.HoveredControl, list)) return;
        var tip = FindSourceHover(list, MousePointRef());
        if (tip == null) return;

        var cell = tip.Rectangle;
        Vector2 start = new(cell.Right + 3, cell.Top + cell.Height * .5f);
        var viewport = ViewportRef();
        foreach (var orb in FindSources(____PlayerEmpire, resource.ResourceId))
        {
            var world = Calculations.CalculateViewPosition(orb.GetPositionRaw(), orb.GalaxyX, orb.GalaxyY,
                originOffsetX, originOffsetY, coordinateDivisor) - ___ViewPosition;
            var point = viewport.Project(world, camera.Projection, camera.View, Matrix.Identity);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.Z > 1.000089f) continue;
            DrawArrow(spriteBatch, start, new Vector2(point.X, point.Y), viewport);
        }
    }

    private static void DrawArrow(SpriteBatch batch, Vector2 start, Vector2 target, Viewport viewport)
    {
        var delta = target - start;
        float length = delta.Length();
        if (length < 10f) return;
        var direction = delta / length;
        // Point to the edge when the source is outside the current camera view.
        float edgeX = delta.X > 0 ? viewport.X + viewport.Width - 8 : viewport.X + 8;
        float edgeY = delta.Y > 0 ? viewport.Y + viewport.Height - 8 : viewport.Y + 8;
        float scale = Math.Min(1f, Math.Min(delta.X == 0 ? 1f : (edgeX - start.X) / delta.X,
            delta.Y == 0 ? 1f : (edgeY - start.Y) / delta.Y));
        if (scale * length < 10f) return;

        Vector2 tip = start + delta * scale;
        Vector2 tail = tip - direction * 9f;
        Vector2 facing = new(-direction.Y * 4f, direction.X * 4f);
        DrawingHelper.DrawLine(batch, start.X, start.Y, tip.X, tip.Y, ArrowColor, 1.5f);
        DrawingHelper.DrawLine(batch, tail.X + facing.X, tail.Y + facing.Y, tip.X, tip.Y, ArrowColor, 2);
        DrawingHelper.DrawLine(batch, tail.X - facing.X, tail.Y - facing.Y, tip.X, tip.Y, ArrowColor, 2);
    }
}
