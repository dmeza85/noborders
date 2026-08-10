# Handoff: NoBorders UI redesign (WinForms → modern dark UI)

## Overview
A full visual redesign of **NoBorders**, a Windows borderless-window manager for games. The existing app (`reference/program.cs`, WinForms, .NET) tracks games by regex on window title, strips `WS_CAPTION | WS_THICKFRAME`, repositions the window per monitor profile, optionally clips the cursor, and exposes global hotkeys, tray behaviour, monitor defaults, elevation handling and a log file.

The redesign keeps all of that functionality but replaces the tabbed WinForms shell with a **frameless dark window**: a hero header for the selected game, a library rail, a Display / Matching detail pane with stepper inputs and a live result preview, plus separate Settings and Activity Log windows.

## About the Design Files
The files in this bundle are **design references authored in HTML** — prototypes showing intended look, structure and behaviour. They are **not production code to copy**. The task is to **recreate these designs inside the existing NoBorders application** (WinForms/.NET, per `reference/program.cs`) using its established patterns — or, if the team migrates the shell (WPF/WinUI 3 is the natural target for this level of custom chrome), to implement the same designs there.

Practical note for WinForms: nearly everything here (frameless chrome, rounded corners, pill badges, custom toggles, hover tints) requires owner-drawn controls or a move to WPF/WinUI. Treat the mock as the spec for the *result*, not the widget tree.

`NoBorders UI.dc.html` needs `support.js` (included) next to it to render; open it in a browser. It is a design canvas — several screens laid out side by side, each labelled with an id badge (`2a`, `5a`, `4a`, …). Those ids are used throughout this README and in the screenshot filenames.

## Fidelity
**High fidelity.** Final colors, typography, spacing, and states. Recreate pixel-accurately, subject to platform constraints. All numeric values below are exact and taken from the design file.

Data shown (game names, resolutions, monitor models, log lines, paths) is **sample content**, not fixed copy.

---

## Design Tokens

### Color
| Token | Hex | Use |
|---|---|---|
| Base / app background | `#0a0a0c` | Canvas behind windows, deepest surfaces |
| Chrome / sunken | `#0b0b0e` | Title bar, status bar, side rails, log list, preview well |
| Surface | `#0f0f13` | Main content panes, cards |
| Field | `#131318` / `#16161d` | Inputs, search boxes, list rows |
| Raised | `#1c1c23` / `#22222b` | Dividers, stepper buttons, neutral button fill |
| Border subtle | `#1c1c23` | Panel separators, 1px hairlines |
| Border | `#23232c` / `#26262f` | Input and card borders |
| Border strong | `#2c2c38` / `#2f2f3c` | Secondary button borders, focused fields |
| Text primary | `#f2f2f7` | Headings, values |
| Text bright | `#e8e8ee` | Body-strong, labels on chrome |
| Text body | `#c9c9d6` | Secondary buttons, list items |
| Text secondary | `#8a8a99` | Sublines, hints, log metadata |
| Text muted | `#55556a` | All-caps mono field labels, placeholders (decorative only) |
| Text faint | `#4e4e5c` | Table header strip only |
| Accent (violet) | `#8b6cff` | Primary buttons, selection, active tab/nav, preview fill |
| Accent tint | `rgba(139,108,255,.08–.16)` | Selected rows, active chips, preview fill |
| Accent text | `#b9a6ff` | Text/labels on accent tints |
| Success | `#3ddc84` | Borderless ON, OK log level, connected group, regex match |
| Success on-fill | `#08120c` | Text inside a `#3ddc84` pill |
| Warning | `#f0b429` | Elevation prompt, WARN log level |
| Warning text (rows) | `#e0be7a` / `#e8d3a4` | WARN row metadata / message |
| Danger | `#ff6b74` | Close button, ERROR level, destructive text |
| Danger text (rows) | `#ffb9bd` | ERROR row metadata / message |
| On-accent foreground | `#0a0a0c` | Text on violet or green fills (amber uses `#180f02`) |

Contrast rule that came out of review: **any value the user reads is `#8a8a99` or lighter.** `#55556a` / `#4e4e5c` are reserved for decorative all-caps labels and table header strips.

### Typography
- **UI / display:** `Barlow` (Google Fonts, weights 400/500/600/700)
- **Data / technical:** `JetBrains Mono` (400/500/700) — resolutions, offsets, timestamps, hotkeys, regex, PIDs, paths, all-caps section labels

