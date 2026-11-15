# Changelog

## 0.2.4 — 2026-11-14 — prepared Steam beta candidate, not installed

- Optional Galaxy Waypoints0.10.0 environmental eligibility before the two-leg freighter route check, before orders, contracts or reservations. Evaluate the actual freighter at pickup and delivery; refusal supplies the dispatch waiting reason.
- Existing cargo, retained stock, priority, quota and cancellation logic are unchanged. Without the new optional WP provider the established FD behavior remains.
- 737 full native FD regressions on Steam1.3.8.7 passed; paired WP eligibility/contract-free refusal checked separately. Live travel/rendering remain owner acceptance after a separately authorized installation.

## 0.2.3 — 2026-11-13

- Fix a reproduced save-coordination defect: a blocked FD save kept the old main file, but Refreshed Shipset Colors1.1.0 still deleted or overwrote its color companion from a success postfix.
- Reject FD's own preflight failure with a private typed exception before any file opens. Its finalizer clears only that rejection, preserves unrelated exceptions and restores the thread scope. Success postfixes no longer run for that failed save; accepted save behaviour stays native. No graphics-mod DLL is modified.
- Actual paired old-file/new-file/write/delete/unrelated-error checks pass25 assertions after the pre-fix16-assertion reproduction. Successful native SaveGame body is controlled in this focused test; the full game/render session is not launched. Final serial gates on the frozen candidate DLL:25paired-save,737full,306actual War Chest and218actual Ship Logistics assertions passed; build0warnings/errors. Game installation remains a separate step.

## Ship Logistics1.0.40 coordination — installed, 2026-11-13

- Metadata-only exact-version coordination for SL1.0.40, preserving installed War Chest1.0.21 coordination. FD0.2.2, SL and original RVE DLLs remain unchanged; owner-approved one-JSON installation independently verified.
-218 actual native assertions pass in both registration orders: physical freight load/partial cancel/unload, persistence failure, task limits and reload, population-policy scope restoration on normal/exception paths, and a real SL refuel veto honoured without creating a task voyage/contract.
- Colony load-body/exception tests use a controlled barrier; physical freight tests use the native body. Refuel fixture explicitly moves the deep-space base into a system and seeds its derived fuel-point index. No real campaign/game writes, live colonist loading or GPU/FPS claim. Test managed heap capped at1.5GiB; checks serialized.

## War Chest1.0.21 coordination — installed, 2026-11-13

- Exact-version `hooksCoordinatedWith` declaration only for the verified pair FD0.2.2 / War Chest1.0.21. Runtime DLLs, contract rules and save schema are unchanged. Owner approved the one-JSON GOG update; installed/protected hashes verified.
- Actual installed budget/expense/ledger hooks pass306 focused assertions in both contract-prefix orders. Controlled post-refresh budget data and a test-only loader generation are used; native contracts, payments, cancellation and actual ledger hooks execute. Native/WarChest control without the FD contract prefix matches combined results for own/external/limited/zero-budget trades, including fees, refund amounts and cargo ownership. FD refusal and binding retain300 cases pass.
- Native foreign fees can exceed the base-price allowance and cancellation retains part of the payment; this also occurs without the FD contract hook. No refund/fee policy is altered by this compatibility declaration.
- Full FD737 native/UI assertions also pass on the same unchanged runtime DLL, with 0 build warnings/errors. RVE and the source fixture are unchanged. No game launch or live-rendering claim.

## 0.2.2 — local candidate, 2026-11-13; not installed

- Add guarded left-title dragging, local position persistence, default placement beside native selection HUD and resolution clamping. Reuse the existing Battle Log pointer/preference approach; campaign data is unchanged.
- Fix older installed BL/FX/Waypoints dock helpers treating the real FD panel as an unrelated modal. Three scoped postfixes recognize only the registered panel owned by FD; same-name foreign panels and native/unknown blockers remain blocked. Peer DLLs are unchanged.
- Explain missing base/spaceport source and empty own free cargo; hide empty resource inputs. Destination remains independent and defaults to the capital; no dispatch/reserve/persistence changes.
- Build0warnings/errors;737full native/UI checks,615targeted overlapping overview/UI checks and84legacy/layout checks passed sequentially. Actual installed peer dock/control code is exercised; peer body fixtures are controlled, and no live rendered UI/travel/FPS is claimed. Installed GOG remains0.2.1 until a separately authorized stopped-game update.


## 0.2.1 — 2026-11-13

