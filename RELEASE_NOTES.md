# Draft release notes — v1.2.5

Not published anywhere yet — this is a draft body for creating the Gitea
release (tag `v1.2.5`) once you're happy with it. Delete this file after
copying it into the release, or keep it around and update it each release;
your call.

---

## NoBorders v1.2.5

A Windows borderless-window manager for games: strips the title bar/border
off a game window, positions it exactly where you want (including on a
specific monitor or offset onto part of an ultrawide), and keeps enforcing
that layout for as long as the game runs — re-applying automatically on
every future launch. Portable, self-contained `.exe`, no installer.

See [README.md](README.md) for the full feature list and screenshots.

### This release

- **"Lock mouse cursor to window bounds" now saves instantly** — toggling it
  on the Display tab persists and re-applies right away instead of needing
  a separate Save Changes click.

### Recent history

- **v1.2.4** — fixed an enforcement "thrash loop" that could wedge the GPU
  driver hard enough to require a reboot, triggered by a game resetting its
  own window style while NoBorders fought to correct it every tick.
- **v1.2.3** — added Settings → Diagnostics "Full Reset"; fixed a game
  staying stuck borderless after being removed from the list while its
  window was still open; stopped leftover per-version cache folders from
  piling up under `%LOCALAPPDATA%`.

Full history: [CHANGELOG.md](CHANGELOG.md).

### Known issues

See the [README's Known Bugs / Limitations section](README.md#known-bugs--limitations)
— nothing release-blocking, the most notable being the Diagnostics "Export
Log" button not being wired up yet.

### Download

`NoBorders.exe` (attach the build from `bin/Release/net10.0-windows/win-x64/publish/NoBorders.exe`).

### Requirements

Windows 10/11, 64-bit. No install needed — just run the exe. Administrator
rights are only needed for games protected by anti-cheat; NoBorders prompts
for that itself when needed.
