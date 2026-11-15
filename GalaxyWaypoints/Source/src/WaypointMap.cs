using System.Runtime.CompilerServices;
using System.Reflection;
using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Input;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static readonly ConditionalWeakTable<GraphicsDevice, WaypointSprites> MapSprites = new();
    private sealed record MapSelection(Waypoint Point);

    private static bool MapOverlayUiActive() => LocalModUi.MapUiGate.Unobstructed("GalaxyWaypointsToolMenu");
    private static bool WaypointMenuContains(Point point) => waypointToolMenu is { Visible: true } menu && Contains(menu.CalculateAbsoluteRectangle(), point);
    private static bool WaypointMenuIntersects(RectangleF rectangle)
    {
        if (waypointToolMenu is not { Visible: true } menu) return false;
        var bounds = menu.CalculateAbsoluteRectangle();
        return rectangle.Left < bounds.Right && rectangle.Right > bounds.Left && rectangle.Top < bounds.Bottom && rectangle.Bottom > bounds.Top;
    }
    private static bool NativeMapPanelIntersects(RectangleF rectangle)
    {
        foreach (var control in new DWControl?[] { UserInterfaceController.SelectionPanel, UserInterfaceController.MessageListView })
            if (control is { Visible: true } && control.CheckVisible())
            {
                var bounds = control.CalculateAbsoluteRectangle();
                if (rectangle.Left < bounds.Right && rectangle.Right > bounds.Left && rectangle.Top < bounds.Bottom && rectangle.Bottom > bounds.Top) return true;
            }
        return false;
    }
    private static bool WaypointMarkerCovered(Vector2 point)
    {
        var bounds = new RectangleF(point.X - 20, point.Y - 20, 40, 40);
        return WaypointMenuIntersects(bounds) || NativeMapPanelIntersects(bounds);
    }
    private static bool WaypointMapActive(ScaledRenderer renderer) => renderer.SceneRenderType == SceneRenderType.Galaxy && MapOverlayUiActive();

    private static bool ClickWaypoint(ScaledRenderer __instance, DWGame ____Game,
        Point mousePoint, bool isLeftButtonClicked, bool shiftDown, bool ctrlDown)
    {
        try
        {
            if (WaypointMenuContains(mousePoint) || NativeMapPanelIntersects(new(mousePoint.X, mousePoint.Y, 1, 1)) || ____Game?.Galaxy is not { } galaxy ||
                !WaypointMapActive(__instance) || __instance.CurrentCamera is not { } camera ||
                !Campaigns.TryGetValue(galaxy, out Campaign? campaign) ||
                MapProjection.FromMap(__instance, camera, __instance.Width, __instance.Height) is not { } map)
                return true;

            if (campaign.RoutePicker != 0)
            {
                if (isLeftButtonClicked && (shiftDown || ctrlDown)) return false;
                RoutePickerClick(campaign, isLeftButtonClicked ? FindSurveyStar(galaxy, map, mousePoint, __instance.Width, __instance.Height) : null, isLeftButtonClicked);
                return false;
            }
            if (!isLeftButtonClicked) return true;
            if (shiftDown) return !campaign.SurveyPicker && !campaign.TransitPicker;
            if (campaign.TransitPicker)
            {
                campaign.TransitPicker = false;
                var system = FindSurveyStar(galaxy, map, mousePoint, __instance.Width, __instance.Height);
                if (system == null) __instance.SetBriefMessage("Transit selection cancelled: no star selected.", 4);
                else
                {
                    campaign.TransitSelection = system;
                    __instance.SetBriefMessage("Transit rules selected. Open Galaxy Waypoints to edit roles.", 4);
                }
                return false;
            }
            if (campaign.SurveyPicker)
            {
                campaign.SurveyPicker = false;
                var system = FindSurveyStar(galaxy, map, mousePoint, __instance.Width, __instance.Height);
                if (system == null) __instance.SetBriefMessage("Survey priority cancelled: no star selected.", 4);
                else
                {
                    var added = campaign.Survey.Add(system.SystemId, true, FullySurveyed(campaign, system.SystemId));
                    __instance.SetBriefMessage(added switch
                    {
                        SurveyAddResult.Added => "Survey priority added. An available automatic explorer will take it after its current object.",
                        SurveyAddResult.AlreadyQueued => "This system already has a survey priority.",
                        SurveyAddResult.AlreadySurveyed => "This system is already fully surveyed.",
                        _ => "This star is unavailable for survey priority."
                    }, 6);
                }
                return false;
            }

            if (ctrlDown)
            {
                var input = ____Game.Input;
                if (input == null || (!input.IsKeyDown(Keys.LeftAlt) && !input.IsKeyDown(Keys.RightAlt)) ||
                    (uint)mousePoint.X >= (uint)__instance.Width || (uint)mousePoint.Y >= (uint)__instance.Height)
                    return true;

                ToggleWaypoint(__instance, galaxy, campaign, camera, map, mousePoint);
                return false;
            }

            if (campaign.DisplayMode == WaypointDisplayMode.Hidden) { campaign.Selection = null; return true; }

            // DEBUG selection is deliberately presentation-only.  DisplayPoints must never reach routing,
            // traffic, discovery, or the shared native path cache.
            Waypoint? hit = FindMarker(campaign.DisplayPoints, map, mousePoint);
            campaign.Selection = hit is { } point ? new MapSelection(point) : null;
            if (hit is { } focus && campaign.Analysis?.Focus != focus)
            {
                campaign.Analysis = new NetworkAnalysis(campaign, !campaign.ContainsActive(focus), focus);
                analysisPage = 0;
            }
            if (hit != null) OpenRouteCard(campaign, false);
            else if (campaign.RouteCardOpen && !campaign.RoutePairView) CloseWaypointMenu();
            return hit == null;
        }
        catch (Exception ex)
        {
            ReportError("Waypoint selection", ex);
            return true;
        }
    }

    private static StarSystem? FindSurveyStar(Galaxy galaxy, MapProjection map, Point mouse, int width, int height)
    {
        StarSystem? result = null;
        float closest = 18 * 18;
        // Native galaxy picking enumerates system stars without a survey/intel gate; names have a separate privacy gate.
        foreach (var system in galaxy.Systems.Values)
        {
            if (system?.Star == null || !map.TryProject(new(system.GalaxyX, system.GalaxyY), out var point) ||
                !OnMap(point, width, height) || WaypointMarkerCovered(point)) continue;
            float dx = point.X - mouse.X, dy = point.Y - mouse.Y, distance = dx * dx + dy * dy;
            if (distance < closest) { closest = distance; result = system; }
        }
        return result;
    }

    private static void ToggleWaypoint(ScaledRenderer renderer, Galaxy galaxy, Campaign campaign, CameraInfo camera,
        MapProjection map, Point mousePoint)
    {
        if (campaign.DisplayMode == WaypointDisplayMode.AllDebug &&
            FindMarker(campaign.DisplayPoints, map, mousePoint) is { } shown && !campaign.ContainsActive(shown))
        {
            campaign.Selection = null;
            renderer.SetBriefMessage("Diagnostic node only: visit its anchor system to discover it.", 4);
            return;
        }
        Point coordinates = renderer.ResolveGalaxyCoordinates(camera, mousePoint.X, mousePoint.Y);
        var point = new Waypoint(coordinates.X, coordinates.Y);
        Waypoint? existing = campaign.Points.Contains(point) ? point : FindMarker(campaign.Points, map, mousePoint);
        if (existing is { } remove)
            RemoveWaypoint(renderer, campaign, remove);
        else if (FindMarker(campaign.ActivePoints, map, mousePoint) is { } generated)
        {
            campaign.Selection = new MapSelection(generated);
            renderer.SetBriefMessage("Generated waypoint node", 3);
        }
        else if (campaign.Points.Length >= Campaign.MaxPoints)
            renderer.SetBriefMessage($"Waypoint limit reached. Remove one with {WaypointShortcut}.", 4);
        else if (Campaign.InBounds(galaxy, point))
        {
            campaign.SetPoints([.. campaign.Points, point]);
            campaign.Selection = new MapSelection(point);
            renderer.SetBriefMessage("Navigation waypoint added", 3);
            Log($"Added waypoint ({point.X},{point.Y}).");
        }
    }

    private static void RemoveWaypoint(ScaledRenderer renderer, Campaign campaign, Waypoint point)
    {
        campaign.SetPoints(campaign.Points.Where(item => item != point).ToArray());
        if (campaign.Selection?.Point == point)
            campaign.Selection = null;
        renderer.SetBriefMessage("Navigation waypoint removed", 3);
        Log($"Removed waypoint ({point.X},{point.Y}).");
    }

    private static void DrawWaypointOverlay(DWGame game, GraphicsContext graphics, int width, int height,
        MapProjection map, Campaign campaign, Galaxy galaxy)
    {
        if (campaign.DisplayMode == WaypointDisplayMode.Hidden && campaign.RoutePendingFrom == null) return;
        Waypoint? selected = campaign.Selection?.Point;
        Waypoint[] points = campaign.DisplayPoints;
        IReadOnlySet<int>? blockedSystems = campaign.Transit?.Systems;
        if (points.Length == 0 && campaign.Survey.Snapshot.Length == 0 && (blockedSystems == null || blockedSystems.Count == 0) && !campaign.RouteCardOpen && campaign.RoutePicker == 0)
            return;

        Waypoint[] analyzedNodes = RouteNodes(campaign, DisplayedRoute(campaign));
        var sprites = MapSprites.GetValue(game.GraphicsDevice, device => new WaypointSprites(device));
        SpriteBatch batch = sprites.Batch;
        batch.Begin(graphics, blendState: BlendStates.NonPremultiplied, depthStencilState: DepthStencilStates.None);
        try
        {
            if (campaign.RoutePicker == 2 && campaign.RoutePendingFrom is { } startId && galaxy.Systems.GetById(startId) is { } start &&
                map.TryProject(new(start.GalaxyX, start.GalaxyY), out var picked) && OnMap(picked, width, height) && !WaypointMarkerCovered(picked))
            {
                batch.Draw(sprites.Ring, new RectangleF(picked.X - 19, picked.Y - 19, 38, 38), new Color4(.2f, .9f, 1, 1));
                batch.DrawString(FontManager.SelectFont(13, false), "START", 13, picked + new Vector2(20, 12), new Color4(.2f, .9f, 1, 1));
            }
            if (campaign.DisplayMode == WaypointDisplayMode.Hidden) return;
            DrawAnalysisRoutes(batch, map, campaign, galaxy, width, height);
            foreach (var priority in campaign.Survey.Snapshot)
                if (galaxy.Systems.GetById(priority.SystemId) is { } system &&
                    map.TryProject(new(system.GalaxyX, system.GalaxyY), out var marker) && OnMap(marker, width, height) && !WaypointMarkerCovered(marker))
                {
                    batch.Draw(sprites.Ring, new RectangleF(marker.X - 23, marker.Y - 23, 46, 46), new Color4(.22f, .6f, 1, 1));
                    batch.DrawString(FontManager.SelectFont(12, false), "SURVEY", 12, marker + new Vector2(24, 9), new Color4(.4f, .75f, 1, 1));
                }
            if (blockedSystems != null)
            {
                foreach (int id in blockedSystems)
                    if (galaxy.Systems.TryGetValue(id, out StarSystem? system) &&
                        map.TryProject(new(system.GalaxyX, system.GalaxyY), out Vector2 marker) && OnMap(marker, width, height) && !WaypointMarkerCovered(marker))
                        batch.Draw(sprites.Ring, new RectangleF(marker.X - 16, marker.Y - 16, 32, 32), new Color4(1f, .31f, .28f, 1));
            }

            foreach (Waypoint point in points)
            {
                if (!map.TryProject(point, out Vector2 marker) || !OnMap(marker, width, height) || WaypointMarkerCovered(marker))
                    continue;

                if (!campaign.ContainsActive(point))
                {
                    if (campaign.DisplayMode != WaypointDisplayMode.AllDebug) continue;
                    batch.Draw(sprites.Ring, new RectangleF(marker.X - 10, marker.Y - 10, 20, 20), new Color4(.79f, .48f, 1f, .85f));
                    if (analyzedNodes?.Contains(point) == true)
                        batch.Draw(sprites.Ring, new RectangleF(marker.X - 15, marker.Y - 15, 30, 30), new Color4(1f, .72f, .18f, 1));
                    batch.DrawString(FontManager.SelectFont(12, isBold: false), NodeId(campaign, point) + " DEBUG", 12, marker + new Vector2(12, -8), new Color4(.79f, .48f, 1f, 1));
                    continue;
                }
                TrafficCount count = campaign.Traffic?.VisibleCount(point, galaxy.StarDate, passage => VisiblePassage(passage, galaxy, campaign.Observer)) ?? default;
                bool isSelected = point == selected;
                if (isSelected)
                {
                    batch.Draw(sprites.Ring, new RectangleF(marker.X - 14, marker.Y - 14, 28, 28), new Color4(.36f, .88f, 1f, 1f));
                }
                Color4 color = count.Passages > 0 ? new(.32f, .95f, .56f, 1) : new(.72f, .76f, .81f, 1);
                batch.Draw(sprites.Ring, new RectangleF(marker.X - 10, marker.Y - 10, 20, 20), color);
                batch.DrawString(FontManager.SelectFont(12, false), NodeId(campaign, point), 12, marker + new Vector2(12, -8), color);
                if (analyzedNodes?.Contains(point) == true)
                    batch.Draw(sprites.Ring, new RectangleF(marker.X - 15, marker.Y - 15, 30, 30), new Color4(1f, .72f, .18f, 1));
            }


        }
        finally
        {
            batch.End();
        }
    }

    private static bool OnMap(Vector2 marker, int width, int height) =>
        marker.X >= 8 && marker.Y >= 8 && marker.X < width - 8 && marker.Y < height - 8;

    private static bool VisiblePassage(Passage passage, Galaxy galaxy, Empire observer) =>
        passage.EmpireId == observer.EmpireId || passage.EmpireId >= 0 && galaxy.Ships.GetById(passage.ShipId) is { } ship &&
        observer.VisibilityMap?.DetermineVisibilityTypeForItemAllEmpires(ship, observer, galaxy, galaxy.StarDate)
        is VisibilityType.PartiallyVisible or VisibilityType.FullyVisible;

    private static RectangleF? CardAt(Vector2 marker, int width, int height, bool diagnostic = false)
    {
        float panelWidth = UserInterfaceHelper.CalculateScaledValue(320);
        float panelHeight = UserInterfaceHelper.CalculateScaledValue(200);
        if (!OnMap(marker, width, height) || width <= panelWidth + 16 || height <= panelHeight + 16)
            return null;
        float gap = Math.Max(24, UserInterfaceHelper.CalculateScaledValue(32));
        float x = Math.Clamp(marker.X, 8, width - panelWidth - 8);
        float y = Math.Clamp(marker.Y - 14, 8, height - panelHeight - 8);
        var markerBounds = new RectangleF(marker.X - 20, marker.Y - 20, 40, 40);
        Vector2[] positions = [new(marker.X + gap, y), new(marker.X - panelWidth - gap, y),
            new(x, marker.Y - panelHeight - gap), new(x, marker.Y + gap),
            new(8, 8), new(width - panelWidth - 8, 8), new(8, height - panelHeight - 8),
            new(width - panelWidth - 8, height - panelHeight - 8)];
        foreach (var position in positions)
        {
            var rectangle = new RectangleF(position.X, position.Y, panelWidth, panelHeight);
            if (rectangle.Left < 8 || rectangle.Top < 8 || rectangle.Right > width - 8 || rectangle.Bottom > height - 8) continue;
            if (rectangle.Left < markerBounds.Right && rectangle.Right > markerBounds.Left && rectangle.Top < markerBounds.Bottom && rectangle.Bottom > markerBounds.Top) continue;
            if (!NativeMapPanelIntersects(rectangle)) return rectangle;
        }
        return null;
    }

    private static bool Contains(RectangleF rectangle, Point point) => point.X >= rectangle.X && point.Y >= rectangle.Y &&
        point.X < rectangle.X + rectangle.Width && point.Y < rectangle.Y + rectangle.Height;

    private sealed class WaypointSprites
    {
        public readonly SpriteBatch Batch;
        public readonly Texture Ring;
        public readonly Texture Pixel;

        public WaypointSprites(GraphicsDevice device)
        {
            Batch = new SpriteBatch(device);
            Pixel = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.White });
            const int size = 32;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + .5f - size / 2f;
                    float dy = y + .5f - size / 2f;
                    float distance = MathF.Sqrt(dx * dx + dy * dy);
                    float alpha = Math.Clamp(2.35f - Math.Abs(distance - 12.4f), 0, 1);
                    pixels[y * size + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(alpha * 255));
                }
            }
            Ring = Texture.New2D(device, size, size, PixelFormat.R8G8B8A8_UNorm, pixels);
        }
    }
}
