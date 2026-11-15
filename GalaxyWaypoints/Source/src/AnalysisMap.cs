using DistantWorlds.Types;
using DistantWorlds.UI;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static void DrawAnalysisRoutes(SpriteBatch batch, MapProjection map, Campaign campaign, Galaxy galaxy, int width, int height)
    {
        if (!campaign.RouteCardOpen) return;
        var view = DisplayedRoute(campaign);
        if (view != null)
        {
            DrawMapPath(batch, map, view.Before, new Color4(.9f, .72f, .3f, .9f), width, height, dashed: true);
            DrawMapPath(batch, map, view.After, new Color4(.2f, .85f, 1, 1), width, height, dashed: false);
            foreach (int id in new[] { view.From, view.To }) Label(id, new Color4(.5f, .9f, 1, 1));
        }
        var closed = ClosedRouteNodes(campaign, view).ToList();
        if (view == null && campaign.Selection is { } selected && !campaign.ContainsActive(selected.Point) && campaign.FindGenerated(selected.Point) is { } selectedNode)
            closed.Add(selectedNode);
        foreach (var node in closed)
            if (galaxy.Systems.GetById(node.AnchorSystemId) is { } anchor)
                DrawMapPath(batch, map, [node.Point, new(anchor.GalaxyX, anchor.GalaxyY)], new Color4(.8f, .5f, 1, .9f), width, height, dashed: true);
        foreach (int id in closed.Select(n => n.AnchorSystemId).Distinct()) Label(id, new Color4(.8f, .5f, 1, 1));
        void Label(int id, Color4 color)
        {
            if (galaxy.Systems.GetById(id) is { } system && map.TryProject(new(system.GalaxyX, system.GalaxyY), out var p) && OnMap(p, width, height) && !MapHudCovered(p))
                batch.DrawString(FontManager.SelectFont(13, false), SurveySystemLabel(campaign, id) + $" [S-{id:X4}]", 13, p + new Vector2(16, 18), color);
        }
    }

    private static string[] SelectedBenefitLines(Campaign campaign,Waypoint selected)
    {
        return RouteCardLines(campaign);
    }

    private static bool MapHudCovered(Vector2 p)
    {
        if (WaypointMarkerCovered(p)) return true;
        // Native panels are still visible when map overlays are permitted. Never paint across their contents.
        foreach (var control in new DWControl?[] { UserInterfaceController.SelectionPanel, UserInterfaceController.MessageListView })
            if (control is { Visible: true } && control.CheckVisible() && Contains(control.CalculateAbsoluteRectangle(), new Point((int)p.X, (int)p.Y))) return true;
        return false;
    }

    private static void DrawMapPath(SpriteBatch batch, MapProjection map, Waypoint[] path, Color4 color, int width, int height, bool dashed)
    {
        for (int i = 1; i < path.Length; i++)
        {
            if (!map.TryProject(path[i - 1], out var a) || !map.TryProject(path[i], out var b)) continue;
            if (!MapProjection.ClipSegment(ref a, ref b, width, height)) continue;
            float length = Vector2.Distance(a, b);
            if (!float.IsFinite(length) || length <= .01f) continue;
            int pieces = Math.Min(2048, Math.Max(1, (int)Math.Ceiling(length / 8)));
            for (int j = 0; j < pieces; j++)
            {
                if (dashed && j % 2 == 1) continue;
                var from = Vector2.Lerp(a, b, (float)j / pieces);
                var to = Vector2.Lerp(a, b, (float)(j + 1) / pieces);
                if (!OnMap(from, width, height) || !OnMap(to, width, height) || MapHudCovered(from) || MapHudCovered(to)) continue;
                DrawingHelper.DrawLine(batch, (int)from.X, (int)from.Y, (int)to.X, (int)to.Y, new Color(color), dashed ? 1.5f : 2.5f);
            }
        }
    }
}
