# NoBorders — Code Review

Full-project review covering security, correctness, incomplete/dead-end features,
consistency, and general best practices. Every finding below was verified against
the current source (not assumed from naming or habit) — file/line references point
at the exact spot, and each includes a concrete failure scenario, not just "this
looks off."

Scope: `program.cs`, `Services/*.cs`, `Components/**/*.razor(.css)`, `wwwroot/`,
`*.csproj`/`nuget.config`. Excludes the WinForms "shadow control" architecture
itself (control-construction methods, the AppStateService delegate-bridge pattern)
— that's a deliberate, already-reviewed design decision (see MIGRATION_PLAN.md
Phase 7), not something this pass re-litigates.

**Severity key:** 🔴 High — real, currently-reachable impact. 🟠 Medium — real but
needs a specific condition/local attacker/rare trigger. 🟡 Low — correct today but
fragile, or a maintainability/consistency cost rather than a bug.

---

## 1. Security

### 1.1 🟠 Command-line argument injection risk in the elevation relaunch
**`program.cs:1216-1225`** (`RestartAsAdmin`)

```csharp
string args = string.Join(" ",
    Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\""));
var psi = new ProcessStartInfo(exePath, args) { UseShellExecute = true, Verb = "runas", ... };
```

Each original launch argument is wrapped in literal `"..."` but embedded `"`
characters inside an argument are never escaped. `ProcessStartInfo.Arguments` is
re-parsed by the standard Win32 command-line tokenizer, so an argument containing
a `"` can break out of its intended boundary and inject additional tokens into the
elevated relaunch's command line.

**Failure scenario:** NoBorders is normally launched with fixed args (e.g.
`-minimized`), so this needs something else controlling how NoBorders itself was
originally started — a crafted shortcut, or another local process invoking
`NoBorders.exe` with an attacker-chosen argument containing an embedded quote.
Given that precondition, "Restart as Administrator" (Settings > Permissions, or
automatic on an elevation-blocked borderless attempt) reconstructs and replays
that argument into a **UAC-elevated** relaunch — turning a local low-privilege
argument-injection primitive into elevated command execution.

**Fix:** use `ProcessStartInfo.ArgumentList` (a `Collection<string>`) instead of
building a single `Arguments` string — it passes each argument through
`CreateProcess` correctly quoted with no manual escaping needed:
```csharp
foreach (var a in Environment.GetCommandLineArgs().Skip(1))
    psi.ArgumentList.Add(a);
```

### 1.2 🟠 No ReDoS protection on user-authored match patterns
**`program.cs:66-85`** (`GameConfig.CompiledPattern`)

```csharp
_compiledPattern = new Regex(RegexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

No `matchTimeout` is set. `CompiledPattern.IsMatch(...)` runs on every game against
every open window's exe name on **every `EnforceTimer_Tick` (1s) and
`ClipTimer_Tick` (100ms)** — both WinForms `Timer` callbacks on the UI thread.
`RegexPattern` is user-editable text (Matching tab), and nothing validates it's
free of catastrophic-backtracking shapes (e.g. `(a+)+$`-style patterns).

**Failure scenario:** a pathological pattern — typed by hand, pasted, or arriving
via a shared/imported `games_config.json` — hangs `IsMatch` on the UI thread the
next time a process happens to almost-but-not-quite match it. Since this runs
every second against the whole process list, the entire app freezes (borderless
enforcement, the Blazor UI, everything) with no crash and no log line explaining
why.

**Fix:** add an explicit timeout and catch the resulting exception, e.g.
`new Regex(pattern, options, TimeSpan.FromMilliseconds(500))`, with
`RegexMatchTimeoutException` falling back to the existing "never matches" regex
(the catch block already there for `ArgumentException` needs a second case).

### 1.3 🟡 SteamGridDB API key stored in plaintext
**`program.cs:128`**, persisted via `SaveConfig()` → `games_config.json`

`AppSettings.SteamGridDbApiKey` round-trips through `JsonSerializer.Serialize`
with no encryption. For a single-user desktop app this is a common tradeoff, not
a critical flaw, but it means any other local process (or a synced/backed-up copy
of the config file) can read the key in the clear.

**Suggestion:** if this is worth hardening, Windows DPAPI
(`System.Security.Cryptography.ProtectedData`, user-scope) can encrypt just this
one field at rest with a few lines of code and no key-management burden.

### 1.4 🟠 No global unhandled-exception handler
Verified via grep — no `Application.ThreadException`,
`AppDomain.CurrentDomain.UnhandledException`, or
`Application.SetUnhandledExceptionMode` anywhere in `program.cs`.

Nearly every operation in this codebase is carefully wrapped in
try/catch + `AppLogger.Log(ex, ...)` — a genuinely strong pattern — but that
discipline has one structural gap: if an exception ever does escape all of that
(a new code path, an edge case in a `catch`-less section), it currently has **no
backstop**. The two `async void` methods below are the most immediate example,
but this is really a one-line fix that protects the whole app at once.

**Fix:** in `Main()`, before `Application.Run`:
```csharp
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (s, e) => AppLogger.Log(e.Exception, "ThreadException");
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
    AppLogger.Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "UnhandledException");
