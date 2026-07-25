namespace TaskbarMusic.Models
{
    /// <summary>
    /// A candidate lyrics result with a confidence score.
    /// </summary>
    public class LyricsCandidate
    {
        /// <summary>Track name from LRCLIB</summary>
        public string TrackName { get; set; } = string.Empty;

        /// <summary>Artist name from LRCLIB</summary>
        public string ArtistName { get; set; } = string.Empty;

        /// <summary>Album name from LRCLIB</summary>
        public string AlbumName { get; set; } = string.Empty;

        /// <summary>Duration in seconds from LRCLIB</summary>
        public double Duration { get; set; }

        /// <summary>LRCLIB track ID</summary>
        public int? LrcLibId { get; set; }

        /// <summary>Raw synced lyrics (LRC format)</summary>
        public string? SyncedLyrics { get; set; }

        /// <summary>Raw plain lyrics</summary>
        public string? PlainLyrics { get; set; }

        // Score breakdown (for ranking only)
        public double ArtistScore { get; set; }
        public double TitleScore { get; set; }
        public double AlbumScore { get; set; }
        public double DurationScore { get; set; }

        /// <summary>Total ranking score</summary>
        public double TotalScore => ArtistScore + TitleScore + AlbumScore + DurationScore;

        /// <summary>Lyrics provider source ("lrclib", "betterlyrics")</summary>
        public string Provider { get; set; } = "lrclib";

        /// <summary>
        /// Whether this candidate passed strict validation:
        /// artist ≥95%, title ≥95%, duration ≤1s.
        /// Only valid candidates should be loaded.
        /// </summary>
        public bool IsValid { get; set; }
    }
}
