using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Dreams.Themes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dreams
{
    // ═════════════════════════════════════════════════════════════════
    // ███ ABOUT PAGE
    // ═════════════════════════════════════════════════════════════════

    public partial class About : Window
    {
        // ═════════════════════════════════════════════════════════════════
        // ███ FIELDS
        // ═════════════════════════════════════════════════════════════════

        #region Fields
        private bool _isDarkMode = false;
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ CONSTRUCTOR
        // ═════════════════════════════════════════════════════════════════

        #region Constructor
        public About()
        {
            InitializeComponent();

            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;

            App.LanguageChanged += OnLanguageChanged;
            ThemeManager.ThemeChanged += OnThemeManagerChanged;
            ThemeManager.OpacityChanged += OnOpacityManagerChanged;

            this.Loaded += OnPageLoaded;   // ✅ التطبيق هنا بس
            this.Closed += OnPageClosed;

            this.ShowInTaskbar = false;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // ❌ احذف LoadThemePreference() و ApplySavedOpacity() من هنا
        }

        private void ApplySavedOpacity()
        {
            try
            {
                // ✅ Use ThemeManager.GetSavedOpacity() instead of App.GetSavedOpacity()
                double opacity = ThemeManager.GetSavedOpacity();
                this.Opacity = opacity;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying opacity: {ex.Message}");
            }
        }

        // ✅ Handler for ThemeManager.ThemeChanged (Action<bool>)
        private void OnThemeManagerChanged(bool isDark)
        {
            // ✅ تأكد إن النافذة لسه شغالة قبل أي حاجة
            if (!this.IsLoaded && this.Visibility != Visibility.Visible)
                return;

            Dispatcher.Invoke(() =>
            {
                // ✅ تحقق تاني جوه الـ Dispatcher
                if (this.IsLoaded || this.Visibility == Visibility.Visible)
                {
                    _isDarkMode = isDark;
                    ApplyTheme();
                }
            }, DispatcherPriority.Background); // ✅ أولوية أقل عشان ماتقطعش الـ UI
        }

        private void OnOpacityManagerChanged(double opacity)
        {
            if (!this.IsLoaded && this.Visibility != Visibility.Visible)
                return;

            Dispatcher.Invoke(() =>
            {
                if (this.IsLoaded || this.Visibility == Visibility.Visible)
                {
                    this.Opacity = opacity;
                }
            }, DispatcherPriority.Background);
        }

        private void OnLanguageChanged(string langCode)
        {
            Dispatcher.Invoke(() =>
            {
                // UI updates automatically via DynamicResource
                // No action needed here
            });
        }

        private void OnPageLoaded(object sender, EventArgs e)
        {
            try
            {
                _isDarkMode = ThemeManager.IsDarkMode;
                ApplyTheme();
                ApplySavedOpacity();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"About load error: {ex.Message}");
            }
        }

        private void OnPageClosed(object sender, EventArgs e)
        {
            // ✅ إيقاف أي أنيميشن شغال عشان ميفضلش معلق
            try
            {
                if (HeartText != null)
                {
                    HeartText.BeginAnimation(TextBlock.OpacityProperty, null);
                    if (HeartText.RenderTransform is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    }
                }
            }
            catch { /* تجاهل أي خطأ في التنظيف */ }

            // ✅ إلغاء الاشتراك في الأحداث
            App.LanguageChanged -= OnLanguageChanged;
            ThemeManager.ThemeChanged -= OnThemeManagerChanged;
            ThemeManager.OpacityChanged -= OnOpacityManagerChanged;
            this.Loaded -= OnPageLoaded;
            this.Closed -= OnPageClosed;
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ LOAD METHODS
        // ═════════════════════════════════════════════════════════════════

        #region Load Methods
        private void LoadThemePreference()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Dreams"))
                {
                    if (key != null)
                    {
                        string theme = key.GetValue("Theme")?.ToString();
                        _isDarkMode = (theme == "Dark");
                    }
                }
            }
            catch
            {
                _isDarkMode = false;
            }

            ApplyTheme();
        }

        private void ApplyTheme()
        {
            try
            {
                ThemeManager.ApplyThemeToWindow(this);
                if (btnClose != null && TryFindResource("DynamicError") is Brush brush)
                    btnClose.Foreground = brush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyTheme error in About: {ex.Message}");
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ EVENT HANDLERS
        // ═════════════════════════════════════════════════════════════════

        #region Event Handlers
        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void btnWebsite_Click(object sender, RoutedEventArgs e)
        {
            OpenLink("https://github.com/Diagoo1");
        }

        private void btnPayPal_Click(object sender, RoutedEventArgs e)
        {
            OpenLink("https://paypal.me/Diagoo1");
        }

        private void btnEmail_Click(object sender, RoutedEventArgs e)
        {
            OpenLink("mailto:tarek.sadek44@gmail.com");
        }

        private void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                string errorTitle = FindResource("InstallationError")?.ToString() ?? "Error";
                MessageBox.Show($"Could not open link: {ex.Message}", errorTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PUBLIC METHODS
        // ═════════════════════════════════════════════════════════════════

        #region Public Methods
        public void ToggleTheme()
        {

            ThemeManager.ToggleTheme();
        }

        public void SetDarkTheme()
        {
            ThemeManager.SetTheme(true);
        }

        public void SetLightTheme()
        {
            ThemeManager.SetTheme(false);
        }

        public bool IsDarkMode => ThemeManager.IsDarkMode;
        #endregion
    }
}