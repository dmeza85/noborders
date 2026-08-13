using System;
using System.Collections.Generic;

namespace NoBorders.Services
{
    /// <summary>
    /// DI-registered wrapper around MainForm's existing <see cref="AppSettings"/>
    /// instance for Razor components to read/eventually write — no new
    /// persistence format and no second copy of the data (MIGRATION_PLAN.md
    /// Phase 3.1).
    ///
    /// Settings is a live accessor (<c>Func&lt;AppSettings&gt;</c>), not a
    /// captured reference: <c>MainForm.LoadConfig()</c> reassigns its
    /// <c>_settings</c> field once, during the constructor, after this service
    /// is registered (though before it's ever resolved — DI singletons are
    /// constructed lazily on first use). Reading through a delegate rather than
    /// a snapshot means this always reflects whatever LoadConfig produced,
    /// regardless of exactly when either runs. <see cref="SelectedGame"/> uses
    /// the same pattern against MainForm's <c>_selectedGame</c> field (Phase 3.3).
    ///
    /// <see cref="QueueSave"/>/<see cref="SaveNow"/> forward to MainForm's
    /// existing QueueSave()/SaveConfig() — this service never writes the config
    /// file itself. Phase 3 (3.1–3.7) was read-only wiring; Phase 4 starts wiring
    /// actual event handlers, beginning with <see cref="SelectGame"/> (4.1).
    ///
    /// <see cref="Changed"/> is the bridge that makes writes from Phase 4 onward
    /// visible in the Razor UI at all: MainForm mutates its own fields (e.g.
    /// <c>_selectedGame</c>) by reusing existing WinForms handler bodies — those
    /// handlers have no idea a BlazorWebView exists and never call
    /// `StateHasChanged()`. MainForm raises `Changed` itself immediately after
    /// invoking one of those handlers (see the `_lstGames.SelectedIndexChanged`
    /// subscriber added in the constructor for 4.1); components subscribe in
    /// `OnInitialized`/unsubscribe in `Dispose` and re-render from it. This
    /// service never raises `Changed` on its own initiative — every write method
    /// below (`SelectGame`, and whatever Phase 4 adds after it) calls straight
    /// into a MainForm method that both performs the mutation and raises
    /// `Changed` itself, once, after — the DI/service layer doesn't duplicate
    /// that decision.
    ///
    /// review.md §4.1: this used to take 75 positional Func/Action constructor
    /// parameters, nearly all the same shape — a future edit inserting or
    /// reordering one was a silent, compiler-invisible risk. It now takes a
    /// single <see cref="IMainFormBridge"/> instead; every member below reads
    /// through it by name (<c>_bridge.Settings</c>, <c>_bridge.SelectGame(...)</c>,
    /// etc.) rather than through a positionally-bound delegate field. See
    /// IMainFormBridge's own doc comment (program.cs, just above MainForm) for
    /// the full rationale — this class's public API is unchanged by that
    /// refactor, only what's behind it.
    /// </summary>
    public sealed class AppStateService
    {
        private readonly IMainFormBridge _bridge;

        internal AppStateService(IMainFormBridge bridge)
        {
            _bridge = bridge;
        }

        /// <summary>
        /// Raised after any write method here has caused MainForm state to
        /// change, so subscribed components know to re-render. See the class
        /// doc comment — MainForm raises this itself, this service never does.
        /// </summary>
        public event Action? Changed;

        /// <summary>Called by MainForm, once, right after a handler it invoked has finished mutating state.</summary>
        public void RaiseChanged() => Changed?.Invoke();

        /// <summary>The live AppSettings instance — same object MainForm reads/writes.</summary>
        public AppSettings Settings => _bridge.Settings;

        public List<GameConfig> Games => Settings.Games;
        public HashSet<string> KnownMonitors => Settings.KnownMonitors;
        public Dictionary<string, GameDisplayProfile> MonitorDefaults => Settings.MonitorDefaults;
        public bool MinimizeToTray => Settings.MinimizeToTray;
        public bool StartWithWindows => Settings.StartWithWindows;
        public bool StartMinimized => Settings.StartMinimized;
        public HotkeyConfig HotkeyAddApp => Settings.HotkeyAddApp;
        public HotkeyConfig HotkeyRefreshApp => Settings.HotkeyRefreshApp;

        /// <summary>
        /// MainForm's <c>_selectedGame</c> — null until the user has clicked a
        /// rail row, in either the WinForms list or (Phase 4.1) the Razor rail.
        /// Components that need *something* to display before a selection exists
        /// fall back to <c>Games.FirstOrDefault()</c> themselves (see
        /// MainShell.razor) rather than this service silently picking one —
        /// selection state should read exactly as it is.
        /// </summary>
        public GameConfig? SelectedGame => _bridge.SelectedGame;

