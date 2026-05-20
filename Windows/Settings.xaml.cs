using System;
using System.Windows;
using Microsoft.Win32;
using Dreams.Themes;

namespace Dreams
{
    public partial class Settings : Window
    {
        private const string REG_PATH = @"SOFTWARE\Dreams";

        public Settings()
        {
            InitializeComponent();
            Loaded += Settings_Loaded;
        }

        private void Settings_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
        }

        #region Load Settings

        private void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    if (key != null)
                    {
                        string lang = key.GetValue("Language")?.ToString() ?? "en";
                        cmbLanguage.SelectedIndex = lang switch { "en" => 0, "ar" => 1, "fr" => 2, "es" => 3, "ru" => 4, _ => 0 };

                        chkDarkMode.IsChecked = ThemeManager.IsDarkMode;

                        double opacityPercent = Convert.ToDouble(key.GetValue("Opacity") ?? 100);
                        sldOpacity.Value = opacityPercent;

                        ApplyGlobalOpacity(opacityPercent / 100.0);

                        chkHardware.IsChecked = Convert.ToBoolean(key.GetValue("HardwareAccel") ?? true);
                        chkTray.IsChecked = App.IsTrayEnabled;
                    }
                }

                using (var runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                {
                    chkStartup.IsChecked = runKey?.GetValue("Dreams") != null;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoadSettings UI Error: {ex.Message}"); }
        }

        private void ApplyGlobalOpacity(double opacity)
        {
            opacity = Math.Max(0.3, Math.Min(1.0, opacity));
            this.Opacity = opacity;
            if (Application.Current.MainWindow != null)
                Application.Current.MainWindow.Opacity = opacity;
        }

        #endregion

        #region Save Settings

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                {
                    string langCode = cmbLanguage.SelectedIndex switch { 0 => "en", 1 => "ar", 2 => "fr", 3 => "es", 4 => "ru", _ => "en" };
                    key.SetValue("Language", langCode);
                    App.SetLanguage(langCode);

                    bool isDark = chkDarkMode.IsChecked ?? false;
                    key.SetValue("Theme", isDark ? "Dark" : "Light");
                    ThemeManager.SetTheme(isDark);

                    double opacity = sldOpacity.Value / 100.0;
                    key.SetValue("Opacity", sldOpacity.Value);
                    ThemeManager.SetOpacity(opacity);

                    key.SetValue("HardwareAccel", chkHardware.IsChecked ?? true);

                    bool trayPref = chkTray.IsChecked ?? true;
                    key.SetValue("TrayEnabled", trayPref);

                    bool oldTrayState = App.IsTrayEnabled;
                    if (oldTrayState != trayPref)
                    {
                        App.SetTrayEnabled(trayPref);
                    }

                    using (var runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (chkStartup.IsChecked == true)
                            runKey?.SetValue("Dreams", System.Reflection.Assembly.GetExecutingAssembly().Location);
                        else
                            runKey?.DeleteValue("Dreams", false);
                    }
                }

                RefreshAllOpenWindows();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving settings: " + ex.Message);
            }
        }

        private void RefreshAllOpenWindows()
        {
            try
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is FrameworkElement fe)
                    {
                        fe.Resources.MergedDictionaries.Clear();
                        fe.Resources.MergedDictionaries.Add(
                            new ResourceDictionary { Source = new Uri("Themes/Lang-EN.xaml", UriKind.Relative) });
                        fe.UpdateLayout();
                    }
                }
            }
            catch { }
        }

        #endregion

        #region UI Event Handlers

        private void btnClose_Click(object sender, RoutedEventArgs e) => this.Close();
        private void btnReportBug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ✅ غيّر اللينك ده للينك مشروعك على GitHub
                const string githubUrl = "https://github.com/YourUsername/Dreams/issues/new";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = githubUrl,
                    UseShellExecute = true   // ✅ مهم: يفتح في المتصفح الافتراضي
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not open browser:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        private void Border_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();

        private void ToggleTheme(object sender, RoutedEventArgs e)
        {
            ThemeManager.ToggleTheme();
            chkDarkMode.IsChecked = ThemeManager.IsDarkMode;
        }

        private void sldOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsLoaded)
            {
                ThemeManager.SetOpacity(e.NewValue / 100.0);
            }
        }

        private void chkHardware_Click(object sender, RoutedEventArgs e)
        {
            // Logic handled on save or app restart
        }

        #endregion
    }
}