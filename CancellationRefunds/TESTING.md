# Testing Cancellation Refunds 0.2.0

Verified against Steam beta 1.3.8.7. The isolated suite passed 29 ship and orbital base checks, 79 facility and troop checks, 26 targeted checks, and two missing-Matrix checks. The official Mod Launcher 1.0.44 also registered the mod alongside the other active code modules. The constructor test starts at the work site. Live UI and gameplay remain manual checks.

1. Start a new state-funded ship and record its actual treasury debit. Cancel it in the yard's construction queue before work starts. The complete debit should return once.
2. Start a new state-funded orbital base. Use **Scuttle** partway through construction. The credit should match the remaining fraction of the debit. Repeat from a separate save slot after saving and reloading.
3. Cancel multiple unfinished state-funded hulls and compare the combined refund with the individual costs. Retry a cancelled order to check that it pays once.
4. Try a private order, a completed hull, or a project started before installing the update. These are ineligible. Compare the treasury directly, since a War Chest summary may omit a negative expense.

The native test project requires the game's assemblies and an isolated read-only campaign fixture. It reads Positronic Matrix and the optional compatibility test dependencies from the game root's mods folder. Set DW2_TEST_OUTPUT_DIR to a writable scratch directory for generated test saves.
