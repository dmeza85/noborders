using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoBorders
{
    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM — GLOBAL HOTKEYS
    // ══════════════════════════════════════════════════════════════════════════════
    // review.md §4.2: the hotkey capture/registration subsystem, split out of
    // program.cs's single 5,000+ line MainForm class into its own partial-class
    // file — pure behavior move, no logic changed. ToggleHotkeyAddCapture/
    // ToggleHotkeyRefreshCapture (the Blazor "Rebind" buttons' entry points)
    // stay in program.cs: they're PerformClick() forwarders that belong with
    // the rest of the AppStateService bridge glue, not this subsystem's own
    // capture/registration mechanics. TryGetHotkeyTargetExe also stays — it
    // resolves a window's owning process for both a fired hotkey AND the
    // shell-hook window tracker, so it's genuinely shared rather than
    // hotkey-specific.
    internal sealed partial class MainForm
    {
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

            // Deferred, not a direct call (Phase 4.11 finding): when capture is
            // started from a Blazor button, this runs inside a callback WebView2's
            // Chromium widget originated for that click — a synchronous
            // display.Focus() here is silently overridden when that widget
            // re-asserts its own OS-level keyboard focus immediately afterward
            // (confirmed live via GetGUIThreadInfo: focus stayed on
            // Chrome_WidgetWin_1 despite Focus() having been called). Posting it
            // via BeginInvoke runs it after the current call stack — including
            // WebView2's own post-click focus handling — has fully unwound, so
            // the real focus move actually sticks. Purely a timing fix for
            // driving this frozen state machine from Blazor; doesn't change what
            // it decides or when a WinForms-originated capture behaves (that
            // path's call stack has no WebView2 involvement, so Focus() there
            // already worked synchronously and still does).
            BeginInvoke(new MethodInvoker(() => display.Focus()));

            // Capture UI notification only (Phase 4.11) — the state machine's
            // own decisions above are untouched, per MIGRATION_PLAN.md's frozen-
            // systems note ("only the capture UI ... moves to Razor").
            _appState.RaiseChanged();
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
            _appState.RaiseChanged(); // capture UI notification only, see BeginHotkeyCapture
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
            _appState.RaiseChanged(); // capture UI notification only, see BeginHotkeyCapture
        }

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
                AppLogger.Log($"  Bare key blocked — user should add Ctrl/Shift/Alt modifier.", LogLevel.Warn);
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
                AppLogger.Log($"{label} hotkey ({config}) conflicts with another app.", LogLevel.Warn);
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
    }
}
