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
    internal sealed partial class MainForm
    {
        private bool? _trayIconIsLightVariant;
        private bool _trayIconIsPausedVariant;

        private Icon? _currentTrayIcon;

        private bool _isRestoringFromTray;

        private bool _enforcementPaused;

        private static readonly Color TrayChrome       = ColorTranslator.FromHtml("#0b0b0e");
        private static readonly Color TrayBorder       = ColorTranslator.FromHtml("#23232c");
        private static readonly Color TrayHover        = Color.FromArgb(255, 26, 23, 43);
        private static readonly Color TrayTextPrimary  = ColorTranslator.FromHtml("#f2f2f7");
        private static readonly Color TrayTextBody     = ColorTranslator.FromHtml("#c9c9d6");
        private static readonly Color TrayTextSecondary = ColorTranslator.FromHtml("#8a8a99");
        private static readonly Color TrayTextMuted    = ColorTranslator.FromHtml("#55556a");
        private static readonly Color TrayAccentText   = ColorTranslator.FromHtml("#b9a6ff");
        private static readonly Color TraySuccess      = ColorTranslator.FromHtml("#3ddc84");

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
        }

        private static readonly MethodInfo? ShowContextMenuMethod =
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.NonPublic | BindingFlags.Instance);

        private void TrayIcon_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ShowContextMenuMethod?.Invoke(_trayIcon, null);
        }

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

                if (_webViewNeedsRepaintAfterWake)
                {
                    _webViewNeedsRepaintAfterWake = false;
                    RepairWebViewAfterWake();
                }
            }
            finally
            {
                _isRestoringFromTray = false;
            }
        }

        private void RepairWebViewAfterWake()
        {
            try
            {
                var webView = _blazorWebView.WebView;
                if (webView == null) return;

                webView.Visible = false;
                webView.Visible = true;
                AppLogger.Log("  WebView2 repainted after wake-from-tray.");
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "RepairWebViewAfterWake");
            }
        }

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
                        Enabled   = false,
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

        private void ToggleEnforcementPaused()
        {
            _enforcementPaused = !_enforcementPaused;

            if (_enforcementPaused)
            {
                _enforceTimer.Stop();
                _clipTimer.Stop();
                AppLogger.Log("Enforcement paused via tray menu.");
                ShowToast("Enforcement paused\nBorderless scanning and re-apply are temporarily off.", LogLevel.Warn);
            }
            else
            {
                _enforceTimer.Start();
                _clipTimer.Start();
                AppLogger.Log("Enforcement resumed via tray menu.");
                ShowToast("Enforcement resumed", LogLevel.Ok);
            }

            ApplyTrayIconForTheme(IsSystemTaskbarLightTheme());
        }

        private void RefreshTrayIconForTheme()
        {
            bool light = IsSystemTaskbarLightTheme();
            if (_trayIconIsLightVariant == light && _trayIconIsPausedVariant == _enforcementPaused) return;
            ApplyTrayIconForTheme(light);
        }

        private void ApplyTrayIconForTheme(bool taskbarIsLight)
        {
            try
            {
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

        private static Bitmap CreateDotBitmap(Color color)
        {
            var bmp = new Bitmap(10, 10);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 8, 8);
            return bmp;
        }

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
