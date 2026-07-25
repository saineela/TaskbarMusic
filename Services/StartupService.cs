using System;
using Microsoft.Win32;

namespace TaskbarMusic.Services
{
    /// <summary>
    /// Manages the "Run on Startup" feature via the Windows registry.
    /// Stores the app path in HKCU\Software\Microsoft\Windows\CurrentVersion\Run
    /// </summary>
    public static class StartupService
    {
        private const string AppName = "TaskbarMusic";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// Checks if the app is set to run on startup.
        /// </summary>
        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(AppName) != null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Check error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Enables or disables run on startup.
        /// </summary>
        public static void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);

                if (key == null)
                {
                    Console.WriteLine("[Startup] Cannot open registry key");
                    return;
                }

                if (enable)
                {
                    // Get the path to the current executable
                    var exePath = Environment.ProcessPath ?? 
                        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? 
                        string.Empty;

                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppName, $"\"{exePath}\"");
                        Console.WriteLine($"[Startup] Enabled: {exePath}");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                    Console.WriteLine("[Startup] Disabled");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] Set error: {ex.Message}");
            }
        }
    }
}
