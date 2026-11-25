# Community Patch 1.0 and more

One entry in DW2 Mod Launcher for ten modules. Requires Distant Worlds 2 Steam Beta 1.3.8.8, DW2 Mod Launcher 1.0.44 or later, and Positronic Matrix. Keep XL Galaxy Generation as a separate add-on for new campaigns.

In Economy, hover the source count to point to surveyed deposits on the map. The denominator counts only bodies where the resource has been discovered.

Click a resource's **Stockpile** amount to see the stores that hold it, cargo aboard your freighters, and scheduled pickups. Use the arrows to set priority from 1 to 10 and optional ceilings by ship count and percentage of your freighter fleet. Priority starts at 5. When both ceilings are active, the lower ceiling applies to new ordinary assignments. Existing trips can finish. The choices are saved with the campaign.

In **Esc → Settings → Camera** or **Mod Settings → Camera**, **Show Me view** and **Ship focus view** each offer **Keep current zoom**, **System Map**, **Galaxy Map**, and **Game default**. Show Me defaults to System Map, which frames a known system. Ship focus defaults to Keep current zoom and applies to camera jumps from the ship list and numbered ship or fleet hotkeys. For ships in deep space, System Map opens a centered Galaxy Map or keeps the current galaxy scale when that map is already open.

Choose **System zoom speed** and **Galaxy zoom speed** independently. **Game** follows the regular game preference. **1x–4x** set the speed relative to the factory zoom speed. Both settings work with the wheel and Page Up / Page Down.

**Wheel acceleration** starts off. Choose **2x**, **3x** or **4x** to cap the speed reached during a burst of quick wheel steps in one direction. Slow steps use the base speed. A pause of about 200 ms, a direction change, or switching between the system and galaxy maps starts a new burst at the base speed.

**Wheel zoom smoothing** starts **Off**. **Short** eases into the chosen scale over about 120 ms, and **Soft** over about 220 ms. **Inertia** uses the Soft timing and adds up to a quarter of the last step after a fast scroll. Reversing the wheel immediately starts moving in the other direction. This setting applies to the mouse wheel on both maps.

## Install and load order

1. Save to a separate slot, then close the game and launcher.
2. For the local ZIP, extract the CommunityPatch folder into the game's mods folder. For a Workshop release, subscribe to the Community Patch item.
3. In the active profile, enable Positronic Matrix and your chosen Steam Beta-compatible companion mods. Place Community Patch after those companions. The launcher's dependency view lists the optional integrations and their order.
4. Disable the individual entries for the modules listed below and enable Community Patch. Keep your previous profile and individual mod folders for rollback. If a duplicate is enabled, the pack reports its name and skips module initialization.
5. In Mod Settings, check the Community Patch sections. Both fleet refuelling options start off. Copy your existing choices before switching profiles. Launch through DW2 Mod Launcher and load the separate save slot once the launcher reports a successful start.

On Windows, with the launcher closed, you can copy the saved settings before opening the new entry for the first time.

```powershell
.\CopySettings.ps1 -SettingsFolder '<your launcher data folder>\ModSettings'
```

Add `-WorkshopId <published item ID>` for a Workshop installation. The command copies only keys supported by the pack. It leaves the individual modules' settings files in place and refuses to overwrite an existing pack settings file. You can also review and set the two options directly in the launcher.

Optional companions are Freighter Logistics, Ship Logistics, Galaxy Waypoints, Diplomatic Corps, Troop Logistics, War Chest and Coherent R&D.
If using standalone Freight Dispatch, load it after Community Patch. Load Advanced Construction Mod after Freight Dispatch when using both.

If you already used the fifteen-module local preview, re-enable your standalone copies of Advanced Construction Mod, Battle Log, Freight Dispatch, Galaxy Waypoints and Weapon FX Tuner for any campaign that uses them. Keep the standalone Weapon FX Tuner build with the Pulse Wave rollback bundle for saves that depend on those legacy effect names.

## Features

| Module | What it does and where to use it |
| --- | --- |
| Cancellation Refunds | Returns the unused share of a recorded state payment when cancelling a new unfinished ship, orbital base, colony facility or troop recruitment. Use the ordinary cancellation command. Scuttle also works for an eligible unfinished hull. |
| Colony Event Display Fix | Refreshes grouped colony-event tooltips when the contributing colonies change. |
| Colony Support Rules | Residents under Exterminate contribute no colony revenue or tax base and incur 20% of their share of Colony Support. |
| Deep Space Stability and Refuel | Stabilizes deep-space coordinates. Offers two independent manual-fleet refuelling options. |
| Freight Visibility | Adds actual cargo and freighter counts to resource production and runway tooltips. Shows foreign carriers separately. |
| HyperDeny on Move | Allows active player HyperDeny to block enemies at war while the ship or fleet follows a manual Move order. |
| Inherited Ship Retirement | Applies Never enlistment and at-base disassembly to eligible ships inherited when conquering an empire's last colony. |
| Racial Research Unlock | Unlocks a race's frontier research for an empire that owns colonies holding at least three billion residents of that race. |
| Resource Intelligence | Buys information on one buyer-unknown deposit through empire or pirate diplomacy. Price reflects scarcity, shortage and relations. |
| Retirement Research Report | Shows the research gained from each ship disassembled at a base, with technology icons and progress. |

## Refuelling settings

- **Keep gathering while refuelling** preserves the destination when a manual fleet receives Move followed by Refuel from Fuel Tankers. Nearby eligible ships get served first. Stop or a new order ends the operation.
- **Gather and refuel here** adds a manual fleet order to the map context menu. The fleet needs a tanker. Hold Shift to add the rendezvous to the order queue. The next order waits until refuelling ends.

Use **Discussions** on the Workshop item to report a bug or suggest a feature. For bugs, include the game build, Community Patch version, active companion mods, steps and expected versus actual behaviour.

## Salvage and Refit in 1.0.20

Open the removable-module icon on an owned ship or station with working unfamiliar equipment. Choose a compatible empire design. A ship goes to a yard and an automated constructor attends a station on its orbit. The ordinary retrofit handles its cost and resources. Unknown components actually removed use the ordinary ship disassembly research formula. Full station dismantling is a separate confirmation. Cancel while the station survives to keep research and use Repair dismantled bays to fit compatible known parts. Missing replacements are shown before confirmation. Repairing those bays awards no additional research.

The ten bundled modules and the Salvage and Refit feature have individual launcher switches. The camera's Galaxy 3× / System 2× preset changes the two wheel speeds together. Apply writes camera choices to the active profile.

## Plans and open checks

AI decisions about salvage and refit are planned. AI war-planning waypoints are an idea under consideration. Live intersystem travel, dialog readability and physical wheel feel still need gameplay feedback.
