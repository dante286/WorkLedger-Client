using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace WorkLedger_Client.Services
{
    public class AppSettings
    {
        public string Theme { get; set; } = "System"; // "System", "Light", "Dark"
        public string SqliteFilePath { get; set; } = string.Empty;
    }

    public class SettingsService
    {
        private readonly string _settingsPath;

        public SettingsService()
        {
            // Choose a cross-platform settings directory with platform-specific preferences:
            string appData;

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                appData = Path.Combine(home, "Library", "Application Support");
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                // Prefer XDG_CONFIG_HOME, otherwise ~/.config
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (!string.IsNullOrWhiteSpace(xdg))
                    appData = xdg;
                else
                    appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
            }
            else
            {
                // Fallback to LocalApplicationData or Personal
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(appData))
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            }

            var dir = Path.Combine(appData, "WorkLedger");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _settingsPath = Path.Combine(dir, "settings.json");
        }

        // Expose the settings path and directory so other services can place related files next to settings.json.
        public string SettingsPath => _settingsPath;

        public string SettingsDirectory => Path.GetDirectoryName(_settingsPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    var defaults = new AppSettings();
                    SaveSettings(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings ?? new AppSettings();
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }

        public void SaveSettings(AppSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
