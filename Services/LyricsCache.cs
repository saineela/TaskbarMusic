using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TaskbarMusic.Models;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Summary info for a cached song entry, used by the View Cached dialog.
    /// </summary>
    public class CacheEntryInfo
    {
        public string CacheKey { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string DurationLabel { get; set; } = string.Empty;
        public string CachedAgo { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
    }
    /// <summary>
    /// SQLite-backed cache for lyrics lookup results.
    /// Prevents redundant LRCLIB API calls.
    /// 
    /// Key: normalized(artist + "|" + title + "|" + duration)
    /// Value: full LRCLIB response JSON
    /// </summary>
    public class LyricsCache : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _dbPath;

        public LyricsCache()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "TaskbarMusic");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "lyrics_cache.sqlite");

            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // Enable WAL mode for better crash resilience
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }

            EnsureSchema();
        }

        private void EnsureSchema()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS songs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    cache_key TEXT UNIQUE NOT NULL,
                    artist TEXT NOT NULL,
                    title TEXT NOT NULL,
                    album TEXT,
                    duration REAL,
                    lrc_json TEXT NOT NULL,
                    synced_lyrics TEXT,
                    plain_lyrics TEXT,
                    score REAL,
                    provider TEXT DEFAULT 'lrclib',
                    last_checked TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_cache_key ON songs(cache_key);
            ";
            cmd.ExecuteNonQuery();

            // Migration: add 'provider' column if missing (old databases created before this column existed)
            try
            {
                using var migCmd = _connection.CreateCommand();
                migCmd.CommandText = "ALTER TABLE songs ADD COLUMN provider TEXT DEFAULT 'lrclib'";
                migCmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists — ignore
            }

            // Migration: add 'offset_seconds' column for per-song timing offset
            try
            {
                using var migCmd = _connection.CreateCommand();
                migCmd.CommandText = "ALTER TABLE songs ADD COLUMN offset_seconds REAL DEFAULT 0";
                migCmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists — ignore
            }
        }

        /// <summary>
        /// Generates a normalized cache key from metadata.
        /// </summary>
        public static string MakeCacheKey(string artist, string title, double duration = 0)
        {
            var a = StringNormalizer.Normalize(artist);
            var t = StringNormalizer.Normalize(title);
            var d = duration > 0 ? $"|{Math.Round(duration)}" : string.Empty;
            return $"{a}|{t}{d}";
        }

        /// <summary>
        /// Looks up cached lyrics for the given track.
        /// Tries exact duration match first, then falls back to fuzzy match (within ±5s).
        /// Returns null if not cached or stale (>7 days old).
        /// </summary>
        public LyricsCandidate? Get(string artist, string title, double duration = 0)
        {
            // Try exact match first
            var exactKey = MakeCacheKey(artist, title, duration);
            var result = GetByKey(exactKey);
            if (result != null)
                return result;

            // If exact match fails and we have a duration, try fuzzy match (artist+title without exact duration)
            if (duration > 0)
            {
                var fuzzyKey = MakeCacheKey(artist, title); // key without duration
                var fuzzyResult = GetByArtistTitle(fuzzyKey, artist, title, duration);
                if (fuzzyResult != null)
                    return fuzzyResult;
            }

            return null;
        }

        private LyricsCandidate? GetByKey(string key)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT lrc_json, score, last_checked
                FROM songs
                WHERE cache_key = @key
            ";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var lastChecked = DateTime.Parse(reader.GetString(2));
            if ((DateTime.UtcNow - lastChecked).TotalDays > 7)
                return null; // Stale

            var json = reader.GetString(0);
            var score = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);

            Console.WriteLine($"[LyricsCache] Hit: {key} (score={score:F0}, age={(DateTime.UtcNow - lastChecked).TotalHours:F1}h)");

            var candidate = LyricsMatcher.ParseCandidate(JsonDocument.Parse(json).RootElement);
            return candidate;
        }

        /// <summary>
        /// Fuzzy lookup: matches by artist+title prefix, validates duration is within ±5s,
        /// and re-scores the candidate against the phone's current metadata.
        /// </summary>
        private LyricsCandidate? GetByArtistTitle(string keyPrefix, string phoneArtist, string phoneTitle, double targetDuration)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT lrc_json, score, last_checked, duration
                FROM songs
                WHERE cache_key LIKE @prefix || '%'
                ORDER BY last_checked DESC
                LIMIT 3
            ";
            cmd.Parameters.AddWithValue("@prefix", keyPrefix);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var lastChecked = DateTime.Parse(reader.GetString(2));
                if ((DateTime.UtcNow - lastChecked).TotalDays > 7)
                    continue;

                var json = reader.GetString(0);
                var cachedDur = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3);

                // Only accept if duration is within ±5s of target
                if (cachedDur > 0 && targetDuration > 0)
                {
                    var diff = Math.Abs(cachedDur - targetDuration);
                    if (diff > 5.0)
                        continue;
                }

                var candidate = LyricsMatcher.ParseCandidate(JsonDocument.Parse(json).RootElement);

                // Re-score against the phone's current metadata
                LyricsMatcher.ScoreCandidate(candidate, phoneArtist, phoneTitle, candidate.AlbumName, targetDuration);

                Console.WriteLine($"[LyricsCache] Fuzzy hit: {keyPrefix} (cached dur={cachedDur:F0}s, target={targetDuration:F0}s, score={candidate.TotalScore:F0})");

                // Only return if the re-scored candidate is still acceptable
                if (candidate.IsValid)
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Stores a lyrics result in the cache.
        /// </summary>
        public void Put(string artist, string title, string? album, double duration, LyricsCandidate candidate)
        {
            var key = MakeCacheKey(artist, title, duration);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO songs
                    (cache_key, artist, title, album, duration, lrc_json, synced_lyrics, plain_lyrics, score, provider, last_checked)
                VALUES
                    (@key, @artist, @title, @album, @duration, @lrc_json, @synced, @plain, @score, @provider, @checked)
            ";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@artist", candidate.ArtistName);
            cmd.Parameters.AddWithValue("@title", candidate.TrackName);
            cmd.Parameters.AddWithValue("@album", (object?)candidate.AlbumName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@duration", candidate.Duration);
            cmd.Parameters.AddWithValue("@lrc_json", JsonSerializer.Serialize(candidate));
            cmd.Parameters.AddWithValue("@synced", (object?)candidate.SyncedLyrics ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@plain", (object?)candidate.PlainLyrics ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@score", candidate.TotalScore);
            cmd.Parameters.AddWithValue("@provider", (object?)candidate.Provider ?? "lrclib");
            cmd.Parameters.AddWithValue("@checked", DateTime.UtcNow.ToString("o"));

            cmd.ExecuteNonQuery();
            Console.WriteLine($"[LyricsCache] Stored: {key} (score={candidate.TotalScore:F0})");
        }

        /// <summary>
        /// Gets the per-song timing offset for the given track.
        /// </summary>
        public double GetOffset(string artist, string title, double duration = 0)
        {
            var key = MakeCacheKey(artist, title, duration);

            // Try exact match first
            var offset = GetOffsetByKey(key);
            if (offset.HasValue)
                return offset.Value;

            // Fuzzy: try without duration
            if (duration > 0)
            {
                var fuzzyKey = MakeCacheKey(artist, title);
                offset = GetOffsetByArtistTitle(fuzzyKey, duration);
                if (offset.HasValue)
                    return offset.Value;
            }

            return 0;
        }

        private double? GetOffsetByKey(string key)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT offset_seconds FROM songs WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
                return Convert.ToDouble(result);
            return null;
        }

        private double? GetOffsetByArtistTitle(string keyPrefix, double targetDuration)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT offset_seconds, duration
                FROM songs
                WHERE cache_key LIKE @prefix || '%'
                ORDER BY last_checked DESC
                LIMIT 3
            ";
            cmd.Parameters.AddWithValue("@prefix", keyPrefix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cachedDur = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
                if (cachedDur > 0 && targetDuration > 0)
                {
                    var diff = Math.Abs(cachedDur - targetDuration);
                    if (diff > 5.0) continue;
                }
                if (!reader.IsDBNull(0))
                    return reader.GetDouble(0);
            }
            return null;
        }

        /// <summary>
        /// Stores the per-song timing offset. Only updates if a cache entry exists.
        /// </summary>
        public void SetOffset(string artist, string title, double duration, double offset)
        {
            var key = MakeCacheKey(artist, title, duration);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE songs SET offset_seconds = @offset WHERE cache_key = @key
            ";
            cmd.Parameters.AddWithValue("@offset", offset);
            cmd.Parameters.AddWithValue("@key", key);
            var rows = cmd.ExecuteNonQuery();
            Console.WriteLine($"[LyricsCache] Offset set: {key} → {offset:F2}s (rows={rows})");
        }

        /// <summary>
        /// Returns the total number of cached songs and newest/oldest entry ages.
        /// </summary>
        public (int count, string newest, string oldest) GetStats()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    COUNT(*),
                    COALESCE(MAX(last_checked), ''),
                    COALESCE(MIN(last_checked), '')
                FROM songs
            ";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var count = reader.GetInt32(0);
                var newest = reader.IsDBNull(1) || string.IsNullOrEmpty(reader.GetString(1)) ? "—" :
                    $"{(DateTime.UtcNow - DateTime.Parse(reader.GetString(1))).TotalHours:F0}h ago";
                var oldest = reader.IsDBNull(2) || string.IsNullOrEmpty(reader.GetString(2)) ? "—" :
                    $"{(DateTime.UtcNow - DateTime.Parse(reader.GetString(2))).TotalDays:F0}d ago";
                return (count, newest, oldest);
            }
            return (0, "—", "—");
        }

        /// <summary>
        /// Returns all cached entries ordered by most recently checked first.
        /// </summary>
        public List<CacheEntryInfo> GetAllEntries()
        {
            var entries = new List<CacheEntryInfo>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT cache_key, artist, title, duration, last_checked
                FROM songs
                ORDER BY last_checked DESC
                LIMIT 200
            ";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var lastChecked = DateTime.Parse(reader.GetString(4));
                var dur = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3);

                entries.Add(new CacheEntryInfo
                {
                    CacheKey = reader.GetString(0),
                    Artist = reader.GetString(1),
                    Title = reader.GetString(2),
                    DurationSeconds = dur,
                    DurationLabel = dur > 0 ? $"{dur:F0}s" : "—",
                    CachedAgo = $"{(DateTime.UtcNow - lastChecked).TotalDays:F0}d ago"
                });
            }

            return entries;
        }

        /// <summary>
        /// Deletes a single entry by its cache key. Returns true if a row was removed.
        /// </summary>
        public bool DeleteEntry(string cacheKey)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM songs WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", cacheKey);
            var rows = cmd.ExecuteNonQuery();
            if (rows > 0)
                Console.WriteLine($"[LyricsCache] Deleted: {cacheKey}");
            return rows > 0;
        }

        /// <summary>
        /// Overwrites the synced_lyrics field for an existing cache entry.
        /// Used after the user manually tunes plain-text lyrics into proper LRC.
        /// Also updates lrc_json so the tuned lyrics become the new "original"
        /// returned from cache on subsequent lookups.
        /// </summary>
        public bool UpdateSyncedLyrics(string artist, string title, double duration, string newLrcContent)
        {
            var key = MakeCacheKey(artist, title, duration);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE songs
                SET synced_lyrics = @lrc, provider = 'tuned', last_checked = @checked
                WHERE cache_key = @key
            ";
            cmd.Parameters.AddWithValue("@lrc", newLrcContent);
            cmd.Parameters.AddWithValue("@checked", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@key", key);
            var rows = cmd.ExecuteNonQuery();

            // Also update lrc_json so the tuned lyrics are returned as the original from cache
            if (rows > 0)
            {
                try
                {
                    using var readCmd = _connection.CreateCommand();
                    readCmd.CommandText = "SELECT lrc_json FROM songs WHERE cache_key = @key";
                    readCmd.Parameters.AddWithValue("@key", key);
                    var existingJson = readCmd.ExecuteScalar() as string;

                    if (!string.IsNullOrWhiteSpace(existingJson))
                    {
                        var candidate = JsonSerializer.Deserialize<LyricsCandidate>(existingJson);
                        if (candidate != null)
                        {
                            candidate.SyncedLyrics = newLrcContent;
                            candidate.Provider = "tuned";
                            var updatedJson = JsonSerializer.Serialize(candidate);

                            using var updateCmd = _connection.CreateCommand();
                            updateCmd.CommandText = "UPDATE songs SET lrc_json = @json WHERE cache_key = @key";
                            updateCmd.Parameters.AddWithValue("@json", updatedJson);
                            updateCmd.Parameters.AddWithValue("@key", key);
                            updateCmd.ExecuteNonQuery();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LyricsCache] Failed to update lrc_json for tuned lyrics: {ex.Message}");
                }
            }

            Console.WriteLine($"[LyricsCache] Updated synced lyrics: {key} (rows={rows})");
            return rows > 0;
        }

        /// <summary>
        /// Clears stale entries older than the given age.
        /// </summary>
        public void PruneStale(TimeSpan maxAge)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM songs
                WHERE last_checked < @cutoff
            ";
            cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(maxAge).ToString("o"));
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                Console.WriteLine($"[LyricsCache] Pruned {deleted} stale entries");
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}
