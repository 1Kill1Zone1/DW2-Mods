using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace WeaponFX;

[SupportedOSPlatform("windows7.0")]
internal static class ProjectileEffects
{
    private const string Rve = "DW2R_Visual_Effects.Patches.";
    private sealed record Tag(short Id);
    private static readonly ConditionalWeakTable<object, Tag> Tags = new();
    private static Func<WeaponEffect, bool>? _usesTrail;
    private static Func<bool>? _missileReady;
    private static Func<bool>? _torpedoReady;
    private static IDictionary _emitters = null!;
    private static AccessTools.FieldRef<int> _next = null!;
    private static Tag?[] _puffs = [];
    private static (string Name, bool Pulse)[] _beamFamilies = [];

    internal static void Install(Harmony harmony, Assembly assembly)
    {
        var missile = assembly.GetType(Rve + "MissileTrail", true)!;
        _usesTrail = AccessTools.Method(missile, "Uses").CreateDelegate<Func<WeaponEffect, bool>>();
        var torpedo = assembly.GetType(Rve + "TorpedoCore", true)!;
        _missileReady = AccessTools.PropertyGetter(missile, "Ready").CreateDelegate<Func<bool>>();
        _torpedoReady = AccessTools.PropertyGetter(torpedo, "Ready").CreateDelegate<Func<bool>>();
        var beam = assembly.GetType(Rve + "BeamCore", true)!;
        _emitters = (IDictionary)AccessTools.Field(missile, "Emitters").GetValue(null)!;
        _next = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(missile, "_next"));
        _puffs = new Tag?[((Array)AccessTools.Field(missile, "Puffs").GetValue(null)!).Length];
        ClearPuffs();
        lock (_emitters)
            foreach (DictionaryEntry pair in _emitters)
                if (pair.Key is WeaponBlast blast && pair.Value != null) TagEmitter(blast, pair.Value);
        var families = (Array)AccessTools.Field(beam, "Families").GetValue(null)!;
        _beamFamilies = families.Cast<object>().Select(tuple =>
        {
            var name = (string)AccessTools.Field(tuple.GetType(), "Item1").GetValue(tuple)!;
            var style = AccessTools.Field(tuple.GetType(), "Item2").GetValue(tuple)!;
            return (name, (float)AccessTools.Field(style.GetType(), "GateRate").GetValue(style)! > 0 ||
                (float)AccessTools.Field(style.GetType(), "PulseRate").GetValue(style)! > 0);
        }).ToArray();

        Patch(missile, "Note", postfix: nameof(Note), transpiler: nameof(SampleLaunchPosition));
        Patch(missile, "Leave", prefix: nameof(BeforePuff), postfix: nameof(AfterPuff));
        Patch(missile, "Clear", postfix: nameof(ClearPuffs));
        Patch(missile, "Fill", transpiler: nameof(PuffCode));
        Patch(missile, "AddStreak", transpiler: nameof(ProjectileCode));
        Patch(missile, "AddStar", transpiler: nameof(ProjectileCode));
        Patch(torpedo, "AddSprite", transpiler: nameof(ProjectileCode));
        Patch(torpedo, "AddRibbon", transpiler: nameof(ProjectileCode));
        ImpactColors.Install(harmony, assembly);

