# Advanced Construction Mod 0.4.0

Checked local build for Steam beta 1.3.8.7. Install only while the game and launcher are closed.

Keep independent ship-design series such as Hura and Little Devourer, choose the intended retrofit target, and move saved projects between series. Native roles, legal hulls, components, research, costs and retrofit permissions remain authoritative.

## Independent automatic Series upgrades

For a project with a Series, **Hull Upgrade** in Designs now controls only that Series and native role. Two Escort series can use different Automatic/Manual settings. Existing campaigns inherit the current native setting once; global ship-design automation must also be Automatic.

After research, each enabled Series upgrades its latest active project. The hull, component slots, counts, layout, tactics and weapon families stay the same. Native component selection and researched improvements are used; a different equipment category is not inserted in place of a scanner. Change hulls manually. Unsafe or unavailable improvements are skipped with a reason in the Hull Upgrade tooltip. Repeated reviews without improvements create no new version.

The new design retains its Series and automatic-retrofit permission. An explicit target advances only when that target was upgraded; another older pinned target remains selected. Automatic latest follows the new generation. No target and disabled source paths remain disabled. This creates projects, without sending ships to a yard. A conflicting Retrofit order/queue waits rather than silently retargeting it.

Classified hulls are protected from native template replacement; other hulls and AI retain native generation. Existing obsolete targets are not repaired silently: choose the intended active project as the Series target explicitly.

## Interface correction retained from 0.3.1

Reading an obsolete current project now shows its saved series destination rather than substituting the current project's edit-validation error. An obsolete current project and an obsolete series target are different states; edit validation now explicitly says Current project.

Transfer actions explain the difference between moving this project and moving all generations in the series. The transfer screen has numbered steps: receiving series, target for projects left behind, and target for projects being moved. Target selectors are unavailable until a receiving series is chosen; empty remaining groups say No projects remain. Native tooltips and visible helper text explain Automatic latest, No target, staging and the absence of immediate ship orders.

The native template replacement reproduced on 0.3.0 is handled by the Series-aware automation in 0.4.0. The separate current-project/target validation and guided transfer controls from 0.3.1 remain.

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

Versions 1 and 2 metadata remain readable. Initializing independent modes writes version 3, preserving memberships and targets. **ACM 0.3.x cannot read version 3 campaign metadata. Keep the pre-0.4 campaign checkpoint for rollback; reverting DLL files alone does not downgrade a campaign.**

## Boundaries and verification

Steam beta 1.3.8.7 only; the build pins DistantWorlds.Types.dll SHA-256 `787E4A1B6DA8763943A18DED8E3DC29BB452BA34297CEA2E726E5951B4F3B254`. Requires Positronic Matrix and DW2 Mod Launcher. No new View card or manual per-selected-ship cross-series Retrofit command is included. Native View retains its existing pending-save restrictions.

The serial domain/native suite covers independent modes, actual native design review, same-family component upgrades, exact slot retention, global policy, repeats, queued Retrofit refusal, stale state, Matrix/post-commit failures, cache rollback, schema migration and real native save/load. Prior editor, Sources, transfer, Ship Logistics 1.0.40, Freight Dispatch 0.2.4 and Shipset Colors 1.1.0 checks remain. The supplied runtime passed **559 domain/native assertions**, with zero compiler warnings/errors. See [TESTING.md](TESTING.md) for reproduction requirements and manual acceptance.

Native UI tests use test-only font metrics and replace GPU presentation leaves. Live GPU rendering, fleet travel and FPS remain manual acceptance. No campaign fixture is distributed. Use a disposable supported fixture for native checks.
