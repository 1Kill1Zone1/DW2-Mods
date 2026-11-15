namespace GalaxyWaypoints;

internal readonly record struct EnvironmentLoad(double Standard, double Direct, double Ion, double Energy,
    double Shield, double JumpBlock, bool Uncertain = false)
{
    internal bool Any => Standard != 0 || Direct != 0 || Ion != 0 || Energy != 0 || Shield != 0 || JumpBlock != 0 || Uncertain;
    public static EnvironmentLoad operator +(EnvironmentLoad a, EnvironmentLoad b) => new(
        a.Standard + b.Standard, a.Direct + b.Direct, a.Ion + b.Ion, a.Energy + b.Energy, a.Shield + b.Shield,
        Math.Max(a.JumpBlock, b.JumpBlock), a.Uncertain || b.Uncertain);
}

internal readonly record struct EnvironmentProtection(double Reactive, double Ion, double ShieldResistance,
    double ShieldCapacity, double ShieldRecharge, double ShieldRechargeEnergy,
    double Output, double Storage, double Consumption, double Energy, double Shields, bool Fuel)
{
    internal string? Refusal(EnvironmentLoad load)
    {
        if (!load.Any) return null;
        if (load.Uncertain || new[] { Reactive, Ion, ShieldResistance, ShieldCapacity, ShieldRecharge,
                ShieldRechargeEnergy, Output, Storage, Consumption, Energy, Shields, load.Standard, load.Direct,
                load.Ion, load.Energy, load.Shield, load.JumpBlock }.Any(x => !double.IsFinite(x) || x < 0))
            return "environment or functioning protection data unavailable";
        // Direct environmental damage explicitly bypasses defenses in native ApplyLocationEffectGroup.
        if (load.Direct > 0) return "direct environmental damage bypasses reliable protection";
        if (load.Ion > Ion) return $"ion protection {Ion:0.#} < peak {load.Ion:0.#}";
        if (load.JumpBlock > 0) return "environment can block emergency hyperjump";
        double shieldDamage = Math.Max(0, load.Standard - ShieldResistance);
        bool armorHolds = Reactive >= load.Standard;
        double shieldLoad = load.Shield + shieldDamage;
        bool shieldNeeded = !armorHolds || ShieldCapacity > 0 && shieldLoad > 0;
        if (shieldNeeded && (ShieldCapacity <= 0 || Shields <= shieldLoad || ShieldRecharge < shieldLoad))
            return $"shield protection/recharge cannot sustain peak {shieldLoad:0.#}";
        if (!armorHolds && !shieldNeeded) return "ordinary environmental damage exceeds protection";
        double rechargeEnergy = shieldNeeded ? ShieldRechargeEnergy : 0;
        double demand = Consumption + load.Energy + rechargeEnergy;
        if (!Fuel || Energy <= load.Energy || Storage <= load.Energy || Output < demand)
            return $"energy cannot sustain environment: output {Output:0.#}, demand {demand:0.#}";
        return null;
    }
}
