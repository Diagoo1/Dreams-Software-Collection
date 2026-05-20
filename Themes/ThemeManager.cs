using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Dreams.Themes
{
    // ═════════════════════════════════════════════════════════════════
    // ███ THEME MANAGER CLASS
    // ═════════════════════════════════════════════════════════════════
    public static class ThemeManager
    {
        // ═════════════════════════════════════════════════════════════════
        // ███ CONSTANTS
        // ═════════════════════════════════════════════════════════════════
        #region Constants

        private const string REGISTRY_PATH = @"SOFTWARE\Dreams";
        private const string THEME_KEY = "Theme";
        private const string OPACITY_KEY = "Opacity";

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PRIVATE FIELDS
        // ═════════════════════════════════════════════════════════════════
        #region Private Fields

        private static bool _isDarkMode = false;
        private static double _currentOpacity = 1.0;

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ EVENTS
        // ═════════════════════════════════════════════════════════════════
        #region Events

        public static event Action<bool> ThemeChanged;
        public static event Action<double> OpacityChanged;

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PROPERTIES
        // ═════════════════════════════════════════════════════════════════
        #region Properties

        public static bool IsDarkMode
        {
            get => _isDarkMode;
            private set
            {
                if (_isDarkMode != value)
                {
                    _isDarkMode = value;
                    ApplyTheme(value);
                    ThemeChanged?.Invoke(value);
                    UpdateAllOpenWindows();
                }
            }
        }

        public static double CurrentOpacity
        {
            get => _currentOpacity;
            private set
            {
                if (Math.Abs(_currentOpacity - value) > 0.01)
                {
                    _currentOpacity = value;
                    ApplyOpacity(value);
                    OpacityChanged?.Invoke(value);
                }
            }
        }

        public static void SubscribeToChanges(Action<bool> onThemeChanged, Action<double> onOpacityChanged)
        {
            if (onThemeChanged != null) ThemeChanged += onThemeChanged;
            if (onOpacityChanged != null) OpacityChanged += onOpacityChanged;
        }

        public static void UnsubscribeFromChanges(Action<bool> onThemeChanged, Action<double> onOpacityChanged)
        {
            if (onThemeChanged != null) ThemeChanged -= onThemeChanged;
            if (onOpacityChanged != null) OpacityChanged -= onOpacityChanged;
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ INITIALIZATION
        // ═════════════════════════════════════════════════════════════════
        #region Initialization

        public static void Initialize()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REGISTRY_PATH))
                {
                    bool savedDarkMode = key?.GetValue(THEME_KEY)?.ToString() == "Dark";
                    _isDarkMode = savedDarkMode;
                    ApplyTheme(savedDarkMode);

                    if (key?.GetValue(OPACITY_KEY) != null)
                    {
                        double savedOpacity = Convert.ToDouble(key.GetValue(OPACITY_KEY)) / 100.0;
                        _currentOpacity = savedOpacity;
                        ApplyOpacity(savedOpacity);
                    }
                }
                Debug.WriteLine($"ThemeManager initialized: IsDarkMode={_isDarkMode}, Opacity={_currentOpacity}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ThemeManager Initialize Error: {ex.Message}");
                ApplyTheme(false);
                ApplyOpacity(1.0);
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PUBLIC METHODS - THEME
        // ═════════════════════════════════════════════════════════════════
        #region Public Methods - Theme

        public static void SetTheme(bool isDarkMode)
        {
            IsDarkMode = isDarkMode;
            SaveThemeToRegistry(isDarkMode);
        }

        public static void ToggleTheme() => SetTheme(!IsDarkMode);

        private static void SaveThemeToRegistry(bool isDarkMode)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REGISTRY_PATH))
                {
                    key?.SetValue(THEME_KEY, isDarkMode ? "Dark" : "Light");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving theme to registry: {ex.Message}");
            }
        }

        public static void ApplyThemeToWindow(Window window)
        {
            if (window == null) return;
            try
            {
                window.Resources.MergedDictionaries.Clear();
                ApplyTheme(_isDarkMode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyThemeToWindow Error: {ex.Message}");
            }
        }

        private static void UpdateAllOpenWindows()
        {
            try
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().ToList())
                {
                    var content = window.Content as FrameworkElement;
                    content?.ForceUpdateLayout();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateAllOpenWindows Error: {ex.Message}");
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PUBLIC METHODS - OPACITY
        // ═════════════════════════════════════════════════════════════════
        #region Public Methods - Opacity

        public static void SetOpacity(double opacity)
        {
            opacity = Math.Max(0.3, Math.Min(1.0, opacity));
            CurrentOpacity = opacity;
            SaveOpacityToRegistry(opacity);
        }

        public static double GetSavedOpacity()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REGISTRY_PATH))
                {
                    if (key?.GetValue(OPACITY_KEY) != null)
                    {
                        return Convert.ToDouble(key.GetValue(OPACITY_KEY)) / 100.0;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSavedOpacity Error: {ex.Message}");
            }
            return 1.0;
        }

        private static void SaveOpacityToRegistry(double opacity)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REGISTRY_PATH))
                {
                    key?.SetValue(OPACITY_KEY, (int)(opacity * 100));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving opacity to registry: {ex.Message}");
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PRIVATE METHODS - APPLY THEME
        // ═════════════════════════════════════════════════════════════════
        #region Private Methods - Apply Theme

        private static void ApplyTheme(bool isDarkMode)
        {
            try
            {
                var resources = Application.Current.Resources;
                string suffix = isDarkMode ? "Dark" : "";

                void UpdateResource(string dynamicKey, string baseKey)
                {
                    string sourceKey = string.IsNullOrEmpty(suffix) ? baseKey : $"{baseKey}{suffix}";
                    if (resources.Contains(sourceKey) && resources.Contains(dynamicKey))
                    {
                        resources[dynamicKey] = resources[sourceKey];
                    }
                }

                UpdateResource("DynamicWindowBg", "WindowBg");
                UpdateResource("DynamicSidebarBg", "SidebarBg");
                UpdateResource("DynamicCardBg", "CardBg");
                UpdateResource("DynamicHoverBg", "HoverBg");
                UpdateResource("DynamicTotalCardBg", "TotalCardBg");
                UpdateResource("DynamicMainText", "MainText");
                UpdateResource("DynamicSubText", "SubText");

                UpdateResource("DynamicAccent", "Accent");
                UpdateResource("DynamicBorderBrush", "BorderBrush");
                UpdateResource("DynamicBorder", "Border");

                UpdateResource("DynamicPurple", "Purple");
                UpdateResource("DynamicPink", "Pink");
                UpdateResource("DynamicIndigo", "Indigo");
                UpdateResource("DynamicTeal", "Teal");
                UpdateResource("DynamicViolet", "Violet");
                UpdateResource("DynamicFuchsia", "Fuchsia");
                UpdateResource("DynamicBlueLight", "BlueLight");
                UpdateResource("DynamicBlueMedium", "BlueMedium");
                UpdateResource("DynamicBlueViolet", "BlueViolet");
                UpdateResource("DynamicPurpleLight", "PurpleLight");

                Debug.WriteLine($"Theme applied: {(isDarkMode ? "Dark" : "Light")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyTheme Error: {ex.Message}");
            }
        }

        private static void ApplyOpacity(double opacity)
        {
            try
            {
                if (Application.Current?.MainWindow != null)
                {
                    Application.Current.MainWindow.Opacity = opacity;
                }

                foreach (Window window in Application.Current.Windows.Cast<Window>().ToList())
                {
                    if (window != Application.Current.MainWindow)
                    {
                        window.Opacity = opacity;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyOpacity Error: {ex.Message}");
            }
        }

        #endregion
    }

    // ═════════════════════════════════════════════════════════════════
    // ███ FRAMEWORK ELEMENT EXTENSIONS
    // ═════════════════════════════════════════════════════════════════
    public static class FrameworkElementExtensions
    {
        public static void ForceUpdateLayout(this FrameworkElement element)
        {
            if (element == null) return;

            var temp = element.Resources;
            element.Resources = new ResourceDictionary();
            element.Resources = temp;

            element.UpdateLayout();
        }
    }
}