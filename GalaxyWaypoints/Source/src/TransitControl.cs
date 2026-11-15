using DistantWorlds.Types;
using DistantWorlds.UI;
using LocalModUi;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace GalaxyWaypoints;

public static partial class Entry
{
    static TransitControl? pressedTransitControl;
    static object? pressedTransitData;
    private static void ClearTransitPress() { pressedTransitControl = null; pressedTransitData = null; }
    private static bool TransitUiActive() => MapUiGate.TryGetContext(out _, out var galaxy) &&
        ReferenceEquals(toolGalaxy, galaxy) && toolObserver == galaxy.Empires.GetPlayer()?.EmpireId && NativeToolDock.BodyAvailable(NativeToolDock.Waypoints);

    private sealed class TransitControl : DWButton
    {
        bool Available
        {
            get
            {
                if (!TransitUiActive()) { ClearTransitPress(); MapUiGate.ReleaseHover(this); return false; }
                return CheckVisible() && (ExtraClickData is not StarSystem star || TryGetStarTransit(out _, out var selected, out _) && ReferenceEquals(star, selected));
            }
        }
        public override void Render(RenderDrawContext context, SpriteBatch batch, Point point, bool hoverTips)
        {
            if (Available) base.Render(context, batch, point, hoverTips);
        }
        public override bool DoMouseDown(Point point, bool left, Empire empire, bool shift, bool ctrl, bool fromChild)
        {
            if (!Available || !CalculateAbsoluteRectangle().Contains(point)) { ClearTransitPress(); return false; }
            if (left && Enabled && !fromChild) { pressedTransitControl = this; pressedTransitData = ExtraClickData; }
            return base.DoMouseDown(point, left, empire, shift, ctrl, fromChild);
        }
        public override bool DoMouseUp(Point point, bool left, Empire empire, bool shift, bool ctrl, float held)
        {
            var owns = pressedTransitControl == this && Equals(pressedTransitData, ExtraClickData); ClearTransitPress();
            if (!Available || !CalculateAbsoluteRectangle().Contains(point)) return false;
            return !left || !owns || !Enabled || base.DoMouseUp(point, left, empire, shift, ctrl, held);
        }
        public override bool DoMouseMove(Vector2 movement, Point point, bool left, bool right) => Available && base.DoMouseMove(movement, point, left, right);
    }
}
