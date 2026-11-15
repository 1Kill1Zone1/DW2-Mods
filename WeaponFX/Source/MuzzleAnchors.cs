using System.Reflection;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace WeaponFX;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal static class MuzzleAnchors
{
    private sealed record State(WeakReference<ModelComponent> Model, Design Design, int Location, string Path);
    private sealed record Launch(Vector3 Delta, float FirstDistance, float Fade);
    private static readonly ConditionalWeakTable<Ship, State> Models = new();
    private static readonly ConditionalWeakTable<WeaponBlast, Launch> Launches = new();
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.UpdateExteriorModelComponentBays)),
            postfix: new HarmonyMethod(typeof(MuzzleAnchors), nameof(Capture)) { priority = Priority.Last });
    }
    private static void Capture(Ship __instance, Design design, ShipHull shipHull, ModelComponent modelComponent, SceneRenderType sceneRenderType)
    {
        if (sceneRenderType != SceneRenderType.Location || modelComponent?.Model == null || shipHull == null) return;
        if (Models.TryGetValue(__instance, out var state) && state.Model.TryGetTarget(out var old) && ReferenceEquals(old, modelComponent) &&
            ReferenceEquals(state.Design, design) && state.Location == __instance.LocationId && state.Path == shipHull.ModelName) { Turrets.FollowConfirmedAim(__instance, modelComponent); return; }
        Models.Remove(__instance);
        Models.Add(__instance, new State(new WeakReference<ModelComponent>(modelComponent), design, __instance.LocationId, shipHull.ModelName));
        Turrets.FollowConfirmedAim(__instance, modelComponent);
    }
    internal static string ModelPath(Ship ship) => Models.TryGetValue(ship, out var state) ? state.Path : "";
    internal static bool Confirmed(ModelComponent model, MeshInfo mesh, string path) => !string.IsNullOrEmpty(path) && model?.Model?.Meshes is { } meshes &&
        (uint)mesh.MeshIndex < (uint)meshes.Count && meshes[mesh.MeshIndex] is { } actual && actual.NodeIndex == mesh.ModelComponentViewHierarchyNodeId &&
        MuzzleLayouts.Get(actual, path).Length > 0;

    internal static bool TryShip(Ship ship, byte bay, out Vector3[] points, out Vector3 pivot, out float barrel, bool allowZoomHidden = false)
    {
        points = []; pivot = default; barrel = 0;
        if (Entry.Bypass || !Models.TryGetValue(ship, out var state) || state.Location != ship.LocationId || !ReferenceEquals(state.Design, ship.Design) ||
            !state.Model.TryGetTarget(out var model) || model.Entity == null || model.Entity.IsDisposed || model.Entity.Transform.Parent != null ||
            ship.ComponentDisplayData?.GetById(bay) is not { MeshInfo.BayType: ComponentBayType.Weapon } display ||
            (!display.Visible && !allowZoomHidden) ||
            !Confirmed(model, display.MeshInfo, state.Path)) return false;
        var mesh = display.MeshInfo; var local = MuzzleLayouts.Get(model.Model.Meshes[mesh.MeshIndex], state.Path);
        if (!TryMatrix(model, mesh.ModelComponentViewHierarchyNodeId, out var transform, allowZoomHidden && !display.Visible)) return false;
        var world = model.Entity.Transform.LocalMatrix;
        // The renderer's matrix contains exact ship orientation, zoom inflation and scale. Its translation
        // can be rebased/render-scaled; use the current raw ship origin with its linear part instead.
        pivot = ship.GetPositionRaw() + Vector3.TransformNormal(transform.TranslationVector, world);
        points = new Vector3[local.Length];
        for (var i=0;i<local.Length;i++)
        {
            points[i] = ship.GetPositionRaw() + Vector3.TransformNormal(Vector3.TransformCoordinate(local[i], transform), world);
            if (!Finite(points[i])) { points=[]; return false; }
            barrel = Math.Max(barrel, Vector3.Distance(points[i], pivot));
        }
        return barrel > .0001f && float.IsFinite(barrel);
    }
    internal static bool TryMatrix(ModelComponent model, int node, out Matrix transform, bool allowHiddenBay = false)
    {
        transform=Matrix.Identity; var nodes=model.Skeleton?.NodeTransformations; var definitions=model.Skeleton?.Nodes;
        if(nodes==null || definitions==null) return false;
        for(var depth=0;node>0;depth++)
        {
            if(depth>32 || (uint)node>=(uint)nodes.Length || (uint)node>=(uint)definitions.Length) return false;
            var current=nodes[node];
            // Zoom culling hides the turret mesh while retaining its last local pose.
            bool zoomHidden = allowHiddenBay && depth == 0 && current.Flags == Stride.Rendering.ModelNodeFlags.EnableTransform;
            if((!zoomHidden && (current.Flags & Stride.Rendering.ModelNodeFlags.EnableRender)==0) ||
                (current.Flags & Stride.Rendering.ModelNodeFlags.OverrideWorldMatrix)!=0) return false;
            var t=current.Transform;
            transform *= (current.Flags & Stride.Rendering.ModelNodeFlags.EnableTransform)!=0 ?
                Matrix.Scaling(t.Scale)*Matrix.RotationQuaternion(t.Rotation)*Matrix.Translation(t.Position) : current.LocalMatrix;
            node=current.ParentIndex;
        }
        if(node!=0 || nodes.Length==0 || (nodes[0].Flags & Stride.Rendering.ModelNodeFlags.EnableRender)==0 || (nodes[0].Flags & Stride.Rendering.ModelNodeFlags.OverrideWorldMatrix)!=0) return false;
        return true;
    }
    private static Ship? Firer(WeaponBlast blast) => blast._Galaxy is { } galaxy && blast.FirerType==ItemType.Ship &&
        blast.Firer.Resolve<Ship>(galaxy) is { } ship && ship.LocationId==blast.LocationId ? ship : null;
    // Only a current, visible hit may replace a tail that has become detached from its firer.
    // Misses deliberately retain their recorded tail, and stale/reused blasts fail closed.
    internal static bool RefreshesStaleBeamOrigin(WeaponBlast blast) => blast.FireType==WeaponFireType.FullLengthBeam && !blast.IsDestroyed && blast.Alpha>0 &&
        blast.WillHitTarget && !blast.HasMissedTarget && blast._Galaxy?.Locations?.GetById(blast.LocationId)?.WeaponBlasts.GetById(blast.WeaponBlastId)==blast;
    internal static bool TryBeam(WeaponBlast blast, Vector3 original, Vector3 tip, out Vector3[] points)
    {
        points=[];
        if(Firer(blast) is not { } ship || !TryShip(ship,blast.FiringWeaponComponentBayId,out points,out var pivot,out var barrel, allowZoomHidden: true) ||
            (!RefreshesStaleBeamOrigin(blast) && Vector3.DistanceSquared(original,pivot)>barrel*barrel*9) ||
            points.Any(p=>!Finite(tip-p)||(tip-p).LengthSquared()<.000001f)) { points=[];return false; }
        Turrets.RememberConfirmedAim(blast, ship, tip-pivot);
        return true;
    }
    internal static bool TryLaunchOffset(WeaponBlast blast, out Vector3 offset)
    {
        offset=default;
        if(Entry.Bypass) return false;
        if(!Launches.TryGetValue(blast,out var launch))
        {
            if(Firer(blast) is not { } ship || !TryShip(ship,blast.FiringWeaponComponentBayId,out var points,out _,out var barrel)) return false;
            var fade=Math.Max(barrel*3, .001f); var distance=blast.DistanceTravelled; var delta=Vector3.Zero;
            if(distance>=0 && distance<=fade && blast.Direction.LengthSquared()>.000001f)
            {
                var start=blast.GetPositionRaw()-Vector3.Normalize(blast.Direction)*distance;
                delta=points[(int)((uint)blast.WeaponBlastId%(uint)points.Length)]-start;
                if(!Finite(delta) || delta.LengthSquared()>fade*fade) delta=Vector3.Zero;
            }
            launch=Launches.GetValue(blast,_=>new Launch(delta,distance,fade));
        }
        offset=launch.Delta*Math.Clamp(1-(blast.DistanceTravelled-launch.FirstDistance)/launch.Fade,0,1);
        return true;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
