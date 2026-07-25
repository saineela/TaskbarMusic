using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TaskbarMusic.Models;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Lyrics fetcher: Local cache → Server LRC cache → LRCLIB get → LRCLIB search → BetterLyrics → lyrics-api → Spotify.
    /// BetterLyrics: real synced TTML (timed), lyrics-api: plain text (3s/line fallback).
    /// Auto-uploads fetched/tuned LRC to the server's global cache for sharing across laptops.
    /// Writes errors to Desktop\lyrics_error.txt for debugging.
    /// </summary>
    public class LyricsService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly LyricsCache _cache;
        private readonly WebSocketService? _wsService; // nullable — LRC server cache
        private const string LyricsApiBase = "https://lyrics.lewdhutao.my.eu.org";
        private readonly string _spotifyApiUrl;

        private string? _lastCacheKey;
        private List<LyricLine>? _lastLyrics;
        private double _currentOffset;

        public string? LastTrackArtist { get; private set; }
        public string? LastTrackTitle { get; private set; }
        public double LastTrackDuration { get; private set; }

        /// <summary>
        /// Provider of the currently loaded lyrics ("lrclib", "betterlyrics", "musixmatch", "youtube", "spotify", or null).
        /// "musixmatch" and "youtube" are plain-text providers (artificial 3s/line timing).
        /// </summary>
        public string? CurrentProvider { get; private set; }

        /// <summary>
        /// True when the current lyrics came from a plain-text provider (musixmatch/youtube)
        /// and have artificial timing instead of real synced timestamps.
        /// </summary>
        public bool IsPlainTextLyrics =>
            CurrentProvider == "musixmatch" || CurrentProvider == "youtube";

        public LyricsService(WebSocketService? wsService = null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "TaskbarMusic/1.0");
            _cache = new LyricsCache();
            _wsService = wsService;
            try { _spotifyApiUrl = (ConfigService.Load().SpotifyLyricsApiUrl ?? "http://localhost:8080").TrimEnd('/'); }
            catch { _spotifyApiUrl = "http://localhost:8080"; }
        }

        private static void LogError(string msg)
        {
            try
            {
                var path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\lyrics_error.txt";
                System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {msg}\n");
            }
            catch { }
        }

        private void TryCachePut(string artist, string title, string album, double dur, string provider, string lrc)
        {
            try
            {
                _cache.Put(artist, title, album, dur, new LyricsCandidate
                {
                    Provider = provider, ArtistName = artist, TrackName = title,
                    AlbumName = album, Duration = dur, SyncedLyrics = lrc, IsValid = true
                });
            }
            catch (Exception ex) { LogError($"Cache: {ex.Message}"); }
        }

        public async Task<List<LyricLine>> GetLyricsAsync(
            string trackName, string artistName, double durationSeconds = 0, string albumName = "")
        {
            if (string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(artistName))
                return new List<LyricLine>();

            var cacheKey = LyricsCache.MakeCacheKey(artistName, trackName, durationSeconds);

            // Track current song for offset lookups (must be before shortcut)
            LastTrackArtist = artistName;
            LastTrackTitle = trackName;
            LastTrackDuration = durationSeconds;
            _currentOffset = _cache.GetOffset(artistName, trackName, durationSeconds);

            // In-memory cache hit: apply offset before returning
            if (_lastCacheKey == cacheKey && _lastLyrics != null)
                return ApplyOffset(_lastLyrics, _currentOffset);

            // Step 1: Local SQLite cache
            var cached = _cache.Get(artistName, trackName, durationSeconds);
            if (cached != null && cached.IsValid && !string.IsNullOrWhiteSpace(cached.SyncedLyrics))
            {
                var lines = LrcParser.Parse(cached.SyncedLyrics);
                if (lines.Count > 0) { CurrentProvider = cached.Provider; _lastCacheKey = cacheKey; _lastLyrics = lines; return ApplyOffset(lines, _currentOffset); }
            }

            // Step 2: Server LRC cache (WebSocket)
            var serverLrc = await TryServerLrcCacheAsync(artistName, trackName, durationSeconds, albumName, cacheKey);
            if (serverLrc != null) return ApplyOffset(serverLrc, _currentOffset);

            // Step 3: LRCLIB /api/get (5s)
            var r = await TryLrcLibGet(trackName, artistName, albumName, durationSeconds);
            if (r != null) { CurrentProvider = "lrclib"; _lastCacheKey = cacheKey; _lastLyrics = r; _ = UploadToServerCacheAsync(artistName, trackName, LinesToLrc(r)); return ApplyOffset(r, _currentOffset); }

            // Step 4: LRCLIB /api/search (5s)
            r = await TryLrcLibSearch(trackName, artistName, durationSeconds);
            if (r != null) { CurrentProvider = "lrclib"; _lastCacheKey = cacheKey; _lastLyrics = r; _ = UploadToServerCacheAsync(artistName, trackName, LinesToLrc(r)); return ApplyOffset(r, _currentOffset); }

            // Step 5: BetterLyrics — real synced TTML (5s)
            r = await TryBetterLyrics(trackName, artistName, albumName, durationSeconds);
            if (r != null)
            {
                var lrc = LinesToLrc(r);
                TryCachePut(artistName, trackName, albumName, durationSeconds, "betterlyrics", lrc);
                CurrentProvider = "betterlyrics"; _lastCacheKey = cacheKey; _lastLyrics = r; _ = UploadToServerCacheAsync(artistName, trackName, lrc); return ApplyOffset(r, _currentOffset);
            }

            // Step 6a: Musixmatch via lyrics-api — plain text, 3s/line (5s)
            r = await TryLyricsApi(trackName, artistName, "musixmatch");
            if (r != null) { var lrc = LinesToLrc(r); TryCachePut(artistName, trackName, albumName, durationSeconds, "musixmatch", lrc); CurrentProvider = "musixmatch"; _lastCacheKey = cacheKey; _lastLyrics = r; _ = UploadToServerCacheAsync(artistName, trackName, lrc); return ApplyOffset(r, _currentOffset); }

            // Step 6b: YouTube via lyrics-api — plain text, 3s/line (5s)
            r = await TryLyricsApi(trackName, artistName, "youtube");
            if (r != null) { var lrc = LinesToLrc(r); TryCachePut(artistName, trackName, albumName, durationSeconds, "youtube", lrc); CurrentProvider = "youtube"; _lastCacheKey = cacheKey; _lastLyrics = r; _ = UploadToServerCacheAsync(artistName, trackName, lrc); return ApplyOffset(r, _currentOffset); }

            // Step 7: Spotify — timed lyrics via spotify-lyrics-api (5s)
            r = await TrySpotify(trackName, artistName);
            if (r != null) { var lrc = LinesToLrc(r); TryCachePut(artistName, trackName, albumName, durationSeconds, "spotify", lrc); CurrentProvider = "spotify"; _lastCacheKey = cacheKey; _lastLyrics = r; _ = UploadToServerCacheAsync(artistName, trackName, lrc); return ApplyOffset(r, _currentOffset); }

            CurrentProvider = null;
            _lastLyrics = null; _lastCacheKey = null;
            return new List<LyricLine>();
        }

        // ==================== LRCLIB ====================

        private async Task<List<LyricLine>?> TryLrcLibGet(string t, string a, string al, double d)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(t)}&artist_name={Uri.EscapeDataString(a)}";
                if (d > 0) url += $"&duration={Math.Round(d)}";
                if (!string.IsNullOrWhiteSpace(al)) url += $"&album_name={Uri.EscapeDataString(al)}";
                var json = await _httpClient.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(json);

                var candidate = LyricsMatcher.ParseCandidate(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(candidate.SyncedLyrics) &&
                    LyricsMatcher.IsValidMatch(a, t, d, candidate))
                {
                    var lines = LrcParser.Parse(candidate.SyncedLyrics);
                    if (lines.Count > 0) { TryCachePut(a, t, al, d, "lrclib", candidate.SyncedLyrics); return lines; }
                }
            }
            catch (Exception ex) { LogError($"LRCLIB get: {ex.Message}"); }
            return null;
        }

        private async Task<List<LyricLine>?> TryLrcLibSearch(string t, string a, double d)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(t)}&artist_name={Uri.EscapeDataString(a)}";
                var json = await _httpClient.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                var candidates = new List<LyricsCandidate>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var c = LyricsMatcher.ParseCandidate(item);
                    if (!string.IsNullOrWhiteSpace(c.SyncedLyrics))
                        candidates.Add(c);
                }

                var best = LyricsMatcher.GetBestMatch(candidates, a, t, "", d);
                if (best != null)
                {
                    var lines = LrcParser.Parse(best.SyncedLyrics!);
                    if (lines.Count > 0) { TryCachePut(a, t, "", d, "lrclib", best.SyncedLyrics!); return lines; }
                }
            }
            catch (Exception ex) { LogError($"LRCLIB search: {ex.Message}"); }
            return null;
        }

        // ==================== BETTERLYRICS (synced TTML) ====================

        private async Task<List<LyricLine>?> TryBetterLyrics(string t, string a, string al, double d)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var url = $"https://lyrics-api.boidu.dev/getLyrics?s={Uri.EscapeDataString(t)}&a={Uri.EscapeDataString(a)}";
                if (d > 0) url += $"&d={Math.Round(d)}";
                if (!string.IsNullOrWhiteSpace(al)) url += $"&al={Uri.EscapeDataString(al)}";

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    LogError($"BetterLyrics: HTTP {(int)resp.StatusCode}");
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync();
                return ParseTtmlResponse(body);
            }
            catch (Exception ex) { LogError($"BetterLyrics: {ex.Message}"); }
            return null;
        }

        private static List<LyricLine>? ParseTtmlResponse(string body)
        {
            // JSON: {"ttml": "<tt>..."}
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ttml", out var prop) &&
                    prop.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.GetString()))
                    return ParseTtml(prop.GetString()!);
            }
            catch (JsonException) { }

            // Raw TTML XML
            if (body.TrimStart().StartsWith("<"))
                return ParseTtml(body);

            return null;
        }

        private static List<LyricLine>? ParseTtml(string ttml)
        {
            try
            {
                var doc = XDocument.Parse(ttml);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                var pElements = doc.Descendants(ns + "p").ToList();
                if (pElements.Count == 0)
                    pElements = doc.Descendants().Where(e => e.Name.LocalName == "p").ToList();

                var lines = new List<LyricLine>();
                foreach (var p in pElements)
                {
                    var begin = p.Attribute("begin")?.Value ?? p.Attribute("start")?.Value;
                    if (begin == null) continue;
                    var time = ParseTtmlTime(begin);
                    if (time < 0) continue;
                    var text = Regex.Replace(p.Value ?? "", @"\s+", " ").Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    lines.Add(new LyricLine { TimeSeconds = time, Text = text });
                }
                lines.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                return lines.Count > 0 ? lines : null;
            }
            catch { return null; }
        }

        private static double ParseTtmlTime(string time)
        {
            if (string.IsNullOrWhiteSpace(time)) return -1;
            time = time.Trim();

            var m = Regex.Match(time, @"^(\d+):(\d{2}):(\d{2})(?:[.:](\d{1,3}))?$");
            if (m.Success)
                return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                       int.Parse(m.Groups[3].Value) +
                       (m.Groups[4].Success ? int.Parse(m.Groups[4].Value.PadRight(3, '0')) / 1000.0 : 0);

            m = Regex.Match(time, @"^(\d+):(\d{2})(?:[.:](\d{1,3}))?$");
            if (m.Success)
                return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) +
                       (m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(3, '0')) / 1000.0 : 0);

            m = Regex.Match(time, @"^([\d.]+)(h|m|s|ms)$", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amt))
                return m.Groups[2].Value.ToLowerInvariant() switch { "h" => amt * 3600, "m" => amt * 60, "s" => amt, "ms" => amt / 1000, _ => -1 };

            if (double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) return bare;
            return -1;
        }

        // ==================== LYRICS-API (plain text fallback) ====================

        private async Task<List<LyricLine>?> TryLyricsApi(string t, string a, string source)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var url = $"{LyricsApiBase}/v2/{source}/lyrics?title={Uri.EscapeDataString(t)}&artist={Uri.EscapeDataString(a)}";
                var resp = await _httpClient.GetAsync(url, cts.Token);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var preview = body.Length > 100 ? body.Substring(0, 100) + "..." : body;
                    LogError($"{source}: HTTP {(int)resp.StatusCode} — {preview}");
                    return null;
                }
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("lyrics", out var lyrics) &&
                    lyrics.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lyrics.GetString()))
                {
                    var lines = lyrics.GetString()!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var result = new List<LyricLine>();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var tx = lines[i].Trim();
                        if (!string.IsNullOrWhiteSpace(tx))
                            result.Add(new LyricLine { TimeSeconds = i * 3.0, Text = tx });
                    }
                    return result.Count > 0 ? result : null;
                }
            }
            catch (Exception ex) { LogError($"{source}: {ex.Message}"); }
            return null;
        }

        // ==================== SPOTIFY (last resort) ====================

        private async Task<List<LyricLine>?> TrySpotify(string t, string a)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var query = Uri.EscapeDataString($"{a} {t}");
                var sr = await _httpClient.GetAsync($"https://open.spotify.com/search/{query}", cts.Token);
                if (!sr.IsSuccessStatusCode) return null;
                var html = await sr.Content.ReadAsStringAsync();
                var m = Regex.Match(html, @"/track/([a-zA-Z0-9_-]{22})");
                if (!m.Success) return null;

                var lr = await _httpClient.GetStringAsync($"{_spotifyApiUrl}/?trackid={m.Groups[1].Value}&format=lrc", cts.Token);
                using var doc = JsonDocument.Parse(lr);
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.True) return null;
                if (!doc.RootElement.TryGetProperty("lines", out var la) || la.ValueKind != JsonValueKind.Array) return null;

                var result = new List<LyricLine>();
                foreach (var ln in la.EnumerateArray())
                {
                    if (!ln.TryGetProperty("words", out var w) || !ln.TryGetProperty("timeTag", out var tt)) continue;
                    var text = w.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var time = ParseSpotifyTime(tt.GetString());
                    if (time < 0) continue;
                    result.Add(new LyricLine { TimeSeconds = time, Text = text });
                }
                return result.Count > 0 ? result : null;
            }
            catch (Exception ex) { LogError($"Spotify: {ex.Message}"); }
            return null;
        }

        private static double ParseSpotifyTime(string? tt)
        {
            if (string.IsNullOrWhiteSpace(tt)) return -1;
            var m = Regex.Match(tt, @"^(\d+):(\d{2})\.(\d{2,3})$");
            if (!m.Success) return -1;
            return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) +
                   int.Parse(m.Groups[3].Value.PadRight(3, '0')) / 1000.0;
        }

        // ==================== OFFSET ====================

        /// <summary>
        /// Applies the per-song timing offset to all lyric lines.
        /// </summary>
        private static List<LyricLine> ApplyOffset(List<LyricLine> raw, double offset)
        {
            if (Math.Abs(offset) < 0.001) return raw;
            return raw.Select(l => new LyricLine
            {
                TimeSeconds = Math.Max(0, l.TimeSeconds + offset),
                Text = l.Text
            }).ToList();
        }

        /// <summary>
        /// Gets the current offset for the last-tracked song.
        /// </summary>
        public double GetCurrentOffset() => _currentOffset;

        /// <summary>
        /// Returns cache statistics (count, newest/oldest entry age).
        /// </summary>
        public (int count, string newest, string oldest) GetCacheStats() => _cache.GetStats();

        /// <summary>
        /// Returns all cached entries for the View Cached dialog.
        /// </summary>
        public List<CacheEntryInfo> GetAllCachedEntries() => _cache.GetAllEntries();

        /// <summary>
        /// Deletes a cached entry by its cache key.
        /// </summary>
        public bool DeleteCachedEntry(string cacheKey) => _cache.DeleteEntry(cacheKey);

        /// <summary>
        /// Adjusts the per-song offset and persists to cache.
        /// Returns the offsetted lyrics ready for display.
        /// </summary>
        public List<LyricLine> AdjustOffset(double deltaSeconds)
        {
            _currentOffset += deltaSeconds;
            _currentOffset = Math.Round(_currentOffset, 3); // avoid floating-point drift

            if (LastTrackArtist != null && LastTrackTitle != null)
                _cache.SetOffset(LastTrackArtist, LastTrackTitle, LastTrackDuration, _currentOffset);

            // Re-apply offset to raw lyrics
            if (_lastLyrics != null)
                return ApplyOffset(_lastLyrics, _currentOffset);

            return new List<LyricLine>();
        }

        /// <summary>
        /// Saves manually tuned lyrics (user-tapped timings) to cache and reloads them.
        /// Returns the newly parsed, properly timed lyrics.
        /// Resets the per-song offset to 0 since tuned timestamps are already correct.
        /// </summary>
        public List<LyricLine> SaveTunedLyrics(string newLrcContent)
        {
            if (LastTrackArtist == null || LastTrackTitle == null)
                return new List<LyricLine>();

            _cache.UpdateSyncedLyrics(LastTrackArtist, LastTrackTitle, LastTrackDuration, newLrcContent);
            CurrentProvider = "tuned"; // Mark as manually tuned so it's not flagged as plain text

            // Reset offset — tuned timestamps are already correct
            _currentOffset = 0;
            _cache.SetOffset(LastTrackArtist, LastTrackTitle, LastTrackDuration, 0);

            // Upload tuned lyrics to server cache (fire-and-forget)
            _ = UploadToServerCacheAsync(LastTrackArtist, LastTrackTitle, newLrcContent);

            var newLines = LrcParser.Parse(newLrcContent);
            _lastLyrics = newLines;
            _lastCacheKey = LyricsCache.MakeCacheKey(LastTrackArtist, LastTrackTitle, LastTrackDuration);
            return newLines;
        }

        // ==================== SERVER LRC CACHE ====================

        /// <summary>
        /// Tries the WebSocket server's global LRC cache. Returns parsed lyrics or null.
        /// On success, also stores in local SQLite cache.
        /// </summary>
        private async Task<List<LyricLine>?> TryServerLrcCacheAsync(
            string artist, string title, double duration, string album, string cacheKey)
        {
            if (_wsService == null || !_wsService.IsConnected)
                return null;

            try
            {
                var song = $"{artist} - {title}";
                var (found, content) = await _wsService.RequestLrcFromServerAsync(song);

                if (!found || string.IsNullOrWhiteSpace(content))
                    return null;

                var lines = LrcParser.Parse(content);
                if (lines.Count == 0)
                    return null;

                // Cache locally too
                TryCachePut(artist, title, album, duration, "server", content);
                CurrentProvider = "server";
                _lastCacheKey = cacheKey;
                _lastLyrics = lines;
                Console.WriteLine($"[LyricsService] Found {lines.Count} lines in server LRC cache: {song}");
                return lines;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LyricsService] Server LRC cache error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fire-and-forget upload of LRC content to the server's global cache.
        /// Applies the current per-song offset so server-stored LRC is already corrected.
        /// </summary>
        private async Task UploadToServerCacheAsync(string artist, string title, string lrcContent)
        {
            if (_wsService == null || !_wsService.IsConnected)
                return;

            try
            {
                // Apply current offset before uploading so the server copy is offset-corrected
                var offsetLrc = lrcContent;
                if (Math.Abs(_currentOffset) > 0.001)
                {
                    var parsed = LrcParser.Parse(lrcContent);
                    if (parsed.Count > 0)
                    {
                        var offsetted = ApplyOffset(parsed, _currentOffset);
                        offsetLrc = LinesToLrc(offsetted);
                    }
                }

                var song = $"{artist} - {title}";
                await _wsService.SendLrcUploadAsync(song, offsetLrc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LyricsService] Server LRC upload error: {ex.Message}");
            }
        }

        // ==================== HELPERS ====================

        private static string LinesToLrc(List<LyricLine> lines) =>
            string.Join("\n", lines.Select(l =>
                $"[{(int)(l.TimeSeconds / 60):D2}:{l.TimeSeconds % 60:00.00}] {l.Text}"));

        public void Dispose() { _httpClient?.Dispose(); _cache?.Dispose(); }
    }
}