        /// <summary>
        /// Selects a game exactly as clicking its row in the WinForms games list
        /// would (Phase 4.1) — forwards to MainForm's own lookup-by-reference
        /// against `_lstGames.Items`/`SelectedIndex`, so the real
        /// `LstGames_SelectedIndexChanged` handler runs unchanged and every
        /// WinForms control it also updates (monitor combo, advanced fields,
        /// etc.) stays in sync even though those controls aren't the visible UI
        /// anymore — MainForm remains the single source of truth for selection.
        /// </summary>
        public void SelectGame(GameConfig game) => _bridge.SelectGame(game);

        /// <summary>
        /// Options for the target-monitor dropdown — same list `_cmbMonitor.Items`
        /// holds (MIGRATION_PLAN.md Phase 4.2): connected monitors plus any saved
        /// `KnownMonitors` not currently connected, per `SyncMonitorComboBoxes()`.
        /// Read directly off the WinForms control rather than recomputed here, so
        /// there's exactly one place that decides this list's contents/order.
        /// </summary>
        public List<string> MonitorOptions => _bridge.MonitorOptions;

        /// <summary>
        /// MainForm's `_activeScope` — which monitor's profile is currently being
        /// edited for the selected game. Empty until a game has been selected at
        /// least once (Phase 4.1's `SelectGame`/`LstGames_SelectedIndexChanged`
        /// populates it). Components that need a monitor to show before this is
        /// set fall back to their own display-only pick (see DisplayDetailPane's
        /// `TargetMonitorName`), same rationale as `SelectedGame`.
        /// </summary>
        public string ActiveMonitorScope => _bridge.ActiveMonitorScope;

        /// <summary>
        /// Selects a monitor scope exactly as picking it in the WinForms combo
        /// would (Phase 4.2) — forwards to MainForm's own `SelectMonitorScope`,
        /// so the real, unchanged `CmbMonitor_SelectedIndexChanged` handler runs
        /// (which also persists the previous scope's edited fields via
        /// `SaveUIToProfile` before switching).
        /// </summary>
        public void SelectMonitorScope(string scope) => _bridge.SelectMonitorScope(scope);

        /// <summary>
        /// Flips the selected game's `IsActive` exactly as clicking the WinForms
        /// borderless toggle would (Phase 4.3) — forwards to MainForm's
        /// `ToggleGameActive`, so the real, unchanged `ChkActive_CheckedChanged`
        /// handler runs (which also saves the config and immediately
        /// enforces/un-enforces the window). No-op if no game is selected.
        /// </summary>
        public void ToggleActive() => _bridge.ToggleGameActive();

        /// <summary>
        /// Hero's "Re-Apply" button — previously unwired (no @onclick at
        /// all). Re-applies borderless to the selected game's currently
        /// tracked window; no-op with a toast if it isn't currently running,
        /// same "no fallback game" convention as ToggleActive above.
        /// </summary>
        public void ReapplyBorderless() => _bridge.ReapplyBorderless();

        /// <summary>
        /// Loads the current monitor scope's saved default profile onto the
        /// selected game and immediately applies it, exactly as clicking "Load
        /// Monitor Defaults" would (Phase 4.4) — forwards to MainForm's own
        /// `PerformClick()` on that button, so the real, unchanged
        /// `BtnLoadDefaults_Click` runs. No-op (with a status message) if there's
        /// no saved default for the active scope.
        /// </summary>
        public void LoadMonitorDefaults() => _bridge.LoadMonitorDefaultsForSelectedGame();

        /// <summary>
        /// MainForm's `_txtGameName.Text` — the pending display-name edit buffer,
        /// not `GameConfig.GameName` directly (Phase 4.5). Kept in sync with the
        /// selected game's real name on selection (`LstGames_SelectedIndexChanged`),
        /// but can diverge from it once <see cref="FetchName"/> fills it with a
        /// freshly-fetched window title; only committed to `GameConfig.GameName`
        /// by Save Changes (Phase 4.6). Components should prefer this over
        /// `SelectedGame.GameName` once a game is actually selected, so a fetch
        /// is visible before it's saved.
        /// </summary>
        public string PendingDisplayName => _bridge.PendingDisplayName;

