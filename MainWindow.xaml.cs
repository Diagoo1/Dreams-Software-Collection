using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dreams.Pages;
using Dreams.Themes;
using System.Threading;

namespace Dreams
{
    ///------ Main application window - Controls page navigation and theme management ------
    public partial class MainWindow : Window
    {
        #region ==================== FIELDS ====================

        ///------ Current theme state (Light/Dark) ------
        private bool _isDarkMode;

        ///------ Flag to prevent operations during window closing ------
        private bool _isClosing;

        ///------ Dynamic message keys for each page ------
        private static readonly Dictionary<string, string[]> PageMessageKeys = new()
        {
            { "Dashboard", new[]
                {
                    "MsgHome_01", "MsgHome_02", "MsgHome_03", "MsgHome_04",
                    "MsgHome_05", "MsgHome_06", "MsgHome_07", "MsgHome_08"
                }
            },
            { "Installer", new[]
                {
                    "MsgInstall_01", "MsgInstall_02", "MsgInstall_03", "MsgInstall_04",
                    "MsgInstall_05", "MsgInstall_06", "MsgInstall_07", "MsgInstall_08"
                }
            },
            { "App Store", new[]
                {
                    "MsgStore_01", "MsgStore_02", "MsgStore_03", "MsgStore_04",
                    "MsgStore_05", "MsgStore_06", "MsgStore_07", "MsgStore_08"
                }
            },
            { "Optimizer", new[]
                {
                    "MsgOptimize_01", "MsgOptimize_02", "MsgOptimize_03", "MsgOptimize_04",
                    "MsgOptimize_05", "MsgOptimize_06", "MsgOptimize_07", "MsgOptimize_08"
                }
            },
            { "Tweaks", new[]
                {
                    "MsgTweaks_01", "MsgTweaks_02", "MsgTweaks_03", "MsgTweaks_04",
                    "MsgTweaks_05", "MsgTweaks_06", "MsgTweaks_07", "MsgTweaks_08"
                }
            },
            { "DNS", new[]
                {
                    "MsgDNS_01", "MsgDNS_02", "MsgDNS_03", "MsgDNS_04",
                    "MsgDNS_05", "MsgDNS_06", "MsgDNS_07", "MsgDNS_08"
                }
            }
        };

        ///------ Random number generator for selecting messages ------
        private static readonly Random _rnd = new();
        private CancellationTokenSource _typingCancellation = null;

        #endregion


        #region ==================== CONSTRUCTOR ====================

        ///------ Constructor - Initialize components and bind events ------
        public MainWindow()
        {
            InitializeComponent();

            // 1. تطبيق الثيم واللغة أولاً
            _isDarkMode = ThemeManager.IsDarkMode;
            ApplyTheme(_isDarkMode);
            ApplySavedOpacity();

            // 2. ربط الأحداث بما فيها حدث تغيير اللغة الجديد
            ThemeManager.ThemeChanged += OnThemeChanged;
            ThemeManager.OpacityChanged += OnOpacityChanged;
            App.LanguageChanged += OnLanguageChanged; // سنضيف هذه الدالة في الخطوة القادمة

            if (FindName("btnTheme") is Button themeBtn)
                themeBtn.Content = _isDarkMode ? "\uE706" : "\uE708";

            // 3. تحميل الصفحة واللقب بعد التأكد من تحميل الموارد
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PagesNavigation.Content == null)
                {
                    PagesNavigation.Content = new HomePage();
                    UpdatePageTitle("Dashboard"); // الآن سيجد الترجمة الصحيحة
                }

