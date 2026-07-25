using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Service that calls the Aksharamukha Python library for Indic→Indic script transliteration.
    /// Launches Python as a subprocess, sends JSON over stdin, reads JSON from stdout.
    /// </summary>
    public static class AksharamukhaService
    {
        private static string? _bridgePath;
        private static bool _checked = false;
        private static bool _available = false;

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarMusic", "akshara_debug.log");

        private static void Log(string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [Aksharamukha] {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>
        /// Checks whether the Python bridge script and aksharamukha are available.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_checked)
                    CheckAvailability();
                return _available;
            }
        }

        /// <summary>
        /// Extracts the embedded transliterate_bridge.py to a temp directory.
        /// Used when running from a single-file publish where no physical file exists.
        /// </summary>
        private static string? ExtractEmbeddedScript()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();

                // Find the embedded resource by scanning all resource names
                string? resourceName = null;
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("transliterate_bridge.py"))
                    {
                        resourceName = name;
                        break;
                    }
                }

                if (resourceName == null)
                {
                    Log("Embedded bridge script not found in assembly resources");
                    return null;
                }

                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    Log("Embedded bridge script stream is null");
                    return null;
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "TaskbarMusic");
                Directory.CreateDirectory(tempDir);
                var tempPath = Path.Combine(tempDir, "transliterate_bridge.py");

                using (var fileStream = File.Create(tempPath))
                {
                    stream.CopyTo(fileStream);
                }

                Log($"Extracted embedded bridge script to: {tempPath}");
                return tempPath;
            }
            catch (Exception ex)
            {
                Log($"ExtractEmbeddedScript exception: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void CheckAvailability()
        {
            _checked = true;
            try
            {
                // Look for the bridge script next to the executable
                var asmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var dir = Path.GetDirectoryName(asmPath);
                if (string.IsNullOrEmpty(dir))
                    dir = Environment.CurrentDirectory;

                Log($"Assembly path: {asmPath}");

                // Try common locations
                var candidates = new[]
                {
                    Path.Combine(dir, "transliterate_bridge.py"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transliterate_bridge.py"),
                    Path.Combine(Environment.CurrentDirectory, "transliterate_bridge.py"),
                    // Also check project root (relative from bin/Debug)
                    Path.Combine(dir, "..", "..", "..", "transliterate_bridge.py"),
                    Path.Combine(dir, "..", "..", "..", "..", "transliterate_bridge.py"),
                };

                foreach (var candidate in candidates)
                {
                    var full = Path.GetFullPath(candidate);
                    Log($"Checking candidate: {full} (exists: {File.Exists(full)})");
                    if (File.Exists(full))
                    {
                        _bridgePath = full;
                        Log($"Found bridge at: {full}");
                        break;
                    }
                }

                // If not found on disk, try extracting from embedded resources
                if (_bridgePath == null)
                {
                    _bridgePath = ExtractEmbeddedScript();
                }

                if (_bridgePath == null)
                {
                    Log("Bridge script not found in any candidate path or embedded resources");
                    Debug.WriteLine("[Aksharamukha] Bridge script not found");
                    return;
                }

                // Quick test: check if Python can import aksharamukha
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"from aksharamukha import transliterate; print('OK')\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Log("Process.Start returned null — Python not found?");
                    return;
                }

                var output = proc.StandardOutput.ReadToEnd().Trim();
                var stderr = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit(3000);

                Log($"Python test exited={proc.ExitCode}, stdout='{output}', stderr='{stderr}'");

                _available = output == "OK";
                Log($"Aksharamukha available: {_available}");
            }
            catch (Exception ex)
            {
                Log($"CheckAvailability exception: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[Aksharamukha] Check failed: {ex.Message}");
                _available = false;
            }
        }

        /// <summary>
        /// Map from TransliterationTarget enum to Aksharamukha script name.
        /// </summary>
        public static string TargetToScriptName(TransliterationService.TransliterationTarget target) => target switch
        {
            TransliterationService.TransliterationTarget.Devanagari => "Devanagari",
            TransliterationService.TransliterationTarget.Telugu => "Telugu",
            TransliterationService.TransliterationTarget.Tamil => "Tamil",
            TransliterationService.TransliterationTarget.Malayalam => "Malayalam",
            TransliterationService.TransliterationTarget.Kannada => "Kannada",
            TransliterationService.TransliterationTarget.Bengali => "Bengali",
            TransliterationService.TransliterationTarget.Gujarati => "Gujarati",
            TransliterationService.TransliterationTarget.Gurmukhi => "Gurmukhi",
            TransliterationService.TransliterationTarget.Odia => "Odia",
            TransliterationService.TransliterationTarget.Sinhala => "Sinhala",
            TransliterationService.TransliterationTarget.Thai => "Thai",
            TransliterationService.TransliterationTarget.Myanmar => "Myanmar",
            TransliterationService.TransliterationTarget.Khmer => "Khmer",
            TransliterationService.TransliterationTarget.Lao => "Lao",
            _ => "Latin",
        };

        /// <summary>
        /// Map from DetectedLang enum to Aksharamukha script name.
        /// </summary>
        public static string DetectedLangToScriptName(TransliterationService.DetectedLang lang) => lang switch
        {
            TransliterationService.DetectedLang.Hindi or
            TransliterationService.DetectedLang.Marathi or
            TransliterationService.DetectedLang.Nepali => "Devanagari",
            TransliterationService.DetectedLang.Telugu => "Telugu",
            TransliterationService.DetectedLang.Tamil => "Tamil",
            TransliterationService.DetectedLang.Malayalam => "Malayalam",
            TransliterationService.DetectedLang.Kannada => "Kannada",
            TransliterationService.DetectedLang.Bengali => "Bengali",
            TransliterationService.DetectedLang.Gujarati => "Gujarati",
            TransliterationService.DetectedLang.Gurmukhi => "Gurmukhi",
            TransliterationService.DetectedLang.Odia => "Odia",
            TransliterationService.DetectedLang.Sinhala => "Sinhala",
            TransliterationService.DetectedLang.Thai => "Thai",
            TransliterationService.DetectedLang.Lao => "Lao",
            TransliterationService.DetectedLang.Tibetan => "Tibetan",
            TransliterationService.DetectedLang.Myanmar => "Myanmar",
            TransliterationService.DetectedLang.Khmer => "Khmer",
            TransliterationService.DetectedLang.Chinese => "Chinese",
            TransliterationService.DetectedLang.Japanese => "Japanese",
            TransliterationService.DetectedLang.Korean => "Korean",
            TransliterationService.DetectedLang.Latin => "ISO",
            _ => "ISO",
        };

        /// <summary>
        /// Batch-transliterate an array of texts from one Indic script to another.
        /// </summary>
        public static string[] BatchTransliterate(
            string[] texts,
            string sourceScript,
            string targetScript)
        {
            if (texts == null || texts.Length == 0)
                return Array.Empty<string>();

            if (!IsAvailable)
            {
                Log("BatchTransliterate: IsAvailable is false, returning texts unchanged");
                Debug.WriteLine("[Aksharamukha] Not available, returning texts unchanged");
                return texts;
            }

            Log($"BatchTransliterate: source={sourceScript}, target={targetScript}, texts.Count={texts.Length}, firstText='{Truncate(texts[0], 50)}'");

            try
            {
                var request = new Dictionary<string, object>
                {
                    ["source"] = sourceScript,
                    ["target"] = targetScript,
                    ["texts"] = texts,
                };

                var jsonRequest = JsonSerializer.Serialize(request);

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{_bridgePath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Log("BatchTransliterate: Process.Start returned null");
                    return texts;
                }

                // Write JSON to stdin
                proc.StandardInput.Write(jsonRequest);
                proc.StandardInput.Close();

                // Read JSON from stdout
                var jsonResponse = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();

                proc.WaitForExit(5000);

                Log($"BatchTransliterate: exit={proc.ExitCode}, stdoutLen={jsonResponse?.Length ?? 0}, stderr='{Truncate(stderr, 100)}'");

                if (proc.ExitCode != 0)
                {
                    Log($"BatchTransliterate: Process exited with code {proc.ExitCode}: {stderr}");
                    Debug.WriteLine($"[Aksharamukha] Process exited with code {proc.ExitCode}: {stderr}");
                    return texts;
                }

                if (string.IsNullOrWhiteSpace(jsonResponse))
                {
                    Log("BatchTransliterate: Empty response from stdout");
                    return texts;
                }

                var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonResponse);
                if (result == null || !result.TryGetValue("results", out var resultsElement))
                {
                    Log($"BatchTransliterate: Failed to parse response: '{Truncate(jsonResponse, 200)}'");
                    return texts;
                }

                var results = new List<string>();
                int idx = 0;
                foreach (var item in resultsElement.EnumerateArray())
                {
                    var val = item.GetString() ?? "";
                    // If Aksharamukha returned an error for this item, use original text
                    if (val.StartsWith("[Error:") && idx < texts.Length)
                    {
                        Log($"BatchTransliterate: Item {idx} returned error: {Truncate(val, 100)}");
                        val = texts[idx];
                    }
                    results.Add(val);
                    idx++;
                }

                Log($"BatchTransliterate: success, {results.Count} results, firstResult='{Truncate(results[0], 50)}'");
                return results.ToArray();
            }
            catch (Exception ex)
            {
                Log($"BatchTransliterate exception: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[Aksharamukha] Error: {ex.Message}");
                return texts;
            }
        }

        private static string Truncate(string? s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s[..maxLen] + "...";
        }
    }
}