| Role | Font | Size / weight | Notes |
|---|---|---|---|
| Hero game title | Barlow | 44px / 700, `line-height:1`, `letter-spacing:-.02em` | |
| Screen title | Barlow | 17–19px / 700 | Settings panes, dialogs |
| Section label (all-caps) | JetBrains Mono | 9.5–10px / 700, `letter-spacing:.12–.16em` | e.g. `TARGET MONITOR` |
| Body | Barlow | 12.5–13.5px / 400–600 | |
| Subline / hint | Barlow | 11.5–12px / 400, `line-height:1.4–1.5` | |
| Field value | JetBrains Mono | 13–14px / 500 | W/H/offset, regex |
| Data / log line | JetBrains Mono | 11–12px / 400–500, `line-height:1.4` | |
| Chrome wordmark | Barlow | 12px / 700, `letter-spacing:.16em` | `NOBORDERS` |
| Status bar | JetBrains Mono | 10.5px / 400 | |

### Spacing, radius, misc
- Window title bar height **40px** (log window **38px**); status bar **26–28px**.
- Radius: **4px** chrome buttons · **5–7px** small pills/steppers · **8px** inputs & buttons · **9–10px** cards/panels · **12px** window · **999px** filter chips and toggles.
- Control heights: **26px** chrome button · **30px** compact button/field · **34–36px** standard input/button · **42px** hero button.
- Gaps: 8px within a button group, 10px between fields, 16–26px between panes; pane padding 20–26px.
- Panel divider: 1px `#1c1c23`.
- Placeholder art (cover/hero) is a striped fill: `repeating-linear-gradient(135deg,#1b1b23 0 8px,#16161d 8px 16px)` (hero uses `115deg`, 10px/20px; small thumbs use `#22222c`/`#1a1a22` when selected, `#1c1c24`/`#16161d` otherwise). Replace with real art.

### Icon conventions (important)
- **↺** is reserved for **"fetch name from the running window"** (matches `BtnFetchName_Click` in program.cs).
- **Undo is a labelled button — `↶ Undo`** — never a bare ↺, so the two are never confused. It appears in both tabs.

---

## Screens / Views

### 2a — Main window, **Display** tab (primary deliverable)
**Purpose:** pick a game, aim it at a monitor, set its size/offset, toggle borderless. Card **1120 × ~760**.

1. **Title bar** — 40px, `#0b0b0e`, bottom border `#1c1c23`, padding `0 12px`, gap 12px.
   Left: 16px violet rounded square (app mark) · `NOBORDERS` wordmark · version chip `v2.4` (mono 10px, border `#23232c`, radius 4).
   Right: **Settings** button (26px tall, padding `0 11px`, radius 7, border `#26262f`, bg `#131318`, text `#c9c9d6` 11.5/600, 11px violet ring glyph) → 1px `#22222b` divider (18px tall) → window controls: three 34×26 cells, gap 2px, `—`, `▢`, `✕`; close cell `background:rgba(255,80,90,.12); color:#ff6b74; radius 4`.
2. **Hero** — 260px. Striped placeholder art under a **vertical** scrim `linear-gradient(180deg, rgba(10,10,12,0) 0%, rgba(10,10,12,.45) 52%, rgba(10,10,12,.94) 100%)` — art readable at top, copy on near-black at bottom. Caption `hero art 16:5` top-left (mono 9.5 `#8a8a99`).
   Content at `left:36px; right:36px; bottom:26px`, flex align-end, gap 22:
   - 7px `#3ddc84` dot + `BORDERLESS APPLIED` (mono 10.5/700, `.14em`).
   - Game title 44/700 white.
   - Meta line (mono 12 `#9a9aa8`): `2560×1440 · +0,+0 · LG 27GP850 · cursor locked`.
   - Buttons (42px, radius 9): **Disable Borderless** (violet fill, `#0a0a0c`, 13.5/700), **Re-Apply** (`rgba(255,255,255,.08)` fill, `rgba(255,255,255,.14)` border).
