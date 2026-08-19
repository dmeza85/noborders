using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoBorders
{
    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM — MONITOR ENUMERATION
    // ══════════════════════════════════════════════════════════════════════════════
    // review.md §4.2: the monitor-discovery subsystem, split out of program.cs's
    // single 5,000+ line MainForm class into its own partial-class file — pure
    // behavior move, no logic changed. The DISPLAYCONFIG_* P/Invoke structs and
    // declarations these methods call stay in program.cs's existing "Win32
    // display-config structs" region: they're inert declarations already
    // clearly labeled there, interleaved with other unrelated P/Invoke blocks,
    // and moving them adds transcription risk for no behavioral value.
    // "Settings > Monitors" UI glue (DeleteMonitorDefault, SaveMonitorDefault,
    // RemoveAllSavedMonitors, etc.) stays in program.cs too — those lean on
    // _lstGames/_btnDeleteMonitor/_appState the same way every other
    // AppStateService bridge method does, so they belong with that group, not
    // this one; only the actual "what displays are connected, and what are
    // they called" logic lives here.
    internal sealed partial class MainForm
    {
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

                    _monitors.Add(new MonitorItem
                    {
                        ID = friendly, DeviceName = screen.DeviceName,
                        Width = screen.Bounds.Width, Height = screen.Bounds.Height,
                        Primary = screen.Primary
                    });
                    if (!IsIgnoredName(friendly)) _settings.KnownMonitors.Add(friendly);

                    // User request (2026-08-13): remember every connected
                    // monitor's real resolution so Display/Monitors' Result
                    // Preview and coverage %% keep working once it's
                    // unplugged — see MonitorResolution's own doc comment.
                    // Only writes (and queues a save) when the value is new
                    // or actually changed, not on every one of RefreshMonitors'
                    // frequent callers.
                    if (!_settings.LastKnownMonitorResolutions.TryGetValue(friendly, out var known)
                        || known.Width != screen.Bounds.Width || known.Height != screen.Bounds.Height)
                    {
                        _settings.LastKnownMonitorResolutions[friendly] =
                            new MonitorResolution { Width = screen.Bounds.Width, Height = screen.Bounds.Height };
                        AppLogger.LogVerbose($"Monitor resolution remembered: '{friendly}' = {screen.Bounds.Width}x{screen.Bounds.Height}.");
                        QueueSave();
                    }
                }

                // Phase 8.14 (user request): primary monitor first, for
                // "ease of finding it" — Screen.AllScreens' own enumeration
                // order has no guaranteed relationship to which one is
                // primary (confirmed live: a real 2-monitor setup had the
                // primary listed second). OrderByDescending is a stable
                // sort, so non-primary monitors keep their existing relative
                // order among themselves. Sorted once, here, rather than at
                // each consumer — AppStateService.Monitors (Settings >
                // Monitors' CONNECTED grid) and SyncMonitorComboBoxes' `all`
                // list (the Display tab's Target Monitor dropdown, built
                // from _monitors.Select(m => m.ID) below) both read this
                // same list, so both inherit the order for free.
                var primaryFirst = _monitors.OrderByDescending(m => m.Primary).ToList();
                _monitors.Clear();
                _monitors.AddRange(primaryFirst);

                SyncMonitorComboBoxes();
            }
            catch (Exception ex) { AppLogger.Log(ex, "RefreshMonitors"); }
        }

        /// <summary>
        /// review.md §3: backs Settings &gt; Monitors' "Detect Displays" button,
        /// previously dead (no @onclick at all). Re-runs the same monitor
        /// enumeration OnDisplayConfigChanged already reacts to automatically,
        /// for whenever a user wants it on demand instead of waiting for
        /// Windows to notify us (e.g. a display that reconnected without
        /// firing a config-changed event on some hardware). Not routed
        /// through a hidden WinForms button's Click, so RaiseChanged is
        /// explicit here — same shape as AdjustWidth/AdjustMonitorDefaultWidth
        /// below.
        /// </summary>
        private void DetectDisplays()
        {
            RefreshMonitors();
            _appState.RaiseChanged();
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
    }
}