- Fix successful Priority confirmations masking a later active task waiting status. Failed actions still report errors.
- Keep observing native unload/contract-removal delivery counters while the session fault latch stops new pickups; reconcile deltas exactly once.
- Retry and verify valid retained task/ledger state at the save barrier after cleanup. Roll back rejected Priority/Start commits so recovery cannot revive them; ordinary commits on a faulted session throw instead of silently claiming success.
- Guard actual DWGame.SaveGame before its file is opened/truncated. Skip the native save on an unrecovered Matrix/cleanup error, preserve the previous file and defer its native failure notice until Running. Avoid a second Matrix commit after opening the file; restore the prepared-save scope on completion/skip/exception without suppressing unrelated errors.
- Keep the existing campaign schema and the 0.2.0 Freighters overview. Raw unreadable payloads stay preserved; the runtime fault latch remains until reload.
- Final build 0 warnings/errors; 500 full native/UI checks and 84 actual installed-peer dock checks on the same DLL. Includes the original reproduced delivery case (3 physical units, Delivered 3 after native reload), duplicate callbacks, persistent outage, preservation of an existing test save through hooked native SaveGame, scope cleanup, deferred notice and rejected-command recovery. Live rendering/travel/FPS and a full native game save session remain unverified.


## 0.2.0 — local candidate, 2026-11-13; not installed

- Add a Freighters tab independent of base selection: all own civil freight hulls, name/resource/activity/assignment filters, ship pages and complete paged details.
- Keep actual cargo and separate smuggling cargo distinct from mission plans and fuel tanks. Show pickup/delivery targets, multiple contract/resource counters, current remaining commands, queued missions in native FIFO order and separately labelled revert missions.
- Rebuild queued command display from the first command to match actual native queued reassignment. Show only assigned plans, never invented future decisions or ordinary-AI reasoning.
- Confirm FD association using own ship, order, contract, resource and both endpoints. Label missing/reused native identities, metadata faults and unstable samples explicitly.
- Sample only the open/unobstructed tab, at most once every two seconds; retain snapshot age, bound retries to one and release name-search focus behind blockers/on tab or campaign changes. Reuse existing map/input guards; no new economic hooks or save schema.
- Final build 0 warnings/errors; 475 full native/UI assertions and 84 installed-peer dock assertions passed on the same candidate DLL. Copied fixture hash and installed 0.1.0 DLL remain unchanged. No live rendering/travel/FPS claim.

The two independently reproduced 0.1.0 P2 issues below remain open; dispatch scheduling, accounting and persistence code are unchanged by 0.2.0.


## 0.1.0 — installed 2026-11-13, unpublished

- Add Normal/High source preference and explicit owned-base dispatch tasks with chosen resources, manual retain amounts, destination, and concurrent freighter limit.
- Use native reservations, transport missions and physical loading/unloading. Preserve committed inbound deliveries, stop new reverse replenishment, and finish loaded deliveries after cancellation.
- Persist campaign/player task, order and contract identities through Positronic Matrix; reject missing or corrupt storage.
- Roll back unloaded allocations after failed persistence, preserve loaded cargo, and block further task pickups/top-ups until recovery.
- Show actual distinct allocated ships, current completed own civil freighter total, percentage, and requested limit separately. Include repair/refit hulls in the total without describing them as available.
- Add a native map panel with input/occlusion guards and refresh expensive counters at most once per second.

The final build passed with zero warnings/errors; the same binary passed 324 full-suite checks and 224 targeted native UI assertions. The separate installed-peer dock review passed 84 assertions. The copied source campaign hash remained unchanged.

The route guard conservatively requires the current tank to cover pickup plus delivery, including native path-time cost, without assuming pickup refuelling. Original fixture capital acceptance was about 75 Silicon; the two-ship case consumed only held destination stock in memory and respected unchanged native capacity. Older peer tools require closing Freight Dispatch before switching. Offline checks do not establish live travel, rendered appearance, or FPS. Installed and enabled in GOG XL Local 34 (36 mods) on 2026-11-13; not published. Installation receipt: C:/Projects/DW2 Modding/artifacts/freight-planets-install-20261113-000159/installed.json. Live travel/rendering/FPS remain unverified.

### Known issues from independent Astra review — 2026-11-13

The installed0.1.0 exact DLL rerun passed324 baseline and84 dock assertions. Separate scratch repros confirmed two P2 issues: changing Priority masks a later active-task waiting status, and a loaded delivery that finishes after a Matrix fault can lose its Delivered bookkeeping across save/load. The repro delivered and preserved3 actual cargo units but restored Delivered=0. No physical cargo loss was observed. Both were pending at review time and are fixed in 0.2.1; the original read-only review did not change source/installed payload. Evidence: work/FreightDispatchReview20261113/targeted-repro.log. Live rendering/autonomous travel and the full simultaneous mod-hook combination remain unverified.