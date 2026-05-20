using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using Dreams.Themes;

namespace Dreams
{
    public partial class App : Application
    {
        private static Mutex _appMutex = new Mutex(true, "{Dreams_Software_Unique_Mutex_ID}");

        public static event Action<string> LanguageChanged;
        public static event Action<FlowDirection> FlowDirectionChanged;

        private const string REG_PATH = @"SOFTWARE\Dreams";

        private static ResourceDictionary _currentLanguageDict;
        private static bool _isChangingLanguage = false;
        private static readonly object _languageLock = new object();
        private static bool _isTrayEnabled = true;

        public static TrayManager TrayManager { get; private set; }
        public static bool IsDarkMode => ThemeManager.IsDarkMode;
        public static Window MainAppWindow => Current.MainWindow;
        public static bool IsTrayEnabled => _isTrayEnabled;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!_appMutex.WaitOne(TimeSpan.Zero, true))
            {
                MessageBox.Show("The application is already running.", "Dreams Software",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ThemeManager.Initialize();
            LoadApplicationSettings();

            // ✅ إنشاء النافذة الرئيسية مرة واحدة فقط
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            if (_isTrayEnabled && TrayManager == null)
            {
                TrayManager = new TrayManager();
                // ✅ لو التراي مفعل، النافذة تخفى
                mainWindow.Hide();
            }
            else
            {
                // ✅ لو التراي مش مفعل، النافذة تظهر
                mainWindow.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            TrayManager?.Dispose();

            if (_appMutex != null)
            {
                try { _appMutex.ReleaseMutex(); } catch { }
                _appMutex.Close();
                _appMutex = null;
            }

            base.OnExit(e);
        }

        // ✅ دالة لإظهار النافذة الرئيسية وإلغاء وضع التراي
        public static void ShowMainWindowAndDisableTrayMode()
        {
            Current.Dispatcher.Invoke(() =>
            {
                var mainWindow = Current.MainWindow as MainWindow;

                if (mainWindow == null)
                {
                    mainWindow = new MainWindow();
                    Current.MainWindow = mainWindow;
                }

                mainWindow.Show();

                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;

                mainWindow.Activate();
                mainWindow.Focus();

                _isTrayEnabled = false;

                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                    {
                        key?.SetValue("TrayEnabled", false);
                    }
                }
                catch { }

                TrayManager?.SetVisible(false);
            });
        }

        public static void ExitApplication()
        {
            try
            {
                if (TrayManager != null)
                {
                    TrayManager.SetVisible(false);
                    TrayManager.Dispose();
                    TrayManager = null;
                }

                if (_appMutex != null)
                {
                    try { _appMutex.ReleaseMutex(); } catch { }
                    _appMutex.Close();
                    _appMutex = null;
                }
            }
            catch { }

            try
            {
                Current.Dispatcher.Invoke(() =>
                {
                    foreach (Window win in Current.Windows)
                    {
                        try { win.Close(); } catch { }
                    }
                });
            }
            catch { }

            try
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        // ✅ تعديل دالة SetTrayEnabled
        public static void SetTrayEnabled(bool enabled)
        {
            bool oldState = _isTrayEnabled;
            _isTrayEnabled = enabled;

            if (enabled && !oldState)
            {
                if (TrayManager == null)
                {
                    TrayManager = new TrayManager();
                }
                else
                {
                    TrayManager.SetVisible(true);
                }

                if (Current.MainWindow is MainWindow mainWin && mainWin.IsVisible)
                {
                    mainWin.Hide();
                }
            }
            else if (!enabled && oldState)
            {
                ShowMainWindowAndDisableTrayMode();

                if (TrayManager != null)
                {
                    TrayManager.SetVisible(false);
                    TrayManager.Dispose();
                    TrayManager = null;
                }
            }

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                {
                    key?.SetValue("TrayEnabled", enabled);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Save Tray Error: {ex.Message}");
            }
        }

        private void LoadApplicationSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    string langCode = key?.GetValue("Language")?.ToString() ?? "en";
                    SetLanguage(langCode);

                    bool hwAccel = Convert.ToBoolean(key?.GetValue("HardwareAccel") ?? true);
                    System.Windows.Media.RenderOptions.ProcessRenderMode = hwAccel
                        ? RenderMode.Default
                        : RenderMode.SoftwareOnly;

                    _isTrayEnabled = Convert.ToBoolean(key?.GetValue("TrayEnabled") ?? true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Load Settings Error: {ex.Message}");
                _isTrayEnabled = true;
            }
        }

        public static void SetLanguage(string languageCode)
        {
            lock (_languageLock)
            {
                if (_isChangingLanguage) return;
                _isChangingLanguage = true;
            }

            try
            {
                Current.Dispatcher.Invoke(async () =>
                {
                    try
                    {
                        var resources = Current.Resources;
                        if (_currentLanguageDict != null)
                        {
                            resources.MergedDictionaries.Remove(_currentLanguageDict);
                            _currentLanguageDict = null;
                        }

                        await Task.Delay(50);

                        string langFile = languageCode switch
                        {
                            "ar" => "Lang/Lang-AR.xaml",
                            "fr" => "Lang/Lang-FR.xaml",
                            "es" => "Lang/Lang-ES.xaml",
                            "ru" => "Lang/Lang-RU.xaml",
                            _ => "Lang/Lang-EN.xaml"
                        };

                        _currentLanguageDict = new ResourceDictionary { Source = new Uri(langFile, UriKind.Relative) };
                        resources.MergedDictionaries.Add(_currentLanguageDict);

                        await Task.Delay(50);
                        ApplyLanguageFont(languageCode);

                        CultureInfo culture;
                        if (languageCode == "ar")
                        {
                            culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
                            culture.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
                            culture.NumberFormat.NumberDecimalSeparator = ".";
                            culture.NumberFormat.NumberGroupSeparator = ",";
                            culture.DateTimeFormat = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
                            culture.DateTimeFormat.Calendar = new GregorianCalendar();
                            culture.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
                            culture.DateTimeFormat.LongDatePattern = "dd MMMM yyyy";
                        }
                        else
                        {
                            culture = new CultureInfo(languageCode switch { "fr" => "fr-FR", "es" => "es-ES", "ru" => "ru-RU", _ => "en-US" });
                        }

                        Thread.CurrentThread.CurrentCulture = culture;
                        Thread.CurrentThread.CurrentUICulture = culture;

                        FlowDirection newFlowDirection = languageCode == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                        resources["ApplicationFlowDirection"] = newFlowDirection;

                        foreach (Window window in Current.Windows)
                        {
                            if (window?.Content is FrameworkElement content)
                                content.FlowDirection = newFlowDirection;
                        }

                        using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                        {
                            key?.SetValue("Language", languageCode);
                        }

                        await Task.Delay(100);
                        TrayManager?.UpdateLanguage();

                        LanguageChanged?.Invoke(languageCode);
                        FlowDirectionChanged?.Invoke(newFlowDirection);
                    }
                    finally
                    {
                        _isChangingLanguage = false;
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetLanguage Error: {ex.Message}");
                _isChangingLanguage = false;
            }
        }

        public static void ApplyLanguageFont(string langCode)
        {
            try
            {
                string resourceKey = langCode switch
                {
                    "ar" => "Font_AR",
                    "fr" => "Font_FR",
                    "es" => "Font_ES",
                    "ru" => "Font_RU",
                    _ => "Font_EN"
                };
                Current.Resources["GlobalFontFamily"] = Current.Resources[resourceKey];
            }
            catch
            {
                Current.Resources["GlobalFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            }
        }
    }
}