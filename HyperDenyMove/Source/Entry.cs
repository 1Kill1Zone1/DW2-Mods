using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace HyperDenyMove;

public static class Entry
{
    private const string PatchId = "local.dw2.hyperdeny-move";

    public static void Init()
    {
        var check = AccessTools.DeclaredMethod(typeof(ShipList), nameof(ShipList.CheckAnyShipsWithActiveHyperDenyForPosition));
        if (Harmony.GetPatchInfo(check)?.Owners.Contains(PatchId) == true)
            return;

        new Harmony(PatchId).Patch(check, postfix: new HarmonyMethod(typeof(Entry), nameof(CheckMovingShips)));
    }

    private static void CheckMovingShips(ShipList __instance, Vector3 position, Location location,
        short empireIdToExclude, float hyperBlockingInsulation, ref bool __result)
    {
        if (__result || empireIdToExclude <= 0)
            return;

        foreach (Ship ship in __instance.GetReadOnlySpan())
        {
            if (ship?.Summary is not { } summary || !(summary.HyperDenyRange > 0f) ||
                !(summary.HyperDenyStrength > hyperBlockingInsulation) || ship.IsDestroyedCached ||
                ship.EmpireId == empireIdToExclude || ship.LocationId != location.LocationId ||
                ship.Mission is not { Type: ShipMissionType.Move, IsManuallyAssigned: true })
                continue;

            // Fleet Move uses the same manual Move mission on each participating ship.
            if (Vector3.DistanceSquared(position, ship.GetPosition(location)) > summary.HyperDenyRange * summary.HyperDenyRange ||
                ship.IsHyperjumping() || ship.GetEmpire() is not { IsPlayer: true } owner)
                continue;

            owner.Relations.FastLookupInfo(empireIdToExclude, out bool atWar, out _, out _);
            if (atWar)
            {
                __result = true;
                return;
            }
        }
    }
}
