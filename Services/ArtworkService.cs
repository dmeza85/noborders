using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace NoBorders.Services
{
    public enum ArtworkFetchStatus { NoApiKey, NoMatch, NetworkError, Success }

    /// <summary>One SteamGridDB search result — a candidate game to fetch art for,
    /// not yet art itself (the user picks one before anything downloads).</summary>
    public sealed record ArtworkCandidate(int SteamGridDbId, string Name);

    public sealed record ArtworkSearchResult(ArtworkFetchStatus Status, string? Error, IReadOnlyList<ArtworkCandidate> Candidates);

    public sealed record ArtworkDownloadResult(ArtworkFetchStatus Status, string? Error, string? HeroPath, string? IconPath);

    /// <summary>
    /// Phase 8 (MIGRATION_PLAN.md): the app's first-ever network dependency —
    /// SteamGridDB (steamgriddb.com/api/v2) for real hero banners/rail icons,
    /// with a fully offline .exe-icon fallback so a missing/invalid API key or
    /// no network never blocks the app or shows a broken image, only ever the
    /// existing CSS placeholder art.
    ///
    /// Nothing here runs automatically or in the background — every fetch is a
    /// single user-triggered action (the Display/Matching detail pane's "Fetch
    /// Artwork" button), matching the app's existing "explicit action, not a
    /// background poller" pattern (c.f. the ↺ Fetch Name button). Downloaded
    /// images are cached to disk under wwwroot/artwork-cache and referenced by
    /// GameConfig.HeroImagePath/IconImagePath (a relative wwwroot path, not a
    /// live URL) — so once fetched, a game's art loads offline from then on,
    /// with no repeat network calls.
    ///
    /// SteamGridDB's v2 JSON envelope (confirmed live against the real API,
    /// unauthenticated, during development: `{"success":false,"errors":[...]}`
    /// on failure) is assumed to mirror to `{"success":true,"data":[...]}` on
    /// success, per the API's public documentation — the success-path shape
    /// itself was NOT verified against a real response, since that requires a
    /// real personal API key (steamgriddb.com/profile/preferences/api) this
    /// session had no way to obtain. Parsing below is defensive (every field
    /// read is a checked TryGetProperty, never an indexer that can throw) so a
    /// shape mismatch degrades to a clean NetworkError rather than a crash —
    /// treat the SteamGridDB path as needing one real end-to-end check with a
    /// live key before fully trusting it.
    /// </summary>
    public sealed class ArtworkService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private const string ApiBase = "https://www.steamgriddb.com/api/v2";

        private readonly string _cacheDir;
        private readonly Func<string> _getApiKey;

        /// <param name="wwwrootPath">Absolute path to the app's real wwwroot (same
        /// directory BlazorWebView serves statically), so cached files are
        /// immediately reachable via a relative URL with no extra hosting setup.</param>
        /// <param name="getApiKey">Live accessor for AppSettings.SteamGridDbApiKey —
        /// same "read through a delegate, never a snapshot" convention as
        /// AppStateService, so a key pasted in Settings takes effect immediately.</param>
        public ArtworkService(string wwwrootPath, Func<string> getApiKey)
        {
            _cacheDir = Path.Combine(wwwrootPath, "artwork-cache");
            Directory.CreateDirectory(_cacheDir);
            _getApiKey = getApiKey;
        }

        public async Task<ArtworkSearchResult> SearchAsync(string gameName)
        {
            string apiKey = _getApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
                return new ArtworkSearchResult(ArtworkFetchStatus.NoApiKey, null, Array.Empty<ArtworkCandidate>());

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiBase}/search/autocomplete/{Uri.EscapeDataString(gameName)}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await Http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
                {
                    string err = TryGetFirstError(doc) ?? $"SteamGridDB returned HTTP {(int)resp.StatusCode}";
                    return new ArtworkSearchResult(ArtworkFetchStatus.NetworkError, err, Array.Empty<ArtworkCandidate>());
                }

                var candidates = new List<ArtworkCandidate>();
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataEl.EnumerateArray().Take(6))
                    {
                        if (item.TryGetProperty("id", out var idEl) && item.TryGetProperty("name", out var nameEl))
                            candidates.Add(new ArtworkCandidate(idEl.GetInt32(), nameEl.GetString() ?? "(unnamed)"));
                    }
                }

                return candidates.Count == 0
                    ? new ArtworkSearchResult(ArtworkFetchStatus.NoMatch, null, candidates)
                    : new ArtworkSearchResult(ArtworkFetchStatus.Success, null, candidates);
            }
            catch (Exception ex)
            {
                return new ArtworkSearchResult(ArtworkFetchStatus.NetworkError, ex.Message, Array.Empty<ArtworkCandidate>());
            }
        }

        /// <summary>Downloads the top hero + icon for a chosen SteamGridDB game id
        /// and caches both to disk. Either can independently come back null if
        /// that game has no art of that particular type uploaded — not an error,
        /// just nothing to show for that slot (stays the CSS placeholder).</summary>
        public async Task<ArtworkDownloadResult> DownloadAsync(int steamGridDbId, string cacheSlug)
        {
            string apiKey = _getApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
                return new ArtworkDownloadResult(ArtworkFetchStatus.NoApiKey, null, null, null);

            try
            {
                string? heroUrl = await GetFirstArtUrlAsync(apiKey, $"{ApiBase}/heroes/game/{steamGridDbId}");
                string? iconUrl = await GetFirstArtUrlAsync(apiKey, $"{ApiBase}/icons/game/{steamGridDbId}");

                string? heroPath = heroUrl != null ? await DownloadToCache(heroUrl, $"{cacheSlug}-hero") : null;
                string? iconPath = iconUrl != null ? await DownloadToCache(iconUrl, $"{cacheSlug}-icon") : null;

                return new ArtworkDownloadResult(ArtworkFetchStatus.Success, null, heroPath, iconPath);
            }
            catch (Exception ex)
            {
                return new ArtworkDownloadResult(ArtworkFetchStatus.NetworkError, ex.Message, null, null);
            }
        }

        private static async Task<string?> GetFirstArtUrlAsync(string apiKey, string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
                return null;
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                    return urlEl.GetString();
            }
            return null;
        }

        private async Task<string?> DownloadToCache(string url, string baseFileName)
        {
            byte[] bytes = await Http.GetByteArrayAsync(url);
            string ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
            string fileName = SanitizeFileName(baseFileName) + ext;
            string fullPath = Path.Combine(_cacheDir, fileName);
            await File.WriteAllBytesAsync(fullPath, bytes);
            // Cache-bust: WebView2 can hold onto a previously-served path, so a
            // re-fetch that overwrites the same filename needs a fresh query
            // string for the <img> tag to actually reload it (callers append
            // "?v=" + a token themselves — this just returns the stable path).
            return $"artwork-cache/{fileName}";
        }

        /// <summary>Fully offline fallback: the game's own .exe icon, same
        /// Icon.ExtractAssociatedIcon MainForm's LstGames_DrawItem already uses
        /// for the WinForms rail (Phase 6.1's doc comment) — reused here via the
        /// same Win32-backed API, just written to a cacheable PNG on disk instead
        /// of kept as an in-memory GDI+ Image.</summary>
        public string? ExtractExeIcon(string exePath, string cacheSlug)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon == null) return null;
                using var bitmap = icon.ToBitmap();
                string fileName = SanitizeFileName(cacheSlug) + "-icon.png";
                string fullPath = Path.Combine(_cacheDir, fileName);
                bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
                return $"artwork-cache/{fileName}";
            }
            catch { return null; }
        }

        private static string? TryGetFirstError(JsonDocument doc)
        {
            if (doc.RootElement.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in errorsEl.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) return e.GetString();
            }
            return null;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }
    }
}