3. **Body** — `min-height:430px`, `#0f0f13`, two columns.
   - **Left rail, 300px**, right border `#1c1c23`: filter field (30px, `#16161d`, border `#23232c`, radius 7) + 30×30 violet `＋`; `LIBRARY · 9` label (mono 9.5/700 `#4e4e5c`); rows `padding:9px 16px`, gap 11 — 34px striped thumb (radius 6), name 13/600, resolution mono 10 `#7a7a8a`, optional `ON` pill (`#3ddc84`/`#08120c`, mono 9/700, radius 4). Selected row `rgba(139,108,255,.12)` + 2px left border `#8b6cff`; unselected names step `#c9c9d6` → `#8a8a99`.
     **No multi-app "running now" grouping** — explicitly removed. The UI shows one selected game's state; do not reintroduce active counts.
   - **Right pane** (padding `20px 26px 24px`): tabs **Display** / **Matching**, gap 26, on a 1px `#1c1c23` rule; active = `#f2f2f7` 13/600 with 2px violet underline, inactive `#7a7a8a` 13/500.
     - Left column, 300px: `TARGET MONITOR` label + 36px dropdown (`#16161d`, border `#23232c`, radius 8, `▾` `#55556a`); **WIDTH / HEIGHT** then **OFFSET X / OFFSET Y**, gap 10. Field = 36px, radius 8, `#16161d`, padding `0 4px 0 12px`, value mono 14/500 `#f2f2f7`, and a **stepper** on the right: two stacked 20×14 buttons (radius 4, `#22222b`, `▲`/`▼` 7px `#c9c9d6`), gap 2. W/H borders `#2f2f3c`, offsets `#23232c`.
       Hint (Barlow 12 `#8a8a99`): `Arrows step by 1 px · hold Shift for 10 · Ctrl for 100`.
       Checkbox: 18px violet square with `✓` + `Lock cursor to window bounds`.
     - Right column: `RESULT PREVIEW` label + 168px well (`#0b0b0e`, border `#23232c`, radius 10, padding 16) with a 290×132 dashed `#33333f` monitor rect; window rect `rgba(139,108,255,.14)` + 1.5px `#8b6cff`, centered mono 10.5 `#b9a6ff` caption.
       Below: **Save Changes** (violet) · **↶ Undo** (outline, 34px tall, padding `0 13px`) · **Load Monitor Defaults** (outline). Gap 8.
4. **Status bar** — 26px `#0b0b0e`, mono 10.5 `#55556a`: `Ctrl+Shift+A add` · `Ctrl+Shift+R refresh` · right `standard user`.

### 5a — Main window, **Matching** tab
Same shell as 2a; only the right pane differs. This is the redesigned version of the old Advanced section (display name, match regex, fetch-name button).
- **Left column, 300px:**
  - `DISPLAY NAME` — 36px field + a 36×36 outline **↺** button, tooltip *"Fetch name from the running window"*.
  - `MATCH PATTERN` — 36px field, value in **mono** (`^HELLDIVERS.?2`), border `#2f2f3c`. Under it a validity line: 6px `#3ddc84` dot + `Valid regex · case-insensitive · 1 window matched` (Barlow 11.5 `#8a8a99`). Invalid pattern → `#ff6b74` dot and the regex parse error in that line, with the field border going `#3a2028`.
  - Mode chips: **Window Title** (active, violet tint) / **Process Name** — which string the pattern is tested against.
  - `EXECUTABLE` — read-only mono path block (11px, `line-height:1.5`, `word-break:break-all`), caption *"Used for the icon and to re-find the game after a restart."*
- **Right column — live test:** header row `LIVE TEST · OPEN WINDOWS` + hairline + **Rescan**. A bordered list (`#0b0b0e`, radius 10) of currently open windows: 26px thumb, window title 12.5/600, `exe · PID` mono 10 `#8a8a99`, and a verdict pill — **MATCH** (`#3ddc84` fill, `#08120c`, plus row tint `rgba(61,220,132,.07)` and 2px `#3ddc84` left border) or **NO** (outline `#2c2c38`, `#8a8a99`). Rows separated by 1px `#16161d`.
  Summary bar (`#0b0b0e`, border `#1c1c23`, radius 8): `matches exactly 1 of 14 open windows` + **Use Title Of Selected** (violet mono link) which writes the selected window's title into the pattern as an escaped literal.
  Actions: **Save Changes** (violet) · **↶ Undo** · **Remove Game** (border `#3a2028`, text `#ff6b74`).
