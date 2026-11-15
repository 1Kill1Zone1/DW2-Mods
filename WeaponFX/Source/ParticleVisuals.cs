using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using DistantWorlds2;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles.Components;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class ParticleVisuals
{
    private sealed record Baseline(short Id, int Channel, Color4 Color);
    private static readonly ConditionalWeakTable<ParticleSystemComponent, Baseline> Baselines = new();
    private static readonly List<WeakReference<ParticleSystemComponent>> Live = new();

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(EffectHelper), "GenerateGraphicsNode", [typeof(DisplayEffect), typeof(Location), typeof(Entity), typeof(Scene), typeof(Entity)]),
            postfix: new HarmonyMethod(typeof(ParticleVisuals), nameof(Track)));
        var update = typeof(GraphicsNodeList).GetMethods().Single(m => m.Name == "Update" && m.GetParameters().Any(p => p.Name == "isBackgroundMode"));
        harmony.Patch(update, postfix: new HarmonyMethod(typeof(ParticleVisuals), nameof(Update)));
    }

    private static void Track(DisplayEffect displayEffect, Entity entity)
    {
        if (displayEffect == null || displayEffect.Id is < 0 or > short.MaxValue || entity == null) return;
        var channel = displayEffect.Type switch
        {
            DisplayEffectType.WeaponMuzzleFlash or DisplayEffectType.WeaponChargeup => 1,
            DisplayEffectType.ArmorImpact or DisplayEffectType.HullImpact => 3,
            _ => 0
        };
        if (channel == 0) return;
        lock (Live)
        {
            foreach (var particle in Particles(entity))
            {
                if (Baselines.TryGetValue(particle, out _)) continue;
                var baseline = new Baseline((short)displayEffect.Id, channel, particle.Color);
                Baselines.Add(particle, baseline);
                Live.Add(new WeakReference<ParticleSystemComponent>(particle));
                Apply(particle, baseline);
            }
        }
    }
    private static IEnumerable<ParticleSystemComponent> Particles(Entity entity)
    {
        if (entity.Get<ParticleSystemComponent>() is { } particle) yield return particle;
        foreach (var child in entity.GetChildren())
            foreach (var nested in Particles(child)) yield return nested;
    }
    private static void Update()
    {
        lock (Live)
            for (var i = Live.Count - 1; i >= 0; i--)
                if (!Live[i].TryGetTarget(out var particle)) Live.RemoveAt(i);
                else if (Baselines.TryGetValue(particle, out var baseline)) Apply(particle, baseline);
    }
    private static void Apply(ParticleSystemComponent particle, Baseline baseline)
    {
        var style = Entry.StyleOf(baseline.Id);
        if (style == null) { particle.Color = baseline.Color; return; }
        var inheritedImpact = baseline.Channel == 3 && style.ImpactColor == null && !style.ImpactFollowsBody;
        var tint = (inheritedImpact ? null : style.ChannelTint(baseline.Channel)) ?? new Vector3(baseline.Color.R, baseline.Color.G, baseline.Color.B);
        var brightness = baseline.Channel == 3 ? style.NativeImpactBrightness : style.ChannelBrightness(baseline.Channel);
        var opacity = style.Opacity(baseline.Channel);
        particle.Color = new Color4(tint.X * brightness * opacity, tint.Y * brightness * opacity,
            tint.Z * brightness * opacity, baseline.Color.A * opacity);
    }
}
