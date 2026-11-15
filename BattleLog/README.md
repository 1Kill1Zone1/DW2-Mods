# Battle Log 0.2.0

Records observed combat encounters by weapon, hit/miss outcome, damage, and point defence.

**Dependencies:** Required: Positronic Matrix.

**Supported target:** Distant Worlds 2 Steam beta 1.3.8.7 with DW2 Mod Launcher. Copy this complete folder to the game's mods directory, enable it in the launcher, and install any dependencies separately. No game DLLs, settings, saves, profiles, logs, or vendor binaries are redistributed.

**Source:** Source/ is a publication snapshot. Build with .NET 8 and local game managed assemblies: dotnet build Source/BattleLog.csproj -c Release -p:GameRoot=<game managed DLL folder>. Helper files required by projects that reference ../Shared are supplied in this folder's Shared/ directory.


