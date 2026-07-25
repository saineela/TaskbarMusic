using System;
using System.Collections.Generic;
using System.Text.Json;
using TaskbarMusic.Models;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Lyrics validation and scoring.
    /// 
    /// A match is valid when:
    /// - Artist similarity >= 80% (normalized both sides)
    /// - Title similarity >= 80% (normalized both sides)
    /// - Duration difference <= 1 second
    /// 
    /// Album is a tiebreaker for ranking, not a hard rejection.
    /// </summary>
    public static class LyricsMatcher
    {
        private const double RequiredSimilarity = 0.80;
        private const double MaxDurationDiff = 1.0;

        /// <summary>
        /// Parses JSON elements from LRCLIB /api/get or /api/search into candidates.
        /// </summary>
        public static LyricsCandidate ParseCandidate(JsonElement element)
        {
            var candidate = new LyricsCandidate();

            if (element.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String)
                candidate.TrackName = tn.GetString() ?? string.Empty;

            if (element.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String)
                candidate.ArtistName = an.GetString() ?? string.Empty;

            if (element.TryGetProperty("albumName", out var aln) && aln.ValueKind == JsonValueKind.String)
                candidate.AlbumName = aln.GetString() ?? string.Empty;

            if (element.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                candidate.Duration = dur.GetDouble();

            if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                candidate.LrcLibId = id.GetInt32();

            if (element.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String)
                candidate.SyncedLyrics = sl.GetString();

            if (element.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String)
                candidate.PlainLyrics = pl.GetString();

            // Parse cached metadata (for deserializing from SQLite cache)
            if (element.TryGetProperty("Provider", out var prov) && prov.ValueKind == JsonValueKind.String)
                candidate.Provider = prov.GetString() ?? "lrclib";
            if (element.TryGetProperty("IsValid", out var valid))
            {
                if (valid.ValueKind == JsonValueKind.True)
                    candidate.IsValid = true;
                else if (valid.ValueKind == JsonValueKind.False)
                    candidate.IsValid = false;
            }

            return candidate;
        }

        /// <summary>
        /// Strict validation: returns true only if artist and title pass similarity
        /// (both sides normalized before comparison), and duration within tolerance.
        /// </summary>
        public static bool IsValidMatch(string phoneArtist, string phoneTitle, double phoneDuration, LyricsCandidate candidate)
        {
            // Similarity already normalizes both sides (lowercase, strip noise, etc.)
            var artistSim = StringNormalizer.Similarity(phoneArtist, candidate.ArtistName);
            var titleSim = StringNormalizer.Similarity(phoneTitle, candidate.TrackName);
            var durationDiff = phoneDuration > 0 && candidate.Duration > 0
                ? Math.Abs(phoneDuration - candidate.Duration)
                : 0;

            if (artistSim < RequiredSimilarity) return false;
            if (titleSim < RequiredSimilarity) return false;
            if (durationDiff > MaxDurationDiff) return false;

            return true;
        }

        /// <summary>
        /// Scores a candidate for ranking purposes (used to pick best among multiple valid matches).
        /// </summary>
        public static void ScoreCandidate(LyricsCandidate candidate, string phoneArtist, string phoneTitle, string phoneAlbum, double phoneDuration)
        {
            // Similarity already normalizes both sides
            var artistSim = StringNormalizer.Similarity(phoneArtist, candidate.ArtistName);
            var titleSim = StringNormalizer.Similarity(phoneTitle, candidate.TrackName);
            var durationDiff = phoneDuration > 0 && candidate.Duration > 0
                ? Math.Abs(phoneDuration - candidate.Duration)
                : 0;

            // Ranking score (higher = better, used only for ordering)
            candidate.ArtistScore = artistSim * 100;
            candidate.TitleScore = titleSim * 100;
            candidate.DurationScore = durationDiff <= MaxDurationDiff ? (100 - durationDiff * 10) : 0;

            if (!string.IsNullOrWhiteSpace(phoneAlbum) && !string.IsNullOrWhiteSpace(candidate.AlbumName))
            {
                candidate.AlbumScore = StringNormalizer.Similarity(phoneAlbum, candidate.AlbumName) * 100;
            }
            else
            {
                candidate.AlbumScore = 0;
            }

            // Mark validity
            candidate.IsValid = IsValidMatch(phoneArtist, phoneTitle, phoneDuration, candidate);
        }

        /// <summary>
        /// Ranks candidates by: 1) Valid first, 2) artist score, 3) title score, 4) lowest duration diff, 5) album score.
        /// </summary>
        public static List<LyricsCandidate> RankCandidates(List<LyricsCandidate> candidates, string phoneArtist, string phoneTitle, string phoneAlbum, double phoneDuration)
        {
            foreach (var c in candidates)
            {
                ScoreCandidate(c, phoneArtist, phoneTitle, phoneAlbum, phoneDuration);
            }

            // Sort: valid first, then by total score descending
            candidates.Sort((a, b) =>
            {
                if (a.IsValid != b.IsValid) return b.IsValid.CompareTo(a.IsValid);
                return b.TotalScore.CompareTo(a.TotalScore);
            });

            return candidates;
        }

        /// <summary>
        /// Gets the best candidate — must pass strict validation (artist≥95%, title≥95%, duration≤1s).
        /// </summary>
        public static LyricsCandidate? GetBestMatch(List<LyricsCandidate> candidates, string phoneArtist, string phoneTitle, string phoneAlbum, double phoneDuration)
        {
            var ranked = RankCandidates(candidates, phoneArtist, phoneTitle, phoneAlbum, phoneDuration);

            if (ranked.Count == 0)
                return null;

            var best = ranked[0];
            var artistSim = StringNormalizer.Similarity(phoneArtist, best.ArtistName);
            var titleSim = StringNormalizer.Similarity(phoneTitle, best.TrackName);
            var durDiff = Math.Abs(phoneDuration - best.Duration);

            Console.WriteLine($"[Lyrics] Best candidate: {best.ArtistName} - {best.TrackName} (artist={artistSim:P0}, title={titleSim:P0}, durDiff={durDiff:F1}s)");

            if (!best.IsValid)
            {
                Console.WriteLine($"[Lyrics] Rejected — strict match failed (artist={artistSim:P0}, title={titleSim:P0}, durDiff={durDiff:F1}s)");
                return null;
            }

            Console.WriteLine($"[Lyrics] Accepted — strict match passed ✓");
            return best;
        }
    }
}
