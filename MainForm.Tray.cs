using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NoBorders
{
    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM — TRAY ICON
    // ══════════════════════════════════════════════════════════════════════════════
    // review.md §4.2: the tray-icon subsystem, split out of program.cs's single
    // 5,000+ line MainForm class into its own partial-class file. _trayIcon/
    // _trayMenu themselves stay declared in program.cs's shared field block
    // (still referenced from a couple of other lifecycle spots there — form
    // Dispose, OnResize's minimize-to-tray handling — so splitting the fields
    // out too would just add indirection with no real gain); only the two
    // methods that actually constitute "the tray subsystem" move here.
    //
    // Icon set import: "Borderless Gaming App UI/icon_export" (see its own
    // README.md). The window/taskbar/Alt-Tab icon needs no code at all here —
    // it comes from <ApplicationIcon>app.ico</ApplicationIcon> in the .csproj,
    // which TryExtractIcon (program.cs) already reads via
    // Icon.ExtractAssociatedIcon(Application.ExecutablePath). The tray icon is
    // different: per the README, "Windows tray does not tint for you — pick
    // the mono asset from the current taskbar theme (SystemUsesLightTheme) and
    // re-load on WM_SETTINGCHANGE" — that adaptive logic lives here.
    //
    // User request: right-click menu redesigned to match the app's dark-violet
    // aesthetic (custom ToolStripRenderer below, colors copied 1:1 from
    // wwwroot/css/tokens.css since WinForms controls can't read CSS custom
    // properties), a live status row per currently-detected tracked game (dot
    // = borderless actually applied, per icon_export/README.md's own "state is
    // tint + one badge dot" convention), and a Pause/Resume Enforcement toggle.
    internal sealed partial class MainForm
    {
        // Tracks which variant is currently loaded so RefreshTrayIconForTheme
        // (called on every WM_SETTINGCHANGE, not just theme changes) only
        // actually swaps the icon when the light/dark/paused choice genuinely
        // changed.
        private bool? _trayIconIsLightVariant;
        private bool _trayIconIsPausedVariant;

        // The Icon currently assigned to _trayIcon.Icon. Must stay alive for
        // exactly as long as _trayIcon.Icon references it (an Icon created
        // via FromHandle throws ObjectDisposedException on next use the
        // moment Dispose() is called, even though Dispose() itself doesn't
        // own/destroy the underlying GDI handle — see ApplyTrayIconForTheme's
        // doc comment for how disposal is sequenced). The handle itself isn't
        // tracked separately — Icon.Handle still returns it right up until
        // Dispose() is called, so it's read from there at cleanup time
        // instead of duplicating it in a second field.
        private Icon? _currentTrayIcon;

        /// <summary>
        /// Reentrancy guard for the RestoreFromTray/OnResize stack-overflow
        /// bugfix — see both methods' own doc comments. True for the whole
        /// duration of a RestoreFromTray call, including every intermediate
        /// Resize event it triggers.
        /// </summary>
        private bool _isRestoringFromTray;

        /// <summary>
        /// True while the user has paused enforcement from the tray menu —
        /// _enforceTimer/_clipTimer are stopped (same mechanism OnSleep
        /// already uses to fully suspend the engine) and WndProc's shell-hook
        /// new-window auto-detect (program.cs) skips acting on new windows
        /// while this is set. Deliberately does NOT block the explicit
        /// Ctrl+Shift+A/R hotkeys or in-app Save Changes/Enable Borderless —
        /// those are direct user intent, not the passive background
        /// "scanning" this toggle is for, and existing borderless windows are
        /// left exactly as they are (pausing suspends new work, it doesn't
        /// undo anything).
        /// </summary>
        private bool _enforcementPaused;

        private static readonly Color TrayChrome       = ColorTranslator.FromHtml("#0b0b0e"); // --nb-chrome
        private static readonly Color TrayBorder       = ColorTranslator.FromHtml("#23232c"); // --nb-border
        private static readonly Color TrayHover        = Color.FromArgb(255, 26, 23, 43);     // --nb-accent-tint (.12) blended over --nb-chrome
        private static readonly Color TrayTextPrimary  = ColorTranslator.FromHtml("#f2f2f7"); // --nb-text-primary
        private static readonly Color TrayTextBody     = ColorTranslator.FromHtml("#c9c9d6"); // --nb-text-body
        private static readonly Color TrayTextSecondary = ColorTranslator.FromHtml("#8a8a99"); // --nb-text-secondary
        private static readonly Color TrayTextMuted    = ColorTranslator.FromHtml("#55556a"); // --nb-text-muted
        private static readonly Color TrayAccentText   = ColorTranslator.FromHtml("#b9a6ff"); // --nb-accent-text
        private static readonly Color TraySuccess      = ColorTranslator.FromHtml("#3ddc84"); // --nb-success

        private static readonly Lazy<Bitmap> TrayDotActive = new(() => CreateDotBitmap(TraySuccess));
        private static readonly Lazy<Bitmap> TrayDotIdle   = new(() => CreateDotBitmap(TrayTextMuted));
        private static readonly Lazy<Font> TrayOpenItemFont = new(() => new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold));

        private void SetupTrayIcon()
        {
            _trayMenu.Renderer = new TrayMenuRenderer();
            _trayMenu.BackColor = TrayChrome;
            _trayMenu.ShowImageMargin = true;
            _trayMenu.Opening += (s, e) => RebuildTrayMenu();

            _trayIcon.Text             = "NoBorders";
            ApplyTrayIconForTheme(IsSystemTaskbarLightTheme());
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            _trayIcon.MouseUp += TrayIcon_MouseUp;
            _trayIcon.Visible          = true;

            // -minimized handling moved to OnLoad so BeginInvoke has a valid handle.
        }

        /// <summary>
        /// NotifyIcon's private ShowContextMenu() — reflected once and cached
        /// so the MouseUp workaround below degrades to a no-op (falling back
        /// to whatever the framework's own automatic behavior is) rather than
        /// throwing, if a future runtime ever renames/removes it.
        /// </summary>
        private static readonly MethodInfo? ShowContextMenuMethod =
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Bugfix: right-clicking the tray icon required two clicks before
        /// the menu appeared — first click did nothing, second showed it.
        ///
        /// First attempt (reverted): called SetForegroundWindow(this.Handle)
        /// here, reasoning WM_RBUTTONUP (this MouseUp event) fires before
        /// WM_CONTEXTMENU (which triggers the automatic Show()) and nothing
        /// was granting focus in between. Confirmed live this made it WORSE —
        /// the first click now activated the (hidden) main window instead of
        /// showing the menu, and the second click showed the menu as before.
        /// That's because NotifyIcon.ShowContextMenu() already calls
        /// SetForegroundWindow on its own internal hidden window before
        /// Show()'ing the strip; calling it again on a DIFFERENT window (the
        /// main form) first consumed Windows' one-shot foreground-lock
        /// exemption for that input event, so by the time the framework's own
        /// (correctly-targeted) call ran on WM_CONTEXTMENU, it lost the race
        /// and got throttled — the main form won the foreground instead of
        /// the menu.
        ///
        /// Real fix: don't add a second, wrong-target SetForegroundWindow
        /// call — invoke the framework's own ShowContextMenu() directly, one
        /// message earlier (WM_RBUTTONUP/MouseUp) than it would normally
        /// fire on its own (WM_CONTEXTMENU). This reuses the framework's
        /// already-correct internal SetForegroundWindow target, just ahead of
        /// whatever is dropping the automatic invocation on the first click.
        /// </summary>
        private void TrayIcon_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ShowContextMenuMethod?.Invoke(_trayIcon, null);
        }

        /// <summary>
        /// Bugfix: see OnResize's (program.cs) doc comment for the
        /// stack-overflow story — _isRestoringFromTray blocks OnResize's
        /// minimize-to-tray branch for this whole call, which is what
        /// actually breaks the Hide/Show feedback loop.
        ///
        /// Regression note: an earlier version of this fix ALSO reordered
        /// WindowState=Normal to run before Show() (on the theory that it
        /// would close the recursion window even without the guard). That
        /// combination reliably produced a second bug — restoring from tray
        /// showed a blank/grey, top-left-snapped-to-minimum-size window with
        /// no BlazorWebView content, which then got captured as the "real"
        /// bounds by CaptureWindowBounds() on the next genuine close,
        /// corrupting the saved position for every future launch too.
        /// Reverted to the original Show()-then-WindowState order — the
        /// reentrancy guard alone is sufficient to stop the stack overflow,
        /// and this order is what BlazorWebView/WinForms' own layout and
        /// restore-bounds tracking actually expect.
        /// </summary>
        private void RestoreFromTray()
        {
            _isRestoringFromTray = true;
            try
            {
                this.Show();
                this.ShowInTaskbar = true;
                this.WindowState   = FormWindowState.Normal;
                this.Activate();
                SetForegroundWindow(this.Handle);
            }
            finally
            {
                _isRestoringFromTray = false;
            }
        }

        /// <summary>
        /// Rebuilds the tray context menu's items from scratch, called from
        /// ContextMenuStrip.Opening so it always reflects live state: which
        /// tracked games are currently detected running (_runningGames, the
        /// same set the main rail's running-first sort already uses) and,
        /// per game, whether borderless is actually applied right now
        /// (_trackedWindows.Values — distinct from GameConfig.IsActive, which
        /// is just the user's on/off toggle and can be on while the window
        /// isn't tracked yet, e.g. an elevation mismatch).
        /// </summary>
        private void RebuildTrayMenu()
        {
            _trayMenu.SuspendLayout();
            _trayMenu.Items.Clear();

            var openItem = new ToolStripMenuItem("Open NoBorders")
            {
                ForeColor = TrayTextPrimary,
                Font      = TrayOpenItemFont.Value,
            };
            openItem.Click += (s, e) => RestoreFromTray();
            _trayMenu.Items.Add(openItem);

            _trayMenu.Items.Add(new ToolStripSeparator());

            var runningGames = _settings.Games
                .Where(g => _runningGames.Contains(g))
                .OrderBy(g => g.GameName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (runningGames.Count == 0)
            {
                _trayMenu.Items.Add(new ToolStripMenuItem("No tracked games detected")
                {
                    Enabled   = false,
                    ForeColor = TrayTextMuted,
                });
            }
            else
            {
                var appliedGames = new HashSet<GameConfig>(_trackedWindows.Values);
                foreach (var g in runningGames)
                {
                    bool applied = appliedGames.Contains(g);
                    _trayMenu.Items.Add(new ToolStripMenuItem(g.GameName)
                    {
                        Enabled   = false, // status row, not an action — OnRenderItemText/OnRenderItemImage below keep it fully colored despite that
                        ForeColor = applied ? TrayTextBody : TrayTextSecondary,
                        Image     = applied ? TrayDotActive.Value : TrayDotIdle.Value,
                    });
                }
            }

            _trayMenu.Items.Add(new ToolStripSeparator());

            var pauseItem = new ToolStripMenuItem(_enforcementPaused ? "Resume Enforcement" : "Pause Enforcement")
            {
                ForeColor = _enforcementPaused ? TrayAccentText : TrayTextPrimary,
            };
            pauseItem.Click += (s, e) => ToggleEnforcementPaused();
            _trayMenu.Items.Add(pauseItem);

            _trayMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit") { ForeColor = TrayTextPrimary };
            exitItem.Click += (s, e) => { _forceClose = true; Application.Exit(); };
            _trayMenu.Items.Add(exitItem);

            _trayMenu.ResumeLayout();
        }

        /// <summary>
        /// Tray menu's "Pause/Resume Enforcement" — stops (or restarts)
        /// _enforceTimer/_clipTimer, the exact same pair OnSleep/OnWake
        /// already stop/start for system suspend, so this reuses a mechanism
        /// already proven to cleanly halt and resume the whole engine rather
        /// than introducing a second way to do it. See _enforcementPaused's
        /// own doc comment for what staying paused does and doesn't affect.
        /// </summary>
        private void ToggleEnforcementPaused()
        {
            _enforcementPaused = !_enforcementPaused;

            if (_enforcementPaused)
            {
                _enforceTimer.Stop();
                _clipTimer.Stop();
                AppLogger.Log("Enforcement paused via tray menu.");
                ShowToast("Enforcement paused\nBorderless scanning and re-apply are temporarily off.", success: false);
            }
            else
            {
                _enforceTimer.Start();
                _clipTimer.Start();
                AppLogger.Log("Enforcement resumed via tray menu.");
                ShowToast("Enforcement resumed", success: true);
            }

            ApplyTrayIconForTheme(IsSystemTaskbarLightTheme());
        }

        /// <summary>
        /// Re-reads the taskbar theme and swaps the tray icon's mono variant
        /// if it (or the paused dimming) changed. Cheap no-op otherwise —
        /// called on every WM_SETTINGCHANGE (WndProc, program.cs), which
        /// fires for many unrelated settings, not just a theme toggle.
        /// </summary>
        private void RefreshTrayIconForTheme()
        {
            bool light = IsSystemTaskbarLightTheme();
            if (_trayIconIsLightVariant == light && _trayIconIsPausedVariant == _enforcementPaused) return;
            ApplyTrayIconForTheme(light);
        }

        /// <summary>
        /// Loads Resources/{fileName} (embedded via &lt;EmbeddedResource&gt; in
        /// the .csproj, so it travels inside the single-file publish output
        /// with no loose files needed), builds a real Icon via Bitmap.GetHicon
        /// (NotifyIcon.Icon has no direct "set from a PNG" path), and assigns
        /// it. While paused, dims it to ~45% alpha in place — icon_export/
        /// README.md's own "Tray states" section: "Paused — mono at ~45%
        /// (#6d6d7d)", same silhouette, no separate asset needed.
        ///
        /// Bugfix: originally disposed the just-created Icon/destroyed its
        /// handle immediately after assignment — Icon.FromHandle's Dispose()
        /// doesn't actually own/destroy the GDI handle, but it DOES mark the
        /// Icon object itself disposed, and NotifyIcon throws
        /// ObjectDisposedException the next time it touches an Icon in that
        /// state (confirmed live: crashed on startup). Both the Icon and its
        /// handle now stay alive for exactly as long as they're the current
        /// _trayIcon.Icon — the previous pair is only cleaned up here, after
        /// the new one has already taken over.
        /// </summary>
        private void ApplyTrayIconForTheme(bool taskbarIsLight)
        {
            try
            {
                // A light taskbar needs dark ink (tray-16-dark, #2f2f3a);
                // a dark taskbar (the common case) needs light ink
                // (tray-16-light, #e8e8ee) — see icon_export/README.md's
                // "Colors" section for the naming.
                string fileName = taskbarIsLight ? "tray-16-dark.png" : "tray-16-light.png";
                var asm = Assembly.GetExecutingAssembly();
                using Stream? stream = asm.GetManifestResourceStream($"NoBorders.Resources.{fileName}");
                if (stream == null)
                {
                    _trayIcon.Icon = TryExtractIcon(Application.ExecutablePath);
                    return;
                }

                using var sourceBmp = new Bitmap(stream);
                Bitmap themedBmp = _enforcementPaused ? ApplyAlphaScale(sourceBmp, 0.45f) : sourceBmp;
                try
                {
                    IntPtr newHIcon = themedBmp.GetHicon();
                    var newIcon = Icon.FromHandle(newHIcon);

                    _trayIcon.Icon = newIcon;

                    // Only now is it safe to let go of whichever icon/handle
                    // was previously assigned — _trayIcon.Icon no longer
                    // references it. Handle must be read before Dispose();
                    // Icon.Handle throws once the Icon is disposed.
                    if (_currentTrayIcon != null)
                    {
                        IntPtr oldHIcon = _currentTrayIcon.Handle;
                        _currentTrayIcon.Dispose();
                        DestroyIcon(oldHIcon);
                    }

                    _currentTrayIcon = newIcon;
                    _trayIconIsLightVariant = taskbarIsLight;
                    _trayIconIsPausedVariant = _enforcementPaused;
                }
                finally
                {
                    if (!ReferenceEquals(themedBmp, sourceBmp)) themedBmp.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "ApplyTrayIconForTheme");
                _trayIcon.Icon = TryExtractIcon(Application.ExecutablePath);
            }
        }

        /// <summary>Multiplies every pixel's alpha by <paramref name="alphaScale"/>, leaving RGB untouched — the "mono at ~45%" paused tray look.</summary>
        private static Bitmap ApplyAlphaScale(Bitmap source, float alphaScale)
        {
            var result = new Bitmap(source.Width, source.Height);
            using var g = Graphics.FromImage(result);
            var matrix = new ColorMatrix { Matrix33 = alphaScale };
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            return result;
        }

        /// <summary>Small filled circle, used as the tray menu's per-game status dot (icon_export/README.md: "state is tint + one badge dot").</summary>
        private static Bitmap CreateDotBitmap(Color color)
        {
            var bmp = new Bitmap(10, 10);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 8, 8);
            return bmp;
        }

        /// <summary>
        /// HKCU\...\Personalize\SystemUsesLightTheme — governs the taskbar
        /// (and Start menu), independent of AppsUseLightTheme (per-app theme).
        /// The README is explicit that tray icon tinting should follow this
        /// key specifically. Defaults to "dark taskbar" (false) if the key is
        /// missing, matching Windows' own out-of-box default.
        /// </summary>
        private static bool IsSystemTaskbarLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object? value = key?.GetValue("SystemUsesLightTheme");
                return value is int i && i != 0;
            }
            catch { return false; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Colors copied 1:1 from wwwroot/css/tokens.css — WinForms controls
        /// can't read CSS custom properties, so this is the tray menu's own
        /// hand-kept mirror of the same palette. TrayHover is the app's own
        /// --nb-accent-tint (rgba(139,108,255,.12)) pre-blended over
        /// --nb-chrome as a solid color, since ProfessionalColorTable colors
        /// don't composite against a transparent background.
        /// </summary>
        private sealed class TrayMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => TrayChrome;
            public override Color ImageMarginGradientBegin    => TrayChrome;
            public override Color ImageMarginGradientMiddle   => TrayChrome;
            public override Color ImageMarginGradientEnd      => TrayChrome;
            public override Color MenuBorder                  => TrayBorder;
            public override Color MenuItemBorder               => TrayHover;
            public override Color MenuItemSelected             => TrayHover;
            public override Color MenuItemSelectedGradientBegin => TrayHover;
            public override Color MenuItemSelectedGradientEnd   => TrayHover;
            public override Color MenuItemPressedGradientBegin  => TrayHover;
            public override Color MenuItemPressedGradientEnd    => TrayHover;
            public override Color SeparatorDark                => TrayBorder;
            public override Color SeparatorLight               => TrayBorder;
        }

        /// <summary>
        /// Forces item text/image to render exactly as this file sets them
        /// (ForeColor/Image), bypassing ToolStrip's default behavior of
        /// auto-graying both for Enabled=false items — needed since the
        /// per-game status rows are deliberately non-interactive
        /// (Enabled=false, so they don't hover-highlight or eat clicks) but
        /// must still show their real text color and full-color dot, not a
        /// washed-out "disabled" look.
        /// </summary>
        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            public TrayMenuRenderer() : base(new TrayMenuColorTable()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.ForeColor;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                if (e.Image != null) e.Graphics.DrawImage(e.Image, e.ImageRectangle);
                else base.OnRenderItemImage(e);
            }

            private static readonly SolidBrush ImageMarginBrush = new(TrayChrome);

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                e.Graphics.FillRectangle(ImageMarginBrush, e.AffectedBounds);
            }
        }
    }
}
