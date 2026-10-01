# Changelog

Versioning: `major.minor.patch`. Patch bumps are small fixes, minor bumps
add functionality, major bumps are major releases.

## v1.2.5 — 2026-10-01

### Changed

- **"Lock mouse cursor to window bounds" (Display tab) now saves instantly** —
  toggling it persists and re-applies right away, matching the Active
  toggle's behavior, instead of requiring a separate Save Changes click.
  Also fixes the toggle silently doing nothing if clicked before a game had
  ever been explicitly selected.

## v1.2.4 — 2026-09-10

### Fixed

- Fixed an enforcement "thrash loop" that could wedge the GPU driver hard
  enough to require a reboot. A game whose in-engine settings reset
  (resolution, window mode, etc.) while its NoBorders profile stayed active
  would fight the 1-second enforcement timer every tick — engine restores
  its own style/size, NoBorders strips it right back, repeat indefinitely —
  flickering the window and, on affected systems, crashing the display
  driver. NoBorders now detects 4 consecutive contested ticks on the same
  window and backs off for 15 seconds instead of continuing to escalate.

## v1.2.3 — 2026-09-09

### New

- **Settings > Diagnostics "Full Reset"** — wipes every persisted setting
  (tracked games, custom resolutions/monitor defaults, hotkeys, ignore list,
  SteamGridDB API key, window bounds, toggles) and the artwork cache, then
  restarts NoBorders as if freshly installed. Confirms first; can't be
  undone.

### Fixed

- Fixed a game staying stuck borderless after being removed from the games
  list while its window was still open. The per-second enforcement loop
  only re-validated newly-launched windows against the games list, not
  windows it was already tracking — an orphaned tracked window kept getting
  re-stripped of its title bar forever, independent of the games list.
- Fixed NoBorders leaving every previous version's extracted wwwroot folder
  behind under `%LOCALAPPDATA%\NoBorders\wwwroot` forever, one per update,
  since nothing ever cleaned up an old version once it stopped being the
  running exe's version. The per-version folder wasn't actually needed —
  rebuild/stale-content detection already runs off the exe's own build
  stamp — so extraction now reuses one fixed folder instead.

## v1.2.1 — 2026-08-31

### Fixed

- Fixed NoBorders showing a permanently blank grey window every time it
  auto-launched minimized at Windows startup (i.e. every reboot). Root cause
  was distinct from the sleep/wake blank-grey-window bug (v1.1.0/v1.1.1):
  minimizing the window while WebView2's controller was still being created
  aborted that creation outright (confirmed via noborders.log: "Operation
  aborted (0x80004004 (E_ABORT))" from
  CoreWebView2Environment.CreateCoreWebView2ControllerAsync), leaving
  CoreWebView2 permanently null for the rest of the process's life — no
  repaint could ever fix it, only restarting the app. The minimized/hidden
  startup sequence now waits for WebView2 to finish initializing before
  minimizing the window.

## v1.2.0 — 2026-08-30

### New

- **Faster keyboard/numpad entry in Width/Height/Offset fields.** Tabbing
  into these fields now skips past the ▲/▼ stepper entirely and lands
  directly on the number with it already selected, ready to type over.
- **Hold-to-ramp on the ▲/▼ steppers.** Holding the mouse down on a stepper
  arrow now auto-repeats and accelerates the longer it's held, so mouse-only
  users can cover large (100s of pixels) adjustments without needing the
  Shift/Ctrl modifiers or clicking dozens of times.
- Default global hotkeys changed from Ctrl+Shift+A / Ctrl+Shift+R to
  Ctrl+F10 / Ctrl+F11 — the old combo could trigger accidentally inside
  games or other apps. Only affects fresh installs; existing saved bindings
  are untouched.

## v1.1.1 — 2026-08-29

### Fixed

- The sleep/wake blank-grey-window fix (v1.1.0) only covered NoBorders being
  minimized to tray at sleep time. Waking the PC while the window was left
  open on the desktop hit the same WebView2 swap-chain loss and still
  rendered blank grey, since the repaint only ran on the tray-restore path.
  It now also runs directly on wake whenever the window was already visible.

## v1.1.0 — 2026-08-26

### New

- **Status bar toast.** Save/action confirmations now show inline in
  MainShell's footer while the window is visible, colored by severity
  (Ok/Warn/Info/Error) instead of a plain success/failure flag. The old
  popup toast is now reserved for when the window is hidden/minimized to
  tray.
- Monitors settings now remember a disconnected monitor's last-known real
  resolution instead of showing it as unknown.
- Faster first interaction after launch: the app now precompiles at
  publish time (ReadyToRun), removing the 300–500ms JIT delay previously
  noticed the first time you opened the Add Running App modal after a
  fresh launch.
- NoBorders no longer offers itself as a pickable "running app" in its
  own Add Running App picker / Matching tab live-test list.
- The SteamGridDB API key hint in Settings → Artwork is now a clickable
  link instead of plain text.

### Fixed

- Waking the PC from sleep while NoBorders was minimized to tray could
  leave the window rendering as blank grey instead of recovering.
- The custom title bar could show a second, native set of Windows 11
  Snap Layout buttons and an occasional white/grey caption strip flicker
  behind the app's own header — Windows was still tracking real (invisible)
  native caption geometry underneath. The native caption is now fully
  removed rather than just painted over, while keeping the window
  resizable and Aero-Snappable.

## v1.0.1 — 2026-08-13

### New

- **Re-Apply** (main window) and **Rescan** (Matching tab) now flash briefly
  to confirm the click actually registered. Re-Apply is also fully wired up
  now — it previously did nothing when clicked.
- That same click-confirmation flash now appears on every button that saves
  or confirms a setting: **Save Changes** (Display and Matching tabs),
  **Load Monitor Defaults**, **Save Monitor Default**, and **Detect
  Displays**.
- The pinned taskbar icon now reliably reopens NoBorders if it's already
  running and minimized to the tray.
- Settings → Diagnostics: **Verbose logging** is now a real toggle. Turn it
  on to log window style/position changes, artwork lookups, registry
  changes, and other background activity to the log file — useful when
  troubleshooting. The error counter on that page now reflects real errors
  from the current session instead of a placeholder number.

### Fixed

- The Activity Log window could silently stop responding to clicks (Pause,
  filters, row selection) after certain repeated warnings appeared in the
  log.
- The app's own version number, shown in the title bar and on the About
  page, was stuck on a placeholder ("v2.4") instead of the real version.
- The portable `.exe` could keep showing an older version of the interface
  after an update, due to stale caching — a fresh copy now always reflects
  the latest build.

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
