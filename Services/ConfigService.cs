using System;
using System.IO;
using System.Text.Json;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Window position data for serialization.
    /// </summary>
    public class WindowPosition
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public bool IsLocked { get; set; }
        /// <summary>Device token for WebSocket relay (NOT a full URL — DNS resolves the IP at connect time).</summary>
        public string DeviceToken { get; set; } = string.Empty;
        /// <summary>URL for self-hosted spotify-lyrics-api (default: http://localhost:8080).</summary>
        public string SpotifyLyricsApiUrl { get; set; } = "http://localhost:8080";
        /// <summary>Whether transliteration is enabled (persisted across restarts).</summary>
        public bool TransliterationEnabled { get; set; } = false;
        /// <summary>The selected transliteration target as a string (persisted across restarts).</summary>
        public string TransliterationTarget { get; set; } = "Latin";
        /// <summary>Custom WebSocket URL override (if empty, uses default relay + device token).</summary>
        public string CustomWebSocketUrl { get; set; } = string.Empty;
        /// <summary>When true, uses local Windows SMTC for media detection instead of phone relay.</summary>
        public bool ThisDeviceMode { get; set; } = false;
    }

    /// <summary>
    /// Saves and loads window position to a JSON config file.
    /// </summary>
    public static class ConfigService
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarMusic"
        );

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        /// <summary>
        /// Loads the saved window position, or returns defaults if no config exists.
        /// Migrates old WebSocketUrl field to DeviceToken if found.
        /// </summary>
        public static WindowPosition Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<WindowPosition>(json) ?? GetDefaults();

                    // Migrate from old WebSocketUrl field if present and DeviceToken is empty
                    if (string.IsNullOrEmpty(config.DeviceToken))
                    {
                        // Try to extract token from old WebSocketUrl format
                        var oldUrl = ExtractOldWebSocketUrl(json);
                        if (!string.IsNullOrEmpty(oldUrl))
                        {
                            config.DeviceToken = ExtractTokenFromUrl(oldUrl);
                            Console.WriteLine($"[Config] Migrated old URL to token: {GetTokenPreview(config.DeviceToken)}");
                            var migratedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(ConfigPath, migratedJson);
                        }
                        else if (string.IsNullOrEmpty(config.DeviceToken))
                        {
                            config.DeviceToken = GetDefaults().DeviceToken;
                            var migratedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(ConfigPath, migratedJson);
                        }
                    }

                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Load error: {ex.Message}");
            }

            return GetDefaults();
        }

        private static string? ExtractOldWebSocketUrl(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("WebSocketUrl", out var wsProp) &&
                    wsProp.ValueKind == JsonValueKind.String)
                {
                    var val = wsProp.GetString();
                    if (!string.IsNullOrEmpty(val) && val.Contains("token="))
                        return val;
                }
            }
            catch { }
            return null;
        }

        private static string ExtractTokenFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var query = uri.Query;
                var tokenIdx = query.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
                if (tokenIdx >= 0)
                {
                    var token = query.Substring(tokenIdx + 6);
                    var ampIdx = token.IndexOf('&');
                    if (ampIdx >= 0) token = token.Substring(0, ampIdx);
                    return token;
                }
            }
            catch { }
            return GetDefaults().DeviceToken;
        }

        /// <summary>
        /// Saves the current window position to disk.
        /// </summary>
        public static void Save(double left, double top, bool isLocked, string? deviceToken = null)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);

                var existing = Load();

                var position = new WindowPosition
                {
                    Left = left,
                    Top = top,
                    IsLocked = isLocked,
                    DeviceToken = deviceToken ?? existing.DeviceToken
                };

                var json = JsonSerializer.Serialize(position, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                Console.WriteLine($"[Config] Saved: Left={left}, Top={top}, Locked={isLocked}, Token={GetTokenPreview(position.DeviceToken)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Save error: {ex.Message}");
            }
        }

        public static void SaveDeviceToken(string deviceToken)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var existing = Load();
                existing.DeviceToken = deviceToken;
                var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                Console.WriteLine($"[Config] Saved token: {GetTokenPreview(deviceToken)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Save token error: {ex.Message}");
            }
        }

        private static WindowPosition GetDefaults()
        {
            return new WindowPosition
            {
                Left = 800,
                Top = 1140,
                IsLocked = false,
                DeviceToken = string.Empty,
                TransliterationEnabled = false,
                TransliterationTarget = "Latin",
                ThisDeviceMode = false
            };
        }

        /// <summary>
        /// Saves the transliteration state (enabled flag + target language) to the config file.
        /// </summary>
        public static void SaveTransliterationState(bool enabled, string target)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var existing = Load();
                existing.TransliterationEnabled = enabled;
                existing.TransliterationTarget = target;
                var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                Console.WriteLine($"[Config] Saved transliteration state: enabled={enabled}, target={target}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Save transliteration state error: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves a custom WebSocket URL override to the config file.
        /// An empty string clears the override (uses default relay).
        /// </summary>
        public static void SaveCustomWebSocketUrl(string url)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var existing = Load();
                existing.CustomWebSocketUrl = url;
                var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                Console.WriteLine($"[Config] Saved custom WebSocket URL: {(string.IsNullOrEmpty(url) ? "(cleared)" : url)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Save custom WebSocket URL error: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the This Device Mode toggle state to the config file.
        /// </summary>
        public static void SaveThisDeviceMode(bool enabled)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var existing = Load();
                existing.ThisDeviceMode = enabled;
                var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                Console.WriteLine($"[Config] Saved ThisDeviceMode: {enabled}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Save ThisDeviceMode error: {ex.Message}");
            }
        }

        public static string GetTokenPreview(string? token)
        {
            if (string.IsNullOrEmpty(token)) return "(empty)";
            if (token.Length > 8)
                return token.Substring(0, 8) + "..." + token.Substring(token.Length - 8);
            return token;
        }
    }
}
