using System;
using System.Collections.Generic;

namespace NoBorders.Services
{
    public sealed class AppStateService
    {
        private readonly IMainFormBridge _bridge;

        internal AppStateService(IMainFormBridge bridge)
        {
            _bridge = bridge;
        }

        public event Action? Changed;

        public void RaiseChanged() => Changed?.Invoke();

        public AppSettings Settings => _bridge.Settings;

        public List<GameConfig> Games => Settings.Games;
        public HashSet<string> KnownMonitors => Settings.KnownMonitors;
        public Dictionary<string, GameDisplayProfile> MonitorDefaults => Settings.MonitorDefaults;
        public bool MinimizeToTray => Settings.MinimizeToTray;
        public bool StartWithWindows => Settings.StartWithWindows;
        public bool StartMinimized => Settings.StartMinimized;
        public HotkeyConfig HotkeyAddApp => Settings.HotkeyAddApp;
        public HotkeyConfig HotkeyRefreshApp => Settings.HotkeyRefreshApp;

        public GameConfig? SelectedGame => _bridge.SelectedGame;

        public void SelectGame(GameConfig game) => _bridge.SelectGame(game);

        public List<string> MonitorOptions => _bridge.MonitorOptions;

        public string ActiveMonitorScope => _bridge.ActiveMonitorScope;

        public void SelectMonitorScope(string scope) => _bridge.SelectMonitorScope(scope);

        public void ToggleActive() => _bridge.ToggleGameActive();

        public void ReapplyBorderless() => _bridge.ReapplyBorderless();

        public void LoadMonitorDefaults() => _bridge.LoadMonitorDefaultsForSelectedGame();

        public string PendingDisplayName => _bridge.PendingDisplayName;

        public void FetchName() => _bridge.FetchNameForSelectedGame();

        public string PendingRegexPattern => _bridge.PendingRegexPattern;

        public void SetPendingRegexPattern(string pattern) => _bridge.SetPendingRegexPattern(pattern);

        public MatchTargetMode PendingMatchTarget => _bridge.PendingMatchTarget;

        public void SetPendingMatchTarget(MatchTargetMode mode) => _bridge.SetPendingMatchTarget(mode);

        public void SaveGameChanges() => _bridge.SaveGameChanges();

        public void RemoveSelectedGame() => _bridge.RemoveSelectedGame();

        public bool AddGameFromRunningWindow(OpenWindowEntry entry) => _bridge.AddGameFromRunningWindow(entry);

        public bool BrowseForExe() => _bridge.BrowseForExe();

        public string ActiveMonitorDefaultScope => _bridge.ActiveMonitorDefaultScope;

        public void SelectMonitorDefaultScope(string scope) => _bridge.SelectMonitorDefaultScope(scope);

        public void SaveMonitorDefault() => _bridge.SaveMonitorDefault();

        public void ApplyMonitorDefaultToAllGames() => _bridge.ApplyMonitorDefaultToAllGames();

        public void DeleteMonitorDefault(string scope) => _bridge.DeleteMonitorDefault(scope);

        public void DetectDisplays() => _bridge.DetectDisplays();

        public void RemoveAllSavedMonitors() => _bridge.RemoveAllSavedMonitors();

        public bool IsCapturingHotkeyAdd => _bridge.IsCapturingHotkeyAdd;

        public bool IsCapturingHotkeyRefresh => _bridge.IsCapturingHotkeyRefresh;

        public void ToggleHotkeyAddCapture() => _bridge.ToggleHotkeyAddCapture();

        public void ToggleHotkeyRefreshCapture() => _bridge.ToggleHotkeyRefreshCapture();

        public void ToggleMinimizeToTray() => _bridge.ToggleMinimizeToTray();

        public void ToggleStartWithWindows() => _bridge.ToggleStartWithWindows();

        public void ToggleStartMinimized() => _bridge.ToggleStartMinimized();

        public bool CanUndo => _bridge.CanUndo;

        public void Undo() => _bridge.UndoLastSave();

        public bool IsGameRunning(GameConfig game) => _bridge.IsGameRunning(game);

        public List<OpenWindowEntry> GetOpenWindows() => _bridge.GetOpenWindows();

        public List<MonitorItem> Monitors => _bridge.Monitors;

        public string HotkeyAddStatusText => _bridge.HotkeyAddStatusText;

        public string HotkeyRefreshStatusText => _bridge.HotkeyRefreshStatusText;

        public void QueueSave() => _bridge.QueueSave();

        public void SaveNow() => _bridge.SaveConfig();

