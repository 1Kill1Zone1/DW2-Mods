using DistantWorlds.Types;

namespace WeaponFX;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal static class BeamShape
{
    internal static bool Supports(ComponentDefinition? component) => component?.Values != null &&
        component.Values.Any(s => s.WeaponFireType == WeaponFireType.FullLengthBeam) && Entry.SizeSuffix(component.Name) is "L" or "H" or "XL";

    // Exact float integer: strip, logarithmic core ratio (under 0.35% error; 1 exact), and hundredth-step gap. No vertex-layout change.
    internal static float Pack(float strip, float core, float gap) => 100 + strip + 1 + 16 *
        ((core == 1 ? 1024 : MathF.Round(MathF.Log2(Math.Clamp(core, .025f, 30f) / .025f) * 100)) + 1025 * MathF.Round(Math.Clamp(gap, 0, 3) * 100));

    internal static float PackOpacity(float core, float coreOpacity, float haloOpacity) => 1000000 + MathF.Round(core * 100) +
        256 * (MathF.Round(coreOpacity * 100) + 101 * MathF.Round(haloOpacity * 100));

    // The original seed is 0–976. Packing the gain keeps the existing vertex layout.
    internal static float PackCoreGain(float seed, float gain) =>
        gain == 1 ? seed : 1024 * (1 + MathF.Round(gain * 1000)) + seed;

