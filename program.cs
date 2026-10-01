
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using NoBorders.Services;

namespace NoBorders
{

    public class GameDisplayProfile
    {
        public int  Width         { get; set; } = 1920;
        public int  Height        { get; set; } = 1080;
        public int  OffsetX       { get; set; } = 0;
        public int  OffsetY       { get; set; } = 0;
        public bool ConstrainMouse { get; set; } = false;
    }

    public enum MatchTargetMode { ProcessName, WindowTitle }

    public class GameConfig
    {
        public string GameName     { get; set; } = string.Empty;
        public string RegexPattern { get; set; } = string.Empty;
        public string ExePath      { get; set; } = string.Empty;
        public bool   IsActive     { get; set; } = false;
        public MatchTargetMode MatchTarget { get; set; } = MatchTargetMode.ProcessName;
        public Dictionary<string, GameDisplayProfile> Profiles { get; set; }
            = new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);

        public string HeroImagePath { get; set; } = string.Empty;
        public string IconImagePath { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        private Regex? _compiledPattern;

        private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(500);

        [System.Text.Json.Serialization.JsonIgnore]
        public Regex CompiledPattern
        {
            get
            {
                if (_compiledPattern == null && !string.IsNullOrEmpty(RegexPattern))
                {
                    try
                    {
                        _compiledPattern = new Regex(
                            RegexPattern,
                            RegexOptions.IgnoreCase | RegexOptions.Compiled,
                            PatternMatchTimeout);
                    }
                    catch
                    {
                        _compiledPattern = new Regex("(?!)", RegexOptions.Compiled, PatternMatchTimeout);
                    }
                }
                return _compiledPattern ?? new Regex("(?!)", RegexOptions.Compiled, PatternMatchTimeout);
            }
        }

        public void InvalidatePattern() => _compiledPattern = null;

        public bool IsMatch(string exeName, string windowTitle = "") => IsMatch(exeName, windowTitle, MatchTarget);

        public bool IsMatch(string exeName, string windowTitle, MatchTargetMode targetOverride)
        {
            string target = targetOverride == MatchTargetMode.WindowTitle ? windowTitle : exeName;
            try { return CompiledPattern.IsMatch(target); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }

    public class HotkeyConfig
    {
        public uint Modifiers { get; set; }
        public uint Key       { get; set; }

        public override string ToString()
        {
            if (Key == 0) return "None";
            var parts = new List<string>();
            uint visibleMods = Modifiers & ~0x4000u;
            if ((visibleMods & 0x0002) != 0) parts.Add("Ctrl");
            if ((visibleMods & 0x0004) != 0) parts.Add("Shift");
            if ((visibleMods & 0x0001) != 0) parts.Add("Alt");
            parts.Add(((Keys)Key).ToString());
            return string.Join(" + ", parts);
        }
    }

    public class AppSettings
    {
        public HashSet<string>                       KnownMonitors   { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, GameDisplayProfile> MonitorDefaults { get; set; }
            = new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, MonitorResolution> LastKnownMonitorResolutions { get; set; }
            = new Dictionary<string, MonitorResolution>(StringComparer.OrdinalIgnoreCase);
        public List<GameConfig> Games              { get; set; } = new List<GameConfig>();
        public bool             MinimizeToTray     { get; set; } = true;
        public bool             StartWithWindows   { get; set; } = false;
        public bool             StartMinimized     { get; set; } = false;
        public HotkeyConfig     HotkeyAddApp       { get; set; } = new HotkeyConfig { Modifiers = 0x0002 | 0x4000, Key = (uint)Keys.F10 };
        public HotkeyConfig     HotkeyRefreshApp   { get; set; } = new HotkeyConfig { Modifiers = 0x0002 | 0x4000, Key = (uint)Keys.F11 };

        [System.Text.Json.Serialization.JsonIgnore]
        public string SteamGridDbApiKey { get; set; } = string.Empty;

        public string SteamGridDbApiKeyProtected { get; set; } = string.Empty;

        public int  WindowWidth      { get; set; } = 1200;
        public int  WindowHeight     { get; set; } = 800;
        public int  WindowX          { get; set; } = int.MinValue;
        public int  WindowY          { get; set; } = int.MinValue;
        public bool WindowMaximized  { get; set; } = false;

        public HashSet<string> IgnoredProcesses { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool AlwaysRunAsAdmin { get; set; } = false;

        public bool VerboseLogging { get; set; } = false;
    }

    public enum MonitorAlignMode { Left, Center, Bottom, Right }

    public class MonitorItem
    {
        public string ID         { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public int  Width         { get; set; }
        public int  Height        { get; set; }
        public bool Primary       { get; set; }
        public override string ToString() => ID;
    }

    public class MonitorResolution
    {
        public int Width  { get; set; }
        public int Height { get; set; }
    }

    public readonly record struct OpenWindowEntry(string WindowTitle, string Exe, int Pid);

    public enum LogLevel { Info, Ok, Warn, Error }

    internal static class AppVersion
    {
        public static readonly string Display = "v" + (
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "0.0.0");
    }

    internal static class AppPaths
    {
        public static readonly string AppDataDir = CreateAndGet();

        private static string CreateAndGet()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoBorders");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    internal static class ExeBuildStamp
    {
        public static readonly string Value = Compute();

        private static string Compute()
        {
            string? exePath = Environment.ProcessPath;
            return exePath != null && File.Exists(exePath)
                ? File.GetLastWriteTimeUtc(exePath).Ticks.ToString()
                : "0";
        }
    }

    internal static class WebView2CacheGuard
    {
        public static void ClearIfStaleBuild()
        {
            string profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoBorders.WebView2", "EBWebView", "Default");
            if (!Directory.Exists(profileDir)) return;

            string markerFile = Path.Combine(AppPaths.AppDataDir, "webview2-cache.stamp");
            string exeStamp = ExeBuildStamp.Value;
            if (File.Exists(markerFile) && File.ReadAllText(markerFile) == exeStamp) return;

            foreach (string sub in new[] { "Cache", "Code Cache" })
            {
                string dir = Path.Combine(profileDir, sub);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { }
            }

            File.WriteAllText(markerFile, exeStamp);
            AppLogger.LogVerbose($"WebView2 cache cleared (new build detected, stamp={exeStamp}).");
        }
    }

    internal static class EmbeddedWwwroot
    {
        private const string ResourcePrefix = "EmbeddedWwwroot/";

        public static readonly string ExtractedDir = Extract();

        private static string Extract()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string targetDir = Path.Combine(AppPaths.AppDataDir, "wwwroot");
            string markerFile = Path.Combine(targetDir, ".extracted");
            string exeStamp = ExeBuildStamp.Value;

            if (File.Exists(markerFile) && File.ReadAllText(markerFile) == exeStamp) return targetDir;

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);

            foreach (string resourceName in asm.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

                string relativePath = resourceName.Substring(ResourcePrefix.Length)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                string destPath = Path.Combine(targetDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                using Stream? resourceStream = asm.GetManifestResourceStream(resourceName);
                if (resourceStream == null) continue;
                using var fileStream = File.Create(destPath);
                resourceStream.CopyTo(fileStream);
            }

            File.WriteAllText(markerFile, exeStamp);
            AppLogger.LogVerbose($"Embedded wwwroot extracted to {targetDir} (new build detected, stamp={exeStamp}).");
            return targetDir;
        }
    }

    internal static class AppLogger
    {
        private static readonly string _path = Path.Combine(
            AppPaths.AppDataDir, "noborders.log");

        public static string LogPath => _path;

        public static bool VerboseEnabled = false;

        public static int ErrorCountThisSession { get; private set; }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            if (level == LogLevel.Error) ErrorCountThisSession++;
            try { File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}\n"); }
            catch { }
        }

        public static void Log(Exception ex, string context)
            => Log($"ERROR in {context}: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);

        public static void LogVerbose(string message)
        {
            if (VerboseEnabled) Log(message, LogLevel.Info);
        }
    }

    internal sealed class NumericTextBox : TextBox
    {
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public decimal Minimum { get; set; } = 0;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public decimal Maximum { get; set; } = 100;

        private decimal _value;
        private bool    _updatingText;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public decimal Value
        {
            get => _value;
            set
            {
                decimal clamped = Math.Max(Minimum, Math.Min(Maximum, value));
                _value = clamped;
                _updatingText = true;
                Text = clamped.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _updatingText = false;
            }
        }

        public event EventHandler? ValueChanged;

        public NumericTextBox()
        {
            TextAlign = HorizontalAlignment.Right;
            Text = "0";
        }

        protected override void OnTextChanged(EventArgs e)
        {
            if (_updatingText) { base.OnTextChanged(e); return; }

            string filtered = FilterText(Text, Minimum < 0);
            if (filtered != Text)
            {
                int pos = SelectionStart;
                _updatingText = true;
                Text = filtered;
                SelectionStart = Math.Min(pos, filtered.Length);
                _updatingText = false;
            }

            if (decimal.TryParse(filtered, out decimal v))
                _value = v;

            base.OnTextChanged(e);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            if (!decimal.TryParse(Text, out decimal v))
                v = Minimum;
            Value = v;
        }

        private static string FilterText(string s, bool allowNegative)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            bool seenMinus = false;
            foreach (char c in s)
            {
                if (char.IsDigit(c)) { sb.Append(c); continue; }
                if (c == '-' && allowNegative && !seenMinus && sb.Length == 0)
                {
                    sb.Append(c);
                    seenMinus = true;
                }
            }
            return sb.ToString();
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxLength);
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static IntPtr FindRunningInstanceWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (!sb.ToString().StartsWith("NoBorders", StringComparison.Ordinal)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    if (!proc.ProcessName.Equals("NoBorders", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }

                found = hWnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static Mutex? AppMutex;

        [STAThread]
        private static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => AppLogger.Log(e.Exception, "Application.ThreadException");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                AppLogger.Log(
                    e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "(unknown)"),
                    $"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})");

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppMutex = new Mutex(true, "NoBordersManager_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                int wm = RegisterWindowMessage("WM_SHOWFIRSTINSTANCE_NOBORDERS");

                IntPtr runningWindow = FindRunningInstanceWindow();
                AppLogger.Log($"Second launch detected while already running — targeted window {(runningWindow == IntPtr.Zero ? "not found, broadcast-only" : runningWindow.ToString())}.");
                if (runningWindow != IntPtr.Zero)
                    PostMessage(runningWindow, wm, IntPtr.Zero, IntPtr.Zero);

                PostMessage((IntPtr)0xFFFF, wm, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "Main");
                MessageBox.Show(
                    $"Fatal error:\n{ex.Message}",
                    "NoBorders", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { AppMutex.ReleaseMutex(); } catch { }
                AppMutex.Dispose();
            }
        }
    }

    internal sealed class ArtworkAwareBlazorWebView : BlazorWebView
    {
        public override IFileProvider CreateFileProvider(string contentRootDir) =>
            new CompositeFileProvider(
                base.CreateFileProvider(contentRootDir),
                SafePhysicalFileProvider(contentRootDir),
                new PhysicalFileProvider(EmbeddedWwwroot.ExtractedDir),
                new PhysicalFileProvider(AppPaths.AppDataDir));

        private static IFileProvider SafePhysicalFileProvider(string root) =>
            Directory.Exists(root) ? new PhysicalFileProvider(root) : new NullFileProvider();
    }

    internal interface IMainFormBridge
    {
        AppSettings Settings { get; }
        GameConfig? SelectedGame { get; }
        List<OpenWindowEntry> GetOpenWindows();
        List<MonitorItem> Monitors { get; }
        string HotkeyAddStatusText { get; }
        string HotkeyRefreshStatusText { get; }
        void SelectGame(GameConfig game);
        List<string> MonitorOptions { get; }
        string ActiveMonitorScope { get; }
        void SelectMonitorScope(string scope);
        void ToggleGameActive();
        void ReapplyBorderless();
        void LoadMonitorDefaultsForSelectedGame();
        string PendingDisplayName { get; }
        void FetchNameForSelectedGame();
        string PendingRegexPattern { get; }
        void SetPendingRegexPattern(string pattern);
        MatchTargetMode PendingMatchTarget { get; }
        void SetPendingMatchTarget(MatchTargetMode mode);
        void SaveGameChanges();
        void RemoveSelectedGame();
        bool AddGameFromRunningWindow(OpenWindowEntry entry);
        bool BrowseForExe();
        string ActiveMonitorDefaultScope { get; }
        void SelectMonitorDefaultScope(string scope);
        void SaveMonitorDefault();
        void DeleteMonitorDefault(string scope);
        void DetectDisplays();
        void RemoveAllSavedMonitors();
        void ApplyMonitorDefaultToAllGames();
        bool IsCapturingHotkeyAdd { get; }
        bool IsCapturingHotkeyRefresh { get; }
        void ToggleHotkeyAddCapture();
        void ToggleHotkeyRefreshCapture();
        void ToggleMinimizeToTray();
        void ToggleStartWithWindows();
        void ToggleStartMinimized();
        bool CanUndo { get; }
        void UndoLastSave();
        bool IsGameRunning(GameConfig game);
        void QueueSave();
        void SaveConfig();
        void RestartAsAdmin();
        void RestartAsStandardUser();
        string ArtworkApiKey { get; }
        void SetSteamGridDbApiKey(string key);
        void ApplyArtwork(string heroPath, string iconPath);
        bool ConstrainMouse { get; }
        void ToggleConstrainMouse();
        bool ConstrainMouseDefault { get; }
        void ToggleConstrainMouseDefault();
        bool IsElevated { get; }
        void ConfirmRestartAsAdmin();
        void ConfirmRestartAsStandardUser();
        bool AlwaysRunAsAdmin { get; set; }
        bool VerboseLogging { get; set; }
        void DismissElevationDialog();
        bool ToastVisible { get; }
        string ToastTitle { get; }
        string ToastDetail { get; }
        string ToastIconPath { get; }
        LogLevel ToastLevel { get; }
        MonitorResolution? GetLastKnownMonitorResolution(string monitorId);
        List<string> GetIgnoredProcesses();
        void AddIgnoredProcess(string exeName);
        void RemoveIgnoredProcess(string exeName);
        void ConfirmClearIgnoredProcesses();
        void ConfirmFullReset();
        int Width { get; }
        void AdjustWidth(int delta);
        int Height { get; }
        void AdjustHeight(int delta);
        int OffsetX { get; }
        void AdjustOffsetX(int delta);
        int OffsetY { get; }
        void AdjustOffsetY(int delta);
        int MonitorDefaultWidth { get; }
        void AdjustMonitorDefaultWidth(int delta);
        int MonitorDefaultHeight { get; }
        void AdjustMonitorDefaultHeight(int delta);
        int MonitorDefaultOffsetX { get; }
        void AdjustMonitorDefaultOffsetX(int delta);
        int MonitorDefaultOffsetY { get; }
        void AdjustMonitorDefaultOffsetY(int delta);
        void AlignOnMonitor(MonitorAlignMode mode);
        void AlignOnMonitorDefault(MonitorAlignMode mode);
        void MinimizeWindow();
        void ToggleMaximizeWindow();
        void CloseWindow();
        bool IsWindowMaximized();
        void BeginWindowDrag();
    }

    internal sealed partial class MainForm : Form, IMainFormBridge
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering, targetAvailable, statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_2DREGION { public uint cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_2DREGION hSyncFreq, vSyncFreq, activeSize, totalSize;
            public uint videoStandard, scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_MODE { public uint width, height, pixelFormat; public int positionX, positionY; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO { public int positionX, positionY, refX, refY; }

        [StructLayout(LayoutKind.Explicit)]
        private struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE  targetMode;
            [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE  sourceMode;
            [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        { public uint infoType, id; public LUID adapterId; public DISPLAYCONFIG_MODE_INFO_UNION modeInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        { public uint type, size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS { public uint value; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS flags;
            public uint outputTechnology;
            public ushort edidManufactureId, edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        private const int    GWL_STYLE              = -16;
        private const uint   WS_CAPTION             = 0x00C00000;
        private const uint   WS_THICKFRAME          = 0x00040000;
        private const uint   WS_MAXIMIZE            = 0x01000000;
        private const uint   SWP_NOMOVE             = 0x0002;
        private const uint   SWP_NOSIZE             = 0x0001;
        private const uint   SWP_NOZORDER           = 0x0004;
        private const uint   SWP_NOACTIVATE         = 0x0010;
        private const uint   SWP_FRAMECHANGED       = 0x0020;
        private const int    SW_RESTORE             = 9;
        private const uint   QDC_ONLY_ACTIVE_PATHS  = 2;
        private const uint   DCDI_GET_SOURCE_NAME   = 1;
        private const uint   DCDI_GET_TARGET_NAME   = 2;
        private const int    WM_HOTKEY              = 0x0312;
        private const int    WM_POWERBROADCAST      = 0x0218;
        private const int    PBT_APMSUSPEND         = 0x0004;
        private const int    PBT_APMRESUMESUSPEND   = 0x0007;
        private const int    PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int    WM_DISPLAYCHANGE       = 0x007E;
        private const int    WM_SETTINGCHANGE       = 0x001A;
        private const int    HSHELL_WINDOWCREATED   = 1;
        private const int    WM_NCLBUTTONDOWN       = 0x00A1;
        private const int    HTCAPTION               = 2;
        private const int    HOTKEY_ID_ADD          = 1001;
        private const int    HOTKEY_ID_REFRESH      = 1002;
        private const uint   MOD_ALT                = 0x0001;
        private const uint   MOD_CONTROL            = 0x0002;
        private const uint   MOD_SHIFT              = 0x0004;
        private const uint   MOD_NOREPEAT           = 0x4000;
        private const string REG_RUN_KEY            = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME               = "NoBordersManager";

        [DllImport("user32.dll")] private static extern bool   RegisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern int    GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int    SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool   SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool   ShowWindow(IntPtr hWnd, int nCmd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool   SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool   GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool   IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool   ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxLength);
        [DllImport("user32.dll")] private static extern bool   ClipCursor(ref RECT r);
        [DllImport("user32.dll")] private static extern bool   ClipCursor(IntPtr zero);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool   RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool   UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern int    GetDisplayConfigBufferSizes(uint flags, out uint nPaths, out uint nModes);
        [DllImport("user32.dll")] private static extern int    QueryDisplayConfig(uint flags, ref uint nPaths,
            [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint nModes,
            [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME n);

        private AppSettings  _settings    = new AppSettings();
        private GameConfig?  _selectedGame;
        private bool         _forceClose  = false;
        private bool         _updatingUI  = false;
        private string       _activeScope = string.Empty;

        private GameConfig? _undoTarget;
        private string _undoGameName = string.Empty;
        private string _undoRegexPattern = string.Empty;
        private MatchTargetMode _undoMatchTarget;

        private MatchTargetMode _pendingMatchTarget;
        private Dictionary<string, GameDisplayProfile> _undoProfiles = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _configPath = Path.Combine(
            AppPaths.AppDataDir, "games_config.json");

        private static readonly HashSet<string> _systemProcessBlocklist =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe",
            "shellexperiencehost.exe",
            "startmenuexperiencehost.exe",
            "searchhost.exe",
            "searchapp.exe",
            "searchui.exe",
            "cortana.exe",
            "applicationframehost.exe",
            "lockapp.exe",
            "logonui.exe",
            "winlogon.exe",
            "userinit.exe",

            "textinputhost.exe",
            "ctfmon.exe",
            "tabtip.exe",
            "tabtip32.exe",
            "osk.exe",
            "magnify.exe",
            "narrator.exe",
            "utilman.exe",

            "svchost.exe",
            "dllhost.exe",
            "runtimebroker.exe",
            "taskhostw.exe",
            "sihost.exe",
            "sidebarhost.exe",
            "smartscreen.exe",
            "wuauclt.exe",
            "musnotification.exe",
            "musnotificationux.exe",
            "werfault.exe",
            "werfaultsecure.exe",
            "dwm.exe",
            "fontdrvhost.exe",
            "csrss.exe",
            "lsass.exe",
            "services.exe",
            "spoolsv.exe",

            "systemsettings.exe",
            "winstore.app.exe",
            "microsoftedge.exe",
            "msedge.exe",
            "microsoftedgecp.exe",
            "officecefloader.exe",
            "msteams.exe",
            "teams.exe",
            "onedrive.exe",
            "calculator.exe",
            "mspaint.exe",
            "snippingtool.exe",
            "stickynot.exe",
            "notepad.exe",

            "msseces.exe",
            "msascuil.exe",
            "securityhealthsystray.exe",
            "securityhealthservice.exe",
            "antimalwareservice.exe",

            "nvcontainer.exe",
            "nvdisplay.container.exe",
            "nvidia web helper.exe",
            "nvspcaps64.exe",
            "nvsphelper64.exe",
            "nvcplui.exe",
            "nvtray.exe",
            "amdrsserv.exe",
            "cccpushserver.exe",
            "radioshim.exe",
            "radeonsoftware.exe",
            "igcctray.exe",
            "intelcphdcpsvc.exe",
            "lghub.exe",
            "logioverlay.exe",
            "corsairservice.exe",
            "icue.exe",
            "razercentralservice.exe",
            "razercentral.exe",
            "steelseriesclientcore.exe",

            "steamservice.exe",
            "steam.exe",
            "steamwebhelper.exe",
            "gameoverlayui.exe",
            "epicgameslauncher.exe",
            "galaxyclient.exe",
            "galaxyclient helper.exe",
            "eadesktop.exe",
            "origin.exe",
            "ubisoft connect.exe",
            "ubisoftgamelauncher.exe",
            "battlenet.exe",
            "gogdl.exe",
            "bethesdanetlauncher.exe",
            "rockstarservice.exe",

            "easyanticheat.exe",
            "battleye.exe",
            "vgc.exe",
            "vgtray.exe",
            "faceitclient.exe",

            "rdpclip.exe",
            "termsrv.exe",
            "parsec.exe",
            "sunshine.exe",

            "obs64.exe",
            "obs32.exe",

            "noborders.exe",
        };

        private readonly Dictionary<string, Image>     _iconCache      = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MonitorItem>             _monitors       = new List<MonitorItem>();
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private bool   _wasHiddenBeforeSleep = false;
        private bool   _webViewNeedsRepaintAfterWake = false;
        private bool   _isElevated = false;
        private readonly HashSet<string> _elevationWarnedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<IntPtr, int>      _consecutiveEnforceChanges = new Dictionary<IntPtr, int>();
        private readonly Dictionary<IntPtr, DateTime> _enforceBackoffUntil       = new Dictionary<IntPtr, DateTime>();
        private readonly HashSet<string> _thrashWarnedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int      ThrashTickThreshold = 4;
        private static readonly TimeSpan ThrashBackoff = TimeSpan.FromSeconds(15);

        private int           _capturingHotkeyId      = 0;
        private HotkeyConfig? _capturingHotkeyConfig  = null;
        private TextBox?      _capturingHotkeyDisplay = null;
        private Button?       _capturingHotkeyButton  = null;
        private string        _capturingHotkeyLabel   = string.Empty;

        private readonly Dictionary<IntPtr, GameConfig> _trackedWindows = new Dictionary<IntPtr, GameConfig>();
        private readonly Dictionary<IntPtr, CancellationTokenSource> _pending = new Dictionary<IntPtr, CancellationTokenSource>();

        private HashSet<GameConfig> _runningGames = new HashSet<GameConfig>();

        private int _wmShellHook;
        private int _wmShowFirst;

        private readonly System.Windows.Forms.Timer _enforceTimer   = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _clipTimer      = new System.Windows.Forms.Timer { Interval = 100 };
        private readonly System.Windows.Forms.Timer _statusTimer    = new System.Windows.Forms.Timer { Interval = 2500 };
        private readonly System.Windows.Forms.Timer _saveDebounce   = new System.Windows.Forms.Timer { Interval = 600 };

        private readonly NotifyIcon       _trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();

        private readonly TabControl      _tabs            = new TabControl();
        private readonly ListBox         _lstGames        = new ListBox();
        private readonly Button          _btnAddRunning   = new Button();
        private readonly Button          _btnAddBrowse    = new Button();
        private readonly Panel           _pnlDetail       = new Panel();
        private readonly Label           _lblDetailHint   = new Label();
        private readonly Label           _lblGameTitle    = new Label();
        private readonly CheckBox        _chkActive       = new CheckBox();
        private readonly ComboBox        _cmbMonitor      = new ComboBox();
        private readonly NumericTextBox  _numWidth        = new NumericTextBox();
        private readonly NumericTextBox  _numHeight       = new NumericTextBox();
        private readonly NumericTextBox  _numOffsetX      = new NumericTextBox();
        private readonly NumericTextBox  _numOffsetY      = new NumericTextBox();
        private readonly CheckBox        _chkConstrain    = new CheckBox();
        private readonly Button          _btnLoadDefaults = new Button();
        private readonly Button          _btnSaveGame     = new Button();
        private readonly Button          _btnDeleteGame   = new Button();
        private readonly Panel           _grpAdvanced     = new Panel();
        private readonly Label           _lblAdvancedHdr  = new Label();
        private readonly Button          _btnSaveAdvanced = new Button();
        private readonly TextBox         _txtGameName     = new TextBox();
        private readonly Button          _btnFetchName    = new Button();
        private readonly TextBox         _txtRegex        = new TextBox();
        private readonly Label           _lblStatus       = new Label();

        private readonly ComboBox        _cmbSetMonitor   = new ComboBox();
        private readonly NumericTextBox  _numSetWidth     = new NumericTextBox();
        private readonly NumericTextBox  _numSetHeight    = new NumericTextBox();
        private readonly NumericTextBox  _numSetOffsetX   = new NumericTextBox();
        private readonly NumericTextBox  _numSetOffsetY   = new NumericTextBox();
        private readonly CheckBox        _chkSetConstrain = new CheckBox();
        private readonly Button          _btnSaveDefault  = new Button();
        private readonly Button          _btnDeleteMonitor = new Button();
        private readonly TextBox         _txtHotkeyAdd    = new TextBox();
        private readonly TextBox         _txtHotkeyRefresh = new TextBox();
        private readonly Button          _btnSetHotkeyAdd     = new Button();
        private readonly Button          _btnSetHotkeyRefresh = new Button();
        private readonly Label           _lblHotkeyAddStatus     = new Label();
        private readonly Label           _lblHotkeyRefreshStatus = new Label();
        private readonly CheckBox        _chkMinToTray    = new CheckBox();
        private readonly CheckBox        _chkStartWindows = new CheckBox();
        private readonly CheckBox        _chkStartMin     = new CheckBox();
        private readonly Label           _lblElevationStatus = new Label();
        private readonly Button          _btnRestartAdmin = new Button();

        private readonly ServiceProvider _blazorServices;

        private BlazorWebView _blazorWebView = null!;

        private AppStateService _appState = null!;

        private ServiceProvider CreateBlazorServices()
        {
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            services.AddSingleton(_ => new Services.LogTailService(AppLogger.LogPath));
            services.AddSingleton(_ => new Services.ArtworkService(
                AppPaths.AppDataDir,
                () => _settings.SteamGridDbApiKey));
            services.AddSingleton(_ => new AppStateService(this));
            return services.BuildServiceProvider();
        }

        AppSettings IMainFormBridge.Settings => _settings;
        GameConfig? IMainFormBridge.SelectedGame => _selectedGame;
        List<OpenWindowEntry> IMainFormBridge.GetOpenWindows() => GetOpenWindowEntries();
        List<MonitorItem> IMainFormBridge.Monitors => _monitors;
        string IMainFormBridge.HotkeyAddStatusText => _lblHotkeyAddStatus.Text;
        string IMainFormBridge.HotkeyRefreshStatusText => _lblHotkeyRefreshStatus.Text;
        void IMainFormBridge.SelectGame(GameConfig game) => SelectGame(game);
        List<string> IMainFormBridge.MonitorOptions => _cmbMonitor.Items.Cast<string>().ToList();
        string IMainFormBridge.ActiveMonitorScope => _activeScope;
        void IMainFormBridge.SelectMonitorScope(string scope) => SelectMonitorScope(scope);
        void IMainFormBridge.ToggleGameActive() => ToggleGameActive();
        void IMainFormBridge.ReapplyBorderless() => ReapplyBorderlessForSelectedGame();
        void IMainFormBridge.LoadMonitorDefaultsForSelectedGame() => LoadMonitorDefaultsForSelectedGame();
        string IMainFormBridge.PendingDisplayName => _txtGameName.Text;
        void IMainFormBridge.FetchNameForSelectedGame() => FetchNameForSelectedGame();
        string IMainFormBridge.PendingRegexPattern => _txtRegex.Text;
        void IMainFormBridge.SetPendingRegexPattern(string pattern) => SetPendingRegexPattern(pattern);
        MatchTargetMode IMainFormBridge.PendingMatchTarget => _pendingMatchTarget;
        void IMainFormBridge.SetPendingMatchTarget(MatchTargetMode mode) => SetPendingMatchTarget(mode);
        void IMainFormBridge.SaveGameChanges() => SaveGameChanges();
        void IMainFormBridge.RemoveSelectedGame() => RemoveSelectedGame();
        bool IMainFormBridge.AddGameFromRunningWindow(OpenWindowEntry entry) => AddGameFromRunningWindow(entry);
        bool IMainFormBridge.BrowseForExe() => BrowseForExe();
        string IMainFormBridge.ActiveMonitorDefaultScope => _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
        void IMainFormBridge.SelectMonitorDefaultScope(string scope) => SelectMonitorDefaultScope(scope);
        void IMainFormBridge.SaveMonitorDefault() => SaveMonitorDefault();
        void IMainFormBridge.DeleteMonitorDefault(string scope) => DeleteMonitorDefault(scope);
        void IMainFormBridge.DetectDisplays() => DetectDisplays();
        void IMainFormBridge.RemoveAllSavedMonitors() => RemoveAllSavedMonitors();
        void IMainFormBridge.ApplyMonitorDefaultToAllGames() => ApplyMonitorDefaultToAllGames();
        bool IMainFormBridge.IsCapturingHotkeyAdd => _capturingHotkeyId == HOTKEY_ID_ADD;
        bool IMainFormBridge.IsCapturingHotkeyRefresh => _capturingHotkeyId == HOTKEY_ID_REFRESH;
        void IMainFormBridge.ToggleHotkeyAddCapture() => ToggleHotkeyAddCapture();
        void IMainFormBridge.ToggleHotkeyRefreshCapture() => ToggleHotkeyRefreshCapture();
        void IMainFormBridge.ToggleMinimizeToTray() => ToggleMinimizeToTray();
        void IMainFormBridge.ToggleStartWithWindows() => ToggleStartWithWindows();
        void IMainFormBridge.ToggleStartMinimized() => ToggleStartMinimized();
        bool IMainFormBridge.CanUndo => _undoTarget != null && _undoTarget == _selectedGame;
        void IMainFormBridge.UndoLastSave() => UndoLastSave();
        bool IMainFormBridge.IsGameRunning(GameConfig game) => _runningGames.Contains(game);
        void IMainFormBridge.QueueSave() => QueueSave();
        void IMainFormBridge.SaveConfig() => SaveConfig();
        void IMainFormBridge.RestartAsAdmin() => RestartAsAdmin();
        void IMainFormBridge.RestartAsStandardUser() => RestartAsStandardUser();
        string IMainFormBridge.ArtworkApiKey => _settings.SteamGridDbApiKey;
        void IMainFormBridge.SetSteamGridDbApiKey(string key) => SetSteamGridDbApiKey(key);
        void IMainFormBridge.ApplyArtwork(string heroPath, string iconPath) => ApplyArtwork(heroPath, iconPath);
        bool IMainFormBridge.ConstrainMouse => _chkConstrain.Checked;
        void IMainFormBridge.ToggleConstrainMouse() => ToggleConstrainMouse();
        bool IMainFormBridge.ConstrainMouseDefault => _chkSetConstrain.Checked;
        void IMainFormBridge.ToggleConstrainMouseDefault() => ToggleConstrainMouseDefault();
        bool IMainFormBridge.IsElevated => _isElevated;
        void IMainFormBridge.ConfirmRestartAsAdmin() => ConfirmRestartAsAdmin();
        void IMainFormBridge.ConfirmRestartAsStandardUser() => ConfirmRestartAsStandardUser();
        bool IMainFormBridge.AlwaysRunAsAdmin
        {
            get => _settings.AlwaysRunAsAdmin;
            set { _settings.AlwaysRunAsAdmin = value; QueueSave(); _appState.RaiseChanged(); }
        }
        bool IMainFormBridge.VerboseLogging
        {
            get => _settings.VerboseLogging;
            set { _settings.VerboseLogging = value; AppLogger.VerboseEnabled = value; QueueSave(); _appState.RaiseChanged(); }
        }
        void IMainFormBridge.DismissElevationDialog() => CloseElevationDialog();
        bool IMainFormBridge.ToastVisible => _toastVisible;
        string IMainFormBridge.ToastTitle => _toastTitle;
        string IMainFormBridge.ToastDetail => _toastDetail;
        string IMainFormBridge.ToastIconPath => _toastIconPath;
        LogLevel IMainFormBridge.ToastLevel => _toastLevel;
        MonitorResolution? IMainFormBridge.GetLastKnownMonitorResolution(string monitorId) =>
            _settings.LastKnownMonitorResolutions.TryGetValue(monitorId, out var res) ? res : null;
        List<string> IMainFormBridge.GetIgnoredProcesses() => GetIgnoredProcesses();
        void IMainFormBridge.AddIgnoredProcess(string exeName) => AddIgnoredProcess(exeName);
        void IMainFormBridge.RemoveIgnoredProcess(string exeName) => RemoveIgnoredProcess(exeName);
        void IMainFormBridge.ConfirmClearIgnoredProcesses() => ConfirmClearIgnoredProcesses();
        void IMainFormBridge.ConfirmFullReset() => ConfirmFullReset();
        int IMainFormBridge.Width => (int)_numWidth.Value;
        void IMainFormBridge.AdjustWidth(int delta) => AdjustWidth(delta);
        int IMainFormBridge.Height => (int)_numHeight.Value;
        void IMainFormBridge.AdjustHeight(int delta) => AdjustHeight(delta);
        int IMainFormBridge.OffsetX => (int)_numOffsetX.Value;
        void IMainFormBridge.AdjustOffsetX(int delta) => AdjustOffsetX(delta);
        int IMainFormBridge.OffsetY => (int)_numOffsetY.Value;
        void IMainFormBridge.AdjustOffsetY(int delta) => AdjustOffsetY(delta);
        int IMainFormBridge.MonitorDefaultWidth => (int)_numSetWidth.Value;
        void IMainFormBridge.AdjustMonitorDefaultWidth(int delta) => AdjustMonitorDefaultWidth(delta);
        int IMainFormBridge.MonitorDefaultHeight => (int)_numSetHeight.Value;
        void IMainFormBridge.AdjustMonitorDefaultHeight(int delta) => AdjustMonitorDefaultHeight(delta);
        int IMainFormBridge.MonitorDefaultOffsetX => (int)_numSetOffsetX.Value;
        void IMainFormBridge.AdjustMonitorDefaultOffsetX(int delta) => AdjustMonitorDefaultOffsetX(delta);
        int IMainFormBridge.MonitorDefaultOffsetY => (int)_numSetOffsetY.Value;
        void IMainFormBridge.AdjustMonitorDefaultOffsetY(int delta) => AdjustMonitorDefaultOffsetY(delta);
        void IMainFormBridge.AlignOnMonitor(MonitorAlignMode mode) => AlignOnMonitor(mode);
        void IMainFormBridge.AlignOnMonitorDefault(MonitorAlignMode mode) => AlignOnMonitorDefault(mode);
        void IMainFormBridge.MinimizeWindow() => MinimizeWindow();
        void IMainFormBridge.ToggleMaximizeWindow() => ToggleMaximizeWindow();
        void IMainFormBridge.CloseWindow() => CloseWindow();
        bool IMainFormBridge.IsWindowMaximized() => IsWindowMaximized();
        void IMainFormBridge.BeginWindowDrag() => BeginWindowDrag();

        public MainForm()
        {
            CheckElevation();
            LoadConfig();

            WebView2CacheGuard.ClearIfStaleBuild();

            BuildUI();
            SetupTrayIcon();
            RefreshMonitors();
            PopulateGamesList();

            if (_settings.Games.Count > 0) _lstGames.SelectedIndex = 0;

            ApplyTheme();

            _enforceTimer.Tick  += EnforceTimer_Tick;
            _clipTimer.Tick     += ClipTimer_Tick;
            _statusTimer.Tick   += (s, e) => { _statusTimer.Stop(); _lblStatus.Text = string.Empty; };
            _saveDebounce.Tick  += (s, e) => { _saveDebounce.Stop(); SaveConfig(); };

            this.Resize      += OnResize;
            this.FormClosing += OnFormClosing;
            this.Load        += OnLoad;

            _blazorServices = CreateBlazorServices();
            _appState       = _blazorServices.GetRequiredService<AppStateService>();

            _lstGames.SelectedIndexChanged    += (s, e) => _appState.RaiseChanged();
            _cmbMonitor.SelectedIndexChanged  += (s, e) => _appState.RaiseChanged();
            _chkActive.CheckedChanged         += (s, e) => _appState.RaiseChanged();
            _btnLoadDefaults.Click            += (s, e) => _appState.RaiseChanged();
            _btnFetchName.Click               += (s, e) => _appState.RaiseChanged();
            _btnSaveGame.Click                += (s, e) => _appState.RaiseChanged();
            _btnDeleteGame.Click              += (s, e) => _appState.RaiseChanged();
            _btnAddBrowse.Click               += (s, e) => _appState.RaiseChanged();
            _cmbSetMonitor.SelectedIndexChanged += (s, e) => _appState.RaiseChanged();
            _btnSaveDefault.Click             += (s, e) => _appState.RaiseChanged();
            _btnDeleteMonitor.Click           += (s, e) => _appState.RaiseChanged();
            _chkMinToTray.CheckedChanged      += (s, e) => _appState.RaiseChanged();
            _chkStartWindows.CheckedChanged   += (s, e) => _appState.RaiseChanged();
            _chkStartMin.CheckedChanged       += (s, e) => _appState.RaiseChanged();
            _chkConstrain.CheckedChanged      += (s, e) => _appState.RaiseChanged();
            _chkSetConstrain.CheckedChanged   += (s, e) => _appState.RaiseChanged();

            _pnlNav.Visible = false;
            _lblStatus.Visible = false;

            _blazorWebView = new ArtworkAwareBlazorWebView
            {
                HostPage = "wwwroot\\index.html",
                Dock     = DockStyle.Fill,
                Services = _blazorServices
            };
            _blazorWebView.RootComponents.Add<Components.Screens.AppShell>("#app");
            this.Controls.Add(_blazorWebView);
            _blazorWebView.BringToFront();
        }

        private void OnLoad(object? sender, EventArgs e)
        {
            try
            {
                _wmShellHook = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(this.Handle);
                _wmShowFirst = RegisterWindowMessage("WM_SHOWFIRSTINSTANCE_NOBORDERS");
                AppLogger.LogVerbose($"Shell hook registered (msg={_wmShellHook}, hwnd={this.Handle}).");
            }
            catch (Exception ex) { AppLogger.Log(ex, "RegisterShellHookWindow"); }

            RegisterHotkeys();

            ApplyTitleBarTheme();

            _enforceTimer.Start();
            _clipTimer.Start();

            if (_settings.AlwaysRunAsAdmin && !_isElevated)
            {
                RestartAsAdmin();
                return;
            }

            ScanExistingWindows();

            if (Environment.GetCommandLineArgs().Contains("-minimized", StringComparer.OrdinalIgnoreCase))
            {
                HideForMinimizedStartup();
            }
        }

        private void HideForMinimizedStartup()
        {
            void DoHide()
            {
                this.WindowState   = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                this.Hide();
            }

            var webView = _blazorWebView.WebView;
            if (webView.CoreWebView2 != null)
            {
                DoHide();
                return;
            }

            void OnInitialized(object? s, object e)
            {
                webView.CoreWebView2InitializationCompleted -= OnInitialized;
                DoHide();
            }
            webView.CoreWebView2InitializationCompleted += OnInitialized;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var img in _iconCache.Values) img.Dispose();
                foreach (var cts in _pending.Values)  cts.Cancel();
                _enforceTimer.Dispose();
                _clipTimer.Dispose();
                _statusTimer.Dispose();
                _saveDebounce.Dispose();
                _trayIcon.Dispose();
                _trayMenu.Dispose();
                _blazorServices.Dispose();
            }
            base.Dispose(disposing);
        }

        private readonly Panel  _pnlNav        = new Panel();
        private readonly Button _btnNavGames   = new Button();
        private readonly Button _btnNavSettings = new Button();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private void CheckElevation()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "CheckElevation");
                _isElevated = false;
            }
        }

        private void ConfirmRestartAsAdmin() => ShowElevationDialog();

        private void ConfirmRestartAsStandardUser() => ShowElevationDialog(toStandardUser: true);

        private void RestartAsAdmin()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                AppLogger.Log($"RestartAsAdmin: Application.ExecutablePath = '{exePath}'");

                string fileName = Path.GetFileNameWithoutExtension(exePath);
                if (!File.Exists(exePath) ||
                    fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"RestartAsAdmin: resolved path looks wrong (fileName='{fileName}', exists={File.Exists(exePath)}). Aborting restart.");
                    MessageBox.Show(
                        "NoBorders couldn't determine the correct file to relaunch " +
                        $"(resolved path: \"{exePath}\").\n\n" +
                        "As a workaround, close NoBorders, then right-click NoBorders.exe " +
                        "in File Explorer and choose \"Run as administrator\".",
                        "Restart Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute  = true,
                    Verb             = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                    psi.ArgumentList.Add(a);
                AppLogger.Log($"RestartAsAdmin: relaunch args = [{string.Join(", ", psi.ArgumentList)}]");

                _saveDebounce.Stop();
                CaptureWindowBounds();
                SaveConfig();
                AppLogger.Log("RestartAsAdmin: config flushed to disk before restart.");

                try
                {
                    Program.AppMutex?.ReleaseMutex();
                    Program.AppMutex?.Dispose();
                    AppLogger.Log("RestartAsAdmin: single-instance mutex released and disposed.");
                }
                catch (Exception relEx)
                {
                    AppLogger.Log(relEx, "RestartAsAdmin: mutex release (may already be released, non-fatal)");
                }

                Process? proc = Process.Start(psi);

                if (proc == null)
                {
                    AppLogger.Log("RestartAsAdmin: Process.Start returned null — the elevated instance did not launch. Keeping this instance open.");
                    ShowStatus("Restart may have failed — check noborders.log for details.");
                    return;
                }

                AppLogger.Log($"RestartAsAdmin: elevated process launched successfully, PID={proc.Id}. Closing this instance.");

                _settings.AlwaysRunAsAdmin = true;
                SaveConfig();

                _forceClose = true;
                Application.Exit();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                AppLogger.Log("RestartAsAdmin: user declined the UAC prompt (ERROR_CANCELLED).");
                ShowStatus("Elevation cancelled.");
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "RestartAsAdmin");
                ShowStatus("Couldn't restart as Administrator — see noborders.log for details.");
            }
        }

        private void RestartAsStandardUser()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                AppLogger.Log($"RestartAsStandardUser: Application.ExecutablePath = '{exePath}'");

                string fileName = Path.GetFileNameWithoutExtension(exePath);
                if (!File.Exists(exePath) ||
                    fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"RestartAsStandardUser: resolved path looks wrong (fileName='{fileName}', exists={File.Exists(exePath)}). Aborting restart.");
                    MessageBox.Show(
                        "NoBorders couldn't determine the correct file to relaunch " +
                        $"(resolved path: \"{exePath}\").\n\n" +
                        "As a workaround, close NoBorders, then start NoBorders.exe normally from File Explorer (without \"Run as administrator\").",
                        "Restart Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var psi = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute  = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                var relaunchArgs = Environment.GetCommandLineArgs().Skip(1)
                    .Select(a => "\"" + a.Replace("\"", "\\\"") + "\"");
                psi.Arguments = string.Join(" ", new[] { "\"" + exePath + "\"" }.Concat(relaunchArgs));
                AppLogger.Log($"RestartAsStandardUser: relaunch via explorer.exe, args = '{psi.Arguments}'");

                _saveDebounce.Stop();
                CaptureWindowBounds();
                SaveConfig();
                AppLogger.Log("RestartAsStandardUser: config flushed to disk before restart.");

                try
                {
                    Program.AppMutex?.ReleaseMutex();
                    Program.AppMutex?.Dispose();
                    AppLogger.Log("RestartAsStandardUser: single-instance mutex released and disposed.");
                }
                catch (Exception relEx)
                {
                    AppLogger.Log(relEx, "RestartAsStandardUser: mutex release (may already be released, non-fatal)");
                }

                Process? proc = Process.Start(psi);

                AppLogger.Log(proc == null
                    ? "RestartAsStandardUser: Process.Start returned null (expected for an explorer.exe handoff — not necessarily a failure)."
                    : $"RestartAsStandardUser: explorer.exe handoff process PID={proc.Id}.");

                _settings.AlwaysRunAsAdmin = false;
                SaveConfig();

                _forceClose = true;
                Application.Exit();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "RestartAsStandardUser");
                ShowStatus("Couldn't restart without Administrator rights — see noborders.log for details.");
            }
        }

        private void PromptRestartAsAdmin(string gameName) => ShowElevationDialog(gameName);

        private void ApplyTitleBarTheme()
        {
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(this.Handle,
                    DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch { }
        }

        private void BuildUI()
        {
            this.Text            = _isElevated ? "NoBorders (Administrator)" : "NoBorders";
            var designMinimum = new Size(1150, 720);
            bool hasSavedPosition = _settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue;
            var savedLocation = new Point(_settings.WindowX, _settings.WindowY);
            var targetScreen = hasSavedPosition
                ? Screen.FromPoint(savedLocation)
                : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
            var workArea = targetScreen.WorkingArea;

            this.MinimumSize     = new Size(
                Math.Min(designMinimum.Width,  workArea.Width),
                Math.Min(designMinimum.Height, workArea.Height));
            this.MaximumSize     = Size.Empty;

            var savedSize = new Size(
                Math.Min(Math.Max(_settings.WindowWidth,  this.MinimumSize.Width), workArea.Width),
                Math.Min(Math.Max(_settings.WindowHeight, this.MinimumSize.Height), workArea.Height));
            bool savedPositionOnScreen = hasSavedPosition &&
                Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(savedLocation, savedSize)));

            this.Size = savedSize;
            if (savedPositionOnScreen)
            {
                this.StartPosition = FormStartPosition.Manual;
                var maxX = workArea.Right  - savedSize.Width;
                var maxY = workArea.Bottom - savedSize.Height;
                this.Location = new Point(
                    Math.Max(workArea.Left, Math.Min(savedLocation.X, maxX)),
                    Math.Max(workArea.Top,  Math.Min(savedLocation.Y, maxY)));
            }
            else
            {
                this.StartPosition = FormStartPosition.CenterScreen;
            }

            this.Font            = new Font("Segoe UI", 9f);
            this.Icon            = TryExtractIcon(Application.ExecutablePath);

            if (_settings.WindowMaximized) this.WindowState = FormWindowState.Maximized;

            _lblStatus.Dock      = DockStyle.Bottom;
            _lblStatus.Height    = 24;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding   = new Padding(10, 0, 0, 0);
            _lblStatus.Font      = new Font("Segoe UI", 8f);

            _pnlNav.Dock   = DockStyle.Top;
            _pnlNav.Height = 42;
            _pnlNav.Padding = new Padding(10, 8, 0, 0);

            ConfigureNavButton(_btnNavGames,    "Games",    0);
            ConfigureNavButton(_btnNavSettings, "Settings", 1);
            _btnNavGames.Location    = new Point(10, 8);
            _btnNavSettings.Location = new Point(104, 8);
            _pnlNav.Controls.Add(_btnNavGames);
            _pnlNav.Controls.Add(_btnNavSettings);

            _tabs.Dock       = DockStyle.Fill;
            _tabs.Appearance = TabAppearance.FlatButtons;
            _tabs.ItemSize   = new Size(0, 1);
            _tabs.SizeMode   = TabSizeMode.Fixed;

            var tabGames    = new TabPage("Games");
            var tabSettings = new TabPage("Settings");
            _tabs.TabPages.Add(tabGames);
            _tabs.TabPages.Add(tabSettings);
            _tabs.SelectedIndexChanged += (s, e) => UpdateNavButtons();

            BuildGamesTab(tabGames);
            BuildSettingsTab(tabSettings);

            this.Controls.Add(_tabs);
            this.Controls.Add(_pnlNav);
            this.Controls.Add(_lblStatus);
        }

        private void ConfigureNavButton(Button btn, string text, int tabIndex)
        {
            btn.Text      = text;
            btn.Size      = new Size(88, 30);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font      = new Font("Segoe UI", 9f);
            btn.FlatAppearance.BorderSize = 0;
            btn.Cursor    = Cursors.Hand;
            btn.Click    += (s, e) => { _tabs.SelectedIndex = tabIndex; };
        }

        private void UpdateNavButtons()
        {
            bool gamesSel = _tabs.SelectedIndex == 0;

            StyleNavButton(_btnNavGames,    gamesSel);
            StyleNavButton(_btnNavSettings, !gamesSel);
        }

        private void StyleNavButton(Button btn, bool selected)
        {
            btn.BackColor = selected ? PaletteSurface : PaletteBase;
            btn.ForeColor = selected ? PaletteTextPrimary : PaletteTextSecondary;
            btn.Font      = new Font("Segoe UI", 9f, selected ? FontStyle.Bold : FontStyle.Regular);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 50);
            btn.FlatAppearance.MouseDownBackColor = btn.BackColor;
        }

        private void BuildGamesTab(TabPage page)
        {
            var pnlLeft = new Panel
            {
                Dock    = DockStyle.Left,
                Width   = 220,
                Padding = new Padding(8, 8, 8, 6)
            };
            pnlLeft.Paint += (s, e) =>
            {
                using var pen = new Pen(PaletteBorder, 1);
                e.Graphics.DrawLine(pen,
                    pnlLeft.Width - 1, 0,
                    pnlLeft.Width - 1, pnlLeft.Height);
            };

            _lstGames.Dock          = DockStyle.Fill;
            _lstGames.DrawMode      = DrawMode.OwnerDrawFixed;
            _lstGames.ItemHeight    = 28;
            _lstGames.BorderStyle   = BorderStyle.None;
            _lstGames.DrawItem     += LstGames_DrawItem;
            _lstGames.SelectedIndexChanged += LstGames_SelectedIndexChanged;

            var pnlAddButtons = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(0, 4, 0, 0) };

            _btnAddRunning.Text   = "＋  Add Running App";
            _btnAddRunning.Dock   = DockStyle.Top;
            _btnAddRunning.Height = 30;
            _btnAddRunning.Click += BtnAddRunning_Click;

            _btnAddBrowse.Text    = "＋  Browse for EXE…";
            _btnAddBrowse.Dock    = DockStyle.Bottom;
            _btnAddBrowse.Height  = 30;
            _btnAddBrowse.Click  += BtnAddBrowse_Click;

            pnlAddButtons.Controls.Add(_btnAddRunning);
            pnlAddButtons.Controls.Add(_btnAddBrowse);
            pnlLeft.Controls.Add(_lstGames);
            pnlLeft.Controls.Add(pnlAddButtons);

            _pnlDetail.Dock    = DockStyle.Fill;
            _pnlDetail.Padding = new Padding(20, 14, 20, 10);

            _lblDetailHint.Text      = "Select a game from the list, or add one with the buttons on the left.";
            _lblDetailHint.AutoSize  = false;
            _lblDetailHint.Dock      = DockStyle.Fill;
            _lblDetailHint.TextAlign = ContentAlignment.MiddleCenter;
            _lblDetailHint.Font      = new Font("Segoe UI", 10f);
            _pnlDetail.Controls.Add(_lblDetailHint);

            BuildDetailControls();

            page.Controls.Add(_pnlDetail);
            page.Controls.Add(pnlLeft);
        }

        private void BuildDetailControls()
        {
            var tbl = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                Padding     = new Padding(0),
                AutoSize    = false
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _lblGameTitle.AutoSize  = false;
            _lblGameTitle.Width     = 280;
            _lblGameTitle.Height    = 34;
            _lblGameTitle.Font      = new Font("Segoe UI", 13f, FontStyle.Bold);
            _lblGameTitle.TextAlign = ContentAlignment.MiddleLeft;

            _chkActive.Text        = "Borderless OFF";
            _chkActive.AutoSize    = false;
            _chkActive.Size        = new Size(138, 30);
            _chkActive.Font        = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _chkActive.Appearance  = Appearance.Button;
            _chkActive.TextAlign   = ContentAlignment.MiddleCenter;
            _chkActive.FlatStyle   = FlatStyle.Flat;
            _chkActive.CheckedChanged += ChkActive_CheckedChanged;
            UpdateActiveButton();

            var pnlTitle = new FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 0, 0, 10)
            };
            pnlTitle.Controls.Add(_lblGameTitle);
            pnlTitle.Controls.Add(_chkActive);
            tbl.Controls.Add(pnlTitle);

