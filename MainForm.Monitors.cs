using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoBorders
{
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

                    if (!_settings.LastKnownMonitorResolutions.TryGetValue(friendly, out var known)
                        || known.Width != screen.Bounds.Width || known.Height != screen.Bounds.Height)
                    {
                        _settings.LastKnownMonitorResolutions[friendly] =
                            new MonitorResolution { Width = screen.Bounds.Width, Height = screen.Bounds.Height };
                        AppLogger.LogVerbose($"Monitor resolution remembered: '{friendly}' = {screen.Bounds.Width}x{screen.Bounds.Height}.");
                        QueueSave();
                    }
                }

                var primaryFirst = _monitors.OrderByDescending(m => m.Primary).ToList();
                _monitors.Clear();
                _monitors.AddRange(primaryFirst);

                SyncMonitorComboBoxes();
            }
            catch (Exception ex) { AppLogger.Log(ex, "RefreshMonitors"); }
        }

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

            string prevDetail = _cmbMonitor.SelectedItem?.ToString() ?? string.Empty;
            _cmbMonitor.Items.Clear();
            _cmbMonitor.Items.AddRange(all.ToArray());
            RestoreComboSelection(_cmbMonitor, prevDetail);

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