        void Patch(Type type, string target, string? prefix = null, string? postfix = null, string? transpiler = null)
        {
            var method = AccessTools.Method(type, target) ?? throw new MissingMethodException(type.FullName, target);
            harmony.Patch(method, Hook(prefix), Hook(postfix), Hook(transpiler));
        }
    }

    internal static bool HasTrail(ComponentDefinition component) => component.WeaponEffect is { } effect && (_usesTrail?.Invoke(effect) == true || effect.FlipbookTextureFilepath?.Contains("PulseTorpedoFlipbook", StringComparison.Ordinal) == true);

    internal static bool ReplacesNativeTorpedoTrail(WeaponEffect? effect) =>
        effect?.FlipbookTextureFilepath?.Contains("PulseTorpedoFlipbook", StringComparison.Ordinal) == true &&
        _missileReady?.Invoke() == true && _torpedoReady?.Invoke() == true;

    internal static bool HasBeamPulse(ComponentDefinition component) => _beamFamilies
        .FirstOrDefault(family => component.Name.Contains(family.Name, StringComparison.OrdinalIgnoreCase)).Pulse;

    private static HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(ProjectileEffects), name);

    private static void Note(WeaponBlast blast)
    {
        lock (_emitters)
            if (_emitters[blast] is { } emitter) TagEmitter(blast, emitter);
    }

    private static void TagEmitter(WeaponBlast blast, object emitter)
    {
        if (!Tags.TryGetValue(emitter, out _)) Tags.GetValue(emitter, _ => new Tag(blast.FiringWeaponComponentId));
    }

    private static void BeforePuff(ref int __state) => __state = _next();
    private static void AfterPuff(object emitter, int __state) => _puffs[__state] = Tags.TryGetValue(emitter, out var tag) ? tag : null;
    private static void ClearPuffs() { lock (_emitters) Array.Clear(_puffs); }

    private static Entry.Style? EmitterStyle(object emitter) => Tags.TryGetValue(emitter, out var tag) ? Entry.StyleOf(tag.Id) : null;
    private static Tag? PuffTag(int loopIndex) => _puffs[(_next() + loopIndex) % _puffs.Length];
    private static Entry.Style? PuffStyle(int loopIndex) => PuffTag(loopIndex) is { } tag ? Entry.StyleOf(tag.Id) : null;
    private static Vector3 LaunchPosition(Vector3 position, WeaponBlast blast) => position + Turrets.LaunchOffset(blast);
    internal static short? EmitterId(object emitter) => Tags.TryGetValue(emitter, out var tag) ? tag.Id : null;
    private static Vector3 ChannelTint(Vector3 tint, Entry.Style? style, int channel, bool shaderFlag, bool colorInherited = false)
    {
        if (style == null) return tint;
        var result = (style.ChannelTint(channel) ?? tint) * style.ChannelBrightness(channel);
        var customColor = (colorInherited || style.FollowsBody(channel) ? style.ChannelColor(channel) : channel == 1 ? style.BodyColor : style.TrailColor) >= 0;
        // Leave room for GPU interpolation error when one of the color channels is exactly zero.
        return shaderFlag ? result + new Vector3(4 + 10 * (1 + MathF.Round(style.Opacity(channel) * 100) + (customColor ? 101 : 0) + ((channel == 1 ? style.LegacyBodyAlpha : style.LegacyTrailAlpha) ? 202 : 0))) : result * style.Opacity(channel);
    }
    private static Vector3 BodyTint(Vector3 tint, object emitter) => ChannelTint(tint, EmitterStyle(emitter), 1, MissileDepth.MissileChannelsReady);
    private static Vector3 TorpedoTint(Vector3 tint, object emitter) => ChannelTint(tint, EmitterStyle(emitter), 1, MissileDepth.TorpedoChannelsReady);
    private static Vector3 TrailTint(Vector3 tint, object emitter) => ChannelTint(tint, EmitterStyle(emitter), 2, MissileDepth.MissileChannelsReady);
    private static Vector3 RibbonTint(Vector3 tint, object emitter) => ChannelTint(tint, EmitterStyle(emitter), 2, MissileDepth.TorpedoChannelsReady, colorInherited: true);
    private static Vector3 PuffTint(Vector3 tint, int index) => ChannelTint(tint, PuffStyle(index), 2, MissileDepth.MissileChannelsReady);
    private static float PuffWidth(float width, int index) => PuffStyle(index) is { Length: 0 } ? 0 : width * (PuffStyle(index)?.Width ?? 1);
    private static float PuffAge(float age, int index) => PuffStyle(index) is { Length: > 0 } style ? age / style.Length : age;
    private static float Width(float value, object emitter) => value * (EmitterStyle(emitter)?.Width ?? 1);
    private static float Length(float value, object emitter) => value * (EmitterStyle(emitter)?.Length ?? 1);
    private static float StarFlag(float original, object emitter) =>
        MissileDepth.MissileHeadTintReady && EmitterStyle(emitter)?.ChannelTint(1) != null ? 1f : original;

    private static IEnumerable<CodeInstruction> SampleLaunchPosition(IEnumerable<CodeInstruction> operations)
    {
        // Record the muzzle offset with the new sample. Moving the camera shifted the entire recorded trail.
        var position = AccessTools.Field(typeof(BillboardRenderItem), nameof(BillboardRenderItem.Position));
        int samples = 0;
        foreach (var operation in operations)
        {
            yield return operation;
            if (operation.opcode != OpCodes.Ldfld || !Equals(operation.operand, position)) continue;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return CodeInstruction.Call(typeof(ProjectileEffects), nameof(LaunchPosition));
            samples++;
        }
        if (samples != 1) throw new InvalidOperationException("Unrecognized projectile trail sample.");
    }

    private static IEnumerable<CodeInstruction> ProjectileCode(IEnumerable<CodeInstruction> operations, MethodBase original)
    {
        var emitter = original.Name is "AddStar" or "AddStreak" ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0;
        var trail = original.Name is "AddStreak" or "AddRibbon";
        var changed = 0;
        var code = operations.ToList();
        var starFlag = false;
        for (var i = 0; i < code.Count; i++)
        {
            var operation = code[i];
            yield return operation;
            if (original.Name == "AddStar" && i + 2 < code.Count && operation.opcode == OpCodes.Ldc_R4 &&
                operation.operand is 0f && code[i + 1].opcode == emitter &&
                code[i + 2].operand is FieldInfo { Name: "Tint" })
            {
                yield return new CodeInstruction(emitter);
                yield return CodeInstruction.Call(typeof(ProjectileEffects), nameof(StarFlag));
                starFlag = true;
            }
            if (operation.opcode == OpCodes.Ldfld && operation.operand is FieldInfo field && field.DeclaringType?.Name == "Emitter")
            {
                var helper = field.Name == "Tint" ? original.Name == "AddStar" ? nameof(BodyTint) : original.Name == "AddSprite" ? nameof(TorpedoTint) : original.Name == "AddRibbon" ? nameof(RibbonTint) : nameof(TrailTint) : original.Name == "AddStar" && field.Name == "Sprite" ? nameof(Width) : null;
                if (helper != null)
                {
                    yield return new CodeInstruction(emitter);
                    yield return CodeInstruction.Call(typeof(ProjectileEffects), helper);
                    changed++;
                }
            }
            var index = StoreIndex(operation);
            var dimension = trail && index == 0 ? nameof(Length) : trail && index == 1 || original.Name == "AddSprite" && index == 6 ? nameof(Width) : null;
            if (dimension != null)
            {
                if (original.GetMethodBody()!.LocalVariables[index].LocalType != typeof(float)) throw new InvalidOperationException("Unrecognized projectile dimension.");
                yield return CodeInstruction.LoadLocal(index);
                yield return new CodeInstruction(emitter);
                yield return CodeInstruction.Call(typeof(ProjectileEffects), dimension);
                yield return CodeInstruction.StoreLocal(index);
                changed++;
            }
        }
        if (changed < 2 || original.Name == "AddStar" && !starFlag) throw new InvalidOperationException("Unrecognized projectile renderer: " + original.Name);
    }

    private static IEnumerable<CodeInstruction> PuffCode(IEnumerable<CodeInstruction> operations, MethodBase original)
    {
        var locals = original.GetMethodBody()!.LocalVariables;
        if (locals[5].LocalType != typeof(int) || locals[7].LocalType != typeof(float)) throw new InvalidOperationException("Unrecognized smoke puff renderer.");
        var count = 0;
        foreach (var operation in operations)
        {
            yield return operation;
            if (StoreIndex(operation) == 7)
            {
                yield return CodeInstruction.LoadLocal(7);
                yield return CodeInstruction.LoadLocal(5);
                yield return CodeInstruction.Call(typeof(ProjectileEffects), nameof(PuffAge));
                yield return CodeInstruction.StoreLocal(7);
                count++;
            }
            else if (operation.opcode == OpCodes.Ldfld && operation.operand is FieldInfo field && field.DeclaringType?.Name == "Puff" && field.Name is "Radius" or "Tint")
            {
                yield return CodeInstruction.LoadLocal(5);
                yield return CodeInstruction.Call(typeof(ProjectileEffects), field.Name == "Radius" ? nameof(PuffWidth) : nameof(PuffTint));
                count++;
            }
        }
        if (count != 4) throw new InvalidOperationException("Unexpected smoke puff fields.");
    }

    private static int StoreIndex(CodeInstruction operation)
    {
        if (operation.opcode == OpCodes.Stloc_0) return 0;
        if (operation.opcode == OpCodes.Stloc_1) return 1;
        return (operation.opcode == OpCodes.Stloc || operation.opcode == OpCodes.Stloc_S) && operation.operand is LocalBuilder local ? local.LocalIndex : -1;
    }
}
