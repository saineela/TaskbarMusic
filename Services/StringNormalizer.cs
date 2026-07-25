using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Normalizes song metadata strings for fuzzy matching.
    /// Removes common suffixes/prefixes and cleans for comparison.
    /// </summary>
    public static class StringNormalizer
    {
        // Words/phrases to remove from metadata
        private static readonly string[] NoiseWords = new[]
        {
            "official", "audio", "video", "lyrics", "remastered", "remaster",
            "live", "version", "remix", "deluxe", "edition", "explicit",
            "clean", "radio", "single", "album", "track", "feat\\.", "ft\\.",
            "featuring", "with", "prod\\.", "produced by"
        };

        // Regex for parenthesized content like "(Official Audio)", "[Remastered 2025]"
        private static readonly Regex ParenRegex = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
        private static readonly Regex DashRegex = new(@"\s*[-–—]\s*", RegexOptions.Compiled);
        private static readonly Regex MultipleSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex NonAlphaNumericRegex = new(@"[^\w\s]", RegexOptions.Compiled);

        /// <summary>
        /// Normalizes a string for comparison: lowercase, remove noise, trim.
        /// </summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var result = input.ToLowerInvariant().Trim();

            // Remove parenthesized/bracketed content like "(Official Audio)", "[Remastered]"
            result = ParenRegex.Replace(result, "");

            // Remove content after a dash (often suffixes like "- Remastered 2025")
            // But only if it looks like a suffix, not part of the title
            var dashParts = DashRegex.Split(result);
            if (dashParts.Length > 1)
            {
                // Keep only the part before the first dash if it's substantial
                var mainPart = dashParts[0].Trim();
                if (mainPart.Length >= 3)
                    result = mainPart;
            }

            // Remove noise words
            foreach (var word in NoiseWords)
            {
                result = Regex.Replace(result, @"\b" + word + @"\b", " ");
            }

            // Remove non-alphanumeric characters (keep spaces)
            result = NonAlphaNumericRegex.Replace(result, " ");

            // Collapse multiple spaces
            result = MultipleSpaceRegex.Replace(result, " ").Trim();

            return result;
        }

        /// <summary>
        /// Cleans metadata text for lyrics lookup queries.
        /// Uses tag-based regex approach (matches JustAnotherMusicClient's cleanLyricsLookupText):
        /// 1. Removes brackets containing noise tags: [Official Video], [Lyrics], etc.
        /// 2. Removes parentheses containing noise tags: (feat. Artist), (Remastered), etc.
        /// 3. Strips dash suffixes with noise content: " - Official Video", " - Remastered 2025"
        /// 4. Collapses whitespace.
        /// </summary>
        public static string CleanLookupText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Build a tag regex from all noise words: official|video|audio|lyrics|remaster|feat|live|...
            var tagWords = new[]
            {
                "official", "video", "audio", "lyrics", "remaster", "remix",
                "feat", "ft", "featuring", "live", "visualizer", "acoustic",
                "radio edit", "deluxe", "explicit", "clean version", "album version",
                "single", "edit", "hd", "hq", "4k", "performance"
            };
            var tagRegex = string.Join("|", tagWords.Select(Regex.Escape));

            var result = input;

            // 1. Remove bracketed content with noise tags: [Official Video], [Lyrics], [Remastered 2025]
            result = Regex.Replace(result,
                $@"\[[^\]]*?(?:{tagRegex})[^\]]*?\]", " ", RegexOptions.IgnoreCase);

            // 2. Remove parenthesized content with noise tags: (Official Audio), (feat. Artist), (Live)
            result = Regex.Replace(result,
                $@"\([^)]*?(?:{tagRegex})[^)]*?\)", " ", RegexOptions.IgnoreCase);

            // 3. Strip dash suffixes containing noise: " - Official Video", " - Remastered 2025"
            result = Regex.Replace(result,
                $@"\s*[-–—]\s*[^-\n]*?(?:{tagRegex})[^-\n]*\s*$", "", RegexOptions.IgnoreCase);

            // 4. Collapse multiple spaces
            result = MultipleSpaceRegex.Replace(result, " ").Trim();

            return result;
        }

        /// <summary>
        /// Computes similarity between two strings (0.0 to 1.0).
        /// Uses normalized Levenshtein-style matching.
        /// </summary>
        public static double Similarity(string a, string b)
        {
            var na = Normalize(a);
            var nb = Normalize(b);

            if (string.IsNullOrEmpty(na) && string.IsNullOrEmpty(nb))
                return 1.0;
            if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(nb))
                return 0.0;

            // Exact match after normalization
            if (na == nb)
                return 1.0;

            // One contains the other
            if (na.Contains(nb) || nb.Contains(na))
            {
                var shorter = na.Length < nb.Length ? na : nb;
                var longer = na.Length < nb.Length ? nb : na;
                return (double)shorter.Length / longer.Length;
            }

            // Levenshtein similarity
            var maxLen = Math.Max(na.Length, nb.Length);
            var distance = LevenshteinDistance(na, nb);
            return 1.0 - (double)distance / maxLen;
        }

        /// <summary>
        /// Computes Levenshtein edit distance between two strings.
        /// </summary>
        private static int LevenshteinDistance(string s, string t)
        {
            var n = s.Length;
            var m = t.Length;
            var d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost
                    );
                }
            }

            return d[n, m];
        }
    }
}