        public void RestartAsAdmin() => _bridge.RestartAsAdmin();

        public void RestartAsStandardUser() => _bridge.RestartAsStandardUser();

        public bool IsElevated => _bridge.IsElevated;

        public void ConfirmAndRestartAsAdmin() => _bridge.ConfirmRestartAsAdmin();

        public void ConfirmAndRestartAsStandardUser() => _bridge.ConfirmRestartAsStandardUser();

        public bool AlwaysRunAsAdmin => _bridge.AlwaysRunAsAdmin;

        public void ToggleAlwaysRunAsAdmin() => _bridge.AlwaysRunAsAdmin = !_bridge.AlwaysRunAsAdmin;

        public void DismissElevationDialog() => _bridge.DismissElevationDialog();

        public bool VerboseLogging => _bridge.VerboseLogging;

        public void ToggleVerboseLogging() => _bridge.VerboseLogging = !_bridge.VerboseLogging;

        public bool ToastVisible => _bridge.ToastVisible;
        public string ToastTitle => _bridge.ToastTitle;
        public string ToastDetail => _bridge.ToastDetail;
        public string ToastIconPath => _bridge.ToastIconPath;
        public LogLevel ToastLevel => _bridge.ToastLevel;

        public MonitorResolution? GetLastKnownMonitorResolution(string monitorId) =>
            _bridge.GetLastKnownMonitorResolution(monitorId);

        public string ArtworkApiKey => _bridge.ArtworkApiKey;
        public void SetArtworkApiKey(string key) => _bridge.SetSteamGridDbApiKey(key);

        public void ApplyArtwork(string heroPath, string iconPath) => _bridge.ApplyArtwork(heroPath, iconPath);

        public List<string> IgnoredProcesses => _bridge.GetIgnoredProcesses();

        public void AddIgnoredProcess(string exeName) => _bridge.AddIgnoredProcess(exeName);

        public void RemoveIgnoredProcess(string exeName) => _bridge.RemoveIgnoredProcess(exeName);

        public void ConfirmClearIgnoredProcesses() => _bridge.ConfirmClearIgnoredProcesses();

        public void ConfirmFullReset() => _bridge.ConfirmFullReset();

        public int Width => _bridge.Width;
        public void AdjustWidth(int delta) => _bridge.AdjustWidth(delta);
        public int Height => _bridge.Height;
        public void AdjustHeight(int delta) => _bridge.AdjustHeight(delta);
        public int OffsetX => _bridge.OffsetX;
        public void AdjustOffsetX(int delta) => _bridge.AdjustOffsetX(delta);
        public int OffsetY => _bridge.OffsetY;
        public void AdjustOffsetY(int delta) => _bridge.AdjustOffsetY(delta);

        public int MonitorDefaultWidth => _bridge.MonitorDefaultWidth;
        public void AdjustMonitorDefaultWidth(int delta) => _bridge.AdjustMonitorDefaultWidth(delta);
        public int MonitorDefaultHeight => _bridge.MonitorDefaultHeight;
        public void AdjustMonitorDefaultHeight(int delta) => _bridge.AdjustMonitorDefaultHeight(delta);
        public int MonitorDefaultOffsetX => _bridge.MonitorDefaultOffsetX;
        public void AdjustMonitorDefaultOffsetX(int delta) => _bridge.AdjustMonitorDefaultOffsetX(delta);
        public int MonitorDefaultOffsetY => _bridge.MonitorDefaultOffsetY;
        public void AdjustMonitorDefaultOffsetY(int delta) => _bridge.AdjustMonitorDefaultOffsetY(delta);

        public void AlignOnMonitor(MonitorAlignMode mode) => _bridge.AlignOnMonitor(mode);

        public void AlignOnMonitorDefault(MonitorAlignMode mode) => _bridge.AlignOnMonitorDefault(mode);

        public bool ConstrainMouse => _bridge.ConstrainMouse;

        public void ToggleConstrainMouse() => _bridge.ToggleConstrainMouse();

        public bool ConstrainMouseDefault => _bridge.ConstrainMouseDefault;

        public void ToggleConstrainMouseDefault() => _bridge.ToggleConstrainMouseDefault();

        public void MinimizeWindow() => _bridge.MinimizeWindow();

        public void ToggleMaximizeWindow() => _bridge.ToggleMaximizeWindow();

        public void CloseWindow() => _bridge.CloseWindow();

        public bool IsWindowMaximized => _bridge.IsWindowMaximized();

        public void BeginWindowDrag() => _bridge.BeginWindowDrag();
    }
}
