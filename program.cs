// NoBorders - Borderless window manager
// Rewritten for clean tabbed UX, maintainability, and correctness.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using NoBorders.Services;

namespace NoBorders
{
    // ══════════════════════════════════════════════════════════════════════════════
    // DATA MODELS
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Per-monitor display profile for a single game.</summary>
    public class GameDisplayProfile
    {
        public int  Width         { get; set; } = 1920;
        public int  Height        { get; set; } = 1080;
        public int  OffsetX       { get; set; } = 0;
        public int  OffsetY       { get; set; } = 0;
        public bool ConstrainMouse { get; set; } = false;
    }

    /// <summary>
    /// Configuration for a single tracked game.
    /// Profiles is keyed by monitor friendly-name (e.g. "Samsung S34J55x").
    /// </summary>
    public class GameConfig
    {
        public string GameName     { get; set; } = string.Empty;
        public string RegexPattern { get; set; } = string.Empty;
        public string ExePath      { get; set; } = string.Empty;
        public bool   IsActive     { get; set; } = false;
        public Dictionary<string, GameDisplayProfile> Profiles { get; set; }
            = new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);

        // Compiled regex is built on first use and cached. [System.Text.Json.Serialization.JsonIgnore]
        // keeps it out of the saved config file.
        [System.Text.Json.Serialization.JsonIgnore]
        private Regex? _compiledPattern;

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
                            RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    }
                    catch
                    {
                        _compiledPattern = new Regex("(?!)", RegexOptions.Compiled); // never matches
                    }
                }
                return _compiledPattern ?? new Regex("(?!)", RegexOptions.Compiled);
            }
        }

        /// <summary>Call after editing RegexPattern so the cache is rebuilt.</summary>
        public void InvalidatePattern() => _compiledPattern = null;
    }

    public class HotkeyConfig
    {
        public uint Modifiers { get; set; }
        public uint Key       { get; set; }

        public override string ToString()
        {
            if (Key == 0) return "None";
            var parts = new List<string>();
            // Mask out MOD_NOREPEAT (0x4000) — it's an implementation detail,
            // not something the user set, so don't show it in the display.
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
        public List<GameConfig> Games              { get; set; } = new List<GameConfig>();
        public bool             MinimizeToTray     { get; set; } = true;
        public bool             StartWithWindows   { get; set; } = false;
        public bool             StartMinimized     { get; set; } = false;
        public HotkeyConfig     HotkeyAddApp       { get; set; } = new HotkeyConfig { Modifiers = 0x0002 | 0x0004 | 0x4000, Key = (uint)Keys.A };
        public HotkeyConfig     HotkeyRefreshApp   { get; set; } = new HotkeyConfig { Modifiers = 0x0002 | 0x0004 | 0x4000, Key = (uint)Keys.R };
    }

    public class MonitorItem
    {
        public string ID         { get; set; } = string.Empty; // friendly name shown to user
        public string DeviceName { get; set; } = string.Empty; // GDI device name e.g. \\.\DISPLAY1
        public override string ToString() => ID;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // LOGGER
    // ══════════════════════════════════════════════════════════════════════════════

    internal static class AppLogger
    {
        private static readonly string _path = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "noborders.log");

        public static void Log(string message)
        {
            try { File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n"); }
            catch { }
        }

        public static void Log(Exception ex, string context)
            => Log($"ERROR in {context}: {ex.Message}\n{ex.StackTrace}");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // NUMERIC TEXT BOX
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Digit-only text box used in place of NumericUpDown for width/height/offset
    /// fields. NumericUpDown's internal UpDownBase layout logic re-establishes the
    /// spinner region on any relayout (including the WM_SIZE-driven relayout that
    /// occurs on minimize/restore), which reintroduces a stale-pixel artifact
    /// regardless of any one-time Invalidate() call made at setup. TextBox has no
    /// such internal child-control layout, so the artifact cannot occur here.
    /// </summary>
    internal sealed class NumericTextBox : TextBox
    {
        // [DesignerSerializationVisibility(Hidden)] tells the WinForms designer-
        // serialization analyzer (WFO1000) that these properties are not meant to
        // be round-tripped through designer-generated code. They're only ever set
        // programmatically (SetupNumeric), so no serialization behavior is needed.
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

        // Filters both typed input and pasted text. Typed input alone could be
        // handled in OnKeyPress, but paste (Ctrl+V) bypasses OnKeyPress entirely,
        // so filtering is done here instead, on the resulting text.
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
            // Normalizes and clamps on focus loss — covers an empty box, a bare
            // "-", or an in-range-violating value left over from mid-edit typing.
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

    // ══════════════════════════════════════════════════════════════════════════════
    // ENTRY POINT
    // ══════════════════════════════════════════════════════════════════════════════

    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Exposed so MainForm can release the single-instance lock before
        // spawning an elevated copy of itself (see RestartAsAdmin). Without
        // releasing it first, the new elevated process would see the mutex
        // still held and assume another copy is already running.
        public static Mutex? AppMutex;

        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppMutex = new Mutex(true, "NoBordersManager_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                int wm = RegisterWindowMessage("WM_SHOWFIRSTINSTANCE_NOBORDERS");
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
                // Only release if we still own it — RestartAsAdmin may have
                // already released it in order to hand off to the elevated copy.
                try { AppMutex.ReleaseMutex(); } catch { }
                AppMutex.Dispose();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM
    // ══════════════════════════════════════════════════════════════════════════════

    internal sealed class MainForm : Form
    {
        // ── Win32 display-config structs ─────────────────────────────────────────
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

        // ── Win32 constants ──────────────────────────────────────────────────────
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
        private const int    PBT_APMSUSPEND         = 0x0004; // system going to sleep
        private const int    PBT_APMRESUMESUSPEND   = 0x0007; // woke from sleep
        private const int    PBT_APMRESUMEAUTOMATIC = 0x0012; // woke automatically (no user input yet)
        private const int    WM_DISPLAYCHANGE       = 0x007E;
        private const int    HSHELL_WINDOWCREATED   = 1;
        private const int    HOTKEY_ID_ADD          = 1001;
        private const int    HOTKEY_ID_REFRESH      = 1002;
        private const uint   MOD_ALT                = 0x0001;
        private const uint   MOD_CONTROL            = 0x0002;
        private const uint   MOD_SHIFT              = 0x0004;
        // MOD_NOREPEAT prevents WM_HOTKEY from firing repeatedly while the key
        // is held, and is always OR'd into registrations. When used alone it
        // allows bare function-key hotkeys without requiring Ctrl/Shift/Alt.
        private const uint   MOD_NOREPEAT           = 0x4000;
        private const string REG_RUN_KEY            = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME               = "NoBordersManager";

        // ── P/Invoke ─────────────────────────────────────────────────────────────
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

        // ── State ────────────────────────────────────────────────────────────────
        private AppSettings  _settings    = new AppSettings();
        private GameConfig?  _selectedGame;
        private bool         _forceClose  = false;
        private bool         _updatingUI  = false;
        private string       _activeScope = string.Empty; // currently viewed monitor scope

        private readonly string _configPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "games_config.json");

        // ── Process blocklist for "Add Running App" dialog ─────────────────────────
        // Any process whose name appears here will never show up in the game picker.
        // This covers the full range of Windows shell, UWP infrastructure, system
        // utility, accessibility, security, and driver companion processes that can
        // have visible window handles but are obviously not games.
        // Add entries here (lowercase, with .exe) as new system processes surface.
        private static readonly HashSet<string> _systemProcessBlocklist =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Windows Shell & Explorer ─────────────────────────────────────────
            "explorer.exe",                 // Windows shell / desktop
            "shellexperiencehost.exe",      // Start menu, Action Center shell
            "startmenuexperiencehost.exe",  // Start menu UWP host
            "searchhost.exe",               // Windows Search UI
            "searchapp.exe",                // Windows Search (older builds)
            "searchui.exe",                 // Cortana / Search (legacy)
            "cortana.exe",                  // Cortana assistant
            "applicationframehost.exe",     // UWP app window frame host
            "lockapp.exe",                  // Lock screen
            "logonui.exe",                  // Logon UI
            "winlogon.exe",                 // Windows logon process
            "userinit.exe",                 // User initialisation

            // ── Input & Accessibility ────────────────────────────────────────────
            "textinputhost.exe",            // Touch keyboard / Windows Input Service
            "ctfmon.exe",                   // Text input / IME manager
            "tabtip.exe",                   // Touch keyboard (legacy)
            "tabtip32.exe",                 // Touch keyboard 32-bit shim
            "osk.exe",                      // On-Screen Keyboard
            "magnify.exe",                  // Magnifier
            "narrator.exe",                 // Narrator screen reader
            "utilman.exe",                  // Ease of Access manager

            // ── System & Runtime Infrastructure ─────────────────────────────────
            "svchost.exe",                  // Service host
            "dllhost.exe",                  // COM surrogate / DLL host
            "runtimebroker.exe",            // UWP runtime broker
            "taskhostw.exe",                // Task host window
            "sihost.exe",                   // Shell infrastructure host
            "sidebarhost.exe",              // Windows Sidebar / Gadgets
            "smartscreen.exe",              // Windows SmartScreen
            "wuauclt.exe",                  // Windows Update agent
            "musnotification.exe",          // Windows Update notification
            "musnotificationux.exe",        // Windows Update notification UX
            "werfault.exe",                 // Windows Error Reporting
            "werfaultsecure.exe",           // Windows Error Reporting (secure)
            "dwm.exe",                      // Desktop Window Manager
            "fontdrvhost.exe",              // Font driver host
            "csrss.exe",                    // Client/Server Runtime process
            "lsass.exe",                    // Local Security Authority
            "services.exe",                 // Service Control Manager
            "spoolsv.exe",                  // Print spooler

            // ── UWP / Microsoft Store Apps ───────────────────────────────────────
            "systemsettings.exe",           // Settings app
            "winstore.app.exe",             // Microsoft Store
            "microsoftedge.exe",            // Edge (legacy)
            "msedge.exe",                   // Microsoft Edge (Chromium)
            "microsoftedgecp.exe",          // Edge content process
            "officecefloader.exe",          // Office CEF loader
            "msteams.exe",                  // Microsoft Teams (classic)
            "teams.exe",                    // Microsoft Teams (new)
            "onedrive.exe",                 // OneDrive sync client
            "calculator.exe",               // Calculator
            "mspaint.exe",                  // Paint (classic)
            "snippingtool.exe",             // Snipping Tool
            "stickynot.exe",                // Sticky Notes
            "notepad.exe",                  // Notepad

            // ── Security & Anticheat (launchers, not games) ──────────────────────
            "msseces.exe",                  // Microsoft Security Essentials
            "msascuil.exe",                 // Windows Defender tray
            "securityhealthsystray.exe",    // Windows Security tray
            "securityhealthservice.exe",    // Windows Security service
            "antimalwareservice.exe",       // Defender service (display name variant)

            // ── Driver & Hardware Companions ─────────────────────────────────────
            "nvcontainer.exe",              // NVIDIA container
            "nvdisplay.container.exe",      // NVIDIA display container
            "nvidia web helper.exe",        // NVIDIA web helper
            "nvspcaps64.exe",               // NVIDIA ShadowPlay capture
            "nvsphelper64.exe",             // NVIDIA helper
            "nvcplui.exe",                  // NVIDIA Control Panel (new)
            "nvtray.exe",                   // NVIDIA tray app
            "amdrsserv.exe",                // AMD Radeon Services
            "cccpushserver.exe",            // AMD Catalyst Control Center push
            "radioshim.exe",                // AMD Radeon shim
            "radeonsoftware.exe",           // AMD Radeon Software
            "igcctray.exe",                 // Intel Graphics Command Center tray
            "intelcphdcpsvc.exe",           // Intel HDCP service
            "lghub.exe",                    // Logitech G Hub
            "logioverlay.exe",              // Logitech overlay
            "corsairservice.exe",           // Corsair iCUE
            "icue.exe",                     // Corsair iCUE UI
            "razercentralservice.exe",      // Razer Central
            "razercentral.exe",             // Razer Central UI
            "steelseriesclientcore.exe",    // SteelSeries GG

            // ── Game Platform Launchers & Overlays (not the games themselves) ────
            "steamservice.exe",             // Steam service
            "steam.exe",                    // Steam client
            "steamwebhelper.exe",           // Steam web helper
            "gameoverlayui.exe",            // Steam overlay
            "epicgameslauncher.exe",        // Epic Games Launcher
            "galaxyclient.exe",             // GOG Galaxy
            "galaxyclient helper.exe",      // GOG Galaxy helper
            "eadesktop.exe",                // EA Desktop app
            "origin.exe",                   // EA Origin (legacy)
            "ubisoft connect.exe",          // Ubisoft Connect
            "ubisoftgamelauncher.exe",      // Ubisoft launcher
            "battlenet.exe",                // Battle.net launcher
            "gogdl.exe",                    // GOG downloader
            "bethesdanetlauncher.exe",      // Bethesda launcher
            "rockstarservice.exe",          // Rockstar launcher service

            // ── Anticheat (standalone processes, not game windows) ───────────────
            "easyanticheat.exe",            // Easy Anti-Cheat service
            "battleye.exe",                 // BattlEye service
            "vgc.exe",                      // Vanguard anticheat (Valorant)
            "vgtray.exe",                   // Vanguard tray
            "faceitclient.exe",             // FACEIT anti-cheat

            // ── Remote Desktop & Streaming ───────────────────────────────────────
            "rdpclip.exe",                  // RDP clipboard
            "termsrv.exe",                  // Terminal Services
            "parsec.exe",                   // Parsec remote desktop
            "sunshine.exe",                 // Sunshine game streaming server

            // ── Screen Capture & Recording ───────────────────────────────────────
            "obs64.exe",                    // OBS Studio
            "obs32.exe",                    // OBS Studio (32-bit)
        };

        private readonly Dictionary<string, Image>     _iconCache      = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MonitorItem>             _monitors       = new List<MonitorItem>();
        // Foreground window captured in WndProc before the hotkey is dispatched.
        // GetForegroundWindow() called *inside* HotkeyAdd() can return our own
        // window handle by the time WM_HOTKEY is processed.
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private bool   _wasHiddenBeforeSleep = false; // tracks tray state across sleep/wake
        private bool   _isElevated = false; // true if this process is running as Administrator
        // Games we've already shown an elevation-related toast for this session,
        // so the warning fires once per game rather than every enforcement tick.
        private readonly HashSet<string> _elevationWarnedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Hotkey capture session state ────────────────────────────────────────
        // Set while the user has clicked "Set Hotkey" and the app is waiting for
        // a new key combination. While non-zero, the corresponding global hotkey
        // is unregistered, so pressing the current (or any) combination during
        // capture only feeds the capture UI and can never also fire the global
        // Add/Refresh action underneath it.
        private int           _capturingHotkeyId      = 0; // 0 = not capturing
        private HotkeyConfig? _capturingHotkeyConfig  = null;
        private TextBox?      _capturingHotkeyDisplay = null;
        private Button?       _capturingHotkeyButton  = null;
        private string        _capturingHotkeyLabel   = string.Empty;

        private readonly Dictionary<IntPtr, GameConfig> _trackedWindows = new Dictionary<IntPtr, GameConfig>();
        private readonly Dictionary<IntPtr, CancellationTokenSource> _pending = new Dictionary<IntPtr, CancellationTokenSource>();

        // Games whose process is currently detected running (a process with a
        // visible window matching the game's pattern exists), independent of
        // the Borderless on/off toggle. Used to bubble currently-running
        // entries to the top of the games list for quick access.
        private HashSet<GameConfig> _runningGames = new HashSet<GameConfig>();

        private int _wmShellHook;
        private int _wmShowFirst;

        // ── Timers ───────────────────────────────────────────────────────────────
        private readonly System.Windows.Forms.Timer _enforceTimer   = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _clipTimer      = new System.Windows.Forms.Timer { Interval = 100 };
        private readonly System.Windows.Forms.Timer _statusTimer    = new System.Windows.Forms.Timer { Interval = 2500 };
        private readonly System.Windows.Forms.Timer _saveDebounce   = new System.Windows.Forms.Timer { Interval = 600 };

        // ── Tray ─────────────────────────────────────────────────────────────────
        private readonly NotifyIcon       _trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();

        // ── UI controls — Games tab ───────────────────────────────────────────────
        private readonly TabControl      _tabs            = new TabControl();
        private readonly ListBox         _lstGames        = new ListBox();
        private readonly Button          _btnAddRunning   = new Button();
        private readonly Button          _btnAddBrowse    = new Button();
        private readonly Panel           _pnlDetail       = new Panel();
        private readonly Label           _lblDetailHint   = new Label();
        // game detail
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
        // status bar
        private readonly Label           _lblStatus       = new Label();

        // ── UI controls — Settings tab ───────────────────────────────────────────
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

        // ── Blazor ───────────────────────────────────────────────────────────────
        // DI container for the BlazorWebView control. `CreateBlazorServices()` now
        // registers AppStateService against the live _settings field/QueueSave/
        // SaveConfig, which requires instance context (`this`) — C# doesn't allow
        // field initializers to call instance methods (CS0236), so unlike Phase
        // 1.2's original version, this can no longer be built via a field
        // initializer. Instead it's assigned at the very end of the constructor
        // body (see below), appended after the existing 7-call flow rather than
        // interleaved with it — equally non-disruptive to that ordering, since
        // "appended after" can't reorder what came before it either.
        private readonly ServiceProvider _blazorServices;

        private ServiceProvider CreateBlazorServices()
        {
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            // Lazy factory — only runs whenever something first resolves
            // AppStateService, which happens long after LoadConfig() has already
            // run, and the Func<AppSettings> accessor re-reads _settings on every
            // access rather than capturing a snapshot, so this is correct
            // regardless of exact timing.
            services.AddSingleton(_ => new AppStateService(() => _settings, QueueSave, SaveConfig));
            return services.BuildServiceProvider();
        }

        // ════════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════════
        public MainForm()
        {
            // Constructor does pure setup only — no window handle access, no
            // Invoke/BeginInvoke, no async work. Everything that needs the handle
            // lives in OnLoad, which fires after the handle is fully created.
            CheckElevation(); // must run before BuildUI so the title/labels can reflect it
            LoadConfig();
            BuildUI();
            SetupTrayIcon();
            RefreshMonitors();
            PopulateGamesList();
            ApplyTheme();

            _enforceTimer.Tick  += EnforceTimer_Tick;
            _clipTimer.Tick     += ClipTimer_Tick;
            _statusTimer.Tick   += (s, e) => { _statusTimer.Stop(); _lblStatus.Text = string.Empty; };
            _saveDebounce.Tick  += (s, e) => { _saveDebounce.Stop(); SaveConfig(); };

            this.Resize      += OnResize;
            this.FormClosing += OnFormClosing;
            this.Load        += OnLoad;

            // Appended after the flow above rather than a field initializer — see
            // the comment on the _blazorServices field for why.
            _blazorServices = CreateBlazorServices();
        }

        /// <summary>
        /// Fires after the window handle is fully created and the form is ready
        /// to be shown. Safe to call Invoke, BeginInvoke, and Win32 APIs that
        /// require a valid HWND (RegisterShellHookWindow, RegisterHotKey etc.).
        /// </summary>
        private void OnLoad(object? sender, EventArgs e)
        {
            // Register window messages and shell hook — requires valid this.Handle.
            try
            {
                _wmShellHook = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(this.Handle);
                _wmShowFirst = RegisterWindowMessage("WM_SHOWFIRSTINSTANCE_NOBORDERS");
            }
            catch (Exception ex) { AppLogger.Log(ex, "RegisterShellHookWindow"); }

            // Global hotkeys — requires valid this.Handle. Must run here rather
            // than the constructor: registering before the form's message loop
            // is actually pumping (Application.Run) does not reliably stick,
            // which is why hotkeys previously needed a manual re-bind from
            // Settings (that re-registration happens post-load and works).
            RegisterHotkeys();

            // Dark/light native title bar — needs a valid window handle.
            ApplyTitleBarTheme();

            // Start timers only once the form is live.
            _enforceTimer.Start();
            _clipTimer.Start();

            // Scan processes already running before we started.
            ScanExistingWindows();

            // Handle -minimized launch argument (Start with Windows minimized).
            // Must be here rather than the constructor so BeginInvoke is safe.
            if (Environment.GetCommandLineArgs().Contains("-minimized", StringComparer.OrdinalIgnoreCase))
            {
                this.WindowState   = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                BeginInvoke(new MethodInvoker(this.Hide));
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // DISPOSE
        // ════════════════════════════════════════════════════════════════════════
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

        // ════════════════════════════════════════════════════════════════════════
        // UI CONSTRUCTION
        // ════════════════════════════════════════════════════════════════════════

        // Custom navigation bar replacing the native tab header. The native
        // TabControl header strip is painted by Windows and cannot be recolored,
        // which caused the glaring white strip in dark mode.
        private readonly Panel  _pnlNav        = new Panel();
        private readonly Button _btnNavGames   = new Button();
        private readonly Button _btnNavSettings = new Button();

        // DWM attribute to make the native title bar dark (Windows 10 1809+).
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>Detects whether this process is running with Administrator privileges.</summary>
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
                _isElevated = false; // assume the worse case if detection itself fails
            }
        }

        /// <summary>
        /// Relaunches NoBorders elevated via the UAC "runas" verb, then exits this
        /// (non-elevated) instance. The single-instance mutex is released first so
        /// the new elevated process doesn't think another copy is already running.
        /// </summary>
        private void RestartAsAdmin()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                AppLogger.Log($"RestartAsAdmin: Application.ExecutablePath = '{exePath}'");

                // Guard against the classic "dotnet host" trap: if NoBorders was
                // launched via `dotnet NoBorders.dll` (e.g. framework-dependent
                // deployment, or certain IDE debug launch configurations),
                // Application.ExecutablePath can resolve to dotnet.exe itself
                // rather than the actual app. Relaunching dotnet.exe with runas
                // and no arguments opens nothing visible — which matches
                // "the app closes but never reopens".
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

                // Preserve any launch arguments (e.g. -minimized) on the relaunch.
                string args = string.Join(" ",
                    Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\""));
                AppLogger.Log($"RestartAsAdmin: relaunch args = '{args}'");

                var psi = new ProcessStartInfo(exePath, args)
                {
                    UseShellExecute  = true,
                    Verb             = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };

                // Flush any pending debounced save immediately so the new elevated
                // instance loads fully up-to-date settings rather than whatever was
                // on disk before the last 600ms autosave window elapsed.
                _saveDebounce.Stop();
                SaveConfig();
                AppLogger.Log("RestartAsAdmin: config flushed to disk before restart.");

                // Release the single-instance lock BEFORE spawning the new process,
                // otherwise the elevated copy would see it still held and simply
                // bring this (soon-to-close) window to the front instead of starting.
                try
                {
                    Program.AppMutex?.ReleaseMutex();
                    AppLogger.Log("RestartAsAdmin: single-instance mutex released.");
                }
                catch (Exception relEx)
                {
                    AppLogger.Log(relEx, "RestartAsAdmin: mutex release (may already be released, non-fatal)");
                }

                Process? proc = Process.Start(psi);

                // Process.Start can return null if no new process resource was
                // actually created. If that happens, DO NOT close this instance —
                // there would be nothing left running at all.
                if (proc == null)
                {
                    AppLogger.Log("RestartAsAdmin: Process.Start returned null — the elevated instance did not launch. Keeping this instance open.");
                    ShowStatus("Restart may have failed — check noborders.log for details.");
                    return;
                }

                AppLogger.Log($"RestartAsAdmin: elevated process launched successfully, PID={proc.Id}. Closing this instance.");
                _forceClose = true;
                Application.Exit();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — user clicked "No" on the UAC prompt.
                AppLogger.Log("RestartAsAdmin: user declined the UAC prompt (ERROR_CANCELLED).");
                ShowStatus("Elevation cancelled.");
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "RestartAsAdmin");
                ShowStatus("Couldn't restart as Administrator — see noborders.log for details.");
            }
        }

        /// <summary>
        /// Shown once, right after a newly added game's borderless settings
        /// failed to apply — which ApplyBorderless records via
        /// _elevationWarnedGames the first time a style/position Win32 call
        /// fails while NoBorders isn't elevated. That combination almost always
        /// means the game (or its anti-cheat) runs elevated and NoBorders
        /// currently doesn't, so this offers the same restart already available
        /// from Settings → Restart as Administrator, right at the point it's
        /// actually needed instead of leaving the user to notice a toast or dig
        /// through the log.
        /// </summary>
        private void PromptRestartAsAdmin(string gameName)
        {
            var result = ShowTopmostMessageBox(
                $"NoBorders added \"{gameName}\", but couldn't fully apply borderless " +
                "mode to its window.\n\n" +
                "This usually means the game (or its anti-cheat) is running with " +
                "Administrator privileges while NoBorders is not — Windows blocks a " +
                "non-elevated app from modifying an elevated window.\n\n" +
                "Restart NoBorders as Administrator now?",
                "Administrator Privileges Needed",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Yes) RestartAsAdmin();
        }

        /// <summary>
        /// Shows a modal MessageBox guaranteed to appear above other topmost
        /// windows — including a fullscreen/exclusive game — regardless of
        /// whether the main NoBorders window currently has focus or is hidden
        /// to tray. Owning the dialog with `this` (the usual pattern) only
        /// brings it above windows already behind the main form; a dialog
        /// owned by a genuinely TopMost window is itself shown as topmost, so
        /// this briefly creates an invisible topmost owner instead.
        /// </summary>
        private DialogResult ShowTopmostMessageBox(string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        {
            using var owner = new Form
            {
                TopMost         = true,
                ShowInTaskbar   = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition   = FormStartPosition.Manual,
                Location        = new Point(-32000, -32000), // off-screen; never actually visible
                Size            = new Size(1, 1),
                Opacity         = 0
            };
            owner.Show();
            owner.Activate();
            try
            {
                return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
            }
            finally
            {
                owner.Close();
            }
        }

        private void ApplyTitleBarTheme()
        {
            try
            {
                // Single dark palette app-wide — always request the dark immersive
                // title bar so the native chrome stays consistent with it.
                int useDark = 1;
                DwmSetWindowAttribute(this.Handle,
                    DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch { /* pre-1809 Windows — harmless */ }
        }

        private void BuildUI()
        {
            this.Text            = _isElevated ? "NoBorders (Administrator)" : "NoBorders";
            this.Size            = new Size(1200, 800);
            // Was fixed-width (800/800) for the WinForms UI alone. Widened and made
            // freely resizable so the window is comfortably large enough by default
            // for both the current Settings tab content (previously needed a manual
            // resize to see the Permissions section without scrolling) and the
            // Blazor screens being built in MIGRATION_PLAN.md Phase 2, which are
            // authored at 1120px wide per design-handoff/README.md and aren't
            // responsive below that yet.
            this.MinimumSize     = new Size(1150, 720);
            this.MaximumSize     = Size.Empty; // no maximum — freely resizable/maximizable
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.Font            = new Font("Segoe UI", 9f);
            this.Icon            = TryExtractIcon(Application.ExecutablePath);

            // Status bar — thin, sits below the tab content
            _lblStatus.Dock      = DockStyle.Bottom;
            _lblStatus.Height    = 24;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding   = new Padding(10, 0, 0, 0);
            _lblStatus.Font      = new Font("Segoe UI", 8f);

            // ── Custom nav bar ───────────────────────────────────────────────────
            _pnlNav.Dock   = DockStyle.Top;
            _pnlNav.Height = 42;
            _pnlNav.Padding = new Padding(10, 8, 0, 0);

            ConfigureNavButton(_btnNavGames,    "Games",    0);
            ConfigureNavButton(_btnNavSettings, "Settings", 1);
            _btnNavGames.Location    = new Point(10, 8);
            _btnNavSettings.Location = new Point(104, 8);
            _pnlNav.Controls.Add(_btnNavGames);
            _pnlNav.Controls.Add(_btnNavSettings);

            // ── TabControl with hidden native header ─────────────────────────────
            // FlatButtons + 1px items effectively hides the native strip entirely.
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

        /// <summary>Restyles the nav buttons to reflect the selected tab.</summary>
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

        // ── Games tab ────────────────────────────────────────────────────────────

        private void BuildGamesTab(TabPage page)
        {
            // ── Left column ────────────────────────────────────────────────────
            var pnlLeft = new Panel
            {
                Dock    = DockStyle.Left,
                Width   = 220,
                Padding = new Padding(8, 8, 8, 6)
            };
            // Paint a right-side border on the left panel to act as a divider
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

            // Add buttons stacked at the bottom
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

            // ── Right column ───────────────────────────────────────────────────
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

            // ── Title row: game name + Borderless toggle ─────────────────────────
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

            // ── Monitor selector ─────────────────────────────────────────────────
            _cmbMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMonitor.Width         = 270;
            _cmbMonitor.Height        = 26;
            _cmbMonitor.SelectedIndexChanged += CmbMonitor_SelectedIndexChanged;
            tbl.Controls.Add(MakeDetailRow("Monitor", _cmbMonitor));

            // ── Width / Height ───────────────────────────────────────────────────
            SetupNumeric(_numWidth,  0, 16384); _numWidth.Width  = 108;
            SetupNumeric(_numHeight, 0, 16384); _numHeight.Width = 108;
            tbl.Controls.Add(MakeDetailRow("Width / Height",
                _numWidth,
                MakeSeparatorLabel("×"),
                _numHeight));

            // ── Offset X / Y ─────────────────────────────────────────────────────
            SetupNumeric(_numOffsetX, -16384, 16384); _numOffsetX.Width = 108;
            SetupNumeric(_numOffsetY, -16384, 16384); _numOffsetY.Width = 108;
            tbl.Controls.Add(MakeDetailRow("Offset X / Y",
                _numOffsetX,
                MakeSeparatorLabel("×"),
                _numOffsetY));

            // ── Constrain mouse ──────────────────────────────────────────────────
            _chkConstrain.Text     = "Lock mouse cursor to window bounds";
            _chkConstrain.AutoSize = true;
            tbl.Controls.Add(MakeDetailRow(string.Empty, _chkConstrain));

            // ── Action buttons ───────────────────────────────────────────────────
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

            // ── Advanced collapsible section ─────────────────────────────────────
            // Structure: outer Panel (_grpAdvanced) contains a header label
            // (DockStyle.Top) and a content panel (DockStyle.Top, hidden when
            // collapsed). The border is drawn via a Paint handler on the OUTER
            // panel AFTER children paint, using e.Graphics with WM_PAINT clipping.
            // To ensure the border draws on top of children, we use the
            // WS_EX_TRANSPARENT trick — instead, we simply call Invalidate after
            // any child change and paint last via the panel's own Paint event.
            // The key fix: BorderStyle.FixedSingle is not used (it ignores color);
            // instead the Paint handler draws the rect LAST so it's always on top.

            // Header label — click area + title
            _lblAdvancedHdr.Text      = "▶  Advanced";
            _lblAdvancedHdr.AutoSize  = false;
            _lblAdvancedHdr.Height    = 28;
            _lblAdvancedHdr.Dock      = DockStyle.Top;
            _lblAdvancedHdr.TextAlign = ContentAlignment.MiddleLeft;
            _lblAdvancedHdr.Cursor    = Cursors.Hand;
            _lblAdvancedHdr.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblAdvancedHdr.Padding   = new Padding(8, 0, 0, 0);
            _lblAdvancedHdr.Click    += (s, e) => ToggleAdvanced();

            // Content panel (shown when expanded)
            var pnlAdvContent = new Panel
            {
                Dock    = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 6)
            };

            _txtGameName.Width = 220;
            _txtRegex.Width    = 310;

            // Fix 3: icon-only fetch button — tooltip explains the action
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

            // Outer container — fixed width so the border paint rect is stable
            _grpAdvanced.AutoSize  = false;
            _grpAdvanced.Width     = 500;
            _grpAdvanced.Height    = 30;   // collapsed: header height + border
            _grpAdvanced.Margin    = new Padding(0, 4, 0, 4);
            // 1px padding insets docked children so the painted border on the
            // outer panel remains visible on all four edges.
            _grpAdvanced.Padding   = new Padding(1);
            _grpAdvanced.Cursor    = Cursors.Hand;
            _grpAdvanced.Click    += (s, e) => ToggleAdvanced();
            _grpAdvanced.Paint    += GrpAdvanced_Paint;
            // Add content BEFORE header so DockStyle.Top stacks header on top
            _grpAdvanced.Controls.Add(pnlAdvContent);
            _grpAdvanced.Controls.Add(_lblAdvancedHdr);

            // Store reference to content panel for show/hide on toggle
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
                // Height = border(1) + header(28) + content + border(1)
                _grpAdvanced.Height = expanding
                    ? 30 + pnlContent.PreferredSize.Height
                    : 30;
            }
            _grpAdvanced.Invalidate();
        }

        /// <summary>Sets the Borderless toggle button colour — green ON, muted grey OFF.</summary>
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

        // ── Settings tab ─────────────────────────────────────────────────────────

        private void BuildSettingsTab(TabPage page)
        {
            // Wrap the settings content in a scrollable panel so it is never
            // clipped when the window is small. AutoScroll adds a scrollbar only
            // when the content is taller than the visible area.
            var scrollPanel = new Panel
            {
                Dock       = DockStyle.Fill,
                AutoScroll = true,
                Padding    = Padding.Empty
            };

            // Dock = Top makes the table width track the scroll panel width,
            // eliminating the horizontal scrollbar; AutoSize height enables
            // vertical scrolling only when content exceeds the visible area.
            var tbl = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize    = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock        = DockStyle.Top,
                Padding     = new Padding(20, 12, 20, 12)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            // ── Monitor defaults section ─────────────────────────────────────────
            tbl.Controls.Add(MakeSectionHeader("Monitor Baseline Defaults"));

            var pnlMon = MakeSettingsRow("Monitor:");
            _cmbSetMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSetMonitor.Width = 280;
            _cmbSetMonitor.SelectedIndexChanged += CmbSetMonitor_SelectedIndexChanged;
            pnlMon.Controls.Add(_cmbSetMonitor);

            // Delete button — removes a monitor that is no longer connected
            // (e.g. an old monitor that was replaced, or a temporary display).
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

            // ── Hotkeys section ──────────────────────────────────────────────────
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

            // ── Behaviour section ────────────────────────────────────────────────
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
            _chkStartMin.Margin   = new Padding(28, 0, 0, 0); // indent as sub-option
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

            // ── Permissions section ──────────────────────────────────────────────
            // Games with anti-cheat (Vanguard, EAC, BattlEye) run elevated, and a
            // non-elevated NoBorders can silently fail to modify their windows.
            // This section shows current status and offers a one-click fix.
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
            _btnRestartAdmin.Visible = !_isElevated; // nothing to do if already elevated
            _btnRestartAdmin.Click  += (s, e) =>
            {
                var confirm = MessageBox.Show(
                    "NoBorders will restart with Administrator privileges.\n\n" +
                    "This helps with games that run elevated (common with anti-cheat " +
                    "software like Vanguard, EAC, or BattlEye), which a non-elevated " +
                    "copy of NoBorders cannot modify.\n\nContinue?",
                    "Restart as Administrator",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm == DialogResult.Yes) RestartAsAdmin();
            };
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

        // ── Layout helpers ────────────────────────────────────────────────────────

        /// <summary>Small muted separator label between paired controls (e.g. the × between W/H).</summary>
        private Label MakeSeparatorLabel(string text) => new Label
        {
            Text      = text,
            Width     = 20,
            Height    = 26,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = PaletteTextMuted,
            BackColor = Color.Transparent
        };

        /// <summary>Creates a left-label + right-controls FlowLayoutPanel row for the detail panel.</summary>
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


        /// <summary>Absolute-position row panel inside an arbitrary parent panel.</summary>
        // MakeRowIn removed — advanced panel now uses TableLayoutPanel directly.


        /// <summary>Left-label row for the settings tab.</summary>
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
                // BackColor set by PaintControlsDark based on PaletteBorder
            };
        }

        // SetFlowLayout stub removed — no longer needed.


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
            n.Value   = Math.Max(min, Math.Min(max, 0)); // initializes Text consistently
        }

        // ════════════════════════════════════════════════════════════════════════
        // TRAY ICON
        // ════════════════════════════════════════════════════════════════════════

        private void SetupTrayIcon()
        {
            var menuOpen = new ToolStripMenuItem("Open NoBorders");
            menuOpen.Click += (s, e) => RestoreFromTray();

            var menuExit = new ToolStripMenuItem("Exit");
            menuExit.Click += (s, e) => { _forceClose = true; Application.Exit(); };

            _trayMenu.Items.AddRange(new ToolStripItem[] { menuOpen, new ToolStripSeparator(), menuExit });
            _trayIcon.Text             = "NoBorders";
            _trayIcon.Icon             = TryExtractIcon(Application.ExecutablePath);
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            _trayIcon.Visible          = true;

            // -minimized handling moved to OnLoad so BeginInvoke has a valid handle.
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.ShowInTaskbar = true;
            this.WindowState   = FormWindowState.Normal;
            this.Activate();
            SetForegroundWindow(this.Handle);
        }

        private static Icon TryExtractIcon(string path)
        {
            try { return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }

        // ════════════════════════════════════════════════════════════════════════
        // PALETTE — single fixed dark palette, applied once at startup. There is
        // no light mode and no system-theme following; the design bundle is
        // dark-only, so there's nothing to switch between.
        // ════════════════════════════════════════════════════════════════════════

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

            // Nav bar
            _pnlNav.BackColor = PaletteBase;
            UpdateNavButtons();

            // Advanced section header + content panel (special surfaces)
            _lblAdvancedHdr.BackColor = Color.FromArgb(38, 38, 42);
            _lblAdvancedHdr.ForeColor = PaletteTextPrimary;
            if (_grpAdvanced.Tag is Panel advContent)
                advContent.BackColor = PaletteSurface;

            PaintControlsDark(this.Controls);

            // Re-apply the special colors that PaintControlsDark's generic cases
            // would have overwritten (it sets all Labels/Panels to Base).
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

            // Elevation status label — green when elevated, muted otherwise.
            // Re-applied here since the generic Label case in PaintControlsDark
            // would otherwise reset it to plain TextSecondary/TextPrimary.
            _lblElevationStatus.BackColor = PaletteBase;
            _lblElevationStatus.ForeColor = _isElevated
                ? Color.FromArgb(72, 199, 72)
                : PaletteTextSecondary;

            // Hotkey status labels — color reflects live registration state
            // (set by SetHotkeyStatusLabel), so only the background is reset
            // here; ForeColor is deliberately left alone.
            _lblHotkeyAddStatus.BackColor     = PaletteBase;
            _lblHotkeyRefreshStatus.BackColor = PaletteBase;

            UpdateNavButtons();

            // Dark native title bar (no-op before Windows 10 1809)
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
                    // ── Containers — recurse ──────────────────────────────────────
                    case TabControl tc:
                        tc.BackColor  = PaletteBase;
                        tc.ForeColor  = PaletteTextPrimary;
                        // Force tab strip repaint by toggling Padding
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
                        // 1px separator panels use border color
                        pnl.BackColor = pnl.Height == 1 ? PaletteBorder : PaletteBase;
                        pnl.Invalidate();
                        PaintControlsDark(pnl.Controls);
                        break;

                    // ── Interactive controls ──────────────────────────────────────
                    case ListBox lst:
                        lst.BackColor  = PaletteSurface;
                        lst.ForeColor  = PaletteTextPrimary;
                        lst.BorderStyle = BorderStyle.None; // border painted by parent panel
                        break;

                    case ComboBox cmb:
                        cmb.DrawMode  = DrawMode.Normal;
                        cmb.BackColor = PaletteSurface;
                        cmb.ForeColor = PaletteTextPrimary;
                        break;

                    case NumericTextBox ntb:
                        // Must precede the TextBox case below — NumericTextBox
                        // derives from TextBox and would otherwise be matched
                        // by that case first.
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
                        // All buttons use FlatStyle so we control every pixel
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
                            ? PaletteAccentFg   // handled by UpdateActiveButton
                            : PaletteTextPrimary;
                        chk.FlatStyle = FlatStyle.Flat;
                        if (chk.Appearance == Appearance.Normal)
                        {
                            chk.FlatAppearance.BorderColor        = PaletteBorderStrong;
                            chk.FlatAppearance.CheckedBackColor   = PaletteAccent;
                            chk.FlatAppearance.MouseOverBackColor = Color.Transparent;
                        }
                        break;

                    // ── Labels ───────────────────────────────────────────────────
                    case Label lbl:
                        if (lbl == _lblDetailHint || lbl == _lblGameTitle ||
                            lbl == _lblHotkeyAddStatus || lbl == _lblHotkeyRefreshStatus) break;
                        // Section headers (bold) use primary text; others secondary
                        lbl.BackColor = PaletteBase;
                        lbl.ForeColor = lbl.Font.Bold ? PaletteTextPrimary : PaletteTextSecondary;
                        break;
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // MONITOR HELPERS
        // ════════════════════════════════════════════════════════════════════════

        private void RefreshMonitors()
        {
            try
            {
                _monitors.Clear();
                var gdiMap    = BuildGdiToFriendlyMap();
                var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var screen in Screen.AllScreens)
                {
                    string gdi = TrimNull(screen.DeviceName);
                    if (!gdiMap.TryGetValue(gdi, out string? friendly) || string.IsNullOrWhiteSpace(friendly))
                        friendly = gdi;

                    if (seenNames.TryGetValue(friendly, out int n))
                    {
                        seenNames[friendly] = n + 1;
                        friendly = $"{friendly} ({n + 1})";
                    }
                    else seenNames[friendly] = 1;

                    _monitors.Add(new MonitorItem { ID = friendly, DeviceName = screen.DeviceName });
                    if (!IsIgnoredName(friendly)) _settings.KnownMonitors.Add(friendly);
                }

                SyncMonitorComboBoxes();
            }
            catch (Exception ex) { AppLogger.Log(ex, "RefreshMonitors"); }
        }

        private void SyncMonitorComboBoxes()
        {
            _settings.KnownMonitors.RemoveWhere(IsIgnoredName);

            var all = _monitors.Select(m => m.ID).ToList();
            foreach (var k in _settings.KnownMonitors)
                if (!all.Contains(k, StringComparer.OrdinalIgnoreCase))
                    all.Add(k);

            // Game detail monitor combobox
            string prevDetail = _cmbMonitor.SelectedItem?.ToString() ?? string.Empty;
            _cmbMonitor.Items.Clear();
            _cmbMonitor.Items.AddRange(all.ToArray());
            RestoreComboSelection(_cmbMonitor, prevDetail);

            // Settings monitor combobox
            string prevSet = _cmbSetMonitor.SelectedItem?.ToString() ?? string.Empty;
            _cmbSetMonitor.Items.Clear();
            _cmbSetMonitor.Items.AddRange(all.ToArray());
            RestoreComboSelection(_cmbSetMonitor, prevSet);
        }

        private static void RestoreComboSelection(ComboBox cmb, string previous)
        {
            if (cmb.Items.Count == 0) return;
            if (!string.IsNullOrEmpty(previous) && cmb.Items.Contains(previous))
                cmb.SelectedItem = previous;
            else
                cmb.SelectedIndex = 0;
        }

        private string GetPrimaryMonitorId()
        {
            string gdi = TrimNull(Screen.PrimaryScreen?.DeviceName ?? string.Empty);
            return _monitors.FirstOrDefault(m =>
                TrimNull(m.DeviceName).Equals(gdi, StringComparison.OrdinalIgnoreCase))?.ID
                ?? string.Empty;
        }

        private Dictionary<string, string> BuildGdiToFriendlyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nP, out uint nM) != 0)
                    return map;

                var paths = new DISPLAYCONFIG_PATH_INFO[nP];
                var modes = new DISPLAYCONFIG_MODE_INFO[nM];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nP, paths, ref nM, modes, IntPtr.Zero) != 0)
                    return map;

                for (int i = 0; i < nP; i++)
                {
                    var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = DCDI_GET_SOURCE_NAME,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                            adapterId = paths[i].sourceInfo.adapterId,
                            id        = paths[i].sourceInfo.id
                        }
                    };
                    var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = DCDI_GET_TARGET_NAME,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                            adapterId = paths[i].targetInfo.adapterId,
                            id        = paths[i].targetInfo.id
                        }
                    };

                    if (DisplayConfigGetDeviceInfo(ref src) == 0 &&
                        DisplayConfigGetDeviceInfo(ref tgt) == 0)
                    {
                        string gdi = TrimNull(src.viewGdiDeviceName);
                        string friendly = TrimNull(tgt.monitorFriendlyDeviceName);
                        if (string.IsNullOrEmpty(friendly))
                            friendly = $"Monitor_{tgt.edidManufactureId:X4}_{tgt.edidProductCodeId:X4}";
                        if (!string.IsNullOrEmpty(gdi)) map[gdi] = friendly;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Log(ex, "BuildGdiToFriendlyMap"); }
            return map;
        }

        private static string TrimNull(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int i = s.IndexOf('\0');
            return (i >= 0 ? s[..i] : s).Trim();
        }

        private static bool IsIgnoredName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.StartsWith(@"\\.\DISPLAY",    StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("Generic PnP",     StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("Generic Non-PnP", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ════════════════════════════════════════════════════════════════════════
        // CONFIG
        // ════════════════════════════════════════════════════════════════════════

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(_configPath)) return;
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath));
                if (loaded == null) return;
                _settings = loaded;

                _settings.MonitorDefaults ??= new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);
                _settings.Games           ??= new List<GameConfig>();
                _settings.HotkeyAddApp    ??= new HotkeyConfig { Modifiers = MOD_CONTROL | MOD_SHIFT, Key = (uint)Keys.A };
                _settings.HotkeyRefreshApp ??= new HotkeyConfig { Modifiers = MOD_CONTROL | MOD_SHIFT, Key = (uint)Keys.R };

                foreach (var g in _settings.Games)
                {
                    g.Profiles ??= new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in g.Profiles.Keys)
                        if (!IsIgnoredName(k)) _settings.KnownMonitors.Add(k);
                }

                // Sort immediately after load so the list is alphabetical
                // from the very first frame, before PopulateGamesList runs.
                SortGames();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "LoadConfig");
                _settings = new AppSettings();
            }
        }

        private void SaveConfig()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_configPath, JsonSerializer.Serialize(_settings, opts));
            }
            catch (Exception ex) { AppLogger.Log(ex, "SaveConfig"); }
        }

        private void QueueSave()
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        // ════════════════════════════════════════════════════════════════════════
        // GAMES LIST
        // ════════════════════════════════════════════════════════════════════════

        private void GrpAdvanced_Paint(object? sender, PaintEventArgs e)
        {
            // With Padding(1) on the panel, docked children are inset by 1px,
            // so this border painted on the parent stays visible on all edges.
            var g = e.Graphics;

            // Fill any exposed background (the 1px frame + area below content)
            using (var bgBrush = new SolidBrush(PaletteSurface))
                g.FillRectangle(bgBrush, 0, 0, _grpAdvanced.Width, _grpAdvanced.Height);

            using var borderPen = new Pen(PaletteBorderStrong, 1);
            g.DrawRectangle(borderPen, 0, 0, _grpAdvanced.Width - 1, _grpAdvanced.Height - 1);
        }

        /// <summary>Sorts the games list in-place. Running games (process currently
        /// detected, regardless of the Borderless on/off toggle) are placed
        /// before non-running games. Within each of those two groups, sorting is
        /// culture-aware and case-insensitive (CurrentCultureIgnoreCase handles
        /// special characters such as ™ and accents more naturally than
        /// OrdinalIgnoreCase).</summary>
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

        /// <summary>
        /// Rescans running processes to determine which configured games are
        /// currently running — i.e. have a process with a visible window whose
        /// exe name matches the game's pattern — regardless of whether the
        /// Borderless toggle is on for that game. If the set of running games
        /// changed since the last check, the games list is re-sorted and rebuilt
        /// so running games move to the top, with the current selection
        /// preserved.
        /// </summary>
        private void RefreshRunningGames()
        {
            var running = new HashSet<GameConfig>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    foreach (var g in _settings.Games)
                    {
                        if (running.Contains(g)) continue;
                        if (g.CompiledPattern.IsMatch(exe)) running.Add(g);
                    }
                }
                catch { /* process may have exited */ }
            }

            if (!running.SetEquals(_runningGames))
            {
                _runningGames = running;
                RefreshGamesListOrder();
            }
        }

        /// <summary>
        /// Re-sorts _settings.Games and rebuilds the games ListBox contents to
        /// reflect the new order (running games first), preserving the
        /// currently selected game by reference, if one is selected.
        /// </summary>
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

            // Row background — selected uses accent tint
            Color rowBg = selected
                ? Color.FromArgb(60, 55, 100)   // dark accent tint
                : PaletteSurface;
            Color rowFg = PaletteTextPrimary;

            using var bgBrush = new SolidBrush(rowBg);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

            // Left accent bar for selected item
            if (selected)
            {
                using var accentBrush = new SolidBrush(PaletteAccent);
                e.Graphics.FillRectangle(accentBrush, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
            }

            // Status dot (filled circle)
            Color dotColor = game.IsActive
                ? Color.FromArgb(72, 199, 72)
                : Color.FromArgb(90, 90, 90);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var dotBrush = new SolidBrush(dotColor);
            e.Graphics.FillEllipse(dotBrush, e.Bounds.X + 9, e.Bounds.Y + 10, 8, 8);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

            // Exe icon (small, vertically centred)
            int iconX = e.Bounds.X + 24;
            int iconY = e.Bounds.Y + (e.Bounds.Height - 16) / 2;
            if (!string.IsNullOrEmpty(game.ExePath) &&
                _iconCache.TryGetValue(game.ExePath, out Image? img))
                e.Graphics.DrawImage(img, iconX, iconY, 16, 16);
            else
                e.Graphics.DrawIcon(SystemIcons.Application,
                    new Rectangle(iconX, iconY, 16, 16));

            // Game name — Segoe UI, slightly larger for readability
            var nameFont = new Font("Segoe UI", 9f, selected ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(e.Graphics, game.GameName, nameFont,
                new Rectangle(e.Bounds.X + 46, e.Bounds.Y, e.Bounds.Width - 50, e.Bounds.Height),
                rowFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            nameFont.Dispose();

            // Subtle bottom border between rows
            using var borderPen = new Pen(PaletteBorder, 1);
            e.Graphics.DrawLine(borderPen,
                e.Bounds.X, e.Bounds.Bottom - 1,
                e.Bounds.Right, e.Bounds.Bottom - 1);
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

            // Title + toggle
            _lblGameTitle.Text = _selectedGame.GameName;
            _chkActive.Checked = _selectedGame.IsActive;
            UpdateActiveButton();

            // Populate monitor combo, preferring the current primary
            string primary = GetPrimaryMonitorId();
            if (!string.IsNullOrEmpty(primary) && _cmbMonitor.Items.Contains(primary))
                _cmbMonitor.SelectedItem = primary;
            else if (_cmbMonitor.Items.Count > 0)
                _cmbMonitor.SelectedIndex = 0;

            _activeScope = _cmbMonitor.SelectedItem?.ToString() ?? string.Empty;
            LoadProfileToUI(_selectedGame, _activeScope);

            // Advanced fields
            _txtGameName.Text = _selectedGame.GameName;
            _txtRegex.Text    = _selectedGame.RegexPattern;

            SetDetailVisible(true);
            _updatingUI = false;
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
            LoadProfileToUI(_selectedGame, _activeScope);
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

        private void BtnLoadDefaults_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null || string.IsNullOrEmpty(_activeScope)) return;
            if (_settings.MonitorDefaults.TryGetValue(_activeScope, out var def))
            {
                // Load values into the UI fields
                _numWidth.Value       = Clamp(def.Width,   0, 16384);
                _numHeight.Value      = Clamp(def.Height,  0, 16384);
                _numOffsetX.Value     = Clamp(def.OffsetX, -16384, 16384);
                _numOffsetY.Value     = Clamp(def.OffsetY, -16384, 16384);
                _chkConstrain.Checked = def.ConstrainMouse;

                // Immediately persist to the game's profile and apply to any
                // running window — no separate Save Changes click required.
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

        private void BtnFetchName_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null) return;

            // Walk running processes looking for one whose exe name matches
            // this game's compiled regex. Use the first match's window title.
            string? found = null;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    if (!_selectedGame.CompiledPattern.IsMatch(exe)) continue;
                    found = p.MainWindowTitle.Trim();
                    break;
                }
                catch { /* process may have exited */ }
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

        private void BtnSaveGame_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null) return;
            _selectedGame.GameName     = _txtGameName.Text.Trim();
            _selectedGame.RegexPattern = _txtRegex.Text.Trim();
            _selectedGame.InvalidatePattern();
            SaveUIToProfile(_activeScope);
            SaveConfig();
            if (_selectedGame.IsActive) EnforceGame(_selectedGame);
            int idx = _lstGames.SelectedIndex;
            PopulateGamesList();
            _lstGames.SelectedIndex = idx;
            ShowStatus($"Saved changes for {_selectedGame.GameName}.");
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

        /// <summary>
        /// Restores any windows currently being enforced for the given game back
        /// to their normal (bordered) state and stops tracking them. Must be
        /// called before a GameConfig is removed from _settings.Games —
        /// _trackedWindows holds its own GameConfig reference per tracked window
        /// handle, independent of the Games list, so EnforceTimer_Tick would
        /// otherwise keep re-applying borderless to an orphaned entry forever.
        /// </summary>
        private void RemoveGameTracking(GameConfig g)
        {
            bool wasActive = g.IsActive;
            g.IsActive = false; // routes ApplyBorderless into its restore-title-bar branch

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
        }

        // ════════════════════════════════════════════════════════════════════════
        // ADD GAME
        // ════════════════════════════════════════════════════════════════════════

        private void BtnAddRunning_Click(object? sender, EventArgs e)
        {
            // Collect processes that have a visible window title, deduped by exe name.
            // Build the list of system directories whose processes should be excluded.
            // This catches system processes not on the named blocklist.
            var systemDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps")
            };

            var entries = Process.GetProcesses()
                .Where(p =>
                {
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) return false;
                    if (p.MainWindowHandle == IntPtr.Zero) return false;
                    // Check the static named blocklist first — fast path
                    if (_systemProcessBlocklist.Contains(p.ProcessName + ".exe")) return false;
                    // Also exclude anything whose exe lives in a Windows system directory
                    try
                    {
                        string? exePath = p.MainModule?.FileName;
                        if (exePath != null && systemDirs.Any(d =>
                            exePath.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                            return false;
                    }
                    catch { /* access denied on system process — exclude it */ return false; }
                    return true;
                })
                .OrderBy(p => p.MainWindowTitle)
                .Select(p => new {
                    Display     = $"{p.MainWindowTitle}  ({p.ProcessName}.exe)",
                    Exe         = p.ProcessName + ".exe",
                    WindowTitle = p.MainWindowTitle   // passed as display name
                })
                .GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

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
            lst.Items.AddRange(entries.Select(x => x.Display).ToArray());
            dlg.Controls.Add(lst);
            dlg.Controls.Add(btn);
            dlg.AcceptButton = btn;

            if (dlg.ShowDialog(this) == DialogResult.OK && lst.SelectedIndex >= 0)
                AddGame(entries[lst.SelectedIndex].Exe, false,
                        entries[lst.SelectedIndex].WindowTitle);
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

        /// <param name="displayName">
        /// Optional human-readable name shown in the list. When null the sanitized
        /// exe basename is used (browse-for-EXE path). When provided (running-app
        /// picker path) the window title is used so the entry reads naturally.
        /// </param>
        /// <returns>
        /// True if the game was newly added and borderless mode applied to it
        /// without an elevation issue. False if nothing was added (empty input,
        /// duplicate) or if it was added but the apply failed — in either case
        /// AddGame has already shown appropriate status/popup feedback itself,
        /// so callers only need the return value to decide whether it's safe to
        /// layer an additional "success" notification on top.
        /// </returns>
        private bool AddGame(string input, bool isFullPath, string? displayName = null)
        {
            if (string.IsNullOrEmpty(input)) return false;

            string baseName  = Path.GetFileNameWithoutExtension(input);
            string cleanName = Regex.Replace(
                baseName, @"(-d|-shipping|-win64|-win32|-test|-debug)$", string.Empty,
                RegexOptions.IgnoreCase);
            string pattern   = $"^{Regex.Escape(cleanName)}.*\\.exe$";

            // Display name: prefer the window title passed from the picker;
            // fall back to the sanitized exe basename (browse path).
            string gameName  = !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : cleanName;

            // Dupe check against both the display name and the exe basename so
            // neither "HELLDIVERS 2" nor "helldivers2" can be added twice.
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
                GameName     = gameName,   // window title or sanitized exe name
                RegexPattern = pattern,    // always built from the exe basename
                ExePath      = exePath,
                IsActive     = true
            };

            // Seed profiles from all known monitor defaults
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
            // If no defaults exist at all, seed with a bare profile for the primary monitor
            if (game.Profiles.Count == 0)
            {
                string primary = GetPrimaryMonitorId();
                if (!string.IsNullOrEmpty(primary))
                    game.Profiles[primary] = new GameDisplayProfile();
            }

            _settings.Games.Add(game);
            SaveConfig();
            CacheIcon(exePath);
            PopulateGamesList();   // sorts the list before populating
            // Find the new game by reference after the sort so we select
            // the correct position regardless of alphabetical placement.
            int newIdx = _lstGames.Items.IndexOf(game);
            if (newIdx >= 0) _lstGames.SelectedIndex = newIdx;

            EnforceGame(game);

            // EnforceGame -> ApplyBorderless adds the game's name to
            // _elevationWarnedGames the first time a style/position Win32 call
            // fails while NoBorders isn't elevated — the same signal already
            // used for the recurring toast warning. Since this is a fresh add,
            // any presence here can only mean it just happened on this call.
            bool elevationIssue = !_isElevated && _elevationWarnedGames.Contains(game.GameName);
            ShowStatus(elevationIssue
                ? $"{cleanName} added, but borderless mode couldn't be fully applied."
                : $"{cleanName} added and borderless mode applied.");

            if (elevationIssue) PromptRestartAsAdmin(game.GameName);

            return !elevationIssue;
        }

        // ════════════════════════════════════════════════════════════════════════
        // SETTINGS TAB HANDLERS
        // ════════════════════════════════════════════════════════════════════════

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

        /// <summary>
        /// Permanently deletes a monitor's stored default profile and removes it
        /// from the known-monitors list. Also strips that monitor's entry from
        /// every game's per-monitor Profiles dictionary, since a profile for a
        /// monitor that no longer exists is dead weight in the config file.
        /// Refuses to delete a monitor that is currently connected — deleting it
        /// would just have it silently re-added on the next display scan (or on
        /// the very next enforcement tick), which would be confusing since the
        /// entry would seem to "come back" for no visible reason.
        /// </summary>
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

            // Count how many games have a saved profile for this monitor so the
            // confirmation dialog accurately reflects what will be lost.
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

            // Remove the monitor default profile and the known-monitor entry.
            _settings.MonitorDefaults.Remove(sel);
            _settings.KnownMonitors.Remove(sel);

            // Strip this monitor's profile from every game that has one.
            foreach (var g in _settings.Games)
                g.Profiles.Remove(sel);

            SaveConfig();
            AppLogger.Log($"Deleted monitor '{sel}' — removed default and profiles from {affectedGames} game(s).");

            // Refresh both monitor comboboxes so the deleted entry disappears
            // immediately, and refresh the currently selected game's detail view
            // in case it was showing the now-deleted monitor's profile.
            RefreshMonitors();
            if (_selectedGame != null)
            {
                int idx = _lstGames.SelectedIndex;
                if (idx >= 0) LstGames_SelectedIndexChanged(this, EventArgs.Empty);
            }

            ShowStatus($"Deleted monitor \"{sel}\" and its saved settings.");
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

            // Flash the button text briefly
            _btnSaveDefault.Text = "Saved ✔";
            var t = new System.Windows.Forms.Timer { Interval = 1400 };
            t.Tick += (ts, te) => { _btnSaveDefault.Text = "Save Monitor Default"; t.Stop(); t.Dispose(); };
            t.Start();
        }

        /// <summary>
        /// Begins listening for a new key combination for the given hotkey slot.
        /// Unregisters that specific global hotkey first, so pressing its current
        /// combination while picking a new one — including re-picking the exact
        /// same combo — only feeds this capture UI and can never also fire the
        /// live Add/Refresh action underneath it.
        /// </summary>
        private void BeginHotkeyCapture(int hotkeyId, HotkeyConfig config, TextBox display, Button button, string label)
        {
            if (_capturingHotkeyId != 0) CancelHotkeyCapture(); // only one capture session at a time

            UnregisterHotKey(this.Handle, hotkeyId);

            _capturingHotkeyId      = hotkeyId;
            _capturingHotkeyConfig  = config;
            _capturingHotkeyDisplay = display;
            _capturingHotkeyButton  = button;
            _capturingHotkeyLabel   = label;

            display.Text = "Press new key combo… (Esc to cancel)";
            button.Text  = "Listening… (click to cancel)";
            display.Focus();
        }

        /// <summary>
        /// Aborts an in-progress hotkey capture without changing the hotkey,
        /// restoring the display text and re-registering the unchanged binding
        /// that was unregistered when capture began.
        /// </summary>
        private void CancelHotkeyCapture()
        {
            if (_capturingHotkeyId == 0) return;

            _capturingHotkeyDisplay!.Text = _capturingHotkeyConfig!.ToString();
            _capturingHotkeyButton!.Text  = "Set Hotkey";

            _capturingHotkeyId      = 0;
            _capturingHotkeyConfig  = null;
            _capturingHotkeyDisplay = null;
            _capturingHotkeyButton  = null;
            _capturingHotkeyLabel   = string.Empty;

            RegisterHotkeys(); // restores the binding that was unregistered for capture
        }

        /// <summary>
        /// KeyDown handler for the hotkey display textboxes. Does nothing unless
        /// a capture session for that exact textbox is active (started via its
        /// Set Hotkey button) — a stray click/keypress in the box otherwise has
        /// no effect, and the currently-registered hotkeys keep working normally
        /// while the boxes just sit there showing the current bindings.
        /// </summary>
        private void CaptureHotkey(KeyEventArgs e, TextBox display)
        {
            if (_capturingHotkeyId == 0 || _capturingHotkeyDisplay != display) return;
            e.SuppressKeyPress = true;
            if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu) return; // wait for a real key

            string label = _capturingHotkeyLabel;

            if (e.KeyCode == Keys.Escape)
            {
                CancelHotkeyCapture();
                ShowStatus($"{label} hotkey unchanged.");
                return;
            }

            var config = _capturingHotkeyConfig!;
            var button = _capturingHotkeyButton!;

            if (e.KeyCode is Keys.Back or Keys.Delete)
            {
                config.Modifiers = 0;
                config.Key       = 0;
            }
            else
            {
                uint mods = MOD_NOREPEAT;  // always set; hidden from display
                if (e.Control) mods |= MOD_CONTROL;
                if (e.Shift)   mods |= MOD_SHIFT;
                if (e.Alt)     mods |= MOD_ALT;
                config.Modifiers = mods;
                config.Key       = (uint)e.KeyCode;
            }

            display.Text = config.ToString();
            button.Text  = "Set Hotkey";
            SaveConfig();

            _capturingHotkeyId      = 0;
            _capturingHotkeyConfig  = null;
            _capturingHotkeyDisplay = null;
            _capturingHotkeyButton  = null;
            _capturingHotkeyLabel   = string.Empty;

            RegisterHotkeys();
            ShowStatus($"{label} hotkey updated to {config}.");
        }

        // ════════════════════════════════════════════════════════════════════════
        // HOTKEYS
        // ════════════════════════════════════════════════════════════════════════

        private void RegisterHotkeys()
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID_ADD);
            UnregisterHotKey(this.Handle, HOTKEY_ID_REFRESH);

            TryRegisterHotkey(HOTKEY_ID_ADD,     _settings.HotkeyAddApp,     "Add",     _lblHotkeyAddStatus);
            TryRegisterHotkey(HOTKEY_ID_REFRESH,  _settings.HotkeyRefreshApp, "Refresh", _lblHotkeyRefreshStatus);
        }

        /// <summary>
        /// Attempts to register a hotkey, always including MOD_NOREPEAT.
        /// Updates the given status label so the actual registration outcome is
        /// always visible in Settings rather than relying on catching a toast —
        /// a hotkey that fails to register at the OS level (most commonly
        /// because another app or driver already owns that combination) will
        /// simply never fire, with no per-window symptom to debug from, so this
        /// is the ground-truth signal for "is this hotkey actually live".
        /// </summary>
        private void TryRegisterHotkey(int id, HotkeyConfig config, string label, Label? statusLabel = null)
        {
            if (config.Key == 0)
            {
                SetHotkeyStatusLabel(statusLabel, "Disabled", Color.Empty, muted: true);
                return;
            }

            // Always ensure MOD_NOREPEAT is set regardless of how the config was saved.
            uint mods = config.Modifiers | MOD_NOREPEAT;

            if (RegisterHotKey(this.Handle, id, mods, config.Key))
            {
                AppLogger.Log($"RegisterHotKey {label} OK: {config}");
                SetHotkeyStatusLabel(statusLabel, "Active", Color.FromArgb(72, 199, 72), muted: false);
                return;
            }

            int err = Marshal.GetLastWin32Error();
            AppLogger.Log($"RegisterHotKey {label} failed (Win32={err}): {config}");

            // ERROR_HOTKEY_ALREADY_REGISTERED = 1409
            // This is the most common failure — another app, driver, or Windows
            // itself already owns this key combination. On some laptops, F-keys
            // are also intercepted at the firmware/Fn-lock level before Windows
            // ever sees a normal virtual-key code, which looks identical to a
            // registration conflict from here.
            bool bareKey = (mods & ~MOD_NOREPEAT) == 0; // no Ctrl/Shift/Alt
            Color failColor = Color.FromArgb(210, 70, 70);
            if (err == 1409 && bareKey)
            {
                // Bare F-key blocked by Windows or the foreground app.
                // Inform the user — they need to add a modifier.
                SetHotkeyStatusLabel(statusLabel, "Not active — blocked by Windows", failColor, muted: false);
                if (this.IsHandleCreated)
                    ShowToast(
                        $"{label} hotkey ({config}) is blocked by Windows.\n"
                        + "Try adding Shift or Ctrl in Settings.",
                        success: false);
                AppLogger.Log($"  Bare key blocked — user should add Ctrl/Shift/Alt modifier.");
            }
            else if (err == 1409)
            {
                // Modifier combo is taken by something else.
                SetHotkeyStatusLabel(statusLabel, "Not active — conflicts with another app", failColor, muted: false);
                if (this.IsHandleCreated)
                    ShowToast(
                        $"{label} hotkey ({config}) conflicts with another app.\n"
                        + "Change it in Settings.",
                        success: false);
            }
            else
            {
                SetHotkeyStatusLabel(statusLabel, $"Not active — Win32 error {err}", failColor, muted: false);
            }
        }

        /// <summary>Updates a hotkey status label's text/color. muted uses the
        /// current theme's muted text color instead of an explicit color.</summary>
        private void SetHotkeyStatusLabel(Label? lbl, string text, Color color, bool muted)
        {
            if (lbl == null) return;
            lbl.Text      = text;
            lbl.ForeColor = muted ? PaletteTextMuted : color;
        }

        /// <summary>
        /// Resolves the exe name for a window's owning process, and returns false
        /// if that process should never be acted on by a hotkey — either because
        /// it's on the system blocklist, or because it is NoBorders itself. This
        /// guard is independent of what's in the games list, so it can't be
        /// bypassed even if a user-edited Match Regex happens to be broad enough
        /// to match the shell or NoBorders' own exe name.
        /// The self-check compares process IDs first (Environment.ProcessId),
        /// since that's exact and never throws — MainModule path comparison is
        /// used only as a secondary check, since it can throw when the *target*
        /// process is elevated and this one isn't (that failure means "not us"
        /// safely, so it's caught and ignored rather than treated as a match).
        /// </summary>
        private bool TryGetHotkeyTargetExe(IntPtr hwnd, out string exeName)
        {
            exeName = string.Empty;
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;
                if (pid == (uint)Environment.ProcessId) return false; // NoBorders itself

                using var p = Process.GetProcessById((int)pid);
                exeName = p.ProcessName + ".exe";

                if (_systemProcessBlocklist.Contains(exeName)) return false;

                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) &&
                        path.Equals(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                catch { /* elevated process — MainModule inaccessible; pid check above already covers "is us" */ }

                return true;
            }
            catch
            {
                return false; // process may have exited between capture and lookup
            }
        }

        private void HotkeyAdd()
        {
            // Use the window captured in WndProc before dispatch — avoids the
            // race where GetForegroundWindow() returns our own form during hotkey processing.
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
                // Fetch the window title directly from the handle — more reliable
                // than Process.MainWindowTitle which can lag or return empty.
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                string windowTitle = sb.ToString().Trim();
                AppLogger.Log($"HotkeyAdd: windowTitle=\"{windowTitle}\"");

                bool alreadyTracked = _settings.Games.Any(g => g.CompiledPattern.IsMatch(exeName));
                AppLogger.Log($"HotkeyAdd: alreadyTracked={alreadyTracked}");

                if (!alreadyTracked && this.IsHandleCreated)
                {
                    // Pass the window title as displayName so the list entry
                    // reads naturally (same as the running-app picker).
                    string nameToUse = !string.IsNullOrWhiteSpace(windowTitle) ? windowTitle : exeName;
                    AppLogger.Log($"HotkeyAdd: calling AddGame with displayName=\"{nameToUse}\"");
                    this.Invoke(new MethodInvoker(() =>
                    {
                        // AddGame already shows its own status/popup feedback on
                        // failure (including the elevation prompt), so only add
                        // this success toast when it actually fully succeeded —
                        // otherwise the user would see "applied" right next to a
                        // dialog saying it wasn't.
                        if (AddGame(exeName, false, nameToUse))
                            ShowToast($"Added & borderless applied\n{nameToUse}", success: true);
                    }));
                }
                else if (alreadyTracked)
                {
                    // Game is already in the list — apply borderless instead.
                    AppLogger.Log("HotkeyAdd: game already tracked, applying borderless.");
                    var match = _settings.Games.First(g => g.CompiledPattern.IsMatch(exeName));
                    _trackedWindows[hwnd] = match;
                    ApplyBorderless(hwnd, match);
                    ShowToast($"Borderless applied\n{match.GameName}", success: true);
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
                string exeName = exeNameRaw.ToLowerInvariant();
                var match       = _settings.Games.FirstOrDefault(g =>
                    g.IsActive && g.CompiledPattern.IsMatch(exeName));
                if (match == null)
                {
                    ShowToast("Foreground app is not in the game list.", success: false);
                    return;
                }

                // Force a fresh monitor refresh before applying so that if the
                // display configuration changed since startup we have current data.
                RefreshMonitors();

                // Update the tracked handle to the current foreground window and
                // re-apply using the now-current monitor layout.
                _trackedWindows[hwnd] = match;
                ApplyBorderless(hwnd, match);
                ShowToast($"Borderless re-applied\n{match.GameName}", success: true);
            }
            catch (Exception ex) { AppLogger.Log(ex, "HotkeyRefresh"); }
        }

        // ════════════════════════════════════════════════════════════════════════
        // WINDOW ENFORCEMENT
        // ════════════════════════════════════════════════════════════════════════


        private void ScanExistingWindows()
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        MatchAndApply(p.MainWindowHandle, p);
                }
                catch { /* process may have exited */ }
            }
        }

        private void MatchAndApply(IntPtr hwnd, Process p)
        {
            string exe = (p.ProcessName + ".exe").ToLowerInvariant();
            foreach (var g in _settings.Games)
            {
                if (g.IsActive && g.CompiledPattern.IsMatch(exe))
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
                // Retry for up to 30 seconds (60 × 500ms). Many games take
                // 10–20 seconds to fully initialise their window after creating it.
                for (int i = 0; i < 60 && !cts.Token.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, cts.Token);
                    try
                    {
                        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) continue;
                        using var p  = Process.GetProcessById((int)pid);
                        string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                        GetWindowRect(hwnd, out RECT r);
                        if ((r.Right - r.Left) < 100) continue;

                        foreach (var g in _settings.Games)
                        {
                            if (g.IsActive && g.CompiledPattern.IsMatch(exe))
                            {
                                ApplyBorderless(hwnd, g);
                                _trackedWindows[hwnd] = g;
                                return;
                            }
                        }
                    }
                    catch (InvalidOperationException) { /* process exited mid-loop */ }
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
                    if (g.CompiledPattern.IsMatch((p.ProcessName + ".exe").ToLowerInvariant()))
                    {
                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                    }
                }
                catch { /* process may have exited */ }
            }
        }

        private void EnforceTimer_Tick(object? sender, EventArgs e)
        {
            // Step 0: refresh which games are currently running so the games
            // list can keep running entries pinned to the top.
            RefreshRunningGames();

            // Step 1: re-apply settings to all windows we are already tracking.
            // This corrects any style resets the game engine may have done.
            foreach (var hwnd in _trackedWindows.Keys.ToList())
            {
                if (!IsWindow(hwnd)) { _trackedWindows.Remove(hwnd); continue; }
                try { ApplyBorderless(hwnd, _trackedWindows[hwnd]); }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, "EnforceTimer_Tick");
                    _trackedWindows.Remove(hwnd);
                }
            }

            // Step 2: check whether any active-but-untracked games have opened.
            // This is the safety net for cases where the shell hook fired too
            // early (before the game's window was stable) and TrackNewWindowAsync
            // gave up, or where the shell hook message was missed entirely.
            // We only run the process scan when there are active games that don't
            // yet have a tracked handle, keeping the cost low.
            var trackedGames = new HashSet<GameConfig>(_trackedWindows.Values);
            bool anyUntracked = _settings.Games.Any(g => g.IsActive && !trackedGames.Contains(g));
            if (!anyUntracked) return;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (_trackedWindows.ContainsKey(p.MainWindowHandle)) continue;

                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    foreach (var g in _settings.Games)
                    {
                        if (!g.IsActive || !g.CompiledPattern.IsMatch(exe)) continue;
                        if (trackedGames.Contains(g)) continue;

                        // Verify the window is sized (not a splash/loading stub)
                        GetWindowRect(p.MainWindowHandle, out RECT r);
                        if ((r.Right - r.Left) < 100) continue;

                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                        trackedGames.Add(g); // don't match this game again this tick
                    }
                }
                catch { /* process may have exited between GetProcesses and here */ }
            }
        }

        private void ApplyBorderless(IntPtr hwnd, GameConfig g)
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);

            if (!g.IsActive)
            {
                // Restore title bar if we removed it
                if ((style & (int)WS_CAPTION) == 0)
                {
                    SetWindowLong(hwnd, GWL_STYLE, (int)(style | WS_CAPTION | WS_THICKFRAME));
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
                return;
            }

            // Determine which monitor the window is currently on.
            // Screen.FromHandle always returns a valid Screen even if _monitors
            // hasn't been populated yet — it falls back to the primary screen.
            var scr = Screen.FromHandle(hwnd);

            // Try to resolve the friendly monitor name from our cached list.
            // If _monitors is empty or stale, fall back to Screen.PrimaryScreen
            // so we never silently bail out with an empty monId.
            string monId = _monitors.FirstOrDefault(m =>
                TrimNull(m.DeviceName).Equals(TrimNull(scr.DeviceName),
                    StringComparison.OrdinalIgnoreCase))?.ID ?? string.Empty;

            if (string.IsNullOrEmpty(monId))
            {
                // _monitors didn't match — refresh and try once more before giving up.
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

            // Always re-strip window styles if the game has reset them (common
            // during engine init). Only skip the SetWindowPos call if the window
            // is already in exactly the right position — that call causes a brief
            // visual flicker on every tick if applied unconditionally.
            bool styleCallFailed = false;
            bool posCallFailed   = false;

            if (!styleCorrect)
            {
                ShowWindow(hwnd, SW_RESTORE);
                int prevStyle = SetWindowLong(hwnd, GWL_STYLE, (int)newStyle);
                // SetWindowLong returns 0 both on genuine failure AND when the
                // previous style value legitimately was 0 — check GetLastWin32Error
                // to tell them apart. A non-zero error code means it actually failed.
                if (prevStyle == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0)
                    {
                        styleCallFailed = true;
                        AppLogger.Log($"SetWindowLong failed for '{g.GameName}' (hwnd={hwnd}), Win32={err}");
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
                    AppLogger.Log($"SetWindowPos failed for '{g.GameName}' (hwnd={hwnd}), Win32={err}");
                }
            }

            // If either Win32 call failed and we're not running elevated, the most
            // likely cause is that the target window belongs to an elevated process
            // (common with anti-cheat like Vanguard/EAC/BattlEye). Windows blocks
            // non-elevated processes from modifying elevated windows. Warn once per
            // game per session rather than spamming a toast every enforcement tick.
            if ((styleCallFailed || posCallFailed) && !_isElevated
                && !_elevationWarnedGames.Contains(g.GameName))
            {
                _elevationWarnedGames.Add(g.GameName);
                ShowToast(
                    $"Couldn't fully apply borderless to {g.GameName}.\n"
                    + "Try Settings → Restart as Administrator.",
                    success: false);
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
                var match      = _settings.Games.FirstOrDefault(g =>
                    g.IsActive && g.CompiledPattern.IsMatch(exeName));

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

        // ════════════════════════════════════════════════════════════════════════
        // STATUS BAR
        // ════════════════════════════════════════════════════════════════════════

        private void ShowStatus(string message)
        {
            _lblStatus.Text = message;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        /// <summary>
        /// Shows a small non-stealing toast notification in the bottom-right corner
        /// of the primary screen. Fades in, holds, then fades out automatically.
        /// Safe to call from background threads — marshals to the UI thread.
        /// </summary>
        private void ShowToast(string message, bool success = true)
        {
            if (!this.IsHandleCreated) return;

            // Marshal to UI thread if called from a hotkey/background context.
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new MethodInvoker(() => ShowToast(message, success)));
                return;
            }

            // Toast uses the app's single dark palette for consistency
            Color toastBg = Color.FromArgb(28, 28, 32);
            Color accentColor = success
                ? Color.FromArgb(34, 160, 74)
                : Color.FromArgb(200, 55, 55);
            Color msgFg = Color.FromArgb(210, 210, 210);

            var toast = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar   = false,
                TopMost         = true,
                Opacity         = 0,
                Size            = new Size(300, 62),
                StartPosition   = FormStartPosition.Manual,
                BackColor       = toastBg
            };

            var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            toast.Location = new Point(
                screen.Right  - toast.Width  - 18,
                screen.Bottom - toast.Height - 18);

            // Left accent bar
            var accent = new Panel
            {
                Dock      = DockStyle.Left,
                Width     = 4,
                BackColor = accentColor
            };

            // "NoBorders" app label
            var lblApp = new Label
            {
                Text      = "NoBorders",
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = accentColor,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Bounds    = new Rectangle(12, 5, 276, 16),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Message text
            var lblMsg = new Label
            {
                Text      = message,
                Font      = new Font("Segoe UI", 9f),
                ForeColor = msgFg,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Bounds    = new Rectangle(12, 22, 276, 34),
                TextAlign = ContentAlignment.MiddleLeft
            };

            toast.Controls.Add(lblApp);
            toast.Controls.Add(lblMsg);
            toast.Controls.Add(accent);

            // Rounded-corner region (Windows 10/11 style).
            toast.Region = new Region(new System.Drawing.Drawing2D.GraphicsPath());
            toast.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedRect(new Rectangle(0, 0, toast.Width - 1, toast.Height - 1), 8);
                using var bg   = new SolidBrush(toast.BackColor);
                e.Graphics.FillPath(bg, path);
                toast.Region = new Region(path);
            };

            // Fade-in → hold → fade-out timer sequence.
            const int fadeSteps    = 12;   // steps for fade in and out
            const int fadeInterval = 20;   // ms per step
            const int holdMs       = 2200; // ms to hold at full opacity
            int step = 0;
            bool holding = false;
            bool fadingOut = false;

            var fadeTimer = new System.Windows.Forms.Timer { Interval = fadeInterval };
            fadeTimer.Tick += (s, e) =>
            {
                if (!fadingOut && !holding)
                {
                    // Fade in
                    step++;
                    toast.Opacity = Math.Min(1.0, step / (double)fadeSteps);
                    if (step >= fadeSteps)
                    {
                        holding = true;
                        fadeTimer.Interval = holdMs;
                    }
                }
                else if (holding)
                {
                    // Hold complete — start fade out
                    holding  = false;
                    fadingOut = true;
                    step     = fadeSteps;
                    fadeTimer.Interval = fadeInterval;
                }
                else
                {
                    // Fade out
                    step--;
                    toast.Opacity = Math.Max(0.0, step / (double)fadeSteps);
                    if (step <= 0)
                    {
                        fadeTimer.Stop();
                        fadeTimer.Dispose();
                        toast.Close();
                        toast.Dispose();
                    }
                }
            };

            // Show without stealing focus.
            toast.Show();
            NativeMethods.ShowWindowNoActivate(toast.Handle);
            fadeTimer.Start();
        }

        /// <summary>Helper to build a rounded-rectangle GraphicsPath.</summary>
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

        // ════════════════════════════════════════════════════════════════════════
        // REGISTRY / STARTUP
        // ════════════════════════════════════════════════════════════════════════

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
                }
                else key.DeleteValue(APP_NAME, false);
            }
            catch (Exception ex) { AppLogger.Log(ex, "UpdateRegistryStartup"); }
        }

        // ════════════════════════════════════════════════════════════════════════
        // FORM EVENTS & MESSAGE PUMP
        // ════════════════════════════════════════════════════════════════════════

        private void OnResize(object? sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }
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

            // Flush any pending save immediately on exit
            SaveConfig();

            ClipCursor(IntPtr.Zero);
            _trayIcon.Visible = false;
        }

        /// <summary>
        /// Called when Windows reports a display configuration change (monitor
        /// enabled/disabled, resolution changed, primary changed, etc.).
        /// We refresh the monitor list, clear all tracked window handles so they
        /// are re-evaluated against the new layout, then re-apply borderless after
        /// a short delay to let Windows finish repositioning windows.
        /// </summary>
        private async void OnDisplayConfigChanged()
        {
            // Refresh the monitor list immediately so _monitors reflects the new layout.
            RefreshMonitors();

            // Windows takes up to ~1 second to finish moving windows around after a
            // display change. Wait before re-applying so we're working on stable geometry.
            await Task.Delay(1500);

            // Clear the tracked-window cache so every window gets re-evaluated
            // from scratch against the new monitor layout.
            _trackedWindows.Clear();

            // Cancel any pending new-window detection tasks — they were started
            // with the old monitor layout and should be restarted clean.
            foreach (var cts in _pending.Values) cts.Cancel();
            _pending.Clear();

            // Re-apply borderless to all processes that match an active game.
            // This picks up the correct monitor profile now that _monitors is updated.
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        MatchAndApply(p.MainWindowHandle, p);
                }
                catch { /* process may have exited */ }
            }

            // Refresh the detail panel if a game is currently selected, so the
            // monitor combobox reflects the updated display list.
            // Guard with IsHandleCreated so a display change that fires during
            // early startup can't cause "Invoke before handle created" crashes.
            if (_selectedGame != null && this.IsHandleCreated)
            {
                int idx = _lstGames.SelectedIndex;
                if (idx >= 0) LstGames_SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Ignore all custom messages until the form is fully initialised.
            // WndProc can fire during handle creation (before OnLoad) when
            // accessing this.Handle for the first time, which would cause
            // "Invoke before handle created" exceptions.
            if (!this.IsHandleCreated)
            {
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == _wmShellHook && m.WParam.ToInt32() == HSHELL_WINDOWCREATED)
                _ = TrackNewWindowAsync(m.LParam);

            if (_wmShowFirst != 0 && m.Msg == _wmShowFirst)
            { RestoreFromTray(); return; }

            if (m.Msg == WM_DISPLAYCHANGE)
                OnDisplayConfigChanged();

            if (m.Msg == WM_HOTKEY)
            {
                // Capture the foreground window NOW, before dispatching.
                // By the time HotkeyAdd/HotkeyRefresh runs, focus may have
                // shifted to our own form window.
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

        // ════════════════════════════════════════════════════════════════════════
        // SLEEP / WAKE
        // ════════════════════════════════════════════════════════════════════════

        private void OnSleep()
        {
            AppLogger.Log("=== SYSTEM SLEEP ===");
            AppLogger.Log($"  Tracked windows at sleep : {_trackedWindows.Count}");
            AppLogger.Log($"  Pending tasks at sleep   : {_pending.Count}");
            AppLogger.Log($"  Active games             : {_settings.Games.Count(g => g.IsActive)}");
            AppLogger.Log($"  Enforce timer running    : {_enforceTimer.Enabled}");

            // Record whether the form was hidden to tray before sleep.
            // Windows can restore a hidden/minimized window during resume,
            // so we need to re-hide it ourselves on wake if necessary.
            _wasHiddenBeforeSleep = !this.Visible || this.WindowState == FormWindowState.Minimized;
            AppLogger.Log($"  Form visible before sleep: {this.Visible}, minimized: {this.WindowState == FormWindowState.Minimized}");

            // Pause timers while suspended — they serve no purpose and prevent
            // the system from settling cleanly on resume.
            _enforceTimer.Stop();
            _clipTimer.Stop();

            // Unregister hotkeys — Windows invalidates global hotkey registrations
            // across sleep/wake on some hardware and driver configurations.
            UnregisterHotKey(this.Handle, HOTKEY_ID_ADD);
            UnregisterHotKey(this.Handle, HOTKEY_ID_REFRESH);
            AppLogger.Log("  Hotkeys unregistered before sleep.");

            // Clear tracked handles — after wake the window handles may be
            // recycled or invalid, and game processes may have been suspended.
            _trackedWindows.Clear();
            foreach (var cts in _pending.Values) cts.Cancel();
            _pending.Clear();
            AppLogger.Log("  Tracked windows and pending tasks cleared.");
        }

        private async void OnWake()
        {
            AppLogger.Log("=== SYSTEM WAKE ===");

            // Wait for Windows to finish resuming drivers, re-enumerating displays,
            // and restoring network/GPU state before we do anything.
            // 3 seconds covers most hardware; display change will fire separately
            // if the monitor configuration changed during sleep.
            await Task.Delay(3000);

            // Re-register hotkeys — they must be re-registered after wake.
            RegisterHotkeys();
            AppLogger.Log($"  Hotkeys re-registered: Add={_settings.HotkeyAddApp}, Refresh={_settings.HotkeyRefreshApp}");

            // Refresh monitor list in case display config changed during sleep
            // (e.g. TV powered off while machine was asleep).
            RefreshMonitors();
            AppLogger.Log($"  Monitors after wake: {string.Join(", ", _monitors.Select(m => m.ID))}");

            // Log current state of active games and their profiles.
            foreach (var g in _settings.Games.Where(g => g.IsActive))
            {
                AppLogger.Log($"  Active game: '{g.GameName}' profiles=[{string.Join(", ", g.Profiles.Keys)}]");
            }

            // Restart timers.
            _enforceTimer.Start();
            _clipTimer.Start();
            AppLogger.Log("  Timers restarted.");

            // Re-apply borderless to any games that are still running.
            // _trackedWindows was cleared on sleep so this is a clean scan.
            int applied = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string exe = (p.ProcessName + ".exe").ToLowerInvariant();
                    foreach (var g in _settings.Games)
                    {
                        if (!g.IsActive || !g.CompiledPattern.IsMatch(exe)) continue;
                        GetWindowRect(p.MainWindowHandle, out RECT r);
                        AppLogger.Log($"  Found running game '{g.GameName}' (hwnd={p.MainWindowHandle}, rect={r.Left},{r.Top},{r.Right},{r.Bottom})");
                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                        applied++;
                    }
                }
                catch { /* process may have exited */ }
            }
            AppLogger.Log($"  Applied borderless to {applied} window(s) after wake.");
            AppLogger.Log($"  Tracked windows after wake: {_trackedWindows.Count}");

            // If the form was hidden to tray before sleep, ensure it stays
            // hidden after wake. Windows resume can restore the window to
            // the taskbar even if it was hidden, causing it to appear in Alt+Tab.
            if (_wasHiddenBeforeSleep && _settings.MinimizeToTray)
            {
                this.Hide();
                this.ShowInTaskbar = false;
                this.WindowState   = FormWindowState.Minimized;
                AppLogger.Log("  Form was hidden before sleep — re-hidden after wake.");
            }
            else
            {
                AppLogger.Log($"  Form visibility restored: visible={this.Visible}, taskbar={this.ShowInTaskbar}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // UTILITIES
        // ════════════════════════════════════════════════════════════════════════

        private static decimal Clamp(int value, int min, int max)
            => Math.Max(min, Math.Min(max, value));
    }
    /// <summary>Win32 helpers that don't belong on MainForm.</summary>
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

        /// <summary>Show a window without stealing focus from the foreground app.</summary>
        public static void ShowWindowNoActivate(IntPtr hWnd)
        {
            ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hWnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        }
    }
}
