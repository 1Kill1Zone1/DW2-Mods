# ACM + Coherent R&D — full XL test release RC2

For Distant Worlds 2 Steam Beta 1.3.8.7, Advanced Construction Mod 0.6.1 and Coherent R&D 1.0.43. RC2 identifies the paired test binaries. We are requesting a live full-XL pass.

## Install the test build

Use a copy of an XL campaign. Close the game and launcher. Back up your local ACM and Coherent mod folders. Extract the runtime ZIP over those local mod copies, keeping the rest of the Coherent 1.0.43 package. Enable one copy of each mod with Positronic Matrix and your normal full XL dependencies. Enable **Edit auto-design presets** for editor testing. It defaults to off. The checksum file covers the downloadable handoff assets. The AI review notes include the expected DLL hashes.

## Cumulative integration changes

- ACM owns the upgrade and retrofit path of a design assigned to its series. On mixed hulls it asks Coherent to review unclassified candidates after its own generation pass. Both mods use version 1 launcher LiveChannels without an assembly dependency. Review SeriesAutomation.cs, CoherentMissions.cs, MissionChannels.cs and Entry.cs. The implemented bridge hands off candidate design IDs. The early proposal for public series-name and series-target query channels is not a prerequisite.
- Coherent exposes stored mission ID, display text, inheritance on copy, eligible missions for the hull, detached preview, save and conditional review of unclassified designs. ACM exposes the current editor mission, preview and refresh. Check behavior when a channel is absent, a design already has a successor, the editor is cancelled or a second research review runs.
- The Coherent startup check also reads the actual launcher's active code entries so that ACM is detected even when its assembly loads later. The generic Coherent lineage hooks step aside. The unclassified review requires the appropriate Ship Design and Design Lineages settings. A narrowly scoped ACM hook claim covers the reviewed generator combination.
- Players can select a mission and preview equipment even with general Coherent redesign disabled. Upgrade preserves an inherited series and restores its name on rejected reassignment. Copy as New allows another series. Edit follows the game's equipment lock. Closing without Save discards the draft. The Mission panel takes precedence over the fallback selector near Retrofit Path.
- Designs has distinct Series, Spec and Role columns. Spec shows the saved mission's display name. The role cell suppresses a duplicate specialization suffix when the Spec column already displays it.
- Optional in-game presets cover range, stance, PD share, larger PD bays, reserve for other weapons, hangars retained, fleet support and preferred main weapon family. The weapon-family list uses researched choices. Automatic stance leaves the stance override unset. Main family, stance and scope use selectable rows in the active modal. Percentage controls support typing and compact stacked steppers. Before Apply, the PD and hangar counters reflect a detached design for the current hull. The reserve counter shows bays protected from PD. Weapon details are available on hover. With an explicit 100% PD preset and zero reserve the generator may use all compatible bays.
- Rules are stored for the player's empire and mission in the current campaign. Hull ID overrides class, class overrides all classes, and Coherent supplies the fallback. Earlier class rules retain their keys. Save preset applies the preview and changes that scope, Inherit clears it, Apply preview changes the editor draft, and the game Save publishes the design. A distinct Patrol Escort hull can inherit the class rule while a Heavy Escort has an override.
- The latest editor fix rebinds the native Design Overview panel to the working design when a mission replaces the draft. Previously Space and validation updated, but the overview could still show the original size.
- ACM's selected-ship Retrofit to Series window supports mixed-role assignments, review of exact targets, an explicit Send, row-wide checks and an independent status/details action. Back preserves scroll and selection. Port routing is a future design topic.

## Changes since RC1

- Apply preview stages the rule for the current design. A successful Save and Exit commits the last applied rule for the selected mission and scope. Cancel discards the staged edit. Save preset applies the loadout to the open draft and writes the selected campaign rule. Reset loads the built-in Coherent values into the form. Inherit removes the saved override at the selected level, keeps the form open and shows the effective values. Precedence runs from hull variant to class to all classes to Coherent defaults.
- The range calculation uses researched weapons from the preferred main family that fit the hull when available. An explicit 0% PD rule can remove the policy’s default turret. When PD and reserve would exceed 100%, editing one lowers the other. Smaller totals are allowed. A positive PD target is limited by eligible bays and the reserve for other weapons.
- The editor forecasts the fitted PD count as percentages change, before Apply. The reserve counter shows how many bays the rule protects from PD. Hovering it lists the projected weapons. A reserved bay can remain empty. The fields accept decimal points and commas.
- Combat stance, weapon family and rule scope open lists beside their fields. The small stacked arrows change percentages. Hull mesh labels such as `#engine1` are suppressed while the preset form is open.
- The retrofit editor opens Series target at the button. Edit sources explains how to confirm a series when that prerequisite is missing. Transfer instructions identify the affected design versions and the action that commits the move.
- RC1 also fixed a stale Design Overview after mission selection. The preview now rebinds the overview to the active draft. The player confirmed the original Size issue was fixed on Vanilla. Please include this regression on XL.

## Verification and outstanding issue

- On the four-bay Vanilla frigate, Carrier with Beams and 0% PD survived Save and Exit and Copy as New. The regenerated copy had two beam weapons and zero PD. The latest editor integration suite passed 279 domain/native assertions. The ACM suite passed 919.
- In a separate native research run (105 assertions) an ACM-series Picket and an unclassified Standoff on hull 1037 each received one successor. A second review created zero extra frigates. Automatic retrofit stayed within the appropriate series.
- During manual Vanilla testing the player retained four Buckler PD on Zamlik through Edit, Upgrade and Copy as New, confirmed the preset selectors, and observed the linked percentages responding. Large mounts require Allow PD in larger bays.
- **Open hangar issue.** Carrier on Zamlik v4 retained one Small Hive Hangar with hangars set to 0%. Picket showed zero of one at 100%. Please reproduce on XL and record the template, bay count, forecast and fitted parts. A configurable hangar on a Line frigate is a future proposal.
- The player confirmed the earlier overview sizing fix. The previous live launcher audit and earlier isolated Mortalen XL research test predate RC2. Live full-XL editor, research, campaign reload and launcher compatibility checks remain open.

## Downloads

The runtime ZIP includes the install overlay, patch notes and AI review instructions. The cumulative Coherent source patch and AI review are also separate downloads. The tag includes ACM source and checks.
