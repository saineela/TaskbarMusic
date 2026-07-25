using System.Text.Json.Serialization;

namespace TaskbarMusic.Models
{
    /// <summary>
    /// Represents a WebSocket message received from the Android phone relay.
    /// 
    /// Track: {"type":"track","artist":"...","title":"...","album":"...","duration":240}
    /// Position: {"type":"position","position":125,"playing":true}
    /// Heartbeat: {"type":"heartbeat"}
    /// LRC request: {"type":"lrc_request","song":"Artist - Title"}
    /// LRC upload: {"type":"lrc_upload","song":"Artist - Title","content":"[00:12.00]..."}
    /// LRC response: {"type":"lrc_response","song":"Artist - Title","content":"[00:12.00]...","error":null}
    /// 
    /// All position/duration values are in SECONDS.
    /// </summary>
    public class WebSocketMessage
    {
        /// <summary>
        /// Message type: "track", "position", "heartbeat", "lrc_request", "lrc_upload", "lrc_response"
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        // Track metadata (for "track" type)
        [JsonPropertyName("artist")]
        public string? Artist { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("album")]
        public string? Album { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        // Position (for "position" type) — in seconds
        [JsonPropertyName("position")]
        public double? Position { get; set; }

        // Playback state (for "position" type)
        [JsonPropertyName("playing")]
        public bool? Playing { get; set; }

        // Role (for "connected" type)
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        // Schedule (for "schedule" type — Windows → Android)
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("at")]
        public long? At { get; set; }

        // ── LRC cache fields (lrc_request / lrc_upload / lrc_response) ──

        /// <summary>"Artist - Title" cache key for LRC operations.</summary>
        [JsonPropertyName("song")]
        public string? Song { get; set; }

        /// <summary>Raw LRC content (lrc_upload / lrc_response).</summary>
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        /// <summary>Error string for lrc_response (e.g. "not_found").</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
