using DistantWorlds.Types;
using DistantWorlds.UI;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace LocalModUi;

// Keep the existing button event/background hit testing; let the stock DWPanel draw its frame/header.
internal class NativeToolWindow : DWButton
{
    readonly DWPanel frame;
    protected NativeToolWindow(string title, bool opaque = false)
    {
        ShowTexture = ShowTextureHovered = false;
        ButtonColor = ButtonColorHover = Color.Transparent;
        var background = ColorHelper.GalactopediaPanelBackColor;
        if (opaque) background.A = byte.MaxValue;
        frame = new DWPanel { HeaderText = title, UseRenderedBorder = true, UseOverlay = true, BackColor = background };
        frame.Initialize(FontSize.Small, FontSize.Small, UserInterfaceController.SelectionPanel?.TilingBackground!);
        frame.SetFeatureDefaults();
        frame.HeaderHeight = Math.Max(28, UserInterfaceHelper.CalculateScaledValue(30));
        frame.FontHeader = UserInterfaceHelper.SelectFont(FontSize.Normal, false); frame.FontHeaderBold = UserInterfaceHelper.SelectFont(FontSize.Normal, true);
    }
    internal void RenderNativeFrame(RenderDrawContext context, SpriteBatch batch, Point point)
    {
        frame.Layer = Layer; frame.PostScene = PostScene; frame.Visible = Visible;
        if (frame.Position != Position || frame.Size != Size) frame.SetSizeAndPosition(Size, Position);
        frame.Render(context, batch, point, false);
    }
    public override void Render(RenderDrawContext context, SpriteBatch batch, Point point, bool tips)
    {
        RenderNativeFrame(context, batch, point); base.Render(context, batch, point, tips);
    }
}
