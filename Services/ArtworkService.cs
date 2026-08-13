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
    /// Nothing here polls or runs on a timer — every fetch is still triggered
    /// by a single user action, matching the app's existing "explicit action,
    /// not a background poller" pattern (c.f. the ↺ Fetch Name button). Two
    /// such actions exist: the Display/Matching detail pane's "Fetch Artwork"
    /// button (always available, always shows the picker after the first
    /// attempt), and — net-new — adding a game at all (running-app picker or
    /// Browse for EXE), which now tries <see cref="TryAutoFetchAsync"/> once,
    /// automatically, right after the add succeeds; a direct name match
    /// applies immediately, anything less just leaves the new game's art
    /// blank for "Fetch Artwork" to handle later, same as before this existed.
    /// Downloaded images are cached to disk under wwwroot/artwork-cache and
    /// referenced by GameConfig.HeroImagePath/IconImagePath (a relative
    /// wwwroot path, not a live URL) — so once fetched, a game's art loads
    /// offline from then on, with no repeat network calls.
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

        /// <param name="cacheRootDir">Absolute path to the writable app-data
        /// directory (AppPaths.AppDataDir, %LOCALAPPDATA%\NoBorders — review.md
        /// §2.1). ArtworkAwareBlazorWebView layers a PhysicalFileProvider
        /// rooted here on top of the install-directory wwwroot, so files
        /// written into cacheRootDir\artwork-cache are still reachable via the
        /// same relative "artwork-cache/..." URL with no extra hosting setup.</param>
        /// <param name="getApiKey">Live accessor for AppSettings.SteamGridDbApiKey —
        /// same "read through a delegate, never a snapshot" convention as
        /// AppStateService, so a key pasted in Settings takes effect immediately.</param>
        public ArtworkService(string cacheRootDir, Func<string> getApiKey)
        {
            _cacheDir = Path.Combine(cacheRootDir, "artwork-cache");
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
                    $"{ApiBase}/search/autocomplete/{Uri.EscapeDataString(SanitizeSearchQuery(gameName))}");
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

        /// <summary>Result of <see cref="TryAutoFetchAsync"/> — Search is always
        /// populated (even on a NoMatch/NoApiKey/NetworkError status, so a
        /// caller that also wants to show the manual picker on a miss doesn't
        /// need a second network round-trip); HeroPath/IconPath are non-null
        /// only when a direct match was found AND downloaded successfully.</summary>
        public sealed record ArtworkAutoFetchResult(ArtworkSearchResult Search, string? HeroPath, string? IconPath);

        /// <summary>
        /// Searches for <paramref name="gameName"/> and, only if a candidate is
        /// an exact name match (<see cref="IsDirectMatch"/>), downloads and
        /// returns its art. Used both by the picker's own first-attempt
        /// auto-apply and by the "auto-fetch right after adding a new game"
        /// flow — this method only ever fetches, it never applies anything
        /// itself (no AppStateService dependency at this layer); callers apply
        /// HeroPath/IconPath via AppStateService.ApplyArtwork themselves, and
        /// should leave a game's existing Hero/IconImagePath alone when both
        /// come back null — that's "no direct match, nothing to auto-apply",
        /// not an error, and the user's own Fetch Artwork button/picker
        /// remains available either way.
        /// </summary>
        public async Task<ArtworkAutoFetchResult> TryAutoFetchAsync(string gameName, string cacheSlug)
        {
            var search = await SearchAsync(gameName);
            if (search.Status != ArtworkFetchStatus.Success)
                return new ArtworkAutoFetchResult(search, null, null);

            var directMatch = search.Candidates.FirstOrDefault(c => IsDirectMatch(c.Name, gameName));
            if (directMatch == null)
                return new ArtworkAutoFetchResult(search, null, null);

            var result = await DownloadAsync(directMatch.SteamGridDbId, cacheSlug);
            if (result.Status != ArtworkFetchStatus.Success || (result.HeroPath == null && result.IconPath == null))
                return new ArtworkAutoFetchResult(search, null, null);

            return new ArtworkAutoFetchResult(search, result.HeroPath, result.IconPath);
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

        /// <summary>Some games' real window titles carry invisible Unicode
        /// "format" characters (BOM, zero-width space/joiners, etc.) sprinkled
        /// between letters — confirmed live (Phase 8.3 bugfix) against a real
        /// game whose fetched window title reads as "ARC Raiders" to the eye
        /// but is actually "A&lt;BOM&gt;RC&lt;ZWSP&gt; &lt;ZWSP&gt;&lt;ZWSP&gt;R..." — plausibly the
        /// game's own anti-cheat mangling its title to resist naive text
        /// scraping. `Uri.EscapeDataString` percent-encodes those invisible
        /// characters right along with the visible ones, so SteamGridDB's
        /// autocomplete effectively only ever saw "A" before hitting the
        /// first one and returned unrelated "starts with A" results. Only
        /// used for the search query — the stored/display GameName is left
        /// exactly as fetched, since it isn't this method's job to silently
        /// rewrite a user's game entry.</summary>
        private static string SanitizeSearchQuery(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == System.Globalization.UnicodeCategory.Format) continue;
                sb.Append(category == System.Globalization.UnicodeCategory.SpaceSeparator ? ' ' : c);
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        /// <summary>True when a SteamGridDB candidate's name is, after the same
        /// sanitization <see cref="SearchAsync"/> applies to the query,
        /// identical to the game's own name — used by the picker to
        /// auto-apply on the very first fetch attempt without waiting for a
        /// manual pick. Deliberately exact-match only (not "starts with" or
        /// "contains"): a same-ish-titled but different game (e.g. a demo, a
        /// sequel, "X: Remastered") should never get silently auto-applied,
        /// only an unambiguous match does — anything less still falls through
        /// to the normal manual picker.</summary>
        public static bool IsDirectMatch(string candidateName, string gameName) =>
            string.Equals(SanitizeSearchQuery(candidateName), SanitizeSearchQuery(gameName), StringComparison.OrdinalIgnoreCase);

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

        /// <summary>Also replaces spaces (a perfectly valid Windows filename
        /// character, so <see cref="Path.GetInvalidFileNameChars"/> alone
        /// doesn't touch them) with underscores — confirmed live (Phase 8.3
        /// bugfix) that BlazorWebView's static-file handler passes the request
        /// path's percent-encoding straight to the file provider without
        /// decoding it first, so a cached file with a literal space in its
        /// name (e.g. an ExePath under "...\Core Keeper\...") is requested as
        /// "...Core%20Keeper..." and never matches, 404-ing forever no matter
        /// how correct the file's actual physical location is.
        ///
        /// Also replaces apostrophes for the same reason as spaces above, not
        /// a request-routing one this time: cacheSlug is usually derived from
        /// GameConfig.ExePath (Phase 8.3/8.4's Slug property), and a real
        /// install path like "...\Len's Island\Len's Island.exe" carries one
        /// straight into the cached file name. Every consumer of Hero/
        /// IconImagePath (MainShell's hero/rail thumbnails, Toast's icon)
        /// embeds the path in a single-quoted CSS url('...') — confirmed live
        /// (Phase 8.7 bugfix) that an apostrophe there prematurely closes the
        /// CSS string, so the browser silently drops the whole background-
        /// image declaration and just keeps showing the placeholder, even
        /// though the fetch genuinely succeeded and the file is sitting right
        /// there on disk. CssUrlEscape (below) defends every consumer against
        /// this for paths that already made it into a saved profile before
        /// this fix; sanitizing it out of new file names here is the cheaper
        /// fix for everything fetched from now on.</summary>
        /// <summary>internal, not private, so review.md §5's regression test can reach it directly.</summary>
        internal static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) || c == ' ' || c == '\'' ? '_' : c).ToArray();
            string result = new string(chars);

            // review.md §2.3: today this can never come out as exactly ".." —
            // \ and / are already replaced above, and every call site appends
            // an extension afterward — but both of those are invariants held
            // elsewhere, not enforced at this function's own boundary. Guard
            // it here directly so a future edit to either of those can't
            // quietly reintroduce a path-traversal primitive: a result made
            // up entirely of dots can never safely reach Path.Combine.
            if (result.Length == 0 || result.All(c => c == '.'))
                result = "_" + result;

            return result;
        }

        /// <summary>Escapes a relative artwork path for safe embedding inside a
        /// single-quoted CSS <c>url('...')</c> declaration — every consumer
        /// (MainShell's hero/rail thumbnails, Toast's icon) builds one this
        /// way. Defends against a stray <c>'</c> the same way <see
        /// cref="SanitizeFileName"/> now prevents in newly-cached file names,
        /// but also covers paths saved before that fix existed (an already-
        /// persisted GameConfig.HeroImagePath/IconImagePath with an
        /// apostrophe in it doesn't get rewritten retroactively — this is
        /// what actually makes it render). Backslash escaped too, defensively,
        /// even though every path here is already forward-slash (DownloadToCache/
        /// ExtractExeIcon both return "artwork-cache/...").</summary>
        public static string CssUrlEscape(string path) =>
            path.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
