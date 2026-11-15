using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

internal static partial class Checks
{
    private static int tooltipLeafCalls;
    private static bool TooltipGpuLeaf(SpriteFont font,ref Vector2 __result)
    {
        if(font==null)throw new InvalidOperationException("Tooltip reached GPU leaf without a font");
        tooltipLeafCalls++;__result=new(100,20);return false;
    }
    private static void CheckTooltip(HoverTip tip,string context)
    {
        var hooks=new Harmony("acm.check.tooltip-font-boundary");
        var leaf=AccessTools.GetDeclaredMethods(typeof(DrawingHelper)).Single(m=>m.Name=="DrawStringDropShadowWordWrapWithSize"&&m.GetParameters().Length==14);
        hooks.Patch(leaf,prefix:new HarmonyMethod(typeof(Checks),nameof(TooltipGpuLeaf)));
        try
        {
            tooltipLeafCalls=0;
            try
            {
                // Keep the real native wrapper's font dereference; replace only the GPU leaf.
                DrawingHelper.DrawStringDropShadowWordWrap(null!,tip.Title,tip.TitleFont,tip.TitleColor,tip.ShadowColor,0,0,320,TextAlignment.Left);
                DrawingHelper.DrawStringDropShadowWordWrap(null!,tip.Text,tip.TextFont,tip.TextColor,tip.ShadowColor,0,0,320,TextAlignment.Left);
            }
            catch(NullReferenceException ex){throw new InvalidOperationException("FAIL: "+context+" has an uninitialized font on the native tooltip draw path",ex);}
            Require(tooltipLeafCalls==2,context+" title/body reach the native drawing leaf with initialized fonts");
        }
        finally{hooks.UnpatchAll(hooks.Id);}
    }
}
