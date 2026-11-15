# Testing Freight Dispatch 0.2.4

Target: Steam beta1.3.8.7 package; see the bounded verification evidence below.

737 recorded full native/UI checks plus scoped integration checks; live deliveries/FPS remain manual. Sources are the frozen0.2.4 snapshot tied to the supplied checked runtime.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Select an owned source base with free cargo, open FD and choose an owned destination colony/spaceport. Check the resource list explains missing source, empty cargo or invalid destination instead of silently remaining blank.
2. Create a bounded export task with explicit reserves and a small freighter limit. Compare assigned ships / total civilian freighters, percentage and requested limit; these are different counts.
3. Observe an eligible freighter physically pick up and deliver cargo. Manual reserves take precedence for that task; new reverse replenishment of selected resources is suppressed while already committed inbound voyages remain valid.
4. Open the freighter overview and compare actual hold, current destination, assigned queue and FD association against the selected ship. Unknown native AI reasons must remain labelled unknown.
5. Test cancel/wait/no-route/full-destination behavior and save/reload in a disposable slot. Cargo already aboard must not vanish; cancellation must not teleport resources or abandon an underway delivery.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
