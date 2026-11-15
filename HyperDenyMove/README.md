# HyperDeny on Move 0.1.0

Applies player HyperDeny against wartime enemies during manual ship and fleet Move orders.

**Dependencies:** No external dependency declared.

**Supported target:** Distant Worlds 2 Steam beta 1.3.8.7 with DW2 Mod Launcher. Copy this complete folder to the game's mods directory, enable it in the launcher, and install any dependencies separately. No game DLLs, settings, saves, profiles, logs, or vendor binaries are redistributed.

**Source:** Source/ is a publication snapshot. Build with .NET 8 and local game managed assemblies: dotnet build Source/HyperDenyMove.csproj -c Release -p:GameRoot=<game managed DLL folder>. Helper files required by projects that reference ../Shared are supplied in this folder's Shared/ directory.


