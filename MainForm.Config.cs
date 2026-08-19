using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace NoBorders
{
    // ══════════════════════════════════════════════════════════════════════════════
    // MAIN FORM — JSON PERSISTENCE
    // ══════════════════════════════════════════════════════════════════════════════
    // review.md §4.2: the config load/save subsystem, split out of program.cs's
    // single 5,000+ line MainForm class into its own partial-class file — pure
    // behavior move, no logic changed. _configPath and _saveDebounce stay
    // declared in program.cs's shared field block (the latter is started/
    // stopped from a few other lifecycle spots there too — OnFormClosing,
    // the elevation-restart path — so splitting just the field out would add
    // indirection with no real gain).
    internal sealed partial class MainForm
    {
        /// <summary>
        /// review.md §2.1: one-time migration for anyone upgrading from a build
        /// that still stored games_config.json / artwork-cache next to the .exe
        /// (AppDomain.CurrentDomain.BaseDirectory). Both now live under
        /// AppPaths.AppDataDir (%LOCALAPPDATA%\NoBorders) instead — without this,
        /// LoadConfig would simply find nothing at the new path and an existing
        /// user would silently lose every saved game, hotkey, and monitor
        /// default on their next launch. Only copies (never deletes/moves the
        /// old files) and only when the new location doesn't already have them,
        /// so it's safe to run on every startup.
        /// </summary>
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
                _settings.HotkeyAddApp    ??= new HotkeyConfig { Modifiers = MOD_CONTROL | MOD_SHIFT, Key = (uint)Keys.A };
                _settings.HotkeyRefreshApp ??= new HotkeyConfig { Modifiers = MOD_CONTROL | MOD_SHIFT, Key = (uint)Keys.R };
                _settings.IgnoredProcesses ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _settings.LastKnownMonitorResolutions ??= new Dictionary<string, MonitorResolution>(StringComparer.OrdinalIgnoreCase);

                // Settings > Ignore List (Phase 8.4): _settings.IgnoredProcesses
                // holds only the user's own additions — the built-in
                // _systemProcessBlocklist stays internal, never shown or editable
                // (GetOpenWindowEntries/TryGetHotkeyTargetExe check both sets, see
                // their own comments). Stripping any built-in-list entry here
                // covers a config saved by an earlier build that briefly seeded
                // this set FROM the defaults — without this, those ~100 entries
                // would otherwise show up looking like the user's own customizations.
                _settings.IgnoredProcesses.ExceptWith(_systemProcessBlocklist);

                // review.md §1.3: one-time migration for anyone who already has
                // a plaintext key saved from before this fix — the new
                // SteamGridDbApiKey property is [JsonIgnore], so
                // Deserialize<AppSettings> above silently skips the old JSON
                // key entirely; without this, an existing saved key would
                // just vanish (SteamGridDbApiKeyProtected doesn't exist yet in
                // an old file, so it deserializes empty) rather than getting
                // re-saved encrypted. Only a raw JsonDocument probe can still
                // see the ignored property's old value. Confirmed live this
                // was NOT a hypothetical: this exact repo's own
                // games_config.json had a real plaintext key sitting in it
                // when this fix was written.
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
                                // Re-save immediately rather than waiting for
                                // whatever the next unrelated save trigger
                                // happens to be — closes the plaintext-on-disk
                                // window as soon as possible instead of
                                // leaving the old value sitting there for an
                                // indeterminate time.
                                SaveConfig();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log(ex, "LoadConfig: legacy SteamGridDbApiKey migration probe failed");
                    }
                }

                // review.md §1.3: decrypt the persisted DPAPI blob (if any)
                // into the real in-memory field. CurrentUser scope, no extra
                // entropy — matches DPAPI's own standard "protect for this
                // Windows account" usage; a failure here (e.g. the config was
                // copied to a different machine/user, where CurrentUser-scoped
                // DPAPI blobs can never decrypt by design) degrades to an
                // empty key rather than a crash, same as any other
                // corrupt-config scenario LoadConfig already tolerates.
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

                // Sort immediately after load so the list is alphabetical
                // from the very first frame, before PopulateGamesList runs.
                SortGames();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "LoadConfig");
                _settings = new AppSettings();
            }

            // AppLogger.VerboseEnabled is separate static state from
            // _settings.VerboseLogging (the persisted preference) — needs
            // syncing here on every load, including the exception fallback
            // just above, or a prior run's "on" choice wouldn't take effect
            // until the user re-toggled it.
            AppLogger.VerboseEnabled = _settings.VerboseLogging;
        }

        private void SaveConfig()
        {
            try
            {
                // review.md §1.3: re-encrypt SteamGridDbApiKey (the real,
                // JsonIgnore'd field) into the persisted
                // SteamGridDbApiKeyProtected blob right before every save, so
                // the two can never drift — same "encrypt at the write
                // boundary" shape as LoadConfig decrypts at the read
                // boundary, both one-shot, both here only.
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
