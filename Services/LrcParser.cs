using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using TaskbarMusic.Models;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Parses LRC format synchronized lyrics into LyricLine objects.
    /// LRC format: [MM:SS.xx] Lyric text
    /// </summary>
    public static class LrcParser
    {
        // Matches [MM:SS.xx] or [MM:SS.xxx] or [MM:SS]
        private static readonly Regex TimestampRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

        /// <summary>
        /// Parses LRC format text into a sorted list of LyricLine objects.
        /// </summary>
        /// <param name="lrcContent">The LRC format string</param>
        /// <returns>Sorted list of LyricLine objects</returns>
        public static List<LyricLine> Parse(string lrcContent)
        {
            var lyrics = new List<LyricLine>();

            if (string.IsNullOrWhiteSpace(lrcContent))
                return lyrics;

            var lines = lrcContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Skip metadata tags like [ar:Artist], [ti:Title], etc.
                if (TimestampRegex.Matches(trimmed).Count == 0)
                    continue;

                var matches = TimestampRegex.Matches(trimmed);

                // Extract text after all timestamps
                var textStartIndex = 0;
                foreach (Match match in matches)
                {
                    textStartIndex = match.Index + match.Length;
                }

                if (textStartIndex >= trimmed.Length)
                    continue;

                var text = trimmed.Substring(textStartIndex).Trim();

                // Skip empty lines
                if (string.IsNullOrEmpty(text))
                    continue;

                // Create a lyric line for each timestamp in the line
                foreach (Match match in matches)
                {
                    if (int.TryParse(match.Groups[1].Value, out int minutes) &&
                        int.TryParse(match.Groups[2].Value, out int seconds))
                    {
                        double milliseconds = 0;
                        if (match.Groups[3].Success)
                        {
                            var msString = match.Groups[3].Value.PadRight(3, '0');
                            if (double.TryParse(msString, NumberStyles.Any, CultureInfo.InvariantCulture, out double ms))
                            {
                                milliseconds = ms / 1000.0;
                            }
                        }

                        var timeSeconds = minutes * 60.0 + seconds + milliseconds;

                        lyrics.Add(new LyricLine
                        {
                            TimeSeconds = timeSeconds,
                            Text = text
                        });
                    }
                }
            }

            // Sort by timestamp
            lyrics.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));

            return lyrics;
        }

        /// <summary>
        /// Finds the index of the lyric line that should be displayed at the given time.
        /// </summary>
        /// <param name="lyrics">Sorted list of lyric lines</param>
        /// <param name="currentTimeSeconds">Current playback time in seconds</param>
        /// <returns>Index of the current line, or -1 if no lyrics</returns>
        public static int FindCurrentLineIndex(List<LyricLine> lyrics, double currentTimeSeconds)
        {
            if (lyrics == null || lyrics.Count == 0)
                return -1;

            // Binary search for the current line
            int left = 0;
            int right = lyrics.Count - 1;
            int result = -1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;

                if (lyrics[mid].TimeSeconds <= currentTimeSeconds)
                {
                    result = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            return result;
        }
    }
}