    internal static string? Rewrite(string source)
    {
        const string sphere = "float sphere = exp(-dot(fromSphere, fromSphere) / 0.2);";
        const string flare = "float flare = exp(-(across * across) / (flareWidth * flareWidth)) * flareLeft * flareLeft;";
        const string muzzleLight = "(sphere * 6.0) + (flare * 1.6)";
        string[] anchors = ["float coreSpread = max(0.02 * coreWidth * coreWidth, streams.PixelShare * streams.PixelShare * 1.5);",
            "streams.StripRow = streams.BeamExtra.w;", "streams.Across = streams.Corner.x;",
            "(streams.Corner.x * width)", "float across = abs(streams.Across);", "stage override void VSMain()", "float3 tail = streams.Tail - Camera;",
            "max(0.11 * coreWidth, coreSpread * 5.0)", sphere, flare, muzzleLight];
        if (anchors.Any(a => source.IndexOf(a, StringComparison.Ordinal) < 0 || source.IndexOf(a, StringComparison.Ordinal) != source.LastIndexOf(a, StringComparison.Ordinal))) return null;
        return source.Replace("stage override void VSMain()", "stream float4 WeaponFxShape : TEXCOORD15;\n        stream float WeaponFxCoreGain : TEXCOORD16;\n        stage override void VSMain()", StringComparison.Ordinal)
            .Replace("streams.BeamExtra.z", "weaponFxSeed", StringComparison.Ordinal)
            .Replace("float3 tail = streams.Tail - Camera;", """
                float weaponFxSeedPayload = streams.BeamExtra.z;
                float weaponFxSeed = fmod(weaponFxSeedPayload, 1024.0);
                streams.WeaponFxCoreGain = weaponFxSeedPayload >= 1024.0
                    ? (floor(weaponFxSeedPayload / 1024.0) - 1.0) / 1000.0 : 1.0;
                float weaponFxPacked = streams.BeamExtra.w - 100.0;
                float weaponFxControls = floor(weaponFxPacked / 16.0);
                float weaponFxCoreCode = fmod(weaponFxControls, 1025.0);
                float weaponFxCore = streams.BeamExtra.w >= 100.0 && weaponFxCoreCode < 1023.5 ? 0.025 * exp2(weaponFxCoreCode / 100.0) : 1.0;
                float weaponFxGap = streams.BeamExtra.w >= 100.0 ? floor(weaponFxControls / 1025.0) / 100.0 : 0.0;
                if (streams.Finish.w < 0) weaponFxGap = 0.0;
                float weaponFxExtent = streams.Finish.w < 0 ? 1.0 : max(1.0, weaponFxCore) + weaponFxGap;
                float weaponFxOpacity = floor((streams.Firing.x - 1000000.0) / 256.0);
                float weaponFxBaseCore = streams.Firing.x >= 1000000.0 ? fmod(streams.Firing.x - 1000000.0, 256.0) / 100.0 : streams.Firing.x;
                streams.WeaponFxShape = float4(weaponFxCore, weaponFxGap,
                    streams.Firing.x >= 1000000.0 ? fmod(weaponFxOpacity, 101.0) / 100.0 : 1.0,
                    streams.Firing.x >= 1000000.0 ? floor(weaponFxOpacity / 101.0) / 100.0 : 1.0);
                float3 tail = streams.Tail - Camera;
                """, StringComparison.Ordinal)
            .Replace("(streams.Corner.x * width)", "(streams.Corner.x * width * weaponFxExtent)", StringComparison.Ordinal)
            .Replace("float4(streams.Firing.x,", "float4(weaponFxBaseCore,", StringComparison.Ordinal)
            .Replace("streams.Across = streams.Corner.x;", "streams.Across = streams.Corner.x * weaponFxExtent;", StringComparison.Ordinal)
            .Replace("streams.StripRow = streams.BeamExtra.w;", "streams.StripRow = streams.BeamExtra.w >= 100.0 ? fmod(weaponFxPacked, 16.0) - 1.0 : streams.BeamExtra.w;", StringComparison.Ordinal)
            .Replace("float across = abs(streams.Across);", """
                float split = streams.WeaponFxShape.y * smoothstep(0.0, 0.08, streams.Along) * (1.0 - smoothstep(0.92, 1.0, streams.Along));
                float across = abs(abs(streams.Across) - split);
                """, StringComparison.Ordinal)
            .Replace(anchors[0], anchors[0] + "\n                float originalCoreSpread = coreSpread;\n                coreSpread *= streams.WeaponFxShape.x * streams.WeaponFxShape.x;", StringComparison.Ordinal)
            .Replace("max(0.11 * coreWidth, coreSpread * 5.0)", "max(0.11 * coreWidth, originalCoreSpread * 5.0)", StringComparison.Ordinal)
            .Replace("core * 2.4 * coreAmount * coreShare * burst", "core * 2.4 * coreAmount * coreShare * burst * streams.WeaponFxShape.z * streams.WeaponFxCoreGain", StringComparison.Ordinal)
            .Replace("body * 0.75 * bodyShare", "body * streams.WeaponFxShape.w * 0.75 * bodyShare", StringComparison.Ordinal)
            .Replace(sphere, """
                float muzzleFade = smoothstep(0.0, 0.5, fromTail);
                float sphere = exp(-dot(fromSphere, fromSphere) / 0.1) * muzzleFade;
                """, StringComparison.Ordinal)
            .Replace(flare, "float flare = exp(-(across * across) / (flareWidth * flareWidth)) * flareLeft * flareLeft * muzzleFade;", StringComparison.Ordinal)
            .Replace(muzzleLight, "(sphere * 3.0 * streams.WeaponFxShape.z * streams.WeaponFxCoreGain) + (flare * 1.6 * streams.WeaponFxShape.w)", StringComparison.Ordinal)
            .Replace("float edge = saturate((1.0 - across) * 1.5);", "float edge = saturate((1.0 - across / max(1.0, streams.WeaponFxShape.x)) * 1.5);", StringComparison.Ordinal)
            .Replace("saturate((core * 1.3) + sphere)", "saturate((core * 1.3) + sphere) * streams.WeaponFxShape.z", StringComparison.Ordinal)
            .Replace("float3(1, 1, 1)", "(float3)max(streams.Tint.r, max(streams.Tint.g, streams.Tint.b))", StringComparison.Ordinal);
    }
}