        /// <summary>
        /// Fetches the display name from the selected game's currently-running
        /// window, exactly as clicking the ↺ button would (Phase 4.5) — forwards
        /// to MainForm's `PerformClick()` on the real fetch button, so the real,
        /// unchanged `BtnFetchName_Click` runs. Only fills
        /// <see cref="PendingDisplayName"/>; does not persist anything (that's
        /// Save Changes' job). No-op (with a status message) if the game isn't
        /// currently running.
        /// </summary>
        public void FetchName() => _bridge.FetchNameForSelectedGame();

        /// <summary>
        /// MainForm's `_txtRegex.Text` — the pending match-pattern edit buffer,
        /// mirroring <see cref="PendingDisplayName"/>'s relationship to
        /// `GameConfig.GameName`. Kept in sync with the selected game's real
        /// pattern on selection, but can diverge once <see
        /// cref="SetPendingRegexPattern"/> writes into it; only committed to
        /// `GameConfig.RegexPattern` by Save Changes (Phase 4.6 — this buffer
        /// already existed on the WinForms side; Blazor just never read it).
        /// </summary>
        public string PendingRegexPattern => _bridge.PendingRegexPattern;

        /// <summary>
        /// review.md §3: backs "Use Title Of Selected" on the Matching tab,
        /// previously dead. Not routed through a hidden WinForms button, so
        /// RaiseChanged is MainForm's job here (same shape as AdjustWidth).
        /// Writes only the pending buffer, same as <see cref="FetchName"/> —
        /// Save Changes still owns actually persisting it.
        /// </summary>
        public void SetPendingRegexPattern(string pattern) => _bridge.SetPendingRegexPattern(pattern);

        /// <summary>
        /// review.md §3: which of the Matching tab's two chips is selected —
        /// Process Name (the default, and the only mode that existed before
        /// this) or Window Title. Same pending/Save-Changes-commits shape as
        /// <see cref="PendingRegexPattern"/>.
        /// </summary>
        public MatchTargetMode PendingMatchTarget => _bridge.PendingMatchTarget;

        /// <summary>Writes the pending buffer only, same as <see cref="SetPendingRegexPattern"/> — Save Changes still owns actually persisting it.</summary>
        public void SetPendingMatchTarget(MatchTargetMode mode) => _bridge.SetPendingMatchTarget(mode);

        /// <summary>
        /// Commits the pending edits — <see cref="PendingDisplayName"/>, the
        /// match pattern, and the active scope's numeric/lock-cursor fields —
        /// onto the selected game and persists them, exactly as clicking either
        /// "Save Changes" button would (Phase 4.6; the README notes both the
        /// Display and Matching tabs share one Save Changes/Undo). Forwards to
        /// MainForm's `PerformClick()` on `_btnSaveGame`, which the real
        /// `BtnSaveGame_Click` is wired to — the same handler the WinForms UI's
        /// *other* Save Changes button (`_btnSaveAdvanced`) already shared.
        /// </summary>
        public void SaveGameChanges() => _bridge.SaveGameChanges();

        /// <summary>
        /// Removes the selected game entirely, exactly as clicking "Remove Game"
        /// would (Phase 4.7) — forwards to MainForm's `PerformClick()` on
        /// `_btnDeleteGame`, so the real, unchanged `BtnDeleteGame_Click` runs
        /// (restores any enforced windows to bordered first, then deletes and
        /// persists). That handler has no confirmation prompt, and neither does
        /// this — see its doc comment in program.cs. No-op if no game is selected.
        /// </summary>
        public void RemoveSelectedGame() => _bridge.RemoveSelectedGame();

        /// <summary>
        /// Adds a new tracked game from a currently-running window — the Blazor
        /// Add Running App modal's (screen 1d) "Add Game" action (Phase 4.8).
        /// Unlike this class's other write methods, this does not simulate a
        /// click on any native WinForms control: the native `BtnAddRunning_Click`
        /// dialog was already replaced by this in-app modal per Phase 1/2's
        /// adaptation decisions, so this forwards straight to MainForm's
        /// `AddGame` — the real state-mutating method that dialog's OK-path (and
        /// the Browse-for-EXE path) both already call.
        ///
        /// Returns true if a game was genuinely added (false on a duplicate,
        /// empty input, etc.) — Phase 8.4's auto-fetch-artwork-on-add feature
        /// uses this to know whether <see cref="SelectedGame"/> is now the new
        /// game before trying to fetch its art.
        /// </summary>
        public bool AddGameFromRunningWindow(OpenWindowEntry entry) => _bridge.AddGameFromRunningWindow(entry);

