# ACM 0.6.1 RC3 — independent Coherent redesign on a shared hull

ACM-side test build for Distant Worlds 2 Steam Beta Vanilla 1.3.8.7 and full XL integration review. Extract the ZIP into the game directory with the game and launcher closed. Enable one ACM provider and Positronic Matrix. Use your own Coherent build and a copy of your XL campaign.

## Changes since the paired RC2

- Restored live channel `advanced-construction.design-series`, version 1, delegate `Func<object,int,string>`. Pass the current Galaxy and design ID. It returns the player series name, or null for unclassified and unknown designs. Resolve the channel when needed so either mod may load first.
- Removed ACM's call to `coherent-research.review-unclassified`. Coherent owns its review schedule and redesign rules. The ACM guard now applies inside the stock generator to the exact hull IDs with active series. The shared hull-policy getter is available to Coherent.
- Restored Series controls when the native designer replaces its retrofit panel during an upgrade. The Designs list supports draggable column boundaries, saved widths and double-click reset.

## What passed on Vanilla

On Gizurean frigate hull 1037, Picket was in an ACM series with automatic series upgrades off; Long (Standoff) was outside any series. Hull automation was on and Proton Engine level 1 became available to both designs.

1. An independent test driver queried series membership and called the real RC2 Coherent redesign handler twice. Exactly one Long successor was created, with the new engine and its Long mission. Picket stayed active in its series.
2. Enabling upgrades for the Picket series produced exactly one Picket successor. Repeating the review produced zero additional frigates.
3. The contract run passed 109 assertions. The broader ACM native regression run passed 928.

## Open end-to-end check for Mordachai

The ordinary game generator with the local RC2 Coherent build produced **zero Long successors**. Coherent reported Design Lineages disabled when ACM was present. The independent driver above exercises the redesign handler; it does not validate the production scheduler.

In Coherent's own automatic review, consult the membership channel per design, skip ACM members, and process unclassified designs under Coherent's normal rules even when ACM skips its review. On full XL, repeat the two-frigate scenario using the ordinary scheduler with the test driver off. Expect one Long, zero Pickets and no duplicate on repeat. Enable the series afterward and verify one Picket with its series and mission retained.

Full XL verification is pending. The RC3 package contains ACM only. Earlier editor changes, tests and known limitations are documented in the [RC2 review notes](https://github.com/1Kill1Zone1/DW2-Mods/blob/055c8e2/review/xl-integration-rc2/PATCH-NOTES.md).

## Earlier test guide

# Testing Advanced Construction Mod 0.4.0

## Requirements and installation

Use Distant Worlds 2 Steam beta **1.3.8.7**, DW2 Mod Launcher, and Positronic Matrix. The tested DistantWorlds.Types.dll SHA-256 is `787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254`.

1. Close the game and launcher. Retain the previous ACM files and a separate pre-0.4 campaign save.
2. Download the ACM 0.4.0 ZIP from this repository's Releases and extract it into the game directory. It contains `mods/AdvancedConstructionMod/`.
3. In DW2 Mod Launcher, enable Positronic Matrix and Advanced Construction Mod; load Matrix before ACM. Launch through the launcher.
4. Use a separate test save slot.

**Save compatibility:** ACM 0.4 reads version-1/2 metadata and initializes independent modes as version 3. ACM 0.3.x cannot read version 3. Reverting DLL files alone does not downgrade the campaign; retain a separate pre-0.4 save. Existing obsolete targets are not silently repaired: select the intended active project explicitly.

## Manual acceptance

### New in 0.4.0: independent automatic upgrades

- Assign two Escort projects to different Series, e.g. Xura and Little Devourer. Set Xura Hull Upgrade to Automatic and Devourer to Manual: the other Series must not toggle. Ensure global ship-design automation is Automatic.
- Research an improvement to an installed component within its family. The latest active Auto project should produce a classified new generation with the same hull, bay positions, component counts and weapon families. Manual Devourer stays unchanged. Hull changes remain manual; targeting equipment is not inserted in place of a scanner.
- With no safe improvement, expect no extra project. Hover Hull Upgrade for the reason. Repeated design reviews must not create duplicates.
- An upgraded explicit target advances to its new version. If a different older target is pinned, it remains pinned. Automatic latest follows the new version; No target, disabled paths and native auto-Retrofit permission stay intact. No ships are immediately sent to a yard.
- Existing Retrofit missions/queues must be retained; conflicting automatic publication waits. Save/reload and confirm Series membership, independent modes, targets and slot layout.
- Compare an obsolete current project's display with its active target. Transfer controls should clearly distinguish receiving Series, projects left behind and incoming projects.

### Editor, targets, transfers and persistence

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

The exact supplied runtime passed **559 domain/native assertions**, with zero compiler warnings/errors. Coverage includes actual native CreateShipDesigns, UpgradeComponents, independent modes and global policy, exact bay retention, no-op reviews, schema2-to-3 initialization, older pins, No target/disabled paths, queued-refit refusal, Matrix and post-commit cache rollback, plus prior Save/Upgrade/Copy, transfers, stale/retry/cancel/concurrency, mission/waiting/active-yard guards and native campaign save/load. Integration cases use Ship Logistics 1.0.40, Freight Dispatch 0.2.4 and Refreshed Shipset Colors 1.1.0.

These checks use isolated fixtures, test font metrics and GPU presentation stubs. They do not establish live rendering, fleet movement or gameplay FPS. The original campaign fixture is not distributed; native checks require a compatible disposable fixture and the relevant installed dependencies.

Known native Save boundary: if the game publishes a new component design and the later ACM metadata commit fails, that new project may remain saved without a series. ACM reports the failure and preserves the previous series/target/path group. Full manual native component-publication rollback is not claimed. The new automatic-publication path separately rolls back the new project, metadata, flags, paths and latest-design caches on a handled failure.

The safe View redesign and per-selected-ship cross-series Retrofit commands are not included.

## Building the source

Install the .NET 8 SDK and supply your own supported game installation. From the repository root:

```powershell
dotnet build AdvancedConstructionMod/AdvancedConstructionMod.csproj -c Release -p:GameRoot="C:/Games/Steam/steamapps/common/Distant Worlds 2" --disable-build-servers
```

Run only one build/test at a time. The root `scripts/run-check.ps1` wrapper supplies BelowNormal priority and DOTNET_PROCESSOR_COUNT=2. The check project is `AdvancedConstructionMod/check/Check.csproj`; its private native fixture is supplied as a command-line argument, not obtained from this repository. No proprietary game/dependency DLLs are redistributed.
