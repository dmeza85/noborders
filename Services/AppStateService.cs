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
    /// file itself. Not exercised yet: Phase 3 (3.1–3.7) is read-only wiring;
    /// Phase 4 wires the actual event handlers that call these.
    /// </summary>
    public sealed class AppStateService
    {
        private readonly Func<AppSettings> _getSettings;
        private readonly Func<GameConfig?> _getSelectedGame;
        private readonly Func<List<OpenWindowEntry>> _getOpenWindows;
        private readonly Func<List<MonitorItem>> _getMonitors;
        private readonly Func<string> _getHotkeyAddStatus;
        private readonly Func<string> _getHotkeyRefreshStatus;
        private readonly Action _queueSave;
        private readonly Action _saveNow;

        public AppStateService(
            Func<AppSettings> getSettings,
            Func<GameConfig?> getSelectedGame,
            Func<List<OpenWindowEntry>> getOpenWindows,
            Func<List<MonitorItem>> getMonitors,
            Func<string> getHotkeyAddStatus,
            Func<string> getHotkeyRefreshStatus,
            Action queueSave,
            Action saveNow)
        {
            _getSettings            = getSettings;
            _getSelectedGame        = getSelectedGame;
            _getOpenWindows         = getOpenWindows;
            _getMonitors            = getMonitors;
            _getHotkeyAddStatus     = getHotkeyAddStatus;
            _getHotkeyRefreshStatus = getHotkeyRefreshStatus;
            _queueSave              = queueSave;
            _saveNow                = saveNow;
        }

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
        /// rail row in the (still-live) WinForms UI or, once Phase 4.1 wires rail
        /// clicks, in the Razor UI. Components that need *something* to display
        /// before a selection exists fall back to <c>Games.FirstOrDefault()</c>
        /// themselves (see MainShell.razor) rather than this service silently
        /// picking one — selection state should read exactly as it is.
        /// </summary>
        public GameConfig? SelectedGame => _getSelectedGame();

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
