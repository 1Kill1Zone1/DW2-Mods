using System.Text;
using HarmonyLib;
using Stride.Core.Storage;
using Stride.Shaders.Parser.Mixins;
using System.Runtime.Versioning;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class MissileDepth
{
    private const string Original = "if (depth < 1.0 && abs(streams.ViewSpot.z) > abs(viewZ) + streams.Radius) discard;";
    private const string Replacement = """
        float occlusionZ = (streams.Kind >= 1.5 && streams.Kind < 2.5) ? streams.ViewCenter.z : streams.ViewSpot.z;
        float depthSlack = (streams.Kind < 3.5 || streams.Kind > 4.5) ? 0.02 : streams.Radius;
        if (depth < 1.0 && abs(occlusionZ) > abs(viewZ) + depthSlack) discard;
        """;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Reported = new();
    internal static volatile bool ChargeShaderReady;
    internal static volatile bool MissileHeadTintReady;
    internal static volatile bool BeamShapeReady;
    internal static volatile bool MissileChannelsReady;
    internal static volatile bool TorpedoChannelsReady;
    internal static volatile bool ImpactChannelsReady;

    internal static void Install(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(ShaderSourceManager), "LoadShaderSource", [typeof(string), typeof(string)]);
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(MissileDepth), nameof(LoadShaderSource)));
    }

    private static void LoadShaderSource(string type, ref ShaderSourceManager.ShaderSourceWithHash __result)
    {
        if (type is not ("MissileImpact" or "MissileTrail" or "TorpedoCore" or "BeamCore" or "BoltCore") || __result.Source == null) return;
        var source = type == "MissileImpact" ? Rewrite(__result.Source) : type == "BeamCore" ? BeamShape.Rewrite(__result.Source) : type == "BoltCore" ? BoltShader(__result.Source) : BrightnessShader(__result.Source, type);
        if (source != null)
        {
            __result.Source = source;
            __result.Hash = ObjectId.FromBytes(Encoding.UTF8.GetBytes(source));
            if (type == "MissileImpact")
            {
                ChargeShaderReady = source.Contains("WeaponFxCharge()", StringComparison.Ordinal);
                ImpactChannelsReady = source.Contains("weaponFxImpactTint", StringComparison.Ordinal);
            }
            if (type == "BeamCore") BeamShapeReady = true;
            if (type == "MissileTrail") MissileChannelsReady = source.Contains("weaponFxChannel", StringComparison.Ordinal);
            if (type == "TorpedoCore") TorpedoChannelsReady = source.Contains("weaponFxChannel", StringComparison.Ordinal);
            if (type == "MissileTrail") MissileHeadTintReady = source.Contains("weaponFxHeadColor", StringComparison.Ordinal);
        }
        if (Reported.TryAdd(type, 0))
            Entry.Log(source == null ? $"{type} shader not recognized; original source retained." : $"{type} shader source updated.");
    }

    internal static string? Rewrite(string source)
    {
        var first = source.IndexOf(Original, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(Original, first + Original.Length, StringComparison.Ordinal) >= 0 ||
            !source.Contains("if (streams.Kind < 1.5) result = ShieldFlash();", StringComparison.Ordinal)) return null;
        var result = source.Replace(Original, Replacement, StringComparison.Ordinal);
        const string impactEnd = "streams.ColorTarget = result;";
        if (result.Contains(impactEnd, StringComparison.Ordinal) && result.Contains("float4 result = float4(0, 0, 0, 0);", StringComparison.Ordinal))
            result = result.Replace("float4 result = float4(0, 0, 0, 0);", """
                bool weaponFxImpactTint = min(streams.Tint.r, min(streams.Tint.g, streams.Tint.b)) >= 12.0;
                if (weaponFxImpactTint) streams.Tint = max(streams.Tint - 14.0, 0.0);
                float3 weaponFxImpactColor = streams.Tint;
                if (weaponFxImpactTint) streams.Tint /= max(max(streams.Tint.r, max(streams.Tint.g, streams.Tint.b)), 0.00001);
                float4 result = float4(0, 0, 0, 0);
                """, StringComparison.Ordinal).Replace(impactEnd, """
                if (weaponFxImpactTint && streams.Kind < 4.5)
                    result.rgb = weaponFxImpactColor * max(result.r, max(result.g, result.b));
                result.rgb *= streams.WeaponFxImpactOpacity.x * streams.WeaponFxImpactOpacity.y;
                result.a *= streams.WeaponFxImpactOpacity.x;
                streams.ColorTarget = result;
                """, StringComparison.Ordinal);
        result = result.Replace("stage override void VSMain()", "stream float2 WeaponFxImpactOpacity : TEXCOORD9;\n        stage override void VSMain()", StringComparison.Ordinal)
            .Replace("float kind = streams.ImpactData.w;", """
                float kind = streams.ImpactData.w;
                float weaponFxTurn = streams.ImpactData.z;
                float weaponFxImpactLevels = floor((weaponFxTurn - 1000.0) / 256.0);
                streams.WeaponFxImpactOpacity = weaponFxTurn >= 1000.0 ? float2(fmod(weaponFxImpactLevels, 101.0), floor(weaponFxImpactLevels / 101.0)) / 100.0 : float2(1, 1);
                if (weaponFxTurn >= 1000.0) weaponFxTurn = fmod(weaponFxTurn - 1000.0, 256.0) * (6.28318530718 / 255.0);
                """, StringComparison.Ordinal)
            .Replace("cos(streams.ImpactData.z)", "cos(weaponFxTurn)", StringComparison.Ordinal)
            .Replace("sin(streams.ImpactData.z)", "sin(weaponFxTurn)", StringComparison.Ordinal);
        const string vertex = "else if (kind > 3.5)";
        const string pixel = "else result = Glint();";
        if (!result.Contains(vertex, StringComparison.Ordinal) || !result.Contains(pixel, StringComparison.Ordinal)) return result;
        const string charge = """
            float4 WeaponFxCharge()
            {
                float progress = streams.Age;
                float reach = length(streams.Place);
                float glow;
                if (streams.Kind < 5.5)
                {
                    float arm = exp(-min(abs(streams.Place.x), abs(streams.Place.y)) * 19.0);
                    glow = arm * exp(-abs(reach - (0.78 - progress * 0.62)) * 9.0) + exp(-reach * 10.0) * progress;
                }
                else if (streams.Kind < 6.5)
                {
                    float swell = 8.0 - 4.5 * progress + streams.Turn.x * 1.2;
                    glow = exp(-reach * reach * swell * swell);
                }
                else
                {
                    float edge = (reach - (0.78 - progress * 0.53)) * 17.0;
                    glow = exp(-edge * edge) * 0.85 + exp(-reach * 11.0) * progress;
                }
                return float4(streams.Tint * glow * (0.24 + progress * 1.5), 0);
            }

            stage override void PSMain()
            """;
        return result.Replace(vertex, "else if (kind > 4.5) { spot = center + float3(streams.Corner * radius, 0); }\n            " + vertex, StringComparison.Ordinal)
            .Replace(pixel, "else if (streams.Kind < 4.5) result = Glint();\n                else result = WeaponFxCharge();", StringComparison.Ordinal)
            .Replace("stage override void PSMain()", charge, StringComparison.Ordinal);
    }

    private static string? BoltShader(string source)
    {
        const string white = "float3(0.45, 0.45, 0.45) + (streams.Tint * 0.55)";
        return source.Contains(white, StringComparison.Ordinal) ? source.Replace(white,
            "((float3)(0.45 * max(streams.Tint.r, max(streams.Tint.g, streams.Tint.b)))) + (streams.Tint * 0.55)", StringComparison.Ordinal) : null;
    }

    private static string? BrightnessShader(string source, string type)
    {
        const string signature = "stage override void PSMain()";
        var method = source.IndexOf(signature, StringComparison.Ordinal);
        if (!source.Contains("class " + type + " : ShaderBase", StringComparison.Ordinal) || method < 0 ||
            source.IndexOf(signature, method + signature.Length, StringComparison.Ordinal) >= 0) return null;
        var open = source.IndexOf('{', method);
        if (open < 0) return null;
        var depth = 1;
        var close = open + 1;
        for (; close < source.Length; close++)
        {
            if (source[close] == '{') depth++;
            else if (source[close] == '}' && --depth == 0) break;
        }
        if (depth != 0 || !source.AsSpan(open, close - open).Contains("streams.ColorTarget =", StringComparison.Ordinal)) return null;
        var result = source.Insert(close, "\n            streams.ColorTarget.rgb *= weaponFxBrightness * weaponFxOpacity;\n            streams.ColorTarget.a *= weaponFxOpacity * (weaponFxToken >= 202.0 ? saturate(weaponFxBrightness) : 1.0);\n        ")
            .Insert(open + 1, "\n            float weaponFxToken = floor(min(streams.Tint.r, min(streams.Tint.g, streams.Tint.b)) / 10.0) - 1.0;\n            bool weaponFxChannel = weaponFxToken >= 0.0 && fmod(floor(weaponFxToken / 101.0), 2.0) > 0.5;\n            float weaponFxOpacity = weaponFxToken >= 0.0 ? fmod(weaponFxToken, 101.0) / 100.0 : 1.0;\n            if (weaponFxToken >= 0.0) streams.Tint = max(streams.Tint - ((weaponFxToken + 1.0) * 10.0 + 4.0), 0.0);\n            float weaponFxBrightness = max(streams.Tint.r, max(streams.Tint.g, streams.Tint.b));\n            streams.Tint /= max(weaponFxBrightness, 0.00001);\n");
        const string starTag = "streams.Streak = 2.0;";
        const string starColor = "((float3(1, 1, 1) * star) + (warm * halo))";
        if (type == "MissileTrail" && result.Contains(starTag, StringComparison.Ordinal) && result.Contains(starColor, StringComparison.Ordinal))
        {
            result = result.Replace(starTag, "streams.Streak = seed > 0.5 ? 3.0 : 2.0;", StringComparison.Ordinal)
                .Replace("streams.ColorTarget = float4(" + starColor,
                    "float3 weaponFxHeadColor = streams.Streak > 2.5 ? streams.Tint : float3(1, 1, 1);\n                " +
                    "streams.ColorTarget = float4(((weaponFxHeadColor * star) + (warm * halo))", StringComparison.Ordinal);
        }
        if (type == "MissileTrail")
        {
            result = result.Replace("streams.ColorTarget = float4(hot *", "if (weaponFxChannel) hot = streams.Tint;\n                streams.ColorTarget = float4(hot *", StringComparison.Ordinal)
                .Replace("streams.ColorTarget = float4(smoke *", "if (weaponFxChannel) smoke *= streams.Tint;\n                streams.ColorTarget = float4(smoke *", StringComparison.Ordinal);
        }
        else
        {
            result = result.Replace("streams.ColorTarget = float4(color *", "if (weaponFxChannel) color = streams.Tint;\n                streams.ColorTarget = float4(color *", StringComparison.Ordinal)
                .Replace("streams.ColorTarget = float4(light *", "if (weaponFxChannel) light = streams.Tint * max(light.r, max(light.g, light.b));\n                streams.ColorTarget = float4(light *", StringComparison.Ordinal);
        }
        return result;
    }
}
