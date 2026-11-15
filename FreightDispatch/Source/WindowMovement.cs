using DistantWorlds.Types;
using DistantWorlds.UI;
using LocalModUi;
using Stride.Core.Mathematics;

namespace FreightDispatch;

sealed partial class Panel
{
    bool positionLoaded, dragging;
    Point dragOrigin;
    Vector2 dragPosition;
    internal bool Dragging => dragging;

    void InitializeMovement()
    {
        var header = new DragHandle(this) { Name = "FD drag handle", Text = "", Visible = true, PostScene = true,
            ShowTexture = false, ShowTextureHovered = false, ButtonColor = Color.Transparent, ButtonColorHover = Color.Transparent };
        header.Initialize(FontSize.Small, FontSize.Small, null!);
        header.SetSizeAndPosition(new Vector2(172, 32), Vector2.Zero); AddControl(header);
    }

    void Place()
    {
        var size = new Vector2(Math.Max(1, Math.Min(644, UserInterfaceController.ScreenWidth - 24)), Math.Max(1, Math.Min(600, UserInterfaceController.ScreenHeight - 24)));
        Vector2 position;
        if (!positionLoaded)
        {
            positionLoaded = true;
            if (!WindowPreferences.TryLoad(out position)) position = InitialPosition(size);
        }
        else position = Position;
        position = ClampPosition(position, size);
        if (Position != position || Size != size) SetSizeAndPosition(size, position);
    }
    static Vector2 InitialPosition(Vector2 size)
    {
        float width = UserInterfaceController.ScreenWidth, height = UserInterfaceController.ScreenHeight;
        if (MapUiGate.Displayed(UserInterfaceController.SelectionPanel))
        {
            var column = UserInterfaceController.SelectionPanel.CalculateAbsoluteRectangle();
            if (column.Left < width / 2 && column.Right + size.X + 24 <= width)
                return new Vector2(column.Right + 12, column.Top);
        }
        return new Vector2(Math.Max(12, (width - size.X) / 2), Math.Max(12, height - size.Y - 160));
    }
    static Vector2 ClampPosition(Vector2 position, Vector2 size) => new(
        Math.Clamp(position.X, 12, Math.Max(12, UserInterfaceController.ScreenWidth - size.X - 12)),
        Math.Clamp(position.Y, 12, Math.Max(12, UserInterfaceController.ScreenHeight - size.Y - 12)));

    bool BeginDrag(Point point, bool left)
    {
        if (!left || !CanInteract || dragging) return false;
        ReleaseInput(); dragOrigin = point; dragPosition = Position; dragging = true; return true;
    }
    bool MoveDrag(Point point, bool left)
    {
        if (!dragging) return false;
        if (!left || !CanInteract) { dragging = false; return true; }
        var position = ClampPosition(dragPosition + new Vector2(point.X - dragOrigin.X, point.Y - dragOrigin.Y), Size);
        if (Position != position) SetSizeAndPosition(Size, position);
        return true;
    }
    bool EndDrag(bool left)
    {
        if (!dragging) return false;
        dragging = false;
        if (left && CanInteract && Position != dragPosition) WindowPreferences.Save(Position);
        return true;
    }
    sealed class DragHandle(Panel panel) : DWButton
    {
        public override bool DoMouseDown(Point p, bool left, Empire e, bool shift, bool ctrl, bool child) =>
            panel.CanInteract && CheckVisible() && CalculateAbsoluteRectangle().Contains(p) && !child && panel.BeginDrag(p, left);
        public override bool DoMouseUp(Point p, bool left, Empire e, bool shift, bool ctrl, float time) => panel.EndDrag(left);
        public override bool DoMouseMove(Vector2 move, Point p, bool left, bool right) => panel.MoveDrag(p, left);
    }
}
