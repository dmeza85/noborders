# Changelog

## v1.0.0 — 2026-08-13 (first release)

### New

- **Portable — no installer needed.** NoBorders now ships as a single, fully
  self-contained `.exe`. Copy it anywhere and run it; nothing else needs to
  be installed or sit next to it.
- **Window Title matching.** Games can now be matched by their window title
  instead of just their process name, for the rare case where a game's exe
  name isn't a reliable way to find it.
- **Redesigned tray icon and right-click menu**, matching the app's own
  look — shows your currently-running tracked games with a live status dot,
  plus Pause/Resume Enforcement and Exit.
- **"Always start as Administrator"** toggle (Settings → Permissions). Turn
  it on once and NoBorders relaunches elevated automatically on every future
  startup — no more clicking through the admin prompt every time you want
  anti-cheat-protected games to go borderless.
- The "Administrator rights needed" prompt now appears as part of the app
  itself instead of a plain Windows popup, with a real "Restart as
  Administrator" button on it.
- A large app icon on the About page, and the new icon set applied
  throughout (tray, taskbar, title bar).

### Fixed

- Right-clicking the tray icon no longer requires two clicks to open the
  menu.
- Double-clicking the tray icon while the app was hidden could crash it
  outright.
- Closing the app to the tray and reopening it could show a blank grey
  window in the wrong position.
- "Load Monitor Defaults" (and a few other buttons on the Games tab) could
  silently do nothing depending on which tab you'd last visited in
  Settings.
- A game's display name could get silently corrupted by invisible
  characters picked up from its window title.
- Exe names with more than one build-tag suffix (e.g. `-Win64-Shipping`)
  weren't fully cleaned up for the game's display name.
