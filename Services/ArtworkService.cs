using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using NoBorders;

namespace NoBorders.Services
{
    public enum ArtworkFetchStatus { NoApiKey, NoMatch, NetworkError, Success }

    public sealed record ArtworkCandidate(int SteamGridDbId, string Name);

    public sealed record ArtworkSearchResult(ArtworkFetchStatus Status, string? Error, IReadOnlyList<ArtworkCandidate> Candidates);

    public sealed record ArtworkDownloadResult(ArtworkFetchStatus Status, string? Error, string? HeroPath, string? IconPath);

    public sealed class ArtworkService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private const string ApiBase = "https://www.steamgriddb.com/api/v2";

        private readonly string _cacheDir;
        private readonly Func<string> _getApiKey;

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
                AppLogger.LogVerbose($"SteamGridDB search '{gameName}': HTTP {(int)resp.StatusCode}.");

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

                AppLogger.LogVerbose($"SteamGridDB search '{gameName}': {candidates.Count} candidate(s).");
                return candidates.Count == 0
                    ? new ArtworkSearchResult(ArtworkFetchStatus.NoMatch, null, candidates)
                    : new ArtworkSearchResult(ArtworkFetchStatus.Success, null, candidates);
            }
            catch (Exception ex)
            {
                return new ArtworkSearchResult(ArtworkFetchStatus.NetworkError, ex.Message, Array.Empty<ArtworkCandidate>());
            }
        }

        public sealed record ArtworkAutoFetchResult(ArtworkSearchResult Search, string? HeroPath, string? IconPath);

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

                AppLogger.LogVerbose($"SteamGridDB download (id={steamGridDbId}): hero={(heroPath != null ? "ok" : "none")}, icon={(iconPath != null ? "ok" : "none")}.");
                return new ArtworkDownloadResult(ArtworkFetchStatus.Success, null, heroPath, iconPath);
            }
            catch (Exception ex)
            {
                return new ArtworkDownloadResult(ArtworkFetchStatus.NetworkError, ex.Message, null, null);
            }
        }

        // Strips invisible Unicode format chars (BOM/ZWSP etc.) some window titles contain,
        // which otherwise truncate the search query after the first one.
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
            return $"artwork-cache/{fileName}";
        }

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

        internal static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) || c == ' ' || c == '\'' ? '_' : c).ToArray();
            string result = new string(chars);

            if (result.Length == 0 || result.All(c => c == '.'))
                result = "_" + result;

            return result;
        }

        public static string CssUrlEscape(string path) =>
            path.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
