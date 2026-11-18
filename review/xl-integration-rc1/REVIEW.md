# Full XL review for Mordachai

This is an integration candidate for Distant Worlds 2 Steam Beta 1.3.8.7, Advanced Construction Mod (ACM) 0.6.1 and Coherent R&D 1.0.43. Please review the Coherent changes against your current XL branch and play through the scenarios below with the full XL content pack. RC1 identifies this pair of builds. The original mod version numbers are retained for launcher compatibility.

## Install and reproduce

Use a separate test profile and a copy of an XL campaign. Install the normal full XL content pack, its dependencies, Positronic Matrix and Coherent R&D 1.0.43. Close the game and launcher. Back up the two affected mod folders. The runtime ZIP contains a complete ACM runtime and an overlay for Coherent. Copy the ACM folder into the managed mods directory. Apply the four files under 3819331138 to a **local test copy** of Coherent 1.0.43, retaining its other files. Enable exactly one copy of each mod. Keep the XL profile's dependency order. Verify the DLLs against payload-sha256.txt. For the preset-editor scenarios enable Edit auto-design presets. It defaults to off. To revert, restore the backed-up folders and return to the original profile and save.

## Code handoff

The tag contains the paired ACM source in AdvancedConstructionMod, its test harness under check, and shared helpers in Shared. The cumulative Coherent patch in this directory applies at the root of the author's DW2-XL repository at commit 11394f6b38eb36aa753622d5a9941b6b0a67935e. The unchanged shared Coherent source is supplied by that checkout. The patch was replayed on the original source. To inspect and build in PowerShell, from that checkout:

~~~powershell
git apply --check PATH/TO/coherent-1.0.43-xl-review.patch
git apply PATH/TO/coherent-1.0.43-xl-review.patch
dotnet build dll/DW2CoherentResearch/DW2CoherentResearch.csproj -c Release "-p:DW2GameFolder=$gameRoot"
~~~

Set $gameRoot to the matching Steam beta game directory. From this GitHub tag build ACM with dotnet build AdvancedConstructionMod/AdvancedConstructionMod.csproj -c Release "-p:GameRoot=$gameRoot". The Coherent patch preserves Steven S. Wolf's license. [Pinned upstream source](https://gitlab.com/coyote-wolf/DW2-XL/-/tree/11394f6b38eb36aa753622d5a9941b6b0a67935e/dll/DW2CoherentResearch)

## Behavior and ownership

- ACM owns the upgrade and retrofit path of a design assigned to its series. On mixed hulls it asks Coherent to review unclassified candidates after its own generation pass. Both mods use version 1 launcher LiveChannels without an assembly dependency. Review SeriesAutomation.cs, CoherentMissions.cs, MissionChannels.cs and Entry.cs. The implemented bridge hands off candidate design IDs. The early proposal for public series-name and series-target query channels is not a prerequisite.
- Coherent exposes stored mission ID, display text, inheritance on copy, eligible missions for the hull, detached preview, save and conditional review of unclassified designs. ACM exposes the current editor mission, preview and refresh. Check behavior when a channel is absent, a design already has a successor, the editor is cancelled or a second research review runs.
- The Coherent startup check also reads the actual launcher's active code entries so that ACM is detected even when its assembly loads later. The generic Coherent lineage hooks step aside. The unclassified review requires the appropriate Ship Design and Design Lineages settings. A narrowly scoped ACM hook claim covers the reviewed generator combination.
- Players can select a mission and preview equipment even with general Coherent redesign disabled. Upgrade preserves an inherited series and restores its name on rejected reassignment. Copy as New allows another series. Edit follows the game's equipment lock. Closing without Save discards the draft. The Mission panel takes precedence over the fallback selector near Retrofit Path.
- Designs has distinct Series, Spec and Role columns. Spec shows the saved mission's display name. The role cell suppresses a duplicate specialization suffix when the Spec column already displays it.
- Optional in-game presets cover range, stance, PD share, larger PD bays, reserve for other weapons, hangars retained, fleet support and preferred main weapon family. The weapon-family list uses researched choices. Automatic stance leaves the stance override unset. Main family, stance and scope use selectable rows in the active modal. Percentage controls support typing and compact stacked steppers. Before Apply, the parenthesized counts come from a detached generated design for the current hull. Weapon details are available on hover. With an explicit 100% PD preset and zero reserve the generator may use all compatible bays.
- Rules are stored for the player's empire and mission in the current campaign. Hull ID overrides class, class overrides all classes, and Coherent supplies the fallback. Earlier class rules retain their keys. Save preset changes that scope, Inherit clears it, Apply preview changes the editor draft, and the game Save publishes the design. A distinct Patrol Escort hull can inherit the class rule while a Heavy Escort has an override.
- The latest editor fix rebinds the native Design Overview panel to the working design when a mission replaces the draft. Previously Space and validation updated, but the overview could still show the original size.
- ACM's selected-ship Retrofit to Series window supports mixed-role assignments, review of exact targets, an explicit Send, row-wide checks and an independent status/details action. Back preserves scroll and selection. Port routing is a future design topic.

