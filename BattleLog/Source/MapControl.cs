using DistantWorlds.Types;
using DistantWorlds.UI;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace BattleLog;

// Native DWButton fires on mouse-up without owning a press. Guard both the
// draw and event paths, including a dialog opened after the last UI update.
class MapControl : DWButton
{
    static MapControl? pressed;
    internal static void ClearPress() => pressed = null;
    bool Available
    {
        get
        {
            if (!Entry.UiAvailable()) { ClearPress(); return false; }
            return CheckVisible();
        }
    }
    public override void Render(RenderDrawContext context, SpriteBatch batch, Point mousePoint, bool renderHoverTips)
    {
        if (!Available) return;
        base.Render(context, batch, mousePoint, renderHoverTips);
    }
    public override bool DoMouseDown(Point point, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, bool callingFromChild)
    {
        if (!Available || !CalculateAbsoluteRectangle().Contains(point)) { ClearPress(); return false; }
        if (isLeftClick && Enabled && !callingFromChild) pressed = this;
        return base.DoMouseDown(point, isLeftClick, empire, shiftDown, ctrlDown, callingFromChild);
    }
    public override bool DoMouseUp(Point point, bool isLeftClick, Empire empire, bool shiftDown, bool ctrlDown, float timeDown)
    {
        var ownsPress = pressed == this; ClearPress();
        if (!Available || !CalculateAbsoluteRectangle().Contains(point)) return false;
        if (!isLeftClick || !ownsPress || !Enabled) return true;
        return base.DoMouseUp(point, isLeftClick, empire, shiftDown, ctrlDown, timeDown);
    }
    public override bool DoMouseMove(Vector2 movement, Point point, bool leftDown, bool rightDown)
    {
        if (!Available) return false;
        return base.DoMouseMove(movement, point, leftDown, rightDown);
    }
}
