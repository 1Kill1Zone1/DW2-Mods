# Testing Freight Visibility 0.1.1

Target: Steam beta1.3.8.7 package; see the bounded verification evidence below.

23 recorded scoped checks; the read-only package is installed in Steam beta, but live tooltip rendering remains manual.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Open Resources and hover the middle Production/Runway value, often labelled steady. The freight block should append to the normal tooltip instead of replacing it.
2. Compare actual cargo with an own freighter hold. Pickup plans must be separate from loaded cargo; a partially loaded ship can appear in both without adding planned units to actual stock.
3. Check mixed loads, loaded refueling/retrofitting/idle ships and a disabled freighter carrying cargo. Reactor tank fuel is excluded; fuel transported as cargo is included.
4. Inspect the foreign-contract count without revealing foreign holds. Repeated hovering should reuse the short cache and must not issue hauling orders or edit stock.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