- The list must respect the existing `_systemProcessBlocklist`; the match test uses the same compiled regex path as `GameConfig.CompiledPattern` (IgnoreCase | Compiled) so the preview cannot disagree with runtime behaviour.

### 3a — Result preview states
Two 290×132 previews in one 720px card.
- **Full coverage:** window rect fills the monitor rect; caption `3440×1440 @ 0,0`.
- **Windowed + offset:** monitor rect striped (`repeating-linear-gradient(135deg,#111116 0 6px,#0d0d11 6px 12px)`); window rect at `left:37px; top:16px; width:216px; height:99px` = **2560×1080 @ 440,180 on a 3440×1440 monitor**, scaled `290/3440` × `132/1440`. Shaded bands (`rgba(139,108,255,.06)`, dashed `#3a3a4a`) mark the X band (left, full height) and Y band (above the window); micro-labels `X 440`, `Y 180` (mono 9 `#7a7a8a`), `3440×1440 monitor` bottom-right.
- Summary: `2560×1080 @ 440,180 — 56% of Samsung S34J55x · fits on screen` (green) + **Center On Monitor**.
**Implementation:** percentage is area coverage (`w*h / mw*mh`); "fits on screen" flips to a warning when `offset + size` exceeds monitor bounds.

### 4a — Activity Log window (separate top-level window)
Card **880 × ~570**. Opened from Settings → Diagnostics.
- **Title bar** 38px: violet mark · `ACTIVITY LOG` · `noborders.log` (mono 10.5 `#4e4e5c`) · window controls (32×24 cells).
- **Toolbar** (`12px 14px`, `#0f0f13`): level chips with counts — `All 248` (violet tint + `rgba(139,108,255,.35)` border, `#b9a6ff`), `Info 231`, `Warn 14` (`#f0b429` on `#3a3320`), `Error 3` (`#ff6b74` on `#3a2028`); radius 999, padding `6px 11px` · flexible **Filter lines…** field (`flex:1 1 auto; min-width:100px`) · **Auto-Scroll** toggle (24×14 violet pill) · **Pause** · **Open Folder**.
- **Body** 420px: list (`flex:1`) + 250px detail pane.
  - Header strip: grid `78px 62px 128px 1fr` — `TIME / LEVEL / SOURCE / MESSAGE`, mono 9.5/700 `#4e4e5c`.
  - Rows: same grid, `padding:7px 14px`, mono 11.5, 1px `#101015` separators; timestamps and source `#9a9aa8`, message `#c9c9d6`; level `#6a6a7a` INFO · `#3ddc84` OK · `#f0b429` WARN · `#ff6b74` ERROR.
  - WARN row `rgba(240,180,41,.06)` with `#e0be7a` / `#e8d3a4` text; ERROR row `rgba(255,107,116,.09)` + 2px `#ff6b74` left border, `#ffb9bd` text.
  - Tail indicator: green dot + `tailing — new lines appear here`.
  - Detail pane: `SELECTED ENTRY` label · level pill · message (Barlow 12.5) · mono key/value block (time with ms, game, `hwnd`, `pid`, line-height 1.9) · explanation card (`#0b0b0e`, border `#1c1c23`) · **Restart As Admin** (amber `#f0b429`, text `#180f02`) + **Copy**.
- **Status bar** 28px: `%LOCALAPPDATA%\NoBorders\noborders.log` · `248 lines · 61 KB` · right **Clear Log**.

### 4b — Settings → Diagnostics
520px card. Bordered row (`#131318`, border `#26262f`, radius 10): **Activity log** + description + **Open Log** (violet). Then a **Verbose logging** toggle (off: `#26262f` track, `#55556a` knob) with "Records every enforcement tick. Larger file." Footer rule: `3 errors in the last session` + **Export Log**.