```
This means a future crash actually leaves a line in `noborders.log` instead of
vanishing — the single highest-value fix in this review relative to its size.

---

## 2. Correctness & Robustness

### 2.1 🔴 Config, log, and artwork cache all live next to the executable
**`program.cs:203, 615-616`**
```csharp
AppDomain.CurrentDomain.BaseDirectory, "noborders.log"
AppDomain.CurrentDomain.BaseDirectory, "games_config.json"
```
(and `ArtworkService`'s `wwwroot/artwork-cache`, passed the same base path).

Every piece of mutable app data — settings, the log, cached artwork — is written
next to `NoBorders.exe` itself, not under `%LOCALAPPDATA%`/`%APPDATA%`. This has
worked throughout development because the app has only ever run from a
user-writable dev/output folder. It will **not** work for a standard installation:

**Failure scenario:** install NoBorders to `C:\Program Files\NoBorders\` (the
conventional location for a "real" installed Windows app) and run it as a normal
(non-admin) user — every `SaveConfig()` call throws `UnauthorizedAccessException`
writing to a directory standard users can't write to. Settings never save, the
log never writes, artwork never caches — and depending on which of these fails
first and how loudly, this can range from "silently doesn't persist" to a startup
crash, all invisible until someone actually tries a real install.

**Fix:** move persisted data to
`Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` +
`"NoBorders"`, creating the directory on first run. This also matches standard
Windows app conventions (install directory = read-only payload; `%LOCALAPPDATA%`
= mutable per-user data) — worth doing before this app is ever installed anywhere
outside a dev folder.

### 2.2 🟠 `async void` handlers with no fault containment
**`program.cs:4801` (`OnDisplayConfigChanged`), `program.cs:4959` (`OnWake`)**

Both are legitimate uses of `async void` (they're event-handler-shaped callbacks
with no caller to `await` them), but neither has an outer try/catch, and — per
§1.4 — there's currently no global handler to catch what escapes either. An
exception thrown from either (e.g. mid-way through the `foreach (var p in
Process.GetProcesses())` loop in `OnDisplayConfigChanged`, outside that loop's
own per-process try/catch) crashes the process with no log entry.

**Fix:** either wrap each method body in try/catch + `AppLogger.Log`, or rely on
§1.4's global handler once added — the global handler is the lower-effort fix
that also covers every other `async void`/event handler in the app, not just
these two.

### 2.3 🟡 `SanitizeFileName` is safe today but not defensively obvious
**`Services/ArtworkService.cs:309-314`**

```csharp
private static string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var chars = name.Select(c => invalid.Contains(c) || c == ' ' || c == '\'' ? '_' : c).ToArray();
    return new string(chars);
}
```

Verified this is currently safe: `Path.GetInvalidFileNameChars()` includes `\`
and `/` on Windows, so a crafted `cacheSlug` (e.g. from `GameConfig.ExePath`)
can't smuggle a subpath into the cache filename, and every call site appends an
extension afterward (`baseFileName + ext`), so the result can never be exactly
`".."`. That safety currently depends on two things staying true elsewhere
(extension always appended; this function always called before `Path.Combine`) —
neither is enforced at this function's own boundary. Not a bug today; worth a
one-line regression test (`SanitizeFileName("..") + ".png"` doesn't escape
`_cacheDir`) so a future edit can't quietly reintroduce a path-traversal primitive
here.

---

## 3. Incomplete / dead-end UI

These are already individually noted in scattered doc comments, compiled here in
one place since the ask was specifically to find "incomplete" code:

| Element | Location | State |
|---|---|---|
| "Detect Displays" button | Settings > Monitors | No `@onclick` at all — confirmed via grep, zero backing handler anywhere in `program.cs` |
| "Remove All" (saved monitors) | Settings > Monitors | Same — static, no handler |
| "Use Title Of Selected" | Matching tab | Static text link, never wired |
| "Window Title" match-mode chip | Matching tab | Present but inactive — app only ever matches by process/exe name; no `GameConfig` field exists yet to back a second mode |
| Double-click-to-maximize | Frameless title bar (all 3 screens) | Deliberately dropped this session — the mousedown-triggered native drag loop disrupts Chromium's own dblclick detection; documented as a known gap in `WindowControls.razor`'s own comment, not silently missing |

None of these are regressions — each was either always static (Phase 2 shell
never picked up) or a documented tradeoff — but they're genuinely incomplete
relative to what the UI visually implies is clickable.

---

## 4. Consistency & Maintainability

### 4.1 🔴 `AppStateService`'s constructor takes 59 positional parameters
**`Services/AppStateService.cs`** (constructor spans ~110 lines)

Every one of the 59 is a same-shaped `Func<T>`/`Action`/`Action<T>` — meaning the
compiler cannot catch two adjacent parameters being swapped; only a manual read
of the ~110-line call site in `program.cs`'s `CreateBlazorServices()` would catch
it, and even that call site is now long enough that a misordered pair is a
plausible, easy-to-miss mistake in a future edit.

**Failure scenario:** a future addition inserts a new `Func<int>`/`Action<int>`
pair in the wrong relative position between the constructor's parameter list and
the call site's argument list — both compile cleanly (types match), and the bug
only surfaces as a stepper silently adjusting the wrong field, discovered by
testing rather than the compiler.

**Suggestion:** this has grown organically, one feature at a time, and a full
redesign is out of scope for a review — but worth flagging now while it's "only"
59, before it grows further. A few real options, roughly in order of effort:
- Group related delegates into small records (e.g. `MonitorDefaultStepperOps(Func<int> GetWidth, Action<int> AdjustWidth, ...)`) passed as a handful of objects instead of dozens of loose parameters — cuts the list by ~4-8x and gives each group a name.
- Or invert the dependency: have `MainForm` implement a small interface (or several role-based ones) that `AppStateService` takes a single instance of, instead of unbundling it into individual delegates at all.

### 4.2 🟠 `program.cs` is a single 5,057-line class
`MainForm : Form` currently owns window chrome, tray icon, global hotkeys, the
shell-hook window tracker, monitor enumeration, the entire legacy WinForms
control tree, JSON persistence, and every `AppStateService` entry point in one
class. This is a known, common outcome of an incremental WinForms→Blazor
migration (confirmed intentional per MIGRATION_PLAN.md — Phase 7 correctly
decided the WinForms half can't be deleted since it's now the app's live data
layer) — not something to "fix" reflexively, but worth naming explicitly: any
future non-UI change (hotkeys, shell hook, monitor handling) touches the same
file as every UI-bridging change, with no compiler-enforced boundary between
them. If this project keeps growing, splitting the non-UI subsystems (tray,
hotkeys, shell hook, monitor enumeration, JSON persistence) into their own
partial-class files or standalone service classes — without touching the
control-construction/shadow-control code Phase 7 already ruled out touching —
would be the lower-risk way to shrink it.

### 4.3 🟠 Icon-only interactive elements: non-semantic markup, inconsistent labeling
Two concrete gaps, both currently reachable by a keyboard-only or screen-reader
user:

- **`Components/Screens/MonitorAlignButton.razor:26`** — the whole clickable
  surface is a `<div @onclick=...>` with only a `title` tooltip. A `<div>` is not
  in the keyboard tab order and has no accessible name exposed to assistive tech;
  `title` alone is unreliable (many screen readers don't announce it, and it's
  invisible until hovered).
- **`Components/Screens/MonitorCard.razor:32`** (the "✕" remove button) — same
  pattern, plain `<div>`, no `aria-label` at all (not even a `title`).

By contrast, `WindowControls.razor`'s three buttons and the rail's "+" button
(`MainShell.razor`) do this correctly — real `<button>` elements with
`aria-label`. The inconsistency suggests this is a per-component oversight, not a
deliberate choice, since the "right" pattern already exists elsewhere in the same
codebase.

**Fix, per site:**
```razor
<button type="button" class="nb-align-btn" aria-label="@Title" @onclick="...">
```
(swap `<div>`→`<button>`, add `aria-label`; CSS targeting `.nb-align-btn` doesn't
need to change, `<button>` accepts the same styling.)

### 4.4 🟡 `@foreach` without `@key` on reorderable lists
Confirmed via grep across 11 files; the two with the most user-visible impact:

- **`MainShell.razor`** — `@foreach (var g in State.Games)` (the rail).
  `RefreshGamesListOrder` actively re-sorts this list (running games float to the
  top), so the list's *order* changes independent of its *contents* while the app
  is running.
- **`MonitorsSettingsPage.razor`** — `@foreach (var m in State.Monitors)`, now
  primary-first sorted (this session's own change) — same shape of "order changes
  without content changing."

Without `@key`, Blazor's diff matches old/new render output by **position**, not
identity — after a reorder, it can retain a `MonitorCard`/rail-row component
instance in the wrong "slot" for a frame, or do more DOM patching than necessary.
Not a data-correctness bug (each render still binds the right `Name`/`Selected`
etc. from the right underlying object), but a real perf/flicker cost that a
one-line fix removes:
```razor
@foreach (var g in State.Games)
{
    <div @key="g" class="nb-shell__rail-row ...">
```
(`GameConfig`/`MonitorItem` are reference types, so using the object itself as
the key is enough — no need for a separate ID field.)

### 4.5 🟡 Two empty `catch` blocks with no explanatory comment
**`program.cs:212`** (inside `AppLogger.Log` itself — swallowing a log-write
failure makes sense, since logging the failure would be circular) and
**`program.cs:370`** (best-effort mutex release in a `finally`, rationale is in a
comment a few lines above but not on the line itself).

Every *other* empty catch in this file (10 of them) has an inline comment
explaining why swallowing is correct — these two are the only ones without,
which reads as an inconsistency rather than a deliberate omission. Not a
functional issue, purely a "match the codebase's own standard" nit:
```csharp
catch { /* logging a log-write failure would be circular; nothing to do */ }
```

### 4.6 🟡 Persistent build warning (WindowsBase version conflict)
Every build emits ~22 lines of `MSB3277` — a version conflict between
`WindowsBase, Version=4.0.0.0` (from the WinForms target) and
`Version=5.0.0.0` (pulled in transitively by
`Microsoft.Web.WebView2.Wpf.dll`, itself a transitive dependency of the
WebView2/WindowsForms package). The build auto-resolves it and nothing is
currently broken, but it's noise on every single build, and version-conflict
warnings are exactly the category that occasionally *does* matter (a future SDK
or package bump could change which version "wins"). Worth either an explicit
`<PackageReference>` pin or a documented `<Reference>`/binding redirect that
silences it deliberately rather than leaving it to auto-resolution.

---

## 5. Process / testing gaps

- **No automated tests anywhere in the repo** — no unit tests for the regex-
  pattern generation (`AddGame`'s cleanup regex, `ApplyAlign`'s offset math,
  `SanitizeFileName`), no integration test harness. Everything in this project's
  history has been verified via live manual testing (screenshots + Win32
  automation) each session — thorough in the moment, but it doesn't accumulate:
  every future change re-earns the same confidence from scratch. Even a small
  xunit project covering the pure-function pieces (`ApplyAlign`, `SanitizeFileName`,
  `HotkeyConfig.ToString()`, the regex cleanup in `AddGame`) would catch
  regressions for free going forward.
- **No CI configuration** (no `.github/workflows`, no build pipeline) — nothing
  currently verifies a build even compiles before it's committed.

---

## 6. What's already solid (for balance)

Not everything in a review should be a complaint — several things stood out as
genuinely good practice worth *not* changing:

- `nuget.config` clears inherited sources and pins to exactly `nuget.org` over
  HTTPS — correct defense against dependency-confusion attacks.
- No `MarkupString` usage anywhere in the Blazor layer — every render goes
  through Blazor's default auto-escaping, so there's no manual-HTML-injection
  surface at all.
- SteamGridDB response parsing is fully defensive (`TryGetProperty` throughout,
  never an indexer that can throw) — a malformed API response degrades to a
  clean error state, never a crash.
- IDisposable/event-subscription hygiene is clean: all 8 Razor components that
  subscribe to `AppStateService.Changed` correctly unsubscribe in `Dispose()` —
  verified 1:1, no leaks.
- The empty-catch convention (comment explaining *why* swallowing is safe) is a
  genuinely good habit — §4.5 flags the two spots it wasn't followed, but 10/12
  is a strong hit rate.
