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
        private readonly Action _queueSave;
        private readonly Action _saveNow;

        public AppStateService(
            Func<AppSettings> getSettings,
            Func<GameConfig?> getSelectedGame,
            Action queueSave,
            Action saveNow)
        {
            _getSettings     = getSettings;
            _getSelectedGame = getSelectedGame;
            _queueSave       = queueSave;
            _saveNow         = saveNow;
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

        /// <summary>Debounced save, same as MainForm's QueueSave() (600ms via _saveDebounce).</summary>
        public void QueueSave() => _queueSave();

        /// <summary>Immediate save, same as MainForm's SaveConfig().</summary>
        public void SaveNow() => _saveNow();
    }
}
