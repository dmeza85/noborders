using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace NoBorders
{
    internal sealed partial class MainForm
    {
        private void MigrateLegacyAppDataIfNeeded()
        {
            try
            {
                string oldDir = AppDomain.CurrentDomain.BaseDirectory;

                string oldConfig = Path.Combine(oldDir, "games_config.json");
                if (!File.Exists(_configPath) && File.Exists(oldConfig))
                {
                    File.Copy(oldConfig, _configPath);
                    AppLogger.Log($"Migrated legacy config from '{oldConfig}' to '{_configPath}'.");
                }

                string oldCacheDir = Path.Combine(oldDir, "wwwroot", "artwork-cache");
                string newCacheDir = Path.Combine(AppPaths.AppDataDir, "artwork-cache");
                if (Directory.Exists(oldCacheDir) && !Directory.Exists(newCacheDir))
                {
                    Directory.CreateDirectory(newCacheDir);
                    foreach (string file in Directory.GetFiles(oldCacheDir))
                        File.Copy(file, Path.Combine(newCacheDir, Path.GetFileName(file)), overwrite: false);
                    AppLogger.Log($"Migrated legacy artwork cache from '{oldCacheDir}' to '{newCacheDir}'.");
                }
            }
            catch (Exception ex) { AppLogger.Log(ex, "MigrateLegacyAppDataIfNeeded"); }
        }

        private void LoadConfig()
        {
            try
            {
                MigrateLegacyAppDataIfNeeded();

                string? rawJson = null;
                if (File.Exists(_configPath))
                {
                    rawJson = File.ReadAllText(_configPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(rawJson);
                    if (loaded != null) _settings = loaded;
                }

                _settings.MonitorDefaults ??= new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);
                _settings.Games           ??= new List<GameConfig>();
                _settings.HotkeyAddApp    ??= new HotkeyConfig { Modifiers = MOD_CONTROL, Key = (uint)Keys.F10 };
                _settings.HotkeyRefreshApp ??= new HotkeyConfig { Modifiers = MOD_CONTROL, Key = (uint)Keys.F11 };
                _settings.IgnoredProcesses ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _settings.LastKnownMonitorResolutions ??= new Dictionary<string, MonitorResolution>(StringComparer.OrdinalIgnoreCase);

                _settings.IgnoredProcesses.ExceptWith(_systemProcessBlocklist);

                if (string.IsNullOrEmpty(_settings.SteamGridDbApiKeyProtected) && !string.IsNullOrEmpty(rawJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                        if (doc.RootElement.TryGetProperty("SteamGridDbApiKey", out var legacyKeyEl) &&
                            legacyKeyEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string? legacyKey = legacyKeyEl.GetString();
                            if (!string.IsNullOrEmpty(legacyKey))
                            {
                                _settings.SteamGridDbApiKey = legacyKey;
                                AppLogger.Log("LoadConfig: migrated a legacy plaintext SteamGridDbApiKey to encrypted storage.");
                                SaveConfig();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log(ex, "LoadConfig: legacy SteamGridDbApiKey migration probe failed");
                    }
                }

                if (!string.IsNullOrEmpty(_settings.SteamGridDbApiKeyProtected))
                {
                    try
                    {
                        byte[] encrypted = Convert.FromBase64String(_settings.SteamGridDbApiKeyProtected);
                        byte[] plain     = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                        _settings.SteamGridDbApiKey = System.Text.Encoding.UTF8.GetString(plain);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log(ex, "LoadConfig: SteamGridDbApiKey decrypt failed — leaving it empty");
                    }
                }

                foreach (var g in _settings.Games)
                {
                    g.Profiles ??= new Dictionary<string, GameDisplayProfile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in g.Profiles.Keys)
                        if (!IsIgnoredName(k)) _settings.KnownMonitors.Add(k);
                }

                SortGames();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "LoadConfig");
                _settings = new AppSettings();
            }

            AppLogger.VerboseEnabled = _settings.VerboseLogging;
        }

        private void SaveConfig()
        {
            try
            {
                _settings.SteamGridDbApiKeyProtected = string.IsNullOrEmpty(_settings.SteamGridDbApiKey)
                    ? string.Empty
                    : Convert.ToBase64String(ProtectedData.Protect(
                        System.Text.Encoding.UTF8.GetBytes(_settings.SteamGridDbApiKey),
                        null, DataProtectionScope.CurrentUser));

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
    }
}