        /// <summary>
        /// Opens the native "Select Game Executable" file picker exactly as
        /// clicking "＋ Browse for EXE…" would (Phase 4.9) — forwards to
        /// MainForm's `PerformClick()` on `_btnAddBrowse`, so the real,
        /// unchanged `BtnAddBrowse_Click` runs (a genuine OS common dialog,
        /// blocking until dismissed — not something the design mock replaces).
        ///
        /// Returns true if a game was genuinely added (false if the user
        /// cancelled the dialog, or the pick was a duplicate) — same reason as
        /// <see cref="AddGameFromRunningWindow"/>'s return value.
        /// </summary>
        public bool BrowseForExe() => _bridge.BrowseForExe();

        /// <summary>
        /// MainForm's `_cmbSetMonitor.SelectedItem` — which monitor's baseline
        /// default is currently being edited on Settings > Monitors (Phase 4.10).
        /// Empty until a card has been clicked at least once (from either UI, in
        /// principle — the WinForms combo isn't otherwise pre-populated). Same
        /// display-only-fallback rationale as <see cref="ActiveMonitorScope"/>
        /// for components that need something to show before then.
        /// </summary>
        public string ActiveMonitorDefaultScope => _bridge.ActiveMonitorDefaultScope;

        /// <summary>
        /// Selects a monitor as the Settings > Monitors baseline-default target
        /// exactly as picking it in the WinForms combo would (Phase 4.10) —
        /// forwards to MainForm's `SelectMonitorDefaultScope`, firing the real,
        /// unchanged `CmbSetMonitor_SelectedIndexChanged`.
        /// </summary>
        public void SelectMonitorDefaultScope(string scope) => _bridge.SelectMonitorDefaultScope(scope);

        /// <summary>
        /// Saves the active monitor default scope's current fields, exactly as
        /// clicking "Save Monitor Default" would (Phase 4.10) — forwards to
        /// MainForm's `PerformClick()` on `_btnSaveDefault`, so the real,
        /// unchanged `BtnSaveDefault_Click` runs.
        /// </summary>
        public void SaveMonitorDefault() => _bridge.SaveMonitorDefault();

        /// <summary>
        /// Deletes a monitor's saved default profile exactly as selecting it and
        /// clicking the WinForms "✕" would (Phase 4.10) — forwards to MainForm's
        /// `DeleteMonitorDefault`, which selects the scope then calls
        /// `PerformClick()` on `_btnDeleteMonitor`, so the real, unchanged
        /// `BtnDeleteMonitor_Click` runs — including its native confirmation
        /// `MessageBox`es (refuses connected monitors, confirms before deleting).
        /// </summary>
        public void DeleteMonitorDefault(string scope) => _bridge.DeleteMonitorDefault(scope);

        /// <summary>
        /// Re-enumerates connected monitors on demand, exactly as MainForm's
        /// own OnDisplayConfigChanged does automatically (review.md §3 —
        /// "Detect Displays" previously had no backing handler at all).
        /// </summary>
        public void DetectDisplays() => _bridge.DetectDisplays();

        /// <summary>
        /// Deletes every saved-but-not-connected monitor at once, exactly as
        /// clicking "Remove All" would (review.md §3 — previously had no
        /// backing handler at all). Native confirmation MessageBox and all,
        /// same as DeleteMonitorDefault.
        /// </summary>
        public void RemoveAllSavedMonitors() => _bridge.RemoveAllSavedMonitors();

        /// <summary>
        /// True while MainForm's `_capturingHotkeyId` state machine is actively
        /// listening for a new "Add focused app" combination (Phase 4.11). The
        /// capture logic itself — what counts as a valid key, when it commits —
        /// lives entirely in MainForm's frozen hotkey system; this only reflects
        /// its current phase for display.
        /// </summary>
        public bool IsCapturingHotkeyAdd => _bridge.IsCapturingHotkeyAdd;

        /// <summary>Same as <see cref="IsCapturingHotkeyAdd"/>, for "Re-apply / refresh displays".</summary>
        public bool IsCapturingHotkeyRefresh => _bridge.IsCapturingHotkeyRefresh;

        /// <summary>
        /// Starts or cancels capture for the "Add focused app" hotkey exactly as
        /// clicking its WinForms "Set Hotkey" button would (Phase 4.11) —
        /// forwards to MainForm's `ToggleHotkeyAddCapture`, so the real,
        /// unchanged toggle logic runs. Once capture begins, the actual key
        /// combination is captured by MainForm's frozen `CaptureHotkey`
        /// (a `KeyDown` handler on the real, focused — if visually
        /// Blazor-covered — WinForms textbox), not by anything in Razor.
        /// </summary>
        public void ToggleHotkeyAddCapture() => _bridge.ToggleHotkeyAddCapture();

