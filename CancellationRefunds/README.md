# Cancellation Refunds 0.1.1 — Steam beta

Refunds the unspent part of an actual state payment when an unfinished, newly tracked planetary building or troop order is successfully cancelled. Player and AI use the same attribution rules. This adds no button: use the normal cancellation command.

Requires Positronic Matrix and DW2 Mod Launcher. This package is checked against **Steam beta1.3.8.7**, with an exact Types-assembly hash guard. The historical0.1.0 release remains the separately verified GOG1.3.8.4 package. Other engine builds are not verified.

Only paid ordinary orders begun while this mod is active qualify. Old unknown payments, free grants, upgrades, captures, completed/damaged-completed assets, combat losses, ships and resources are excluded. Example:1000 actual credits paid,25% complete ->750 returned, once. A failed/repeated cancellation pays nothing. Save/load retains pending attribution; missing/corrupt history never invents money.

Close game and launcher, keep previous files and a separate pre-mod save, copy this complete folder into mods/, and enable it after Matrix. Optional tested peers: Troop Logistics1.0.40 and War Chest1.0.21. No third-party DLLs or personal settings are included.

The supplied DLL passed79 full native,26 targeted and two missing-Matrix checks on beta; native hook registration also passed. The source guard/API-target compatibility changed; payout rules did not. The cold fixture initializes ship summaries for native colony-capture side effects. Live gameplay/FPS remain manual acceptance. This version is installed in the owner's Steam Beta Vanilla profile; it has not been installed in GOG.

Build from this folder using .NET8: `dotnet build Source/CancellationRefunds.csproj -c Release -p:GameRoot="<your supported game managed DLL directory>" --disable-build-servers`. References are supplied by your game. Do not expect byte-identical binaries across different SDK/build paths. See TESTING.md and SHA256SUMS.
