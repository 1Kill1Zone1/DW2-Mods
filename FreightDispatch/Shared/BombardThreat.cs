using System.Collections.Concurrent;
using DistantWorlds.Types;

namespace DW2Mod;

// Whether an empire has a reason for planetary shields: an empire it has met can bombard (it has researched a bombard weapon, or any weapon with a
// bombardment value) and is at war with it, plans conquest, raid or elimination against it, or thinks little of it; or the Shakturi are out of their rifts
// [owner: they most definitely qualify]. Planetary shield research (Coherent R&D) and shield placement (Troop Logistics) both wait for it, so the two
// agree. Null when there is none, else who it is. Read every few seconds.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class BombardThreat
{
    // An empire's opinion of us at or below this (the game's overall evaluation, about -50 when it plans conquest) counts as angry
    const float AngryOpinion = -20f;
    const long ThreatMilliseconds = 10_000;
    // empire id -> when it was read (Environment.TickCount64), and who (for the log)
    static readonly ConcurrentDictionary<int, (long at, string who)> known = new();
    static readonly ConcurrentDictionary<int, string> said = new();

    public static string Who(Galaxy galaxy, Empire empire)
    {
        long now = Environment.TickCount64;
        if (known.TryGetValue(empire.EmpireId, out var last) && now - last.at < ThreatMilliseconds)
        {
            return last.who;
        }
        string who = null;
        try
        {
            if (galaxy.ShakturiStoryController is { } story && story.StoryStep >= ShakturiStoryController.StoryStepShakturiEmergeFromRifts
                && story.ShakturiEmpire != null && story.ShakturiEmpire.EmpireId != empire.EmpireId)
            {
                who = story.ShakturiEmpire.Name;
            }
            for (int i = 0; i < empire.Relations.Count && who == null; i++)
            {
                EmpireRelation ours = empire.Relations[i];
                Empire other = ours != null && ours.HaveMet ? ours.GetOtherEmpire(galaxy) : null;
                if (other == null || other.EmpireId == empire.EmpireId || other.EmpireId == 0 || other.Research?.ResearchedComponents == null || !CanBombard(other))
                {
                    continue;
                }
                bool hostile = ours.AtWar;
                for (int j = 0; j < other.Relations.Count && !hostile; j++)
                {
                    EmpireRelation theirs = other.Relations[j];
                    if (theirs != null && theirs.OtherEmpireId == empire.EmpireId)
                    {
                        hostile = theirs.AtWar || theirs.Strategy == DiplomaticStrategy.Conquer || theirs.Strategy == DiplomaticStrategy.Invade
                            || theirs.Strategy == DiplomaticStrategy.Eliminate || theirs.Strategy == DiplomaticStrategy.Raid
                            || (theirs.FactorsCurrent != null && theirs.FactorsCurrent.OverallEvaluation <= AngryOpinion);
                        break;
                    }
                }
                if (hostile)
                {
                    who = other.Name;
                }
            }
        }
        catch (Exception ex)
        {
            ModHost.Failed($"looking for neighbors that can bombard {empire.Name}, planetary shields are not held back", ex);
            who = "unreadable";
        }
        known[empire.EmpireId] = (now, who);
        if (empire.IsPlayer && said.TryGetValue(empire.EmpireId, out string before) ? before != (who ?? "") : empire.IsPlayer)
        {
            said[empire.EmpireId] = who ?? "";
            ModHost.Detail(who == null
                ? $"planetary shields: {empire.Name}: no empire that can bombard is at war with us or angry, so planetary shields wait"
                : $"planetary shields: {empire.Name}: {who} can bombard and is hostile to us, so planetary shields are wanted");
        }
        return who;
    }

    // whether the empire has a bombard weapon, or any weapon with a bombardment value (the Boskara's, the firestorm torpedoes)
    static bool CanBombard(Empire other)
    {
        ComponentList held = other.Research.ResearchedComponents;
        for (int i = 0; i < held.Count; i++)
        {
            Component c = held[i];
            if (c?.Definition == null)
            {
                continue;
            }
            if (c.Definition.Category == ComponentCategory.WeaponBombard)
            {
                return true;
            }
            ComponentStats values = c.GetValues(other.Research);
            if (values != null && (values.WeaponBombardDamageInfrastructure > 0f || values.WeaponBombardDamageMilitary > 0f
                || values.WeaponBombardDamagePopulation > 0f || values.WeaponBombardDamageQuality > 0f))
            {
                return true;
            }
        }
        return false;
    }
}
