// ============================================================
// ملف: TrayMenu.xaml.cs
// المسار: Dreams/TrayMenu.xaml.cs
// ============================================================

using System;
using System.Windows;
using System.Windows.Threading;

namespace Dreams
{
    public partial class TrayMenu : Window
    {
        public TrayMenu()
        {
            InitializeComponent();
        }

        // ═════════════════════════════════════════════════════════════════
        // ███ WINDOW EVENT HANDLERS (✅ النسخة المحسنة)
        // ═════════════════════════════════════════════════════════════════

        #region Window Event Handlers

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // ✅ تأخير بسيط عشان مش يتقفل لو بيفتح dialog
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // ✅ تأكد إن مفيش dialog مفتوح
                bool hasActiveDialog = false;
                foreach (Window win in System.Windows.Application.Current.Windows)
                {
                    if (win != this && win.IsActive && win.IsVisible)
                    {
                        hasActiveDialog = true;
                        break;
                    }
                }

                if (!hasActiveDialog && this.IsLoaded)
                    this.Close();

            }), DispatcherPriority.Background);
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ MENU BUTTON HANDLERS
        // ═════════════════════════════════════════════════════════════════

        #region Menu Button Handlers

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("Dashboard");
            this.Close();
        }

        private void Optimize_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("Optimizer");
            this.Close();
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("Installer");
            this.Close();
        }
        private void AppStore_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("AppStore");
            this.Close();
        }
        private void DNS_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("DNS");
            this.Close();
        }
        private void Tweaks_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("Tweaks");
            this.Close();
        }
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("Settings");
            this.Close();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            App.TrayManager.TriggerAction("About");
            this.Close();
        }

        private async void Exit_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow main)
            {
                // ✅ نستخدم الـ Dispatcher عشان الـ async يشتغل صح
                await main.Dispatcher.InvokeAsync(async () =>
                {
                    await main.RequestForceCloseAsync();
                });
            }
            else
            {
                // لو مفيش ويندو → نخرج مباشرة
                App.ExitApplication();
            }
        }

        #endregion
    }
}