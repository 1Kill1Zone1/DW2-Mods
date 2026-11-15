# Testing HyperDeny on Move 0.1.0

Target: Steam beta1.3.8.7 package; see the bounded verification evidence below.

Original native checks target1.3.8.4; the supplied unchanged runtime is also installed in Steam beta. Live beta combat remains manual.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Use a player ship with a working HyperDeny system and issue a manual Move order. Bring an enemy at war into the same location and within the field radius.
2. Try a target whose drive isolation is strictly lower than field strength: it should be denied starting a jump while the player ship continues its Move order.
3. Repeat with an out-of-range target, a non-hostile target and isolation equal to or greater than field strength. Those cases must not be denied by this extra rule.
4. Repeat using a manual fleet Move. Confirm native energy/range/hostility conditions remain in force rather than treating a Move order as a guaranteed denial.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
