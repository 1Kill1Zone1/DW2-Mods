# Changelog

## 0.3.0 RC 1

## Gather and refuel here

A manual player fleet can now stop for tanker service at a point you choose on the map. Enable **Gather and refuel here** in the mod's launcher settings, select a fleet containing a fuel tanker, then right-click an empty spot on the map. Hold **Shift** to add the stop to an existing route. The setting defaults to off and works independently of **Keep gathering while refuelling**.

- Ships can receive fuel as they arrive within the rendezvous area. Tankers use their normal cargo, transfer range and transfer rate.
- The next order waits during the rendezvous. Once the fleet has gathered, it can continue when service is complete or its tankers have no more fuel to deliver.
- Queued rendezvous coordinates and subsequent orders survive saving and loading. A manual fleet Stop cancels the active rendezvous. A fleet without a tanker shows a disabled command with an explanation.

**Install and test.** Extract the release ZIP into the Distant Worlds 2 installation directory (the ZIP contains `mods/DeepSpaceStability/`). Enable Deep Space Stability and Refuel and Positronic Matrix in DW2 Mod Launcher. The coordinate stability fix remains available with the refuelling options off.

**Verification.** On Steam Beta Vanilla 1.3.8.7, 76 coordinate checks and 58 fleet/refuelling checks passed in the native headless harness. Coverage includes the fleet menu command, Shift queueing, native save/load, empty tankers and Stop. Live in-game UI and fleet travel are pending player testing.


## Public prerelease snapshot â€” 2026-11-15

- Publishes the exact runtime and manifest identified by this folder's `SHA256SUMS`.
- Adds a source snapshot where available, without bundling game, launcher, dependency, save, profile, log, or third-party content.
- See the matching GitHub prerelease for candidate-specific verification and limits.
