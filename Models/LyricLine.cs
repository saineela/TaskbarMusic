using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TaskbarMusic.Models
{
    /// <summary>
    /// Represents a single line of synchronized lyrics with a timestamp.
    /// </summary>
    public class LyricLine
    {
        /// <summary>
        /// The timestamp in seconds when this line should be displayed.
        /// </summary>
        public double TimeSeconds { get; set; }

        /// <summary>
        /// The lyric text content.
        /// </summary>
        public string Text { get; set; } = string.Empty;

        public override string ToString() => $"[{TimeSeconds:F2}] {Text}";
    }
}
