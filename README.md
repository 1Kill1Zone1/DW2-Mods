# DW2 Mods

Distant Worlds 2 mods, with one mod per top-level folder. Shared test helpers live in `Shared/`; build utilities live in `scripts/`.

| Mod | Version | Source and documentation | Test download |
| --- | --- | --- | --- |
| Advanced Construction Mod | 0.4.0 | [AdvancedConstructionMod/](AdvancedConstructionMod/) | [ACM 0.4.0 release](https://github.com/1Kill1Zone1/DW2-Mods/releases/tag/acm-v0.4.0) |

Advanced Construction Mod keeps independent ship-design Series, explicit retrofit targets, reverse source selection and project/Series transfers. Version 0.4.0 adds independent Automatic/Manual component upgrades per Series, retaining the hull, component layout and weapon families.

- [Features and rules](AdvancedConstructionMod/README.md)
- [Installation and testing](AdvancedConstructionMod/TESTING.md)
- [Change history](AdvancedConstructionMod/CHANGELOG.md)

Test target: **Steam beta 1.3.8.7**, DW2 Mod Launcher and Positronic Matrix. The runtime passed 559 isolated domain/native assertions; live rendering and natural research progression still need gameplay testing. Keep a separate pre-0.4 campaign save: version-3 Series metadata cannot be loaded by ACM 0.3.x.

Only our mod source and shared test helpers are included. Obtain the game, launcher and third-party dependencies separately. No campaign fixtures, game binaries, personal profiles or internal work logs are published. Additional mods belong in their own folders when published; currently this repository contains ACM.