                var homeButton = MenuStack.Children.OfType<RadioButton>().FirstOrDefault();
                if (homeButton != null)
                {
                    homeButton.IsChecked = true;
                    MoveIndicatorTo(homeButton, false);
                }
            }), DispatcherPriority.Background);
        }

        #endregion


        #region ==================== THEME & OPACITY HANDLERS ====================

        ///------ Theme change handler - Updates when toggling Light/Dark mode ------
        private void OnThemeChanged(bool isDark)
        {
            if (_isClosing) return;
            Dispatcher.Invoke(() =>
            {
                _isDarkMode = isDark;
                ApplyTheme(_isDarkMode);
                if (FindName("btnTheme") is Button themeBtn)
                    themeBtn.Content = _isDarkMode ? "\uE706" : "\uE708";
            });
        }

        ///------ Opacity change handler - Updates when window opacity is modified ------
        private void OnOpacityChanged(double opacity)
        {
            if (_isClosing) return;
            Dispatcher.Invoke(() => Opacity = opacity);
        }

        ///------ Apply theme to window resources ------
        private void ApplyTheme(bool isDark)
        {
            try
            {
                Resources.MergedDictionaries.Clear();
                ThemeManager.SetTheme(isDark);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyTheme error: {ex.Message}");
            }
        }

        ///------ Apply saved opacity value from settings ------
        private void ApplySavedOpacity()
        {
            try
            {
                Opacity = ThemeManager.CurrentOpacity;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying opacity: {ex.Message}");
            }
        }
        private void OnLanguageChanged(string langCode)
        {
            // الحصول على اسم الصفحة الحالية بناءً على الصفحة المفتوحة في الفريم
            string currentKey = "Dashboard";
            if (PagesNavigation.Content is InstallPage) currentKey = "Installer";
            else if (PagesNavigation.Content is OnlinePage) currentKey = "App Store";
            else if (PagesNavigation.Content is OptimizePage) currentKey = "Optimizer";
            else if (PagesNavigation.Content is TweaksPage) currentKey = "Tweaks";
            else if (PagesNavigation.Content is DnsPage) currentKey = "DNS";

            UpdatePageTitle(currentKey);
        }
        #endregion


        #region ==================== WINDOW MANAGEMENT ====================

        ///------ Request safe window close - Checks for ongoing operations ------
        public async void RequestClose()
        {
            // ✅ إذا كان التراي مفعلاً ← اخفِ النافذة فقط
            if (App.IsTrayEnabled && App.TrayManager != null)
            {
                this.Hide();
                return;
            }

            // ✅ إذا كان التراي غير مفعل ← أغلق البرنامج بالكامل
            bool canClose = true;

            if (PagesNavigation?.Content is InstallPage installPage)
                canClose = await installPage.RequestSafeExitAsync();
            else if (PagesNavigation?.Content is OptimizePage optimizePage)
                canClose = await optimizePage.RequestSafeExitAsync();
            else if (PagesNavigation?.Content is OnlinePage onlinePage)
                canClose = await onlinePage.RequestSafeExitAsync();

            if (canClose)
            {
                _isClosing = true;
                Cleanup();
                Application.Current.Shutdown();
            }
        }

        ///------ Request force close with user confirmation ------
        public async Task<bool> RequestForceCloseAsync()
        {
            bool canClose = true;

            if (PagesNavigation?.Content is InstallPage installPage)
                canClose = await installPage.RequestSafeExitAsync();
            else if (PagesNavigation?.Content is OptimizePage optimizePage)
                canClose = await optimizePage.RequestSafeExitAsync();
            else if (PagesNavigation?.Content is OnlinePage onlinePage)
                canClose = await onlinePage.RequestSafeExitAsync();

            if (canClose)
            {
                _isClosing = true;
                Cleanup();
                Application.Current.Shutdown();
                return true;
            }

            return false;
        }

        ///------ Window drag handler - Allows dragging from non-interactive areas ------
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement fe)
            {
                DependencyObject current = fe;
                while (current != null)
                {
                    if (current is Border border && (border.Name?.Contains("card") == true || border.Name == "cardCustom"))
                    {
                        return;
                    }
                    if (current is Button || current is TextBox || current is ComboBox ||
                        current is CheckBox || current is RadioButton || current is Thumb || current is ToggleButton)
                    {
                        return;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
                DragMove();
        }

        ///------ Close window ------
        private void btnClose_Click(object sender, RoutedEventArgs e) => RequestClose();

        ///------ Maximize/Restore window with dynamic button icon update ------
        private void btnRestore_Click(object sender, RoutedEventArgs e)
        {
            var newState = (WindowState == WindowState.Normal)
                ? WindowState.Maximized
                : WindowState.Normal;

            WindowState = newState;

            ///------ Update button icon based on window state ------
            if (sender is Button btn)
            {
                btn.Content = newState == WindowState.Maximized
                    ? "\uE923"
                    : "\uE922";

                btn.ToolTip = newState == WindowState.Maximized
                    ? "Restore Down"
                    : "Maximize";
            }
        }

        ///------ Minimize window ------
        private void btnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        ///------ Toggle Light/Dark theme ------
        private void ToggleTheme(object sender, RoutedEventArgs e)
        {
            ThemeManager.ToggleTheme();
        }

        ///------ Toggle Always on Top mode ------
        private void ToggleAlwaysOnTop(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;

            if (sender is Button btn)
            {
                btn.Content = Topmost ? "\uE718" : "\uE840";

                var brushKey = Topmost ? "DynamicAccent" : "DynamicMainText";
                if (TryFindResource(brushKey) is SolidColorBrush brush)
                    btn.Foreground = brush;

                btn.ToolTip = Topmost
                    ? "Always on Top: ON"
                    : "Always on Top: OFF";
            }
        }

        #endregion


        #region ==================== NAVIGATION ====================

        ///------ Navigate to Home page ------
        private void Home_btn(object sender, RoutedEventArgs e)
        {
            // ✅ لو نفس الصفحة مفتوحة بالفعل، متعملش حاجة
            if (PagesNavigation.Content is HomePage) return;

            PagesNavigation.Navigate(new Uri("Pages/HomePage.xaml", UriKind.RelativeOrAbsolute));
            UpdatePageTitle("Dashboard");
            MoveIndicatorTo(sender as RadioButton);
        }

        ///------ Navigate to Install page ------
        private void Install_btn(object sender, RoutedEventArgs e)
        {
            // ✅ لو نفس الصفحة مفتوحة بالفعل، متعملش حاجة
            if (PagesNavigation.Content is InstallPage) return;

            PagesNavigation.Navigate(new Uri("Pages/InstallPage.xaml", UriKind.RelativeOrAbsolute));
            UpdatePageTitle("Installer");
            MoveIndicatorTo(sender as RadioButton);
        }

        ///------ Navigate to Online page ------
        private void Online_btn(object sender, RoutedEventArgs e)
        {
            // ✅ لو نفس الصفحة مفتوحة بالفعل، متعملش حاجة
            if (PagesNavigation.Content is OnlinePage) return;

            PagesNavigation.Navigate(new Uri("Pages/OnlinePage.xaml", UriKind.RelativeOrAbsolute));
            UpdatePageTitle("App Store");
            MoveIndicatorTo(sender as RadioButton);
        }

        ///------ Navigate to Optimize page ------
        private void Optimize_btn(object sender, RoutedEventArgs e)
        {
            // ✅ لو نفس الصفحة مفتوحة بالفعل، متعملش حاجة
            if (PagesNavigation.Content is OptimizePage) return;

            PagesNavigation.Navigate(new Uri("Pages/OptimizePage.xaml", UriKind.RelativeOrAbsolute));
            UpdatePageTitle("Optimizer");
            MoveIndicatorTo(sender as RadioButton);
        }

        ///------ Navigate to Tweaks page ------
        private void Tweaks_btn(object sender, RoutedEventArgs e)
        {
            // ✅ لو نفس الصفحة مفتوحة بالفعل، متعملش حاجة
            if (PagesNavigation.Content is TweaksPage) return;

            PagesNavigation.Navigate(new Uri("Pages/TweaksPage.xaml", UriKind.RelativeOrAbsolute));
            UpdatePageTitle("Tweaks");
            MoveIndicatorTo(sender as RadioButton);
        }

        ///------ Navigate to DNS page ------
        private void DNS_btn(object sender, RoutedEventArgs e)
        {
            // ✅ لو نفس الصفحة مفتوحة بالفعل، متعملش حاجة
            if (PagesNavigation.Content is DnsPage) return;

            PagesNavigation.Navigate(new Uri("Pages/DnsPage.xaml", UriKind.RelativeOrAbsolute));
            UpdatePageTitle("DNS");
            MoveIndicatorTo(sender as RadioButton);
        }

        ///------ Update page title with typewriter animation (cancellable for fast navigation) ------
        public async void UpdatePageTitle(string pageKey)
        {
            if (FindName("lblCurrentPage") is not TextBlock lbl) return;

            if (_typingCancellation != null)
            {
                _typingCancellation.Cancel();
                _typingCancellation.Dispose();
            }
            _typingCancellation = new CancellationTokenSource();
            var token = _typingCancellation.Token;

            // ✅ نص افتراضي: اسم الصفحة (يُستخدم فقط لو مفيش رسائل عشوائية)
            string translatedPageName = TryFindResource(pageKey) as string ?? pageKey;
            string fullText = translatedPageName;

            // ✅ لو فيه رسائل عشوائية للقسم ده - اعرض الرسالة فقط بدون اسم القسم والشرطة
            if (PageMessageKeys.TryGetValue(pageKey, out var keys) && keys?.Length > 0)
            {
                var randomKey = keys[_rnd.Next(keys.Length)];
                string randomMsg = TryFindResource(randomKey) as string ?? "";

                if (!string.IsNullOrEmpty(randomMsg))
                    fullText = randomMsg;   // 🎯 الرسالة لوحدها فقط
            }

            try
            {
                lbl.BeginAnimation(TextBlock.TextProperty, null);
                lbl.Text = "";
                lbl.Opacity = 1;

                for (int i = 0; i <= fullText.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    lbl.Text = fullText.Substring(0, i);
                    await Task.Delay(30, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_typingCancellation != null && _typingCancellation.Token == token)
                    _typingCancellation = null;
            }
        }


        private async Task BlinkCursor(TextBlock lbl)
        {
            for (int i = 0; i < 3; i++)
            {
                if (_isClosing) break;
                lbl.Text += "|";
                await Task.Delay(300);
                if (lbl.Text.EndsWith("|"))
                    lbl.Text = lbl.Text.Substring(0, lbl.Text.Length - 1);
                await Task.Delay(200);
            }
        }

        ///------ Handle About button - Opens dialog without sidebar selection ------
        private void About_btn_Preview(object sender, MouseButtonEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                e.Handled = true;
                rb.IsChecked = false;
            }
            new About { Owner = this }.ShowDialog();
        }

        ///------ Handle Settings button - Opens dialog without sidebar selection ------
        private void Settings_btn_Preview(object sender, MouseButtonEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                e.Handled = true;
                rb.IsChecked = false;
            }
            new Settings { Owner = this }.ShowDialog();
        }

        #region ==================== EXTERNAL NAVIGATION (للتراي) ====================

        /// <summary>
        /// ✅ تنقل خارجي (من التراي مثلاً) — يحدّث السايد بار والإندكيتور والعنوان
        /// </summary>
        /// <param name="action">اسم القسم: Dashboard / Installer / AppStore / Optimizer / Tweaks / DNS</param>
        public void NavigateFromExternal(string action)
        {
            // ✅ خريطة الأقسام: index في MenuStack + Uri + العنوان
            var navMap = new Dictionary<string, (int Index, string Uri, string TitleKey)>
    {
        { "Dashboard", (0, "Pages/HomePage.xaml",     "Dashboard") },
        { "Installer", (1, "Pages/InstallPage.xaml",  "Installer") },
        { "AppStore",  (2, "Pages/OnlinePage.xaml",   "App Store") },
        { "Optimizer", (3, "Pages/OptimizePage.xaml", "Optimizer") },
        { "Tweaks",    (4, "Pages/TweaksPage.xaml",   "Tweaks") },
        { "DNS",       (5, "Pages/DnsPage.xaml",      "DNS") }
    };

            if (!navMap.TryGetValue(action, out var info)) return;

            // ✅ 1) منع التكرار: لو نفس الصفحة بالفعل مفتوحة
            bool sameAlreadyOpen =
                (action == "Dashboard" && PagesNavigation.Content is HomePage) ||
                (action == "Installer" && PagesNavigation.Content is InstallPage) ||
                (action == "AppStore" && PagesNavigation.Content is OnlinePage) ||
                (action == "Optimizer" && PagesNavigation.Content is OptimizePage) ||
                (action == "Tweaks" && PagesNavigation.Content is TweaksPage) ||
                (action == "DNS" && PagesNavigation.Content is DnsPage);

            // ✅ حتى لو نفس الصفحة، نتأكد إن السايد بار متظبط (مهم لو المستخدم كان فاتح Settings/About)
            var radios = MenuStack.Children.OfType<RadioButton>().ToList();
            if (info.Index < radios.Count)
            {
                var targetRadio = radios[info.Index];

                // ✅ 2) تعليم الزرار الصحيح (هيشيل التحديد من القديم تلقائياً)
                targetRadio.IsChecked = true;

                // ✅ 3) تحريك الإندكيتور
                // BeginInvoke عشان نضمن إن الـ Layout اتحدث قبل القياس
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MoveIndicatorTo(targetRadio, animate: true);
                }), DispatcherPriority.Loaded);
            }

            // ✅ 4) التنقل + تحديث العنوان (لو الصفحة مش مفتوحة بالفعل)
            if (!sameAlreadyOpen)
            {
                PagesNavigation.Navigate(new Uri(info.Uri, UriKind.RelativeOrAbsolute));
                UpdatePageTitle(info.TitleKey);
            }
        }

        #endregion

        #endregion


        #region ==================== INDICATOR ANIMATION ====================

        ///------ After content rendered - Initialize indicator and bind size events ------
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                ///------ Position indicator on Home at startup ------
                var firstButton = MenuStack.Children.OfType<RadioButton>().FirstOrDefault();
                if (firstButton != null)
                    MoveIndicatorTo(firstButton, animate: false);

                ///------ Update indicator position when sidebar resizes ------
                sideBar.SizeChanged += (s, ev) =>
                {
                    var checkedBtn = MenuStack.Children.OfType<RadioButton>()
                                              .FirstOrDefault(rb => rb.IsChecked == true);
                    if (checkedBtn != null)
                        MoveIndicatorTo(checkedBtn, animate: false);
                };

                ///------ Update indicator position when window resizes ------
                this.SizeChanged += (s, ev) =>
                {
                    var checkedBtn = MenuStack.Children.OfType<RadioButton>()
                                              .FirstOrDefault(rb => rb.IsChecked == true);
                    if (checkedBtn != null)
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Render,
                            new Action(() => MoveIndicatorTo(checkedBtn, animate: false)));
                };

                ///------ Update maximize button icon when window state changes ------
                this.StateChanged += (s, ev) =>
                {
                    if (FindName("btnRestore") is Button restoreBtn)
                    {
                        restoreBtn.Content = WindowState == WindowState.Maximized
                            ? "\uE923"
                            : "\uE922";
                        restoreBtn.ToolTip = WindowState == WindowState.Maximized
                            ? "Restore Down"
                            : "Maximize";
                    }
                };
            }));
        }

        ///------ Animate indicator to selected button with smooth transition ------
        public void MoveIndicatorTo(RadioButton target, bool animate = true)
        {
            if (target == null) return;

            ///------ Use fixed height if ActualHeight not yet available ------
            double indicatorHeight = ActiveIndicator.ActualHeight > 0
                ? ActiveIndicator.ActualHeight
                : 24;

            var transform = target.TransformToAncestor(sideBar);
            var pos = transform.Transform(new Point(0, 0));
            double targetY = pos.Y + (target.ActualHeight / 2.0) - (indicatorHeight / 2.0);

            if (animate)
            {
                var anim = new DoubleAnimation
                {
                    To = targetY,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                IndicatorTransform.BeginAnimation(TranslateTransform.YProperty, anim);
            }
            else
            {
                IndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
                IndicatorTransform.Y = targetY;
            }
        }

        #endregion


        #region ==================== CLEANUP ====================

        ///------ On window closing - Handle tray behavior ------
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // ✅ لو التراي مفعل → مش بنقفل، بنخبي بس
            if (App.IsTrayEnabled && App.TrayManager != null)
            {
                e.Cancel = true;        // ✅ إلغاء الإغلاق
                this.Hide();            // ✅ إخفاء بدل إغلاق
                return;
            }

            // ✅ لو التراي مش مفعل → إغلاق عادي
            base.OnClosing(e);
        }

        ///------ On window closed - Release resources ------
        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            Cleanup();
            base.OnClosed(e);
        }

        ///------ Release events and resources to prevent memory leaks ------
        private void Cleanup()
        {
            try
            {
                _typingCancellation?.Cancel();
                _typingCancellation?.Dispose();
                ThemeManager.ThemeChanged -= OnThemeChanged;
                ThemeManager.OpacityChanged -= OnOpacityChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cleanup error: {ex.Message}");
            }
        }

        #endregion
    }
}