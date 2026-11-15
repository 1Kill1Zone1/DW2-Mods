using DistantWorlds.Types;

namespace GalaxyWaypoints;

public static partial class Entry
{
    private static DesignProfile[] AnalysisProfiles(Campaign campaign, bool diagnostic, out string? error)
    {
        error = null;
        var actual = CurrentDesignProfiles(campaign);
        if (actual.Length > 0 || !diagnostic || campaign.DisplayMode != WaypointDisplayMode.AllDebug) return actual;

        var galaxy = campaign.Observer.Galaxy;
        var scout = campaign.Observer.Designs.Where(design => design?.Summary != null && !design.Obsolete &&
                design.GetRole(galaxy) == ShipRole.ExplorationShip)
            .OrderByDescending(design => design.StarDate).ThenByDescending(design => design.DesignId).FirstOrDefault();
        if (scout?.Summary is not { FuelCapacity: > 0, ReactorEnergyStorageCapacity: > 0, ReactorFuelUnitsForFullCharge: > 0, StaticEnergyConsumption: >= 0 } source)
        { error = "Reference unavailable: no functional scout fuel/reactor data"; return []; }
        if (galaxy.Components.GetByComponentId(65) is not { Category: ComponentCategory.HyperDrive } drive)
        { error = "Reference unavailable: base Skip Drive missing"; return []; }
        var values = drive.Values?.GetByLevel(0);
        if (values == null || values.HyperDriveSpeed <= 0 || values.HyperDriveJumpRange <= 0)
        { error = "Reference unavailable: invalid base Skip Drive data"; return []; }

        // Isolated arithmetic input, never a registered design or a ship capability.
        var summary = new ShipSummary
        {
            FuelCapacity = source.FuelCapacity,
            ReactorEnergyStorageCapacity = source.ReactorEnergyStorageCapacity,
            ReactorFuelUnitsForFullCharge = source.ReactorFuelUnitsForFullCharge,
            StaticEnergyConsumption = source.StaticEnergyConsumption,
            HyperDriveSpeed = values.HyperDriveSpeed,
            HyperDriveEnergyUsage = values.HyperDriveEnergyUsage,
            HyperDriveJumpRange = values.HyperDriveJumpRange,
            HyperDriveJumpInitiationTime = values.HyperDriveJumpInitiationTime
        };
        double minimum = Math.Max(3200, 2.5 * Math.Max(800, values.HyperDriveJumpAccuracy == 0 ? 2000 : values.HyperDriveJumpAccuracy));
        var capability = new RouteCapability(minimum, summary.HyperDriveJumpRange, summary.CalculateMaximumFuelRange(),
            summary.HyperDriveSpeed, Math.Max(0, summary.HyperDriveJumpInitiationTime));
        if (!capability.Valid || capability.PoweredRange <= 0)
        { error = "Reference unavailable: invalid scout fuel estimate"; return []; }
        return [new DesignProfile($"REF {scout.Name} + {drive.Name} L0", capability, TransitGroup.Explorers,
            20_000 + Positive(values.HyperDriveJumpAccuracy)) { Reference = true }];
    }

    private static string ReferenceParameters(DesignProfile profile) =>
        $"jump {profile.Capability.JumpRange / 1e6:0.#}M; fuel {profile.Capability.PoweredRange / 1e6:0.#}M; " +
        $"speed {profile.Capability.Speed:N0}; prep {profile.Capability.Preparation:0.#}s";
}
