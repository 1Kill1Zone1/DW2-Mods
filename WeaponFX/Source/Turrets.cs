using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class Turrets
{
    private sealed class Baseline
    {
        public object? Definition;
        public readonly Dictionary<int, Vector3> Scales = new();
    }
    private static readonly ConditionalWeakTable<Array, Baseline> Baselines = new();
    private readonly record struct BeamAim(Vector3 Direction, long Timestamp, WeakReference<WeaponBlast>? Shot = null);
    private static readonly ConditionalWeakTable<Ship, ConcurrentDictionary<byte, BeamAim>> BeamAims = new();
    private static Func<Galaxy, Ship, float> _shownScale = null!;

    internal static void Install(Harmony harmony, Assembly assembly)
    {
        var mesh = assembly.GetType("DW2R_Visual_Effects.Patches.ShieldShellMesh", true)!;
        _shownScale = AccessTools.Method(mesh, "ShownScale", [typeof(Galaxy), typeof(Ship)]).CreateDelegate<Func<Galaxy, Ship, float>>();
        harmony.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.UpdateExteriorModelComponentBays)),
            postfix: new HarmonyMethod(typeof(Turrets), nameof(UpdateModels)));
    }

    private static void UpdateModels(Ship __instance, Design design, ModelComponent modelComponent)
    {
        var skeleton = modelComponent?.Skeleton;
        var nodes = skeleton?.NodeTransformations;
        if (nodes == null || design?.Components == null || __instance.ComponentDisplayData == null) return;
        var baseline = Baselines.GetValue(nodes, _ => new Baseline());
        if (!ReferenceEquals(baseline.Definition, skeleton!.Nodes))
        {
            baseline.Definition = skeleton.Nodes;
            baseline.Scales.Clear();
        }
        foreach (var display in __instance.ComponentDisplayData.GetReadOnlySpan())
        {
            if (display?.MeshInfo is not { BayType: ComponentBayType.Weapon } mesh) continue;
            var index = mesh.ModelComponentViewHierarchyNodeId;
            if ((uint)index >= (uint)nodes.Length) continue;
            var component = design.Components.GetById(display.ComponentBayId);
            if (component.IsEmpty) continue;
            if (!baseline.Scales.TryGetValue(index, out var scale)) baseline.Scales[index] = scale = nodes[index].Transform.Scale;
            nodes[index].Transform.Scale = scale * (Entry.StyleOf(component.ComponentId)?.MountScale ?? 1f);
            if (!MuzzleAnchors.Confirmed(modelComponent!, mesh, MuzzleAnchors.ModelPath(__instance)) && display.Visible && display.RotationType == ModelRotationType.RotateToTarget &&
                BeamAims.TryGetValue(__instance, out var aims) && aims.TryGetValue(display.ComponentBayId, out var aim) &&
                Stopwatch.GetElapsedTime(aim.Timestamp).TotalSeconds <= .25)
            {
                var shipLocal = Vector3.TransformNormal(aim.Direction, __instance.DeRotation);
                var modelLocal = Vector3.Transform(shipLocal, mesh.CompensatingRotation);
                nodes[index].Transform.Rotation = AimRotation(mesh.DefaultDirection, modelLocal);
            }
        }
    }

    private static Quaternion AimRotation(Vector3 forward, Vector3 target)
    {
        var axis = Vector3.Cross(forward, target);
        if (axis.LengthSquared() > 0.00000001f || Vector3.Dot(forward, target) >= 0f)
            return Calculations.GetRotationOffsetFromDirection(forward, target);

        // The native cross-product axis is zero when the target is exactly behind the turret.
        axis = Vector3.Cross(forward, Math.Abs(forward.Y) < .9f ? Vector3.UnitY : Vector3.UnitZ);
        axis.Normalize();
        return Quaternion.RotationAxis(axis, MathF.PI);
    }

    internal static Vector3 BeamMuzzle(WeaponBlast blast, Vector3 original, Vector3 tip)
    {
        if (MuzzleAnchors.TryBeam(blast, original, tip, out var actual)) return actual.Aggregate(Vector3.Zero, (sum, p) => sum + p) / actual.Length;
        var galaxy = blast._Galaxy;
        if (galaxy == null || blast.FirerType != ItemType.Ship || blast.Firer.Resolve<Ship>(galaxy) is not { } ship ||
            ship.LocationId != blast.LocationId || !TryPivot(galaxy, ship, blast.FiringWeaponComponentBayId, out var display, out var pivot, out var barrel)) return original;
        // Keep the approximate path consistent with confirmed meshes: only a current live hit
        // can replace an old tail; a miss or stale/reused blast keeps its recorded origin.
        if (!MuzzleAnchors.RefreshesStaleBeamOrigin(blast) && Vector3.DistanceSquared(original, pivot) > barrel * barrel * 9f) return original;
        var scale = Entry.StyleOf(blast.FiringWeaponComponentId)?.MountScale ?? 1f;
        if (display.RotationType != ModelRotationType.RotateToTarget)
            return TryVisualMuzzle(galaxy, ship, blast.FiringWeaponComponentBayId, scale, out var muzzle, out _) ? muzzle : original;
        var direction = tip - pivot;
        var distance = direction.Length();
        if (distance <= .001f || !float.IsFinite(distance)) return original;
        direction /= distance;
        RememberAim(ship, blast.FiringWeaponComponentBayId, direction);
        return pivot + direction * Math.Min(barrel * scale, distance * .5f);
    }

    private static void RememberAim(Ship ship, byte bay, Vector3 direction) =>
        BeamAims.GetValue(ship, _ => new ConcurrentDictionary<byte, BeamAim>())[bay] = new BeamAim(direction, Stopwatch.GetTimestamp());

    internal static void RememberConfirmedAim(WeaponBlast blast, Ship ship, Vector3 direction)
    {
        if(direction.LengthSquared() < .000001f)return;
        BeamAims.GetValue(ship, _ => new ConcurrentDictionary<byte, BeamAim>())[blast.FiringWeaponComponentBayId] =
            new BeamAim(Vector3.Normalize(direction), Stopwatch.GetTimestamp(), new WeakReference<WeaponBlast>(blast));
    }
    internal static void FollowConfirmedAim(Ship ship, ModelComponent model)
    {
        if(Entry.Bypass || !BeamAims.TryGetValue(ship,out var aims) || ship.ComponentDisplayData==null)return;
        foreach(var display in ship.ComponentDisplayData.GetReadOnlySpan())
        {
            if(display is not {Visible:true,RotationType:ModelRotationType.RotateToTarget,MeshInfo: { } mesh} ||
                !MuzzleAnchors.Confirmed(model,mesh,MuzzleAnchors.ModelPath(ship)) || !aims.TryGetValue(display.ComponentBayId,out var aim) ||
                aim.Shot==null || !aim.Shot.TryGetTarget(out var blast) || blast.Alpha<=0 || blast.FireType!=WeaponFireType.FullLengthBeam || blast.LocationId!=ship.LocationId ||
                blast._Galaxy?.Locations?.GetById(blast.LocationId)?.WeaponBlasts.GetById(blast.WeaponBlastId)!=blast)continue;
            var local=Vector3.TransformNormal(aim.Direction,ship.DeRotation);
            model.Skeleton.NodeTransformations[mesh.ModelComponentViewHierarchyNodeId].Transform.Rotation =
                AimRotation(mesh.DefaultDirection,Vector3.Transform(local,mesh.CompensatingRotation));
        }
    }

    internal static Vector3 LaunchOffset(WeaponBlast blast)
    {
        if (MuzzleAnchors.TryLaunchOffset(blast, out var anchored)) return anchored;
        if (Entry.StyleOf(blast.FiringWeaponComponentId) is not { } style || style.MountScale == 1f ||
            !TryMuzzle(blast, out _, out var delta, out var barrel)) return Vector3.Zero;
        return delta * Math.Clamp(1f - blast.DistanceTravelled / (barrel * 3f), 0f, 1f);
    }

    private static bool TryMuzzle(WeaponBlast blast, out Vector3 muzzle, out Vector3 delta, out float barrel)
    {
        muzzle = delta = Vector3.Zero;
        barrel = 0;
        var galaxy = blast._Galaxy;
        if (galaxy == null || blast.FirerType != ItemType.Ship || blast.Firer.Resolve<Ship>(galaxy) is not { } ship ||
            ship.LocationId != blast.LocationId || !TryMount(galaxy, ship, blast.FiringWeaponComponentBayId, out muzzle, out var offset, out barrel)) return false;
        delta = offset * ((Entry.StyleOf(blast.FiringWeaponComponentId)?.MountScale ?? 1f) - 1f);
        return true;
    }

    internal static bool TryVisualMuzzle(Galaxy galaxy, Ship ship, byte bay, float mountScale, out Vector3 muzzle, out float barrel)
    {
        if (!TryMount(galaxy, ship, bay, out muzzle, out var offset, out barrel)) return false;
        muzzle += offset * (mountScale - 1f);
        return true;
    }

    private static bool TryMount(Galaxy galaxy, Ship ship, byte bay, out Vector3 muzzle, out Vector3 offset, out float barrel)
    {
        muzzle = offset = Vector3.Zero;
        if (!TryPivot(galaxy, ship, bay, out var display, out var pivot, out barrel)) return false;
        var direction = Vector3.TransformNormal(display.TargetDirectionWorld, ship.Rotation);
        if (direction.LengthSquared() <= 0.000001f) return false;
        offset = direction * barrel;
        muzzle = pivot + offset;
        return true;
    }

    private static bool TryPivot(Galaxy galaxy, Ship ship, byte bay, out ComponentDisplay display, out Vector3 pivot, out float barrel)
    {
        display = null!;
        pivot = Vector3.Zero;
        barrel = 0;
        if (_shownScale == null || ship.Design == null || ship.ComponentDisplayData?.GetById(bay) is not { MeshInfo.BayType: ComponentBayType.Weapon } found) return false;
        display = found;
        var mesh = display.MeshInfo;
        var scale = _shownScale(galaxy, ship);
        barrel = Math.Abs(mesh.ComponentMeshSize.Z * scale);
        if (barrel <= .001f || !float.IsFinite(barrel)) return false;
        pivot = ship.GetPositionRaw() + Vector3.TransformNormal(mesh.PositionWorld * scale, ship.Rotation);
        return true;
    }
}
