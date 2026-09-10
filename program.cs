// NoBorders - Borderless window manager
// Rewritten for clean tabbed UX, maintainability, and correctness.

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
    ///
    /// Deliberately no Equals/GetHashCode override, so every
    /// HashSet&lt;GameConfig&gt;/Dictionary lookup against it (_runningGames,
    /// the EnforceTimer_Tick Step 2 untracked-games scan, MainForm.Tray.cs's
    /// RebuildTrayMenu appliedGames set) compares by reference, not by
    /// content. That's correct today, not just untested: _settings.Games is
    /// the single canonical list built once at load, and every one of those
    /// collections only ever stores references back into that same list —
    /// never a clone or a freshly-deserialized duplicate representing the
    /// same logical game — so reference equality and value equality
    /// coincide everywhere they're actually compared. Would need revisiting
    /// only if something started constructing a second GameConfig instance
    /// for a game that's already in _settings.Games and expected it to
    /// compare equal to the original (nothing does this).
    /// </summary>
    /// <summary>
    /// review.md §3: what a game's RegexPattern is tested against.
    /// ProcessName (the default, and the only mode that ever existed before
    /// this) tests the exe basename ("game.exe"); WindowTitle tests the
    /// actual window title text instead. Defaults to ProcessName on
    /// deserialization for every pre-existing saved game, so this is a
    /// purely additive schema change — no migration step needed.
    /// </summary>
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

        // Phase 8 (MIGRATION_PLAN.md): wwwroot-relative paths to locally cached
        // artwork (e.g. "artwork-cache/valorant-hero.png"), empty until a real
        // fetch (Services/ArtworkService.cs) succeeds — never a remote URL, so
        // the hero/rail/toast art is never a broken image or a live network
        // fetch on every render. Empty means "still the CSS placeholder."
        public string HeroImagePath { get; set; } = string.Empty;
        public string IconImagePath { get; set; } = string.Empty;

        // Compiled regex is built on first use and cached. [System.Text.Json.Serialization.JsonIgnore]
        // keeps it out of the saved config file.
        [System.Text.Json.Serialization.JsonIgnore]
        private Regex? _compiledPattern;

        // review.md §1.2: a 500ms matchTimeout on both the real and the
        // "never matches" fallback regex — this pattern is user-editable
        // text (Matching tab) and IsMatch runs against every open window on
        // every EnforceTimer_Tick (1s) and ClipTimer_Tick (100ms), both on
        // the UI thread, so a pathological catastrophic-backtracking pattern
        // (typed by hand, pasted, or arriving via an imported config) would
        // otherwise hang the whole app with no crash and no log line
        // explaining why. GameConfig.IsMatch below is the only place that
        // actually calls CompiledPattern.IsMatch — it catches the resulting
        // RegexMatchTimeoutException and treats a timeout as "no match"
        // (safe default: never accidentally enforces borderless on the
        // wrong window), so every call site gets this for free instead of
        // needing its own try/catch.
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
                        _compiledPattern = new Regex("(?!)", RegexOptions.Compiled, PatternMatchTimeout); // never matches
                    }
                }
                return _compiledPattern ?? new Regex("(?!)", RegexOptions.Compiled, PatternMatchTimeout);
            }
        }

        /// <summary>Call after editing RegexPattern so the cache is rebuilt.</summary>
        public void InvalidatePattern() => _compiledPattern = null;

        /// <summary>The one real entry point for testing a window against this
        /// game's pattern — every call site should use this instead of touching
        /// CompiledPattern.IsMatch directly, so the ReDoS timeout guard above
        /// can't accidentally be bypassed by a new call site that skips it.
        /// Tests <paramref name="exeName"/> or <paramref name="windowTitle"/>
        /// depending on <see cref="MatchTarget"/> — review.md §3's "Window
        /// Title" mode. <paramref name="windowTitle"/> may be empty (not every
        /// call site tracks a live window, e.g. dupe-name checks against the
        /// whole games list); in ProcessName mode (the default) it's never
        /// even read.</summary>
        public bool IsMatch(string exeName, string windowTitle = "") => IsMatch(exeName, windowTitle, MatchTarget);

        /// <summary>Same as <see cref="IsMatch(string, string)"/>, but tests against an
        /// explicitly-given target mode instead of this game's saved <see cref="MatchTarget"/> —
        /// lets the Matching tab's live-test list preview the pending (not-yet-saved)
        /// Window Title/Process Name chip selection instead of only ever answering for
        /// what's currently enforced.</summary>
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

        // User request (2026-08-13): the last real resolution seen for each
        // monitor ID, updated every RefreshMonitors — see MonitorResolution's
        // own doc comment for why this exists (Display/Monitors' Result
        // Preview and coverage %% now work for a disconnected monitor
        // instead of showing "not connected — preview unavailable").
        public Dictionary<string, MonitorResolution> LastKnownMonitorResolutions { get; set; }
            = new Dictionary<string, MonitorResolution>(StringComparer.OrdinalIgnoreCase);
        public List<GameConfig> Games              { get; set; } = new List<GameConfig>();
        public bool             MinimizeToTray     { get; set; } = true;
        public bool             StartWithWindows   { get; set; } = false;
        public bool             StartMinimized     { get; set; } = false;
        public HotkeyConfig     HotkeyAddApp       { get; set; } = new HotkeyConfig { Modifiers = 0x0002 | 0x4000, Key = (uint)Keys.F10 };
        public HotkeyConfig     HotkeyRefreshApp   { get; set; } = new HotkeyConfig { Modifiers = 0x0002 | 0x4000, Key = (uint)Keys.F11 };

        // Phase 8: user's own free SteamGridDB personal API key (obtained at
        // steamgriddb.com/profile/preferences/api), pasted in Settings >
        // Diagnostics. Empty means artwork fetch falls back to the game's own
        // .exe icon only — never a hard requirement to use the app.
        //
        // review.md §1.3: the in-memory plaintext value the rest of the app
        // reads/writes (ArtworkService's Bearer header, the Settings field) —
        // JsonIgnore keeps it OUT of games_config.json. The persisted form is
        // SteamGridDbApiKeyProtected below; LoadConfig/SaveConfig do the
        // actual DPAPI encrypt/decrypt round-trip, once, in one place.
        [System.Text.Json.Serialization.JsonIgnore]
        public string SteamGridDbApiKey { get; set; } = string.Empty;

        /// <summary>DPAPI-protected (CurrentUser scope), base64-encoded form of
        /// SteamGridDbApiKey — the only copy that actually reaches disk. Only
        /// touched by LoadConfig (decrypt into SteamGridDbApiKey after
        /// deserializing) and SaveConfig (encrypt from SteamGridDbApiKey right
        /// before serializing) — never read/written anywhere else.</summary>
        public string SteamGridDbApiKeyProtected { get; set; } = string.Empty;

        // Phase 8.4: remembers the main window's last real (non-minimized)
        // bounds so a resize/move persists across restarts. WindowX/Y default
        // to int.MinValue as an "unset" sentinel — distinct from a legitimate
        // saved coordinate (which can itself be negative on a multi-monitor
        // setup with a display to the left of/above the primary) — so a
        // fresh install falls back to the original CenterScreen behavior
        // instead of parsing as "restore to (0,0)".
        public int  WindowWidth      { get; set; } = 1200;
        public int  WindowHeight     { get; set; } = 800;
        public int  WindowX          { get; set; } = int.MinValue;
        public int  WindowY          { get; set; } = int.MinValue;
        public bool WindowMaximized  { get; set; } = false;

        // Settings > Ignore List (Phase 8.4): the user's own additions on top
        // of the built-in, never-shown _systemProcessBlocklist — kept as a
        // separate set (rather than merged into it) specifically so the
        // Settings page only ever lists what the user actually chose to add,
        // never the ~100 built-in system-process entries. "Clear All" empties
        // just this set; the built-in list still applies underneath either way.
        public HashSet<string> IgnoredProcesses { get; set; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Settings > Permissions "Always start as Administrator" — set
        // automatically whenever RestartAsAdmin() actually runs (any of its
        // several trigger points), and toggleable directly. Checked once at
        // startup (OnLoad): true + not currently elevated silently re-runs
        // RestartAsAdmin() with no prompt, since this is a standing
        // preference rather than a fresh decision each launch.
        public bool AlwaysRunAsAdmin { get; set; } = false;

        // Settings > Diagnostics "Verbose logging" — was a static, inert
        // ToggleSwitch with no backing field at all (that page's own doc
        // comment: "nothing on this page corresponds to a real AppSettings
        // field"). Same shape as AlwaysRunAsAdmin: a plain persisted
        // preference, synced to AppLogger.VerboseEnabled at startup and on
        // every toggle.
        public bool VerboseLogging { get; set; } = false;
    }

    /// <summary>
    /// Phase 8.6: the four one-shot alignment actions replacing the old
    /// single "Center On Monitor" link — Left/Right keep the current
    /// Width/Height's vertical centering but flush the window to that edge;
    /// Bottom keeps horizontal centering and flushes to the bottom edge;
    /// Center is the original both-axes-centered behavior.
    /// </summary>
    public enum MonitorAlignMode { Left, Center, Bottom, Right }

    public class MonitorItem
    {
        public string ID         { get; set; } = string.Empty; // friendly name shown to user
        public string DeviceName { get; set; } = string.Empty; // GDI device name e.g. \\.\DISPLAY1
        // Native resolution and primary-ness, filled in by RefreshMonitors from
        // Screen.Bounds/Screen.Primary. Added for the Settings > Monitors Blazor
        // view (MIGRATION_PLAN.md Phase 3.5) — MonitorItem itself isn't
        // serialized anywhere, so this has no config-format impact.
        public int  Width         { get; set; }
        public int  Height        { get; set; }
        public bool Primary       { get; set; }
        public override string ToString() => ID;
    }

    /// <summary>
    /// User request (2026-08-13): a monitor's real resolution, captured the
    /// last time RefreshMonitors actually saw it connected — unlike
    /// MonitorItem (never serialized, live-only), this persists to
    /// games_config.json so the Result Preview/coverage math keeps working
    /// for a monitor that's since been unplugged, instead of the "not
    /// connected — preview unavailable" placeholder ResultPreview fell back
    /// to before this (which was itself a fix for an earlier bug: guessing
    /// the window's own size as a stand-in for an unknown monitor silently
    /// produced a wrong, self-contradicting preview).
    /// </summary>
    public class MonitorResolution
    {
        public int Width  { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// One blocklist-filtered open window — shared shape between
    /// <c>BtnAddRunning_Click</c>'s dialog and the Matching tab's (screen 5a)
    /// live-test list, both built from <c>MainForm.GetOpenWindowEntries()</c>.
    /// </summary>
    public readonly record struct OpenWindowEntry(string WindowTitle, string Exe, int Pid);

    // ══════════════════════════════════════════════════════════════════════════════
    // LOGGER
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Phase 6.4 (MIGRATION_PLAN.md): real level metadata for the Activity Log's
    /// level chips/row coloring. Added additively — every pre-existing call site
    /// (~80, plain prose trace statements) keeps compiling unchanged and defaults
    /// to Info, since <see cref="AppLogger.Log(string, LogLevel)"/>'s new parameter
    /// is optional. Only a small, specifically-justified set of call sites (those
    /// paired with a real <c>ShowToast</c> success/failure, i.e. an event a user
    /// actually sees) were upgraded to Ok/Warn — see MIGRATION_PLAN.md's 6.4
    /// write-up for the exact list and reasoning. Everything else stays Info by
    /// default rather than guessing intent from message prose.
    /// </summary>
    public enum LogLevel { Info, Ok, Warn, Error }

    /// <summary>
    /// Single source of truth for the version shown in the UI — MainShell's
    /// titlebar chip and Settings > About both hardcoded a literal "v2.4"
    /// left over from the original design mockup's own placeholder text,
    /// which never matched the real build (NoBorders.csproj's &lt;Version&gt;,
    /// currently 1.0.0) and would only drift further with every future
    /// release. Reads the assembly's own version instead, so a version bump
    /// in the .csproj is the only place that ever needs to change.
    /// </summary>
    internal static class AppVersion
    {
        // AssemblyVersion always carries a 4th (Revision) component even
        // though the .csproj only sets three (<Version>1.0.0</Version> ->
        // parsed as 1.0.0.0) — dropped here since this app has no
        // per-build revision numbering, so showing it would just be a
        // permanent ".0" with no meaning.
        public static readonly string Display = "v" + (
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "0.0.0");
    }

    /// <summary>
    /// review.md §2.1: games_config.json, noborders.log, and the artwork-cache
    /// folder used to live under AppDomain.CurrentDomain.BaseDirectory — the
    /// app's own install directory. That's fine for a portable dev build, but
    /// a standard user can't write there if the app is ever installed to
    /// somewhere like Program Files, silently breaking settings persistence,
    /// logging, and artwork caching alike. %LOCALAPPDATA%\NoBorders is always
    /// writable by the user running the app, regardless of install location.
    /// </summary>
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

    /// <summary>
    /// A value that changes on every rebuild/republish regardless of
    /// assembly version (which doesn't get bumped per dev iteration) —
    /// shared by EmbeddedWwwroot and WebView2CacheGuard so both invalidate
    /// their own stale caches together on the same "this is a new build"
    /// signal. Environment.ProcessPath rather than Assembly.Location, which
    /// is empty for a PublishSingleFile bundle — confirmed live.
    /// </summary>
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

    /// <summary>
    /// WebView2's own HTTP cache — a persistent Chromium profile at
    /// %LOCALAPPDATA%\NoBorders.WebView2 (its default fallback location,
    /// since this app's own install directory isn't reliably writable) —
    /// is keyed by request URL only and survives across every relaunch AND
    /// rebuild, independent of EmbeddedWwwroot's own extraction cache
    /// below. Confirmed live: a same-day CSS fix, correctly re-extracted to
    /// a fresh wwwroot copy, still rendered as the previous day's stale
    /// build until this profile's Cache/Code Cache folders were cleared —
    /// WebView2 was serving the old response bytes for the same
    /// "css/buttons.css" URL without ever re-requesting it. Gated on the
    /// same exe-mtime stamp as EmbeddedWwwroot, so this is a no-op on every
    /// ordinary launch and only actually clears anything right after a
    /// rebuild/republish. Must run before the first BlazorWebView creates
    /// its WebView2 environment and locks these folders — called from
    /// Main() right after the single-instance mutex is acquired.
    /// </summary>
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
                catch { /* best-effort — a locked file here just means the old cache lingers one more launch */ }
            }

            File.WriteAllText(markerFile, exeStamp);
            AppLogger.LogVerbose($"WebView2 cache cleared (new build detected, stamp={exeStamp}).");
        }
    }

    /// <summary>
    /// Portable single-exe support (see the .csproj's own doc comment on the
    /// wwwroot EmbeddedResource glob): a PublishSingleFile build bundles the
    /// runtime and managed code into NoBorders.exe but never wwwroot —
    /// confirmed live by copying just the exe to an empty folder and getting
    /// a blank BlazorWebView window. Extracts wwwroot's embedded copy to a
    /// real folder under AppPaths.AppDataDir on first run so
    /// ArtworkAwareBlazorWebView's PhysicalFileProvider layers have
    /// something to read even when no physical wwwroot sits next to the
    /// exe.
    ///
    /// Single fixed folder (not one named per assembly version): re-extraction
    /// is gated entirely on the running exe's own last-write time (see
    /// Extract()'s doc comment on the marker file), which already correctly
    /// detects "this is a different build" independent of whatever the
    /// assembly version number says — a rebuild that doesn't bump the
    /// version still gets fresh content on next launch instead of silently
    /// serving a stale previous build's copy forever. A per-version folder
    /// name added nothing on top of that (the marker file is what actually
    /// does the invalidation), it just meant every version bump left its
    /// predecessor's folder sitting on disk forever with nothing left to
    /// ever clean it up — confirmed live: 5 stale version folders
    /// accumulated across releases before this was simplified to reuse one
    /// folder and just overwrite it in place. WebView2's own separate
    /// HTTP-level cache (same "old build, same URL" staleness risk, one
    /// layer up) is unaffected by any of this either way — see
    /// WebView2CacheGuard just above, gated on the same ExeBuildStamp.
    /// </summary>
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

            // Bugfix: keying the marker on version alone meant an unversioned
            // rebuild (same 1.0.0.0 across dev iterations) kept serving
            // whatever wwwroot got extracted the FIRST time that version was
            // ever run — confirmed live serving CSS from a build almost a
            // day stale after a same-day fix, since nothing here ever
            // changes the version number between iterations. ExeBuildStamp
            // changes on every rebuild/republish regardless of version,
            // making the marker self-invalidating without requiring a
            // manual version bump for every UI-only change.
            if (File.Exists(markerFile) && File.ReadAllText(markerFile) == exeStamp) return targetDir;

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);

            foreach (string resourceName in asm.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

                // The prefix is the only part guaranteed to use '/' — the
                // %(RecursiveDir) portion after it uses whatever separator
                // MSBuild produced on this OS (confirmed live: '\' on
                // Windows), so both need normalizing before Path.Combine.
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

        /// <summary>Real file path, exposed for Services/LogTailService.cs to tail
        /// — was private until Phase 6.4 needed a reader.</summary>
        public static string LogPath => _path;

        /// <summary>Settings > Diagnostics "Verbose logging" toggle — synced from
        /// AppSettings.VerboseLogging at startup and on every toggle (see
        /// MainForm's IMainFormBridge.VerboseLogging setter). Gates LogVerbose
        /// only; ordinary Log() calls (warnings, errors, routine one-shot
        /// events) are unaffected regardless of this setting.</summary>
        public static bool VerboseEnabled = false;

        /// <summary>Settings > Diagnostics "N errors in the last session" —
        /// was a hardcoded literal with nothing behind it. A plain in-memory
        /// counter rather than re-parsing the log file: it naturally resets
        /// to 0 on every process start, which is exactly "this session".</summary>
        public static int ErrorCountThisSession { get; private set; }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            if (level == LogLevel.Error) ErrorCountThisSession++;
            try { File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}\n"); }
            catch { /* logging a log-write failure would be circular; nothing to do */ }
        }

        public static void Log(Exception ex, string context)
            => Log($"ERROR in {context}: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);

        /// <summary>Only writes when VerboseEnabled — the "Records every
        /// enforcement tick. Larger file." behavior the Diagnostics page's
        /// toggle describes but, until now, never actually did anything.</summary>
        public static void LogVerbose(string message)
        {
            if (VerboseEnabled) Log(message, LogLevel.Info);
        }
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

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxLength);
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// Bugfix: a pinned taskbar icon click (or any second launch while
        /// already running) relies on the HWND_BROADCAST below to reach the
        /// hidden/minimized first instance's WndProc — confirmed live that
        /// this broadcast can silently fail to be delivered at all (the
        /// window never restores, no error, nothing in noborders.log, since
        /// the second instance has already exited by the time anyone could
        /// notice), while posting the exact same message directly to that
        /// window's own handle works every time. Finds it here instead of
        /// trusting the broadcast to reach it — title is "NoBorders" or
        /// "NoBorders (Administrator)" depending on elevation, so this
        /// matches by prefix rather than requiring an exact FindWindow hit.
        ///
        /// Bugfix: title-matching alone isn't enough — confirmed live that
        /// an unrelated OneCommander window (a file-manager tab happened to
        /// be titled exactly "NoBorders", presumably a folder name) matched
        /// first and silently ate the message, since EnumWindows walks every
        /// top-level window on the desktop, not just this app's own. Now
        /// also checks the window's owning process is actually named
        /// "NoBorders", not just its title.
        /// </summary>
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
                catch { return true; } // process exited mid-enumeration — not it

                found = hWnd;
                return false; // stop enumerating
            }, IntPtr.Zero);
            return found;
        }

        // Exposed so MainForm can release the single-instance lock before
        // spawning an elevated copy of itself (see RestartAsAdmin). Without
        // releasing it first, the new elevated process would see the mutex
        // still held and assume another copy is already running.
        public static Mutex? AppMutex;

        [STAThread]
        private static void Main()
        {
            // review.md §1.4: without these, an exception thrown from inside
            // a WinForms event handler (a button click, a Timer tick — the
            // routine case, not a startup failure) never reaches the
            // try/catch around Application.Run below at all — WinForms'
            // message pump catches those itself and, with no
            // ThreadException/UnhandledException subscriber, either shows
            // the bare default .NET crash dialog or just terminates, with
            // nothing written to noborders.log explaining why. Registered
            // before anything else runs so no code path is left uncovered.
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

                // Direct-to-window first (see FindRunningInstanceWindow's own
                // doc comment for why — the broadcast below isn't reliable
                // enough to be the only delivery path). Still also broadcasts
                // regardless, cheap insurance for the rare case where the
                // first instance's window doesn't exist yet (a genuine race
                // right at startup, before it's created its own window).
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
                // Only release if we still own it — RestartAsAdmin may have
                // already released it in order to hand off to the elevated copy.
                try { AppMutex.ReleaseMutex(); } catch { /* not owned by this thread/instance — nothing to release */ }
                AppMutex.Dispose();
            }
        }
    }

    /// <summary>
    /// Phase 8.3 bugfix: a plain <see cref="BlazorWebView"/> only serves wwwroot
    /// content it knew about at build time — a `dotnet build` (as opposed to
    /// `dotnet publish`) run loads static content through the SDK's dev-time
    /// static-web-assets manifest, which only knows about files that existed
    /// at build time. `ArtworkService` (Services/ArtworkService.cs) downloads
    /// hero/icon art into `contentRootDir`/artwork-cache at runtime, long
    /// after that manifest was generated, so it's invisible to it. Layering a
    /// plain <see cref="PhysicalFileProvider"/> rooted at `contentRootDir` on
    /// top of the SDK's own provider closes that gap defensively for any
    /// runtime-written asset. Confirmed via temporary logging this was NOT
    /// actually the reason art wasn't rendering, though (`contentRootDir`
    /// here already matched exactly where ArtworkService writes) — the real
    /// bug was in ArtworkService's own cache filenames (see
    /// SanitizeFileName's doc comment: BlazorWebView passes the still-percent
    /// -encoded request path straight to the file provider, so a cached file
    /// with a literal space in its name never matched a "%20" lookup no
    /// matter which physical folder was checked). Kept anyway since it's a
    /// correct, harmless safety net for future runtime-written wwwroot
    /// content — a no-op in a real `dotnet publish` build, where
    /// `contentRootDir` is already right on its own.
    ///
    /// review.md §2.1: ArtworkService now caches into %LOCALAPPDATA%\NoBorders
    /// (AppPaths.AppDataDir), not contentRootDir, since the install directory
    /// isn't guaranteed writable. contentRootDir itself can't be redirected —
    /// BlazorWebView derives it from HostPage relative to the app's own base
    /// directory, with no supported override — so a third PhysicalFileProvider
    /// rooted at AppDataDir is layered on top instead. A request for
    /// "artwork-cache/xyz.png" resolves against AppDataDir\artwork-cache\xyz.png,
    /// while everything else (index.html, css, js) still comes from the
    /// install-directory providers, which never had an artwork-cache folder
    /// to conflict with in the first place.
    ///
    /// Portable single-exe support: adds EmbeddedWwwroot.ExtractedDir as a
    /// fourth layer, between the install-directory providers and
    /// AppDataDir's artwork-cache one. For a traditional install (wwwroot
    /// physically sitting next to the exe) the first two providers already
    /// resolve everything and this layer never gets reached. For a
    /// standalone portable exe (no physical wwwroot at all — contentRootDir
    /// points at a directory that doesn't exist), those first two providers
    /// simply never find anything and CompositeFileProvider falls through
    /// to this one, which always has a real, already-extracted copy.
    /// </summary>
    internal sealed class ArtworkAwareBlazorWebView : BlazorWebView
    {
        public override IFileProvider CreateFileProvider(string contentRootDir) =>
            new CompositeFileProvider(
                base.CreateFileProvider(contentRootDir),
                SafePhysicalFileProvider(contentRootDir),
                new PhysicalFileProvider(EmbeddedWwwroot.ExtractedDir),
                new PhysicalFileProvider(AppPaths.AppDataDir));

        /// <summary>
        /// PhysicalFileProvider's own constructor throws if the root doesn't
        /// exist — confirmed live: a portable single-exe with no physical
        /// wwwroot next to it threw here, which crashed BlazorWebView's
        /// whole StartWebViewCoreIfPossible() call and left a blank window
        /// (the global Application.ThreadException handler caught it, but
        /// by then BlazorWebView had already given up on ever navigating).
        /// contentRootDir is exactly this "might not exist" case for a
        /// portable exe — unlike EmbeddedWwwroot.ExtractedDir and
        /// AppPaths.AppDataDir just below, which both guarantee their own
        /// directory exists before ever returning it.
        /// </summary>
        private static IFileProvider SafePhysicalFileProvider(string root) =>
            Directory.Exists(root) ? new PhysicalFileProvider(root) : new NullFileProvider();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM BRIDGE
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// review.md §4.1: AppStateService used to take 59 (then 75, after this
    /// session's own additions) positional Func/Action constructor parameters
    /// — nearly all the exact same shape (Func&lt;bool&gt;, Action, etc.), so
    /// the compiler could not catch two adjacent ones being swapped; only a
    /// manual read of the ~90-line call site in CreateBlazorServices() would
    /// catch it. This interface replaces that entire positional list with
    /// named members: MainForm implements it explicitly (below), and
    /// AppStateService takes a single <c>IMainFormBridge</c> instead — every
    /// binding is now resolved by name at compile time, so a swap is a
    /// compile error, not a silent behavioral bug. Members are named to match
    /// AppStateService's own existing public API 1:1 (this interface exists
    /// purely so that API has something safer than a delegate list behind
    /// it) except where MainForm already had a same-shaped real method,
    /// which is reused as-is rather than introducing a second name for the
    /// same thing.
    /// </summary>
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

    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM
    // ══════════════════════════════════════════════════════════════════════════════

    internal sealed partial class MainForm : Form, IMainFormBridge
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
        private const int    WM_SETTINGCHANGE       = 0x001A; // fires on taskbar/app theme toggle, among other broadcast settings changes
        private const int    HSHELL_WINDOWCREATED   = 1;
        // Phase 8.9/9: frameless-chrome custom title bar (see CreateParams'
        // WS_CAPTION removal, BeginWindowDrag, and WindowControls.razor).
        // Dragging the Blazor-drawn header does NOT use the classic
        // WM_NCHITTEST-reports-HTCAPTION recipe — confirmed live that
        // message never reaches this window once BlazorWebView covers the
        // client area (WebView2's own child HWND fields it first) — see
        // BeginWindowDrag's doc comment for the real mechanism
        // (WM_NCLBUTTONDOWN, sent explicitly from a genuine Blazor mousedown
        // event instead). HTCAPTION is still needed as that message's
        // wParam value.
        private const int    WM_NCLBUTTONDOWN       = 0x00A1;
        private const int    HTCAPTION               = 2;
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
        // BeginWindowDrag's mechanism — see its doc comment.
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

        // ── State ────────────────────────────────────────────────────────────────
        private AppSettings  _settings    = new AppSettings();
        private GameConfig?  _selectedGame;
        private bool         _forceClose  = false;
        private bool         _updatingUI  = false;
        private string       _activeScope = string.Empty; // currently viewed monitor scope

        // Single-level Undo (MIGRATION_PLAN.md Phase 4.13) — net-new per the
        // README, no prior WinForms UI or handler existed for this. Captures
        // the affected game's pre-change GameName/RegexPattern/Profiles right
        // before BtnSaveGame_Click applies a Save Changes commit; Undo restores
        // exactly that snapshot, then clears it (a second Undo click has
        // nothing further to revert to, matching "the last committed change",
        // singular, not a multi-step history). _undoTarget doubles as the
        // "is there anything to undo" flag and as the "for which game" scope —
        // switching to a different game correctly disables Undo without
        // discarding the pending snapshot, in case the user switches back.
        private GameConfig? _undoTarget;
        private string _undoGameName = string.Empty;
        private string _undoRegexPattern = string.Empty;
        private MatchTargetMode _undoMatchTarget;

        // review.md §3: MatchTarget's pending-edit buffer. GameName/RegexPattern
        // use real hidden WinForms TextBoxes here (_txtGameName/_txtRegex) since
        // those controls pre-date the Blazor migration and BtnSaveGame_Click
        // still reads them directly — but MatchTarget never had a WinForms
        // control to begin with, so a plain field is simpler and just as
        // correct; there's no legacy handler this needs to stay in lockstep with.
        private MatchTargetMode _pendingMatchTarget;
        private Dictionary<string, GameDisplayProfile> _undoProfiles = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _configPath = Path.Combine(
            AppPaths.AppDataDir, "games_config.json");

        // ── Built-in ignore list (Settings > Ignore List, Phase 8.4) ───────────────
        // Permanently baked-in and never shown/editable in Settings — kept
        // separate from _settings.IgnoredProcesses (the user's own additions)
        // specifically so the Ignore List page only ever lists what the user
        // actually chose to add, not ~100 built-in system-process entries the
        // vast majority of users would never touch. GetOpenWindowEntries/
        // TryGetHotkeyTargetExe check both sets; "Clear All" only ever empties
        // the user's own set, this one included unconditionally regardless.
        // Covers the full range of Windows shell, UWP infrastructure, system
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

            // ── NoBorders itself ─────────────────────────────────────────────────
            // GetOpenWindowEntries (Add Running App modal, Matching tab's live-test
            // list) has no PID-based self-check the way TryGetHotkeyTargetExe does
            // (see its "NoBorders itself" comment below) — NoBorders has a visible
            // main window and title, so without this it showed up as a pickable
            // "running app" in its own picker. AssemblyName is "NoBorders"
            // (NoBorders.csproj), so Process.ProcessName + ".exe" is "NoBorders.exe";
            // OrdinalIgnoreCase already covers any casing.
            "noborders.exe",
        };

        private readonly Dictionary<string, Image>     _iconCache      = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MonitorItem>             _monitors       = new List<MonitorItem>();
        // Foreground window captured in WndProc before the hotkey is dispatched.
        // GetForegroundWindow() called *inside* HotkeyAdd() can return our own
        // window handle by the time WM_HOTKEY is processed.
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private bool   _wasHiddenBeforeSleep = false; // tracks tray state across sleep/wake
        // Set whenever the form goes (or stays) hidden-to-tray in a way that
        // means WebView2 never got to paint while actually visible: either
        // OnWake() re-hiding the form after a sleep cycle (its compositor
        // loses the swap chain to the sleep/wake GPU reset), or OnLoad's
        // -minimized cold-start Hide() (its compositor never painted a first
        // frame at all, since a full reboot auto-launches straight into
        // -minimized before BlazorWebView ever gets shown). Either way the
        // window comes back as a blank grey rectangle the next time
        // RestoreFromTray() (MainForm.Tray.cs) makes it visible. Cleared
        // there once the repaint workaround has run.
        private bool   _webViewNeedsRepaintAfterWake = false;
        private bool   _isElevated = false; // true if this process is running as Administrator
        // Games we've already shown an elevation-related toast for this session,
        // so the warning fires once per game rather than every enforcement tick.
        private readonly HashSet<string> _elevationWarnedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Thrash guard: a game whose in-engine settings got reset (resolution
        // changed, window mode toggled, etc.) while its NoBorders profile stayed
        // active can end up fighting us — the engine keeps restoring its own
        // style/size and we keep stripping it right back, every single
        // enforcement tick. That repeated SetWindowLong/SetWindowPos churn during
        // the engine's own mode-switch (menus, intro videos) has been observed to
        // wedge the video driver hard enough to require a reboot. These track how
        // many *consecutive* ticks in a row needed a real change for a given
        // window, and — once backed off — until when we should stop touching it.
        private readonly Dictionary<IntPtr, int>      _consecutiveEnforceChanges = new Dictionary<IntPtr, int>();
        private readonly Dictionary<IntPtr, DateTime> _enforceBackoffUntil       = new Dictionary<IntPtr, DateTime>();
        private readonly HashSet<string> _thrashWarnedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int      ThrashTickThreshold = 4;                 // consecutive changed ticks before backing off
        private static readonly TimeSpan ThrashBackoff = TimeSpan.FromSeconds(15);

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

        // Phase 7.0: the permanent Blazor mount — held so it's inspectable/
        // disposable if ever needed, though nothing currently reaches back
        // into it (all real interaction goes through AppStateService).
        private BlazorWebView _blazorWebView = null!;

        // Resolved once, right after _blazorServices is built (Phase 4.1) — the
        // same singleton instance any Razor component's @inject resolves, since
        // AppStateService is registered via AddSingleton. Held so the
        // constructor can subscribe existing WinForms events (below) to
        // AppStateService.RaiseChanged() without those events' own handlers
        // needing any Blazor awareness — see AppStateService.Changed's doc
        // comment for why MainForm, not the service, owns raising it.
        private AppStateService _appState = null!;

        private ServiceProvider CreateBlazorServices()
        {
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            // Phase 6.4: tails AppLogger.LogPath for the Activity Log view. One
            // instance for the process's lifetime — it owns its own polling timer
            // and in-memory buffer regardless of how many BlazorWebViews resolve it.
            services.AddSingleton(_ => new Services.LogTailService(AppLogger.LogPath));
            // Phase 8: live accessor for the API key, same "read through a
            // delegate" convention as AppStateService — a key pasted in
            // Settings takes effect on the very next fetch, no restart needed.
            services.AddSingleton(_ => new Services.ArtworkService(
                AppPaths.AppDataDir,
                () => _settings.SteamGridDbApiKey));
            // Lazy factory — only runs whenever something first resolves
            // AppStateService, which happens long after LoadConfig() has already
            // run; IMainFormBridge's members re-read live fields on every access
            // rather than capturing a snapshot (same as the Func<T> accessors
            // this replaced), so this is correct regardless of exact timing.
            // review.md §4.1: this used to be a single ~35-line call passing 75
            // positional Func/Action arguments — see IMainFormBridge's doc
            // comment (just above the MainForm class) for why that was a risk,
            // and MainForm's own "IMainFormBridge" region below for the named
            // members that replaced them.
            services.AddSingleton(_ => new AppStateService(this));
            return services.BuildServiceProvider();
        }

        // ════════════════════════════════════════════════════════════════════════
        // IMainFormBridge — explicit implementation
        // ════════════════════════════════════════════════════════════════════════
        // review.md §4.1: named replacement for what used to be 75 positional
        // constructor arguments passed to AppStateService (see
        // IMainFormBridge's own doc comment). Explicit implementation (the
        // `IMainFormBridge.Member` syntax) keeps every one of these off
        // MainForm's own public surface — only reachable through the
        // interface, exactly as narrow as the old delegate list was — while
        // still being a single named, compiler-checked member instead of a
        // positional slot. Each line is either a direct forward to an
        // existing private method of the same name (no behavior change at
        // all) or, for the ones that previously had no real member — just a
        // lambda closing over a private field — the smallest possible
        // wrapper around that same field.
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

            // Moved here from Main() (still runs before _blazorWebView's own
            // creation further down, which is the actual ordering constraint
            // — WebView2 must not have locked its cache folders yet) so that
            // AppLogger.VerboseEnabled is already synced from LoadConfig
            // above by the time this runs. Calling it from Main(), before
            // MainForm even exists, meant the verbose toggle could never be
            // synced yet at that point, so a rebuild's cache-clear could
            // never actually respect the user's saved preference.
            WebView2CacheGuard.ClearIfStaleBuild();

            BuildUI();
            SetupTrayIcon();
            RefreshMonitors();
            PopulateGamesList();

            // Phase 8.7: establish a real selection immediately at startup.
            // PopulateGamesList() always sets _selectedGame back to null, and
            // originally nothing here ever re-selected a row — the old
            // WinForms UI just hid the whole detail pane (SetDetailVisible
            // (false)) until a real click happened. But Blazor's rail/Target
            // Monitor/Hero banner have always shown a "display-only fallback"
            // pick (DisplayedGame => SelectedGame ?? Games.FirstOrDefault())
            // as if it WERE selected — while Width/Height/OffsetX/OffsetY
            // (backed by the shadow NumericTextBox fields, only ever
            // populated by LstGames_SelectedIndexChanged) stayed at their
            // zeroed default, since that handler had never actually run.
            // Confirmed live: fresh launch showed the rail's first game
            // highlighted and its monitor named correctly, but 0×0 fields,
            // "cursor free", and a blank Result Preview well. Now that
            // Blazor is the only UI (Phase 7.0), there's no more reason to
            // leave real selection state unestablished — this makes the
            // fallback's visual claim true instead of merely cosmetic.
            // LstGames_SelectedIndexChanged is already subscribed (BuildUI,
            // above) and does the real work; Blazor's _appState.RaiseChanged
            // subscriber isn't wired until after this, so nothing needs to
            // be notified yet — Blazor's first render just reads correct
            // state directly.
            if (_settings.Games.Count > 0) _lstGames.SelectedIndex = 0;

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
            _appState       = _blazorServices.GetRequiredService<AppStateService>();

            // Additional subscribers on top of BuildUI()'s own handler
            // subscriptions above — those handlers are completely untouched;
            // these just also notify Blazor once each has finished, regardless
            // of whether the change originated from the WinForms control or
            // from the corresponding AppStateService method (Phase 4.1-4.10).
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
            // Phase 8.4: Width/Height/Offset X/Y steppers do NOT get the same
            // "+= (s,e) => _appState.RaiseChanged()" treatment as the
            // checkboxes above — confirmed live in NumericTextBox's own
            // source (this file, ~line 241): its Value setter sets
            // _updatingText = true before touching Text, and OnTextChanged
            // (the only place ValueChanged is actually raised) early-returns
            // whenever _updatingText is true. So a *programmatic* `.Value =`
            // assignment — which is the only way to change it at all now
            // that Phase 7.0 covers these controls with the BlazorWebView —
            // never raises ValueChanged. AdjustWidth/AdjustHeight/
            // AdjustOffsetX/AdjustOffsetY/their MonitorDefault counterparts
            // below call _appState.RaiseChanged() themselves instead.

            // ════════════════════════════════════════════════════════════════
            // PHASE 7.0 CUTOVER (MIGRATION_PLAN.md) — permanent Blazor mount.
            // ════════════════════════════════════════════════════════════════
            // _pnlNav (the real "Games/Settings" pill bar, Dock=Top, 42px) and
            // _lblStatus (the real native status label, Dock=Bottom, 24px) —
            // because Dock=Fill always yields to sibling Top/Bottom/Left/Right
            // docks in the same container regardless of add order — have been
            // visible around every Blazor screen tested this entire migration
            // (every temp-mount recipe run added its BlazorWebView the same
            // way this one does; _lblStatus specifically was caught live
            // during this item's own testing — ToggleGameActive's real
            // ShowStatus call left "ZZZ Notepad Game — borderless disabled."
            // showing through beneath Blazor's own status bar). Hiding both
            // reclaims their space; neither has other dependents (_pnlNav only
            // forwards clicks to _tabs.SelectedIndex; _lblStatus.Text is a
            // pure write target for ShowStatus) — nothing reads either's
            // Visible property.
            _pnlNav.Visible = false;
            _lblStatus.Visible = false;

            // _tabs (and everything inside it — every Games/Settings control
            // Phase 4's "trigger the real control" pattern depends on) is
            // deliberately left fully alive, Visible=true throughout its own
            // tree — only fully covered, never hidden or torn down. That's
            // load-bearing, not incidental: PerformClick()/SelectedIndex
            // assignment/etc. all still run through these real controls.
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
                AppLogger.LogVerbose($"Shell hook registered (msg={_wmShellHook}, hwnd={this.Handle}).");
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

            // Persisted "Always start as Administrator" preference (Settings >
            // Permissions) — checked here, not the constructor, for the same
            // reason as -minimized below: RestartAsAdmin() calls
            // CaptureWindowBounds(), which needs a real handle/WindowState to
            // read correct values from. No prompt here — a standing
            // preference the user already set, not a fresh decision.
            if (_settings.AlwaysRunAsAdmin && !_isElevated)
            {
                RestartAsAdmin();
                return; // relaunching — nothing else in OnLoad matters for this instance
            }

            // Scan processes already running before we started.
            ScanExistingWindows();

            // Handle -minimized launch argument (Start with Windows minimized).
            // Must be here rather than the constructor so BeginInvoke is safe.
            if (Environment.GetCommandLineArgs().Contains("-minimized", StringComparer.OrdinalIgnoreCase))
            {
                HideForMinimizedStartup();
            }
        }

        /// <summary>
        /// Bugfix: -minimized cold-start used to run immediately —
        /// this.WindowState = Minimized, this.ShowInTaskbar = false,
        /// Hide(). Confirmed live via a simulated reboot-launch (still
        /// reproduced with only the ShowInTaskbar/Hide half deferred, which
        /// ruled that half out — the WindowState assignment alone is
        /// sufficient): BlazorWebView's CreateCoreWebView2ControllerAsync()
        /// call, kicked off moments earlier when this.Handle was first
        /// touched above in this same OnLoad, is still in flight against
        /// this top-level window at the point WindowState flips to
        /// Minimized. WebView2 requires its parent window to actually be in
        /// a normal (non-iconic) state while the controller is being
        /// created — minimizing it out from under that pending native call
        /// aborts it (Application.ThreadException logs "Operation aborted
        /// (0x80004004 (E_ABORT))" from deep inside
        /// CoreWebView2Environment.CreateCoreWebView2ControllerAsync), and
        /// nothing ever retries: CoreWebView2 stays permanently null for the
        /// rest of the process's life, so the window is blank grey forever
        /// once shown, not just until a repaint. This is what full-PC-reboot
        /// users were actually hitting — every reboot auto-launches with
        /// -minimized, so every reboot lost this race.
        ///
        /// Fix: don't touch WindowState/ShowInTaskbar/Hide until CoreWebView2
        /// has actually finished initializing (or already has, on whatever
        /// future runtime makes this synchronous) — nothing in this class
        /// depends on -minimized startup being instantaneous, so there is no
        /// downside to waiting the extra tens-of-milliseconds this takes.
        /// _webViewNeedsRepaintAfterWake (see its own doc comment) is
        /// deliberately NOT set here: that flag exists to fix a control that
        /// initialized fine but lost its already-created swap chain, which
        /// isn't what happens here — by the time this runs, CoreWebView2 has
        /// either already succeeded (nothing to repair) or the app never
        /// reaches this callback at all (still broken, but no worse than
        /// before — see AppLogger for the underlying WebView2/OS-level
        /// failure in that case).
        /// </summary>
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
        /// <summary>
        /// Blazor Settings > Permissions "Restart as Administrator" button's
        /// entry point — shows ElevationDialog (the in-app Blazor
        /// notification, generic wording since no specific game triggered
        /// this) rather than a native MessageBox; RestartAsAdmin() itself
        /// only runs once the user clicks that dialog's own "Restart As
        /// Admin" button.
        /// </summary>
        private void ConfirmRestartAsAdmin() => ShowElevationDialog();

        /// <summary>Statusbar user-type indicator's click handler while already
        /// elevated (MainShell.razor) — same ElevationDialog host Form as
        /// ConfirmRestartAsAdmin, just the reverse direction.</summary>
        private void ConfirmRestartAsStandardUser() => ShowElevationDialog(toStandardUser: true);

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
                // review.md §1.1: ArgumentList (not a manually quoted Arguments
                // string) — CreateProcess-correct escaping per element, so an
                // argument containing an embedded `"` can't break out of its
                // own boundary and inject extra tokens into this elevated
                // relaunch's command line.
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute  = true,
                    Verb             = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                    psi.ArgumentList.Add(a);
                AppLogger.Log($"RestartAsAdmin: relaunch args = [{string.Join(", ", psi.ArgumentList)}]");

                // Flush any pending debounced save immediately so the new elevated
                // instance loads fully up-to-date settings rather than whatever was
                // on disk before the last 600ms autosave window elapsed. Also
                // captures window bounds (same as a real close) so the elevated
                // copy reopens at the same size/position, not the 1200×800 default.
                _saveDebounce.Stop();
                CaptureWindowBounds();
                SaveConfig();
                AppLogger.Log("RestartAsAdmin: config flushed to disk before restart.");

                // Release the single-instance lock BEFORE spawning the new process,
                // otherwise the elevated copy would see it still held and simply
                // bring this (soon-to-close) window to the front instead of starting.
                //
                // ReleaseMutex() alone isn't enough (confirmed live: the elevated
                // copy launched, then vanished with zero logging and no crash
                // record — Main()'s `if (!isNew) { ...; return; }` early-exit,
                // silent by design). A named OS mutex stays alive system-wide as
                // long as ANY handle to it is open, regardless of who currently
                // "owns" it — ReleaseMutex only gives up ownership, it doesn't
                // close this process's handle. That handle isn't actually closed
                // until Main()'s own `finally { AppMutex.Dispose(); }` runs, which
                // only happens after Application.Run() returns — i.e. after the
                // full WinForms/BlazorWebView teardown below completes, easily
                // slower than how fast the elevated child starts via UAC. So the
                // child's own `new Mutex(true, NAME, out isNew)` was finding the
                // still-existing (if unowned) object and getting isNew=false,
                // assuming a copy was already running. Disposing here — not just
                // releasing — actually closes this process's handle immediately,
                // so the object is fully gone before the child ever asks.
                // Mutex.Dispose() is safe to call twice, so Main()'s own later
                // `finally` block disposing it again is a harmless no-op.
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

                // Only now — confirmed launched, not just attempted — does this
                // count as the user actually choosing to run elevated. Setting
                // this any earlier (e.g. before Process.Start) would persist it
                // even if the user then cancelled the UAC prompt, causing a
                // repeat-prompt-every-launch loop for someone who declined once.
                // The new elevated copy already read its own config moments ago
                // (the flush above), so this needs its own save — it only
                // matters for a *future* launch, not this handoff.
                _settings.AlwaysRunAsAdmin = true;
                SaveConfig();

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
        /// Statusbar user-type indicator's "restart without Administrator
        /// rights" flow — the reverse of RestartAsAdmin above. Windows gives a
        /// running process no way to drop its own elevated token, so this
        /// can't just be RestartAsAdmin without the "runas" verb: launching
        /// exePath directly, even with UseShellExecute and no verb, still
        /// inherits THIS process's elevated token. Instead it asks the
        /// already-running (non-elevated) explorer.exe shell to launch the
        /// target on our behalf — the well-known de-elevation trick, same
        /// effect as right-click > "Run as different user" minus the prompt —
        /// so the new process gets explorer's medium-integrity token instead.
        /// </summary>
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

                // explorer.exe, not exePath — see the method doc comment above.
                // Arguments are quoted individually then joined, same
                // CreateProcess-correct-escaping intent as RestartAsAdmin's
                // ArgumentList (ProcessStartInfo.Arguments here instead
                // because the target being launched, exePath, is itself one
                // of explorer.exe's own arguments and needs its own
                // quoting — ArgumentList only escapes for the process actually
                // being started, which here is explorer.exe, not NoBorders).
                var psi = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute  = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                var relaunchArgs = Environment.GetCommandLineArgs().Skip(1)
                    .Select(a => "\"" + a.Replace("\"", "\\\"") + "\"");
                psi.Arguments = string.Join(" ", new[] { "\"" + exePath + "\"" }.Concat(relaunchArgs));
                AppLogger.Log($"RestartAsStandardUser: relaunch via explorer.exe, args = '{psi.Arguments}'");

                // Same flush-then-release sequencing as RestartAsAdmin — see
                // that method's comments for why each step is ordered this way.
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

                // Unlike RestartAsAdmin's runas launch, a null Process here is
                // NOT necessarily a failure: when explorer.exe is already
                // running (the normal case), CreateProcess forwards the
                // request to that existing instance via DDE instead of
                // starting a genuinely new process, and Process.Start
                // legitimately returns null for that path even on success.
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

        /// <summary>
        /// Shown once, right after a newly added game's borderless settings
        /// failed to apply — which ApplyBorderless records via
        /// _elevationWarnedGames the first time a style/position Win32 call
        /// fails while NoBorders isn't elevated. That combination almost always
        /// means the game (or its anti-cheat) runs elevated and NoBorders
        /// currently doesn't, so this offers the same restart already available
        /// from Settings → Restart as Administrator, right at the point it's
        /// actually needed instead of leaving the user to notice a toast or dig
        /// through the log. Shows ElevationDialog (naming the game) rather
        /// than a native MessageBox — same in-app-notification move as
        /// ConfirmRestartAsAdmin, and RestartAsAdmin() only runs from that
        /// dialog's own button, not synchronously here.
        /// </summary>
        private void PromptRestartAsAdmin(string gameName) => ShowElevationDialog(gameName);

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
            // Was fixed-width (800/800) for the WinForms UI alone. Widened and made
            // freely resizable so the window is comfortably large enough by default
            // for both the current Settings tab content (previously needed a manual
            // resize to see the Permissions section without scrolling) and the
            // Blazor screens being built in MIGRATION_PLAN.md Phase 2, which are
            // authored at 1120px wide per design-handoff/README.md and aren't
            // responsive below that yet.
            // Design minimum from the 1120px-wide Blazor content plus chrome. On a
            // screen whose work area is smaller than this — a sub-1080p laptop panel,
            // or a higher DPI scale factor shrinking the effective work area below it
            // even on a 1080p+ display — enforcing this as a hard floor would push part
            // of the window off-screen, forcing the user to manually move or maximize
            // it just to see everything. Shrink the floor to fit the target screen
            // instead; content may need internal scrolling at the smaller size, but the
            // whole window stays reachable without manual intervention.
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
            this.MaximumSize     = Size.Empty; // no maximum — freely resizable/maximizable

            // Phase 8.4: restore the last real (non-minimized) size/position if one
            // was saved and still makes sense on the monitors currently connected —
            // a saved position from a since-unplugged/reconfigured monitor would
            // otherwise open the window off-screen, unreachable without Windows'
            // own "move window" keyboard recovery. Falls back to the original
            // fixed 1200×800 CenterScreen default on first run, or whenever the
            // saved position doesn't check out against Screen.AllScreens. Also
            // capped to the target screen's work area so the window never opens
            // larger than the screen it's about to appear on.
            var savedSize = new Size(
                Math.Min(Math.Max(_settings.WindowWidth,  this.MinimumSize.Width), workArea.Width),
                Math.Min(Math.Max(_settings.WindowHeight, this.MinimumSize.Height), workArea.Height));
            bool savedPositionOnScreen = hasSavedPosition &&
                Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(savedLocation, savedSize)));

            this.Size = savedSize;
            if (savedPositionOnScreen)
            {
                this.StartPosition = FormStartPosition.Manual;
                // Clamp so a saved position near a screen edge can't leave part of
                // the (possibly larger, since-changed) window hanging off-screen.
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

            // Applied after Location/Size are set above so un-maximizing later
            // restores to the correct saved bounds rather than whatever the
            // default constructor geometry would have been.
            if (_settings.WindowMaximized) this.WindowState = FormWindowState.Maximized;

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

        /// <summary>
        /// Phase 8.9: WindowControls.razor's minimize button — the frameless
        /// custom title bar's replacement for the native minimize button
        /// (Adaptation Decision #1 originally kept the native title bar
        /// specifically to avoid needing this; superseded by the user's
        /// explicit request to match the mock's inline window-control
        /// buttons). Just sets WindowState, exactly what a native minimize
        /// click already does under the hood — OnResize's existing
        /// MinimizeToTray handling picks it up unchanged, nothing here
        /// duplicates that logic.
        /// </summary>
        private void MinimizeWindow() => this.WindowState = FormWindowState.Minimized;

        /// <summary>Same shape as <see cref="MinimizeWindow"/>, for the maximize/restore button — toggles WindowState, same as double-clicking the (now-Blazor-drawn) title bar drag region already does via WM_NCHITTEST's HTCAPTION.</summary>
        private void ToggleMaximizeWindow() =>
            this.WindowState = this.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;

        /// <summary>Same shape as <see cref="MinimizeWindow"/>, for the close button — calls Close(), exactly what a native close click already does, so OnFormClosing's existing MinimizeToTray-on-close handling runs unchanged.</summary>
        private void CloseWindow() => this.Close();

        /// <summary>Drives WindowControls.razor's maximize/restore glyph swap (▢ vs ❐).</summary>
        private bool IsWindowMaximized() => this.WindowState == FormWindowState.Maximized;

        /// <summary>
        /// Phase 8.9 bugfix: WindowControls.razor's drag region (the header
        /// row, minus its own buttons) — replaces a WM_NCHITTEST-based
        /// approach confirmed live NOT to work once BlazorWebView covers the
        /// whole client area (see CreateParams' doc comment for the WS_CAPTION
        /// removal this now runs against). Called from a real
        /// Blazor @onmousedown, which WebView2 DOES deliver normally (it's
        /// ordinary client-area input, not a non-client hit-test query) —
        /// ReleaseCapture() lets go of whatever implicit mouse capture the
        /// click just started, then SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)
        /// asks DefWndProc to do exactly what it would have done if the OS's
        /// own hit-testing had classified the original click as HTCAPTION:
        /// start its real, native, modal caption-drag loop — Aero Snap,
        /// restore-then-follow-cursor from maximized, and all, genuinely at
        /// the OS level, not anything reimplemented here. Standard technique
        /// for exactly this "custom title bar hosted in an embedded browser
        /// control" scenario (same shape web-content-hosted apps generally
        /// use), not specific to this codebase.
        /// </summary>
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

        // Monitor enumeration (RefreshMonitors, SyncMonitorComboBoxes,
        // BuildGdiToFriendlyMap, DetectDisplays, TrimNull, IsIgnoredName,
        // GetPrimaryMonitorId, RestoreComboSelection) moved to
        // MainForm.Monitors.cs — review.md §4.2.

        // Config load/save (MigrateLegacyAppDataIfNeeded, LoadConfig,
        // SaveConfig, QueueSave) moved to MainForm.Config.cs — review.md §4.2.

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
                    string title = GetWindowTitle(p.MainWindowHandle);
                    foreach (var g in _settings.Games)
                    {
                        if (running.Contains(g)) continue;
                        if (g.IsMatch(exe, title)) running.Add(g);
                    }
                }
                catch { /* process may have exited */ }
            }

            if (!running.SetEquals(_runningGames))
            {
                _runningGames = running;
                RefreshGamesListOrder();

                // Phase 4.14 — the only genuinely new state EnforceTimer_Tick
                // produces that Blazor doesn't already reflect via some other
                // already-wired path (see that method's own doc comment for
                // why). RefreshGamesListOrder() sorts _settings.Games itself
                // (not just the WinForms ListBox), and AppStateService.Games
                // is a direct passthrough to that same list, so the Blazor
                // rail's running-games-first order comes free from this one
                // notification — no separate Razor sort logic needed.
                _appState.RaiseChanged();
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

        /// <summary>
        /// Blazor rail click's entry point (MIGRATION_PLAN.md Phase 4.1) — selects
        /// exactly as clicking the corresponding WinForms list row would, by finding
        /// that row and setting `SelectedIndex`, which fires the real
        /// `LstGames_SelectedIndexChanged` below unchanged. Does not touch
        /// `_selectedGame` directly, so there is exactly one code path that ever
        /// assigns it, regardless of which UI triggered the selection.
        /// </summary>
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
            _txtGameName.Text  = _selectedGame.GameName;
            _txtRegex.Text     = _selectedGame.RegexPattern;
            _pendingMatchTarget = _selectedGame.MatchTarget;

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

        /// <summary>
        /// Blazor target-monitor dropdown's entry point (MIGRATION_PLAN.md Phase
        /// 4.2) — selects a scope exactly as picking it in the WinForms combo
        /// would, by setting `SelectedItem`, which fires the real, unchanged
        /// `CmbMonitor_SelectedIndexChanged` above.
        /// </summary>
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

        /// <summary>
        /// Blazor hero primary button's entry point (MIGRATION_PLAN.md Phase 4.3)
        /// — flips `_chkActive.Checked`, which fires the real, unchanged
        /// `ChkActive_CheckedChanged` above. No-ops with no selected game, same
        /// as the WinForms button being unreachable in that state.
        /// </summary>
        private void ToggleGameActive()
        {
            if (_selectedGame == null) return;
            _chkActive.Checked = !_chkActive.Checked;
        }

        /// <summary>
        /// Blazor hero's "Re-Apply" button — previously had no @onclick at
        /// all (confirmed via grep: no backing handler anywhere in
        /// program.cs), so clicking it silently did nothing. Re-applies
        /// borderless to the selected game's currently tracked window, the
        /// same mechanism HotkeyRefresh already uses for "whatever's in the
        /// foreground" — this targets the hero's own game instead, matching
        /// _selectedGame's own no-fallback convention (ToggleGameActive
        /// above, this button's sibling, does the same).
        /// </summary>
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

            // Force a fresh monitor refresh before applying so that if the
            // display configuration changed since startup we have current data.
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

        /// <summary>
        /// Blazor "Load Monitor Defaults" button's entry point (MIGRATION_PLAN.md
        /// Phase 4.4) — calls `PerformClick()`, which raises the real `Click` event
        /// exactly as a physical click would, so `BtnLoadDefaults_Click` above runs
        /// unchanged (plus the `Changed`-raising subscriber added in the constructor).
        /// </summary>
        private void LoadMonitorDefaultsForSelectedGame()
        {
            EnsureGamesTabActive();
            _btnLoadDefaults.PerformClick();
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
                    string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                    string title = p.MainWindowTitle.Trim();
                    if (!_selectedGame.IsMatch(exe, title)) continue;
                    found = title;
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

        /// <summary>
        /// Blazor fetch-name (↺) button's entry point (MIGRATION_PLAN.md Phase 4.5)
        /// — calls `PerformClick()`, so the real, unchanged `BtnFetchName_Click`
        /// above runs (which only fills `_txtGameName.Text`; committing it to
        /// `_selectedGame.GameName` is Save Changes' job, Phase 4.6).
        /// </summary>
        private void FetchNameForSelectedGame()
        {
            EnsureGamesTabActive();
            _btnFetchName.PerformClick();
        }

        /// <summary>
        /// review.md §3: backs Matching tab's "Use Title Of Selected" link,
        /// previously dead. Only writes the pending `_txtRegex.Text` buffer —
        /// same as FetchNameForSelectedGame does for the display name — Save
        /// Changes still owns committing it to GameConfig.RegexPattern. Not
        /// routed through a hidden WinForms button's Click, so RaiseChanged is
        /// explicit here (same shape as AdjustWidth/DetectDisplays).
        /// </summary>
        private void SetPendingRegexPattern(string pattern)
        {
            _txtRegex.Text = pattern;
            _appState.RaiseChanged();
        }

        /// <summary>Same shape as <see cref="SetPendingRegexPattern"/>, for the
        /// Window Title/Process Name chips — writes the pending buffer only,
        /// Save Changes still owns committing it to GameConfig.MatchTarget.</summary>
        private void SetPendingMatchTarget(MatchTargetMode mode)
        {
            _pendingMatchTarget = mode;
            _appState.RaiseChanged();
        }

        private void BtnSaveGame_Click(object? sender, EventArgs e)
        {
            if (_selectedGame == null) return;
            CaptureUndoSnapshot(_selectedGame); // Phase 4.13 — before any mutation below
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

            // ShowStatus alone lands on _lblStatus, a legacy WinForms control
            // permanently covered by the BlazorWebView since Phase 7.0 — so it
            // was never actually visible to a Blazor-UI user, making "Save
            // Changes" look like a no-op even though it was persisting fine.
            // The toast is real, on-screen confirmation.
            ShowToast($"Changes saved\n{_selectedGame.GameName} — {_activeScope}", LogLevel.Ok, _selectedGame.IconImagePath);
        }

        /// <summary>Records <paramref name="game"/>'s pre-Save state so <see cref="UndoLastSave"/> can restore it. See the field group's doc comment for the single-level-undo rationale.</summary>
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

        /// <summary>
        /// Blazor "↶ Undo" buttons' entry point (MIGRATION_PLAN.md Phase 4.13) —
        /// net-new, no WinForms UI or handler existed for this before (README:
        /// "reverts the last committed change to the current game's config").
        /// Restores <see cref="_undoTarget"/> to its pre-Save-Changes snapshot,
        /// persists and re-enforces it exactly like a normal save would, then
        /// clears the snapshot — a second click has nothing left to undo.
        /// </summary>
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

            _undoTarget = null; // consumed — single level
            _appState.RaiseChanged();
        }

        /// <summary>
        /// Blazor "Save Changes" buttons' entry point (MIGRATION_PLAN.md Phase
        /// 4.6) — shared by both the Display and Matching tabs, per the README
        /// ("both tabs share Save Changes / Undo"), same as the WinForms UI's own
        /// two Save Changes buttons (`_btnSaveGame`/`_btnSaveAdvanced`) both being
        /// wired to the same `BtnSaveGame_Click`. Calls `PerformClick()` on
        /// `_btnSaveGame`, so the real, unchanged handler runs regardless of which
        /// Blazor tab is showing.
        /// </summary>
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

        /// <summary>
        /// Blazor "Remove Game" button's entry point (MIGRATION_PLAN.md Phase 4.7)
        /// — calls `PerformClick()`, so the real, unchanged `BtnDeleteGame_Click`
        /// runs. That handler has no confirmation prompt today, so this doesn't
        /// add one either — reproducing exact existing behavior, not inventing
        /// safer-seeming UX the original app never had.
        /// </summary>
        private void RemoveSelectedGame()
        {
            EnsureGamesTabActive();
            _btnDeleteGame.PerformClick();
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
            _thrashWarnedGames.Remove(g.GameName);
        }

        // ════════════════════════════════════════════════════════════════════════
        // ADD GAME
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Collects processes that have a visible window title, respecting
        /// <see cref="_systemProcessBlocklist"/> plus anything whose exe lives in
        /// a Windows system directory, deduped by exe name — shared by
        /// `BtnAddRunning_Click`'s dialog and (Phase 3.4) the Matching tab's
        /// live-test list. Extracted unchanged from `BtnAddRunning_Click`'s
        /// previous inline body — same filtering, same ordering, same dedup —
        /// so this refactor doesn't alter that handler's existing behavior.
        /// </summary>
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
                    // Check the built-in default list plus the user's own
                    // Settings > Ignore List additions — fast path. The default
                    // list is never shown/editable in Settings (Phase 8.4); only
                    // _settings.IgnoredProcesses is user-visible/editable there.
                    string exeName = p.ProcessName + ".exe";
                    if (_systemProcessBlocklist.Contains(exeName) || _settings.IgnoredProcesses.Contains(exeName)) return false;
                    // Also exclude anything whose exe lives in a Windows system directory.
                    // Best-effort only: confirmed live that anti-cheat-protected games
                    // (EAC, BattlEye, Vanguard) can make Process.MainModule throw
                    // Win32Exception "Access is denied" outright (a real running
                    // EAC-protected game reproduced this exactly) — the exact same
                    // failure TryGetHotkeyTargetExe already treats as "can't confirm,
                    // so don't exclude" (see its own doc comment), not as "assume
                    // system process, hide it". Getting that wrong here silently
                    // vanished precisely the games this app cares most about from the
                    // Matching tab's live-test list, so this mirrors that same
                    // fail-open handling instead of the fail-closed `return false`
                    // this used to have.
                    try
                    {
                        string? exePath = p.MainModule?.FileName;
                        if (exePath != null && systemDirs.Any(d =>
                            exePath.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                            return false;
                    }
                    catch { /* can't determine — don't assume system process */ }
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

        /// <summary>
        /// Blazor Add Running App modal's entry point (MIGRATION_PLAN.md Phase
        /// 4.8). Unlike every other Phase 4 sub-item, this does NOT trigger the
        /// native `_btnAddRunning`/`ShowDialog()` path — Phase 1/2's adaptation
        /// decisions already replaced that native modal dialog with an in-app
        /// Blazor view (`AddRunningAppModal.razor`, screen 1d), the same way
        /// Settings/Activity Log became in-app views instead of separate OS
        /// windows. So this calls straight into `AddGame`, the actual
        /// state-mutating method `BtnAddRunning_Click`'s dialog OK-path (and
        /// `BtnAddBrowse_Click`'s OK-path) both already call — reusing the real
        /// business logic at the right level, not the dialog-construction
        /// wrapper around it. `AddGame` internally reassigns `_lstGames.SelectedIndex`,
        /// which fires the existing `Changed`-raising subscriber (Phase 4.1) — the
        /// explicit `RaiseChanged()` below is a defensive backstop for the (very
        /// unlikely) case the new game isn't found in the rebuilt list.
        ///
        /// Returns whether a genuinely new <see cref="GameConfig"/> was created
        /// (`_settings.Games.Count` growing), not `AddGame`'s own bool (which
        /// also folds in "added, but an elevation warning happened applying
        /// borderless" as false — irrelevant here; the Blazor caller uses this
        /// return value only to decide whether `State.SelectedGame` is now the
        /// new game, for the Phase 8.4 auto-fetch-artwork-on-add feature).
        /// </summary>
        private bool AddGameFromRunningWindow(OpenWindowEntry entry)
        {
            int countBefore = _settings.Games.Count;
            AddGame(entry.Exe, false, entry.WindowTitle);
            _appState.RaiseChanged();
            return _settings.Games.Count > countBefore;
        }

        /// <summary>
        /// Phase 8.4: the Ctrl+Shift+A hotkey-add path's auto-fetch — same
        /// underlying ArtworkService.TryAutoFetchAsync as the Blazor "Add
        /// Running App"/Browse-for-EXE modal, resolved from `_blazorServices`
        /// since this runs entirely on the WinForms side. Mutates `game`
        /// directly rather than going through AppStateService.ApplyArtwork,
        /// since this is a genuine fire-and-forget background call (see the
        /// call site's own comment for why) — `game` is a specific captured
        /// object reference, not "whichever game happens to be selected when
        /// this resolves". The `await` here resumes on the UI thread's own
        /// SynchronizationContext (HotkeyAdd always runs on the UI thread, via
        /// WndProc), so touching `_settings`/QueueSave/_appState.RaiseChanged
        /// afterward needs no extra marshaling.
        /// </summary>
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

        /// <summary>
        /// Phase 8.3: Settings > Diagnostics' SteamGridDB API key field —
        /// direct mutation + persist, same shape as AddGameFromRunningWindow
        /// above (no pre-existing WinForms control to reuse, since this
        /// setting is entirely net-new). Debounced via QueueSave rather than
        /// SaveConfig so typing into the field doesn't hit disk on every
        /// keystroke.
        /// </summary>
        private void SetSteamGridDbApiKey(string key)
        {
            _settings.SteamGridDbApiKey = key;
            QueueSave();
            _appState.RaiseChanged();
        }

        /// <summary>
        /// Phase 8.3: writes fetched artwork paths onto the real, currently
        /// selected game and persists — same direct-mutation shape as
        /// SetSteamGridDbApiKey above. A no-op if nothing is genuinely selected
        /// (the Fetch Artwork picker's caller already runs EnsureSelected()
        /// first, same as every other write action since Phase 4.3, but this
        /// guards independently rather than trusting the caller).
        /// </summary>
        private void ApplyArtwork(string heroPath, string iconPath)
        {
            if (_selectedGame == null) return;
            _selectedGame.HeroImagePath = heroPath;
            _selectedGame.IconImagePath = iconPath;
            QueueSave();
            _appState.RaiseChanged();
        }

        /// <summary>
        /// Settings > Ignore List (Phase 8.4): the live, sorted contents of
        /// _settings.IgnoredProcesses — no pre-existing WinForms control to
        /// reuse, same shape as ApplyArtwork/SetSteamGridDbApiKey above (this
        /// setting is entirely net-new).
        /// </summary>
        private List<string> GetIgnoredProcesses() =>
            _settings.IgnoredProcesses.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Adds an exe name (e.g. from the ignore-list "Add Program" picker,
        /// which reuses GetOpenWindowEntries the same way the game picker
        /// does) to the ignore list. Immediately affects every other consumer
        /// of _settings.IgnoredProcesses (GetOpenWindowEntries,
        /// TryGetHotkeyTargetExe) on their next call — no separate "apply" step.
        /// </summary>
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

        /// <summary>
        /// Settings > Ignore List "Clear All" button's entry point — shows a
        /// native confirmation MessageBox (same reasoning/shape as
        /// ConfirmRestartAsAdmin: irreversible from the user's point of view)
        /// naming exactly how many entries are about to be discarded, then
        /// empties _settings.IgnoredProcesses on Yes. Only ever touches the
        /// user's own additions — the built-in list (_systemProcessBlocklist)
        /// isn't part of this set at all, so there's nothing to "restore".
        /// </summary>
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

        /// <summary>
        /// Settings > Diagnostics "Full Reset" button's entry point — wipes
        /// every persisted NoBorders setting (tracked games, custom
        /// resolutions/monitor defaults, hotkeys, ignore list, SteamGridDB key,
        /// window bounds, toggles) and the artwork cache, leaving the user's
        /// AppData folder as if the app had just been installed. Confirms
        /// first (same irreversible-action shape as ConfirmClearIgnoredProcesses/
        /// RemoveAllSavedMonitors) since there is no undo. Unlike those two,
        /// this can't just mutate _settings in place and keep running: stale
        /// in-memory state (registered hotkeys, WinForms controls already
        /// populated from the old settings, BlazorWebView's own component
        /// state) would drift from the fresh AppSettings, so instead this
        /// relaunches the process — same "flush, release the mutex, spawn,
        /// exit" shape as RestartAsAdmin, just without the elevation change
        /// (no "runas" verb, AlwaysRunAsAdmin/elevation untouched either way).
        /// </summary>
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

                // Restore any windows currently stripped of their title bar back
                // to normal before wiping _settings out from under them —
                // otherwise a game left running through the reset would stay
                // stuck borderless (nothing left tracking it to ever restore it).
                foreach (var g in _trackedWindows.Values.Distinct().ToList())
                    RemoveGameTracking(g);
                _trackedWindows.Clear();
                _runningGames.Clear();

                _settings = new AppSettings();
                UpdateRegistryStartup(); // removes the Run key — fresh StartWithWindows is false

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

                // Same mutex handoff as RestartAsAdmin — release AND dispose
                // before spawning, or the new instance's own single-instance
                // check finds it still held and just no-ops.
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

        /// <summary>
        /// Blazor "Browse for EXE…" affordance's entry point (MIGRATION_PLAN.md
        /// Phase 4.9) — calls `PerformClick()`, so the real, unchanged
        /// `BtnAddBrowse_Click` runs, including its native `OpenFileDialog`
        /// (a real OS common dialog, not something the design mock replaces —
        /// unlike 4.8's running-app picker, there's no in-app Blazor equivalent
        /// to build here). Placement note: the mock has no dedicated "+" for
        /// this at all (design-handoff/README.md never mentions "browse"), so
        /// it's surfaced as a secondary link inside AddRunningAppModal (screen
        /// 1d) rather than inventing new rail iconography the mock doesn't
        /// specify — see that component's doc comment.
        ///
        /// Returns whether a genuinely new game was created — same
        /// `_settings.Games.Count` growth check as `AddGameFromRunningWindow`,
        /// for the same Phase 8.4 auto-fetch-artwork-on-add reason (the user
        /// may have cancelled the native file dialog, in which case nothing
        /// was added and there's no new selection to fetch art for).
        /// </summary>
        private bool BrowseForExe()
        {
            EnsureGamesTabActive();
            int countBefore = _settings.Games.Count;
            _btnAddBrowse.PerformClick();
            return _settings.Games.Count > countBefore;
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
        /// <summary>
        /// review.md §5: the exe-basename cleanup extracted out of AddGame so
        /// it's testable in isolation — strips known engine/platform/build
        /// tags (Unreal Engine's own packaging convention chains them:
        /// "&lt;Name&gt;-Win64-Shipping.exe", "&lt;Name&gt;-Win64-Test.exe",
        /// etc.) so e.g. "HELLDIVERS2-Win64-Shipping" and "HELLDIVERS2" are
        /// recognized as the same game.
        ///
        /// Bugfix: a single Regex.Replace call only ever stripped one tag —
        /// Replace removes non-overlapping matches from the ORIGINAL string
        /// in one pass, it doesn't re-scan its own output, so
        /// "HELLDIVERS2-Win64-Shipping" used to lose only "-Shipping",
        /// leaving "-Win64" behind (previously documented as observed, not
        /// fixed, in MainFormLogicTests.cs). Now loops to a fixed point so a
        /// full chain strips, and also strips a leading tag (e.g.
        /// "Win64-GameName"), not just a trailing one.
        ///
        /// Deliberately NOT in the tag list despite being common build
        /// vocabulary: "release"/"final"/"development" — too likely to
        /// collide with a real game's actual title ("Final Fantasy" would
        /// lose "Final" if "final" were a strippable prefix). Only tokens
        /// that are essentially never real words in a game title made the
        /// cut. The lone "d" abbreviation stays suffix-only for the same
        /// reason (as a prefix it would mangle e.g. "D-Day").
        /// </summary>
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

        /// <summary>
        /// Bugfix: a real live game window title ("ARC Raiders") was found to
        /// contain embedded zero-width Unicode characters (U+FEFF, U+200B,
        /// U+2005) invisible in the UI but persisted verbatim into
        /// GameName — confirmed in a real games_config.json. GetWindowText
        /// returns whatever the OS/game puts in the title bar with no
        /// guarantee it's "clean" text, and .Trim() only strips leading/
        /// trailing whitespace, not characters embedded mid-string. Strips
        /// Unicode format characters (Cf — zero-width joiners, BOM, etc.,
        /// which have no width so no replacement is needed) and normalizes
        /// non-standard space separators (Zs other than U+0020, e.g. the
        /// four-per-em space seen above) to a normal space rather than
        /// deleting them, so words that were space-separated stay
        /// separated.
        /// </summary>
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

            // Display name: prefer the window title passed from the picker;
            // fall back to the sanitized exe basename (browse path).
            string gameName  = !string.IsNullOrWhiteSpace(displayName)
                ? SanitizeDisplayName(displayName)
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

        /// <summary>
        /// Controls on a non-active WinForms `TabPage` are implicitly
        /// `Visible = false` (the `TabControl` manages this), which makes
        /// `Button.PerformClick()` silently no-op — its `IButtonControl`
        /// implementation gates on `CanSelect`, which requires `Visible`.
        /// Discovered live while wiring Phase 4.10: `_btnDeleteMonitor`'s
        /// `PerformClick()` returned normally but never actually raised `Click`,
        /// because the real (Blazor-covered) `_tabs.SelectedIndex` was still 0
        /// (Games) — nothing had ever told the WinForms tab control that Blazor
        /// was showing Settings, since that navigation isn't wired yet. Every
        /// Settings-tab bridge method (this phase and 4.11/4.12) must call this
        /// first. A plain property assignment (like `_cmbSetMonitor.SelectedItem`
        /// itself, or anything set directly rather than via `PerformClick()`)
        /// isn't affected — only the `PerformClick()` gate is.
        /// </summary>
        private void EnsureSettingsTabActive() => EnsureTabActive(1);

        /// <summary>
        /// The Games-tab mirror of <see cref="EnsureSettingsTabActive"/> — same
        /// `PerformClick()`/`CanSelect`/`Visible` gate, just for the other
        /// direction. Bugfix: every Settings-tab bridge method already called
        /// its own guard, but the five Games-tab ones (Load Monitor Defaults,
        /// Fetch Name, Save Changes, Remove Game, Browse for EXE) never did —
        /// confirmed live as the reported repro: visit Settings > Monitors
        /// (which leaves the real `_tabs.SelectedIndex` on 1 via
        /// `EnsureSettingsTabActive`), navigate back to a game's Display tab in
        /// Blazor, click "Load Monitor Defaults" — `_btnLoadDefaults` is still
        /// on the now-inactive tab 0, so `PerformClick()` silently no-ops.
        /// </summary>
        private void EnsureGamesTabActive() => EnsureTabActive(0);

        /// <summary>Shared body for <see cref="EnsureSettingsTabActive"/>/<see cref="EnsureGamesTabActive"/> — same `PerformClick()`/`CanSelect`/`Visible` gate, just a different tab index.</summary>
        private void EnsureTabActive(int index)
        {
            if (_tabs.SelectedIndex != index) _tabs.SelectedIndex = index;
        }

        /// <summary>
        /// Blazor Settings > Monitors card click's entry point (MIGRATION_PLAN.md
        /// Phase 4.10) — selects a scope exactly as picking it in the WinForms
        /// combo would, firing the real, unchanged `CmbSetMonitor_SelectedIndexChanged`
        /// below.
        /// </summary>
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

        /// <summary>
        /// Blazor Settings > Monitors "✕" entry point (MIGRATION_PLAN.md Phase
        /// 4.10) — selects the given scope first (same as a card click would),
        /// then calls `PerformClick()`, so the real, unchanged
        /// `BtnDeleteMonitor_Click` runs against it, native confirmation
        /// `MessageBox`es and all — those aren't replaced with Blazor UI, same
        /// reasoning as 4.9's native file picker.
        /// </summary>
        private void DeleteMonitorDefault(string scope)
        {
            SelectMonitorDefaultScope(scope); // also calls EnsureSettingsTabActive()
            _btnDeleteMonitor.PerformClick();
        }

        /// <summary>
        /// review.md §3: backs Settings &gt; Monitors' "Remove All" link
        /// (saved-but-not-connected group), previously dead — same
        /// permanently-delete-with-confirmation shape as
        /// BtnDeleteMonitor_Click, just applied to every saved-disconnected
        /// monitor at once instead of a single selected one.
        /// </summary>
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

            // Flash the button text briefly (same caveat as the toast below —
            // _btnSaveDefault is a legacy WinForms control permanently covered
            // by the BlazorWebView since Phase 7.0, so this text flash was
            // never actually visible; kept as-is since it's still harmless/
            // cheap and the toast is the real confirmation now).
            _btnSaveDefault.Text = "Saved ✔";
            var t = new System.Windows.Forms.Timer { Interval = 1400 };
            t.Tick += (ts, te) => { _btnSaveDefault.Text = "Save Monitor Default"; t.Stop(); t.Dispose(); };
            t.Start();

            ShowToast($"Monitor default saved\n{sel}", LogLevel.Ok);
        }

        /// <summary>
        /// Blazor "Save Monitor Default" button's entry point (MIGRATION_PLAN.md
        /// Phase 4.10) — calls `PerformClick()`, so the real, unchanged
        /// `BtnSaveDefault_Click` above runs.
        /// </summary>
        private void SaveMonitorDefault()
        {
            EnsureSettingsTabActive();
            _btnSaveDefault.PerformClick();
        }

        /// <summary>
        /// "Apply to All Games" — for when a monitor's resolution changed
        /// permanently, or the user just wants every tracked game/program to
        /// share one behavior on this monitor, instead of hand-editing each
        /// game's profile individually. Saves the currently-shown Width/Height/
        /// Offset X/Y/Constrain values as this monitor's default (same as Save
        /// Monitor Default) and then overwrites — or creates — every game's
        /// per-monitor profile for this scope to match, same shape as
        /// RemoveAllSavedMonitors's per-game sweep but writing instead of
        /// removing. Confirmed first since it clobbers any existing
        /// per-game customization for this monitor.
        /// </summary>
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

        /// <summary>
        /// Begins listening for a new key combination for the given hotkey slot.
        /// Unregisters that specific global hotkey first, so pressing its current
        /// combination while picking a new one — including re-picking the exact
        /// same combo — only feeds this capture UI and can never also fire the
        /// live Add/Refresh action underneath it.
        /// </summary>
        /// <summary>
        /// Blazor "Rebind" button's entry point for the "Add focused app" hotkey
        /// (MIGRATION_PLAN.md Phase 4.11) — calls `PerformClick()`, so the real,
        /// unchanged toggle logic in `_btnSetHotkeyAdd`'s `Click` handler runs
        /// (begins or cancels capture depending on current state). Requires the
        /// Settings tab active first, same `PerformClick()`/`Visible` gate found
        /// in Phase 4.10.
        /// </summary>
        private void ToggleHotkeyAddCapture()
        {
            EnsureSettingsTabActive();
            _btnSetHotkeyAdd.PerformClick();
        }

        /// <summary>Same as <see cref="ToggleHotkeyAddCapture"/>, for "Re-apply / refresh displays".</summary>
        private void ToggleHotkeyRefreshCapture()
        {
            EnsureSettingsTabActive();
            _btnSetHotkeyRefresh.PerformClick();
        }

        /// <summary>
        /// Blazor Behaviour toggle entry points (MIGRATION_PLAN.md Phase 4.12) —
        /// flip the real checkbox's `Checked` property, firing each one's
        /// existing, unchanged `CheckedChanged` lambda. Plain property
        /// assignments, not `PerformClick()` — unaffected by 4.10's
        /// inactive-`TabPage`/`CanSelect` finding (that gate is specific to
        /// `IButtonControl.PerformClick()`), so no `EnsureSettingsTabActive()`
        /// call is needed here.
        /// </summary>
        private void ToggleMinimizeToTray() => _chkMinToTray.Checked = !_chkMinToTray.Checked;

        private void ToggleStartWithWindows() => _chkStartWindows.Checked = !_chkStartWindows.Checked;

        /// <summary>
        /// Mirrors the real checkbox's `Enabled` gating: `_chkStartMin` is
        /// disabled (and un-toggleable by a real click) whenever "Start with
        /// Windows" is off, but a plain `.Checked =` assignment in code doesn't
        /// respect `Enabled` on its own — this guard reproduces what a real
        /// click against the disabled control would do (nothing).
        /// </summary>
        private void ToggleStartMinimized()
        {
            if (!_chkStartWindows.Checked) return;
            _chkStartMin.Checked = !_chkStartMin.Checked;
        }

        /// <summary>
        /// Blazor "Lock cursor to window bounds" checkbox entry points — same
        /// shape as <see cref="ToggleMinimizeToTray"/>, flipping the real
        /// checkbox's `Checked` property. `_chkConstrain` backs the selected
        /// game/scope's Display tab checkbox (its current value is only
        /// committed onto the profile by `SaveUIToProfile`, at Save Changes or
        /// on scope switch — same as before this was reachable from Blazor);
        /// `_chkSetConstrain` backs the Settings &gt; Monitors default checkbox
        /// (committed by `BtnSaveDefault_Click`).
        /// </summary>
        private void ToggleConstrainMouse() => _chkConstrain.Checked = !_chkConstrain.Checked;

        private void ToggleConstrainMouseDefault() => _chkSetConstrain.Checked = !_chkSetConstrain.Checked;

        /// <summary>
        /// Blazor Width/Height/Offset X/Y stepper entry points (SteppedNumberField's
        /// ▲/▼) — adjusts the real shadow NumericTextBox's Value by a signed
        /// delta (already ×10/×100'd for Shift/Ctrl by the component) and
        /// raises Changed explicitly, since — unlike the checkboxes above —
        /// a programmatic Value assignment on this control never raises its
        /// own ValueChanged (see the comment on the RaiseChanged wiring block
        /// in the constructor for why). `_numWidth`/`_numHeight`/
        /// `_numOffsetX`/`_numOffsetY` back the Display tab's selected
        /// game/scope (committed by SaveUIToProfile, same as ConstrainMouse);
        /// `_numSetWidth`/`_numSetHeight`/`_numSetOffsetX`/`_numSetOffsetY`
        /// back Settings &gt; Monitors' default profile (committed by
        /// BtnSaveDefault_Click). Value's own setter silently clamps to
        /// Minimum/Maximum rather than throwing, so no clamping is needed here.
        /// </summary>
        private void AdjustWidth(int delta) { _numWidth.Value += delta; _appState.RaiseChanged(); }
        private void AdjustHeight(int delta) { _numHeight.Value += delta; _appState.RaiseChanged(); }
        private void AdjustOffsetX(int delta) { _numOffsetX.Value += delta; _appState.RaiseChanged(); }
        private void AdjustOffsetY(int delta) { _numOffsetY.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultWidth(int delta) { _numSetWidth.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultHeight(int delta) { _numSetHeight.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultOffsetX(int delta) { _numSetOffsetX.Value += delta; _appState.RaiseChanged(); }
        private void AdjustMonitorDefaultOffsetY(int delta) { _numSetOffsetY.Value += delta; _appState.RaiseChanged(); }

        /// <summary>
        /// Blazor alignment buttons' entry point (Display tab) — replaced the
        /// old single "Center On Monitor" link (Phase 8.5) with four one-shot
        /// actions (Phase 8.6). Only meaningful for a currently-connected
        /// monitor — there's no physical resolution to align against for a
        /// saved-but-disconnected one — so this no-ops with a status message
        /// rather than guessing, same shape as BtnLoadDefaults_Click's "no
        /// saved defaults" case.
        /// </summary>
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

        /// <summary>Same as <see cref="AlignOnMonitor"/>, for Settings &gt; Monitors' default-profile fields.</summary>
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

        /// <summary>
        /// Shared math for both alignment entry points above — Left/Right
        /// flush the window to that edge while keeping the other axis
        /// centered; Bottom flushes vertically while keeping X centered;
        /// Center matches the original both-axes-centered "Center On
        /// Monitor" behavior. Integer division matches every other
        /// coverage/offset calculation in this file (SummaryText,
        /// CoveragePercent) — no rounding beyond what `/` already does.
        /// </summary>
        private static void ApplyAlign(MonitorItem mon, MonitorAlignMode mode,
            NumericTextBox numWidth, NumericTextBox numHeight, NumericTextBox numOffsetX, NumericTextBox numOffsetY)
        {
            var (offsetX, offsetY) = ComputeAlignOffset(mon.Width, mon.Height, (int)numWidth.Value, (int)numHeight.Value, mode);
            numOffsetX.Value = Clamp(offsetX, -16384, 16384);
            numOffsetY.Value = Clamp(offsetY, -16384, 16384);
        }

        /// <summary>
        /// review.md §5: the pure offset math extracted out of ApplyAlign so it's
        /// testable without a live NumericTextBox/MonitorItem — same Left/Right/
        /// Bottom/Center rules as ApplyAlign's own doc comment above, unchanged.
        /// </summary>
        internal static (int OffsetX, int OffsetY) ComputeAlignOffset(
            int monitorWidth, int monitorHeight, int windowWidth, int windowHeight, MonitorAlignMode mode) => mode switch
        {
            MonitorAlignMode.Left   => (0, (monitorHeight - windowHeight) / 2),
            MonitorAlignMode.Right  => (monitorWidth - windowWidth, (monitorHeight - windowHeight) / 2),
            MonitorAlignMode.Bottom => ((monitorWidth - windowWidth) / 2, monitorHeight - windowHeight),
            _                       => ((monitorWidth - windowWidth) / 2, (monitorHeight - windowHeight) / 2),
        };

        // Hotkey capture/registration (BeginHotkeyCapture, CancelHotkeyCapture,
        // CaptureHotkey, RegisterHotkeys, TryRegisterHotkey,
        // SetHotkeyStatusLabel) moved to MainForm.Hotkeys.cs — review.md §4.2.

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
        /// <summary>
        /// review.md §3: shared by every window-title-matching call site.
        /// Fetches the title directly from the window handle rather than
        /// Process.MainWindowTitle — HotkeyAdd's own doc comment (below) notes
        /// the latter "can lag or return empty"; GetWindowText talks to the
        /// window itself instead of relying on the Process object's cached
        /// snapshot, so it's used everywhere a hwnd is already in hand.
        /// </summary>
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
                if (pid == (uint)Environment.ProcessId) return false; // NoBorders itself

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
                string windowTitle = GetWindowTitle(hwnd);
                AppLogger.Log($"HotkeyAdd: windowTitle=\"{windowTitle}\"");

                bool alreadyTracked = _settings.Games.Any(g => g.IsMatch(exeName, windowTitle));
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
                        {
                            ShowToast($"Added & borderless applied\n{nameToUse}", LogLevel.Ok);
                            AppLogger.Log($"Added & borderless applied to '{nameToUse}'.", LogLevel.Ok);

                            // Phase 8.4: same auto-fetch-artwork-on-add as the
                            // Blazor "Add Running App"/Browse-for-EXE paths
                            // (AddGameFromRunningWindow's doc comment has the
                            // general design). _selectedGame is exactly the
                            // game just added — AddGame's own
                            // `_lstGames.SelectedIndex = newIdx` just set it.
                            // Captured here (synchronously, still the same
                            // object reference) and applied directly onto it
                            // rather than through AppStateService.ApplyArtwork's
                            // "whichever game is currently selected" semantics:
                            // unlike the Blazor modal (which awaits the fetch
                            // before closing, safe because the modal blocks all
                            // other interaction meanwhile), this hotkey handler
                            // can't block on a network round-trip, so the fetch
                            // has to run in the background — a user selecting a
                            // different game in the rail before it resolves
                            // must not cause it to land on the wrong game.
                            if (_selectedGame is { } newGame)
                                _ = AutoFetchArtworkForNewGameAsync(newGame);
                        }
                    }));
                }
                else if (alreadyTracked)
                {
                    // Game is already in the list — apply borderless instead.
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

                // Force a fresh monitor refresh before applying so that if the
                // display configuration changed since startup we have current data.
                RefreshMonitors();

                // Update the tracked handle to the current foreground window and
                // re-apply using the now-current monitor layout.
                _trackedWindows[hwnd] = match;
                ApplyBorderless(hwnd, match);
                ShowToast($"Borderless re-applied\n{match.GameName}", LogLevel.Ok, match.IconImagePath);
                AppLogger.Log($"Borderless re-applied to '{match.GameName}'.", LogLevel.Ok);
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
                    if (g.IsMatch((p.ProcessName + ".exe").ToLowerInvariant(), GetWindowTitle(p.MainWindowHandle)))
                    {
                        ApplyBorderless(p.MainWindowHandle, g);
                        _trackedWindows[p.MainWindowHandle] = g;
                    }
                }
                catch { /* process may have exited */ }
            }
        }

        /// <summary>
        /// MIGRATION_PLAN.md Phase 4.14 — this method's own logic is completely
        /// untouched, per the frozen-systems note; only <see cref="RefreshRunningGames"/>
        /// (called from Step 0 below) gained one `_appState.RaiseChanged()` call,
        /// at the one point it mutates state nothing else already notifies
        /// Blazor about (<c>_runningGames</c>). The other two "observable
        /// effects" the plan names — the hero's status dot and meta line —
        /// read `GameConfig.IsActive`/`Profiles`, and this method never
        /// mutates either: `IsActive` only ever changes via the user's
        /// Enable/Disable Borderless click (already `RaiseChanged`-covered by
        /// Phase 4.3), and `Profiles` only via Save Changes/Load Monitor
        /// Defaults (4.4/4.6) or Undo (4.13) — all already covered. So this
        /// tick can re-apply styles/positions to already-tracked windows
        /// every second (Step 1) and scan for newly-launched untracked games
        /// (Step 2) without ever needing to notify Blazor of anything beyond
        /// the running-set change already handled above.
        /// </summary>
        private void EnforceTimer_Tick(object? sender, EventArgs e)
        {
            // Settings > Diagnostics "Verbose logging" used to log a flat
            // "N tracked window(s)" heartbeat right here on every tick (every
            // second, regardless of whether anything actually happened) —
            // replaced with real one-shot events fired only when this tick's
            // ApplyBorderless call actually changes a window's style or
            // position, logged from inside ApplyBorderless itself (see its
            // own doc comment) rather than a per-second summary here.

            // Step 0: refresh which games are currently running so the games
            // list can keep running entries pinned to the top.
            RefreshRunningGames();

            // Step 1: re-apply settings to all windows we are already tracking.
            // This corrects any style resets the game engine may have done.
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

                // Bugfix: _trackedWindows holds its own GameConfig reference per
                // window handle, independent of _settings.Games — if the game
                // was removed from the list (or a Full Reset wiped it) while its
                // window stayed open, this loop would otherwise keep re-applying
                // borderless to it forever, since nothing here previously
                // re-validated the game still exists. Only Step 2 below ever
                // checked _settings.Games. Restore the title bar once and stop
                // tracking it instead.
                if (!_settings.Games.Contains(g))
                {
                    try
                    {
                        bool wasActive = g.IsActive;
                        g.IsActive = false; // routes ApplyBorderless into its restore-title-bar branch
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

                    string exe   = (p.ProcessName + ".exe").ToLowerInvariant();
                    string title = GetWindowTitle(p.MainWindowHandle);
                    foreach (var g in _settings.Games)
                    {
                        if (!g.IsActive || !g.IsMatch(exe, title)) continue;
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
                _consecutiveEnforceChanges.Remove(hwnd);
                _enforceBackoffUntil.Remove(hwnd);
                return;
            }

            // Thrash guard — if we're currently backed off this window (see
            // below), leave it alone entirely rather than fighting whatever the
            // engine is doing to it right now.
            if (_enforceBackoffUntil.TryGetValue(hwnd, out DateTime backoffUntil))
            {
                if (DateTime.UtcNow < backoffUntil) return;
                _enforceBackoffUntil.Remove(hwnd);
                _consecutiveEnforceChanges.Remove(hwnd);
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
            bool needsChange  = !styleCorrect || !positionCorrect;

            // Thrash guard — a game whose engine keeps fighting us (resetting its
            // own style/size every frame, e.g. right after its in-game settings
            // were reset while our profile stayed active) will need a real change
            // here on every single tick. Repeatedly slamming SetWindowLong/
            // SetWindowPos into a window while the engine is mid mode-switch
            // (menus, intro videos) has been observed to wedge the GPU driver
            // hard enough to require a reboot, so if this keeps happening several
            // ticks in a row, stop touching the window for a while instead of
            // escalating the fight.
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

            // Settings > Diagnostics "Verbose logging" — real, one-shot Win32
            // interactions (only fires when a style/position change was
            // actually attempted this call, not every enforcement tick — see
            // EnforceTimer_Tick's own doc comment for why the old per-second
            // heartbeat was replaced with this instead).
            if (!styleCorrect && !styleCallFailed)
                AppLogger.LogVerbose($"Restyled '{g.GameName}' (hwnd={hwnd}): removed title bar/resize border.");
            if ((!styleCorrect || !positionCorrect) && !posCallFailed)
                AppLogger.LogVerbose($"Repositioned '{g.GameName}' (hwnd={hwnd}) to {targetX},{targetY} {profile.Width}x{profile.Height} on monitor '{monId}'.");

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
                    LogLevel.Error, g.IconImagePath);
            }
        }

        /// <summary>
        /// MIGRATION_PLAN.md Phase 4.14 — untouched, and needs no `RaiseChanged()`
        /// hook at all: its only effect is the native `ClipCursor` Win32 call,
        /// which confines the OS cursor and mutates no field any Razor
        /// component reads (not `GameConfig`, not `AppSettings`, nothing in
        /// `AppStateService`). There is no "observable effect" here for Blazor
        /// to reflect.
        /// </summary>
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
        /// The currently-open elevation dialog, if any — tracked so a second
        /// trigger (e.g. two elevation-blocked adds in quick succession)
        /// doesn't stack a duplicate on top, and so DismissElevationDialog
        /// has something to close.
        /// </summary>
        private Form? _elevationDialogForm;

        /// <summary>
        /// Moves the "Administrator rights needed" notification back into
        /// the app's own Blazor-rendered surface — replaces the native
        /// MessageBox PromptRestartAsAdmin/ConfirmRestartAsAdmin used to
        /// show. Same always-on-top-dedicated-Form technique as ShowToast
        /// (see that method's own doc comment for why: needs to stay
        /// visible even if MainForm is minimized/covered by a fullscreen
        /// game), but centered on screen and NOT auto-dismissing — this
        /// requires a decision, unlike a passive toast. <paramref
        /// name="gameName"/> empty means the generic Settings > Permissions
        /// trigger; non-empty means the automatic elevation-blocked-on-add
        /// trigger, which gets a message naming that game.
        /// </summary>
        private void ShowElevationDialog(string gameName = "", bool toStandardUser = false)
        {
            if (!this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new MethodInvoker(() => ShowElevationDialog(gameName, toStandardUser)));
                return;
            }

            // Don't stack a second dialog on top of one already open.
            if (_elevationDialogForm is { IsDisposed: false }) return;

            const int dialogWidth  = 470;
            const int dialogHeight = 270;

            var dialogView = new ArtworkAwareBlazorWebView
            {
                HostPage = "wwwroot\\index.html",
                Dock     = DockStyle.Fill,
                Services = _blazorServices // same DI container as the main window; State.DismissElevationDialog/RestartAsAdmin/RestartAsStandardUser need AppStateService
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
                BackColor       = Color.FromArgb(0x0b, 0x0b, 0x0e) // matches --nb-chrome; only visible at the rounded-corner seam
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
            dialog.Activate(); // unlike the toast, this needs a decision — take focus
        }

        /// <summary>ElevationDialog.razor's "Not Now" button and the first step of its "Restart As Admin" button — see that component's own doc comment.</summary>
        private void CloseElevationDialog()
        {
            if (_elevationDialogForm is not { IsDisposed: false } dialog) return;
            _elevationDialogForm = null;
            dialog.Close();
            dialog.Dispose();
        }

        // In-app toast state, read by Blazor via IMainFormBridge (below) and
        // rendered centered in MainShell's own status bar — only used while
        // MainForm is actually visible (see ShowToast's doc comment).
        private System.Windows.Forms.Timer? _toastTimer;
        private string _toastTitle = "";
        private string _toastDetail = "";
        private string _toastIconPath = "";
        private LogLevel _toastLevel = LogLevel.Ok;
        private bool _toastVisible;

        /// <summary>
        /// Shows a status notification, auto-dismissing after ~6s. Safe to call
        /// from background threads — marshals to the UI thread.
        ///
        /// User request (2026-08-13): while MainForm is visible, the message
        /// now renders centered in MainShell's own status bar instead of a
        /// screen-corner popup — "most of the actions are performed within the
        /// app itself," so a docked notification is enough since the user is
        /// already looking at the window. The dedicated always-on-top popup
        /// (Phase 6.2's BlazorWebView-hosted Form) is kept, but now used only
        /// when MainForm is hidden/minimized to tray — a hotkey-triggered add
        /// or an enforcement failure firing while the window isn't on screen
        /// at all still needs *some* visible confirmation, which the in-app
        /// status bar obviously can't provide when nobody can see it. Same
        /// "hidden" check MainForm.Sleep.cs already uses for
        /// _wasHiddenBeforeSleep. Also replaces the old binary success/danger
        /// coloring with the app's existing three-tier LogLevel scheme
        /// (Ok=green, Info/Warn=yellow, Error=red) in both paths. `message`
        /// keeps its original shape (an optional `\n`-separated
        /// "Title\nDetail") so call sites barely changed — only the old `bool
        /// success` argument became a `LogLevel`.
        /// </summary>
        private void ShowToast(string message, LogLevel level = LogLevel.Ok, string iconPath = "")
        {
            if (!this.IsHandleCreated) return;

            // Marshal to UI thread if called from a hotkey/background context.
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

            const int holdMs = 6000; // same duration the popup's progress bar animates over

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

        /// <summary>
        /// The original Phase 6.2 popup: a small non-stealing toast in the
        /// bottom-right corner of the primary screen, auto-dismissing after
        /// ~6s, hosted in its own dedicated always-on-top BlazorWebView
        /// window. Only reached from ShowToast when MainForm itself is
        /// hidden/minimized — see that method's own doc comment.
        /// </summary>
        private void ShowPopupToast(string title, string detail, LogLevel level, string iconPath)
        {
            const int toastWidth  = 330;
            const int toastHeight = 100; // fits title + 2-line detail, same fixed-size simplification the GDI popup used
            const int cornerGap   = 18;
            const int holdMs      = 6200; // README: "auto-dismisses ~6s along the progress bar" + a small buffer past the CSS animation's 6s

            var toastView = new ArtworkAwareBlazorWebView
            {
                HostPage = "wwwroot\\index.html",
                Dock     = DockStyle.Fill,
                Services = _blazorServices // same DI container as the main window; Toast.razor needs none of AppStateService's capabilities
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
                BackColor       = Color.FromArgb(0x11, 0x11, 0x16) // matches Toast.razor.css's own background; only visible at the rounded-corner seam
            };

            var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            toast.Location = new Point(
                screen.Right  - toast.Width  - cornerGap,
                screen.Bottom - toast.Height - cornerGap);

            toast.Controls.Add(toastView);

            // Rounded-corner region — same technique and radius (8px, matching
            // --nb-radius-input) as the old GDI popup's RoundedRect helper, just
            // clipping the whole host window instead of hand-painting a fill.
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

            // Show without stealing focus.
            toast.Show();
            NativeMethods.ShowWindowNoActivate(toast.Handle);
            closeTimer.Start();
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

        // ════════════════════════════════════════════════════════════════════════
        // FORM EVENTS & MESSAGE PUMP
        // ════════════════════════════════════════════════════════════════════════

        private void OnResize(object? sender, EventArgs e)
        {
            // Bugfix: double-clicking the tray icon while hidden crashed the
            // whole process with a native stack overflow (0xc00000fd — not
            // catchable by any managed handler, which is why it never showed
            // up in noborders.log). Root cause: RestoreFromTray's this.Show()
            // can fire this Resize handler while WindowState is still
            // Minimized (the WindowState=Normal assignment hasn't run yet),
            // which re-Hide()s the form mid-restore; that Hide()/Show()
            // churn was feeding back into more Resize events faster than the
            // stack could unwind. _isRestoringFromTray blocks the hide branch
            // for the whole duration of a restore, regardless of how many
            // intermediate Resize events fire during it.
            if (_isRestoringFromTray) return;

            if (this.WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }

            // Phase 8.9: WindowControls.razor's maximize/restore glyph needs
            // to track WindowState regardless of how it changed — a native
            // double-click/drag-to-edge/Win+Up on the frameless title bar's
            // HTCAPTION region never goes through the Blazor button, so
            // nothing else would tell AppStateService a redraw is needed.
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

            // Flush any pending save immediately on exit
            CaptureWindowBounds();
            SaveConfig();

            ClipCursor(IntPtr.Zero);
            _trayIcon.Visible = false;
        }

        /// <summary>
        /// Captures the window's current real (non-minimized) bounds into
        /// _settings, so the next SaveConfig() call persists them — called
        /// right before every path that's about to flush config and end this
        /// process's UI lifetime (real close, restart-as-admin), so a
        /// resize/move sticks across restarts.
        ///
        /// Skipped while WindowState is Minimized (only reachable here via
        /// minimize-to-tray, or mid-restart): RestoreBounds while minimized
        /// still correctly reports the pre-minimize normal bounds, but
        /// WindowState itself can't distinguish "was Normal before
        /// minimizing" from "was Maximized before minimizing" without extra
        /// Win32 plumbing (GetWindowPlacement's showCmd) — rather than guess,
        /// this just leaves whatever was captured the last time the window
        /// was genuinely Normal or Maximized untouched.
        /// </summary>
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

        /// <summary>
        /// Called when Windows reports a display configuration change (monitor
        /// enabled/disabled, resolution changed, primary changed, etc.).
        /// We refresh the monitor list, clear all tracked window handles so they
        /// are re-evaluated against the new layout, then re-apply borderless after
        /// a short delay to let Windows finish repositioning windows.
        /// </summary>
        private async void OnDisplayConfigChanged()
        {
            // review.md §2.2: SystemEvents.DisplaySettingsChanged invokes this
            // directly from its own dedicated thread, outside the normal
            // WinForms message pump — an exception here (either synchronous,
            // or after the await resumes) isn't guaranteed to reach the
            // Application.ThreadException handler the way a control event
            // would, and an unhandled exception on any thread still tears
            // down the whole process. Contain it here instead of relying on
            // a global handler to merely log the crash on the way down.
            try
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
            catch (Exception ex) { AppLogger.Log(ex, "OnDisplayConfigChanged"); }
        }

        // Phase 9 bugfix: the previous approach kept the native WS_CAPTION
        // style and used WM_NCCALCSIZE to visually paper over it with
        // Blazor's own header row (WindowControls.razor). Confirmed live
        // that's not enough — WS_CAPTION still being present means Windows
        // keeps computing real (invisible) native min/max/close hit-test
        // geometry in the top-right corner, which is what fires Windows 11's
        // Snap Layout hover flyout there — a second, native set of buttons
        // overlapping the Blazor ones — and DWM still owns and occasionally
        // repaints the actual non-client caption background (the white-
        // when-unfocused/grey-when-focused strip), since nothing actually
        // removed it. Clearing WS_CAPTION here (before the window handle is
        // created, so there's no post-creation SetWindowLong/SWP_FRAMECHANGED
        // flicker) removes the native caption and its hit-test geometry for
        // good, while leaving WS_THICKFRAME untouched — a window with
        // WS_THICKFRAME and no WS_CAPTION is still a normal OS-resizable/
        // Aero-Snappable window, just with no title bar to draw; the same
        // "still resizable, no caption" combination ApplyBorderless already
        // relies on elsewhere in this file, just without also stripping
        // WS_THICKFRAME the way that one does for target game windows.
        // BeginWindowDrag's WM_NCLBUTTONDOWN(HTCAPTION) trick doesn't need
        // WS_CAPTION either — DefWndProc runs the same real drag-move loop
        // regardless of window style.
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
            // Ignore all custom messages until the form is fully initialised.
            // WndProc can fire during handle creation (before OnLoad) when
            // accessing this.Handle for the first time, which would cause
            // "Invoke before handle created" exceptions.
            if (!this.IsHandleCreated)
            {
                base.WndProc(ref m);
                return;
            }

            // Tray menu's "Pause Enforcement" (MainForm.Tray.cs) — skip
            // auto-detecting/applying borderless to newly-created windows
            // while paused. Explicit hotkeys and in-app actions still work;
            // this only suspends the passive shell-hook scan.
            if (m.Msg == _wmShellHook && m.WParam.ToInt32() == HSHELL_WINDOWCREATED && !_enforcementPaused)
                _ = TrackNewWindowAsync(m.LParam);

            if (_wmShowFirst != 0 && m.Msg == _wmShowFirst)
            { RestoreFromTray(); return; }

            if (m.Msg == WM_DISPLAYCHANGE)
                OnDisplayConfigChanged();

            // icon_export/README.md: "Windows tray does not tint for you —
            // pick the mono asset from the current taskbar theme
            // (SystemUsesLightTheme) and re-load on WM_SETTINGCHANGE." Windows
            // broadcasts this for every settings change (not just theme), so
            // RefreshTrayIconForTheme() re-reads the registry and only
            // actually swaps the icon if the light/dark choice changed.
            if (m.Msg == WM_SETTINGCHANGE)
                RefreshTrayIconForTheme();

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
            // review.md §2.2: same rationale as OnDisplayConfigChanged above —
            // SystemEvents.PowerModeChanged invokes this off the normal
            // message pump, so an unhandled exception here would crash the
            // process rather than merely being logged by a global handler.
            try
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
                _webViewNeedsRepaintAfterWake = true;
                AppLogger.Log("  Form was hidden before sleep — re-hidden after wake.");
            }
            else
            {
                AppLogger.Log($"  Form visibility restored: visible={this.Visible}, taskbar={this.ShowInTaskbar}");

                // The form was already visible on the desktop across the
                // sleep/wake cycle (never hidden to tray), so RestoreFromTray
                // never runs and never fires RepairWebViewAfterWake — the
                // exact same WebView2 swap-chain loss described on that
                // method's doc comment still happens here, just with the
                // window plainly visible instead of tucked in the tray. Run
                // the same repaint directly rather than only covering the
                // tray-restore path.
                if (this.Visible)
                    RepairWebViewAfterWake();
            }
            }
            catch (Exception ex) { AppLogger.Log(ex, "OnWake"); }
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
