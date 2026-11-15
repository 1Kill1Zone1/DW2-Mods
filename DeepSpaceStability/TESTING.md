# Testing Deep Space Stability and Refuel 0.2.0

Target: Steam beta1.3.8.7 package; see the bounded verification evidence below.

127 recorded scoped assertions, including native save/load and installed integration callbacks; live travel remains manual.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Give a ship a manual Move order that needs a fuel stop. Verify the saved destination is retained while it refuels and that it resumes the move instead of idling permanently.
2. Observe tanker service with several eligible fuel requests. Nearby-first service should retain distinct reservations; one delivery must not be silently counted for multiple ships.
3. Check a stranded or unusable-fuel case on a disposable campaign: show the waiting/failure reason and retain a valid goal instead of repeatedly issuing an impossible route.
4. Save/reload during refueling and verify the goal/reservation survives. Optional rally behavior remains off unless deliberately enabled; do not infer a default fleet-order change.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
