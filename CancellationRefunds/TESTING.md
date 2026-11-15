# Testing Cancellation Refunds 0.1.0

Target: GOG DW2 1.3.8.4; Steam beta is unverified.

GOG1.3.8.4 only:79 native,26 targeted and two missing-dependency checks. Checked candidate, not installed; Steam beta is unverified.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Use a disposable GOG1.3.8.4 campaign with Positronic Matrix. Start an ordinary paid planetary building or troop order while the mod is active; record its actual state payment.
2. Cancel after partial progress: refund only the remaining paid work to the original payer, and only after the cancellation succeeds. A repeated cancellation must not pay again.
3. Old/unknown orders, free grants, upgrades, captures, completed/completed-then-damaged assets, combat losses, ships and resources must not produce this refund.
4. Save/reload with an eligible pending order, then cancel. Recorded payment attribution and completed-work boundaries must survive. Do not treat this release as Steam-beta verified.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
