# Changelog

## ACM 0.6.1 RC3 — independent Coherent redesign on a shared hull

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


## 0.4.0 — 2026-11-15 — independent Series component automation

- Store independent Hull Upgrade Automatic/Manual modes per Series and native role. Initialize old campaigns from native settings without unexpectedly enabling automation; respect global design automation.
- Upgrade the latest active project using native component selection and researched component levels. Retain hull, component count, bays, layout, tactics and weapon families. Hull changes remain manual; skip unsafe/no-op upgrades with a tooltip reason.
- Protect classified hulls from native template replacement. Preserve other native hull generation and AI behavior. New versions retain Series, disabled paths and AllowAutoRetrofit; advance only the upgraded explicit target and preserve a different older pin.
- Refuse conflicting queued retrofits/stale state; roll back metadata, design publication, native arrows/flags and latest-design caches on write or post-commit failure. Repeated reviews create no redundant versions or revisions.
- Read metadata1/2 and initialize schema3 modes once. Rollback to0.3.x requires the pre0.4 campaign checkpoint. No silent repair of an existing obsolete Series target.
- Retain0.3.1 current-project wording and guided transfer UI. Serial native/domain, owner-save-copy and FD/Ship Logistics/Shipset Colors verification recorded in the canonical feature card. Live GPU and actual research progression remain owner acceptance.

## 0.3.1 — 2026-11-15 — prepared interface correction

- Display an obsolete current project's actual saved series destination; do not present its edit-validation error as a claim that the selected series target is obsolete. Distinguish Current project in edit warnings.
- Explain transfer scope with native tooltips, numbered receiving-series/remaining-target/incoming-target steps and contextual picker help. Disable unavailable target actions before a receiving series is chosen; explain empty remaining roles.
- Keep transfer/target/activation/data rules unchanged. Native Hull Upgrade click is verified to change only the shared role/level policy. Its background template generator is separately reproduced as an unclassified-project/obsolete-target risk and is not fixed in this interface patch; use Manual for classified branches pending the agreed feature planning.
- 522domain/native assertions passed,0build warnings/errors; exact installed0.3.0 reproduces the misleading obsolete-source message. Real latest save read on a verified copy only; live game and installed files remain untouched during preparation.

## 0.3.0 — 2026-11-15 — prepared series targets and transfers

- Add Automatic latest, explicit own compatible target and No target per series/native role. Explicit older targets preserve equipment/date; obsolete activation is previewed and committed with the group.
- Advance a pinned target only after saving Upgrade of that target. Native Copy as new, New and other Upgrades retain the existing pin. Preserve disabled paths and native AllowAutoRetrofit.
- Add single-project and whole-series transfers, including obsolete generations, pinned destination heads, remaining-source target choices and a paged preview of all source/destination projects, counts, targets, paths and activation. Invalid destination pins require deliberate correction; no silent incoming replacement.
- Make Sources opaque with seven separate columns, explicit project/series/staged status and full paged details. Retain independent checkbox/row selection and hidden search choices.
- Guard pre-Save and Apply against queued/active retrofits. Serialize mission/yard admission with group publication; preserve previous group on rejection, stale data and storage failure.
- Read version 1 metadata and migrate on editing to version 2. Rollback to 0.2.2 requires a pre-0.3.0 campaign slot; replacing the DLL alone is insufficient.
- Retain the native partial-save boundary: a component design can remain newly published but unclassified after ACM commit refusal; report it explicitly and retain the prior target/path/membership group. Safe View redesign and manual per-ship cross-series orders remain deferred.
- 499 isolated domain/native assertions passed with zero build warnings/errors, including actual Copy/Save/Upgrade, fault-injected target retention, queue/active-yard rejection, four-page complete long-name details and native campaign save/load. Final evidence is recorded in the canonical mod document. Candidate preparation only; no installation or live GPU acceptance is implied.

## 0.2.2 — 2026-11-15 — prepared interface correction

- Fix actual pointer selection: the full-width Series label intercepted confirmation/dropdown hits. Use one non-overlapping Series/input/OK/dropdown/Rename row, including readable OK text in place of the unsupported checkmark.
- Measure long-name/error status only when needed; remove permanent blank summary rows and retain source-button hit regions during reflow.
- Show Next Ship in Series and the effective read-only destination/reason for assigned series; restore native selection without a series. Pending metadata keeps View disabled with an explanation, then refreshes native availability after publication.
- Coordinate checks reproduce the old failure on exact installed0.2.1. Full chain covers native focus/keyboard, OK, sources, Use choices and Save and Exit, including an unmodified native DoSave body with only renderer presentation replaced.
- 388 assertions pass with zero build warnings/errors. Metadata, latest/disabled rules, component Upgrade and saved schema remain unchanged. Package preparation only; live GPU acceptance remains separate.