        /// <summary>Same as <see cref="ToggleHotkeyAddCapture"/>, for "Re-apply / refresh displays".</summary>
        public void ToggleHotkeyRefreshCapture() => _bridge.ToggleHotkeyRefreshCapture();

        /// <summary>
        /// Flips "Minimize to system tray" exactly as clicking its WinForms
        /// checkbox would (Phase 4.12) — forwards to MainForm's
        /// `ToggleMinimizeToTray`, which flips `_chkMinToTray.Checked`, firing
        /// the real, unchanged `CheckedChanged` lambda (saves via `QueueSave`).
        /// </summary>
        public void ToggleMinimizeToTray() => _bridge.ToggleMinimizeToTray();

        /// <summary>
        /// Flips "Start with Windows" exactly as clicking its WinForms checkbox
        /// would (Phase 4.12) — forwards to MainForm's `ToggleStartWithWindows`,
        /// firing the real, unchanged `CheckedChanged` lambda (also updates the
        /// registry startup entry and cascades `StartMinimized` off if this is
        /// being turned off, same as the WinForms UI always has).
        /// </summary>
        public void ToggleStartWithWindows() => _bridge.ToggleStartWithWindows();

        /// <summary>
        /// Flips "Start minimized to tray" exactly as clicking its WinForms
        /// checkbox would (Phase 4.12) — forwards to MainForm's
        /// `ToggleStartMinimized`, which no-ops if `StartWithWindows` is off
        /// (mirroring the real checkbox's `Enabled` gating) or otherwise fires
        /// the real, unchanged `CheckedChanged` lambda.
        /// </summary>
        public void ToggleStartMinimized() => _bridge.ToggleStartMinimized();

        /// <summary>
        /// True when there is a Save Changes snapshot to revert for the
        /// currently selected game (Phase 4.13 — net-new, no prior WinForms UI
        /// for this existed). False both when nothing has been saved yet this
        /// session and when the last save was for a *different* game than the
        /// one currently selected — Undo only ever reverts "the last committed
        /// change to the current game's config", per the README, never a
        /// stale snapshot for whatever used to be selected.
        /// </summary>
        public bool CanUndo => _bridge.CanUndo;

        /// <summary>
        /// Reverts the selected game to its state immediately before the last
        /// Save Changes commit, exactly as the mock's "↶ Undo" describes —
        /// forwards to MainForm's `UndoLastSave`, which restores the captured
        /// GameName/RegexPattern/Profiles snapshot, persists, and re-enforces
        /// if the game is active. No-op if <see cref="CanUndo"/> is false.
        /// Single-level: consumes the snapshot, so a second call has nothing
        /// left to revert to until another Save Changes happens.
        /// </summary>
        public void Undo() => _bridge.UndoLastSave();

        /// <summary>
        /// True if a real process matching <paramref name="game"/>'s pattern is
        /// currently running, independent of whether Borderless is toggled on
        /// for it (Phase 4.14) — same `_runningGames` set `EnforceTimer_Tick`
        /// refreshes every second (via `RefreshRunningGames`) purely to sort
        /// running games to the top of the real games list; `AppStateService.Games`
        /// already reflects that same order for free, since it's a direct
        /// passthrough to the same `List&lt;GameConfig&gt;` `RefreshGamesListOrder()`
        /// sorts in place. This getter exists for anything that wants the raw
        /// running/not-running fact itself, not just the ordering it produces.
        /// </summary>
        public bool IsGameRunning(GameConfig game) => _bridge.IsGameRunning(game);

        /// <summary>
        /// Re-enumerates currently open, blocklist-filtered windows — same
        /// `MainForm.GetOpenWindowEntries()` used by `BtnAddRunning_Click`'s
        /// dialog (Phase 3.4). A method, not a cached property: each call walks
        /// every running process, so callers should call it once per render
        /// rather than in a loop.
        /// </summary>
        public List<OpenWindowEntry> GetOpenWindows() => _bridge.GetOpenWindows();

        /// <summary>
        /// Currently connected monitors — same `MainForm._monitors` list built by
        /// `RefreshMonitors()` (Phase 3.5), including the `Width`/`Height`/`Primary`
        /// fields added there for this view. Live accessor, not a snapshot: `_monitors`
        /// is a `readonly` list that RefreshMonitors clears and repopulates in place,
        /// so the same instance is always current.
        /// </summary>
        public List<MonitorItem> Monitors => _bridge.Monitors;

