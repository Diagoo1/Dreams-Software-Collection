// ═══════════════════════════════════════════════════════════════════════
// FILE: Pages/DnsPage.xaml.cs - MERGED: Transparency + Polished Dialogs
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Management;
using Dreams.Themes;
#nullable enable

namespace Dreams.Pages
{
    public partial class DnsPage : Page
    {
        private string _selectedProviderName = "";
        private string _selectedPrimaryDNS = "";
        private string _selectedSecondaryDNS = "";
        private bool _isTesting = false;
        private string _activePrimaryDNS = "";
        private string _activeSecondaryDNS = "";
        private string _activeProviderName = "";
        private bool _isCustomActive = false;
        private long _activeLatency = 0;

        private readonly Dictionary<string, (string Primary, string Secondary,
            string ResourceKey, string FilterCategory,
            string FeatureResourceKey, string LatencyResourceKey)> _dnsProviders = new()
        {
            { "Google",     ("8.8.8.8",         "8.8.4.4",
                             "DnsProvider_Google_Name",
                             "Recommended,Fast",
                             "DnsProvider_Google_Desc",
                             "DnsProvider_Google_Ip"     ) },
            { "Cloudflare", ("1.1.1.1",          "1.0.0.1",
                             "DnsProvider_Cloudflare_Name",
                             "Recommended,Fast,Privacy,Security",
                             "DnsProvider_Cloudflare_Desc",
                             "DnsProvider_Cloudflare_Ip" ) },
            { "OpenDNS",    ("208.67.222.222",   "208.67.220.220",
                             "DnsProvider_OpenDns_Name",
                             "Security",
                             "DnsProvider_OpenDns_Desc",
                             "DnsProvider_OpenDns_Ip"    ) },
            { "Quad9",      ("9.9.9.9",          "149.112.112.112",
                             "DnsProvider_Quad9_Name",
                             "Security,Privacy",
                             "DnsProvider_Quad9_Desc",
                             "DnsProvider_Quad9_Ip"      ) },
            { "AdGuard",    ("94.140.14.14",     "94.140.15.15",
                             "DnsProvider_AdGuard_Name",
                             "Privacy,Security",
                             "DnsProvider_AdGuard_Desc",
                             "DnsProvider_AdGuard_Ip"    ) },
            { "DHCP",       ("Auto",             "Auto",
                             "DnsProvider_Dhcp_Name",
                             "All",
                             "DnsProvider_Dhcp_Desc",
                             "DnsProvider_Dhcp_Ip"       ) },
        };

        public DnsPage()
        {
            InitializeComponent();
            Loaded += DnsPage_Loaded;
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        private async void DnsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await GetCurrentActiveDNS();
            await UpdateActiveDNSInfo();
            UpdateCurrentDNSDisplay();
            SetActiveDNSIndicator();
            await TestAllServersLatency();
        }

        private void OnThemeChanged(bool isDarkMode)
        {
            Dispatcher.Invoke(() =>
            {
                ResetAllCardBorders();
                SetActiveDNSIndicator();
            });
        }

        // ══════════════════════════════════════════════
        //  CARD HOVER EFFECTS
        // ══════════════════════════════════════════════