### 1e — Settings: Monitors & defaults
700px window: 40px title bar (`SETTINGS` + evenly spaced 34×26 controls) and a 180px left nav (`#0b0b0e`): **Monitors / Hotkeys / Behaviour / Permissions / Diagnostics / About**; active item `rgba(139,108,255,.14)`, `#b9a6ff`, 600.
- Title `Monitor baseline defaults` + explanation.
- **`CONNECTED · 2`** group label (mono 9.5/700 `#3ddc84`) + hairline, then a 3-column grid of monitor cards; selected card 1.5px `#8b6cff` + `rgba(139,108,255,.08)`, others `#23232c`.
- **`SAVED · NOT CONNECTED · 3`** group (label `#55556a`, **Remove All** at the right of the rule): dashed `#2c2c38` cards, title `#8a8a99`, subline `#8a8a99` (`1920×1200 · 4 games`), `✕` remove per card. Caption: "Saved monitors keep their defaults so profiles come back if the display is reconnected."
- Four fields (Width / Height / Offset X / Offset Y) in one row + `Lock cursor to window bounds by default`.
- `RESULT PREVIEW` block (same component as 2a).
- **Save Monitor Default** (violet) + **Detect Displays** (outline).
Deliberately **no monitor-arrangement canvas** — the app never rearranges displays.

### 1f — Settings: Hotkeys & behaviour
Same shell, `Hotkeys` active.
- **Capture state:** binding card gets 1.5px `#8b6cff`, `rgba(139,108,255,.07)` fill, `0 0 0 4px rgba(139,108,255,.08)` glow; subline `Listening… press a key combination. Esc to cancel.`; caps show modifiers held (`Ctrl` `+` `Shift`) then an empty 34×30 **dashed violet** slot.
- **Resting state:** `Re-apply / refresh displays`, green dot + `registered` (mono 11 `#3ddc84`), caps `Alt` `+` `F11` — a **single-modifier** binding, which must be supported (MOD_NOREPEAT alone permits bare function keys) — plus **Rebind**.
- Key cap: padding `7px 11px`, radius 7, `#16161d`, border `#2c2c38`, mono 12/600 `#c9c9d6`; capture caps `#1c1c23`/`#33333f`/`#e8e8ee`; `+` separators `#55556a`.
- **Behaviour** list: `Minimize to system tray` (on), `Start with Windows` (on), `Start minimized to tray` (off, indented 24px) — 42×24 toggles, violet on, `#26262f`/`#55556a` off; rows separated 1px `#16161d`.

### 1d — Add Running App picker
560px modal. Title + "System processes are hidden…" + search. Rows (padding 10, radius 9, gap 12): 34px striped icon · name 13.5/600 · mono 10.5 subline `exe · PID · size/state`. Selected: `rgba(139,108,255,.12)` + `rgba(139,108,255,.32)` border + 18px violet check. Already-tracked rows show a `TRACKED` outline pill. Footer (`#0b0b0e`): `Tip: press Ctrl+Shift+A in-game to add the focused window.` (hotkey violet mono) · **Cancel** · **Add Game** (violet).
Applies the existing `_systemProcessBlocklist`.

### 1g — Elevation prompt · tray menu · toast
- **Elevation dialog** (470px, border `#33291a`): 26px amber warning square, `Administrator rights needed`, body about elevated games/anti-cheat, mono detail block on `#0b0b0e` (`SetWindowLong → ERROR_ACCESS_DENIED (5)`), then **Not Now** (outline) + **Restart As Admin** (amber, `#180f02`).
- **Tray menu** (230px, `#0d0d11`, 6px padding): header `NOBORDERS` (mono 10 `#55556a`) · rule · **Open NoBorders** (violet tint) · current game with green dot (`Helldivers 2 — borderless on`) · **Re-Apply** · rule · **Exit** (`#ff6b74`). No active counts.
- **Toast** (330px, `#111116`): 34px thumb · green dot + `Borderless applied` · detail line · violet `Undo · Ctrl+Shift+Z` · 2px progress bar (`#22222b` track, violet fill).

---

