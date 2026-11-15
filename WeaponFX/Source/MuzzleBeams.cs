using System.Reflection;
using System.Runtime.CompilerServices;
using DistantWorlds.Core;
using DistantWorlds.Types;
using DistantWorlds2;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal static class MuzzleBeams
{
    private sealed record Batch(int Start, int Expected, Vector3[] Points);
    private static FieldInfo _building=null!, _count=null!, _tail=null!, _extra=null!;
    internal static void Install(Harmony harmony, Assembly? rve)
    {
        harmony.Patch(AccessTools.Method(typeof(BillboardSystem),"GenerateBillboardVerticesRaw"),
            postfix:new HarmonyMethod(typeof(MuzzleBeams),nameof(Native)));
        if(rve==null)return;
        var beam=rve.GetType("DW2R_Visual_Effects.Patches.BeamCore",true)!;
        _building=AccessTools.Field(beam,"_building");_count=AccessTools.Field(beam,"_buildingCount");
        var vertex=_building.FieldType.GetElementType()!;_tail=AccessTools.Field(vertex,"Tail");_extra=AccessTools.Field(vertex,"Extra");
        harmony.Patch(AccessTools.Method(beam,"Add"),prefix:new HarmonyMethod(typeof(MuzzleBeams),nameof(Begin)),postfix:new HarmonyMethod(typeof(MuzzleBeams),nameof(End)));
    }
    private static void Begin(WeaponBlast blast, Vector3 tail, Vector3 tip, bool orb, ref Batch? __state)
    {
        if(!MuzzleAnchors.TryBeam(blast,tail,tip,out var points))return;
        var start=(int)_count.GetValue(null)!;var capacity=((Array)_building.GetValue(null)!).Length/4;
        if(start+points.Length+(orb?1:0)>Math.Min(4096,capacity))return;
        __state=new Batch(start,orb?2:1,points);
    }
    private static void End(Batch? __state)
    {
        if(__state==null)return;
        var count=(int)_count.GetValue(null)!;if(count!=__state.Start+__state.Expected)return;
        var vertices=(Array)_building.GetValue(null)!;
        var original=new object[4];for(var i=0;i<4;i++)original[i]=vertices.GetValue(__state.Start*4+i)!;
        for(var n=0;n<__state.Points.Length;n++)
        {
            var slot=n==0?__state.Start:count++;
            for(var i=0;i<4;i++)
            {
                var value=RuntimeHelpers.GetObjectValue(original[i]);_tail.SetValue(value,__state.Points[n]);
                var extra=(Vector4)_extra.GetValue(value)!;
                if(extra.W>=100)extra.W=100+(extra.W-100)%(16*1025); // one strand per real barrel; keep core/halo/profile.
                _extra.SetValue(value,extra);vertices.SetValue(value,slot*4+i);
            }
        }
        _count.SetValue(null,count);
    }
    private static void Native(BillboardRenderItemList billboards, RentingList<VertexBillboard> vertices, RentingList<int> indices, Galaxy ____Galaxy, int ____LocationId)
    {
        if(Entry.Bypass || ____Galaxy?.Locations?.GetById(____LocationId) is not { } location)return;
        var cursor=0;
        foreach(var item in billboards.GetReadOnlySpan())
        {
            if(item==null)continue;
            var start=cursor;cursor+=4;
            if(start+3>=vertices.Count || item.ParentType!=ItemType.WeaponBlast || !item.UseDirectionalComponent ||
                item.Parent.Resolve(location.WeaponBlasts) is not { FireType:WeaponFireType.FullLengthBeam } blast)continue;
            var first=vertices[start];
            // RVE hides handled native beams; do not revive suppressed or degenerate quads.
            if(first.Alpha<=0 || first.Scale.Y<=0 || first.Direction.LengthSquared()<.000001f)continue;
            var direction=Vector3.Normalize(first.Direction);var position=first.Position.XYZ();
            var tip=position+direction*first.Scale.Y;
            if(!MuzzleAnchors.TryBeam(blast,position-direction*first.Scale.Y,tip,out var points))continue;
            var original=new[]{vertices[start],vertices[start+1],vertices[start+2],vertices[start+3]};
            for(var n=0;n<points.Length;n++)
            {
                var delta=tip-points[n];var length=delta.Length();if(!float.IsFinite(length)||length<.001f)continue;
                var target=n==0?start:vertices.Count;
                for(var i=0;i<4;i++)
                {
                    var value=original[i];value.Position=new Vector4((tip+points[n])*.5f,1);value.Direction=delta/length;value.Scale.Y=length*.5f;
                    if(n==0)vertices[target+i]=value;else vertices.Add(value);
                }
                if(n>0) foreach(var index in new[]{0,1,2,0,3,1})indices.Add(target+index);
            }
        }
    }
}