        private void Card_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Border card) return;
            string cardName = card.Tag?.ToString() ?? "";
            if (IsActiveCard(cardName)) return;
            card.BorderBrush = GetBrush("DynamicAccent");
            card.BorderThickness = new Thickness(1.5);
        }

        private void Card_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not Border card) return;
            string cardName = card.Tag?.ToString() ?? "";
            if (IsActiveCard(cardName))
            {
                card.BorderBrush = GetBrush("DynamicSuccess");
                card.BorderThickness = new Thickness(2);
            }
            else
            {
                card.SetResourceReference(Border.BorderBrushProperty, "DynamicBorder");
                card.BorderThickness = new Thickness(1.5);
            }
        }

        private bool IsActiveCard(string cardName)
        {
            if (_isCustomActive) return false;
            return cardName == _activeProviderName;
        }

        // ══════════════════════════════════════════════
        //  DNS DETECTION
        // ══════════════════════════════════════════════

        private async Task GetCurrentActiveDNS()
        {
            await Task.Run(() =>
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["DNSServerSearchOrder"] is string[] servers && servers.Length > 0)
                        {
                            _activePrimaryDNS = servers[0];
                            _activeSecondaryDNS = servers.Length > 1 ? servers[1] : "";
                            return;
                        }
                    }
                }
                catch { _activePrimaryDNS = GetDNSViaNetworkInterface(); }
            });
        }

        private string GetDNSViaNetworkInterface()
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
                var dns = ni?.GetIPProperties().DnsAddresses;
                return dns?.Count > 0 ? dns[0].ToString() : "192.168.1.1";
            }
            catch { return GetStringResource("Text_Unknown", "Unknown"); }
        }

        private async Task UpdateActiveDNSInfo()
        {
            await Task.Run(async () =>
            {
                foreach (var kv in _dnsProviders)
                {
                    if (kv.Key == "DHCP") continue;
                    if (kv.Value.Primary == _activePrimaryDNS)
                    {
                        _activeProviderName = kv.Key;
                        _isCustomActive = false;
                        _activeLatency = await PingDNS(_activePrimaryDNS);
                        return;
                    }
                }

                if (string.IsNullOrEmpty(_activePrimaryDNS)
                    || _activePrimaryDNS == "Auto"
                    || _activePrimaryDNS.StartsWith("192.168")
                    || _activePrimaryDNS.StartsWith("10.")
                    || _activePrimaryDNS == GetStringResource("Text_Unknown", "Unknown"))
                {
                    _activeProviderName = "DHCP";
                    _isCustomActive = false;
                    _activeLatency = 0;
                    return;
                }

                _activeProviderName = "Custom";
                _isCustomActive = true;
                _activeLatency = await PingDNS(_activePrimaryDNS);
            });
        }

        private async Task<long> PingDNS(string ip)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, 2000);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch { return -1; }
        }

        // ══════════════════════════════════════════════
        //  UI UPDATE
        // ══════════════════════════════════════════════

        private void UpdateCurrentDNSDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                string adapter = GetActiveAdapterName();
                txtCurrentDNS.Text = $"{adapter}: {_activePrimaryDNS}";

                if (_activeLatency > 0 && _activeLatency < 999)
                {
                    txtLatency.Text = $"{_activeLatency} ms";
                    txtLatency.Foreground = _activeLatency < 60
                        ? GetBrush("DynamicSuccess")
                        : _activeLatency < 100
                            ? GetBrush("DynamicInfo")
                            : GetBrush("DynamicWarning");
                }
                else if (_activeProviderName == "DHCP")
                {
                    txtLatency.Text = GetStringResource("Text_LatencyIsp", "ISP");
                    txtLatency.Foreground = GetBrush("DynamicSubText");
                }
                else
                {
                    txtLatency.Text = GetStringResource("Text_LatencyPlaceholder", "-- ms");
                    txtLatency.Foreground = GetBrush("DynamicSubText");
                }

                string feature = "";
                if (_dnsProviders.TryGetValue(_activeProviderName, out var p))
                    feature = GetStringResource(p.FeatureResourceKey, p.FeatureResourceKey);
                else if (_isCustomActive)
                    feature = GetStringResource("Text_Custom", "Custom");

                if (string.IsNullOrEmpty(feature))
                    feature = GetStringResource("Text_NotAvailable", "N/A");

                txtAdBlocking.Text = feature;
                txtAdBlocking.Foreground =
                    (feature.Contains("Privacy") || feature.Contains("Protection")
                     || feature.Contains("Filtering") || feature.Contains("Block"))
                    ? GetBrush("DynamicSuccess")
                    : GetBrush("DynamicSubText");
            });
        }

        private string GetActiveAdapterName()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    ?.Name ?? GetStringResource("Text_WiFi", "Wi-Fi");
            }
            catch { return GetStringResource("Text_WiFi", "Wi-Fi"); }
        }

        // ══════════════════════════════════════════════
        //  ACTIVE INDICATOR
        // ══════════════════════════════════════════════

        private void SetActiveDNSIndicator()
        {
            Dispatcher.Invoke(() =>
            {
                ResetAllCardBorders();
                if (_isCustomActive) { ApplyActiveMarker(cardCustom, null); return; }
                switch (_activeProviderName)
                {
                    case "Google": ApplyActiveMarker(cardGoogle, selectedGoogle); break;
                    case "Cloudflare": ApplyActiveMarker(cardCloudflare, selectedCloudflare); break;
                    case "OpenDNS": ApplyActiveMarker(cardOpenDNS, selectedOpenDNS); break;
                    case "Quad9": ApplyActiveMarker(cardQuad9, selectedQuad9); break;
                    case "AdGuard": ApplyActiveMarker(cardAdGuard, selectedAdGuard); break;
                    case "DHCP": ApplyActiveMarker(cardDHCP, selectedDHCP); break;
                }
            });
        }

        private void ApplyActiveMarker(Border card, Border? sideBar)
        {
            if (card == null) return;
            card.BorderBrush = GetBrush("DynamicSuccess");
            card.BorderThickness = new Thickness(2);
        }

        private void ResetAllCardBorders()
        {
            var cards = new[] { cardGoogle, cardCloudflare, cardOpenDNS,
                                cardQuad9, cardAdGuard, cardDHCP, cardCustom };
            foreach (var c in cards)
            {
                if (c == null) continue;
                c.SetResourceReference(Border.BorderBrushProperty, "DynamicBorder");
                c.BorderThickness = new Thickness(1.5);
            }
            var bars = new[] { selectedGoogle, selectedCloudflare, selectedOpenDNS,
                               selectedQuad9, selectedAdGuard, selectedDHCP };
            foreach (var b in bars) if (b != null) b.Visibility = Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════
        //  PING / TEST ALL
        // ══════════════════════════════════════════════

        private async Task TestAllServersLatency()
        {
            if (_isTesting) return;
            _isTesting = true;
            var orig = btnTestAll.Content;
            btnTestAll.Content = GetStringResource("Text_Testing", "Testing...");
            btnTestAll.IsEnabled = false;

            await Task.WhenAll(
                TestAndUpdate("8.8.8.8", pingGoogle, pingBarGoogle),
                TestAndUpdate("1.1.1.1", pingCloudflare, pingBarCloudflare),
                TestAndUpdate("208.67.222.222", pingOpenDNS, pingBarOpenDNS),
                TestAndUpdate("9.9.9.9", pingQuad9, pingBarQuad9),
                TestAndUpdate("94.140.14.14", pingAdGuard, pingBarAdGuard)
            );

            if (!string.IsNullOrEmpty(_activePrimaryDNS) && _activePrimaryDNS != "Auto")
            {
                _activeLatency = await PingDNS(_activePrimaryDNS);
                UpdateCurrentDNSDisplay();
            }

            btnTestAll.Content = orig;
            btnTestAll.IsEnabled = true;
            _isTesting = false;
        }

        private async Task TestAndUpdate(string ip, TextBlock label, Border bar)
        {
            long ms = await PingDNS(ip);
            UpdatePingUI(ms, label, bar);
        }

        private void UpdatePingUI(long ms, TextBlock label, Border bar)
        {
            Dispatcher.Invoke(() =>
            {
                if (ms > 0 && ms < 999)
                {
                    label.Text = $"{ms} ms";
                    Brush color;
                    double width;
                    if (ms < 30) { color = GetBrush("DynamicSuccess"); width = 38; }
                    else if (ms < 60) { color = GetBrush("DynamicSuccess"); width = 30; }
                    else if (ms < 100) { color = GetBrush("DynamicInfo"); width = 20; }
                    else { color = GetBrush("DynamicWarning"); width = 12; }
                    label.Foreground = color;
                    bar.Width = width;
                    bar.Background = color;
                }
                else
                {
                    label.Text = GetStringResource("Text_LatencyTimeout", "Timeout");
                    label.Foreground = GetBrush("DynamicSubText");
                    bar.Width = 0;
                }
            });
        }

        // ══════════════════════════════════════════════
        //  CARD CLICK
        // ══════════════════════════════════════════════

        private async void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border) return;
            string name = border.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return;
            _selectedProviderName = name;

            if (_dnsProviders.TryGetValue(name, out var prov))
            {
                _selectedPrimaryDNS = prov.Primary;
                _selectedSecondaryDNS = prov.Secondary;
                string displayName = GetStringResource(prov.ResourceKey, name);
                bool ok = await ShowConfirmDialogAsync(
                    displayName, _selectedPrimaryDNS, _selectedSecondaryDNS);
                if (ok) await ApplyDNSChange(_selectedPrimaryDNS, _selectedSecondaryDNS);
            }
        }

        // ══════════════════════════════════════════════
        //  CUSTOM CARD
        // ══════════════════════════════════════════════

        private async void CardCustom_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var border = sender as Border;
            border?.CaptureMouse();
            await OpenCustomDnsDialog();
            border?.ReleaseMouseCapture();
        }

        private async Task OpenCustomDnsDialog()
        {
            try
            {
                var dialog = new CustomDNSInputDialogWindow
                {
                    Owner = Window.GetWindow(this)
                };
                bool? result = dialog.ShowDialog();
                if (result == true)
                {
                    _selectedProviderName = "Custom";
                    _selectedPrimaryDNS = dialog.PrimaryDNS;
                    _selectedSecondaryDNS = dialog.SecondaryDNS;
                    bool ok = await ShowConfirmDialogAsync(
                        GetStringResource("Text_Custom", "Custom DNS"),
                        _selectedPrimaryDNS, _selectedSecondaryDNS);
                    if (ok)
                        await ApplyDNSChange(_selectedPrimaryDNS, _selectedSecondaryDNS);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error: {ex.Message}");
                await ShowErrorMessage(
                    GetStringResource("Msg_DnsFailed_Title", "Error"),
                    $"Failed to open Custom DNS dialog:\n{ex.Message}");
            }
        }

        private void CardCustom_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isCustomActive) return;
            cardCustom.BorderBrush = GetBrush("DynamicAccent");
            cardCustom.BorderThickness = new Thickness(1.5);
        }

        private void CardCustom_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isCustomActive)
            {
                cardCustom.BorderBrush = GetBrush("DynamicSuccess");
                cardCustom.BorderThickness = new Thickness(2);
            }
            else
            {
                cardCustom.SetResourceReference(Border.BorderBrushProperty, "DynamicBorder");
                cardCustom.BorderThickness = new Thickness(1.5);
            }
        }

        // ══════════════════════════════════════════════
        //  APPLY DNS
        // ══════════════════════════════════════════════

        private async Task ApplyDNSChange(string primary, string secondary)
        {
            bool success = await Task.Run(() =>
            {
                try
                {
                    if (primary == "Auto")
                    {
                        ExecuteNetsh("interface ip set dns name=* source=dhcp");
                        FlushDNSCache();
                        return true;
                    }
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
                    bool applied = false;
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string adapterName = GetAdapterNameFromConfig(obj);
                        if (string.IsNullOrEmpty(adapterName)) continue;
                        ExecuteNetsh(
                            $"interface ip set dns name=\"{adapterName}\" static {primary} primary");
                        if (!string.IsNullOrEmpty(secondary))
                            ExecuteNetsh(
                                $"interface ip add dns name=\"{adapterName}\" {secondary} index=2");
                        applied = true;
                    }
                    FlushDNSCache();
                    return applied;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ApplyDNSChange error: {ex.Message}");
                    return false;
                }
            });

            if (primary == "Auto")
            {
                _activePrimaryDNS = "Auto";
                _activeSecondaryDNS = "Auto";
                _activeProviderName = "DHCP";
                _isCustomActive = false;
                _activeLatency = 0;
            }
            else
            {
                _activePrimaryDNS = primary;
                _activeSecondaryDNS = secondary;
                _activeProviderName = _selectedProviderName;
                _isCustomActive = (_selectedProviderName == "Custom");
                _activeLatency = await PingDNS(primary);
            }

            SetActiveDNSIndicator();
            UpdateCurrentDNSDisplay();

            if (success)
            {
                string display = _selectedProviderName == "Custom"
                    ? GetStringResource("Text_Custom", "Custom DNS")
                    : GetStringResource(
                        _dnsProviders[_selectedProviderName].ResourceKey,
                        _selectedProviderName);

                // This assumes the resource string "Msg_DnsChanged_Body" is now correct
                // and has only one \n where a line break is needed.
                string formatText = GetStringResource("Msg_DnsChanged_Body", "DNS changed to {0}!\n\nPrimary: {1}\nSecondary: {2}").Replace("\\n", "\n");
                string secondaryText = string.IsNullOrEmpty(secondary) ? GetStringResource("Dialog_LabelNone", "None") : secondary;

                string msg = primary == "Auto"
                    ? GetStringResource("Msg_DnsReset_Body", "DNS has been reset to automatic (DHCP).")
                    : string.Format(formatText, display, primary, secondaryText);

                await ShowSuccessMessage(
                    GetStringResource("Msg_DnsChanged_Title", "DNS Changed Successfully"),
                    msg);
            }
            else
            {
                await ShowErrorMessage(
                    GetStringResource("Msg_DnsFailed_Title", "Failed"),
                    GetStringResource("Msg_DnsFailed_Body",
                        "Could not apply DNS settings.\nMake sure you are running as Administrator."));
            }
        }

        private void ExecuteNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    Verb = "runas"
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch (Exception ex) { Debug.WriteLine($"netsh error: {ex.Message}"); }
        }

        private void FlushDNSCache()
        {
            try
            {
                var psi = new ProcessStartInfo("ipconfig", "/flushdns")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch (Exception ex) { Debug.WriteLine($"flushdns error: {ex.Message}"); }
        }

        private string GetAdapterNameFromConfig(ManagementObject obj)
        {
            try
            {
                uint index = (uint)obj["InterfaceIndex"];
                using var niSearcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_NetworkAdapter WHERE InterfaceIndex = {index}");
                foreach (ManagementObject ni in niSearcher.Get())
                    return ni["NetConnectionID"]?.ToString() ?? "";
            }
            catch { }
            return "";
        }

        // ══════════════════════════════════════════════
        //  HEADER BUTTONS
        // ══════════════════════════════════════════════

        private async void btnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            btnFlushDns.IsEnabled = false;
            await Task.Run(FlushDNSCache);
            btnFlushDns.IsEnabled = true;
            await ShowSuccessMessage(
                GetStringResource("Msg_FlushSuccess_Title", "DNS Cache Flushed"),
                GetStringResource("Msg_FlushSuccess_Body",
                    "The DNS resolver cache has been cleared successfully."));
        }

        private async void btnNetworkReset_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ShowConfirmDialogAsync(
                GetStringResource("Btn_NetworkReset", "Network Reset"),
                GetStringResource("Msg_NetworkResetConfirm",
                    "This will reset Winsock and TCP/IP stack.\nContinue?"));
            if (!ok) return;

            btnNetworkReset.IsEnabled = false;
            await Task.Run(() =>
            {
                ExecuteNetsh("winsock reset");
                ExecuteNetsh("int ip reset");
                FlushDNSCache();
            });
            btnNetworkReset.IsEnabled = true;

            // [تعديل] تم تطبيق نفس الحل هنا
            // قمنا بإضافة دالة Replace لإصلاح مشكلة السطر الجديد
            await ShowSuccessMessage(
                GetStringResource("Msg_NetworkReset_Title", "Network Reset Complete"),
                GetStringResource("Msg_NetworkReset_Body",
                    "Winsock and TCP/IP stack have been reset.\n\nA restart may be required for changes to take full effect.")
                    .Replace("\\n", "\n")
            );
        }

        private async void btnTestAll_Click(object sender, RoutedEventArgs e)
            => await TestAllServersLatency();

        // ══════════════════════════════════════════════
        //  FILTER TABS
        // ══════════════════════════════════════════════

        private void TabFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
                ApplyFilter(rb.Tag?.ToString() ?? "All");
        }

        private void ApplyFilter(string filter)
        {
            bool showAll = filter == "All";
            Visibility Vis(string key) =>
                showAll || (_dnsProviders.TryGetValue(key, out var p)
                            && p.FilterCategory.Contains(filter))
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (cardGoogle != null) cardGoogle.Visibility = Vis("Google");
            if (cardCloudflare != null) cardCloudflare.Visibility = Vis("Cloudflare");
            if (cardOpenDNS != null) cardOpenDNS.Visibility = Vis("OpenDNS");
            if (cardQuad9 != null) cardQuad9.Visibility = Vis("Quad9");
            if (cardAdGuard != null) cardAdGuard.Visibility = Vis("AdGuard");
            if (cardDHCP != null) cardDHCP.Visibility = Vis("DHCP");
        }

        // ══════════════════════════════════════════════
        //  RESPONSIVE GRID
        // ══════════════════════════════════════════════

        private void DnsCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DnsCardsContainer == null) return;
            double width = e.NewSize.Width;
            int columns = width switch
            {
                < 600 => 1,
                < 850 => 2,
                < 1350 => 3,
                _ => 4
            };
            if (DnsCardsContainer.Columns != columns)
                DnsCardsContainer.Columns = columns;
        }

        // ══════════════════════════════════════════════
        //  ✅ BUILD DIALOG WINDOW (Transparency Fix)
        // ══════════════════════════════════════════════

        private Window BuildDialogWindow(int w, int h)
        {
            var parentWindow = Window.GetWindow(this);
            double parentOpacity = parentWindow?.Opacity ?? 1.0;
            return new Window
            {
                Width = w,
                Height = h, // [تعديل] الارتفاع هنا قد يكون 0 لكن SizeToContent هو ما سيتحكم في الحجم النهائي
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Opacity = parentOpacity,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Owner = parentWindow,
            };
        }

        // ══════════════════════════════════════════════
        //  ✅ CONFIRM DIALOG (Polished + Grid Info Box)
        //  ✅ الأزرار نزلت لتحت بـ Margin أكبر
        // ══════════════════════════════════════════════

        private async Task<bool> ShowConfirmDialogAsync(
            string providerName, string primary, string secondary)
        {
            var tcs = new TaskCompletionSource<bool>();
            var window = BuildDialogWindow(400, 0); // [تعديل] الارتفاع المبدئي 0
            window.SizeToContent = SizeToContent.Height; // [تعديل] لضبط الارتفاع تلقائياً
            window.MinHeight = 350; // [تعديل] لضمان عدم صغر النافذة بشكل مبالغ فيه

            Brush mainBg = GetBrush("DynamicCardBg",
                                new SolidColorBrush(Color.FromRgb(30, 30, 30)));
            Brush border = GetBrush("DynamicBorder",
                                new SolidColorBrush(Color.FromRgb(60, 60, 60)));
            Brush mainText = GetBrush("DynamicMainText", Brushes.White);
            Brush subText = GetBrush("DynamicSubText", Brushes.Gray);
            Brush accent = GetBrush("DynamicAccent",
                                new SolidColorBrush(Color.FromRgb(14, 165, 233)));
            Brush totalBg = GetBrush("DynamicTotalCardBg",
                                new SolidColorBrush(Color.FromRgb(40, 40, 40)));

            Color stripeColor = Color.FromRgb(14, 165, 233);
            Color bgColor = ((SolidColorBrush)mainBg).Color;

            var bg = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            bg.GradientStops.Add(new GradientStop(stripeColor, 0));
            bg.GradientStops.Add(new GradientStop(stripeColor, 0.018));
            bg.GradientStops.Add(new GradientStop(bgColor, 0.0181));
            bg.GradientStops.Add(new GradientStop(bgColor, 1));

            var outerBorder = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                BorderBrush = border,
                ClipToBounds = true
            };

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

            // Icon
            stack.Children.Add(BuildIconCircle("\uE8D7", stripeColor));

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = GetStringResource("Dialog_ConfirmTitle", "Confirm DNS Change"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = mainText,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 6)
            });

            // Subtitle
            stack.Children.Add(new TextBlock
            {
                Text = string.Format(
                    GetStringResource("Dialog_ConfirmBody",
                        "Switching DNS server to {0}"), providerName),
                FontSize = 12,
                Foreground = subText,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            });

            // ✅ Info Box بـ Grid Layout
            var infoBox = new Border
            {
                Background = totalBg,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 14)
            };

            var infoGrid = new Grid();
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lblPrimary = new TextBlock
            {
                Text = GetStringResource("Dialog_LabelPrimary", "Primary"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = subText,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(lblPrimary, 0); Grid.SetColumn(lblPrimary, 0);

            var valPrimary = new TextBlock
            {
                Text = primary,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = mainText,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(valPrimary, 0); Grid.SetColumn(valPrimary, 1);

            string secVal = string.IsNullOrEmpty(secondary) || secondary == "Auto"
                ? GetStringResource("Dialog_LabelNone", "None")
                : secondary;

            var lblSecondary = new TextBlock
            {
                Text = GetStringResource("Dialog_LabelSecondary", "Secondary"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = subText,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(lblSecondary, 2); Grid.SetColumn(lblSecondary, 0);

            var valSecondary = new TextBlock
            {
                Text = secVal,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                Foreground = string.IsNullOrEmpty(secondary) || secondary == "Auto"
                                        ? subText : mainText,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(valSecondary, 2); Grid.SetColumn(valSecondary, 1);

            infoGrid.Children.Add(lblPrimary);
            infoGrid.Children.Add(valPrimary);
            infoGrid.Children.Add(lblSecondary);
            infoGrid.Children.Add(valSecondary);
            infoBox.Child = infoGrid;
            stack.Children.Add(infoBox);

            // ✅ Warning Note بـ Background خفيف
            var notePanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 245, 158, 11)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 28) // ✅ زودنا الـ Margin لتنزيل الأزرار
            };
            notePanel.Child = new TextBlock
            {
                Text = GetStringResource("Dialog_Warning",
                    "⚠ DNS will change for all active adapters. Cache will be flushed."),
                FontSize = 10,
                Foreground = GetBrush("DynamicWarning",
                    new SolidColorBrush(Color.FromRgb(245, 158, 11))),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            stack.Children.Add(notePanel);

            // ✅ Buttons (نزلت لتحت بـ Margin الإضافي فوق)
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0) // ✅ مسافة إضافية من فوق
            };

            var cancelBtn = BuildDialogButton(
                GetStringResource("Dialog_BtnCancel", "Cancel"),
                Brushes.Transparent, subText, border, isOutline: true);
            cancelBtn.Width = 110;
            cancelBtn.Height = 38;
            cancelBtn.Margin = new Thickness(0, 0, 10, 0);
            cancelBtn.Click += (_, __) => { tcs.TrySetResult(false); window.Close(); };

            var applyBtn = BuildDialogButton(
                GetStringResource("Dialog_BtnApply", "Apply"),
                accent, Brushes.White, accent);
            applyBtn.Width = 110;
            applyBtn.Height = 38;
            applyBtn.Click += (_, __) => { tcs.TrySetResult(true); window.Close(); };

            btns.Children.Add(cancelBtn);
            btns.Children.Add(applyBtn);
            stack.Children.Add(btns);

            outerBorder.Child = stack;
            window.Content = outerBorder;
            AttachDragAndFade(window);
            window.ShowDialog();
            return await tcs.Task;
        }

        private async Task<bool> ShowConfirmDialogAsync(string title, string body)
        {
            var result = await ShowMessageDialog(
                title, body, MessageBoxButton.YesNo, "\uE7BA",
                Color.FromRgb(239, 68, 68));
            return result == MessageBoxResult.Yes;
        }

        private async Task ShowSuccessMessage(string title, string message)
            => await ShowMessageDialog(title, message,
                MessageBoxButton.OK, "\uE73E", Color.FromRgb(34, 197, 94));

        private async Task ShowErrorMessage(string title, string message)
            => await ShowMessageDialog(title, message,
                MessageBoxButton.OK, "\uEB90", Color.FromRgb(220, 53, 69));

        // ══════════════════════════════════════════════
        //  ✅ MESSAGE DIALOG (Polished + Message Border)
        //  ✅ الـ OK Button نزل لتحت
        // ══════════════════════════════════════════════

        private async Task<MessageBoxResult> ShowMessageDialog(
            string title, string message,
            MessageBoxButton buttons, string iconChar, Color iconColor)
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>();
            var window = BuildDialogWindow(400, 0); // [تعديل] الارتفاع المبدئي 0
            window.SizeToContent = SizeToContent.Height; // [تعديل] لضبط الارتفاع تلقائياً
            window.MinHeight = 250; // [تعديل] لضمان عدم صغر النافذة بشكل مبالغ فيه

            Brush mainBg = GetBrush("DynamicCardBg",
                                new SolidColorBrush(Color.FromRgb(30, 30, 30)));
            Brush border = GetBrush("DynamicBorder",
                                new SolidColorBrush(Color.FromRgb(60, 60, 60)));
            Brush mainText = GetBrush("DynamicMainText", Brushes.White);
            Brush subText = GetBrush("DynamicSubText", Brushes.Gray);
            Color bgColor = ((SolidColorBrush)mainBg).Color;

            var bg = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            bg.GradientStops.Add(new GradientStop(iconColor, 0));
            bg.GradientStops.Add(new GradientStop(iconColor, 0.018));
            bg.GradientStops.Add(new GradientStop(bgColor, 0.0181));
            bg.GradientStops.Add(new GradientStop(bgColor, 1));

            var outerBorder = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                BorderBrush = border,
                ClipToBounds = true
            };

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

            stack.Children.Add(BuildIconCircle(iconChar, iconColor));

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = mainText,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10)
            });

            // ✅ Message في Border ملوّن خفيف
            var msgBorder = new Border
            {
                Background = new SolidColorBrush(
                    Color.FromArgb(15, iconColor.R, iconColor.G, iconColor.B)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 30) // ✅ مسافة أكبر لتنزيل الأزرار
            };
            msgBorder.Child = new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = subText,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 18
            };
            stack.Children.Add(msgBorder);

            // ✅ Buttons (نزلوا لتحت)
            var btnsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            if (buttons == MessageBoxButton.YesNo)
            {
                var noBtn = BuildDialogButton(
                    GetStringResource("Dialog_BtnNo", "No"),
                    Brushes.Transparent, subText, border, isOutline: true);
                noBtn.Width = 110;
                noBtn.Height = 38;
                noBtn.Margin = new Thickness(0, 0, 10, 0);
                noBtn.Click += (_, __) =>
                {
                    tcs.TrySetResult(MessageBoxResult.No);
                    window.Close();
                };

                var yesBtn = BuildDialogButton(
                    GetStringResource("Dialog_BtnYes", "Yes"),
                    new SolidColorBrush(iconColor), Brushes.White, Brushes.Transparent);
                yesBtn.Width = 110;
                yesBtn.Height = 38;
                yesBtn.Click += (_, __) =>
                {
                    tcs.TrySetResult(MessageBoxResult.Yes);
                    window.Close();
                };

                btnsPanel.Children.Add(noBtn);
                btnsPanel.Children.Add(yesBtn);
            }
            else
            {
                var okBtn = BuildDialogButton(
                    GetStringResource("Dialog_BtnOk", "OK"),
                    new SolidColorBrush(iconColor), Brushes.White, Brushes.Transparent);
                okBtn.Width = 120;
                okBtn.Height = 38;
                okBtn.Click += (_, __) =>
                {
                    tcs.TrySetResult(MessageBoxResult.OK);
                    window.Close();
                };
                btnsPanel.Children.Add(okBtn);
            }

            stack.Children.Add(btnsPanel);
            outerBorder.Child = stack;
            window.Content = outerBorder;
            AttachDragAndFade(window);
            window.ShowDialog();
            return await tcs.Task;
        }

        // ══════════════════════════════════════════════
        //  SHARED HELPERS
        // ══════════════════════════════════════════════

        private static UIElement BuildIconCircle(string glyph, Color color)
        {
            var circle = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = new SolidColorBrush(
                    Color.FromArgb(38, color.R, color.G, color.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            circle.Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return circle;
        }

        private static Button BuildDialogButton(
            string text, Brush bg, Brush fg, Brush borderBrush, bool isOutline = false)
        {
            var btn = new Button
            {
                Content = text,
                Width = 110,
                Height = 38,
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Background = bg,
                Foreground = fg,
                BorderBrush = borderBrush,
                BorderThickness = isOutline ? new Thickness(1.5) : new Thickness(0),
                Padding = new Thickness(0)
            };

            var tmpl = new ControlTemplate(typeof(Button));
            var bfact = new FrameworkElementFactory(typeof(Border));
            bfact.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            bfact.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bfact.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bfact.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            bfact.AppendChild(cp);
            tmpl.VisualTree = bfact;
            btn.Template = tmpl;

            btn.MouseEnter += (_, __) => btn.Opacity = 0.85;
            btn.MouseLeave += (_, __) => btn.Opacity = 1.0;
            return btn;
        }

        // ✅ Drag + Fade مع دعم الـ Transparency
        private static void AttachDragAndFade(Window window)
        {
            double targetOpacity = window.Opacity;

            window.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                while (src != null && src != window)
                {
                    if (src is Button) return;
                    src = VisualTreeHelper.GetParent(src);
                }
                if (window.WindowState == WindowState.Normal)
                    window.DragMove();
            };

            window.Loaded += (_, __) =>
            {
                var anim = new DoubleAnimation(0, targetOpacity,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                window.BeginAnimation(Window.OpacityProperty, anim);
            };
        }

        private Brush GetBrush(string key, Brush? fallback = null)
        {
            try
            {
                if (TryFindResource(key) is Brush b) return b;
                if (Application.Current?.Resources.Contains(key) == true
                    && Application.Current.Resources[key] is Brush b2) return b2;
            }
            catch { }
            return fallback ?? Brushes.Gray;
        }

        private string GetStringResource(string key, string fallback)
        {
            try
            {
                if (TryFindResource(key) is string s) return s;
                if (Application.Current?.Resources.Contains(key) == true
                    && Application.Current.Resources[key] is string s2) return s2;
            }
            catch { }
            return fallback;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ✅ Custom DNS Dialog Window (Polished + Transparency Support)
    // ══════════════════════════════════════════════════════════════════════
    public class CustomDNSInputDialogWindow : Window
    {
        public string PrimaryDNS { get; private set; } = "";
        public string SecondaryDNS { get; private set; } = "";

        public CustomDNSInputDialogWindow()
        {
            Width = 440;
            SizeToContent = SizeToContent.Height; // [تعديل] لضبط الارتفاع تلقائياً
            MinHeight = 380; // [تعديل] لضمان عدم صغر النافذة بشكل مبالغ فيه
            // Height = 390; // [تعديل] تم تعطيل السطر القديم

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Opacity = 0; // ✅ نبدأ بـ 0 ونعدلها في Loaded

            Color accentColor = Color.FromRgb(14, 165, 233);
            Brush cardBg = GetRes("DynamicCardBg",
                                    new SolidColorBrush(Color.FromRgb(30, 30, 30)));
            Brush borderBr = GetRes("DynamicBorder",
                                    new SolidColorBrush(Color.FromRgb(60, 60, 60)));
            Brush mainText = GetRes("DynamicMainText", Brushes.White);
            Brush subText = GetRes("DynamicSubText", Brushes.Gray);
            Brush hoverBg = GetRes("DynamicHoverBg",
                                    new SolidColorBrush(Color.FromRgb(45, 45, 45)));
            Color bgColor = ((SolidColorBrush)cardBg).Color;

            var bg = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            bg.GradientStops.Add(new GradientStop(accentColor, 0));
            bg.GradientStops.Add(new GradientStop(accentColor, 0.018));
            bg.GradientStops.Add(new GradientStop(bgColor, 0.0181));
            bg.GradientStops.Add(new GradientStop(bgColor, 1));

            var outerBorder = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                BorderBrush = borderBr,
                ClipToBounds = true
            };

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

            // Icon Circle
            var circle = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = new SolidColorBrush(
                    Color.FromArgb(38, accentColor.R, accentColor.G, accentColor.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            circle.Child = new TextBlock
            {
                Text = "\uE710",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = new SolidColorBrush(accentColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(circle);

            stack.Children.Add(new TextBlock
            {
                Text = GetString("CustomDns_Title", "Custom DNS Configuration"),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = mainText,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock
            {
                Text = GetString("CustomDns_Subtitle",
                                        "Enter your preferred DNS server addresses"),
                FontSize = 11,
                Foreground = subText,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Primary DNS
            stack.Children.Add(new TextBlock
            {
                Text = GetString("CustomDns_LabelPrimary", "Primary DNS Server"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = mainText,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var txtPrimary = BuildIPTextBox(hoverBg, borderBr, mainText,
                GetString("CustomDns_PlaceholderPrimary", "e.g. 1.1.1.1"));
            stack.Children.Add(txtPrimary);
            stack.Children.Add(new Border { Height = 12 });

            // Secondary DNS
            stack.Children.Add(new TextBlock
            {
                Text = GetString("CustomDns_LabelSecondary", "Secondary DNS Server"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = mainText,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var txtSecondary = BuildIPTextBox(hoverBg, borderBr, mainText,
                GetString("CustomDns_PlaceholderSecondary", "e.g. 1.0.0.1 (Optional)"));
            stack.Children.Add(txtSecondary);
            // ✅ مسافة أكبر لتنزيل الأزرار
            stack.Children.Add(new Border { Height = 30 });

            // Buttons
            var btnsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var cancelBtn = BuildRoundedCancelButton(
                GetString("Dialog_BtnCancel", "Cancel"), borderBr, subText);
            cancelBtn.Margin = new Thickness(0, 0, 10, 0);
            cancelBtn.Click += (_, __) => { DialogResult = false; Close(); };

            var applyBtn = BuildRoundedActionButton(
                GetString("Dialog_BtnSave", "Save"), accentColor);
            applyBtn.Click += (_, __) =>
            {
                string p = txtPrimary.Text.Trim();
                string s = txtSecondary.Text.Trim();

                if (string.IsNullOrEmpty(p) || !IsValidIP(p))
                {
                    ShowValidationDialog(
                        GetString("CustomDns_ErrorInvalidPrimary", "Invalid Primary DNS"),
                        GetString("CustomDns_ErrorInvalidPrimaryBody",
                            "Please enter a valid Primary DNS address.\nExample: 8.8.8.8"));
                    return;
                }
                if (!string.IsNullOrEmpty(s) && !IsValidIP(s))
                {
                    ShowValidationDialog(
                        GetString("CustomDns_ErrorInvalidSecondary", "Invalid Secondary DNS"),
                        GetString("CustomDns_ErrorInvalidSecondaryBody",
                            "Please enter a valid Secondary DNS or leave it empty."));
                    return;
                }
                PrimaryDNS = p;
                SecondaryDNS = s;
                DialogResult = true;
                Close();
            };

            btnsPanel.Children.Add(cancelBtn);
            btnsPanel.Children.Add(applyBtn);
            stack.Children.Add(btnsPanel);

            outerBorder.Child = stack;
            Content = outerBorder;

            PreviewMouseLeftButtonDown += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                while (src != null && src != this)
                {
                    if (src is Button || src is TextBox) return;
                    src = VisualTreeHelper.GetParent(src);
                }
                if (WindowState == WindowState.Normal) DragMove();
            };

            // ✅ Fade للـ opacity الصح من الـ Owner
            Loaded += (_, __) =>
            {
                double targetOpacity = Owner?.Opacity ?? 1.0;
                var anim = new DoubleAnimation(0, targetOpacity,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(OpacityProperty, anim);
            };

            AttachIPAutoFormat(txtPrimary, txtSecondary);
            AttachIPAutoFormat(txtSecondary, null);
        }

        private static TextBox BuildIPTextBox(
            Brush bg, Brush borderBr, Brush fg, string placeholder)
        {
            var tb = new TextBox
            {
                Text = "",
                Height = 42,
                Background = bg,
                BorderThickness = new Thickness(1.5),
                BorderBrush = borderBr,
                Foreground = fg,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = fg,
                MaxLength = 15
            };
            ApplyRoundedBorder(tb, 10);
            return tb;
        }

        private static Button BuildRoundedActionButton(string text, Color accentColor)
        {
            var btn = new Button
            {
                Content = text,
                Width = 110,
                Height = 38,
                Background = new SolidColorBrush(accentColor),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            var tmpl = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            border.AppendChild(cp);
            tmpl.VisualTree = border;
            btn.Template = tmpl;
            btn.MouseEnter += (_, __) => btn.Opacity = 0.88;
            btn.MouseLeave += (_, __) => btn.Opacity = 1.0;
            return btn;
        }

        private static Button BuildRoundedCancelButton(
            string text, Brush borderBr, Brush subText)
        {
            var btn = new Button
            {
                Content = text,
                Width = 110,
                Height = 38,
                Background = Brushes.Transparent,
                Foreground = subText,
                BorderBrush = borderBr,
                BorderThickness = new Thickness(1.5),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            var tmpl = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            border.AppendChild(cp);
            tmpl.VisualTree = border;
            btn.Template = tmpl;
            btn.MouseEnter += (_, __) => btn.Opacity = 0.7;
            btn.MouseLeave += (_, __) => btn.Opacity = 1.0;
            return btn;
        }

        private static void AttachIPAutoFormat(TextBox tb, TextBox? nextField = null)
        {
            tb.PreviewTextInput += (s, e) =>
            {
                // منع كتابة أي شيء غير الأرقام والنقاط
                foreach (char c in e.Text)
                    if (!char.IsDigit(c) && c != '.') { e.Handled = true; return; }
            };

            tb.TextChanged += (s, e) =>
            {
                if (s is not TextBox box) return;
                string text = box.Text;
                int caret = box.CaretIndex;

                // منع تكرار النقاط ..
                if (text.Contains(".."))
                {
                    box.Text = text.Replace("..", ".");
                    box.CaretIndex = Math.Min(caret, box.Text.Length);
                    return;
                }

                var octets = text.Split('.');

                // منع كتابة أكثر من 4 خانات
                if (octets.Length > 4)
                {
                    box.Text = string.Join(".", octets.Take(4));
                    box.CaretIndex = box.Text.Length;
                    return;
                }

                // إضافة نقطة تلقائياً إذا اكتملت الخانة (3 أرقام) للـ 3 خانات الأولى
                if (octets.Length < 4)
                {
                    string current = octets[octets.Length - 1];
                    if (current.Length == 3 && !text.EndsWith("."))
                    {
                        if (int.TryParse(current, out int val) && val <= 255)
                        {
                            box.Text = text + ".";
                            box.CaretIndex = box.Text.Length;
                        }
                    }
                }

                // ✅ الانتقال التلقائي: يحدث "فقط" إذا امتلأت الخانة الرابعة بـ 3 أرقام
                if (octets.Length == 4 && octets[3].Length == 3 && IsCompleteIP(box.Text) && nextField != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        nextField.Focus();
                        nextField.SelectAll();
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
            };

            tb.PreviewKeyDown += (s, e) =>
            {
                if (s is not TextBox box) return;

                // ✅ ميزة إضافية: إذا ضغط المستخدم على نقطة (.) أو مسطرة في الخانة الأخيرة، ينتقل للمربع الثاني
                if (e.Key == Key.OemPeriod || e.Key == Key.Decimal || e.Key == Key.Space)
                {
                    var octets = box.Text.Split('.');
                    if (octets.Length == 4 && !string.IsNullOrEmpty(octets[3]) && nextField != null)
                    {
                        e.Handled = true;
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            nextField.Focus();
                            nextField.SelectAll();
                        }), System.Windows.Threading.DispatcherPriority.Input);
                    }
                    else if (box.Text.EndsWith(".") || box.Text.Count(c => c == '.') >= 3)
                    {
                        e.Handled = true; // منع كتابة نقطة رابعة
                    }
                }
            };
        }

        private static bool IsValidIP(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            var parts = ip.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
                if (!int.TryParse(part, out int num) || num < 0 || num > 255)
                    return false;
            return true;
        }

        private static bool IsCompleteIP(string ip)
        {
            var parts = ip.Split('.');
            if (parts.Length != 4) return false;
            return parts.All(p =>
                !string.IsNullOrEmpty(p) &&
                int.TryParse(p, out int n) && n >= 0 && n <= 255);
        }

        // ✅ Validation Dialog (Polished + OK زر نزل لتحت)
        private void ShowValidationDialog(string title, string message)
        {
            Color warningColor = Color.FromRgb(245, 158, 11);
            Brush cardBg = GetRes("DynamicCardBg",
                                       new SolidColorBrush(Color.FromRgb(30, 30, 30)));
            Brush borderBr = GetRes("DynamicBorder",
                                       new SolidColorBrush(Color.FromRgb(60, 60, 60)));
            Brush mainText = GetRes("DynamicMainText", Brushes.White);
            Brush subText = GetRes("DynamicSubText", Brushes.Gray);
            Color bgColor = ((SolidColorBrush)cardBg).Color;
            double targetOpacity = Owner?.Opacity ?? this.Opacity;

            var dialog = new Window
            {
                Width = 380,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            dialog.SizeToContent = SizeToContent.Height; // [تعديل] لضبط الارتفاع تلقائياً
            dialog.MinHeight = 270; // [تعديل] لضمان عدم صغر النافذة بشكل مبالغ فيه
            // Height = 280, // [تعديل] تم تعطيل السطر القديم

            dialog.WindowStyle = WindowStyle.None;
            dialog.AllowsTransparency = true;
            dialog.Background = Brushes.Transparent;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.ShowInTaskbar = false;
            dialog.Topmost = true;
            dialog.Opacity = 0;


            var bg = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            bg.GradientStops.Add(new GradientStop(warningColor, 0));
            bg.GradientStops.Add(new GradientStop(warningColor, 0.018));
            bg.GradientStops.Add(new GradientStop(bgColor, 0.0181));
            bg.GradientStops.Add(new GradientStop(bgColor, 1));

            var outerBorder = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                BorderBrush = borderBr,
                ClipToBounds = true
            };

            var stack = new StackPanel { Margin = new Thickness(28, 22, 28, 22) };

            var circle = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = new SolidColorBrush(
                    Color.FromArgb(38, warningColor.R, warningColor.G, warningColor.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            circle.Child = new TextBlock
            {
                Text = "\uE7BA",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = new SolidColorBrush(warningColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(circle);

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = mainText,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var msgBorder = new Border
            {
                Background = new SolidColorBrush(
                    Color.FromArgb(15, warningColor.R, warningColor.G, warningColor.B)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                // ✅ مسافة أكبر تحت
                Margin = new Thickness(0, 0, 0, 26)
            };
            msgBorder.Child = new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = subText,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                LineHeight = 18
            };
            stack.Children.Add(msgBorder);

            var okBtn = BuildRoundedActionButton(
                GetString("Dialog_BtnOk", "OK"), warningColor);
            okBtn.Click += (_, __) => dialog.Close();
            stack.Children.Add(okBtn);

            outerBorder.Child = stack;
            dialog.Content = outerBorder;

            dialog.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                while (src != null && src != dialog)
                {
                    if (src is Button) return;
                    src = VisualTreeHelper.GetParent(src);
                }
                if (dialog.WindowState == WindowState.Normal) dialog.DragMove();
            };

            dialog.Loaded += (_, __) =>
            {
                var anim = new DoubleAnimation(0, targetOpacity,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                dialog.BeginAnimation(Window.OpacityProperty, anim);
            };

            dialog.ShowDialog();
        }

        private static void ApplyRoundedBorder(TextBox tb, double radius)
        {
            var template = new ControlTemplate(typeof(TextBox));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.Name = "PART_ContentHost";
            scroll.SetValue(ScrollViewer.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            scroll.SetValue(ScrollViewer.MarginProperty, new Thickness(10, 0, 10, 0));
            border.AppendChild(scroll);
            template.VisualTree = border;
            tb.Template = template;
        }

        private static Brush GetRes(string key, Brush fallback)
        {
            try
            {
                if (Application.Current?.Resources.Contains(key) == true
                    && Application.Current.Resources[key] is Brush b) return b;
            }
            catch { }
            return fallback;
        }

        private string GetString(string key, string fallback)
        {
            try
            {
                if (Application.Current?.Resources.Contains(key) == true
                    && Application.Current.Resources[key] is string s) return s;
            }
            catch { }
            return fallback;
        }
    }
}