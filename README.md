# DW2 Mods

Distant Worlds 2 mods, with one mod per top-level folder. Shared test helpers live in `Shared/`; build utilities live in `scripts/`.

| Mod | Version | Source and documentation | Test download |
| --- | --- | --- | --- |
| Advanced Construction Mod | 0.4.0 | [AdvancedConstructionMod/](AdvancedConstructionMod/) | [ACM 0.4.0 release](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/acm-v0.4.0) |
| Weapon FX Tuner | 0.1.21 | [WeaponFX/](WeaponFX/) | [FX 0.1.21 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/fx-v0.1.21) |
| Battle Log | 0.2.0 | [BattleLog/](BattleLog/) | [Battle Log 0.2.0 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/battle-log-v0.2.0) |
| Galaxy Waypoints | 0.12.1 | [GalaxyWaypoints/](GalaxyWaypoints/) | [Waypoints 0.12.1 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/waypoints-v0.12.1) |
| Deep Space Stability and Refuel | 0.2.0 | [DeepSpaceStability/](DeepSpaceStability/) | [Deep Space 0.2.0 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/deep-space-v0.2.0) |
| Resource Intelligence | 0.1.2 | [ResourceIntelligence/](ResourceIntelligence/) | [Resource Intelligence 0.1.2 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/resource-intelligence-v0.1.2) |
| Racial Research Unlock | 0.2.1 | [RacialResearchUnlock/](RacialResearchUnlock/) | [Racial Research 0.2.1 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/racial-research-v0.2.1) |
| HyperDeny on Move | 0.1.0 | [HyperDenyMove/](HyperDenyMove/) | [HyperDeny 0.1.0 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/hyperdeny-v0.1.0) |
| Freight Visibility | 0.1.1 | [FreightVisibility/](FreightVisibility/) | [Freight Visibility 0.1.1 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/freight-visibility-v0.1.1) |
| Freight Dispatch | 0.2.4 | [FreightDispatch/](FreightDispatch/) | [Freight Dispatch 0.2.4 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/freight-dispatch-v0.2.4) |
| Cancellation Refunds | 0.1.0 | [CancellationRefunds/](CancellationRefunds/) | [Cancellation Refunds 0.1.0 prerelease](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/cancellation-refunds-v0.1.0) |

Advanced Construction Mod keeps independent ship-design Series, explicit retrofit targets, reverse source selection and project/Series transfers. Version 0.4.0 adds independent Automatic/Manual component upgrades per Series, retaining the hull, component layout and weapon families.

- [Features and rules](AdvancedConstructionMod/README.md)
- [Installation and testing](AdvancedConstructionMod/TESTING.md)
- [Change history](AdvancedConstructionMod/CHANGELOG.md)

Test target: **Steam beta 1.3.8.7**, DW2 Mod Launcher and Positronic Matrix. The runtime passed 559 isolated domain/native assertions; live rendering and natural research progression still need gameplay testing. Keep a separate pre-0.4 campaign save: version-3 Series metadata cannot be loaded by ACM 0.3.x.

Only our mod source and shared test helpers are included. Obtain the game, launcher and third-party dependencies separately. No campaign fixtures, game binaries, personal profiles or internal work logs are published. Each mod is a prerelease candidate with its own explicit release link; Resource Intelligence 0.1.2 and Cancellation Refunds 0.1.0 are checked but not locally installed. The Spies overlay and vendor/Workshop mods are intentionally excluded.
