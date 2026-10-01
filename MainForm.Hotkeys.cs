using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoBorders
{
    internal sealed partial class MainForm
    {
        private void BeginHotkeyCapture(int hotkeyId, HotkeyConfig config, TextBox display, Button button, string label)
        {
            if (_capturingHotkeyId != 0) CancelHotkeyCapture();

            UnregisterHotKey(this.Handle, hotkeyId);

            _capturingHotkeyId      = hotkeyId;
            _capturingHotkeyConfig  = config;
            _capturingHotkeyDisplay = display;
            _capturingHotkeyButton  = button;
            _capturingHotkeyLabel   = label;

            display.Text = "Press new key combo… (Esc to cancel)";
            button.Text  = "Listening… (click to cancel)";

            BeginInvoke(new MethodInvoker(() => display.Focus()));

            _appState.RaiseChanged();
        }

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

            RegisterHotkeys();
            _appState.RaiseChanged();
        }

        private void CaptureHotkey(KeyEventArgs e, TextBox display)
        {
            if (_capturingHotkeyId == 0 || _capturingHotkeyDisplay != display) return;
            e.SuppressKeyPress = true;
            if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu) return;

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
                uint mods = MOD_NOREPEAT;
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
            _appState.RaiseChanged();
        }

        private void RegisterHotkeys()
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID_ADD);
            UnregisterHotKey(this.Handle, HOTKEY_ID_REFRESH);

            TryRegisterHotkey(HOTKEY_ID_ADD,     _settings.HotkeyAddApp,     "Add",     _lblHotkeyAddStatus);
            TryRegisterHotkey(HOTKEY_ID_REFRESH,  _settings.HotkeyRefreshApp, "Refresh", _lblHotkeyRefreshStatus);
        }

        private void TryRegisterHotkey(int id, HotkeyConfig config, string label, Label? statusLabel = null)
        {
            if (config.Key == 0)
            {
                SetHotkeyStatusLabel(statusLabel, "Disabled", Color.Empty, muted: true);
                return;
            }

            uint mods = config.Modifiers | MOD_NOREPEAT;

            if (RegisterHotKey(this.Handle, id, mods, config.Key))
            {
                AppLogger.Log($"RegisterHotKey {label} OK: {config}");
                SetHotkeyStatusLabel(statusLabel, "Active", Color.FromArgb(72, 199, 72), muted: false);
                return;
            }

            int err = Marshal.GetLastWin32Error();
            AppLogger.Log($"RegisterHotKey {label} failed (Win32={err}): {config}");

            bool bareKey = (mods & ~MOD_NOREPEAT) == 0;
            Color failColor = Color.FromArgb(210, 70, 70);
            if (err == 1409 && bareKey)
            {
                SetHotkeyStatusLabel(statusLabel, "Not active — blocked by Windows", failColor, muted: false);
                if (this.IsHandleCreated)
                    ShowToast(
                        $"{label} hotkey ({config}) is blocked by Windows.\n"
                        + "Try adding Shift or Ctrl in Settings.",
                        LogLevel.Warn);
                AppLogger.Log($"  Bare key blocked — user should add Ctrl/Shift/Alt modifier.", LogLevel.Warn);
            }
            else if (err == 1409)
            {
                SetHotkeyStatusLabel(statusLabel, "Not active — conflicts with another app", failColor, muted: false);
                if (this.IsHandleCreated)
                    ShowToast(
                        $"{label} hotkey ({config}) conflicts with another app.\n"
                        + "Change it in Settings.",
                        LogLevel.Warn);
                AppLogger.Log($"{label} hotkey ({config}) conflicts with another app.", LogLevel.Warn);
            }
            else
            {
                SetHotkeyStatusLabel(statusLabel, $"Not active — Win32 error {err}", failColor, muted: false);
            }
        }

        private void SetHotkeyStatusLabel(Label? lbl, string text, Color color, bool muted)
        {
            if (lbl == null) return;
            lbl.Text      = text;
            lbl.ForeColor = muted ? PaletteTextMuted : color;
        }
    }
}
