using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using System.Text.Json;

namespace AdvancedConstructionMod;

internal static class DesignsColumnResize
{
    private sealed record Drag(DesignsSeriesColumn.Layout Layout,DWButtonStrip Strip,int Edge,int StartX,float Left,float Right);
    private static Drag? drag;
    private static (DWButtonStrip? Strip,int Edge,long Time,int X) last;
    private static string PathName => Path.Combine(Environment.GetEnvironmentVariable("DW2_TEST_OUTPUT_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdvancedConstructionMod","designs-columns.json");

    internal static void Install(Harmony h)
    {
        foreach(var (name,handler) in new[]{("DoMouseDown",nameof(Down)),("DoMouseMove",nameof(Move)),("DoMouseUp",nameof(Up))})
            h.Patch(AccessTools.DeclaredMethod(typeof(UserInterfaceController),name),prefix:new HarmonyMethod(typeof(DesignsColumnResize),handler));
    }
    private static bool Down(Point clickPoint,bool isLeftClick,ref bool __result)
    {
        if(!isLeftClick)return true;
        drag=null;
        if(UserInterfaceController.HoveredControl is not DWButton {Parent:DWButtonStrip strip} ||
            !DesignsSeriesColumn.HeaderLayout(strip,out var layout))return true;
        var fields=layout!.HitFields;
        foreach(var button in strip.Controls.OfType<DWButton>())
        {
            if(button.ExtraClickData is not string key)continue;
            int index=fields.FindIndex(f=>f.Name==key);
            if(index<0 || index+1>=fields.Count || !fields[index+1].ShowColumnHeader)continue;
            var r=button.CalculateAbsoluteRectangle();
            if(clickPoint.Y<r.Top || clickPoint.Y>=r.Bottom || Math.Abs(clickPoint.X-r.Right)>UserInterfaceHelper.CalculateScaledValue(4))continue;
            long now=Environment.TickCount64;
            bool reset=last.Strip==strip && last.Edge==index && now-last.Time<=500 && Math.Abs(last.X-clickPoint.X)<=4;
            if(reset)layout.ResetWidths();
            drag=new(layout,strip,index,clickPoint.X,fields[index].Size,fields[index+1].Size);
            last=reset?default:(strip,index,now,clickPoint.X);
            __result=true;return false;
        }
        return true;
    }
    private static bool Move(Point mousePosition,bool leftMouseButtonDown,ref bool __result)
    {
        if(drag is not { } d)return true;
        if(!leftMouseButtonDown || !d.Layout.Visible){drag=null;return true;}
        Resize(d,mousePosition.X);
        __result=true;return false;
    }
    private static void Resize(Drag d,int x)
    {
        float scale=UserInterfaceHelper.CalculateScaledValue(100f)/100f;
        float delta=Math.Clamp((x-d.StartX)/scale,32-d.Left,d.Right-32);
        d.Layout.HitFields[d.Edge].Size=d.Left+delta;
        d.Layout.HitFields[d.Edge+1].Size=d.Right-delta;
        d.Layout.SyncWidths();
        d.Layout.ArrangeHeader(d.Strip);
        if(Math.Abs(x-d.StartX)>4)last=default;
    }
    private static bool Up(Point clickPoint,bool isLeftClick,ref bool __result)
    {
        if(!isLeftClick || drag is not { } d)return true;
        drag=null;
        if(d.Layout.Visible){Resize(d,clickPoint.X);Save(d.Layout.HitFields);}
        __result=true;return false;
    }
    internal static void Restore(ListDrawableFieldList fields)
    {
        try
        {
            if(!File.Exists(PathName))return;
            var saved=JsonSerializer.Deserialize<Dictionary<string,float>>(File.ReadAllText(PathName));
            var visible=fields.Where(f=>f.ShowColumnHeader).ToArray();
            if(saved==null || saved.Count!=visible.Length || visible.Any(f=>!saved.TryGetValue(f.Name,out var w)||!float.IsFinite(w)||w<32) ||
                Math.Abs(visible.Sum(f=>f.Size-saved[f.Name]))>1)return;
            foreach(var f in visible)f.Size=saved[f.Name];
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){System.Diagnostics.Trace.TraceWarning(e.Message);}
    }
    private static void Save(ListDrawableFieldList fields)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            string temp=PathName+"."+Environment.ProcessId+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(fields.Where(f=>f.ShowColumnHeader).ToDictionary(f=>f.Name,f=>f.Size)));
            File.Move(temp,PathName,true);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){System.Diagnostics.Trace.TraceWarning(e.Message);}
    }
}