        /// <summary>
        /// Real registration-outcome text for the "Add focused app" hotkey, as last
        /// set by `TryRegisterHotkey` on its status label (e.g. "Active", "Disabled",
        /// "Not active — conflicts with another app"). A read of that label's current
        /// `Text`, not a reimplementation — the hotkey system itself is frozen
        /// (MIGRATION_PLAN.md), so this only observes its existing output.
        /// </summary>
        public string HotkeyAddStatusText => _bridge.HotkeyAddStatusText;

        /// <summary>Same as <see cref="HotkeyAddStatusText"/>, for the "Re-apply / refresh displays" hotkey.</summary>
        public string HotkeyRefreshStatusText => _bridge.HotkeyRefreshStatusText;

        /// <summary>Debounced save, same as MainForm's QueueSave() (600ms via _saveDebounce).</summary>
        public void QueueSave() => _bridge.QueueSave();

        /// <summary>Immediate save, same as MainForm's SaveConfig().</summary>
        public void SaveNow() => _bridge.SaveConfig();

        /// <summary>
        /// Phase 6.4: the Activity Log detail pane's "Restart As Admin" action
        /// (shown for Warn/Error entries) forwards to MainForm's real
        /// `RestartAsAdmin()` — the same relaunch-elevated flow the elevation
        /// dialog already uses, not a reimplementation. Real and irreversible
        /// (closes this instance and launches an elevated one via UAC), so it's
        /// only ever invoked on an explicit user click, never automatically.
        /// </summary>
        public void RestartAsAdmin() => _bridge.RestartAsAdmin();

        /// <summary>MainForm's `_isElevated` — set once by `CheckElevation()` at
        /// startup, before BuildUI runs. Drives Settings &gt; Permissions'
        /// status line and whether its "Restart as Administrator" button/hint
        /// show at all (nothing to do if already elevated).</summary>
        public bool IsElevated => _bridge.IsElevated;

        /// <summary>
        /// Settings &gt; Permissions' "Restart as Administrator" button —
        /// unlike <see cref="RestartAsAdmin"/> above (the Activity Log's
        /// entry point, which restarts immediately with no prompt), this
        /// shows ElevationDialog (the in-app Blazor notification) first,
        /// since this entry point is reached by casually browsing Settings
        /// rather than reacting to a specific warn/error log entry.
        /// RestartAsAdmin() itself only runs once the user clicks that
        /// dialog's own button.
        /// </summary>
        public void ConfirmAndRestartAsAdmin() => _bridge.ConfirmRestartAsAdmin();

        /// <summary>
        /// Settings &gt; Permissions "Always start as Administrator" checkbox.
        /// Also set to true automatically whenever RestartAsAdmin() actually
        /// runs, from any of its several trigger points — see AppSettings.
        /// AlwaysRunAsAdmin's own doc comment.
        /// </summary>
        public bool AlwaysRunAsAdmin => _bridge.AlwaysRunAsAdmin;

        /// <summary>Same shape as ToggleMinimizeToTray/ToggleStartWithWindows — flips the persisted preference.</summary>
        public void ToggleAlwaysRunAsAdmin() => _bridge.AlwaysRunAsAdmin = !_bridge.AlwaysRunAsAdmin;

        /// <summary>ElevationDialog.razor's "Not Now" button and the first step of its "Restart As Admin" button.</summary>
        public void DismissElevationDialog() => _bridge.DismissElevationDialog();

        /// <summary>
        /// Settings &gt; Diagnostics "Verbose logging" toggle — was a static
        /// ToggleSwitch with no backing field or OnToggle at all. Same shape
        /// as AlwaysRunAsAdmin above.
        /// </summary>
        public bool VerboseLogging => _bridge.VerboseLogging;

        /// <summary>Same shape as ToggleAlwaysRunAsAdmin — flips the persisted preference.</summary>
        public void ToggleVerboseLogging() => _bridge.VerboseLogging = !_bridge.VerboseLogging;

        /// <summary>
        /// Phase 8.3: SteamGridDB personal API key, pasted in Settings >
        /// Artwork (originally landed in Settings > Diagnostics; moved to its
        /// own nav entry once Hotkeys/Behaviour were split the same way).
        /// Unlike most of this service's write methods, there's no
        /// pre-existing WinForms control to trigger — this setting never existed
        /// before this migration — so <see cref="SetArtworkApiKey"/> forwards to
        /// a MainForm method that mutates `_settings.SteamGridDbApiKey` and
        /// persists directly (same shape as `AddGameFromRunningWindow`, Phase
        /// 4.8, the other case with no real control to reuse).
        /// </summary>
        public string ArtworkApiKey => _bridge.ArtworkApiKey;
        public void SetArtworkApiKey(string key) => _bridge.SetSteamGridDbApiKey(key);