## Interactions & Behavior
- **Frameless chrome:** the title bar (except buttons) is the drag region; double-click toggles maximize; `—` minimizes (to tray when `MinimizeToTray` is on). Close respects the same setting.
- **Hover:** surfaces lift one step (`#131318` → `#16161d`, `#22222b` → `#2c2c38`); outline borders brighten to `#3a3a48`; close goes `rgba(255,80,90,.22)`. Transitions ~120ms ease-out.
- **Selection:** clicking a rail row loads that game into the hero + detail pane; hero content cross-fades (~150ms).
- **Tabs:** Display / Matching swap only the right pane; unsaved edits in either tab persist while switching and are both covered by Save Changes / ↶ Undo.
- **Steppers:** click ±1, **Shift** ±10, **Ctrl** ±100; hold-to-repeat after ~400ms at ~20/s. Clamp to existing `NumericTextBox` bounds (0–16384 size, −16384–16384 offsets); typing stays digit-filtered (leading `-` allowed for offsets).
- **Result preview:** recomputes live while typing or stepping; shows coverage % and flags out-of-bounds instead of the green "fits on screen".
- **Matching tab:** regex is validated on every keystroke (debounce ~200ms) and re-tested against the live window list; invalid patterns block Save and show the parse error. **Rescan** re-enumerates windows; **Use Title Of Selected** inserts the escaped title. ↺ pulls the title from the currently matched window (existing fetch-name behaviour).
- **↶ Undo:** reverts the last committed change to the current game's config (display or matching); disabled at 50% opacity when there is nothing to undo.
- **Borderless toggle:** the hero primary button flips `Disable Borderless` / `Enable Borderless`; the status dot and label follow (`BORDERLESS APPLIED` ↔ muted `INACTIVE`). Maps to `GameConfig.IsActive` + the enforcement timer.
- **Hotkey capture:** Rebind unregisters that global hotkey, enters the capture state, fills caps as modifiers go down, commits on the first non-modifier key, cancels on Esc or a second click. Single-modifier and bare function keys are valid.
- **Log window:** independent top-level window (not modal), tails the file live; Auto-Scroll pins to bottom and disengages if the user scrolls up; Pause freezes appends and offers "N new lines"; chips filter by level; the filter field matches source + message; clicking a row fills the detail pane.
- **Toast:** auto-dismisses ~6s along the progress bar; Undo reverts the applied bounds.
- **Resize:** authored at 1120px. Rail stays 300px, the left detail column stays 300px, the preview/test column absorbs extra width. Below ~980px, stack the right column under the fields.

## State Management
Maps onto the existing model in `program.cs`; no new persistence required.
- `AppSettings` — `Games`, `KnownMonitors`, `MonitorDefaults`, `MinimizeToTray`, `StartWithWindows`, `StartMinimized`, `ColorMode`, `HotkeyAddApp`, `HotkeyRefreshApp`.
- `GameConfig` — `GameName`, `RegexPattern`, `ExePath`, `IsActive`, `Profiles[monitorFriendlyName] → GameDisplayProfile{Width,Height,OffsetX,OffsetY,ConstrainMouse}`.
- View state: `selectedGame`, `activeMonitorScope`, `activeTab` (Display | Matching), `matchTarget` (Title | ProcessName), `railFilterText`, `isDirty`, `undoSnapshot`, `hotkeyCaptureTarget`, log state (`levelFilter`, `filterText`, `autoScroll`, `paused`, `selectedEntry`).
- Derived: connected vs. saved-but-disconnected monitors (live `QueryDisplayConfig` vs. `KnownMonitors`), games-per-saved-monitor counts, coverage % / fits-on-screen, regex validity + live match set.
- **Dark only.** The `ThemeMode` light path isn't covered here — either leave the existing light theme untouched behind the setting, or drop the setting.

## Assets
- **No production art included.** Cover thumbs and the hero use striped placeholders; substitute real art (per-game, or the extracted EXE icon the app already caches in `_iconCache`).
- **Icons** are geometric placeholders (rings, squares, chevrons, `▲▼`, `↺`, `↶`). Swap in your icon set — Lucide or Fluent both suit the weight — keeping the ↺ / ↶ distinction.
- **Fonts:** Barlow and JetBrains Mono (Google Fonts, OFL). Ship them with the app rather than loading remotely.
- Window control glyphs are text characters in the mock; use Segoe Fluent Icons (or equivalent) in the real app.

## Files
- `NoBorders UI.dc.html` — all screens (open in a browser; requires `support.js` alongside).
- `support.js` — runtime needed to render the HTML reference.
- `screenshots/` — 2x PNG renders of every screen:
  `2a-main-window-display-tab` · `5a-main-window-matching-tab` · `3a-result-preview-states` · `4a-activity-log-window` · `4b-settings-diagnostics` · `1d-add-running-app` · `1e-settings-monitors` · `1f-settings-hotkeys` · `1g-elevation-tray-toast`.
- `reference/program.cs` — the current WinForms implementation this redesign replaces, kept for the behavioural contract (Win32 calls, process blocklist, hotkeys, elevation flow, logging).