            _cmbMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMonitor.Width         = 270;
            _cmbMonitor.Height        = 26;
            _cmbMonitor.SelectedIndexChanged += CmbMonitor_SelectedIndexChanged;
            tbl.Controls.Add(MakeDetailRow("Monitor", _cmbMonitor));

            SetupNumeric(_numWidth,  0, 16384); _numWidth.Width  = 108;
            SetupNumeric(_numHeight, 0, 16384); _numHeight.Width = 108;
            tbl.Controls.Add(MakeDetailRow("Width / Height",
                _numWidth,
                MakeSeparatorLabel("×"),
                _numHeight));

            SetupNumeric(_numOffsetX, -16384, 16384); _numOffsetX.Width = 108;
            SetupNumeric(_numOffsetY, -16384, 16384); _numOffsetY.Width = 108;
            tbl.Controls.Add(MakeDetailRow("Offset X / Y",
                _numOffsetX,
                MakeSeparatorLabel("×"),
                _numOffsetY));

            _chkConstrain.Text     = "Lock mouse cursor to window bounds";
            _chkConstrain.AutoSize = true;
            _chkConstrain.CheckedChanged += ChkConstrain_CheckedChanged;
            tbl.Controls.Add(MakeDetailRow(string.Empty, _chkConstrain));