        /// <summary>
        /// Phase 8.3: commits fetched artwork paths onto MainForm's real
        /// `_selectedGame` and persists — the write half of the "Fetch Artwork"
        /// flow (Services/ArtworkService.cs does the actual network/disk work,
        /// which is stateless I/O with nothing WinForms-specific about it, so
        /// it's called directly from the Razor picker component; only the
        /// "commit onto this game's config" step needs to go through MainForm,
        /// same division as everywhere else in Phase 4). Same "no pre-existing
        /// control to reuse" shape as SetArtworkApiKey.
        /// </summary>
        public void ApplyArtwork(string heroPath, string iconPath) => _bridge.ApplyArtwork(heroPath, iconPath);

        /// <summary>
        /// Settings &gt; Ignore List (Phase 8.4): live, sorted contents of the
        /// user's own additions to MainForm's _settings.IgnoredProcesses — NOT
        /// the built-in system-process list (_systemProcessBlocklist), which
        /// stays internal/never shown here so this page only ever lists what
        /// the user actually chose to add. Both sets are checked together by
        /// GetOpenWindowEntries/TryGetHotkeyTargetExe, so adding/removing here
        /// immediately changes what shows up in every open-windows picker and
        /// the Matching tab's live-test list.
        /// </summary>
        public List<string> IgnoredProcesses => _bridge.GetIgnoredProcesses();

        /// <summary>Adds an exe name to the ignore list, e.g. from the "Add Program" picker (reuses the same GetOpenWindows rows the game picker does).</summary>
        public void AddIgnoredProcess(string exeName) => _bridge.AddIgnoredProcess(exeName);

        /// <summary>Removes a single entry from the ignore list.</summary>
        public void RemoveIgnoredProcess(string exeName) => _bridge.RemoveIgnoredProcess(exeName);

        /// <summary>
        /// "Clear All" — shows a native confirmation before discarding every
        /// program the user has added to the ignore list. MainForm's own
        /// built-in system-process filtering (_systemProcessBlocklist) isn't
        /// part of this set and is unaffected either way.
        /// </summary>
        public void ConfirmClearIgnoredProcesses() => _bridge.ConfirmClearIgnoredProcesses();

        /// <summary>
        /// Phase 8.4: the Display tab's Width/Height/Offset X/Y stepper
        /// fields — live reads of MainForm's real `_numWidth`/`_numHeight`/
        /// `_numOffsetX`/`_numOffsetY` (NumericTextBox shadow controls, same
        /// pattern as ConstrainMouse/`_chkConstrain`). Adjust* applies a
        /// signed delta (from SteppedNumberField's ▲/▼, ×10 Shift/×100 Ctrl)
        /// directly to the shadow control's own Value, which self-clamps to
        /// its configured Minimum/Maximum (0..16384 for Width/Height,
        /// -16384..16384 for Offset) rather than throwing. Committed onto the
        /// selected game's actual profile the same way it always has been —
        /// by SaveUIToProfile, on Save Changes or a monitor-scope switch —
        /// this only makes the live value itself editable from Blazor.
        /// </summary>
        public int Width => _bridge.Width;
        public void AdjustWidth(int delta) => _bridge.AdjustWidth(delta);
        public int Height => _bridge.Height;
        public void AdjustHeight(int delta) => _bridge.AdjustHeight(delta);
        public int OffsetX => _bridge.OffsetX;
        public void AdjustOffsetX(int delta) => _bridge.AdjustOffsetX(delta);
        public int OffsetY => _bridge.OffsetY;
        public void AdjustOffsetY(int delta) => _bridge.AdjustOffsetY(delta);

        /// <summary>Same shape as Width/Height/OffsetX/OffsetY above, for
        /// Settings &gt; Monitors' default-profile stepper fields
        /// (`_numSetWidth`/`_numSetHeight`/`_numSetOffsetX`/`_numSetOffsetY`),
        /// committed by "Save Monitor Default" (BtnSaveDefault_Click).</summary>
        public int MonitorDefaultWidth => _bridge.MonitorDefaultWidth;
        public void AdjustMonitorDefaultWidth(int delta) => _bridge.AdjustMonitorDefaultWidth(delta);
        public int MonitorDefaultHeight => _bridge.MonitorDefaultHeight;
        public void AdjustMonitorDefaultHeight(int delta) => _bridge.AdjustMonitorDefaultHeight(delta);
        public int MonitorDefaultOffsetX => _bridge.MonitorDefaultOffsetX;
        public void AdjustMonitorDefaultOffsetX(int delta) => _bridge.AdjustMonitorDefaultOffsetX(delta);
        public int MonitorDefaultOffsetY => _bridge.MonitorDefaultOffsetY;
        public void AdjustMonitorDefaultOffsetY(int delta) => _bridge.AdjustMonitorDefaultOffsetY(delta);

