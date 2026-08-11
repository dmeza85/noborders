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
    /// </summary>
    public sealed class AppStateService
    {
        private readonly Func<AppSettings> _getSettings;
        private readonly Func<GameConfig?> _getSelectedGame;
        private readonly Func<List<OpenWindowEntry>> _getOpenWindows;
        private readonly Func<List<MonitorItem>> _getMonitors;
        private readonly Func<string> _getHotkeyAddStatus;
        private readonly Func<string> _getHotkeyRefreshStatus;
        private readonly Action<GameConfig> _selectGame;
        private readonly Func<List<string>> _getMonitorOptions;
        private readonly Func<string> _getActiveScope;
        private readonly Action<string> _selectMonitorScope;
        private readonly Action _toggleGameActive;
        private readonly Action _loadMonitorDefaults;
        private readonly Func<string> _getPendingDisplayName;
        private readonly Action _fetchName;
        private readonly Action _saveGameChanges;
        private readonly Action _queueSave;
        private readonly Action _saveNow;

        public AppStateService(
            Func<AppSettings> getSettings,
            Func<GameConfig?> getSelectedGame,
            Func<List<OpenWindowEntry>> getOpenWindows,
            Func<List<MonitorItem>> getMonitors,
            Func<string> getHotkeyAddStatus,
            Func<string> getHotkeyRefreshStatus,
            Action<GameConfig> selectGame,
            Func<List<string>> getMonitorOptions,
            Func<string> getActiveScope,
            Action<string> selectMonitorScope,
            Action toggleGameActive,
            Action loadMonitorDefaults,
            Func<string> getPendingDisplayName,
            Action fetchName,
            Action saveGameChanges,
            Action queueSave,
            Action saveNow)
        {
            _getSettings            = getSettings;
            _getSelectedGame        = getSelectedGame;
            _getOpenWindows         = getOpenWindows;
            _getMonitors            = getMonitors;
            _getHotkeyAddStatus     = getHotkeyAddStatus;
            _getHotkeyRefreshStatus = getHotkeyRefreshStatus;
            _selectGame             = selectGame;
            _getMonitorOptions      = getMonitorOptions;
            _getActiveScope         = getActiveScope;
            _loadMonitorDefaults    = loadMonitorDefaults;
            _getPendingDisplayName  = getPendingDisplayName;
            _fetchName              = fetchName;
            _saveGameChanges        = saveGameChanges;
            _selectMonitorScope     = selectMonitorScope;
            _toggleGameActive       = toggleGameActive;
            _queueSave              = queueSave;
            _saveNow                = saveNow;
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
        public AppSettings Settings => _getSettings();

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
        public GameConfig? SelectedGame => _getSelectedGame();

        /// <summary>
        /// Selects a game exactly as clicking its row in the WinForms games list
        /// would (Phase 4.1) — forwards to MainForm's own lookup-by-reference
        /// against `_lstGames.Items`/`SelectedIndex`, so the real
        /// `LstGames_SelectedIndexChanged` handler runs unchanged and every
        /// WinForms control it also updates (monitor combo, advanced fields,
        /// etc.) stays in sync even though those controls aren't the visible UI
        /// anymore — MainForm remains the single source of truth for selection.
        /// </summary>
        public void SelectGame(GameConfig game) => _selectGame(game);

        /// <summary>
        /// Options for the target-monitor dropdown — same list `_cmbMonitor.Items`
        /// holds (MIGRATION_PLAN.md Phase 4.2): connected monitors plus any saved
        /// `KnownMonitors` not currently connected, per `SyncMonitorComboBoxes()`.
        /// Read directly off the WinForms control rather than recomputed here, so
        /// there's exactly one place that decides this list's contents/order.
        /// </summary>
        public List<string> MonitorOptions => _getMonitorOptions();

        /// <summary>
        /// MainForm's `_activeScope` — which monitor's profile is currently being
        /// edited for the selected game. Empty until a game has been selected at
        /// least once (Phase 4.1's `SelectGame`/`LstGames_SelectedIndexChanged`
        /// populates it). Components that need a monitor to show before this is
        /// set fall back to their own display-only pick (see DisplayDetailPane's
        /// `TargetMonitorName`), same rationale as `SelectedGame`.
        /// </summary>
        public string ActiveMonitorScope => _getActiveScope();

        /// <summary>
        /// Selects a monitor scope exactly as picking it in the WinForms combo
        /// would (Phase 4.2) — forwards to MainForm's own `SelectMonitorScope`,
        /// so the real, unchanged `CmbMonitor_SelectedIndexChanged` handler runs
        /// (which also persists the previous scope's edited fields via
        /// `SaveUIToProfile` before switching).
        /// </summary>
        public void SelectMonitorScope(string scope) => _selectMonitorScope(scope);

        /// <summary>
        /// Flips the selected game's `IsActive` exactly as clicking the WinForms
        /// borderless toggle would (Phase 4.3) — forwards to MainForm's
        /// `ToggleGameActive`, so the real, unchanged `ChkActive_CheckedChanged`
        /// handler runs (which also saves the config and immediately
        /// enforces/un-enforces the window). No-op if no game is selected.
        /// </summary>
        public void ToggleActive() => _toggleGameActive();

        /// <summary>
        /// Loads the current monitor scope's saved default profile onto the
        /// selected game and immediately applies it, exactly as clicking "Load
        /// Monitor Defaults" would (Phase 4.4) — forwards to MainForm's own
        /// `PerformClick()` on that button, so the real, unchanged
        /// `BtnLoadDefaults_Click` runs. No-op (with a status message) if there's
        /// no saved default for the active scope.
        /// </summary>
        public void LoadMonitorDefaults() => _loadMonitorDefaults();

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
        public string PendingDisplayName => _getPendingDisplayName();

        /// <summary>
        /// Fetches the display name from the selected game's currently-running
        /// window, exactly as clicking the ↺ button would (Phase 4.5) — forwards
        /// to MainForm's `PerformClick()` on the real fetch button, so the real,
        /// unchanged `BtnFetchName_Click` runs. Only fills
        /// <see cref="PendingDisplayName"/>; does not persist anything (that's
        /// Save Changes' job). No-op (with a status message) if the game isn't
        /// currently running.
        /// </summary>
        public void FetchName() => _fetchName();

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
        public void SaveGameChanges() => _saveGameChanges();

        /// <summary>
        /// Re-enumerates currently open, blocklist-filtered windows — same
        /// `MainForm.GetOpenWindowEntries()` used by `BtnAddRunning_Click`'s
        /// dialog (Phase 3.4). A method, not a cached property: each call walks
        /// every running process, so callers should call it once per render
        /// rather than in a loop.
        /// </summary>
        public List<OpenWindowEntry> GetOpenWindows() => _getOpenWindows();

        /// <summary>
        /// Currently connected monitors — same `MainForm._monitors` list built by
        /// `RefreshMonitors()` (Phase 3.5), including the `Width`/`Height`/`Primary`
        /// fields added there for this view. Live accessor, not a snapshot: `_monitors`
        /// is a `readonly` list that RefreshMonitors clears and repopulates in place,
        /// so the same instance is always current.
        /// </summary>
        public List<MonitorItem> Monitors => _getMonitors();

        /// <summary>
        /// Real registration-outcome text for the "Add focused app" hotkey, as last
        /// set by `TryRegisterHotkey` on its status label (e.g. "Active", "Disabled",
        /// "Not active — conflicts with another app"). A read of that label's current
        /// `Text`, not a reimplementation — the hotkey system itself is frozen
        /// (MIGRATION_PLAN.md), so this only observes its existing output.
        /// </summary>
        public string HotkeyAddStatusText => _getHotkeyAddStatus();

        /// <summary>Same as <see cref="HotkeyAddStatusText"/>, for the "Re-apply / refresh displays" hotkey.</summary>
        public string HotkeyRefreshStatusText => _getHotkeyRefreshStatus();

        /// <summary>Debounced save, same as MainForm's QueueSave() (600ms via _saveDebounce).</summary>
        public void QueueSave() => _queueSave();

        /// <summary>Immediate save, same as MainForm's SaveConfig().</summary>
        public void SaveNow() => _saveNow();
    }
}