            var btnSize = new Size(158, 30);

            _btnLoadDefaults.Text    = "Load Monitor Defaults";
            _btnLoadDefaults.Size    = btnSize;
            _btnLoadDefaults.Margin  = new Padding(0, 0, 8, 0);
            _btnLoadDefaults.Click  += BtnLoadDefaults_Click;

            _btnSaveGame.Text    = "Save Changes";
            _btnSaveGame.Size    = btnSize;
            _btnSaveGame.Margin  = new Padding(0, 0, 8, 0);
            _btnSaveGame.Click  += BtnSaveGame_Click;

            _btnDeleteGame.Text   = "Remove Game";
            _btnDeleteGame.Size   = btnSize;
            _btnDeleteGame.Margin = Padding.Empty;
            _btnDeleteGame.Click += BtnDeleteGame_Click;

            var pnlButtons = new FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 8, 0, 8)
            };
            pnlButtons.Controls.Add(_btnLoadDefaults);
            pnlButtons.Controls.Add(_btnSaveGame);
            pnlButtons.Controls.Add(_btnDeleteGame);
            tbl.Controls.Add(pnlButtons);

            _lblAdvancedHdr.Text      = "▶  Advanced";
            _lblAdvancedHdr.AutoSize  = false;
            _lblAdvancedHdr.Height    = 28;
            _lblAdvancedHdr.Dock      = DockStyle.Top;
            _lblAdvancedHdr.TextAlign = ContentAlignment.MiddleLeft;
            _lblAdvancedHdr.Cursor    = Cursors.Hand;
            _lblAdvancedHdr.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblAdvancedHdr.Padding   = new Padding(8, 0, 0, 0);
            _lblAdvancedHdr.Click    += (s, e) => ToggleAdvanced();

            var pnlAdvContent = new Panel
            {
                Dock    = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 6)
            };

            _txtGameName.Width = 220;
            _txtRegex.Width    = 310;

            _btnFetchName.Text    = "↺";
            _btnFetchName.Size    = new Size(30, 24);
            _btnFetchName.Margin  = new Padding(4, 0, 0, 0);
            _btnFetchName.Font    = new Font("Segoe UI", 11f);
            _btnFetchName.Click  += BtnFetchName_Click;
            var tip = new ToolTip();
            tip.SetToolTip(_btnFetchName, "Fetch name from the running game window");

            _btnSaveAdvanced.Text   = "Save Changes";
            _btnSaveAdvanced.Size   = btnSize;
            _btnSaveAdvanced.Margin = new Padding(0, 4, 0, 0);
            _btnSaveAdvanced.Click += BtnSaveGame_Click;

            var tblAdv = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize    = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock        = DockStyle.Top,
                Padding     = Padding.Empty
            };
            tblAdv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tblAdv.Controls.Add(MakeDetailRow("Display Name", _txtGameName, _btnFetchName));
            tblAdv.Controls.Add(MakeDetailRow("Match Regex",  _txtRegex));
            tblAdv.Controls.Add(MakeDetailRow(string.Empty,   _btnSaveAdvanced));

            pnlAdvContent.Controls.Add(tblAdv);

            _grpAdvanced.AutoSize  = false;
            _grpAdvanced.Width     = 500;
            _grpAdvanced.Height    = 30;
            _grpAdvanced.Margin    = new Padding(0, 4, 0, 4);
            _grpAdvanced.Padding   = new Padding(1);
            _grpAdvanced.Cursor    = Cursors.Hand;
            _grpAdvanced.Click    += (s, e) => ToggleAdvanced();
            _grpAdvanced.Paint    += GrpAdvanced_Paint;
            _grpAdvanced.Controls.Add(pnlAdvContent);
            _grpAdvanced.Controls.Add(_lblAdvancedHdr);

            _grpAdvanced.Tag = pnlAdvContent;

            tbl.Controls.Add(_grpAdvanced);

            _pnlDetail.Controls.Add(tbl);
            SetDetailVisible(false);
        }

        private void SetDetailVisible(bool visible)
        {
            _lblDetailHint.Visible = !visible;
            foreach (Control c in _pnlDetail.Controls)
                if (c != _lblDetailHint)
                    c.Visible = visible;
        }

        private void ToggleAdvanced()
        {
            bool expanding = _grpAdvanced.Height <= 30;
            _lblAdvancedHdr.Text = expanding ? "▼  Advanced" : "▶  Advanced";

            if (_grpAdvanced.Tag is Panel pnlContent)
            {
                pnlContent.Visible = expanding;
                _grpAdvanced.Height = expanding
                    ? 30 + pnlContent.PreferredSize.Height
                    : 30;
            }
            _grpAdvanced.Invalidate();
        }

        private void UpdateActiveButton()
        {
            if (_chkActive.Checked)
            {
                _chkActive.Text      = "● Borderless ON";
                _chkActive.BackColor = Color.FromArgb(34, 160, 74);
                _chkActive.ForeColor = Color.White;
                _chkActive.FlatAppearance.BorderColor        = Color.FromArgb(24, 120, 54);
                _chkActive.FlatAppearance.MouseOverBackColor = Color.FromArgb(44, 180, 90);
                _chkActive.FlatAppearance.MouseDownBackColor = Color.FromArgb(20, 130, 55);
            }
            else
            {
                _chkActive.Text      = "○ Borderless OFF";
                _chkActive.BackColor = Color.FromArgb(60, 60, 60);
                _chkActive.ForeColor = Color.FromArgb(160, 160, 160);
                _chkActive.FlatAppearance.BorderColor        = Color.FromArgb(80, 80, 80);
                _chkActive.FlatAppearance.MouseOverBackColor = Color.FromArgb(75, 75, 75);
                _chkActive.FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 45, 45);
            }
        }

        private void BuildSettingsTab(TabPage page)
        {
            var scrollPanel = new Panel
            {
                Dock       = DockStyle.Fill,
                AutoScroll = true,
                Padding    = Padding.Empty
            };

            var tbl = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize    = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock        = DockStyle.Top,
                Padding     = new Padding(20, 12, 20, 12)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            tbl.Controls.Add(MakeSectionHeader("Monitor Baseline Defaults"));

            var pnlMon = MakeSettingsRow("Monitor:");
            _cmbSetMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSetMonitor.Width = 280;
            _cmbSetMonitor.SelectedIndexChanged += CmbSetMonitor_SelectedIndexChanged;
            pnlMon.Controls.Add(_cmbSetMonitor);

            _btnDeleteMonitor.Text     = "✕";
            _btnDeleteMonitor.Size     = new Size(30, 24);
            _btnDeleteMonitor.Margin   = new Padding(6, 0, 0, 0);
            _btnDeleteMonitor.Font     = new Font("Segoe UI", 9f);
            _btnDeleteMonitor.Click   += BtnDeleteMonitor_Click;
            var tipDeleteMon = new ToolTip();
            tipDeleteMon.SetToolTip(_btnDeleteMonitor, "Delete this monitor's saved settings");
            pnlMon.Controls.Add(_btnDeleteMonitor);

            tbl.Controls.Add(pnlMon);

            var pnlSetWH = MakeSettingsRow("Width / Height:");
            SetupNumeric(_numSetWidth,  0, 16384); _numSetWidth.Width  = 110;
            SetupNumeric(_numSetHeight, 0, 16384); _numSetHeight.Width = 110;
            var lx1 = new Label { Text = "×", Width = 16, TextAlign = ContentAlignment.MiddleCenter };
            pnlSetWH.Controls.Add(_numSetWidth);
            pnlSetWH.Controls.Add(lx1);
            pnlSetWH.Controls.Add(_numSetHeight);
            tbl.Controls.Add(pnlSetWH);

            var pnlSetOff = MakeSettingsRow("Offset X / Y:");
            SetupNumeric(_numSetOffsetX, -16384, 16384); _numSetOffsetX.Width = 110;
            SetupNumeric(_numSetOffsetY, -16384, 16384); _numSetOffsetY.Width = 110;
            var lx2 = new Label { Text = "×", Width = 16, TextAlign = ContentAlignment.MiddleCenter };
            pnlSetOff.Controls.Add(_numSetOffsetX);
            pnlSetOff.Controls.Add(lx2);
            pnlSetOff.Controls.Add(_numSetOffsetY);
            tbl.Controls.Add(pnlSetOff);

            var pnlSetConst = MakeSettingsRow(string.Empty);
            _chkSetConstrain.Text    = "Lock mouse cursor to window bounds";
            _chkSetConstrain.AutoSize = true;
            pnlSetConst.Controls.Add(_chkSetConstrain);
            tbl.Controls.Add(pnlSetConst);

            _btnSaveDefault.Text   = "Save Monitor Default";
            _btnSaveDefault.Size   = new Size(180, 28);
            _btnSaveDefault.Click += BtnSaveDefault_Click;
            var pnlSaveBtn = MakeSettingsRow(string.Empty);
            pnlSaveBtn.Controls.Add(_btnSaveDefault);
            tbl.Controls.Add(pnlSaveBtn);

            tbl.Controls.Add(MakeSeparator());

            tbl.Controls.Add(MakeSectionHeader("Global Hotkeys  (click Set Hotkey, then press your key combo)"));

            var pnlHkAdd = MakeSettingsRow("Add Active App:");
            _txtHotkeyAdd.Width    = 200;
            _txtHotkeyAdd.ReadOnly = true;
            _txtHotkeyAdd.Text     = _settings.HotkeyAddApp.ToString();
            _txtHotkeyAdd.KeyDown += (s, e) => CaptureHotkey(e, _txtHotkeyAdd);
            pnlHkAdd.Controls.Add(_txtHotkeyAdd);

            _btnSetHotkeyAdd.Text   = "Set Hotkey";
            _btnSetHotkeyAdd.Size   = new Size(110, 24);
            _btnSetHotkeyAdd.Margin = new Padding(6, 0, 0, 0);
            _btnSetHotkeyAdd.Click += (s, e) =>
            {
                if (_capturingHotkeyId == HOTKEY_ID_ADD)
                {
                    CancelHotkeyCapture();
                    ShowStatus("Add Active App hotkey unchanged.");
                }
                else
                {
                    BeginHotkeyCapture(HOTKEY_ID_ADD, _settings.HotkeyAddApp,
                        _txtHotkeyAdd, _btnSetHotkeyAdd, "Add Active App");
                }
            };
            pnlHkAdd.Controls.Add(_btnSetHotkeyAdd);

            _lblHotkeyAddStatus.Text      = "Not yet registered";
            _lblHotkeyAddStatus.AutoSize  = false;
            _lblHotkeyAddStatus.Width     = 220;
            _lblHotkeyAddStatus.Height    = 24;
            _lblHotkeyAddStatus.Margin    = new Padding(10, 0, 0, 0);
            _lblHotkeyAddStatus.Font      = new Font("Segoe UI", 8f);
            _lblHotkeyAddStatus.TextAlign = ContentAlignment.MiddleLeft;
            pnlHkAdd.Controls.Add(_lblHotkeyAddStatus);
            tbl.Controls.Add(pnlHkAdd);

            var pnlHkRef = MakeSettingsRow("Refresh Display:");
            _txtHotkeyRefresh.Width    = 200;
            _txtHotkeyRefresh.ReadOnly = true;
            _txtHotkeyRefresh.Text     = _settings.HotkeyRefreshApp.ToString();
            _txtHotkeyRefresh.KeyDown += (s, e) => CaptureHotkey(e, _txtHotkeyRefresh);
            pnlHkRef.Controls.Add(_txtHotkeyRefresh);

            _btnSetHotkeyRefresh.Text   = "Set Hotkey";
            _btnSetHotkeyRefresh.Size   = new Size(110, 24);
            _btnSetHotkeyRefresh.Margin = new Padding(6, 0, 0, 0);
            _btnSetHotkeyRefresh.Click += (s, e) =>
            {
                if (_capturingHotkeyId == HOTKEY_ID_REFRESH)
                {
                    CancelHotkeyCapture();
                    ShowStatus("Refresh Display hotkey unchanged.");
                }
                else
                {
                    BeginHotkeyCapture(HOTKEY_ID_REFRESH, _settings.HotkeyRefreshApp,
                        _txtHotkeyRefresh, _btnSetHotkeyRefresh, "Refresh Display");
                }
            };
            pnlHkRef.Controls.Add(_btnSetHotkeyRefresh);

            _lblHotkeyRefreshStatus.Text      = "Not yet registered";
            _lblHotkeyRefreshStatus.AutoSize  = false;
            _lblHotkeyRefreshStatus.Width     = 220;
            _lblHotkeyRefreshStatus.Height    = 24;
            _lblHotkeyRefreshStatus.Margin    = new Padding(10, 0, 0, 0);
            _lblHotkeyRefreshStatus.Font      = new Font("Segoe UI", 8f);
            _lblHotkeyRefreshStatus.TextAlign = ContentAlignment.MiddleLeft;
            pnlHkRef.Controls.Add(_lblHotkeyRefreshStatus);
            tbl.Controls.Add(pnlHkRef);

            tbl.Controls.Add(MakeSeparator());

            tbl.Controls.Add(MakeSectionHeader("Behaviour"));

            _chkMinToTray.Text     = "Minimize to system tray instead of taskbar";
            _chkMinToTray.AutoSize = true;
            _chkMinToTray.Checked  = _settings.MinimizeToTray;
            _chkMinToTray.CheckedChanged += (s, e) =>
            {
                _settings.MinimizeToTray = _chkMinToTray.Checked;
                QueueSave();
            };
            var pnlMin = MakeSettingsRow(string.Empty);
            pnlMin.Controls.Add(_chkMinToTray);
            tbl.Controls.Add(pnlMin);

            _chkStartWindows.Text     = "Start with Windows";
            _chkStartWindows.AutoSize = true;
            _chkStartWindows.Checked  = _settings.StartWithWindows;
            _chkStartWindows.CheckedChanged += (s, e) =>
            {
                _settings.StartWithWindows = _chkStartWindows.Checked;
                _chkStartMin.Enabled = _chkStartWindows.Checked;
                if (!_chkStartWindows.Checked) _chkStartMin.Checked = false;
                UpdateRegistryStartup();
                QueueSave();
            };
            var pnlSW = MakeSettingsRow(string.Empty);
            pnlSW.Controls.Add(_chkStartWindows);
            tbl.Controls.Add(pnlSW);

            _chkStartMin.Text     = "Start minimized to tray";
            _chkStartMin.AutoSize = true;
            _chkStartMin.Checked  = _settings.StartMinimized;
            _chkStartMin.Enabled  = _settings.StartWithWindows;
            _chkStartMin.Margin   = new Padding(28, 0, 0, 0);
            _chkStartMin.CheckedChanged += (s, e) =>
            {
                _settings.StartMinimized = _chkStartMin.Checked;
                UpdateRegistryStartup();
                QueueSave();
            };
            var pnlSM = MakeSettingsRow(string.Empty);
            pnlSM.Controls.Add(_chkStartMin);
            tbl.Controls.Add(pnlSM);

            tbl.Controls.Add(MakeSeparator());

            tbl.Controls.Add(MakeSectionHeader("Permissions"));

            _lblElevationStatus.Text      = _isElevated
                ? "✓  Running as Administrator"
                : "○  Running as Standard User";
            _lblElevationStatus.AutoSize  = true;
            _lblElevationStatus.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var pnlElevation = MakeSettingsRow(string.Empty);
            pnlElevation.Controls.Add(_lblElevationStatus);
            tbl.Controls.Add(pnlElevation);

            _btnRestartAdmin.Text    = "Restart as Administrator";
            _btnRestartAdmin.Size    = new Size(190, 28);
            _btnRestartAdmin.Visible = !_isElevated;
            _btnRestartAdmin.Click  += (s, e) => ConfirmRestartAsAdmin();
            var pnlRestartAdmin = MakeSettingsRow(string.Empty);
            pnlRestartAdmin.Controls.Add(_btnRestartAdmin);
            tbl.Controls.Add(pnlRestartAdmin);

            if (!_isElevated)
            {
                var lblElevationHint = new Label
                {
                    Text      = "Only needed if a tracked game isn't going borderless\n"
                              + "and you see an elevation warning.",
                    AutoSize  = true,
                    Font      = new Font("Segoe UI", 7.75f, FontStyle.Italic),
                    Margin    = new Padding(0, 4, 0, 0)
                };
                var pnlHint = MakeSettingsRow(string.Empty);
                pnlHint.Controls.Add(lblElevationHint);
                tbl.Controls.Add(pnlHint);
            }

            scrollPanel.Controls.Add(tbl);
            page.Controls.Add(scrollPanel);
        }

        private Label MakeSeparatorLabel(string text) => new Label
        {
            Text      = text,
            Width     = 20,
            Height    = 26,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = PaletteTextMuted,
            BackColor = Color.Transparent
        };

        private static FlowLayoutPanel MakeDetailRow(string labelText, params Control[] controls)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 4, 0, 4)
            };
            if (!string.IsNullOrEmpty(labelText))
            {
                row.Controls.Add(new Label
                {
                    Text      = labelText + (labelText.Length > 0 ? ":" : ""),
                    Width     = 140,
                    Height    = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font      = new Font("Segoe UI", 8.5f)
                });
            }
            foreach (var c in controls) row.Controls.Add(c);
            return row;
        }

        private FlowLayoutPanel MakeSettingsRow(string labelText)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize      = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                Margin        = new Padding(0, 3, 0, 3),
                Padding       = Padding.Empty
            };
            if (!string.IsNullOrEmpty(labelText))
            {
                row.Controls.Add(new Label
                {
                    Text      = labelText,
                    Width     = 150,
                    Height    = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font      = new Font("Segoe UI", 8.5f)
                });
            }
            return row;
        }

        private Label MakeSectionHeader(string text)
        {
            return new Label
            {
                Text      = text,
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 28,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft,
                Margin    = new Padding(0, 14, 0, 4),
                Padding   = new Padding(0, 0, 0, 2)
            };
        }

        private Panel MakeSeparator()
        {
            return new Panel
            {
                Height    = 1,
                Width     = 600,
                Margin    = new Padding(0, 10, 0, 10),
                Anchor    = AnchorStyles.Left | AnchorStyles.Right
            };
        }

        private static void AddLabel(Panel row, string text, int width)
        {
            row.Controls.Add(new Label
            {
                Text      = text,
                Width     = width,
                Height    = 23,
                TextAlign = ContentAlignment.MiddleLeft
            });
        }

        private static void SetupNumeric(NumericTextBox n, int min, int max)
        {
            n.Minimum = min;
            n.Maximum = max;
            n.Height  = 24;
            n.Value   = Math.Max(min, Math.Min(max, 0));
        }

        private void MinimizeWindow() => this.WindowState = FormWindowState.Minimized;

        private void ToggleMaximizeWindow() =>
            this.WindowState = this.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;

        private void CloseWindow() => this.Close();

        private bool IsWindowMaximized() => this.WindowState == FormWindowState.Maximized;

        private void BeginWindowDrag()
        {
            ReleaseCapture();
            SendMessage(this.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private static Icon TryExtractIcon(string path)
        {
            try { return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }

        private static readonly Color PaletteBase         = Color.FromArgb(18, 18, 18);
        private static readonly Color PaletteSurface      = Color.FromArgb(30, 30, 30);
        private static readonly Color PaletteRaised       = Color.FromArgb(42, 42, 42);
        private static readonly Color PaletteBorder       = Color.FromArgb(48, 48, 48);
        private static readonly Color PaletteBorderStrong = Color.FromArgb(72, 72, 72);
        private static readonly Color PaletteTextPrimary  = Color.FromArgb(222, 222, 222);
        private static readonly Color PaletteTextSecondary = Color.FromArgb(160, 160, 160);
        private static readonly Color PaletteTextMuted    = Color.FromArgb(90, 90, 90);
        private static readonly Color PaletteAccent       = Color.FromArgb(110, 88, 228);
        private static readonly Color PaletteAccentFg     = Color.White;

        private void ApplyTheme()
        {
            this.BackColor           = PaletteBase;
            _lblStatus.BackColor     = PaletteSurface;
            _lblStatus.ForeColor     = PaletteTextSecondary;
            _lblDetailHint.BackColor = PaletteBase;
            _lblDetailHint.ForeColor = PaletteTextMuted;
            _lblGameTitle.ForeColor  = PaletteTextPrimary;

            _pnlNav.BackColor = PaletteBase;
            UpdateNavButtons();

            _lblAdvancedHdr.BackColor = Color.FromArgb(38, 38, 42);
            _lblAdvancedHdr.ForeColor = PaletteTextPrimary;
            if (_grpAdvanced.Tag is Panel advContent)
                advContent.BackColor = PaletteSurface;

            PaintControlsDark(this.Controls);

            _lblAdvancedHdr.BackColor = Color.FromArgb(38, 38, 42);
            if (_grpAdvanced.Tag is Panel advContent2)
            {
                advContent2.BackColor = PaletteSurface;
                foreach (Control child in advContent2.Controls)
                    if (child is TableLayoutPanel tblAdv2)
                        tblAdv2.BackColor = PaletteSurface;
            }
            _lblStatus.BackColor = PaletteSurface;
            _lblStatus.ForeColor = PaletteTextSecondary;
            _lblDetailHint.BackColor = PaletteBase;
            _lblDetailHint.ForeColor = PaletteTextMuted;

            _lblElevationStatus.BackColor = PaletteBase;
            _lblElevationStatus.ForeColor = _isElevated
                ? Color.FromArgb(72, 199, 72)
                : PaletteTextSecondary;

            _lblHotkeyAddStatus.BackColor     = PaletteBase;
            _lblHotkeyRefreshStatus.BackColor = PaletteBase;

            UpdateNavButtons();

            if (this.IsHandleCreated) ApplyTitleBarTheme();

            _lstGames.Invalidate();
            _grpAdvanced.Invalidate();
            UpdateActiveButton();
        }

        private void PaintControlsDark(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                switch (c)
                {
                    case TabControl tc:
                        tc.BackColor  = PaletteBase;
                        tc.ForeColor  = PaletteTextPrimary;
                        tc.Padding = tc.Padding;
                        PaintControlsDark(tc.Controls);
                        foreach (TabPage tp in tc.TabPages)
                        {
                            tp.BackColor   = PaletteBase;
                            tp.ForeColor   = PaletteTextPrimary;
                            tp.BorderStyle = BorderStyle.None;
                            PaintControlsDark(tp.Controls);
                        }
                        tc.Invalidate(true);
                        break;

                    case FlowLayoutPanel flp:
                        flp.BackColor = PaletteBase;
                        PaintControlsDark(flp.Controls);
                        break;

                    case TableLayoutPanel tlp:
                        tlp.BackColor = PaletteBase;
                        PaintControlsDark(tlp.Controls);
                        break;

                    case Panel pnl:
                        pnl.BackColor = pnl.Height == 1 ? PaletteBorder : PaletteBase;
                        pnl.Invalidate();
                        PaintControlsDark(pnl.Controls);
                        break;

                    case ListBox lst:
                        lst.BackColor  = PaletteSurface;
                        lst.ForeColor  = PaletteTextPrimary;
                        lst.BorderStyle = BorderStyle.None;
                        break;

                    case ComboBox cmb:
                        cmb.DrawMode  = DrawMode.Normal;
                        cmb.BackColor = PaletteSurface;
                        cmb.ForeColor = PaletteTextPrimary;
                        break;

                    case NumericTextBox ntb:
                        ntb.BackColor   = PaletteSurface;
                        ntb.ForeColor   = PaletteTextPrimary;
                        ntb.BorderStyle = BorderStyle.FixedSingle;
                        break;

                    case TextBox txt:
                        txt.BackColor   = PaletteSurface;
                        txt.ForeColor   = PaletteTextPrimary;
                        txt.BorderStyle = BorderStyle.FixedSingle;
                        break;

                    case Button btn:
                        btn.FlatStyle  = FlatStyle.Flat;
                        btn.BackColor  = PaletteRaised;
                        btn.ForeColor  = PaletteTextPrimary;
                        btn.FlatAppearance.BorderColor        = PaletteBorder;
                        btn.FlatAppearance.BorderSize         = 1;
                        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
                        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 30, 30);
                        break;

                    case CheckBox chk:
                        chk.BackColor = PaletteBase;
                        chk.ForeColor = chk.Appearance == Appearance.Button
                            ? PaletteAccentFg
                            : PaletteTextPrimary;
                        chk.FlatStyle = FlatStyle.Flat;
                        if (chk.Appearance == Appearance.Normal)
                        {
                            chk.FlatAppearance.BorderColor        = PaletteBorderStrong;
                            chk.FlatAppearance.CheckedBackColor   = PaletteAccent;
                            chk.FlatAppearance.MouseOverBackColor = Color.Transparent;
                        }
                        break;

                    case Label lbl:
                        if (lbl == _lblDetailHint || lbl == _lblGameTitle ||
                            lbl == _lblHotkeyAddStatus || lbl == _lblHotkeyRefreshStatus) break;
                        lbl.BackColor = PaletteBase;
                        lbl.ForeColor = lbl.Font.Bold ? PaletteTextPrimary : PaletteTextSecondary;
                        break;
                }
            }
        }

        private void GrpAdvanced_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;

            using (var bgBrush = new SolidBrush(PaletteSurface))
                g.FillRectangle(bgBrush, 0, 0, _grpAdvanced.Width, _grpAdvanced.Height);

            using var borderPen = new Pen(PaletteBorderStrong, 1);
            g.DrawRectangle(borderPen, 0, 0, _grpAdvanced.Width - 1, _grpAdvanced.Height - 1);
        }

        private void SortGames()
        {
            _settings.Games.Sort((a, b) =>
            {
                bool aRunning = _runningGames.Contains(a);
                bool bRunning = _runningGames.Contains(b);
                if (aRunning != bRunning) return aRunning ? -1 : 1;
                return string.Compare(a.GameName, b.GameName, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private void RefreshRunningGames()
        {
            var running = new HashSet<GameConfig>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    string title = GetWindowTitle(p.MainWindowHandle);
                    foreach (var g in _settings.Games)
                    {
                        if (running.Contains(g)) continue;
                        if (g.IsMatch(exe, title)) running.Add(g);
                    }
                }
                catch { }
            }

            if (!running.SetEquals(_runningGames))
            {
                _runningGames = running;
                RefreshGamesListOrder();

                _appState.RaiseChanged();
            }
        }

        private void RefreshGamesListOrder()
        {
            var previouslySelected = _selectedGame;

            SortGames();
            _lstGames.Items.Clear();
            foreach (var g in _settings.Games)
                _lstGames.Items.Add(g);

            if (previouslySelected != null)
            {
                int idx = _settings.Games.IndexOf(previouslySelected);
                if (idx >= 0) _lstGames.SelectedIndex = idx;
            }

            _lstGames.Invalidate();
        }

        private void PopulateGamesList()
        {
            SortGames();
            _lstGames.Items.Clear();
            foreach (var g in _settings.Games)
            {
                _lstGames.Items.Add(g);
                CacheIcon(g.ExePath);
            }
            _selectedGame = null;
            SetDetailVisible(false);
        }

        private void CacheIcon(string path)
        {
            if (string.IsNullOrEmpty(path) || _iconCache.ContainsKey(path) || !File.Exists(path))
                return;
            try
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico != null) _iconCache[path] = ico.ToBitmap();
            }
            catch (Exception ex) { AppLogger.Log(ex, $"CacheIcon {path}"); }
        }

        private void LstGames_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _settings.Games.Count) return;
            var  game     = _settings.Games[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;

            Color rowBg = selected
                ? Color.FromArgb(60, 55, 100)
                : PaletteSurface;
            Color rowFg = PaletteTextPrimary;

            using var bgBrush = new SolidBrush(rowBg);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

            if (selected)
            {
                using var accentBrush = new SolidBrush(PaletteAccent);
                e.Graphics.FillRectangle(accentBrush, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
            }

            Color dotColor = game.IsActive
                ? Color.FromArgb(72, 199, 72)
                : Color.FromArgb(90, 90, 90);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var dotBrush = new SolidBrush(dotColor);
            e.Graphics.FillEllipse(dotBrush, e.Bounds.X + 9, e.Bounds.Y + 10, 8, 8);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

            int iconX = e.Bounds.X + 24;
            int iconY = e.Bounds.Y + (e.Bounds.Height - 16) / 2;
            if (!string.IsNullOrEmpty(game.ExePath) &&
                _iconCache.TryGetValue(game.ExePath, out Image? img))
                e.Graphics.DrawImage(img, iconX, iconY, 16, 16);
            else
                e.Graphics.DrawIcon(SystemIcons.Application,
                    new Rectangle(iconX, iconY, 16, 16));

            var nameFont = new Font("Segoe UI", 9f, selected ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(e.Graphics, game.GameName, nameFont,
                new Rectangle(e.Bounds.X + 46, e.Bounds.Y, e.Bounds.Width - 50, e.Bounds.Height),
                rowFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            nameFont.Dispose();

            using var borderPen = new Pen(PaletteBorder, 1);
            e.Graphics.DrawLine(borderPen,
                e.Bounds.X, e.Bounds.Bottom - 1,
                e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        private void SelectGame(GameConfig game)
        {
            int idx = _lstGames.Items.IndexOf(game);
            if (idx >= 0) _lstGames.SelectedIndex = idx;
        }

        private void LstGames_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_lstGames.SelectedIndex < 0 || _lstGames.SelectedIndex >= _settings.Games.Count)
            {
                _selectedGame = null;
                SetDetailVisible(false);
                return;
            }

            _updatingUI   = true;
            _selectedGame = _settings.Games[_lstGames.SelectedIndex];

            _lblGameTitle.Text = _selectedGame.GameName;
            _chkActive.Checked = _selectedGame.IsActive;
            UpdateActiveButton();

            string primary = GetPrimaryMonitorId();
            if (!string.IsNullOrEmpty(primary) && _cmbMonitor.Items.Contains(primary))
                _cmbMonitor.SelectedItem = primary;
            else if (_cmbMonitor.Items.Count > 0)
                _cmbMonitor.SelectedIndex = 0;

            _activeScope = _cmbMonitor.SelectedItem?.ToString() ?? string.Empty;
            LoadProfileToUI(_selectedGame, _activeScope);

            _txtGameName.Text  = _selectedGame.GameName;
            _txtRegex.Text     = _selectedGame.RegexPattern;
            _pendingMatchTarget = _selectedGame.MatchTarget;

            SetDetailVisible(true);
            _updatingUI = false;
        }

        private void ChkConstrain_CheckedChanged(object? sender, EventArgs e)
        {
            if (_updatingUI || _selectedGame == null) return;
            SaveUIToProfile(_activeScope);
            SaveConfig();
            if (_selectedGame.IsActive) EnforceGame(_selectedGame);
            ShowStatus(_chkConstrain.Checked
                ? $"{_selectedGame.GameName} — mouse locked to window bounds."
                : $"{_selectedGame.GameName} — mouse lock disabled.");
        }

        private void LoadProfileToUI(GameConfig g, string scope)
        {
            if (string.IsNullOrEmpty(scope)) return;
            var p = GetEffectiveProfile(g, scope);
            _numWidth.Value    = Clamp(p.Width,   0, 16384);
            _numHeight.Value   = Clamp(p.Height,  0, 16384);
            _numOffsetX.Value  = Clamp(p.OffsetX, -16384, 16384);
            _numOffsetY.Value  = Clamp(p.OffsetY, -16384, 16384);
            _chkConstrain.Checked = p.ConstrainMouse;
        }

        private void SaveUIToProfile(string scope)
        {
            if (_selectedGame == null || string.IsNullOrEmpty(scope)) return;
            if (!_selectedGame.Profiles.ContainsKey(scope))
                _selectedGame.Profiles[scope] = new GameDisplayProfile();
            var p = _selectedGame.Profiles[scope];
            p.Width         = (int)_numWidth.Value;
            p.Height        = (int)_numHeight.Value;
            p.OffsetX       = (int)_numOffsetX.Value;
            p.OffsetY       = (int)_numOffsetY.Value;
            p.ConstrainMouse = _chkConstrain.Checked;
            if (!IsIgnoredName(scope)) _settings.KnownMonitors.Add(scope);
        }

        private GameDisplayProfile GetEffectiveProfile(GameConfig? g, string scope)
        {
            if (g != null && g.Profiles.TryGetValue(scope, out var gp)) return gp;
            if (_settings.MonitorDefaults.TryGetValue(scope, out var def))
                return new GameDisplayProfile
                {
                    Width = def.Width, Height = def.Height,
                    OffsetX = def.OffsetX, OffsetY = def.OffsetY,
                    ConstrainMouse = def.ConstrainMouse
                };
            return new GameDisplayProfile();
        }

        private void CmbMonitor_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_updatingUI || _selectedGame == null) return;
            SaveUIToProfile(_activeScope);
            _activeScope = _cmbMonitor.SelectedItem?.ToString() ?? string.Empty;
            _updatingUI = true;
            LoadProfileToUI(_selectedGame, _activeScope);
            _updatingUI = false;
        }

        private void SelectMonitorScope(string scope)
        {
            if (_cmbMonitor.Items.Contains(scope)) _cmbMonitor.SelectedItem = scope;
        }

        private void ChkActive_CheckedChanged(object? sender, EventArgs e)
        {
            if (_updatingUI || _selectedGame == null) return;
            _selectedGame.IsActive = _chkActive.Checked;
            UpdateActiveButton();
            SaveUIToProfile(_activeScope);
            SaveConfig();
            EnforceGame(_selectedGame);
            _lstGames.Invalidate();
            ShowStatus(_chkActive.Checked
                ? $"{_selectedGame.GameName} — borderless enabled."
                : $"{_selectedGame.GameName} — borderless disabled.");
        }

        private void ToggleGameActive()
        {
            if (_selectedGame == null) return;
            _chkActive.Checked = !_chkActive.Checked;
        }

        private void ReapplyBorderlessForSelectedGame()
        {
            if (_selectedGame == null) return;
            var game = _selectedGame;

            var entry = _trackedWindows.FirstOrDefault(kv => kv.Value == game);
            if (entry.Key == IntPtr.Zero)
            {
                ShowToast($"{game.GameName} isn't currently running.", LogLevel.Warn);
                AppLogger.Log($"ReapplyBorderlessForSelectedGame: '{game.GameName}' has no tracked window.", LogLevel.Warn);
                return;
            }

            RefreshMonitors();
            ApplyBorderless(entry.Key, game);
            ShowToast($"Borderless re-applied\n{game.GameName}", LogLevel.Ok, game.IconImagePath);
            AppLogger.Log($"Borderless re-applied to '{game.GameName}' via Re-Apply button.", LogLevel.Ok);
        }

        private void BtnLoadDefaults_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null || string.IsNullOrEmpty(_activeScope)) return;
            if (_settings.MonitorDefaults.TryGetValue(_activeScope, out var def))
            {
                _updatingUI = true;
                _numWidth.Value       = Clamp(def.Width,   0, 16384);
                _numHeight.Value      = Clamp(def.Height,  0, 16384);
                _numOffsetX.Value     = Clamp(def.OffsetX, -16384, 16384);
                _numOffsetY.Value     = Clamp(def.OffsetY, -16384, 16384);
                _chkConstrain.Checked = def.ConstrainMouse;
                _updatingUI = false;

                SaveUIToProfile(_activeScope);
                SaveConfig();
                if (_selectedGame.IsActive) EnforceGame(_selectedGame);

                ShowStatus($"Defaults loaded and applied for {_activeScope}.");
            }
            else
            {
                ShowStatus($"No saved defaults found for {_activeScope}.");
            }
        }

        private void LoadMonitorDefaultsForSelectedGame()
        {
            EnsureGamesTabActive();
            _btnLoadDefaults.PerformClick();
        }

        private void BtnFetchName_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null) return;

            string? found = null;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                    string title = p.MainWindowTitle.Trim();
                    if (!_selectedGame.IsMatch(exe, title)) continue;
                    found = title;
                    break;
                }
                catch { }
            }

            if (found != null)
            {
                _txtGameName.Text = found;
                ShowStatus($"Display name fetched: \"{found}\"");
            }
            else
            {
                ShowStatus("Game doesn't appear to be running — launch it first.");
            }
        }

        private void FetchNameForSelectedGame()
        {
            EnsureGamesTabActive();
            _btnFetchName.PerformClick();
        }

        private void SetPendingRegexPattern(string pattern)
        {
            _txtRegex.Text = pattern;
            _appState.RaiseChanged();
        }

        private void SetPendingMatchTarget(MatchTargetMode mode)
        {
            _pendingMatchTarget = mode;
            _appState.RaiseChanged();
        }

        private void BtnSaveGame_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null) return;
            CaptureUndoSnapshot(_selectedGame);
            _selectedGame.GameName     = _txtGameName.Text.Trim();
            _selectedGame.RegexPattern = _txtRegex.Text.Trim();
            _selectedGame.MatchTarget  = _pendingMatchTarget;
            _selectedGame.InvalidatePattern();
            SaveUIToProfile(_activeScope);
            SaveConfig();
            if (_selectedGame.IsActive) EnforceGame(_selectedGame);
            int idx = _lstGames.SelectedIndex;
            PopulateGamesList();
            _lstGames.SelectedIndex = idx;
            ShowStatus($"Saved changes for {_selectedGame.GameName}.");

            ShowToast($"Changes saved\n{_selectedGame.GameName} — {_activeScope}", LogLevel.Ok, _selectedGame.IconImagePath);
        }

        private void CaptureUndoSnapshot(GameConfig game)
        {
            _undoTarget       = game;
            _undoGameName     = game.GameName;
            _undoRegexPattern = game.RegexPattern;
            _undoMatchTarget  = game.MatchTarget;
            _undoProfiles     = game.Profiles.ToDictionary(
                kv => kv.Key,
                kv => new GameDisplayProfile
                {
                    Width = kv.Value.Width, Height = kv.Value.Height,
                    OffsetX = kv.Value.OffsetX, OffsetY = kv.Value.OffsetY,
                    ConstrainMouse = kv.Value.ConstrainMouse
                },
                StringComparer.OrdinalIgnoreCase);
        }

        private void UndoLastSave()
        {
            if (_undoTarget == null) return;
            var game = _undoTarget;

            game.GameName     = _undoGameName;
            game.RegexPattern = _undoRegexPattern;
            game.MatchTarget  = _undoMatchTarget;
            game.InvalidatePattern();
            game.Profiles.Clear();
            foreach (var kv in _undoProfiles) game.Profiles[kv.Key] = kv.Value;

            SaveConfig();
            if (game.IsActive) EnforceGame(game);

            int idx = _lstGames.SelectedIndex;
            PopulateGamesList();
            _lstGames.SelectedIndex = idx;
            ShowStatus($"Undid last change for {game.GameName}.");

            _undoTarget = null;
            _appState.RaiseChanged();
        }

        private void SaveGameChanges()
        {
            EnsureGamesTabActive();
            _btnSaveGame.PerformClick();
        }

        private void BtnDeleteGame_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null) return;
            var game = _selectedGame;
            string name = game.GameName;

            RemoveGameTracking(game);

            _settings.Games.Remove(game);
            SaveConfig();
            PopulateGamesList();
            ShowStatus($"{name} removed.");
        }

        private void RemoveSelectedGame()
        {
            EnsureGamesTabActive();
            _btnDeleteGame.PerformClick();
        }

        private void RemoveGameTracking(GameConfig g)
        {
            bool wasActive = g.IsActive;
            g.IsActive = false;

            foreach (var hwnd in _trackedWindows
                .Where(kv => kv.Value == g)
                .Select(kv => kv.Key)
                .ToList())
            {
                try { if (IsWindow(hwnd)) ApplyBorderless(hwnd, g); }
                catch (Exception ex) { AppLogger.Log(ex, "RemoveGameTracking restore"); }
                _trackedWindows.Remove(hwnd);
            }

            g.IsActive = wasActive;

            _runningGames.Remove(g);
            _elevationWarnedGames.Remove(g.GameName);
            _thrashWarnedGames.Remove(g.GameName);
        }

        private List<OpenWindowEntry> GetOpenWindowEntries()
        {
            var systemDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps")
            };

            return Process.GetProcesses()
                .Where(p =>
                {
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) return false;
                    if (p.MainWindowHandle == IntPtr.Zero) return false;
                    string exeName = p.ProcessName + ".exe";
                    if (_systemProcessBlocklist.Contains(exeName) || _settings.IgnoredProcesses.Contains(exeName)) return false;
                    try
                    {
                        string? exePath = p.MainModule?.FileName;
                        if (exePath != null && systemDirs.Any(d =>
                            exePath.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                            return false;
                    }
                    catch { }
                    return true;
                })
                .OrderBy(p => p.MainWindowTitle)
                .Select(p => new OpenWindowEntry(p.MainWindowTitle, p.ProcessName + ".exe", p.Id))
                .GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private void BtnAddRunning_Click(object? sender, EventArgs e)
        {
            var entries = GetOpenWindowEntries();

            using var dlg  = new Form
            {
                Text             = "Add Running App",
                Size             = new Size(400, 460),
                StartPosition    = FormStartPosition.CenterParent,
                FormBorderStyle  = FormBorderStyle.FixedDialog,
                MaximizeBox      = false,
                MinimizeBox      = false
            };
            var lst = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            var btn = new Button  { Text = "Add Selected", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.OK };
            lst.Items.AddRange(entries.Select(x => $"{x.WindowTitle}  ({x.Exe})").ToArray());
            dlg.Controls.Add(lst);
            dlg.Controls.Add(btn);
            dlg.AcceptButton = btn;

            if (dlg.ShowDialog(this) == DialogResult.OK && lst.SelectedIndex >= 0)
                AddGame(entries[lst.SelectedIndex].Exe, false,
                        entries[lst.SelectedIndex].WindowTitle);
        }

        private bool AddGameFromRunningWindow(OpenWindowEntry entry)
        {
            int countBefore = _settings.Games.Count;
            AddGame(entry.Exe, false, entry.WindowTitle);
            _appState.RaiseChanged();
            return _settings.Games.Count > countBefore;
        }

        private async Task AutoFetchArtworkForNewGameAsync(GameConfig game)
        {
            try
            {
                var artwork = _blazorServices.GetRequiredService<ArtworkService>();
                string slug = string.IsNullOrEmpty(game.ExePath) ? game.GameName : game.ExePath;
                var auto = await artwork.TryAutoFetchAsync(game.GameName, slug);
                if (auto.HeroPath == null && auto.IconPath == null) return;

                game.HeroImagePath = auto.HeroPath ?? game.HeroImagePath;
                game.IconImagePath = auto.IconPath ?? game.IconImagePath;
                QueueSave();
                _appState.RaiseChanged();
            }
            catch (Exception ex) { AppLogger.Log(ex, "AutoFetchArtworkForNewGameAsync"); }
        }

        private void SetSteamGridDbApiKey(string key)
        {
            _settings.SteamGridDbApiKey = key;
            QueueSave();
            _appState.RaiseChanged();
        }

        private void ApplyArtwork(string heroPath, string iconPath)
        {
            if (_selectedGame == null) return;
            _selectedGame.HeroImagePath = heroPath;
            _selectedGame.IconImagePath = iconPath;
            QueueSave();
            _appState.RaiseChanged();
        }

        private List<string> GetIgnoredProcesses() =>
            _settings.IgnoredProcesses.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        private void AddIgnoredProcess(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return;
            if (_settings.IgnoredProcesses.Add(exeName.Trim()))
            {
                QueueSave();
                _appState.RaiseChanged();
            }
        }

        private void RemoveIgnoredProcess(string exeName)
        {
            if (_settings.IgnoredProcesses.Remove(exeName))
            {
                QueueSave();
                _appState.RaiseChanged();
            }
        }

        private void ConfirmClearIgnoredProcesses()
        {
            int count = _settings.IgnoredProcesses.Count;
            if (count == 0) return;

            var confirm = MessageBox.Show(
                $"Remove all {count} program{(count == 1 ? "" : "s")} you've added to the ignore list?\n\n" +
                "This can't be undone. NoBorders' own built-in system-process " +
                "filtering (Explorer, Task Manager, etc.) isn't affected either way.",
                "Clear Ignore List",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            _settings.IgnoredProcesses.Clear();
            SaveConfig();
            _appState.RaiseChanged();
        }

        private void ConfirmFullReset()
        {
            var confirm = MessageBox.Show(
                "This will permanently erase ALL NoBorders settings — every tracked " +
                "game, custom resolution, hotkey, ignore-list entry and your " +
                "SteamGridDB API key — and restart the app as if it had just been " +
                "installed.\n\nThis can't be undone.",
                "Full Reset",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            try
            {
                AppLogger.Log("ConfirmFullReset: user-initiated full reset.");
                _saveDebounce.Stop();

                foreach (var g in _trackedWindows.Values.Distinct().ToList())
                    RemoveGameTracking(g);
                _trackedWindows.Clear();
                _runningGames.Clear();

                _settings = new AppSettings();
                UpdateRegistryStartup();

                if (File.Exists(_configPath)) File.Delete(_configPath);

                string cacheDir = Path.Combine(AppPaths.AppDataDir, "artwork-cache");
                if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

                AppLogger.Log("ConfirmFullReset: settings and artwork cache wiped. Relaunching.");

                string exePath = Application.ExecutablePath;
                string fileName = Path.GetFileNameWithoutExtension(exePath);
                if (!File.Exists(exePath) || fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"ConfirmFullReset: resolved path looks wrong (fileName='{fileName}', exists={File.Exists(exePath)}). Aborting relaunch.");
                    MessageBox.Show(
                        "Settings were reset, but NoBorders couldn't relaunch itself " +
                        $"(resolved path: \"{exePath}\").\n\nPlease close and reopen NoBorders manually.",
                        "Full Reset",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute  = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                    psi.ArgumentList.Add(a);

                try
                {
                    Program.AppMutex?.ReleaseMutex();
                    Program.AppMutex?.Dispose();
                }
                catch (Exception relEx) { AppLogger.Log(relEx, "ConfirmFullReset: mutex release (may already be released, non-fatal)"); }

                Process? proc = Process.Start(psi);
                if (proc == null)
                {
                    AppLogger.Log("ConfirmFullReset: Process.Start returned null — the new instance did not launch. Keeping this instance open.");
                    ShowStatus("Reset, but restart may have failed — check noborders.log for details.");
                    return;
                }

                AppLogger.Log($"ConfirmFullReset: new instance launched successfully, PID={proc.Id}. Closing this instance.");
                _forceClose = true;
                Application.Exit();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "ConfirmFullReset");
                MessageBox.Show(
                    "Full reset failed — see noborders.log for details.",
                    "Full Reset",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnAddBrowse_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe",
                Title  = "Select Game Executable"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                AddGame(ofd.FileName, true);
        }

        private bool BrowseForExe()
        {
            EnsureGamesTabActive();
            int countBefore = _settings.Games.Count;
            _btnAddBrowse.PerformClick();
            return _settings.Games.Count > countBefore;
        }

        private static readonly Regex EdgeTagPattern = new(
            @"^(?:shipping|win64|win32|wingdk|x64|x86|test|debug)[-_]|[-_](?:d|shipping|win64|win32|wingdk|x64|x86|test|debug)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string CleanExeBaseName(string baseName)
        {
            string result = baseName, previous;
            do
            {
                previous = result;
                result = EdgeTagPattern.Replace(previous, string.Empty);
            } while (result != previous);
            return result;
        }

        internal static string SanitizeDisplayName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.Format) continue;
                sb.Append(category == UnicodeCategory.SpaceSeparator && c != ' ' ? ' ' : c);
            }
            return sb.ToString().Trim();
        }

        private bool AddGame(string input, bool isFullPath, string? displayName = null)
        {
            if (string.IsNullOrEmpty(input)) return false;

            string baseName  = Path.GetFileNameWithoutExtension(input);
            string cleanName = CleanExeBaseName(baseName);
            string pattern   = $"^{Regex.Escape(cleanName)}.*\\.exe$";

            string gameName  = !string.IsNullOrWhiteSpace(displayName)
                ? SanitizeDisplayName(displayName)
                : cleanName;

            if (_settings.Games.Any(g =>
                    g.GameName.Equals(gameName,    StringComparison.OrdinalIgnoreCase) ||
                    g.GameName.Equals(cleanName,   StringComparison.OrdinalIgnoreCase)))
            {
                ShowStatus($"{gameName} is already in the list.");
                return false;
            }

            string exePath = isFullPath ? input : string.Empty;
            if (!isFullPath)
            {
                try
                {
                    exePath = Process.GetProcesses()
                        .FirstOrDefault(p =>
                            (p.ProcessName + ".exe").Equals(input, StringComparison.OrdinalIgnoreCase))
                        ?.MainModule?.FileName ?? string.Empty;
                }
                catch (Exception ex) { AppLogger.Log(ex, "AddGame path discovery"); }
            }

            var game = new GameConfig
            {
                GameName     = gameName,
                RegexPattern = pattern,
                ExePath      = exePath,
                IsActive     = true
            };

            foreach (var kv in _settings.MonitorDefaults)
            {
                var src = kv.Value;
                game.Profiles[kv.Key] = new GameDisplayProfile
                {
                    Width = src.Width, Height = src.Height,
                    OffsetX = src.OffsetX, OffsetY = src.OffsetY,
                    ConstrainMouse = src.ConstrainMouse
                };
            }
            if (game.Profiles.Count == 0)
            {
                string primary = GetPrimaryMonitorId();
                if (!string.IsNullOrEmpty(primary))
                    game.Profiles[primary] = new GameDisplayProfile();
            }

            _settings.Games.Add(game);
            SaveConfig();
            CacheIcon(exePath);
            PopulateGamesList();
            int newIdx = _lstGames.Items.IndexOf(game);
            if (newIdx >= 0) _lstGames.SelectedIndex = newIdx;

            EnforceGame(game);

            bool elevationIssue = !_isElevated && _elevationWarnedGames.Contains(game.GameName);
            ShowStatus(elevationIssue
                ? $"{cleanName} added, but borderless mode couldn't be fully applied."
                : $"{cleanName} added and borderless mode applied.");

            if (elevationIssue) PromptRestartAsAdmin(game.GameName);

            return !elevationIssue;
        }

        private void EnsureSettingsTabActive() => EnsureTabActive(1);

        private void EnsureGamesTabActive() => EnsureTabActive(0);

        private void EnsureTabActive(int index)
        {
            if (_tabs.SelectedIndex != index) _tabs.SelectedIndex = index;
        }

        private void SelectMonitorDefaultScope(string scope)
        {
            EnsureSettingsTabActive();
            if (_cmbSetMonitor.Items.Contains(scope)) _cmbSetMonitor.SelectedItem = scope;
        }

        private void CmbSetMonitor_SelectedIndexChanged(object? sender, EventArgs e)
        {
            string sel = _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sel)) return;

            if (_settings.MonitorDefaults.TryGetValue(sel, out var def))
            {
                _numSetWidth.Value    = Clamp(def.Width,   0, 16384);
                _numSetHeight.Value   = Clamp(def.Height,  0, 16384);
                _numSetOffsetX.Value  = Clamp(def.OffsetX, -16384, 16384);
                _numSetOffsetY.Value  = Clamp(def.OffsetY, -16384, 16384);
                _chkSetConstrain.Checked = def.ConstrainMouse;
            }
            else
            {
                _numSetWidth.Value    = 1920;
                _numSetHeight.Value   = 1080;
                _numSetOffsetX.Value  = 0;
                _numSetOffsetY.Value  = 0;
                _chkSetConstrain.Checked = false;
            }
        }

        private void BtnDeleteMonitor_Click(object? sender, EventArgs e)
        {
            string sel = _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sel))
            {
                ShowStatus("No monitor selected.");
                return;
            }

            bool isConnected = _monitors.Any(m =>
                m.ID.Equals(sel, StringComparison.OrdinalIgnoreCase));
            if (isConnected)
            {
                MessageBox.Show(
                    $"\"{sel}\" is currently connected and can't be deleted.\n\n" +
                    "Unplug or disable the monitor first, then delete it here.",
                    "Monitor Still Connected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int affectedGames = _settings.Games.Count(g => g.Profiles.ContainsKey(sel));
            string impactLine = affectedGames > 0
                ? $"\n\nThis will also remove the saved profile for this monitor from {affectedGames} game(s)."
                : string.Empty;

            var result = MessageBox.Show(
                $"Permanently delete all saved settings for \"{sel}\"?{impactLine}\n\n" +
                "This cannot be undone.",
                "Delete Monitor",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            _settings.MonitorDefaults.Remove(sel);
            _settings.KnownMonitors.Remove(sel);

            foreach (var g in _settings.Games)
                g.Profiles.Remove(sel);

            SaveConfig();
            AppLogger.Log($"Deleted monitor '{sel}' — removed default and profiles from {affectedGames} game(s).");

            RefreshMonitors();
            if (_selectedGame != null)
            {
                int idx = _lstGames.SelectedIndex;
                if (idx >= 0) LstGames_SelectedIndexChanged(this, EventArgs.Empty);
            }

            ShowStatus($"Deleted monitor \"{sel}\" and its saved settings.");
        }

        private void DeleteMonitorDefault(string scope)
        {
            SelectMonitorDefaultScope(scope);
            _btnDeleteMonitor.PerformClick();
        }

        private void RemoveAllSavedMonitors()
        {
            var connected = new HashSet<string>(_monitors.Select(m => m.ID), StringComparer.OrdinalIgnoreCase);
            var saved = _settings.KnownMonitors.Where(k => !connected.Contains(k)).ToList();
            if (saved.Count == 0)
            {
                ShowStatus("No saved monitors to remove.");
                return;
            }

            var savedSet = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
            int affectedGames = _settings.Games.Count(g => g.Profiles.Keys.Any(savedSet.Contains));
            string impactLine = affectedGames > 0
                ? $"\n\nThis will also remove saved profiles for these monitors from {affectedGames} game(s)."
                : string.Empty;

            var result = MessageBox.Show(
                $"Permanently delete all {saved.Count} saved, not-connected monitor(s)?{impactLine}\n\n" +
                "This cannot be undone.",
                "Remove All Saved Monitors",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            foreach (string name in saved)
            {
                _settings.MonitorDefaults.Remove(name);
                _settings.KnownMonitors.Remove(name);
                foreach (var g in _settings.Games) g.Profiles.Remove(name);
            }

            SaveConfig();
            AppLogger.Log($"Removed all saved monitors ({saved.Count}) — affected {affectedGames} game(s).");

            RefreshMonitors();
            if (_selectedGame != null)
            {
                int idx = _lstGames.SelectedIndex;
                if (idx >= 0) LstGames_SelectedIndexChanged(this, EventArgs.Empty);
            }

            ShowStatus($"Removed {saved.Count} saved monitor(s).");
            _appState.RaiseChanged();
        }

        private void BtnSaveDefault_Click(object? sender, EventArgs e)
        {
            string sel = _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sel)) return;

            _settings.MonitorDefaults[sel] = new GameDisplayProfile
            {
                Width          = (int)_numSetWidth.Value,
                Height         = (int)_numSetHeight.Value,
                OffsetX        = (int)_numSetOffsetX.Value,
                OffsetY        = (int)_numSetOffsetY.Value,
                ConstrainMouse = _chkSetConstrain.Checked
            };
            SaveConfig();
            ShowStatus($"Monitor defaults saved for {sel}.");

            _btnSaveDefault.Text = "Saved ✔";
            var t = new System.Windows.Forms.Timer { Interval = 1400 };
            t.Tick += (ts, te) => { _btnSaveDefault.Text = "Save Monitor Default"; t.Stop(); t.Dispose(); };
            t.Start();

            ShowToast($"Monitor default saved\n{sel}", LogLevel.Ok);
        }

        private void SaveMonitorDefault()
        {
            EnsureSettingsTabActive();
            _btnSaveDefault.PerformClick();
        }

        private void ApplyMonitorDefaultToAllGames()
        {
            string sel = _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sel))
            {
                ShowStatus("No monitor selected.");
                return;
            }

            if (_settings.Games.Count == 0)
            {
                ShowStatus("No tracked games/programs to apply to.");
                return;
            }

            var profile = new GameDisplayProfile
            {
                Width          = (int)_numSetWidth.Value,
                Height         = (int)_numSetHeight.Value,
                OffsetX        = (int)_numSetOffsetX.Value,
                OffsetY        = (int)_numSetOffsetY.Value,
                ConstrainMouse = _chkSetConstrain.Checked
            };

            int existingProfiles = _settings.Games.Count(g => g.Profiles.ContainsKey(sel));
            string impactLine = existingProfiles > 0
                ? $"\n\n{existingProfiles} game(s) already have their own profile for this monitor — those will be overwritten."
                : string.Empty;

            var result = MessageBox.Show(
                $"Apply {profile.Width}×{profile.Height} @ {profile.OffsetX},{profile.OffsetY} on \"{sel}\" to all {_settings.Games.Count} tracked game(s)/program(s)?{impactLine}\n\n" +
                "This cannot be undone.",
                "Apply to All Games",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            _settings.MonitorDefaults[sel] = profile;
            foreach (var g in _settings.Games)
            {
                g.Profiles[sel] = new GameDisplayProfile
                {
                    Width          = profile.Width,
                    Height         = profile.Height,
                    OffsetX        = profile.OffsetX,
                    OffsetY        = profile.OffsetY,
                    ConstrainMouse = profile.ConstrainMouse
                };
            }

            SaveConfig();
            AppLogger.Log($"Applied monitor default for '{sel}' to all {_settings.Games.Count} game(s).");

            if (_selectedGame != null)
            {
                int idx = _lstGames.SelectedIndex;
                if (idx >= 0) LstGames_SelectedIndexChanged(this, EventArgs.Empty);
            }

            ShowStatus($"Applied \"{sel}\" settings to {_settings.Games.Count} game(s).");
            ShowToast($"Applied to {_settings.Games.Count} game(s)\n{sel}", LogLevel.Ok);
            _appState.RaiseChanged();
        }

        private void ToggleHotkeyAddCapture()
        {
            EnsureSettingsTabActive();
            _btnSetHotkeyAdd.PerformClick();
        }

        private void ToggleHotkeyRefreshCapture()
        {
            EnsureSettingsTabActive();
            _btnSetHotkeyRefresh.PerformClick();
        }

        private void ToggleMinimizeToTray() => _chkMinToTray.Checked = !_chkMinToTray.Checked;

        private void ToggleStartWithWindows() => _chkStartWindows.Checked = !_chkStartWindows.Checked;

        private void ToggleStartMinimized()
        {
            if (!_chkStartWindows.Checked) return;
            _chkStartMin.Checked = !_chkStartMin.Checked;
        }

        private void ToggleConstrainMouse() => _chkConstrain.Checked = !_chkConstrain.Checked;

        private void ToggleConstrainMouseDefault() => _chkSetConstrain.Checked = !_chkSetConstrain.Checked;

        private void AdjustWidth(int delta) { _numWidth.Value += delta; _appState.RaiseChanged(); }
        private void AdjustHeight(int delta) { _numHeight.Value += delta; _appState.RaiseChanged(); }
        private void AdjustOffsetX(int delta) { _numOffsetX.Value += delta; _appState.RaiseChanged(); }
        private void AdjustOffsetY(int delta) { _numOffsetY.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultWidth(int delta) { _numSetWidth.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultHeight(int delta) { _numSetHeight.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultOffsetX(int delta) { _numSetOffsetX.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultOffsetY(int delta) { _numSetOffsetY.Value += delta; _appState.RaiseChanged(); }

        private void AlignOnMonitor(MonitorAlignMode mode)
        {
            if (_selectedGame == null || string.IsNullOrEmpty(_activeScope)) return;
            var mon = _monitors.FirstOrDefault(m => m.ID == _activeScope);
            if (mon == null)
            {
                ShowStatus($"{_activeScope} is not connected — can't align.");
                return;
            }
            ApplyAlign(mon, mode, _numWidth, _numHeight, _numOffsetX, _numOffsetY);
            _appState.RaiseChanged();
        }

        private void AlignOnMonitorDefault(MonitorAlignMode mode)
        {
            string sel = _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(sel)) return;
            var mon = _monitors.FirstOrDefault(m => m.ID == sel);
            if (mon == null)
            {
                ShowStatus($"{sel} is not connected — can't align.");
                return;
            }
            ApplyAlign(mon, mode, _numSetWidth, _numSetHeight, _numSetOffsetX, _numSetOffsetY);
            _appState.RaiseChanged();
        }

        private static void ApplyAlign(MonitorItem mon, MonitorAlignMode mode,
            NumericTextBox numWidth, NumericTextBox numHeight, NumericTextBox numOffsetX, NumericTextBox numOffsetY)
        {
            var (offsetX, offsetY) = ComputeAlignOffset(mon.Width, mon.Height, (int)numWidth.Value, (int)numHeight.Value, mode);
            numOffsetX.Value = Clamp(offsetX, -16384, 16384);
            numOffsetY.Value = Clamp(offsetY, -16384, 16384);
        }

        internal static (int OffsetX, int OffsetY) ComputeAlignOffset(
            int monitorWidth, int monitorHeight, int windowWidth, int windowHeight, MonitorAlignMode mode) => mode switch
        {
            MonitorAlignMode.Left   => (0, (monitorHeight - windowHeight) / 2),
            MonitorAlignMode.Right  => (monitorWidth - windowWidth, (monitorHeight - windowHeight) / 2),
            MonitorAlignMode.Bottom => ((monitorWidth - windowWidth) / 2, monitorHeight - windowHeight),
            _                       => ((monitorWidth - windowWidth) / 2, (monitorHeight - windowHeight) / 2),
        };

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString().Trim();
        }

        private bool TryGetHotkeyTargetExe(IntPtr hwnd, out string exeName)
        {
            exeName = string.Empty;
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;
                if (pid == (uint)Environment.ProcessId) return false;

                using var p = Process.GetProcessById((int)pid);
                exeName = p.ProcessName + ".exe";

                if (_systemProcessBlocklist.Contains(exeName) || _settings.IgnoredProcesses.Contains(exeName)) return false;

                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) &&
                        path.Equals(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HotkeyAdd()
        {
            IntPtr hwnd = _lastForegroundHwnd;
            AppLogger.Log($"HotkeyAdd fired. hwnd={hwnd}");

            if (!TryGetHotkeyTargetExe(hwnd, out string exeName))
            {
                AppLogger.Log("HotkeyAdd: foreground window is blocked, NoBorders itself, or invalid — ignoring.");
                return;
            }
            AppLogger.Log($"HotkeyAdd: exeName={exeName}");

            try
            {
                string windowTitle = GetWindowTitle(hwnd);
                AppLogger.Log($"HotkeyAdd: windowTitle=\"{windowTitle}\"");

                bool alreadyTracked = _settings.Games.Any(g => g.IsMatch(exeName, windowTitle));
                AppLogger.Log($"HotkeyAdd: alreadyTracked={alreadyTracked}");

                if (!alreadyTracked && this.IsHandleCreated)
                {
                    string nameToUse = !string.IsNullOrWhiteSpace(windowTitle) ? windowTitle : exeName;
                    AppLogger.Log($"HotkeyAdd: calling AddGame with displayName=\"{nameToUse}\"");
                    this.Invoke(new MethodInvoker(() =>
                    {
                        if (AddGame(exeName, false, nameToUse))
                        {
                            ShowToast($"Added & borderless applied\n{nameToUse}", LogLevel.Ok);
                            AppLogger.Log($"Added & borderless applied to '{nameToUse}'.", LogLevel.Ok);

                            if (_selectedGame is { } newGame)
                                _ = AutoFetchArtworkForNewGameAsync(newGame);
                        }
                    }));
                }
                else if (alreadyTracked)
                {
                    AppLogger.Log("HotkeyAdd: game already tracked, applying borderless.");
                    var match = _settings.Games.First(g => g.IsMatch(exeName, windowTitle));
                    _trackedWindows[hwnd] = match;
                    ApplyBorderless(hwnd, match);
                    ShowToast($"Borderless applied\n{match.GameName}", LogLevel.Ok, match.IconImagePath);
                    AppLogger.Log($"Borderless applied to '{match.GameName}'.", LogLevel.Ok);
                }
            }
            catch (Exception ex) { AppLogger.Log(ex, "HotkeyAdd"); }
        }

        private void HotkeyRefresh()
        {
            IntPtr hwnd = _lastForegroundHwnd;
            AppLogger.Log($"HotkeyRefresh fired. hwnd={hwnd}");

            if (!TryGetHotkeyTargetExe(hwnd, out string exeNameRaw))
            {
                AppLogger.Log("HotkeyRefresh: foreground window is blocked, NoBorders itself, or invalid — ignoring.");
                return;
            }

            try
            {
                string exeName    = exeNameRaw.ToLowerInvariant();
                string windowTitle = GetWindowTitle(hwnd);
                var match       = _settings.Games.FirstOrDefault(g =>
                    g.IsActive && g.IsMatch(exeName, windowTitle));
                if (match == null)
                {
                    ShowToast("Foreground app is not in the game list.", LogLevel.Warn);
                    AppLogger.Log("HotkeyRefresh: foreground app is not in the game list.", LogLevel.Warn);
                    return;
                }

                RefreshMonitors();

                _trackedWindows[hwnd] = match;
                ApplyBorderless(hwnd, match);
                ShowToast($"Borderless re-applied\n{match.GameName}", LogLevel.Ok, match.IconImagePath);
                AppLogger.Log($"Borderless re-applied to '{match.GameName}'.", LogLevel.Ok);
            }
            catch (Exception ex) { AppLogger.Log(ex, "HotkeyRefresh"); }
        }

        private void ScanExistingWindows()
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        MatchAndApply(p.MainWindowHandle, p);
                }
                catch { }
            }
        }

        private void MatchAndApply(IntPtr hwnd, Process p)
        {
            string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
            string title = GetWindowTitle(hwnd);
            foreach (var g in _settings.Games)
            {
                if (g.IsActive && g.IsMatch(exe, title))
                {
                    ApplyBorderless(hwnd, g);
                    _trackedWindows[hwnd] = g;
                }
            }
        }

        private async Task TrackNewWindowAsync(IntPtr hwnd)
        {
            var cts = new CancellationTokenSource();
            _pending[hwnd] = cts;
            try
            {
                for (int i = 0; i < 60 && !cts.Token.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, cts.Token);
                    try
                    {
                        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) continue;
                        using var p  = Process.GetProcessById((int)pid);
                        string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                        string title = GetWindowTitle(hwnd);
                        GetWindowRect(hwnd, out RECT r);
                        if ((r.Right - r.Left) < 100) continue;

                        foreach (var g in _settings.Games)
                        {
                            if (g.IsActive && g.IsMatch(exe, title))
                            {
                                ApplyBorderless(hwnd, g);
                                _trackedWindows[hwnd] = g;
                                return;
                            }
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (Exception ex) { AppLogger.Log(ex, "TrackNewWindowAsync inner"); }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { AppLogger.Log(ex, "TrackNewWindowAsync outer"); }
            finally
            {
                _pending.Remove(hwnd);
                cts.Dispose();
            }
        }

        private void EnforceGame(GameConfig g)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (g.IsMatch((p.ProcessName + ".exe").ToLowerInvariant(), GetWindowTitle(p.MainWindowHandle)))
                    {
                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                    }
                }
                catch { }
            }
        }

        private void EnforceTimer_Tick(object? sender, EventArgs e)
        {

            RefreshRunningGames();

            foreach (var hwnd in _trackedWindows.Keys.ToList())
            {
                if (!IsWindow(hwnd))
                {
                    _trackedWindows.Remove(hwnd);
                    _consecutiveEnforceChanges.Remove(hwnd);
                    _enforceBackoffUntil.Remove(hwnd);
                    continue;
                }

                var g = _trackedWindows[hwnd];

                if (!_settings.Games.Contains(g))
                {
                    try
                    {
                        bool wasActive = g.IsActive;
                        g.IsActive = false;
                        ApplyBorderless(hwnd, g);
                        g.IsActive = wasActive;
                    }
                    catch (Exception ex) { AppLogger.Log(ex, "EnforceTimer_Tick: restoring orphaned tracked window"); }
                    _trackedWindows.Remove(hwnd);
                    continue;
                }

                try { ApplyBorderless(hwnd, g); }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, "EnforceTimer_Tick");
                    _trackedWindows.Remove(hwnd);
                }
            }

            var trackedGames = new HashSet<GameConfig>(_trackedWindows.Values);
            bool anyUntracked = _settings.Games.Any(g => g.IsActive && !trackedGames.Contains(g));
            if (!anyUntracked) return;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (_trackedWindows.ContainsKey(p.MainWindowHandle)) continue;

                    string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                    string title = GetWindowTitle(p.MainWindowHandle);
                    foreach (var g in _settings.Games)
                    {
                        if (!g.IsActive || !g.IsMatch(exe, title)) continue;
                        if (trackedGames.Contains(g)) continue;

                        GetWindowRect(p.MainWindowHandle, out RECT r);
                        if ((r.Right - r.Left) < 100) continue;

                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                        trackedGames.Add(g);
                    }
                }
                catch { }
            }
        }

        private void ApplyBorderless(IntPtr hwnd, GameConfig g)
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);

            if (!g.IsActive)
            {
                if ((style & (int)WS_CAPTION) == 0)
                {
                    SetWindowLong(hwnd, GWL_STYLE, (int)(style | WS_CAPTION | WS_THICKFRAME));
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
                _consecutiveEnforceChanges.Remove(hwnd);
                _enforceBackoffUntil.Remove(hwnd);
                return;
            }

            if (_enforceBackoffUntil.TryGetValue(hwnd, out DateTime backoffUntil))
            {
                if (DateTime.UtcNow < backoffUntil) return;
                _enforceBackoffUntil.Remove(hwnd);
                _consecutiveEnforceChanges.Remove(hwnd);
            }

            var scr = Screen.FromHandle(hwnd);

            string monId = _monitors.FirstOrDefault(m =>
                TrimNull(m.DeviceName).Equals(TrimNull(scr.DeviceName),
                    StringComparison.OrdinalIgnoreCase))?.ID ?? string.Empty;

            if (string.IsNullOrEmpty(monId))
            {
                RefreshMonitors();
                monId = _monitors.FirstOrDefault(m =>
                    TrimNull(m.DeviceName).Equals(TrimNull(scr.DeviceName),
                        StringComparison.OrdinalIgnoreCase))?.ID
                    ?? GetPrimaryMonitorId();
            }

            if (string.IsNullOrEmpty(monId))
            {
                AppLogger.Log($"ApplyBorderless: could not resolve monitor ID for hwnd {hwnd}. Skipping.");
                return;
            }

            var profile  = GetEffectiveProfile(g, monId);
            int targetX  = scr.Bounds.X + profile.OffsetX;
            int targetY  = scr.Bounds.Y + profile.OffsetY;
            uint newStyle = (uint)(style & ~WS_CAPTION & ~WS_THICKFRAME & ~WS_MAXIMIZE);

            GetWindowRect(hwnd, out RECT cur);
            bool positionCorrect =
                cur.Left              == targetX       &&
                cur.Top               == targetY       &&
                (cur.Right  - cur.Left) == profile.Width  &&
                (cur.Bottom - cur.Top)  == profile.Height;
            bool styleCorrect = (style & (int)(WS_CAPTION | WS_THICKFRAME)) == 0;
            bool needsChange  = !styleCorrect || !positionCorrect;

            if (needsChange)
            {
                int consecutive = _consecutiveEnforceChanges.TryGetValue(hwnd, out int c) ? c + 1 : 1;
                _consecutiveEnforceChanges[hwnd] = consecutive;
                if (consecutive >= ThrashTickThreshold)
                {
                    _enforceBackoffUntil[hwnd] = DateTime.UtcNow.Add(ThrashBackoff);
                    _consecutiveEnforceChanges.Remove(hwnd);
                    AppLogger.Log(
                        $"ApplyBorderless: '{g.GameName}' (hwnd={hwnd}) needed re-enforcement on "
                        + $"{consecutive} consecutive ticks — backing off for {ThrashBackoff.TotalSeconds:0}s "
                        + "to avoid fighting the game's own window changes.", LogLevel.Warn);
                    if (_thrashWarnedGames.Add(g.GameName))
                    {
                        ShowToast(
                            $"{g.GameName} keeps resetting its own window — pausing borderless\n"
                            + "enforcement briefly so it doesn't fight the game.",
                            LogLevel.Warn, g.IconImagePath);
                    }
                    return;
                }
            }
            else
            {
                _consecutiveEnforceChanges.Remove(hwnd);
            }

            bool styleCallFailed = false;
            bool posCallFailed   = false;

            if (!styleCorrect)
            {
                ShowWindow(hwnd, SW_RESTORE);
                int prevStyle = SetWindowLong(hwnd, GWL_STYLE, (int)newStyle);
                if (prevStyle == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0)
                    {
                        styleCallFailed = true;
                        AppLogger.Log($"SetWindowLong failed for '{g.GameName}' (hwnd={hwnd}), Win32={err}", LogLevel.Warn);
                    }
                }
            }
            if (!styleCorrect || !positionCorrect)
            {
                bool posOk = SetWindowPos(hwnd, IntPtr.Zero,
                    targetX, targetY, profile.Width, profile.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                if (!posOk)
                {
                    int err = Marshal.GetLastWin32Error();
                    posCallFailed = true;
                    AppLogger.Log($"SetWindowPos failed for '{g.GameName}' (hwnd={hwnd}), Win32={err}", LogLevel.Warn);
                }
            }

            if (!styleCorrect && !styleCallFailed)
                AppLogger.LogVerbose($"Restyled '{g.GameName}' (hwnd={hwnd}): removed title bar/resize border.");
            if ((!styleCorrect || !positionCorrect) && !posCallFailed)
                AppLogger.LogVerbose($"Repositioned '{g.GameName}' (hwnd={hwnd}) to {targetX},{targetY} {profile.Width}x{profile.Height} on monitor '{monId}'.");

            if ((styleCallFailed || posCallFailed) && !_isElevated
                && !_elevationWarnedGames.Contains(g.GameName))
            {
                _elevationWarnedGames.Add(g.GameName);
                ShowToast(
                    $"Couldn't fully apply borderless to {g.GameName}.\n"
                    + "Try Settings → Restart as Administrator.",
                    LogLevel.Error, g.IconImagePath);
            }
        }

        private void ClipTimer_Tick(object? sender, EventArgs e)
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { ClipCursor(IntPtr.Zero); return; }

            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) { ClipCursor(IntPtr.Zero); return; }
                using var p   = Process.GetProcessById((int)pid);
                string exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                string title   = GetWindowTitle(hwnd);
                var match      = _settings.Games.FirstOrDefault(g =>
                    g.IsActive && g.IsMatch(exeName, title));

                if (match != null)
                {
                    var scr  = Screen.FromHandle(hwnd);
                    string monId = _monitors.FirstOrDefault(m =>
                        TrimNull(m.DeviceName).Equals(TrimNull(scr.DeviceName),
                            StringComparison.OrdinalIgnoreCase))?.ID ?? GetPrimaryMonitorId();

                    if (!string.IsNullOrEmpty(monId))
                    {
                        var profile = GetEffectiveProfile(match, monId);
                        if (profile.ConstrainMouse && GetWindowRect(hwnd, out RECT r))
                        {
                            ClipCursor(ref r);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Log(ex, "ClipTimer_Tick"); }

            ClipCursor(IntPtr.Zero);
        }

        private void ShowStatus(string message)
        {
            _lblStatus.Text = message;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private Form? _elevationDialogForm;

        private void ShowElevationDialog(string gameName = "", bool toStandardUser = false)
        {
            if (!this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new MethodInvoker(() => ShowElevationDialog(gameName, toStandardUser)));
                return;
            }

            if (_elevationDialogForm is { IsDisposed: false }) return;

            const int dialogWidth  = 470;
            const int dialogHeight = 270;

            var dialogView = new ArtworkAwareBlazorWebView
            {
                HostPage = "wwwroot\\index.html",
                Dock     = DockStyle.Fill,
                Services = _blazorServices
            };
            var parameters = new Dictionary<string, object?>
            {
                [nameof(Components.Screens.ElevationDialog.GameName)]       = gameName,
                [nameof(Components.Screens.ElevationDialog.ToStandardUser)] = toStandardUser
            };
            dialogView.RootComponents.Add<Components.Screens.ElevationDialog>("#app", parameters);

            var dialog = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar   = false,
                TopMost         = true,
                Size            = new Size(dialogWidth, dialogHeight),
                StartPosition   = FormStartPosition.Manual,
                BackColor       = Color.FromArgb(0x0b, 0x0b, 0x0e)
            };

            var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            dialog.Location = new Point(
                screen.X + (screen.Width  - dialog.Width)  / 2,
                screen.Y + (screen.Height - dialog.Height) / 2);

            dialog.Controls.Add(dialogView);

            using (var path = RoundedRect(new Rectangle(0, 0, dialog.Width, dialog.Height), 10))
                dialog.Region = new Region(path);

            _elevationDialogForm = dialog;
            dialog.Show();
            dialog.Activate();
        }

        private void CloseElevationDialog()
        {
            if (_elevationDialogForm is not { IsDisposed: false } dialog) return;
            _elevationDialogForm = null;
            dialog.Close();
            dialog.Dispose();
        }

        private System.Windows.Forms.Timer? _toastTimer;
        private string _toastTitle = "";
        private string _toastDetail = "";
        private string _toastIconPath = "";
        private LogLevel _toastLevel = LogLevel.Ok;
        private bool _toastVisible;

        private void ShowToast(string message, LogLevel level = LogLevel.Ok, string iconPath = "")
        {
            if (!this.IsHandleCreated) return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new MethodInvoker(() => ShowToast(message, level, iconPath)));
                return;
            }

            int nl = message.IndexOf('\n');
            string title  = nl >= 0 ? message[..nl] : (level == LogLevel.Error ? "Action failed" : "NoBorders");
            string detail = nl >= 0 ? message[(nl + 1)..] : message;

            bool hidden = !this.Visible || this.WindowState == FormWindowState.Minimized;
            if (hidden)
            {
                ShowPopupToast(title, detail, level, iconPath);
                return;
            }

            _toastTitle    = title;
            _toastDetail   = detail;
            _toastLevel    = level;
            _toastIconPath = iconPath;
            _toastVisible  = true;
            _appState.RaiseChanged();

            const int holdMs = 6000;

            _toastTimer?.Stop();
            _toastTimer?.Dispose();
            _toastTimer = new System.Windows.Forms.Timer { Interval = holdMs };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer!.Stop();
                _toastTimer.Dispose();
                _toastTimer = null;
                _toastVisible = false;
                _appState.RaiseChanged();
            };
            _toastTimer.Start();
        }

        private void ShowPopupToast(string title, string detail, LogLevel level, string iconPath)
        {
            const int toastWidth  = 330;
            const int toastHeight = 100;
            const int cornerGap   = 18;
            const int holdMs      = 6200;

            var toastView = new ArtworkAwareBlazorWebView
            {
                HostPage = "wwwroot\\index.html",
                Dock     = DockStyle.Fill,
                Services = _blazorServices
            };
            var parameters = new Dictionary<string, object?>
            {
                [nameof(Components.Screens.Toast.Title)]   = title,
                [nameof(Components.Screens.Toast.Detail)]  = detail,
                [nameof(Components.Screens.Toast.Level)]   = level,
                [nameof(Components.Screens.Toast.IconUrl)] = iconPath
            };
            toastView.RootComponents.Add<Components.Screens.Toast>("#app", parameters);

            var toast = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar   = false,
                TopMost         = true,
                Size            = new Size(toastWidth, toastHeight),
                StartPosition   = FormStartPosition.Manual,
                BackColor       = Color.FromArgb(0x11, 0x11, 0x16)
            };

            var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            toast.Location = new Point(
                screen.Right  - toast.Width  - cornerGap,
                screen.Bottom - toast.Height - cornerGap);

            toast.Controls.Add(toastView);

            using (var path = RoundedRect(new Rectangle(0, 0, toast.Width, toast.Height), 8))
                toast.Region = new Region(path);

            var closeTimer = new System.Windows.Forms.Timer { Interval = holdMs };
            closeTimer.Tick += (s, e) =>
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                toast.Close();
                toast.Dispose();
            };

            toast.Show();
            NativeMethods.ShowWindowNoActivate(toast.Handle);
            closeTimer.Start();
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X,             r.Y,              d, d, 180, 90);
            path.AddArc(r.Right - d,     r.Y,              d, d, 270, 90);
            path.AddArc(r.Right - d,     r.Bottom - d,     d, d,   0, 90);
            path.AddArc(r.X,             r.Bottom - d,     d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        private void UpdateRegistryStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true);
                if (key == null) return;
                if (_settings.StartWithWindows)
                {
                    string args = _settings.StartMinimized ? " -minimized" : string.Empty;
                    key.SetValue(APP_NAME, $"\"{Application.ExecutablePath}\"{args}");
                    AppLogger.LogVerbose($"Registry: set {REG_RUN_KEY}\\{APP_NAME} = \"{Application.ExecutablePath}\"{args}");
                }
                else
                {
                    key.DeleteValue(APP_NAME, false);
                    AppLogger.LogVerbose($"Registry: removed {REG_RUN_KEY}\\{APP_NAME}");
                }
            }
            catch (Exception ex) { AppLogger.Log(ex, "UpdateRegistryStartup"); }
        }

        private void OnResize(object? sender, EventArgs e)
        {
            if (_isRestoringFromTray) return;

            if (this.WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }

            _appState?.RaiseChanged();
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing && _settings.MinimizeToTray)
            {
                e.Cancel        = true;
                this.WindowState = FormWindowState.Minimized;
                OnResize(this, EventArgs.Empty);
                return;
            }

            UnregisterHotKey(this.Handle, HOTKEY_ID_ADD);
            UnregisterHotKey(this.Handle, HOTKEY_ID_REFRESH);

            foreach (var cts in _pending.Values) cts.Cancel();

            _enforceTimer.Stop();
            _clipTimer.Stop();
            _statusTimer.Stop();
            _saveDebounce.Stop();

            CaptureWindowBounds();
            SaveConfig();

            ClipCursor(IntPtr.Zero);
            _trayIcon.Visible = false;
        }

        private void CaptureWindowBounds()
        {
            if (this.WindowState == FormWindowState.Normal)
            {
                _settings.WindowX         = this.Location.X;
                _settings.WindowY         = this.Location.Y;
                _settings.WindowWidth     = this.Size.Width;
                _settings.WindowHeight    = this.Size.Height;
                _settings.WindowMaximized = false;
            }
            else if (this.WindowState == FormWindowState.Maximized)
            {
                _settings.WindowX         = this.RestoreBounds.X;
                _settings.WindowY         = this.RestoreBounds.Y;
                _settings.WindowWidth     = this.RestoreBounds.Width;
                _settings.WindowHeight    = this.RestoreBounds.Height;
                _settings.WindowMaximized = true;
            }
        }

        private async void OnDisplayConfigChanged()
        {
            try
            {
                RefreshMonitors();

                await Task.Delay(1500);

                _trackedWindows.Clear();

                foreach (var cts in _pending.Values) cts.Cancel();
                _pending.Clear();

                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                            MatchAndApply(p.MainWindowHandle, p);
                    }
                    catch { }
                }

                if (_selectedGame != null && this.IsHandleCreated)
                {
                    int idx = _lstGames.SelectedIndex;
                    if (idx >= 0) LstGames_SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
            catch (Exception ex) { AppLogger.Log(ex, "OnDisplayConfigChanged"); }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style &= ~(int)WS_CAPTION;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (!this.IsHandleCreated)
            {
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == _wmShellHook && m.WParam.ToInt32() == HSHELL_WINDOWCREATED && !_enforcementPaused)
                _ = TrackNewWindowAsync(m.LParam);

            if (_wmShowFirst != 0 && m.Msg == _wmShowFirst)
            { RestoreFromTray(); return; }

            if (m.Msg == WM_DISPLAYCHANGE)
                OnDisplayConfigChanged();

            if (m.Msg == WM_SETTINGCHANGE)
                RefreshTrayIconForTheme();

            if (m.Msg == WM_HOTKEY)
            {
                _lastForegroundHwnd = GetForegroundWindow();
                if      (m.WParam.ToInt32() == HOTKEY_ID_ADD)     HotkeyAdd();
                else if (m.WParam.ToInt32() == HOTKEY_ID_REFRESH) HotkeyRefresh();
            }

            if (m.Msg == WM_POWERBROADCAST)
            {
                int evt = m.WParam.ToInt32();
                if (evt == PBT_APMSUSPEND)
                    OnSleep();
                else if (evt == PBT_APMRESUMESUSPEND || evt == PBT_APMRESUMEAUTOMATIC)
                    OnWake();
            }

            base.WndProc(ref m);
        }

        private void OnSleep()
        {
            AppLogger.Log("=== SYSTEM SLEEP ===");
            AppLogger.Log($"  Tracked windows at sleep : {_trackedWindows.Count}");
            AppLogger.Log($"  Pending tasks at sleep   : {_pending.Count}");
            AppLogger.Log($"  Active games             : {_settings.Games.Count(g => g.IsActive)}");
            AppLogger.Log($"  Enforce timer running    : {_enforceTimer.Enabled}");

            _wasHiddenBeforeSleep = !this.Visible || this.WindowState == FormWindowState.Minimized;
            AppLogger.Log($"  Form visible before sleep: {this.Visible}, minimized: {this.WindowState == FormWindowState.Minimized}");

            _enforceTimer.Stop();
            _clipTimer.Stop();

            UnregisterHotKey(this.Handle, HOTKEY_ID_ADD);
            UnregisterHotKey(this.Handle, HOTKEY_ID_REFRESH);
            AppLogger.Log("  Hotkeys unregistered before sleep.");

            _trackedWindows.Clear();
            foreach (var cts in _pending.Values) cts.Cancel();
            _pending.Clear();
            AppLogger.Log("  Tracked windows and pending tasks cleared.");
        }

        private async void OnWake()
        {
            try
            {
            AppLogger.Log("=== SYSTEM WAKE ===");

            await Task.Delay(3000);

            RegisterHotkeys();
            AppLogger.Log($"  Hotkeys re-registered: Add={_settings.HotkeyAddApp}, Refresh={_settings.HotkeyRefreshApp}");

            RefreshMonitors();
            AppLogger.Log($"  Monitors after wake: {string.Join(", ", _monitors.Select(m => m.ID))}");

            foreach (var g in _settings.Games.Where(g => g.IsActive))
            {
                AppLogger.Log($"  Active game: '{g.GameName}' profiles=[{string.Join(", ", g.Profiles.Keys)}]");
            }

            _enforceTimer.Start();
            _clipTimer.Start();
            AppLogger.Log("  Timers restarted.");

            int applied = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                    string title = GetWindowTitle(p.MainWindowHandle);
                    foreach (var g in _settings.Games)
                    {
                        if (!g.IsActive || !g.IsMatch(exe, title)) continue;
                        GetWindowRect(p.MainWindowHandle, out RECT r);
                        AppLogger.Log($"  Found running game '{g.GameName}' (hwnd={p.MainWindowHandle}, rect={r.Left},{r.Top},{r.Right},{r.Bottom})");
                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                        applied++;
                    }
                }
                catch { }
            }
            AppLogger.Log($"  Applied borderless to {applied} window(s) after wake.");
            AppLogger.Log($"  Tracked windows after wake: {_trackedWindows.Count}");

            if (_wasHiddenBeforeSleep && _settings.MinimizeToTray)
            {
                this.Hide();
                this.ShowInTaskbar = false;
                this.WindowState   = FormWindowState.Minimized;
                _webViewNeedsRepaintAfterWake = true;
                AppLogger.Log("  Form was hidden before sleep — re-hidden after wake.");
            }
            else
            {
                AppLogger.Log($"  Form visibility restored: visible={this.Visible}, taskbar={this.ShowInTaskbar}");

                if (this.Visible)
                    RepairWebViewAfterWake();
            }
            }
            catch (Exception ex) { AppLogger.Log(ex, "OnWake"); }
        }

        private static decimal Clamp(int value, int min, int max)
            => Math.Max(min, Math.Min(max, value));
    }
    internal static class NativeMethods
    {
        private const int SW_SHOWNOACTIVATE   = 4;
        private const int HWND_TOPMOST        = -1;
        private const uint SWP_NOACTIVATE     = 0x0010;
        private const uint SWP_NOMOVE         = 0x0002;
        private const uint SWP_NOSIZE         = 0x0001;

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);

        public static void ShowWindowNoActivate(IntPtr hWnd)
        {
            ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hWnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        }
    }
}