## Reported bug: Design Overview / Size freezes after mission selection

On Zamlek v4 (Frigate, capacity 450), the player saw matching values before selecting a mission. After selecting one, the upper Space read 459/450 and the right-hand Size stayed at 388/450. Installing a size-10 Buckler PD raised Space to 469/450 while Size still showed 388/450. The engine correctly reported an oversized design. Inspection of the native editor showed UpdateData refreshes the existing summary binding while the preview swaps the draft object. RC1 explicitly binds the overview to the replacement draft, including restoring the old binding on a rejected ACM preview. The native integration suite passed 212 assertions, including a check that the overview points at the same draft as the editor. Live rendering remains to be confirmed. We will own the follow-up and fix any remaining mismatch regardless of whether its source is ACM, Coherent or the native editor. Please include this case in your full XL report.

## Requested XL acceptance pass

1. Record the game, launcher, full XL and mod versions, mod order, faction, hull ID, mission and relevant settings. Confirm both mods initialize and the mixed-hull channel registers with general rules enabled. Check red conflicts after the session refreshes.
2. On one hull create a Picket in an ACM series and a Long (Standoff) outside any series. Enable hull automation and the two general Coherent settings. Research a component improvement that fits both. Expect one successor per design, same mission, correct series membership and matching retrofit paths. Trigger another review. Expect zero extra successors. Repeat with multiple series and automatic retrofit.
3. With general redesign disabled, switch mission on an editable copy. Check preview and cancel, then repeat with Save, Upgrade, and a locked design. After every mission switch compare Space and Design Overview / Size. Manually add or remove a component and compare both again, including a deliberately oversized design. Verify warnings and Save availability agree. This last visual regression needs a live retest.
4. On two XL hull variants with different bay counts, check 0%, intermediate and 100% PD, with reserve and larger bays both ways. Compare predicted versus fitted counts. Check the preferred weapon family persists across reopen and campaign reload. Inspect actual part sizes, research and fitting constraints when counts differ. Test each scope and remove its override.
5. Verify selectors, stepper hit areas, Apply preview, Inherit, checked rows, status details and Back at your UI scale. Repeat with the preset feature disabled. For a failure please report exact steps, expected and observed results, the two design IDs where relevant, and which mod handled the upgrade.

## Evidence and open verification

The last real Steam Beta Vanilla launcher audit covered 44 enabled mods and 36 observed code modules, with zero red conflicts, zero claim problems and zero file collisions. The player confirmed the red indicators disappeared. These observations preceded the final overview fix. An earlier isolated full-XL Mortalen fixture upgraded Vortex Engines from level 2 to 3 on hull 31, produced one Picket and one Long, and produced zero on repeat. The newer editor and preset changes still require the full XL gameplay pass. No campaign saves are included.
