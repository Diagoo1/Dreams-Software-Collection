// ============================================================
// ملف: TrayManager.cs
// المسار: Dreams/TrayManager.cs
// ============================================================

using System;
using System.Diagnostics;
using System.Drawing;                    // ✅ لأيقونات النظام
using System.Linq;                      // ✅ IMPORTANT - Needed for OfType<T>()
using System.Windows;                    // ✅ لأنواع WPF الأساسية
using System.Windows.Forms;              // ✅ لـ NotifyIcon و MouseEventArgs (مهم!)
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;              // ✅ لـ Matrix
using Dreams.Pages;                      // ✅ للنيم سبيس الجديد للصفحات

using WPFApplication = System.Windows.Application;

namespace Dreams
{
    public class TrayManager : IDisposable
    {
        private NotifyIcon _notifyIcon;
        private bool _isDisposed;
        private TrayMenu _menuWindow; // نافذة المنيو الصغيرة (لو موجودة)

        public TrayManager()
        {
            InitializeTrayIcon();
        }

        // ═════════════════════════════════════════════════════════════════
        // ███ INITIALIZATION
        // ═════════════════════════════════════════════════════════════════

        #region Initialization

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new NotifyIcon();

                try
                {
                    string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exePath);
                }
                catch
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }

                _notifyIcon.Text = GetString("AppName", "Dreams Software");
                _notifyIcon.Visible = App.IsTrayEnabled;

                _notifyIcon.MouseClick += OnTrayIconMouseClick;
                _notifyIcon.DoubleClick += OnTrayIconDoubleClick;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CRITICAL] Tray Init Error: {ex.Message}");
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ EVENT HANDLERS
        // ═════════════════════════════════════════════════════════════════

        #region Event Handlers

        private void OnTrayIconDoubleClick(object sender, EventArgs e)
        {
            ShowMainWindow();
        }

        private void OnTrayIconMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowWpfMenu();
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ MENU DISPLAY (✅ النسخة المحسنة)
        // ═════════════════════════════════════════════════════════════════

        #region Menu Display

        private void ShowWpfMenu()
        {
            try
            {
                // ✅ اغلق القديم لو موجود
                if (_menuWindow != null)
                {
                    try { _menuWindow.Close(); } catch { }
                    _menuWindow = null;
                }

                _menuWindow = new TrayMenu();
                _menuWindow.Closed += (_, __) => _menuWindow = null;

                // ✅ موقع الماوس الحقيقي بدون الاعتماد على MainWindow
                var mouse = System.Windows.Forms.Control.MousePosition;

                // ✅ حساب الـ DPI scaling من الـ screen مباشرة
                double dpiScale = GetDpiScale();

                double mouseX = mouse.X / dpiScale;
                double mouseY = mouse.Y / dpiScale;

                // ✅ أبعاد الشاشة المتاحة
                var workArea = SystemParameters.WorkArea;

                // ✅ أبعاد المنيو (لازم تتعرف قبل الحساب)
                double menuWidth = 220;
                double menuHeight = 400;

                // ✅ حساب الموقع فوق أيقونة التراي
                double left = mouseX - menuWidth / 2;
                double top = mouseY - menuHeight - 10;

                // ✅ تأكد إن المنيو مش بيخرج من حدود الشاشة
                if (left < workArea.Left)
                    left = workArea.Left + 5;

                if (left + menuWidth > workArea.Right)
                    left = workArea.Right - menuWidth - 5;

                if (top < workArea.Top)
                    top = workArea.Top + 5;

                if (top + menuHeight > workArea.Bottom)
                    top = workArea.Bottom - menuHeight - 10;

                _menuWindow.Left = left;
                _menuWindow.Top = top;

                _menuWindow.Show();
                _menuWindow.Activate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Menu Show Error: {ex.Message}");
            }
        }

        // ✅ دالة منفصلة للـ DPI بدون الاعتماد على MainWindow
        private double GetDpiScale()
        {
            try
            {
                // محاولة أولى: من الـ MainWindow لو موجودة وباينة
                var mainWin = WPFApplication.Current.MainWindow;
                if (mainWin != null && mainWin.IsVisible)
                {
                    var source = PresentationSource.FromVisual(mainWin);
                    if (source?.CompositionTarget != null)
                        return source.CompositionTarget.TransformToDevice.M11;
                }

                // ✅ محاولة ثانية: من أي نافذة مفتوحة
                foreach (Window win in WPFApplication.Current.Windows)
                {
                    if (win.IsVisible)
                    {
                        var source = PresentationSource.FromVisual(win);
                        if (source?.CompositionTarget != null)
                            return source.CompositionTarget.TransformToDevice.M11;
                    }
                }

                // ✅ fallback: من الـ Graphics
                using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
                return g.DpiX / 96.0;
            }
            catch
            {
                return 1.0; // افتراضي لو كل حاجة فشلت
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ ACTION HANDLERS (✅ النسخة المحسنة)
        // ═════════════════════════════════════════════════════════════════

        #region Action Handlers

        public async void TriggerAction(string action)
        {
            bool canProceed = true;

            var mainWindow = WPFApplication.Current.MainWindow as MainWindow;

            if (mainWindow?.PagesNavigation?.Content is OptimizePage optPage)
                canProceed = await optPage.RequestSafeExitAsync();
            else if (mainWindow?.PagesNavigation?.Content is InstallPage instPage)
                canProceed = await instPage.RequestSafeExitAsync();
            else if (mainWindow?.PagesNavigation?.Content is OnlinePage onlinePage)
                canProceed = await onlinePage.RequestSafeExitAsync();

            if (!canProceed) return;

            WPFApplication.Current.Dispatcher.Invoke(() =>
            {
                switch (action)
                {
                    // ✅ الصفحات → MainWindow + Navigation + Sidebar Sync
                    case "Dashboard":
                        EnsureMainWindowReady();
                        (WPFApplication.Current.MainWindow as MainWindow)?
                            .NavigateFromExternal("Dashboard");
                        break;

                    case "Installer":
                        EnsureMainWindowReady();
                        (WPFApplication.Current.MainWindow as MainWindow)?
                            .NavigateFromExternal("Installer");
                        break;

                    case "AppStore":
                        EnsureMainWindowReady();
                        (WPFApplication.Current.MainWindow as MainWindow)?
                            .NavigateFromExternal("AppStore");
                        break;

                    case "Optimizer":
                        EnsureMainWindowReady();
                        (WPFApplication.Current.MainWindow as MainWindow)?
                            .NavigateFromExternal("Optimizer");
                        break;

                    case "Tweaks":
                        EnsureMainWindowReady();
                        (WPFApplication.Current.MainWindow as MainWindow)?
                            .NavigateFromExternal("Tweaks");
                        break;

                    case "DNS":
                        EnsureMainWindowReady();
                        (WPFApplication.Current.MainWindow as MainWindow)?
                            .NavigateFromExternal("DNS");
                        break;

                    case "show":
                        EnsureMainWindowReady();
                        break;

                    // ✅ Settings → لوحدها بدون MainWindow
                    case "Settings":
                        OpenStandaloneWindow(new Settings());
                        break;

                    // ✅ About → لوحدها بدون MainWindow
                    case "About":
                        OpenStandaloneWindow(new About());
                        break;
                }
            });
        }
        // ═════════════════════════════════════════════════════════════════
        // ✅ دالة جديدة: فتح نافذة مستقلة بدون MainWindow
        // ═════════════════════════════════════════════════════════════════
        private void OpenStandaloneWindow(Window win)
        {
            try
            {
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                // ✅ Owner فقط لو MainWindow معروضة فعلاً
                var existingMain = GetExistingVisibleMainWindow();
                if (existingMain != null)
                    win.Owner = existingMain;
                else
                    win.ShowInTaskbar = true; // تظهر في الـ Taskbar لوحدها

                win.Show();
                win.Activate();
                win.Focus();
                win.Topmost = true;
                win.Topmost = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenStandaloneWindow Error: {ex.Message}");
            }
        }
        private void NavigateTo(string pageUri)
        {
            var mainWin = WPFApplication.Current.MainWindow as MainWindow;
            mainWin?.PagesNavigation?.Navigate(new Uri(pageUri, UriKind.RelativeOrAbsolute));
        }

        // ✅ بدل تكرار البحث عن MainWindow Visible
        private MainWindow GetExistingVisibleMainWindow()
        {
            foreach (Window win in WPFApplication.Current.Windows)
            {
                if (win is MainWindow mw && mw.IsVisible)
                    return mw;
            }
            return null;
        }

        public void UpdateTrayIconBusyState(bool isBusy)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = isBusy
                    ? $"{GetString("AppName", "Dreams")} - ⏳ Busy"
                    : GetString("AppName", "Dreams Software");
            }
        }

        // ✅ النسخة المحسنة من ShowMainWindow
        private void ShowMainWindow()
        {
            WPFApplication.Current.Dispatcher.Invoke(() =>
            {
                EnsureMainWindowReady();
            });
        }

        /// <summary>
        /// ✅ يضمن وجود MainWindow حية ومعروضة، أو ينشئ واحدة جديدة
        /// </summary>
        private void EnsureMainWindowReady()
        {
            MainWindow mainWindow = null;

            // ✅ ابحث عن نسخة حية في Windows Collection
            foreach (Window win in WPFApplication.Current.Windows)
            {
                if (win is MainWindow mw)
                {
                    mainWindow = mw;
                    break;
                }
            }

            // ✅ لو مفيش نسخة حية → اعمل واحدة جديدة (السيناريو: التطبيق كان مقفول خالص)
            if (mainWindow == null)
            {
                mainWindow = new MainWindow();
                WPFApplication.Current.MainWindow = mainWindow;
                mainWindow.Show();
            }
            else
            {
                WPFApplication.Current.MainWindow = mainWindow;

                // ✅ لو متخفية (Hidden) → اظهرها
                if (!mainWindow.IsVisible)
                    mainWindow.Show();
            }

            // ✅ لو مصغرة → ارجعها لحالتها العادية
            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;

            mainWindow.ShowInTaskbar = true;
            mainWindow.Activate();
            mainWindow.Focus();

            // ✅ trick لإحضارها للمقدمة
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ PUBLIC METHODS
        // ═════════════════════════════════════════════════════════════════

        #region Public Methods

        public void UpdateLanguage()
        {
            if (_notifyIcon != null)
                _notifyIcon.Text = GetString("AppName", "Dreams Software");
        }

        public void SetVisible(bool isVisible)
        {
            if (_notifyIcon != null)
                _notifyIcon.Visible = isVisible;
        }

        public void ShowNotification(string titleKey, string messageKey, ToolTipIcon icon = ToolTipIcon.Info, int durationMs = 3000)
        {
            _notifyIcon?.ShowBalloonTip(durationMs,
                GetString(titleKey, titleKey),
                GetString(messageKey, messageKey),
                icon);
        }

        private string GetString(string key, string fallback)
        {
            try { return WPFApplication.Current.TryFindResource(key) as string ?? fallback; }
            catch { return fallback; }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ DISPOSAL
        // ═════════════════════════════════════════════════════════════════

        #region Disposal

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.MouseClick -= OnTrayIconMouseClick;
                _notifyIcon.DoubleClick -= OnTrayIconDoubleClick;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }

        #endregion
    }
}