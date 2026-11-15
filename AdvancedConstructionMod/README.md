# Advanced Construction Mod 0.3.0

Prepared for Steam beta 1.3.8.7. Installation is a separate owner decision.

Keep independent ship-design series such as Hura and Little Devourer, choose the intended retrofit target, and move saved projects between series. Native roles, legal hulls, components, research, costs and retrofit permissions remain authoritative.

## Series and targets

In the normal Retrofit Path panel, enter a Series name and press **OK**, or choose an existing series from the dropdown. A suggested name is not an assignment. OK stages the choice; **Save / Apply** publishes it. Rename changes the shared series name. Cancel discards uncommitted choices.

Each series has a separate target for each native role:

- **Automatic latest** chooses the newest active, researched, buildable compatible project by date, then ID. Existing version-1 campaigns retain this behavior.
- An **explicit project** can be older than the source. Choosing an obsolete project visibly previews its activation; it becomes Active only on successful Save / Apply. Its components, name and creation date are unchanged.
- **No target** disables the series destination without removing membership. Native disabled source paths remain disabled independently.

**Use this project** stages the current design as target. A successful native Upgrade of the explicitly selected target advances that target to the saved new generation. Copy as new, a new project, and Upgrade of another generation do not replace it. The target never retrofits into itself. Invalid or missing explicit targets fail closed; they do not silently become Automatic latest.

## Move project or entire series

**Move project...** transfers only that saved project's membership; every ship built from it then uses that project's new series. **Move entire series...** transfers every generation, including obsolete projects. Neither action changes ship equipment or sends ships to a yard.

Choose an existing destination and review the paged plan. It includes source/destination memberships, every project in both affected series, built-ship counts, current/future targets, changed native paths, automatic-retrofit flags and any target reactivation. The destination target is explicitly pinned, so a newer incoming project cannot displace it. A destination with no valid explicit target requires a deliberate correction. Only an empty automatic destination can receive an initial head from compatible incoming projects.

If the source's explicit head moves, choose a compatible remaining target, Automatic latest, or No target. Empty source series remain available. Enabled cross-series paths are reconciled within the resulting series; disabled paths and native AllowAutoRetrofit stay unchanged. Incompatible/unresearched transfers are rejected as a whole. **Use transfer plan** returns to the editor; Save / Apply commits the complete group.

## Sources

**Edit sources...** uses an opaque native panel with separate checkbox, Project, Series, Hull, Current target, After Save / Apply and Ships columns. The heading identifies the edited project, its series and staged status. Current and proposed destinations respect the effective series target, which may differ from the project named in the heading.

Click a project name for full details; click its checkbox to change selection. Long details wrap and use Previous/Next pages. Search retains hidden selections. For new projects, **Use choices** returns to the draft; Save publishes. For a saved project, **Apply** publishes the staged group. An explicitly unchecked Upgrade origin remains unchecked after reopening and Save. Native component locks remain enforced.

## Safety and persistence

Retrofit assignment, fleet-template selection and yard admission enforce the effective destination. An existing Retrofit mission, waiting entry or active construction yard blocks conflicting series changes with the ship's name. Cancel the order or wait for completion. The mod does not silently retarget an existing queued operation. Admission and series publication share a transaction gate.

Per-campaign/per-empire metadata is stored through Positronic Matrix using verified, checksummed dual banks. Revision/source checks reject stale changes. A failed group commit retains old membership, target policy, native paths and activation flags. Repeated Apply cannot overwrite a newer revision. Corrupt metadata is retained and fails closed.

**Native Save boundary:** the game publishes the component design before ACM's metadata commit. If that later commit fails, the newly saved native design can remain unclassified; ACM explicitly reports this. The previous series/target/path group and Upgrade origin status are retained, and the explicit target does not advance. This is not a rollback of native component publication.

Version 1 metadata is read without forced conversion. The first ACM edit writes version 2 with per-role targets. **ACM 0.2.2 cannot read version 2 campaign metadata. Keep a separate pre-0.3.0 campaign slot before saving changed series; reverting DLL files alone does not downgrade a campaign.**

## Boundaries and verification

Steam beta 1.3.8.7 only; the build pins DistantWorlds.Types.dll SHA-256 `787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254`. Requires Positronic Matrix and DW2 Mod Launcher. No new View card or manual per-selected-ship cross-series Retrofit command is included. Native View retains its existing pending-save restrictions.

499 isolated assertions passed with zero build warnings/errors. Checks cover domain rejection/selection, native coordinate input and pagination, actual Copy/Save/Upgrade lifecycle, obsolete targets, single/whole transfers, queue/active-yard guards, failure/retry/concurrency, legacy metadata and native campaign save/load. Existing actual Ship Logistics 1.0.40, Freight Dispatch 0.2.4 and Refreshed Shipset Colors 1.1.0 checks remain. See TESTING.md for reproducibility and manual acceptance. Builds/checks run sequentially through the root scripts/run-check.ps1.

Native UI tests use test-only font metrics and replace GPU presentation leaves. Live GPU rendering, fleet travel and FPS remain manual acceptance. No campaign fixture is distributed. Run native checks only on a disposable compatible fixture.
