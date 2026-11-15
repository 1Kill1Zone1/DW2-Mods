# Testing Cancellation Refunds0.1.1

Target: Steam beta1.3.8.7 with Positronic Matrix. The historical0.1.0 asset is for GOG1.3.8.4. This beta DLL passed79 full native,26 targeted and two missing-Matrix checks, plus actual hook registration; actual Troop Logistics1.0.40 / War Chest1.0.21 callbacks were exercised.

1. Use a separate campaign slot. Start a NEW ordinary paid building or troop order after enabling the mod and record the actual state payment. Existing pre-mod orders have no refund record.
2. Cancel at zero progress for the full recorded payment, or at25% for75%. Confirm state treasury rises once; private money is untouched. Use the normal cancellation action.
3. Retry cancellation and save/reload: a removed/refunded order must never pay again. Invalid/stale cancellation pays zero.
4. Free/old/unknown orders, upgrades, completed or damaged-completed assets, captures, combat destruction, ships and resources must not qualify. Capture followed by recapture must not restore eligibility.
5. With the optional tested peers, inspect automatic Garrison/Staging recruits and completion history. Compare treasury values directly: War Chest need not display negative expenses as a refund row.

Live cancellation UI, natural gameplay and FPS remain manual acceptance. Report versions, steps, expected/actual balance and screenshots; keep private campaign files out of public issues. The release cannot run on unverified engine versions/hashes.