        /// <summary>
        /// The four alignment buttons (Phase 8.6, replacing the old single
        /// "Center On Monitor" link) — sets the active scope's Offset X/Y per
        /// <paramref name="mode"/> against that monitor's real connected
        /// resolution. No-ops (with a status message) if the target monitor
        /// isn't currently connected, since alignment needs its real
        /// physical size.
        /// </summary>
        public void AlignOnMonitor(MonitorAlignMode mode) => _bridge.AlignOnMonitor(mode);

        /// <summary>Same as <see cref="AlignOnMonitor"/>, for Settings &gt; Monitors' default-profile fields.</summary>
        public void AlignOnMonitorDefault(MonitorAlignMode mode) => _bridge.AlignOnMonitorDefault(mode);

        /// <summary>
        /// The Display tab's "Lock cursor to window bounds" checkbox for the
        /// selected game/scope — a live read of MainForm's `_chkConstrain.Checked`
        /// (same shadow-control pattern as <see cref="HotkeyAddStatusText"/>),
        /// not the selected game's saved profile: like the WinForms UI it
        /// mirrors, toggling this only takes effect on the in-memory profile
        /// (and is only persisted) via Save Changes or a monitor-scope switch —
        /// see `SaveUIToProfile` in program.cs.
        /// </summary>
        public bool ConstrainMouse => _bridge.ConstrainMouse;

        /// <summary>Flips <see cref="ConstrainMouse"/> exactly as clicking the real checkbox would.</summary>
        public void ToggleConstrainMouse() => _bridge.ToggleConstrainMouse();

        /// <summary>Same as <see cref="ConstrainMouse"/>, for Settings &gt; Monitors' default-profile checkbox (`_chkSetConstrain`), committed by "Save Monitor Default".</summary>
        public bool ConstrainMouseDefault => _bridge.ConstrainMouseDefault;

        /// <summary>Flips <see cref="ConstrainMouseDefault"/> exactly as clicking the real checkbox would.</summary>
        public void ToggleConstrainMouseDefault() => _bridge.ToggleConstrainMouseDefault();

        /// <summary>
        /// Phase 8.9: WindowControls.razor's three buttons (minimize/
        /// maximize-restore/close), replacing the native title bar's system
        /// buttons now that MainForm draws its own frameless chrome (see
        /// program.cs's WndProc WM_NCCALCSIZE/WM_NCHITTEST handling). Each
        /// forwards to the exact same WindowState/Close() calls a native
        /// button click already triggered, so MinimizeToTray-on-minimize and
        /// MinimizeToTray-on-close (OnResize/OnFormClosing) keep working
        /// completely unchanged — nothing here reimplements that behavior.
        /// </summary>
        public void MinimizeWindow() => _bridge.MinimizeWindow();

        /// <summary>Toggles between maximized and normal, same as double-clicking the title bar drag region (WM_NCHITTEST's HTCAPTION) already does natively.</summary>
        public void ToggleMaximizeWindow() => _bridge.ToggleMaximizeWindow();

        /// <summary>Closes the app, respecting the same "minimize to tray instead" setting a native close click always has.</summary>
        public void CloseWindow() => _bridge.CloseWindow();

        /// <summary>Drives the maximize/restore button's glyph swap — true once MainForm.WindowState is Maximized, however that happened (button click, title bar double-click, Win+Up, drag-to-top-edge).</summary>
        public bool IsWindowMaximized => _bridge.IsWindowMaximized();

        /// <summary>
        /// The frameless title bar's drag region — call from the header's
        /// own @onmousedown (not its buttons, which need
        /// @onmousedown:stopPropagation so this never fires for them). See
        /// MainForm.BeginWindowDrag's doc comment for why this replaced a
        /// WM_NCHITTEST-based approach that turned out not to work with
        /// BlazorWebView covering the reclaimed caption area.
        /// </summary>
        public void BeginWindowDrag() => _bridge.BeginWindowDrag();
    }
}
