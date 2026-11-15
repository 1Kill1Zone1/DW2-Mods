# Testing Weapon FX Tuner 0.1.21

Target: Steam beta1.3.8.7 package; see the bounded verification evidence below.

799 UI/native assertions plus the documented RVE integration checks; live pixels and FPS remain manual.

Install with game/launcher closed, obtain the dependencies listed in dw2modlauncher.json separately, and use a disposable campaign/save slot.

1. Open Weapon FX Tuner through its map HUD button or Ctrl+Alt+V. Search for a mounted weapon; verify unobserved foreign equipment is not exposed.
2. Change Body, Trail and Impact colors/opacity separately. Use Test shield and Test hull: the preview should show the chosen layers without changing ship health or creating a damaging shot.
3. With the original Refreshed Visual Effects dependency enabled, observe an actual shield hit: retain the contact flash and show the fitted expanding shield wave. Hull impacts and Original style should retain their normal behavior.
4. Test Save, Undo and Revert to last Save on a disposable visual profile. Open Research or another modal: the tuner must not capture its mouse/keyboard input. Escape closes it; plain V remains the native shortcut.

Report game/mod/dependency versions, steps, expected/actual result and screenshots. Do not post private campaign files publicly. These checks do not establish live GPU performance or compatibility with every mod stack.
