# Testing Advanced Construction Mod 0.3.0

## Requirements and installation

Use Distant Worlds 2 Steam beta **1.3.8.7**, DW2 Mod Launcher, and Positronic Matrix. The tested DistantWorlds.Types.dll SHA-256 is `787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254`.

1. Close the game and launcher. Retain the previous ACM files and a separate pre-0.3 campaign save.
2. Download the ACM 0.3.0 ZIP from this repository's Releases and extract it into the game directory. It contains `mods/AdvancedConstructionMod/`.
3. In DW2 Mod Launcher, enable Positronic Matrix and Advanced Construction Mod; load Matrix before ACM. Launch through the launcher.
4. Use a separate test save slot.

**Save compatibility:** ACM 0.3 writes version-2 series metadata after an ACM edit. ACM 0.2.2 cannot read that metadata. Reverting DLLs alone does not downgrade a campaign; retain the pre-0.3 save.

## Manual acceptance

- Open an existing design and Upgrade it. Enter/confirm a series inline; test OK, dropdown, Edit sources, Cancel and Save and Exit.
- Keep Hura and Little Devourer in separate series. Confirm ordinary Retrofit stays within the intended series and native disabled paths remain disabled.
- Choose an older saved project as the explicit target. If obsolete, verify the activation warning before Save. Check the effective destination and no self-retrofit.
- Upgrade that selected target and save: the new version becomes the target. Copy as new and Upgrade of another generation must not replace the pin.
- Move one saved project: review every affected target and ship count; other source projects stay behind. Then test the entire-series transfer, including obsolete generations. A newer incoming design must not displace the chosen destination target.
- Give a ship a Retrofit order or queue it at a yard. A conflicting series change must be refused with the ship's name; the existing order must remain intact.
- In Sources, test independent checkbox/row selection, search retaining hidden selections, separate Current and After Apply columns, long details paging, and cancellation.
- Save/reload the test slot and reopen the designs. Series, targets, disabled paths and transfers must persist.

Please report game and mod versions, relevant dependency versions, steps, expected/actual result, screenshot, and whether a Retrofit was already queued. Do not post private campaign files publicly.

## Automated verification and limits

The exact supplied runtime passed **499 domain/native assertions**, with zero compiler warnings/errors. Coverage includes actual native Save/Upgrade/Copy, migration, Matrix fault injection, stale/retry/cancel/concurrency, mission/waiting/active-yard guards and native campaign save/load. Integration cases use Ship Logistics 1.0.40, Freight Dispatch 0.2.4 and Refreshed Shipset Colors 1.1.0.

These checks use isolated fixtures, test font metrics and GPU presentation stubs. They do not establish live rendering, fleet movement or gameplay FPS. The original campaign fixture is not distributed; native checks require a compatible disposable fixture and the relevant installed dependencies.

Known native Save boundary: if the game publishes a new component design and the later ACM metadata commit fails, that new project may remain saved without a series. ACM reports the failure and preserves the previous series/target/path group. Full native component-publication rollback is not claimed.

The safe View redesign and per-selected-ship cross-series Retrofit commands are not included.

## Building the source

Install the .NET 8 SDK and supply your own supported game installation. From the repository root:

```powershell
dotnet build AdvancedConstructionMod/AdvancedConstructionMod.csproj -c Release -p:GameRoot="C:/Games/Steam/steamapps/common/Distant Worlds 2" --disable-build-servers
```

Run only one build/test at a time. The root `scripts/run-check.ps1` wrapper supplies BelowNormal priority and DOTNET_PROCESSOR_COUNT=2. The check project is `AdvancedConstructionMod/check/Check.csproj`; its private native fixture is supplied as a command-line argument, not obtained from this repository. No proprietary game/dependency DLLs are redistributed.