## 0.2.1 — 2026-11-15 — prepared crash fix

- Initialize editor confirmation tooltips through native AddHoverTip; raw HoverTip construction left title/body fonts empty and crashed native word-wrap rendering.
- Initialize fonts, colors and margin on Name/Series/clipped-cell row tooltips that have no owning control to generate them.
- Keep series/retrofit policy, component Upgrade and metadata unchanged. Exact installed 0.2.0 reproduces the new native tooltip regression; 330 assertions pass on the fix. Live GPU rendering is still separate acceptance.

## 0.2.0 — 2026-11-15 — prepared update

- Full-width inline Series input with confirm checkmark, native dropdown and separate Rename above it; full name wraps below. Lost focus never creates a series, and Save/Apply blocks unconfirmed input.
- Separate latest-compatible series policy and effective target; stock This Design Upgrades To remains intact. Latest in the assigned series overrides old fixed/native modes, preserving disabled paths and native automatic-retrofit permissions. Date/ID ordering prevents downgrades, including equal-date cases.
- Resolve latest before fleet/ship assignment and resource costing; reject stale late yard targets. No immediate orders or native component-upgrade changes.
- Restore original Designs Name width, retain total row width and Series sorting, and show full selected names in a visible details area.
- Six-column source table with current/effective new target, full selected-row details and separate checkbox handling. Preserve search selections, preview, cancel and atomic application.
- Retain all 0.1.3 regressions and metadata schema 1. 317 isolated assertions pass, including native save-load and current SL/FD paths; live GPU acceptance and installation remain separate.

## 0.1.3 — 2026-11-15 — prepared update

- Retain explicit Upgrade-origin deselection through Use choices, reopening, Save preflight and commit; Cancel leaves the original path untouched.
- Refresh an open Series-sorted Designs list after successful metadata publication, preserving filters, direction, native Design selection and source subset.
- Preserve committed data if UI refresh fails; failed metadata commit retains the old name and does not invoke a success refresh.
- Remove the XX limit from valid canonical Roman generation suffixes. Invalid Roman forms, identifiers and complete Roman-looking names remain unchanged.
- Improve Series label spacing and shorten the pending upgrade caption; upgraded designs renamed to Mk15 keep their inherited series.
- Same saved metadata schema and native retrofit rules; Steam beta1.3.8.7 only. Installation remains separate.

## 0.1.2 — 2026-11-15 — prepared update

- Inline Series textbox and native dropdown in Retrofit Path; separate explicit Rename action, no assignment modal.
- Editable series suggestion from an unassigned design name; trailing vN/Roman generation removed, existing Upgrade series preserved. Duplicate names reuse the existing ID.
- New projects and Upgrades select incoming sources before first Save. Use choices returns to the editor with an incoming-project preview; editor Cancel discards pending edits.
- Stable Edit sources caption; staged series no longer displays a misleading native target from another branch.
- Native Series header styling/baseline and ASC/DESC sort with missing last, stable within-series order and selected-project preservation. Native row draw route uses the Name baseline for Series.
- Remove superseded pending series names and restore prior staged sources after rejected Apply; Cancel followed by Save cannot commit canceled choices.
- Existing metadata schema, retrofit protection and native sorts/filters remain; Steam beta 1.3.8.7 only.
- 242 isolated assertions passed; native keyboard, popup selection, DrawItem emission and campaign save/load covered. Live GPU acceptance remains separate.

## 0.1.1 — 2026-11-15 — candidate, not installed

- Fix hidden native controls: SetupButton defaults Visible=false; explicit visibility now covers editor and modal controls.
- Allow series/path metadata on own in-use projects while retaining native component locks.
- Use Series consistently and show Name → Series → Role in the own Designs table; preserve native positions, sorting and Design instances.
- Fit long names/series with full hover values; guard foreign metadata.
- Discard staged changes on editor close/rebind, preserving commit when native close occurs during Save.
- 235 final native/domain/callback assertions, zero build warnings/errors. GPU rendering remains manual acceptance.

## 0.1.0 — 2026-11-14 — prepared candidate, not installed

- Native Retrofit Path extension: Construction Class picker/create/rename and reverse source selection with search, checkboxes, preview and Apply/Cancel.
- Staged new-version inheritance; independent class branches across normal Retrofit, fleet templates, native assignments and yard admission.
- Native latest modes and legal same-role hull changes; obsolete-generation chains remain within their class.
- Version/facts stale checks, cycle/ownership/class validation, verified two-bank Matrix persistence, atomic source edits and queued-retrofit conflict refusal.
- Save preflight protects an existing main save and Shipset Colors companion before file opening; nested/failing save scopes release their gates.
- Steam beta 1.3.8.7 only. Positronic Matrix required. Native role, research, ship stats and AI design generation preserved.
- Isolated native/callback verification; live GPU rendering and gameplay acceptance remain separate.
