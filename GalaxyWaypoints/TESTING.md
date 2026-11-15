# Testing Galaxy Waypoints 0.12.1

Target: Steam beta1.3.8.7 package; see the bounded verification evidence below.

115 route-card, 330 dock, 21 RVE patch-group and six entrance comparisons; same checked DLL reproduced by a serial source build.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Select a visible unexplored star and use its native left-panel survey/transit actions. Priority survey should show its blue marker; transit restrictions should retain their separate marker.
2. In a disposable campaign, restrict a system for automatic travel. Check ordinary automation respects the rule while an explicit manual command remains the documented override. Unsafe known environments must show a concrete suitability reason.
3. Open a route card and test the two-click pair selection. Compare the displayed system endpoints, node IDs and current/reference route labels. DEBUG visibility must not grant discoveries or open hidden nodes to ships.
4. Save/reload without regenerating the network: node IDs, priorities and transit rules should persist. Do not use generation as an implicit conversion of an existing campaign.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
