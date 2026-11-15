# Distant Worlds 2 local mod release

This folder is a standalone DW2 Mod Launcher mod. Install it by copying its complete folder into the game's `mods` directory and enabling it in the launcher. Read `mod.json` and `dw2modlauncher.json` for the exact version, entry point, and dependencies. Dependencies are installed separately; this release never redistributes the game, launcher, or third-party DLLs.

The binary is pinned in `SHA256SUMS`. `Source` is a frozen publication snapshot where supplied. It may require .NET 8 and locally installed game assemblies to build; those assemblies are not included. The stated API target and verification limits are in `TESTING.md`. Use a disposable campaign and retain a save backup before live use.
