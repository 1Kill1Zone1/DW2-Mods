# Cancellation Refunds 0.1.0

Returns the unspent monetary share of newly tracked cancelled construction and recruitment orders. Positronic Matrix is required.

**Dependencies:** Required: Positronic Matrix.

**Supported target:** Distant Worlds 2 GOG 1.3.8.4 with DW2 Mod Launcher. Steam beta is unverified. This checked candidate is not installed locally. It tracks only newly recorded ordinary paid planetary-building and troop-recruit orders, and refunds only actual remaining paid work after deliberate cancellation. It excludes ships, resources, old/unknown orders, upgrades, captures, completed assets, and damage/losses.

**Source:** Source/ is a publication snapshot. Build with .NET 8 and local game managed assemblies: dotnet build Source/CancellationRefunds.csproj -c Release -p:GameRoot=<game managed DLL folder>. Helper files required by projects that reference ../Shared are supplied in this folder's Shared/ directory.


