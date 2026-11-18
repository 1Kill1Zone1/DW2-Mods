using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

// Test-only metrics; native controls/callbacks run, GPU font loading and rendering do not.
internal sealed class FontMetrics:IDisposable
{
    private readonly Harmony hooks=new("acm.check.headless-font-metrics."+Guid.NewGuid().ToString("N"));
    private static SpriteFont font=null!;
    private readonly float priorLineHeight=UserInterfaceHelper.LineHeightNormal;
    internal FontMetrics()
    {
        font=(SpriteFont)Activator.CreateInstance(typeof(SpriteFont),true)!;GC.SuppressFinalize(font);
        AccessTools.Field(typeof(SpriteFont),"<Size>k__BackingField").SetValue(font,16f);AccessTools.Field(typeof(SpriteFont),"DefaultLineSpacing").SetValue(font,20f);
        UserInterfaceHelper.LineHeightNormal=font.GetTotalLineSpacing(font.Size);
        foreach(var type in new[]{typeof(UserInterfaceHelper),typeof(FontManager)})
        foreach(var method in AccessTools.GetDeclaredMethods(type).Where(m=>m.Name=="SelectFont"&&m.ReturnType==typeof(SpriteFont)))
            hooks.Patch(method,prefix:new HarmonyMethod(typeof(FontMetrics),nameof(Select)));
        foreach(var method in AccessTools.GetDeclaredMethods(typeof(SpriteFont)).Where(m=>m.Name=="MeasureString"&&m.ReturnType==typeof(Vector2)))
            hooks.Patch(method,prefix:new HarmonyMethod(typeof(FontMetrics),nameof(Measure)));
    }
    private static bool Select(ref SpriteFont __result){__result=font;return false;}
    private static bool Measure(object[] __args,ref Vector2 __result)
    {string text=__args[0]?.ToString()??"";__result=new(Math.Max(0,text.Split('\n').Max(s=>s.Length))*8,Math.Max(1,text.Split('\n').Length)*20);return false;}
    public void Dispose(){hooks.UnpatchAll(hooks.Id);UserInterfaceHelper.LineHeightNormal=priorLineHeight;}
}
