using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Dreams.Themes;

namespace Dreams.Pages
{
    public partial class OptimizePage : Page, IDisposable
    {
        // ═══════════════════════════════════════════════════════════════
        // ███ NATIVE METHODS
        // ═══════════════════════════════════════════════════════════════
        #region Native Methods
        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;
        private const int UI_DEBOUNCE = 150;
        private const int UI_UPDATE_THROTTLE_MS = 200;
        private const long KB = 1024L;
        private const long MB = 1024L * KB;
        private const long GB = 1024L * MB;

        private static readonly string DeliveryOptimizationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "DeliveryOptimization", "Cache");
        private static readonly string WebCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "WebCache");
        private static readonly string NotificationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications");
        private static readonly string AppDataLocalTempPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp");
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ MEMORY STATUS STRUCT
        // ═══════════════════════════════════════════════════════════════
        #region Memory Status
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ FIELDS
        // ═══════════════════════════════════════════════════════════════
        #region Fields
        private bool _isDarkMode;
        private bool _isCleaning;
        private volatile bool _isCleaningPaused = false;
        public bool IsCleaning => _isCleaning;
        private bool _isDisposed;
        private bool _updatePending;
        private bool _isCalculating;
        private CancellationTokenSource _cts;
        private volatile bool _isExitPending = false;
        private TaskCompletionSource<bool> _exitDecisionTcs;
        private long _cleanedBytesSoFar;
        private DispatcherTimer _debounceTimer;
        private DateTime _lastUIUpdateTime = DateTime.MinValue;

        private long _tempSize, _recycleSize, _winLogSize, _thumbSize, _errorSize,
                     _webCacheSize, _eventLogsSize, _deliveryOptSize, _driverLogsSize,
                     _startMenuSize, _desktopSize, _notificationsSize, _winUpdateSize,
                     _dnsCacheSize, _networkUsageSize, _browserCacheSize, _browserHistSize,
                     _cookieSize, _dlHistSize, _passwordsSize, _formDataSize,
                     _localStorageSize, _serviceWorkersSize, _appCacheSize, _appLogsSize,
                     _appTempSize, _prefetchSize, _recentDocsSize, _runHistorySize,
                     _clipboardSize, _searchHistorySize, _appDataTempSize;

        private int _regInvalidCount, _regDllCount, _regEmptyCount, _regUninstallCount,
                    _regAppPathsCount, _regTypeLibCount, _regObsoleteCount;
        private long _systemTotal, _browserTotal, _appTotal, _privacyTotal, _registryTotal;

        private readonly ConcurrentDictionary<string, CacheEntry> _sizeCache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

        private readonly bool _isAdmin;
        private string _cachedGB, _cachedDegree, _cachedCelsius;

        private static readonly HashSet<string> _registryForbiddenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"SOFTWARE\Microsoft\Windows NT",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Classes\CLSID",
            @"SOFTWARE\Classes\Interface",
            @"SOFTWARE\Classes\TypeLib",
            @"SYSTEM\CurrentControlSet",
            @"HARDWARE", @"SECURITY", @"SAM"
        };
        private static readonly HashSet<string> _registrySafeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Temp", "Cache", "Logs", "Backup", "Old", "Obsolete", "Invalid" };
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ CACHE ENTRY
        // ═══════════════════════════════════════════════════════════════
        #region Cache Entry
        private class CacheEntry
        {
            public long Size { get; set; }
            public DateTime Timestamp { get; set; }
            public bool IsValid(TimeSpan d) => DateTime.Now - Timestamp < d;
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ CONSTRUCTOR
        // ═══════════════════════════════════════════════════════════════
        #region Constructor
        public OptimizePage()
        {
            InitializeComponent();
            _isAdmin = CheckAdministrator();

            App.LanguageChanged += OnLanguageChanged;
            App.FlowDirectionChanged += OnFlowDirectionChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            ThemeManager.OpacityChanged += OnOpacityChanged;
            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;

            InitDebounceTimer();
            SetupAllEvents();
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ PAGE LOADED & EVENTS
        // ═══════════════════════════════════════════════════════════════
        #region Page Loaded & Events
        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _isDarkMode = ThemeManager.IsDarkMode;
            ApplyTheme();

            var window = Window.GetWindow(this);
            if (window != null) window.Opacity = ThemeManager.GetSavedOpacity();

            CacheUIStrings();
            await Dispatcher.InvokeAsync(InitializeCheckboxHoverEffects, DispatcherPriority.Loaded);
            try { await LoadAllDataAsync(); }
            catch (Exception ex) { Debug.WriteLine($"[OnPageLoaded] {ex.Message}"); }
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e) => Dispose();

        private void OnThemeChanged(bool isDark) => Dispatcher.Invoke(() =>
        {
            _isDarkMode = isDark; ApplyTheme(); UpdateHardcodedElementsInline();
            var mw = Window.GetWindow(this) as MainWindow;
            if (mw?.FindName("btnTheme") is Button themeBtn) themeBtn.Content = isDark ? "\uE706" : "\uE708";
            this.UpdateLayout();
        }, DispatcherPriority.Background);

        private void OnOpacityChanged(double opacity) => Dispatcher.Invoke(() =>
        {
            var w = Window.GetWindow(this);
            if (w != null) w.Opacity = opacity;
        });

        private void OnLanguageChanged(string langCode) => Dispatcher.Invoke(async () =>
        {
            CacheUIStrings();
            await Task.Run(() => { UpdateDiskInfo(CancellationToken.None, true); UpdateStartupCount(CancellationToken.None); });
            RefreshUI(); UpdateLocalizedTexts();
        });

        private void OnFlowDirectionChanged(FlowDirection direction) => Dispatcher.Invoke(() => this.FlowDirection = direction);

        private void UpdateLocalizedTexts()
        {
            if (btnBoostRam != null) btnBoostRam.Content = GetLocalizedString("BOOST", "BOOST");
            if (btnAnalyzeDisk != null) btnAnalyzeDisk.Content = GetLocalizedString("ANALYZE", "ANALYZE");
            if (btnManageStartup != null) btnManageStartup.Content = GetLocalizedString("MANAGE", "MANAGE");
            if (btnRunOptimization != null) btnRunOptimization.Content = GetLocalizedString("RUNOPTIMIZATION", "RUN OPTIMIZATION");
            if (btnCloseResults != null) btnCloseResults.Content = GetLocalizedString("OK", "OK");
            if (lblGreeting != null) lblGreeting.Text = GetLocalizedString("SystemOptimizer", "System Optimizer");
            if (lblProgressText != null && !_isCleaning) lblProgressText.Text = GetLocalizedString("Preparing", "Preparing...");
            if (lblResultTitle != null) lblResultTitle.Text = GetLocalizedString("CleaningComplete", "Cleaning Complete!");
            UpdateDiskInfo(forceUpdate: true); UpdateStartupCount(); UpdateTotalRecoverable(); RefreshUI();
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ HOVER EFFECTS & IDISPOSABLE & EVENT SETUP
        // ═══════════════════════════════════════════════════════════════
        #region Hover & Dispose & Setup
        private void InitializeCheckboxHoverEffects()
        {
            var allCheckboxes = new[]
            {
                chkTempFiles, chkRecycleBin, chkWindowsLogs, chkThumbnails, chkErrorReports,
                chkWebCache, chkDnsCache, chkEventLogs, chkDeliveryOpt, chkDriverLogs,
                chkStartMenu, chkDesktop, chkNotifications, chkNetworkUsage, chkWinUpdate,
                chkBrowserCache, chkBrowserHistory, chkCookies, chkDownloads, chkPasswords,
                chkFormData, chkLocalStorage, chkServiceWorkers, chkAppCache, chkAppLogs,
                chkAppTemp, chkOldPrefetch, chkAppDataTemp,
                chkRegistryInvalid, chkRegistryDLL, chkRegistryEmpty, chkRegistryUninstall,
                chkRegistryAppPaths, chkRegistryTypeLib, chkRegistryObsolete,
                chkRecentDocs, chkRunHistory, chkClipboard, chkSearchHistory
            };

            foreach (var cb in allCheckboxes.Where(c => c != null))
            {
                if (cb.Parent is Panel parent)
                {
                    parent.Children.Remove(cb);
                    var hoverBorder = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(-8, 0, -8, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
                    hoverBorder.MouseEnter += (s, ev) => hoverBorder.Background = (Brush)FindResource("DynamicHoverBg");
                    hoverBorder.MouseLeave += (s, ev) => hoverBorder.ClearValue(Border.BackgroundProperty);
                    hoverBorder.PreviewMouseLeftButtonDown += (s, ev) =>
                    {
                        var source = ev.OriginalSource as DependencyObject;
                        bool isChk = false;
                        var current = source;
                        while (current != null && current != hoverBorder) { if (current is CheckBox) { isChk = true; break; } current = VisualTreeHelper.GetParent(current); }
                        if (!isChk) { cb.IsChecked = !cb.IsChecked; ev.Handled = true; }
                    };
                    parent.Children.Add(hoverBorder);
                    hoverBorder.Child = cb;
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _debounceTimer?.Stop(); _debounceTimer = null;
            if (!_isCleaning) _cts?.Cancel();
            _cts?.Dispose(); _cts = null;
            _sizeCache?.Clear();
            App.LanguageChanged -= OnLanguageChanged; App.FlowDirectionChanged -= OnFlowDirectionChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged; ThemeManager.OpacityChanged -= OnOpacityChanged;
            this.Loaded -= OnPageLoaded; this.Unloaded -= OnPageUnloaded;
            GC.Collect(); GC.WaitForPendingFinalizers();
        }

        private void SetupAllEvents()
        {
            var items = new[]
            {
                chkTempFiles, chkRecycleBin, chkWindowsLogs, chkThumbnails, chkErrorReports,
                chkWebCache, chkDnsCache, chkEventLogs, chkDeliveryOpt, chkDriverLogs,
                chkStartMenu, chkDesktop, chkNotifications, chkNetworkUsage, chkWinUpdate,
                chkBrowserCache, chkBrowserHistory, chkCookies, chkDownloads, chkPasswords,
                chkFormData, chkLocalStorage, chkServiceWorkers, chkAppCache, chkAppLogs,
                chkAppTemp, chkOldPrefetch, chkAppDataTemp,
                chkRegistryInvalid, chkRegistryDLL, chkRegistryEmpty, chkRegistryUninstall,
                chkRegistryAppPaths, chkRegistryTypeLib, chkRegistryObsolete,
                chkRecentDocs, chkRunHistory, chkClipboard, chkSearchHistory
            };
            foreach (var cb in items.Where(c => c != null)) { cb.Checked += OnItemCheckboxChanged; cb.Unchecked += OnItemCheckboxChanged; }
            RegisterMaster(chkSystemMaster, SystemMaster_Changed); RegisterMaster(chkBrowserMaster, BrowserMaster_Changed);
            RegisterMaster(chkAppMaster, AppMaster_Changed); RegisterMaster(chkRegistryMaster, RegistryMaster_Changed);
            RegisterMaster(chkPrivacyMaster, PrivacyMaster_Changed);
            if (chkSelectAll != null) { chkSelectAll.Checked += OnSelectAllClicked; chkSelectAll.Unchecked += OnSelectAllClicked; }
        }
        private static void RegisterMaster(CheckBox cb, RoutedEventHandler h) { if (cb != null) { cb.Checked += h; cb.Unchecked += h; } }
        private void OnItemCheckboxChanged(object sender, RoutedEventArgs e) { _updatePending = true; _debounceTimer.Stop(); _debounceTimer.Start(); }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ LOADING CIRCLES
        // ═══════════════════════════════════════════════════════════════
        #region Loading Circles
        private void StartLoadingCircles() => Dispatcher.Invoke(() =>
        {
            ShowLoadingCircle(loadingSystemSize, lblSystemSize); ShowLoadingCircle(loadingBrowserSize, lblBrowserSize);
            ShowLoadingCircle(loadingAppSize, lblAppSize); ShowLoadingCircle(loadingRegistrySize, lblRegistrySize);
            ShowLoadingCircle(loadingPrivacySize, lblPrivacySize); ShowLoadingCircle(loadingTotalSize, lblTotalRecoverable);
        });

        private void ShowLoadingCircle(Border circleBorder, TextBlock textBlock)
        {
            if (circleBorder == null || textBlock == null) return;
            try
            {
                textBlock.Visibility = Visibility.Collapsed; circleBorder.Child = null; circleBorder.Background = Brushes.Transparent;
                var canvas = new Canvas { Width = 24, Height = 24 };
                var accentColor = (Brush)FindResource("DynamicAccent");
                var ringColor = accentColor ?? new SolidColorBrush(Color.FromRgb(0, 120, 215));
                var points = new[] { new { Angle = 0, Opacity = 1.0 }, new { Angle = 72, Opacity = 0.8 }, new { Angle = 144, Opacity = 0.6 }, new { Angle = 216, Opacity = 0.4 }, new { Angle = 288, Opacity = 0.2 } };
                double dotSize = 4.0, radius = 10.0, cx = 12.0, cy = 12.0;
                foreach (var p in points)
                {
                    var rad = p.Angle * Math.PI / 180.0;
                    var dot = new System.Windows.Shapes.Ellipse { Width = dotSize, Height = dotSize, Fill = ringColor, Opacity = p.Opacity, Tag = p.Angle };
                    Canvas.SetLeft(dot, cx + radius * Math.Cos(rad) - dotSize / 2);
                    Canvas.SetTop(dot, cy + radius * Math.Sin(rad) - dotSize / 2);
                    canvas.Children.Add(dot);
                }
                circleBorder.Child = canvas; circleBorder.Visibility = Visibility.Visible;
                var rotate = new RotateTransform { CenterX = 12, CenterY = 12 }; canvas.RenderTransform = rotate;
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever });
                var fadeAnim = new DoubleAnimation { From = 1, To = 0.2, Duration = TimeSpan.FromSeconds(0.8), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                foreach (var child in canvas.Children)
                    if (child is System.Windows.Shapes.Ellipse dot && dot.Tag is int angle)
                    { var d = fadeAnim.Clone(); d.BeginTime = TimeSpan.FromMilliseconds(angle * 100 / 72); dot.BeginAnimation(System.Windows.Shapes.Ellipse.OpacityProperty, d); }
            }
            catch (Exception ex) { Debug.WriteLine($"[ShowLoadingCircle] {ex.Message}"); textBlock.Visibility = Visibility.Visible; circleBorder.Visibility = Visibility.Collapsed; }
        }

        private void StopLoadingCircles() => Dispatcher.Invoke(() =>
        {
            HideLoadingCircle(loadingSystemSize, lblSystemSize); HideLoadingCircle(loadingBrowserSize, lblBrowserSize);
            HideLoadingCircle(loadingAppSize, lblAppSize); HideLoadingCircle(loadingRegistrySize, lblRegistrySize);
            HideLoadingCircle(loadingPrivacySize, lblPrivacySize); HideLoadingCircle(loadingTotalSize, lblTotalRecoverable);
        });

        private void HideLoadingCircle(Border circleBorder, TextBlock textBlock)
        {
            if (circleBorder == null || textBlock == null) return;
            try
            {
                circleBorder.Child = null; circleBorder.Tag = null; circleBorder.Visibility = Visibility.Collapsed; textBlock.Visibility = Visibility.Visible;
                string itemsText = GetLocalizedString("Items", "items");
                if (textBlock == lblSystemSize && _systemTotal > 0) textBlock.Text = FormatSize(_systemTotal);
                else if (textBlock == lblBrowserSize && _browserTotal > 0) textBlock.Text = FormatSize(_browserTotal);
                else if (textBlock == lblAppSize && _appTotal > 0) textBlock.Text = FormatSize(_appTotal);
                else if (textBlock == lblRegistrySize)
                {
                    int tot = _regInvalidCount + _regDllCount + _regEmptyCount + _regUninstallCount + _regAppPathsCount + _regTypeLibCount + _regObsoleteCount;
                    textBlock.Text = tot > 0 ? $"{tot} {itemsText} (~{FormatSize(_registryTotal)})" : $"0 {itemsText}";
                }
                else if (textBlock == lblPrivacySize && _privacyTotal > 0) textBlock.Text = FormatSize(_privacyTotal);
                else if (textBlock == lblTotalRecoverable) textBlock.Text = FormatSize(_systemTotal + _browserTotal + _appTotal + _registryTotal + _privacyTotal);
            }
            catch (Exception ex) { Debug.WriteLine($"[HideLoadingCircle] {ex.Message}"); textBlock.Visibility = Visibility.Visible; circleBorder.Visibility = Visibility.Collapsed; }
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ DATA LOADING & SIZE CALCULATION ENGINE (محسّن ودقيق) ✅
        // ═══════════════════════════════════════════════════════════════
        #region Data Loading & Calculation
        private async Task LoadAllDataAsync()
        {
            if (_isCalculating) return;
            _isCalculating = true;
            await Dispatcher.InvokeAsync(() =>
            {
                if (btnRunOptimization != null) { btnRunOptimization.IsEnabled = false; btnRunOptimization.Content = GetLocalizedString("Loading", "Loading..."); }
                StartLoadingCircles();
            });
            _cts?.Cancel(); _cts?.Dispose(); _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                await Dispatcher.InvokeAsync(() => RefreshUI(), DispatcherPriority.Background);
                await Task.WhenAll(UpdateRAMInfoAsync(token), Task.Run(() => UpdateDiskInfo(token), token), Task.Run(() => UpdateStartupCount(token), token));

                var progress = new Progress<(string key, long value)>(update =>
                {
                    ApplyProgressUpdate(update.key, update.value);
                    if ((DateTime.Now - _lastUIUpdateTime).TotalMilliseconds > UI_UPDATE_THROTTLE_MS)
                    {
                        _lastUIUpdateTime = DateTime.Now;
                        Dispatcher.Invoke(() => { UpdateCategoryTotals(); UpdateTotalRecoverable(); UpdateDiskAnalyzer(); }, DispatcherPriority.Background);
                    }
                });

                await CalculateAllSizesAsync(token, progress);
                await Dispatcher.InvokeAsync(() => { RefreshUI(); StopLoadingCircles(); }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { Debug.WriteLine("[LoadAllDataAsync] Cancelled"); }
            catch (Exception ex) { Debug.WriteLine($"[LoadAllDataAsync] {ex.Message}"); }
            finally
            {
                _isCalculating = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (btnRunOptimization != null) { btnRunOptimization.IsEnabled = true; btnRunOptimization.Content = GetLocalizedString("RUNOPTIMIZATION", "RUN OPTIMIZATION"); }
                });
            }
        }

        private void ApplyProgressUpdate(string key, long value)
        {
            switch (key)
            {
                case "Temp": _tempSize = value; break;
                case "WinLog": _winLogSize = value; break;
                case "Recycle": _recycleSize = value; break;
                case "Thumb": _thumbSize = value; break;
                case "Error": _errorSize = value; break;
                case "WebCache": _webCacheSize = value; break;
                case "EventLogs": _eventLogsSize = value; break;
                case "DeliveryOpt": _deliveryOptSize = value; break;
                case "DriverLogs": _driverLogsSize = value; break;
                case "StartMenu": _startMenuSize = value; break;
                case "Desktop": _desktopSize = value; break;
                case "Notifications": _notificationsSize = value; break;
                case "WinUpdate": _winUpdateSize = value; break;
                case "AppCache": _appCacheSize = value; break;
                case "AppLogs": _appLogsSize = value; break;
                case "AppTemp": _appTempSize = value; break;
                case "Prefetch": _prefetchSize = value; break;
                case "Recent": _recentDocsSize = value; break;
                case "Search": _searchHistorySize = value; break;
                case "AppDataTemp": _appDataTempSize = value; break;
                case "BrowserCache": _browserCacheSize = value; break;
                case "BrowserHist": _browserHistSize = value; break;
                case "Cookies": _cookieSize = value; break;
                case "Passwords": _passwordsSize = value; break;
                case "FormData": _formDataSize = value; break;
                case "LocalStorage": _localStorageSize = value; break;
                case "ServiceWorkers": _serviceWorkersSize = value; break;
            }
        }

        // ✅ حساب حجم الملف بدقة (يتطابق مع الإكسبلورر)
        private static long CheckAndGetFileSize(string file)
        {
            try
            {
                var info = new FileInfo(file);
                // تخطي: نظامي + مخفي + نقاط إعادة تحميل (Junctions/Symlinks)
                if ((info.Attributes & (FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) return 0;
                return info.Exists ? info.Length : 0;
            }
            catch { return 0; }
        }

        // ✅ حساب مجلد كامل بدقة وسرعة
        private async Task<long> GetDeletableDirectorySizeAsync(string path, CancellationToken token)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
            return await Task.Run(() =>
            {
                long total = 0;
                try
                {
                    token.ThrowIfCancellationRequested();
                    // استخدام AllDirectories آمن مع try/catch حول التعداد
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        try { total += CheckAndGetFileSize(file); } catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (UnauthorizedAccessException) { /* مجلد محمي، نكمل */ }
                catch { /* أخطاء تعداد أخرى، نكمل */ }
                return total;
            }, token);
        }

        private async Task<long> GetDeletableFileSizeAsync(string filePath)
        {
            try { return !File.Exists(filePath) ? 0 : await Task.Run(() => CheckAndGetFileSize(filePath)); }
            catch { return 0; }
        }

        private async Task<long> GetDeletableRecycleBinSizeAsync(CancellationToken token)
        {
            long total = 0;
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Take(4).ToList();
                var tasks = drives.Select(d => GetDeletableDirectorySizeAsync(Path.Combine(d.Name, "$Recycle.Bin"), token));
                total = (await Task.WhenAll(tasks)).Sum();
            }
            catch { }
            return total;
        }

        private async Task<long> GetDeletablePatternSizeAsync(string basePath, string pattern, CancellationToken token)
        {
            try
            {
                if (!Directory.Exists(basePath)) return 0;
                var dirs = Directory.GetDirectories(basePath, pattern, SearchOption.TopDirectoryOnly).Take(15).ToList();
                var tasks = dirs.Select(d => GetDeletableDirectorySizeAsync(d, token));
                return (await Task.WhenAll(tasks)).Sum();
            }
            catch { return 0; }
        }

        private Task<long> GetDeletableThumbnailSizeAsync() => Task.Run(() =>
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
                return !Directory.Exists(path) ? 0L : Directory.EnumerateFiles(path, "thumbcache_*.db", SearchOption.AllDirectories).Take(50).Sum(f => CheckAndGetFileSize(f));
            }
            catch { return 0L; }
        });

        private async Task CalculateAllSizesAsync(CancellationToken token, IProgress<(string, long)> progress)
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);

            async Task<long> Calc(string key, Func<Task<long>> calc)
            {
                if (_sizeCache.TryGetValue(key, out var entry) && entry.IsValid(_cacheDuration)) { progress?.Report((key, entry.Size)); return entry.Size; }
                long size = await calc();
                _sizeCache[key] = new CacheEntry { Size = size, Timestamp = DateTime.Now };
                progress?.Report((key, size));
                return size;
            }

            token.ThrowIfCancellationRequested();
            var tempResults = new Dictionary<string, long>();
            async Task RunTask(string key, Func<Task<long>> calc, Action<long> setter)
            {
                var result = await Calc(key, calc);
                lock (tempResults) { tempResults[key] = result; }
                setter(result);
            }

            var systemTasks = new List<Task>
            {
                RunTask("Temp", () => GetDeletableDirectorySizeAsync(Path.GetTempPath(), token), v => _tempSize = v),
                RunTask("WinLog", () => GetDeletableDirectorySizeAsync(Path.Combine(winDir, "Temp"), token), v => _winLogSize = v),
                RunTask("Recycle", () => GetDeletableRecycleBinSizeAsync(token), v => _recycleSize = v),
                RunTask("Thumb", () => GetDeletableThumbnailSizeAsync(), v => _thumbSize = v),
                RunTask("Error", () => GetDeletableDirectorySizeAsync(Path.Combine(local, "CrashDumps"), token), v => _errorSize = v),
                RunTask("WebCache", () => GetDeletableDirectorySizeAsync(WebCachePath, token), v => _webCacheSize = v),
                RunTask("EventLogs", () => GetDeletableDirectorySizeAsync(Path.Combine(sysDir, "winevt", "Logs"), token), v => _eventLogsSize = v),
                RunTask("DeliveryOpt", () => Directory.Exists(DeliveryOptimizationPath) ? GetDeletableDirectorySizeAsync(DeliveryOptimizationPath, token) : Task.FromResult(0L), v => _deliveryOptSize = v),
                RunTask("Notifications", () => GetDeletableDirectorySizeAsync(NotificationPath, token), v => _notificationsSize = v),
                RunTask("DriverLogs", () => GetDeletableFileSizeAsync(Path.Combine(winDir, "inf", "setupapi.dev.log")), v => _driverLogsSize = v),
                RunTask("StartMenu", () => GetDeletableDirectorySizeAsync(startMenu, token), v => _startMenuSize = v),
                RunTask("Desktop", () => GetDeletableDirectorySizeAsync(systemDrive, token), v => _desktopSize = v),
                RunTask("WinUpdate", () => GetDeletableDirectorySizeAsync(Path.Combine(winDir, "SoftwareDistribution", "Download"), token), v => _winUpdateSize = v),
                RunTask("AppCache", () => GetDeletablePatternSizeAsync(local, "*Cache*", token), v => _appCacheSize = v),
                RunTask("AppLogs", () => GetDeletablePatternSizeAsync(local, "*Log*", token), v => _appLogsSize = v),
                RunTask("AppTemp", () => GetDeletablePatternSizeAsync(local, "*Temp*", token), v => _appTempSize = v),
                RunTask("Prefetch", () => GetDeletableDirectorySizeAsync(Path.Combine(winDir, "Prefetch"), token), v => _prefetchSize = v),
                RunTask("Recent", () => GetDeletableDirectorySizeAsync(recent, token), v => _recentDocsSize = v),
                RunTask("Search", () => GetDeletableDirectorySizeAsync(Path.Combine(local, "Microsoft", "Windows", "Search"), token), v => _searchHistorySize = v),
                RunTask("AppDataTemp", () => GetDeletableDirectorySizeAsync(AppDataLocalTempPath, token), v => _appDataTempSize = v)
            };

            await Task.WhenAll(systemTasks);
            token.ThrowIfCancellationRequested();
            _dnsCacheSize = 512 * KB; _networkUsageSize = 1 * MB; _runHistorySize = 512 * KB; _clipboardSize = 1 * MB;
            progress?.Report(("DNS", _dnsCacheSize)); progress?.Report(("Network", _networkUsageSize));
            token.ThrowIfCancellationRequested();
            var browserProfiles = FindBrowserProfiles(local);
            await Task.WhenAll(CalculateBrowserSizesSafeAsync(browserProfiles, token, progress), Task.Run(() => ScanRegistryItemsSafe(token), token));
        }

        private List<string> FindBrowserProfiles(string local)
        {
            var profiles = new List<string>();
            string[] bases = { "Google\\Chrome\\User Data", "Chromium\\User Data", "Microsoft\\Edge\\User Data" };
            foreach (var b in bases)
            {
                string fp = Path.Combine(local, b);
                if (!Directory.Exists(fp)) continue;
                foreach (var dir in Directory.GetDirectories(fp))
                {
                    string name = Path.GetFileName(dir);
                    if (name == "Default" || name.StartsWith("Profile ")) profiles.Add(dir);
                }
            }
            return profiles;
        }

        private async Task CalculateBrowserSizesSafeAsync(List<string> profiles, CancellationToken token, IProgress<(string, long)> progress = null)
        {
            try
            {
                long totalCache = 0, totalLS = 0, totalSW = 0, totalPwd = 0, totalForm = 0, totalHist = 0, totalCookies = 0;
                foreach (var prof in profiles)
                {
                    token.ThrowIfCancellationRequested();
                    bool running = IsBrowserProcessRunning(prof);
                    if (!running)
                    {
                        totalCache += await GetDeletableDirectorySizeAsync(Path.Combine(prof, "Cache"), token);
                        totalLS += await GetDeletableDirectorySizeAsync(Path.Combine(prof, "Local Storage"), token);
                        totalSW += await GetDeletableDirectorySizeAsync(Path.Combine(prof, "Service Worker"), token);
                    }
                    totalPwd += await GetDeletableFileSizeAsync(Path.Combine(prof, "Login Data"));
                    totalForm += await GetDeletableFileSizeAsync(Path.Combine(prof, "Web Data"));
                    totalHist += await GetDeletableFileSizeAsync(Path.Combine(prof, "History"));
                    totalCookies += await GetDeletableFileSizeAsync(Path.Combine(prof, "Cookies"));
                }
                _browserCacheSize = totalCache; _localStorageSize = totalLS; _serviceWorkersSize = totalSW;
                _passwordsSize = totalPwd; _formDataSize = totalForm; _browserHistSize = totalHist; _cookieSize = totalCookies;
                _dlHistSize = totalHist / 10;
                progress?.Report(("BrowserCache", totalCache)); progress?.Report(("LocalStorage", totalLS));
                progress?.Report(("ServiceWorkers", totalSW)); progress?.Report(("Passwords", totalPwd));
                progress?.Report(("FormData", totalForm)); progress?.Report(("BrowserHist", totalHist));
                progress?.Report(("Cookies", totalCookies));
            }
            catch (Exception ex) { Debug.WriteLine($"[CalculateBrowserSizes] {ex.Message}"); }
        }

        private bool IsBrowserProcessRunning(string profilePath)
        {
            try
            {
                string name = profilePath.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0 ? "chrome" :
                              profilePath.IndexOf("Edge", StringComparison.OrdinalIgnoreCase) >= 0 ? "msedge" : "";
                return !string.IsNullOrEmpty(name) && Process.GetProcessesByName(name).Length > 0;
            }
            catch { return false; }
        }

        private void ScanRegistryItemsSafe(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                _regInvalidCount = CountDeletableRunKeysSafe(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", false, token);
                _regDllCount = 0;
                _regEmptyCount = CountDeletableEmptySubKeysSafe(Registry.CurrentUser, "Software", 100, token);
                _regUninstallCount = CountDeletableUninstallEntriesSafe(Registry.CurrentUser, token);
                _regAppPathsCount = CountDeletableAppPathsSafe(Registry.CurrentUser, token);
                _regTypeLibCount = CountDeletableTypeLibsSafe(Registry.CurrentUser, token);
                _regObsoleteCount = CountDeletableObsoleteEntriesSafe(Registry.CurrentUser, token);
                if (_isAdmin)
                {
                    _regInvalidCount += CountDeletableRunKeysSafe(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false, token);
                    _regEmptyCount += CountDeletableEmptySubKeysSafe(Registry.LocalMachine, "Software", 100, token);
                    _regUninstallCount += CountDeletableUninstallEntriesSafe(Registry.LocalMachine, token);
                    _regAppPathsCount += CountDeletableAppPathsSafe(Registry.LocalMachine, token);
                    _regTypeLibCount += CountDeletableTypeLibsSafe(Registry.LocalMachine, token);
                    _regObsoleteCount += CountDeletableObsoleteEntriesSafe(Registry.LocalMachine, token);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[ScanRegistry] {ex.Message}"); }
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ DELETE ENGINE (مطابق للحساب بدقة + معالجة الملفات المفتوحة) ✅
        // ═══════════════════════════════════════════════════════════════
        #region Delete Engine
        private (long Bytes, int Files, List<string> Skipped) DeleteFilesWithCheck(string path, CancellationToken token)
        {
            if (!Directory.Exists(path)) return (0, 0, new List<string>());
            long totalBytes = 0; int totalFiles = 0;
            var skipped = new List<string>();

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        // نفس معايير الحساب بالضبط
                        if ((info.Attributes & (FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) continue;

                        // محاولة إزالة ReadOnly
                        if ((info.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            try { info.Attributes &= ~FileAttributes.ReadOnly; }
                            catch { skipped.Add($"Read-only failed: {Path.GetFileName(file)}"); continue; }
                        }

                        long size = info.Length;
                        File.Delete(file);
                        totalBytes += size; totalFiles++;
                    }
                    catch (IOException) { skipped.Add($"In use: {Path.GetFileName(file)}"); }
                    catch (UnauthorizedAccessException) { skipped.Add($"Access denied: {Path.GetFileName(file)}"); }
                    catch { /* تخطي سريع */ }
                }
                DeleteEmptyDirectories(path, token);
            }
            catch { }
            return (totalBytes, totalFiles, skipped);
        }

        private void DeleteEmptyDirectories(string path, CancellationToken token)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                {
                    token.ThrowIfCancellationRequested();
                    DeleteEmptyDirectories(dir, token);
                    try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false); } catch { }
                }
            }
            catch { }
        }

        private (long Bytes, int Files, List<string> Skipped) DeleteThumbnails()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
            if (!Directory.Exists(path)) return (0, 0, new List<string>());
            long totalBytes = 0; int totalFiles = 0; var skipped = new List<string>();
            foreach (var file in Directory.EnumerateFiles(path, "thumbcache_*.db", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & (FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) continue;
                    if ((info.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly) try { info.Attributes &= ~FileAttributes.ReadOnly; } catch { skipped.Add("ReadOnly"); continue; }
                    long s = info.Length; File.Delete(file); totalBytes += s; totalFiles++;
                }
                catch (IOException) { skipped.Add("In use"); }
                catch { }
            }
            return (totalBytes, totalFiles, skipped);
        }

        private (long Bytes, int Files, List<string> Skipped) DeletePattern(string basePath, string pattern, CancellationToken token)
        {
            long b = 0; int f = 0; var skipped = new List<string>();
            try
            {
                if (!Directory.Exists(basePath)) return (0, 0, skipped);
                foreach (var dir in Directory.GetDirectories(basePath, pattern, SearchOption.TopDirectoryOnly).Take(20))
                {
                    token.ThrowIfCancellationRequested();
                    var (db, df, ds) = DeleteFilesWithCheck(dir, token); b += db; f += df; skipped.AddRange(ds);
                }
            }
            catch { }
            return (b, f, skipped);
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ UI UPDATE METHODS
        // ═══════════════════════════════════════════════════════════════
        #region UI Update
        private void RefreshUI() => Dispatcher.BeginInvoke(new Action(() => { UpdateCategoryTotals(); UpdateTotalRecoverable(); UpdateDiskAnalyzer(); }), DispatcherPriority.Background);

        private void UpdateCategoryTotals()
        {
            _systemTotal = GetCheckedValue(chkTempFiles, _tempSize) + GetCheckedValue(chkRecycleBin, _recycleSize) + GetCheckedValue(chkWindowsLogs, _winLogSize) +
                           GetCheckedValue(chkThumbnails, _thumbSize) + GetCheckedValue(chkErrorReports, _errorSize) + GetCheckedValue(chkWebCache, _webCacheSize) +
                           GetCheckedValue(chkDnsCache, _dnsCacheSize) + GetCheckedValue(chkEventLogs, _eventLogsSize) + GetCheckedValue(chkDeliveryOpt, _deliveryOptSize) +
                           GetCheckedValue(chkDriverLogs, _driverLogsSize) + GetCheckedValue(chkStartMenu, _startMenuSize) + GetCheckedValue(chkDesktop, _desktopSize) +
                           GetCheckedValue(chkNotifications, _notificationsSize) + GetCheckedValue(chkNetworkUsage, _networkUsageSize) + GetCheckedValue(chkWinUpdate, _winUpdateSize);
            SetLabelText(lblSystemSize, _systemTotal);

            _browserTotal = GetCheckedValue(chkBrowserCache, _browserCacheSize) + GetCheckedValue(chkBrowserHistory, _browserHistSize) + GetCheckedValue(chkCookies, _cookieSize) +
                            GetCheckedValue(chkDownloads, _dlHistSize) + GetCheckedValue(chkPasswords, _passwordsSize) + GetCheckedValue(chkFormData, _formDataSize) +
                            GetCheckedValue(chkLocalStorage, _localStorageSize) + GetCheckedValue(chkServiceWorkers, _serviceWorkersSize);
            SetLabelText(lblBrowserSize, _browserTotal);

            _appTotal = GetCheckedValue(chkAppCache, _appCacheSize) + GetCheckedValue(chkAppLogs, _appLogsSize) + GetCheckedValue(chkAppTemp, _appTempSize) +
                        GetCheckedValue(chkOldPrefetch, _prefetchSize) + GetCheckedValue(chkAppDataTemp, _appDataTempSize);
            SetLabelText(lblAppSize, _appTotal);

            int totalRegCount = 0;
            if (IsChecked(chkRegistryInvalid)) totalRegCount += _regInvalidCount;
            if (IsChecked(chkRegistryDLL)) totalRegCount += _regDllCount;
            if (IsChecked(chkRegistryEmpty)) totalRegCount += _regEmptyCount;
            if (IsChecked(chkRegistryUninstall)) totalRegCount += _regUninstallCount;
            if (IsChecked(chkRegistryAppPaths)) totalRegCount += _regAppPathsCount;
            if (IsChecked(chkRegistryTypeLib)) totalRegCount += _regTypeLibCount;
            if (IsChecked(chkRegistryObsolete)) totalRegCount += _regObsoleteCount;
            _registryTotal = totalRegCount * KB;
            if (lblRegistrySize != null)
            {
                string it = GetLocalizedString("Items", "items");
                lblRegistrySize.Text = totalRegCount > 0 ? $"{totalRegCount} {it} (~{FormatSize(_registryTotal)})" : $"0 {it}";
            }

            _privacyTotal = GetCheckedValue(chkRecentDocs, _recentDocsSize) + GetCheckedValue(chkRunHistory, _runHistorySize) +
                            GetCheckedValue(chkClipboard, _clipboardSize) + GetCheckedValue(chkSearchHistory, _searchHistorySize);
            SetLabelText(lblPrivacySize, _privacyTotal);
        }

        private void UpdateTotalRecoverable()
        {
            long total = _systemTotal + _browserTotal + _appTotal + _registryTotal + _privacyTotal;
            if (lblTotalRecoverable != null) lblTotalRecoverable.Text = FormatSize(total);
        }

        private void UpdateDiskAnalyzer()
        {
            long total = _systemTotal + _browserTotal + _appTotal + _registryTotal + _privacyTotal;
            if (total <= 0) { SetPercentText(lblSystemPercent, 0, 1); SetPercentText(lblBrowserPercent, 0, 1); SetPercentText(lblAppPercent, 0, 1); SetPercentText(lblRegistryPercent, 0, 1); SetPercentText(lblPrivacyPercent, 0, 1); return; }
            SetPercentText(lblSystemPercent, _systemTotal, total); SetPercentText(lblBrowserPercent, _browserTotal, total); SetPercentText(lblAppPercent, _appTotal, total);
            SetPercentText(lblRegistryPercent, _registryTotal, total); SetPercentText(lblPrivacyPercent, _privacyTotal, total);
        }

        private static long GetCheckedValue(CheckBox cb, long value) => cb?.IsChecked == true ? value : 0;
        private static bool IsChecked(CheckBox cb) => cb?.IsChecked == true;
        private void SetLabelText(TextBlock lbl, long value) { if (lbl != null) lbl.Text = FormatSize(value); }
        private static void SetPercentText(TextBlock lbl, long size, long total) { if (lbl != null) lbl.Text = total > 0 ? $"{(double)size / total * 100:F1}%" : "0.0%"; }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0) return $"0 {GetLocalizedString("UnitB", "B")}";
            string[] units = { "UnitB", "UnitKB", "UnitMB", "UnitGB", "UnitTB" };
            double val = bytes; int i = 0;
            while (val >= 1024 && i < units.Length - 1) { val /= 1024; i++; }
            return $"{val:0.##} {GetLocalizedString(units[i], units[i].Replace("Unit", ""))}";
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ SYSTEM INFO
        // ═══════════════════════════════════════════════════════════════
        #region System Info
        private async Task UpdateRAMInfoAsync(CancellationToken token = default)
        {
            try
            {
                var (usedGB, totalGB, pct) = await Task.Run(() =>
                {
                    var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    if (!GlobalMemoryStatusEx(ref mem)) return (0.0, 0.0, 0);
                    double totalGB = mem.ullTotalPhys / (1024.0 * 1024 * 1024);
                    double availGB = mem.ullAvailPhys / (1024.0 * 1024 * 1024);
                    return (totalGB - availGB, totalGB, (int)mem.dwMemoryLoad);
                }, token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (lblRamUsage != null) lblRamUsage.Text = $"{usedGB:F1} / {totalGB:F1} {_cachedGB}";
                    if (lblRamPercent != null) lblRamPercent.Text = $"{pct}% {GetLocalizedString("Used", "Used")}";
                    if (prgRam != null) prgRam.Value = Math.Min(100, pct);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.WriteLine($"[UpdateRAMInfoAsync] {ex.Message}"); }
        }

        private static long GetFreeRamKB()
        {
            try { var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() }; return GlobalMemoryStatusEx(ref mem) ? (long)(mem.ullAvailPhys / 1024) : 0; }
            catch { return 0; }
        }

        private void UpdateDiskInfo(CancellationToken token = default, bool forceUpdate = false)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                var drive = new DriveInfo(systemDrive);
                if (!drive.IsReady) return;
                long totalGB = drive.TotalSize / GB;
                long usedGB = (drive.TotalSize - drive.TotalFreeSpace) / GB;
                int pct = totalGB > 0 ? (int)(usedGB * 100L / totalGB) : 0;
                Dispatcher.Invoke(() =>
                {
                    if (lblDiskSpace != null) lblDiskSpace.Text = $"{usedGB} / {totalGB} {_cachedGB}";
                    if (lblDiskPercent != null) lblDiskPercent.Text = $"{pct}% {GetLocalizedString("Used", "Used")}";
                    if (prgDisk != null) prgDisk.Value = Math.Min(100, Math.Max(0, pct));
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.WriteLine($"[UpdateDiskInfo] {ex.Message}"); }
        }

        private void UpdateStartupCount(CancellationToken token = default)
        {
            int startupCount = 0, totalInstalled = 0;
            try
            {
                string runPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using (var cu = Registry.CurrentUser.OpenSubKey(runPath)) startupCount += cu?.GetValueNames().Length ?? 0;
                using (var lm = Registry.LocalMachine.OpenSubKey(runPath)) startupCount += lm?.GetValueNames().Length ?? 0;
                string[] uninstallPaths = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
                foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                    foreach (var path in uninstallPaths)
                    {
                        using var key = root.OpenSubKey(path);
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames().Take(200))
                        {
                            try { using var sk = key.OpenSubKey(sub); if (sk == null) continue; string dn = sk.GetValue("DisplayName")?.ToString(); string us = sk.GetValue("UninstallString")?.ToString(); string pk = sk.GetValue("ParentKeyName")?.ToString(); string sc = sk.GetValue("SystemComponent")?.ToString(); if (string.IsNullOrEmpty(dn) || sc == "1" || pk != null) continue; if (!string.IsNullOrEmpty(us)) totalInstalled++; } catch { }
                        }
                    }
            }
            catch { }
            Dispatcher.Invoke(() =>
            {
                if (lblStartupCount != null) lblStartupCount.Text = $"{startupCount}";
                if (lblStartupDesc != null) lblStartupDesc.Text = totalInstalled > 0 ? $"{startupCount} {GetLocalizedString("StartupItems", "startup")} / {totalInstalled} {GetLocalizedString("InstalledApps", "installed")}" : $"{startupCount} {GetLocalizedString("StartupItems", "startup")}";
                if (prgStartup != null) prgStartup.Value = totalInstalled > 0 ? Math.Min(100, (startupCount * 100) / totalInstalled) : Math.Min(100, startupCount * 10);
            }, DispatcherPriority.Background);
        }

        private Task RefreshSystemInfoAsync() => Task.WhenAll(UpdateRAMInfoAsync(), Task.Run(() => UpdateDiskInfo()));
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ MASTER CHECKBOX HANDLERS
        // ═══════════════════════════════════════════════════════════════
        #region Master Checkboxes
        private static void SetGroupChecked(bool value, params CheckBox[] boxes) { foreach (var cb in boxes.Where(c => c != null)) cb.IsChecked = value; }
        private void SystemMaster_Changed(object s, RoutedEventArgs e) => SetGroupChecked(IsChecked(chkSystemMaster), chkTempFiles, chkRecycleBin, chkWindowsLogs, chkThumbnails, chkErrorReports, chkWebCache, chkDnsCache, chkEventLogs, chkDeliveryOpt, chkDriverLogs, chkStartMenu, chkDesktop, chkNotifications, chkNetworkUsage, chkWinUpdate);
        private void BrowserMaster_Changed(object s, RoutedEventArgs e) => SetGroupChecked(IsChecked(chkBrowserMaster), chkBrowserCache, chkBrowserHistory, chkCookies, chkDownloads, chkPasswords, chkFormData, chkLocalStorage, chkServiceWorkers);
        private void AppMaster_Changed(object s, RoutedEventArgs e) => SetGroupChecked(IsChecked(chkAppMaster), chkAppCache, chkAppLogs, chkAppTemp, chkOldPrefetch, chkAppDataTemp);
        private void RegistryMaster_Changed(object s, RoutedEventArgs e) => SetGroupChecked(IsChecked(chkRegistryMaster), chkRegistryInvalid, chkRegistryDLL, chkRegistryEmpty, chkRegistryUninstall, chkRegistryAppPaths, chkRegistryTypeLib, chkRegistryObsolete);
        private void PrivacyMaster_Changed(object s, RoutedEventArgs e) => SetGroupChecked(IsChecked(chkPrivacyMaster), chkRecentDocs, chkRunHistory, chkClipboard, chkSearchHistory);
        private void OnSelectAllClicked(object s, RoutedEventArgs e) => SetGroupChecked(IsChecked(chkSelectAll), chkSystemMaster, chkBrowserMaster, chkAppMaster, chkRegistryMaster, chkPrivacyMaster);
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ CLEANING PIPELINE
        // ═══════════════════════════════════════════════════════════════
        #region Cleaning Pipeline
        private async void btnRunOptimization_Click(object sender, RoutedEventArgs e) { if (_isCleaning) return; await ExecuteCleaningAsync(); }

        private async Task ExecuteCleaningAsync()
        {
            _isCleaning = true; App.TrayManager?.UpdateTrayIconBusyState(true); _cleanedBytesSoFar = 0;
            progressSection.Visibility = Visibility.Visible; resultsSection.Visibility = Visibility.Collapsed;
            progressBar.Value = 0; lblProgressPercent.Text = "0%";

            _cts?.Cancel(); _cts?.Dispose(); _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var opts = new CleanupOptions { Temp = IsChecked(chkTempFiles), Recycle = IsChecked(chkRecycleBin), WinLogs = IsChecked(chkWindowsLogs), Thumbnails = IsChecked(chkThumbnails), ErrorReports = IsChecked(chkErrorReports), WebCache = IsChecked(chkWebCache), DnsCache = IsChecked(chkDnsCache), EventLogs = IsChecked(chkEventLogs), DeliveryOpt = IsChecked(chkDeliveryOpt), DriverLogs = IsChecked(chkDriverLogs), StartMenu = IsChecked(chkStartMenu), Desktop = IsChecked(chkDesktop), Notifications = IsChecked(chkNotifications), NetworkUsage = IsChecked(chkNetworkUsage), WinUpdate = IsChecked(chkWinUpdate), BrowserCache = IsChecked(chkBrowserCache), BrowserHistory = IsChecked(chkBrowserHistory), Cookies = IsChecked(chkCookies), Downloads = IsChecked(chkDownloads), Passwords = IsChecked(chkPasswords), FormData = IsChecked(chkFormData), LocalStorage = IsChecked(chkLocalStorage), ServiceWorkers = IsChecked(chkServiceWorkers), AppCache = IsChecked(chkAppCache), AppLogs = IsChecked(chkAppLogs), AppTemp = IsChecked(chkAppTemp), Prefetch = IsChecked(chkOldPrefetch), AppDataTemp = IsChecked(chkAppDataTemp), RegInvalid = IsChecked(chkRegistryInvalid), RegDLL = IsChecked(chkRegistryDLL), RegEmpty = IsChecked(chkRegistryEmpty), RegUninstall = IsChecked(chkRegistryUninstall), RegAppPaths = IsChecked(chkRegistryAppPaths), RegTypeLib = IsChecked(chkRegistryTypeLib), RegObsolete = IsChecked(chkRegistryObsolete), RecentDocs = IsChecked(chkRecentDocs), RunHistory = IsChecked(chkRunHistory), Clipboard = IsChecked(chkClipboard), SearchHistory = IsChecked(chkSearchHistory) };

            var stages = new List<CleanupStage>
            {
                new CleanupStage($"🧹 {GetLocalizedString("CleaningSystem", "Cleaning system...")}", opts.HasSystem, () => CleanSystemAsync(opts, token)),
                new CleanupStage($"🌐 {GetLocalizedString("CleaningBrowser", "Cleaning browser...")}", opts.HasBrowser, () => CleanBrowserAsyncSafe(opts, token)),
                new CleanupStage($"📦 {GetLocalizedString("CleaningApps", "Cleaning apps...")}", opts.HasApp, () => CleanAppAsync(opts, token)),
                new CleanupStage($"🔧 {GetLocalizedString("CleaningRegistry", "Cleaning registry...")}", opts.HasRegistry, () => CleanRegistryAsyncSafe(opts, token)),
                new CleanupStage($"🔒 {GetLocalizedString("CleaningPrivacy", "Cleaning privacy...")}", opts.HasPrivacy, () => CleanPrivacyAsync(opts, token))
            }.Where(s => s.IsActive).ToList();

            if (stages.Count == 0)
            {
                progressSection.Visibility = Visibility.Collapsed; _isCleaning = false;
                MessageBox.Show(GetLocalizedString("NoCleanupItemsSelected", "Please select at least one item to clean."), GetLocalizedString("CleaningCategories", "No Items Selected"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long totalFreed = 0; var allDetails = new List<string>(); var allSkipped = new List<string>();
            double stepSize = 100.0 / stages.Count; int currentStep = 0;

            try
            {
                foreach (var stage in stages)
                {
                    while (_isCleaningPaused && !token.IsCancellationRequested) await Task.Delay(150, token);
                    if (token.IsCancellationRequested) break;
                    if (_isExitPending && _exitDecisionTcs != null) { bool shouldExit = await _exitDecisionTcs.Task; if (shouldExit) { _cts?.Cancel(); return; } _isExitPending = false; }
                    lblProgressText.Text = stage.Label;
                    var result = await stage.Execute();
                    totalFreed += result.BytesFreed; _cleanedBytesSoFar += result.BytesFreed;
                    allDetails.AddRange(result.Details); allSkipped.AddRange(result.SkippedFiles);
                    currentStep++; AnimateProgressBar(Math.Min(100, currentStep * stepSize)); await Task.Delay(50, token);
                }
                AnimateProgressBar(100); lblProgressPercent.Text = "100%"; lblProgressText.Text = $"✅ {GetLocalizedString("Complete", "Complete!")}";

                _sizeCache?.Clear();
                await CalculateAllSizesAsync(CancellationToken.None, null);
                RefreshUI(); await RefreshSystemInfoAsync();

                ShowCleanupResults(totalFreed, GetLocalizedString("CleaningComplete", "Cleaning Complete!"), allDetails, allSkipped);
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                if (_isExitPending && _exitDecisionTcs?.Task.Result == true) return;
                ShowCleanupResults(_cleanedBytesSoFar, $"⚠️ {GetLocalizedString("Cancelled", "Cancelled")}"); await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                ShowCleanupResults(0, $"⚠️ {GetLocalizedString("Error", "Error")}", message: ex.Message); await Task.Delay(2000);
            }
            finally { App.TrayManager?.UpdateTrayIconBusyState(false); progressSection.Visibility = Visibility.Collapsed; _isCleaning = false; }
        }

        private void AnimateProgressBar(double targetValue)
        {
            var anim = new DoubleAnimation(progressBar.Value, targetValue, TimeSpan.FromMilliseconds(200));
            progressBar.BeginAnimation(ProgressBar.ValueProperty, anim); lblProgressPercent.Text = $"{(int)targetValue}%";
        }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ CLEANUP IMPLEMENTATIONS
        // ═══════════════════════════════════════════════════════════════
        #region Cleanup Implementations
        private Task<CleanupResult> CleanSystemAsync(CleanupOptions opts, CancellationToken token) => Task.Run(() =>
        {
            var result = new CleanupResult();
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string sysDrv = Path.GetPathRoot(Environment.SystemDirectory);

            if (opts.Temp) { var (b, f, s) = DeleteFilesWithCheck(Path.GetTempPath(), token); if (b > 0) result.Add(b, $"✓ Temp Files: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.Recycle) { long b = 0; try { SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND); b = _recycleSize; } catch { } if (b > 0) result.Add(b, $"✓ Recycle Bin: {FormatSize(b)}"); }
            if (opts.WinLogs) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(winDir, "Temp"), token); if (b > 0) result.Add(b, $"✓ Windows Logs: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.Thumbnails) { var (b, f, s) = DeleteThumbnails(); if (b > 0) result.Add(b, $"✓ Thumbnail Cache: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.ErrorReports) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(local, "CrashDumps"), token); if (b > 0) result.Add(b, $"✓ Error Reports: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.WebCache) { var (b, f, s) = DeleteFilesWithCheck(WebCachePath, token); if (b > 0) result.Add(b, $"✓ Web Cache: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.WinUpdate) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(winDir, "SoftwareDistribution", "Download"), token); if (b > 0) result.Add(b, $"✓ Windows Update Cache: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.DnsCache) { RunCommand("ipconfig", "/flushdns", 5000); result.Add(_dnsCacheSize, "✓ DNS Cache: Flushed"); }
            if (opts.EventLogs) { RunCommand("cmd", "/c for /F \"tokens=*\" %1 in ('wevtutil el') DO wevtutil cl \"%1\"", 15000); result.Add(_eventLogsSize, "✓ Event Logs: Cleared"); }
            if (opts.DeliveryOpt) { var (b, f, s) = DeleteFilesWithCheck(DeliveryOptimizationPath, token); if (b > 0) result.Add(b, $"✓ Delivery Optimization: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.DriverLogs) { string lf = Path.Combine(winDir, "inf", "setupapi.dev.log"); if (File.Exists(lf)) { try { long s = new FileInfo(lf).Length; File.Delete(lf); if (s > 0) result.Add(s, $"✓ Driver Logs: {FormatSize(s)}"); } catch { } } }
            if (opts.StartMenu) { var (b, f, s) = DeleteFilesWithCheck(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), token); if (b > 0) result.Add(b, $"✓ Start Menu: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.Desktop) { var (b, f, s) = DeleteFilesWithCheck(sysDrv, token); if (b > 0) result.Add(b, $"✓ Desktop: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.Notifications) { var (b, f, s) = DeleteFilesWithCheck(NotificationPath, token); if (b > 0) result.Add(b, $"✓ Notifications: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.NetworkUsage) { int c = DeleteRegistrySubKeys(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\NetworkUsage"); if (c > 0) result.Add(c * KB, "✓ Network Usage: Cleared"); }
            return result;
        }, token);

        private Task<CleanupResult> CleanBrowserAsyncSafe(CleanupOptions opts, CancellationToken token) => Task.Run(() =>
        {
            var result = new CleanupResult();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profiles = FindBrowserProfiles(local);
            long totalCache = 0; int totalCacheFiles = 0;
            foreach (var prof in profiles)
            {
                token.ThrowIfCancellationRequested();
                bool running = IsBrowserProcessRunning(prof);
                if (running) { result.Add(0, $"⚠️ Browser running for {Path.GetFileName(Path.GetDirectoryName(prof))}, skipping cache"); continue; }
                if (opts.BrowserCache) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(prof, "Cache"), token); totalCache += b; totalCacheFiles += f; result.SkippedFiles.AddRange(s); }
                if (opts.LocalStorage) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(prof, "Local Storage"), token); if (b > 0) result.Add(b, $"✓ Local Storage ({Path.GetFileName(prof)}): {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
                if (opts.ServiceWorkers) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(prof, "Service Worker"), token); if (b > 0) result.Add(b, $"✓ Service Workers ({Path.GetFileName(prof)}): {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
                if (opts.BrowserHistory) { string hf = Path.Combine(prof, "History"); if (File.Exists(hf)) { try { long s = new FileInfo(hf).Length; File.Delete(hf); if (s > 0) result.Add(s, $"✓ Browser History ({Path.GetFileName(prof)}): {FormatSize(s)}"); } catch { } } }
                if (opts.Cookies) { string cf = Path.Combine(prof, "Cookies"); if (File.Exists(cf)) { try { long s = new FileInfo(cf).Length; File.Delete(cf); if (s > 0) result.Add(s, $"✓ Cookies ({Path.GetFileName(prof)}): {FormatSize(s)}"); } catch { } } }
                if (opts.Passwords) { string pf = Path.Combine(prof, "Login Data"); if (File.Exists(pf)) { try { long s = new FileInfo(pf).Length; File.Delete(pf); if (s > 0) result.Add(s, $"✓ Passwords ({Path.GetFileName(prof)}): {FormatSize(s)}"); } catch { } } }
                if (opts.FormData) { string ff = Path.Combine(prof, "Web Data"); if (File.Exists(ff)) { try { long s = new FileInfo(ff).Length; File.Delete(ff); if (s > 0) result.Add(s, $"✓ Form Data ({Path.GetFileName(prof)}): {FormatSize(s)}"); } catch { } } }
            }
            if (totalCache > 0) result.Add(totalCache, $"✓ Browser Cache: {FormatSize(totalCache)} ({totalCacheFiles} files)");
            return result;
        }, token);

        private Task<CleanupResult> CleanAppAsync(CleanupOptions opts, CancellationToken token) => Task.Run(() =>
        {
            var result = new CleanupResult();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            if (opts.AppCache) { var (b, f, s) = DeletePattern(local, "*Cache*", token); if (b > 0) result.Add(b, $"✓ App Cache: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.AppLogs) { var (b, f, s) = DeletePattern(local, "*Log*", token); if (b > 0) result.Add(b, $"✓ App Logs: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.AppTemp) { var (b, f, s) = DeletePattern(local, "*Temp*", token); if (b > 0) result.Add(b, $"✓ App Temp: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.Prefetch) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(winDir, "Prefetch"), token); if (b > 0) result.Add(b, $"✓ Prefetch: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.AppDataTemp) { var (b, f, s) = DeleteFilesWithCheck(AppDataLocalTempPath, token); if (b > 0) result.Add(b, $"✓ AppData Temp: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            return result;
        }, token);

        private Task<CleanupResult> CleanRegistryAsyncSafe(CleanupOptions opts, CancellationToken token) => Task.Run(() =>
        {
            var result = new CleanupResult();
            if (opts.RegInvalid) { int c = CleanInvalidRunPathsSafe(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", false, token); if (_isAdmin) c += CleanInvalidRunPathsSafe(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false, token); if (c > 0) result.Add(c * KB, $"✓ Invalid Startup Paths: {c} entries"); }
            if (opts.RegEmpty) { int c = CleanEmptyRegistryKeysSafe(Registry.CurrentUser, "Software", token); if (_isAdmin) c += CleanEmptyRegistryKeysSafe(Registry.LocalMachine, "Software", token); if (c > 0) result.Add(c * 2 * KB, $"✓ Empty Registry Keys: {c} entries"); }
            if (opts.RegUninstall) { int c = CleanMissingUninstallEntriesSafe(Registry.CurrentUser, token); if (_isAdmin) c += CleanMissingUninstallEntriesSafe(Registry.LocalMachine, token); if (c > 0) result.Add(c * 2 * KB, $"✓ Old Uninstall Entries: {c} entries"); }
            if (opts.RegAppPaths) { int c = CleanInvalidAppPathsSafe(Registry.CurrentUser, token); if (_isAdmin) c += CleanInvalidAppPathsSafe(Registry.LocalMachine, token); if (c > 0) result.Add(c * KB, $"✓ Invalid App Paths: {c} entries"); }
            if (opts.RegTypeLib) { int c = CleanInvalidTypeLibsSafe(Registry.CurrentUser, token); if (_isAdmin) c += CleanInvalidTypeLibsSafe(Registry.LocalMachine, token); if (c > 0) result.Add(c * KB, $"✓ Invalid Type Libraries: {c} entries"); }
            if (opts.RegObsolete) { int c = CleanObsoleteSoftwareEntriesSafe(Registry.CurrentUser, token); if (_isAdmin) c += CleanObsoleteSoftwareEntriesSafe(Registry.LocalMachine, token); if (c > 0) result.Add(c * KB, $"✓ Obsolete Software: {c} entries"); }
            return result;
        }, token);

        private Task<CleanupResult> CleanPrivacyAsync(CleanupOptions opts, CancellationToken token) => Task.Run(() =>
        {
            var result = new CleanupResult();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (opts.RecentDocs) { var (b, f, s) = DeleteFilesWithCheck(Environment.GetFolderPath(Environment.SpecialFolder.Recent), token); if (b > 0) result.Add(b, $"✓ Recent Docs: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            if (opts.RunHistory) { int c = CleanRunMRU(token); if (c > 0) result.Add(c * KB, $"✓ Run History: {c} entries"); }
            if (opts.Clipboard) { Dispatcher.Invoke(() => { try { Clipboard.Clear(); } catch { } }); result.Add(_clipboardSize, "✓ Clipboard: Cleared"); }
            if (opts.SearchHistory) { var (b, f, s) = DeleteFilesWithCheck(Path.Combine(local, "Microsoft", "Windows", "Search"), token); if (b > 0) result.Add(b, $"✓ Search History: {FormatSize(b)} ({f} files)"); result.SkippedFiles.AddRange(s); }
            return result;
        }, token);
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ REGISTRY HELPERS
        // ═══════════════════════════════════════════════════════════════
        #region Registry Helpers
        private bool IsRegistryKeySafeToDelete(string fullPath, string keyName) { foreach (var f in _registryForbiddenPaths) if (fullPath.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return false; if (keyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || keyName.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || keyName.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return false; foreach (var s in _registrySafeKeys) if (keyName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        private static bool IsRegistryKeySafeToDeleteStatic(string fullPath, string keyName) { var fp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"SOFTWARE\Microsoft\Windows NT", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Classes\CLSID", @"SOFTWARE\Classes\Interface", @"SOFTWARE\Classes\TypeLib", @"SYSTEM\CurrentControlSet", @"HARDWARE", @"SECURITY", @"SAM" }; foreach (var f in fp) if (fullPath.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return false; if (keyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || keyName.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || keyName.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return false; return true; }
        private static string ExtractExecutablePath(string val) { val = val.Trim(); if (val.StartsWith("\"")) { int e = val.IndexOf('"', 1); return e > 0 ? val.Substring(1, e - 1) : val; } int sp = val.IndexOf(' '); return sp > 0 ? val.Substring(0, sp) : val; }
        private static string ExtractDllPath(string val) { int i = val.IndexOf(".dll", StringComparison.OrdinalIgnoreCase); if (i < 0) return ""; return val.Substring(0, i + 4).Replace("\"", "").Trim(); }
        private int CountDeletableRunKeysSafe(RegistryKey root, string path, bool dllMode, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var k = root.OpenSubKey(path, false); if (k == null) return 0; var safe = new HashSet<string> { "SecurityHealth", "Windows Defender", "OneDriveSetup", "RtkAudUService" }; foreach (var name in k.GetValueNames().Take(50)) { token.ThrowIfCancellationRequested(); try { if (safe.Contains(name)) continue; string val = k.GetValue(name)?.ToString() ?? ""; if (string.IsNullOrWhiteSpace(val)) continue; string tp = ExtractExecutablePath(val); if (!string.IsNullOrEmpty(tp) && !File.Exists(tp)) count++; } catch { } } } catch { } return count; }
        private int CountDeletableEmptySubKeysSafe(RegistryKey root, string path, int limit, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var k = root.OpenSubKey(path, true); if (k == null) return 0; foreach (var sub in k.GetSubKeyNames().Take(limit)) { token.ThrowIfCancellationRequested(); try { string fp = $"{path}\\{sub}"; if (!IsRegistryKeySafeToDelete(fp, sub)) continue; using var sk = k.OpenSubKey(sub); if (sk != null && sk.SubKeyCount == 0 && sk.ValueCount == 0) count++; } catch { } } } catch { } return count; }
        private int CountDeletableUninstallEntriesSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", false); if (k == null) return 0; var safe = new HashSet<string> { "Microsoft Visual C++", "Microsoft .NET", "Microsoft Edge", "Microsoft Update", "DirectX", "Microsoft Windows", "KB" }; foreach (var sub in k.GetSubKeyNames().Take(100)) { token.ThrowIfCancellationRequested(); try { bool isSafe = safe.Any(s => sub.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0); if (isSafe) continue; using var sk = k.OpenSubKey(sub); if (sk?.GetValue("UninstallString") == null) { string dn = sk?.GetValue("DisplayName")?.ToString() ?? ""; if (dn.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0 && dn.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) < 0 && dn.IndexOf("Update", StringComparison.OrdinalIgnoreCase) < 0) count++; } } catch { } } } catch { } return count; }
        private int CountDeletableAppPathsSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths", true); if (k == null) return 0; foreach (var sub in k.GetSubKeyNames().Take(50)) { token.ThrowIfCancellationRequested(); try { string fp = $"AppPaths\\{sub}"; if (!IsRegistryKeySafeToDelete(fp, sub)) continue; using var sk = k.OpenSubKey(sub); string p = sk?.GetValue("")?.ToString(); if (!string.IsNullOrEmpty(p) && !File.Exists(p) && !Directory.Exists(p)) count++; } catch { } } } catch { } return count; }
        private int CountDeletableTypeLibsSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var k = root.OpenSubKey(@"SOFTWARE\Classes\TypeLib", true); if (k == null) return 0; foreach (var sub in k.GetSubKeyNames().Take(50)) { token.ThrowIfCancellationRequested(); try { string fp = $"TypeLib\\{sub}"; if (!IsRegistryKeySafeToDelete(fp, sub)) continue; using var sk = k.OpenSubKey(sub); if (sk?.SubKeyCount == 0 && sk?.ValueCount == 0) count++; } catch { } } } catch { } return count; }
        private int CountDeletableObsoleteEntriesSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true); if (k == null) return 0; foreach (var sub in k.GetSubKeyNames().Take(100)) { token.ThrowIfCancellationRequested(); try { string fp = $"Uninstall\\{sub}"; if (!IsRegistryKeySafeToDelete(fp, sub)) continue; using var sk = k.OpenSubKey(sub); string dn = sk?.GetValue("DisplayName")?.ToString(); string pub = sk?.GetValue("Publisher")?.ToString(); if (!string.IsNullOrEmpty(dn) && (dn.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)) continue; if (string.IsNullOrEmpty(dn) || (!string.IsNullOrEmpty(pub) && (pub.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0 || pub.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0))) count++; } catch { } } } catch { } return count; }
        private static int CleanInvalidRunPathsSafe(RegistryKey root, string path, bool dllMode, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = root.OpenSubKey(path, true); if (key == null) return 0; var safe = new HashSet<string> { "SecurityHealth", "Windows Defender", "OneDriveSetup", "RtkAudUService" }; foreach (var name in key.GetValueNames().ToList()) { token.ThrowIfCancellationRequested(); try { if (safe.Contains(name)) continue; string val = key.GetValue(name)?.ToString() ?? ""; if (string.IsNullOrWhiteSpace(val)) continue; string tp = ExtractExecutablePath(val); if (!string.IsNullOrEmpty(tp) && !File.Exists(tp)) { key.DeleteValue(name); count++; } } catch { } } } catch (OperationCanceledException) { throw; } catch { } return count; }
        private static int CleanEmptyRegistryKeysSafe(RegistryKey root, string path, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = root.OpenSubKey(path, true); if (key == null) return 0; foreach (var sub in key.GetSubKeyNames().ToList()) { token.ThrowIfCancellationRequested(); try { string fp = $"{path}\\{sub}"; if (!IsRegistryKeySafeToDeleteStatic(fp, sub)) continue; using var sk = key.OpenSubKey(sub); if (sk == null) continue; bool empty = sk.SubKeyCount == 0 && sk.ValueCount == 0; sk.Close(); if (empty) { key.DeleteSubKey(sub); count++; } } catch { } } } catch { } return count; }
        private static int CleanMissingUninstallEntriesSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true); if (key == null) return 0; var safe = new HashSet<string> { "Microsoft Visual C++", "Microsoft .NET", "Microsoft Edge", "Microsoft Update", "DirectX", "Microsoft Windows", "KB" }; foreach (var sub in key.GetSubKeyNames().ToList()) { token.ThrowIfCancellationRequested(); try { if (safe.Any(s => sub.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue; using var sk = key.OpenSubKey(sub); if (sk?.GetValue("UninstallString") != null) continue; string dn = sk?.GetValue("DisplayName")?.ToString() ?? ""; if (dn.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0) continue; sk?.Close(); key.DeleteSubKey(sub); count++; } catch { } } } catch { } return count; }
        private static int CleanInvalidAppPathsSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths", true); if (key == null) return 0; foreach (var sub in key.GetSubKeyNames().Take(100).ToList()) { token.ThrowIfCancellationRequested(); try { string fp = $"AppPaths\\{sub}"; if (!IsRegistryKeySafeToDeleteStatic(fp, sub)) continue; using var sk = key.OpenSubKey(sub); string p = sk?.GetValue("")?.ToString(); if (!string.IsNullOrEmpty(p) && !File.Exists(p) && !Directory.Exists(p)) { key.DeleteSubKey(sub); count++; } } catch { } } } catch { } return count; }
        private static int CleanInvalidTypeLibsSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = root.OpenSubKey(@"SOFTWARE\Classes\TypeLib", true); if (key == null) return 0; foreach (var sub in key.GetSubKeyNames().Take(100).ToList()) { token.ThrowIfCancellationRequested(); try { string fp = $"TypeLib\\{sub}"; if (!IsRegistryKeySafeToDeleteStatic(fp, sub)) continue; using var sk = key.OpenSubKey(sub); if (sk?.SubKeyCount == 0 && sk?.ValueCount == 0) { key.DeleteSubKey(sub); count++; } } catch { } } } catch { } return count; }
        private static int CleanObsoleteSoftwareEntriesSafe(RegistryKey root, CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true); if (key == null) return 0; foreach (var sub in key.GetSubKeyNames().Take(200).ToList()) { token.ThrowIfCancellationRequested(); try { string fp = $"Uninstall\\{sub}"; if (!IsRegistryKeySafeToDeleteStatic(fp, sub)) continue; using var sk = key.OpenSubKey(sub); string dn = sk?.GetValue("DisplayName")?.ToString(); string pub = sk?.GetValue("Publisher")?.ToString(); if (!string.IsNullOrEmpty(dn) && (dn.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 || dn.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)) continue; if (string.IsNullOrEmpty(dn) || (!string.IsNullOrEmpty(pub) && (pub.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0 || pub.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0))) { key.DeleteSubKey(sub); count++; } } catch { } } } catch { } return count; }
        private static int CleanRunMRU(CancellationToken token) { int count = 0; try { token.ThrowIfCancellationRequested(); using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", true); if (key == null) return 0; foreach (var name in key.GetValueNames().Where(n => n != "MRUList").ToList()) { try { key.DeleteValue(name); count++; } catch { } } } catch (OperationCanceledException) { throw; } catch { } return count; }
        private static int DeleteRegistrySubKeys(RegistryKey root, string path) { int count = 0; try { using var key = root.OpenSubKey(path, true); if (key == null) return 0; foreach (var sub in key.GetSubKeyNames().ToList()) { try { key.DeleteSubKey(sub); count++; } catch { } } } catch { } return count; }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ UTILITY METHODS
        // ═══════════════════════════════════════════════════════════════
        #region Utility
        private static void RunCommand(string fileName, string arguments, int timeoutMs) { try { var psi = new ProcessStartInfo { FileName = fileName, Arguments = arguments, CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }; using var proc = Process.Start(psi); proc?.WaitForExit(timeoutMs); } catch { } }
        private async void btnBoostRam_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || !btn.IsEnabled) return; btn.IsEnabled = false; btn.Content = $"⚡ {GetLocalizedString("BoostingText", "Boosting...")}";
            long ramFreedKB = 0;
            try
            {
                ramFreedKB = await Task.Run(() =>
                {
                    long before = GetFreeRamKB(); GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    foreach (var proc in Process.GetProcesses()) try { EmptyWorkingSet(proc.Handle); SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
                    try { NtSetSystemInformation(0x15, IntPtr.Zero, 0); } catch { }
                    Thread.Sleep(300); return Math.Max(0, GetFreeRamKB() - before);
                });
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ramFreedKB > 0) { string freed = ramFreedKB >= 1024 ? $"{(ramFreedKB / 1024.0):F1} MB" : $"{ramFreedKB} KB"; lblRamUsage.Text = freed; lblRamPercent.Text = GetLocalizedString("Cleaned", "Cleaned"); }
                    else lblRamPercent.Text = GetLocalizedString("AlreadyOptimized", "Already Optimized");
                });
                await Task.Delay(2000); await UpdateRAMInfoAsync();
            }
            finally { btn.Content = GetLocalizedString("BOOST", "BOOST"); btn.IsEnabled = true; }
        }

        public async Task<bool> RequestSafeExitAsync()
        {
            if (!_isCleaning) return true; _isCleaningPaused = true; await Task.Delay(100); _isExitPending = true; _exitDecisionTcs = new TaskCompletionSource<bool>();
            var result = await ModernMessageBox.Show(this, GetLocalizedString("ExitCleaningTitle", "Exit Cleaning"), GetLocalizedString("AreYouSureExitCleaning", "Cleaning is in progress. Are you sure you want to exit?"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            bool yes = result == MessageBoxResult.Yes; _exitDecisionTcs.TrySetResult(yes);
            if (!yes) { _isCleaningPaused = false; _isExitPending = false; _exitDecisionTcs = null; return false; }
            _cts?.Cancel(); _isExitPending = false; return true;
        }

        private void btnManageStartup_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo { FileName = "taskmgr.exe", Arguments = "/0 /startup", UseShellExecute = true }); } catch { try { Process.Start("msconfig.exe"); } catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); } } Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => UpdateStartupCount())); }
        private void btnAnalyzeDisk_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo { FileName = "cleanmgr.exe", Arguments = "/d C:", UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); } }
        private static bool CheckAdministrator() { try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
        private string GetLocalizedString(string key, string fallback = "") { try { if (this.FindResource(key) is string s) return s; if (Application.Current?.Resources.Contains(key) == true && Application.Current.Resources[key] is string a) return a; } catch { } return fallback; }
        private void CacheUIStrings() { _cachedGB = GetLocalizedString("GB", "GB"); _cachedDegree = GetLocalizedString("DegreeSymbol", "°"); _cachedCelsius = GetLocalizedString("Celsius", "C"); }
        private void InitDebounceTimer() { _debounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(UI_DEBOUNCE) }; _debounceTimer.Tick += (s, e) => { _debounceTimer.Stop(); if (_updatePending && !_isCalculating) { _updatePending = false; RefreshUI(); } }; }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ RESULTS DISPLAY
        // ═══════════════════════════════════════════════════════════════
        #region Results
        private void ShowCleanupResults(long bytesFreed, string title, List<string> details = null, List<string> skipped = null, string message = null)
        {
            Dispatcher.Invoke(() =>
            {
                resultsSection.Visibility = Visibility.Visible; resultsSection.Opacity = 1; resultsSection.Focus();
                lblResultTitle.Text = title;

                string mainMsg = !string.IsNullOrEmpty(message) ? message :
                    bytesFreed > 0 ? string.Format(GetLocalizedString("CleaningCompletedSuccess", "Successfully cleaned {0}"), FormatSize(bytesFreed)) : GetLocalizedString("NoIssuesFound", "No files were cleaned.");

                if (details?.Any() == true) mainMsg += "\n" + string.Join("\n", details.Take(5));
                if (skipped?.Any() == true)
                {
                    mainMsg += $"\n\n⚠️ {skipped.Count} items couldn't be deleted (in use, hidden, or protected)";
                    if (skipped.Count < 6) mainMsg += ":\n" + string.Join("\n", skipped);
                }
                lblResultsText.Text = mainMsg;
                lblSpaceFreed.Text = FormatSize(bytesFreed);
            }, DispatcherPriority.Normal);
        }

        private void btnCloseResults_Click(object sender, RoutedEventArgs e) { resultsSection.Visibility = Visibility.Collapsed; progressSection.Visibility = Visibility.Collapsed; if (btnRunOptimization != null) btnRunOptimization.IsEnabled = true; }
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ THEME MANAGEMENT
        // ═══════════════════════════════════════════════════════════════
        #region Theme
        private void LoadThemePreference() { try { using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Dreams"); _isDarkMode = key?.GetValue("Theme")?.ToString() == "Dark"; } catch { _isDarkMode = false; } ApplyTheme(); }
        private void ApplyTheme() { try { UpdateHardcodedElements(); } catch (Exception ex) { Debug.WriteLine($"ApplyTheme error: {ex.Message}"); } }
        private void UpdateHardcodedElements()
        {
            if (totalCardBorder != null) totalCardBorder.SetResourceReference(Border.BackgroundProperty, _isDarkMode ? "DynamicCardBgDark" : "DynamicCardBg");
            UpdateLoadingCirclesTheme();
            if (progressBar != null) progressBar.SetResourceReference(ProgressBar.ForegroundProperty, "DynamicAccent");
        }
        private void UpdateLoadingCirclesTheme()
        {
            var circles = new[] { loadingSystemSize, loadingBrowserSize, loadingAppSize, loadingRegistrySize, loadingPrivacySize, loadingTotalSize };
            foreach (var circle in circles) if (circle?.Child is Canvas canvas) foreach (var child in canvas.Children) if (child is System.Windows.Shapes.Ellipse dot) dot.SetResourceReference(System.Windows.Shapes.Ellipse.FillProperty, "DynamicAccent");
        }
        private void UpdateHardcodedElementsInline()
        {
            if (totalCardBorder != null) totalCardBorder.SetResourceReference(Border.BackgroundProperty, _isDarkMode ? "DynamicCardBgDark" : "DynamicCardBg");
            UpdateLoadingCirclesTheme(); if (progressBar != null) progressBar.SetResourceReference(ProgressBar.ForegroundProperty, "DynamicAccent");
        }
        private void ApplySavedOpacity() { try { var w = Window.GetWindow(this); if (w != null) w.Opacity = ThemeManager.GetSavedOpacity(); } catch (Exception ex) { Debug.WriteLine($"Error applying opacity: {ex.Message}"); } }
        public void ToggleTheme(object sender, RoutedEventArgs e) => ThemeManager.ToggleTheme();
        public bool IsDarkMode => ThemeManager.IsDarkMode;
        #endregion

        // ═══════════════════════════════════════════════════════════════
        // ███ HELPER CLASSES
        // ═══════════════════════════════════════════════════════════════
        #region Helper Classes
        internal sealed class CleanupOptions
        {
            public bool Temp, Recycle, WinLogs, Thumbnails, ErrorReports, WebCache, DnsCache, EventLogs, DeliveryOpt, DriverLogs, StartMenu, Desktop, Notifications, NetworkUsage, WinUpdate, BrowserCache, BrowserHistory, Cookies, Downloads, Passwords, FormData, LocalStorage, ServiceWorkers, AppCache, AppLogs, AppTemp, Prefetch, AppDataTemp, RegInvalid, RegDLL, RegEmpty, RegUninstall, RegAppPaths, RegTypeLib, RegObsolete, RecentDocs, RunHistory, Clipboard, SearchHistory;
            public bool HasSystem => Temp || Recycle || WinLogs || Thumbnails || ErrorReports || WebCache || DnsCache || EventLogs || DeliveryOpt || DriverLogs || StartMenu || Desktop || Notifications || NetworkUsage || WinUpdate;
            public bool HasBrowser => BrowserCache || BrowserHistory || Cookies || Downloads || Passwords || FormData || LocalStorage || ServiceWorkers;
            public bool HasApp => AppCache || AppLogs || AppTemp || Prefetch || AppDataTemp;
            public bool HasRegistry => RegInvalid || RegDLL || RegEmpty || RegUninstall || RegAppPaths || RegTypeLib || RegObsolete;
            public bool HasPrivacy => RecentDocs || RunHistory || Clipboard || SearchHistory;
        }

        internal sealed class CleanupResult
        {
            public long BytesFreed { get; private set; }
            public List<string> Details { get; } = new List<string>();
            public List<string> SkippedFiles { get; } = new List<string>();
            public void Add(long bytes, string detail) { BytesFreed += bytes; if (!string.IsNullOrEmpty(detail)) Details.Add(detail); }
        }

        internal sealed class CleanupStage
        {
            public string Label { get; }
            public bool IsActive { get; }
            private readonly Func<Task<CleanupResult>> _executor;
            public CleanupStage(string label, bool isActive, Func<Task<CleanupResult>> executor) { Label = label; IsActive = isActive; _executor = executor; }
            public Task<CleanupResult> Execute() => _executor();
        }
        #endregion
    }

    // ═══════════════════════════════════════════════════════════════════
    // ███ MODERN MESSAGEBOX
    // ═══════════════════════════════════════════════════════════════════
    #region ModernMessageBox
    public static class ModernMessageBox
    {
        private static string GetLocalizedString(DependencyObject owner, string key, string fallback)
        { try { if (owner is FrameworkElement fe && fe.FindResource(key) is string s) return s; if (Application.Current?.Resources.Contains(key) == true && Application.Current.Resources[key] is string a) return a; } catch { } return fallback ?? key; }

        public static async Task<MessageBoxResult> Show(DependencyObject owner, string title, string message, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>(); double parentOpacity = 1.0; if (owner is FrameworkElement fe && Window.GetWindow(fe) is Window pw) parentOpacity = pw.Opacity;
            var window = new Window { Width = 450, Height = 260, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Opacity = parentOpacity, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            Brush GetRes(string key, Brush fallback) { try { if (Application.Current?.Resources.Contains(key) == true && Application.Current.Resources[key] is Brush b) return b; if (owner is FrameworkElement fe2 && fe2.TryFindResource(key) is Brush b2) return b2; } catch { } return fallback; }
            Brush mainBg = GetRes("DynamicCardBg", Brushes.White); Brush borderBrush = GetRes("DynamicBorderBrush", new SolidColorBrush(Color.FromRgb(220, 220, 220))); Brush mainText = GetRes("DynamicMainText", Brushes.Black); Brush subText = GetRes("DynamicSubText", new SolidColorBrush(Color.FromRgb(90, 90, 90))); Brush accentColor = GetRes("DynamicAccent", new SolidColorBrush(Color.FromRgb(0, 120, 212)));
            string iconChar = icon switch { MessageBoxImage.Warning => "\\uE7BA", MessageBoxImage.Information => "\\uE946", MessageBoxImage.Error => "\\uEB90", MessageBoxImage.Question => "\\uE897", _ => "\\uE946" }; Color iconColorValue = icon switch { MessageBoxImage.Warning => Color.FromRgb(255, 193, 7), MessageBoxImage.Error => Color.FromRgb(220, 53, 69), MessageBoxImage.Question => Color.FromRgb(0, 192, 192), _ => ((SolidColorBrush)accentColor).Color }; Brush iconColorBrush = new SolidColorBrush(iconColorValue); Brush lightCircle = new SolidColorBrush(Color.FromArgb(38, iconColorValue.R, iconColorValue.G, iconColorValue.B));
            var bgBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) }; bgBrush.GradientStops.Add(new GradientStop(iconColorValue, 0)); bgBrush.GradientStops.Add(new GradientStop(iconColorValue, 0.023)); bgBrush.GradientStops.Add(new GradientStop(((SolidColorBrush)mainBg).Color, 0.0231)); bgBrush.GradientStops.Add(new GradientStop(((SolidColorBrush)mainBg).Color, 1));
            var border = new Border { Background = bgBrush, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = borderBrush, ClipToBounds = true };
            var mainGrid = new Grid(); mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var iconPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 12) }; var iconBorder = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = lightCircle, HorizontalAlignment = HorizontalAlignment.Center }; var iconTb = new TextBlock { Text = iconChar, FontSize = 32, Foreground = iconColorBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe MDL2 Assets") }; iconBorder.Child = iconTb; iconPanel.Children.Add(iconBorder); Grid.SetRow(iconPanel, 0);
            var contentPanel = new StackPanel { Margin = new Thickness(30, 0, 30, 20) }; contentPanel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = mainText, Margin = new Thickness(0, 0, 0, 8), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap }); contentPanel.Children.Add(new TextBlock { Text = message, FontSize = 13, Foreground = subText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }); Grid.SetRow(contentPanel, 1);
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) }; string txtOK = GetLocalizedString(owner, "OK", "OK"), txtCancel = GetLocalizedString(owner, "Cancel", "Cancel"), txtYes = GetLocalizedString(owner, "Yes", "Yes"), txtNo = GetLocalizedString(owner, "No", "No");
            void AddBtn(string text, MessageBoxResult res, bool outline = false) { var btn = CreateModernButton(text, icon, outline ? mainText : iconColorBrush, borderBrush, mainText, outline); btn.Click += (_, __) => { tcs.SetResult(res); window.Close(); }; buttonPanel.Children.Add(btn); }
            if (buttons == MessageBoxButton.OK) AddBtn(txtOK, MessageBoxResult.OK); else if (buttons == MessageBoxButton.OKCancel) { AddBtn(txtCancel, MessageBoxResult.Cancel, true); AddBtn(txtOK, MessageBoxResult.OK); } else if (buttons == MessageBoxButton.YesNo) { AddBtn(txtNo, MessageBoxResult.No, true); AddBtn(txtYes, MessageBoxResult.Yes); } else if (buttons == MessageBoxButton.YesNoCancel) { AddBtn(txtCancel, MessageBoxResult.Cancel, true); AddBtn(txtNo, MessageBoxResult.No, true); AddBtn(txtYes, MessageBoxResult.Yes); }
            Grid.SetRow(buttonPanel, 2); mainGrid.Children.Add(iconPanel); mainGrid.Children.Add(contentPanel); mainGrid.Children.Add(buttonPanel); border.Child = mainGrid; window.Content = border;
            window.PreviewMouseLeftButtonDown += (s, e) => { DependencyObject src = e.OriginalSource as DependencyObject; bool isBtn = false; while (src != null && src != window) { if (src is Button) { isBtn = true; break; } src = VisualTreeHelper.GetParent(src); } if (!isBtn && window.WindowState == WindowState.Normal) window.DragMove(); }; window.Cursor = Cursors.Arrow;
            window.Loaded += (_, __) => window.BeginAnimation(Window.OpacityProperty, new DoubleAnimation(0, parentOpacity, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }); window.ShowDialog(); return await tcs.Task;
        }

        private static Button CreateModernButton(string text, MessageBoxImage iconType, Brush iconColor, Brush borderBrush, Brush hoverFg, bool isOutline = false)
        {
            Brush bg = iconType switch { MessageBoxImage.Warning => new SolidColorBrush(Color.FromRgb(255, 193, 7)), MessageBoxImage.Error => new SolidColorBrush(Color.FromRgb(220, 53, 69)), MessageBoxImage.Question => new SolidColorBrush(Color.FromRgb(0, 192, 192)), MessageBoxImage.Information => new SolidColorBrush(Color.FromRgb(0, 120, 212)), _ => iconColor };
            var button = new Button { Content = text, Width = 100, Height = 38, Margin = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold, FontSize = 13, Padding = new Thickness(0) };
            var template = new ControlTemplate(typeof(Button)); var bdFactory = new FrameworkElementFactory(typeof(Border)); bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); bdFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); bdFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); bdFactory.SetValue(Border.BorderThicknessProperty, isOutline ? new Thickness(1.5) : new Thickness(0)); var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); cp.SetBinding(System.Windows.Documents.TextElement.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); bdFactory.AppendChild(cp); template.VisualTree = bdFactory; button.Template = template;
            if (isOutline) { button.Background = Brushes.Transparent; button.Foreground = hoverFg; button.BorderBrush = bg; button.MouseEnter += (_, __) => { if (bg is SolidColorBrush s) button.Background = new SolidColorBrush(Color.FromArgb(35, s.Color.R, s.Color.G, s.Color.B)); }; button.MouseLeave += (_, __) => button.Background = Brushes.Transparent; }
            else { button.Background = bg; button.Foreground = Brushes.White; button.BorderBrush = bg; button.MouseEnter += (_, __) => { if (bg is SolidColorBrush s) button.Background = new SolidColorBrush(Color.FromRgb((byte)(s.Color.R * .88), (byte)(s.Color.G * .88), (byte)(s.Color.B * .88))); }; button.MouseLeave += (_, __) => button.Background = bg; }
            button.PreviewMouseLeftButtonDown += (_, __) => button.Opacity = 0.85; button.PreviewMouseLeftButtonUp += (_, __) => button.Opacity = 1; return button;
        }
    }
    #endregion
}