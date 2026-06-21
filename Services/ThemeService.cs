using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using System;
using System.Linq;

namespace WorkLedger_Client.Services
{
    public class ThemeService
    {
        private readonly Application _app;
        private bool _darkMode;

        public ThemeService(IClassicDesktopStyleApplicationLifetime appLifetime)
        {
            _app = Application.Current ?? throw new InvalidOperationException("No Avalonia Application instance.");

            // Detect system theme on startup
            DetectAndApplySystemTheme();
        }

        public ThemeService(IApplicationLifetime applicationLifetime)
        {
            _app = Application.Current ?? throw new InvalidOperationException("No Avalonia Application instance.");

            // Detect system theme on startup
            DetectAndApplySystemTheme();
        }

        private void DetectAndApplySystemTheme()
        {
            // Get system theme variant from Avalonia
            var themeVariant = _app.ActualThemeVariant;
            _darkMode = themeVariant == ThemeVariant.Dark;

            // Apply the detected theme
            SetTheme(_darkMode ? "Dark" : "Light");
        }

        public bool DarkMode
        {
            get => _darkMode;
            set
            {
                if (_darkMode != value)
                {
                    _darkMode = value;
                    SetTheme(_darkMode ? "Dark" : "Light");
                }
            }
        }

        /// <summary>
        /// Toggles between dark and light themes
        /// </summary>
        public void ToggleTheme()
        {
            DarkMode = !DarkMode;
        }

        /// <summary>
        /// Sets the theme to either "Dark" or "Light"
        /// </summary>
        public void SetTheme(string themeName)
        {
            // Ensure UI operations execute on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => SetTheme(themeName));
                return;
            }

            var isDark = themeName.Equals("Dark", StringComparison.OrdinalIgnoreCase);
            _darkMode = isDark;

            var themeUri = isDark
                ? "avares://WorkLedger-Client/Assets/themes/DarkTheme.xaml"
                : "avares://WorkLedger-Client/Assets/themes/LightTheme.xaml";

            try
            {
                var theme = new ResourceInclude(new Uri("avares://WorkLedger-Client"))
                {
                    Source = new Uri(themeUri)
                };

                // Remove existing theme resources from MergedDictionaries
                var mergedDictionaries = _app.Resources.MergedDictionaries;
                var existingThemes = mergedDictionaries
                    .OfType<ResourceInclude>()
                    .Where(ri => ri.Source != null && ri.Source.ToString().Contains("/Assets/themes/"))
                    .ToList();

                foreach (var existing in existingThemes)
                {
                    mergedDictionaries.Remove(existing);
                }

                // Add the new theme
                mergedDictionaries.Add(theme);

                // Also update the theme variant
                _app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting theme: {ex.Message}");
            }
        }
    }
}