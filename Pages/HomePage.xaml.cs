// ═════════════════════════════════════════════════════════════════
// ███ FILE: Pages/HomePage.xaml.cs
// ═════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using IO = System.IO;
using OpenHardwareMonitor.Hardware;
using Dreams.Themes;
using Wpf.Ui.Controls;

namespace Dreams.Pages
{
    // ═════════════════════════════════════════════════════════════════
    // ███ DATA TRANSFER OBJECTS (DTOs)
    // ═════════════════════════════════════════════════════════════════

    #region DTOs
    public class CpuData { public string Name { get; set; } public string Speed { get; set; } public string Cores { get; set; } public string Threads { get; set; } }
    public class GpuData { public List<GpuInfo> Gpus { get; set; } = new List<GpuInfo>(); public string RefreshRate { get; set; } }
    public class GpuInfo { public string Name { get; set; } public string VRAM { get; set; } public string ColorDepth { get; set; } public bool IsIntegrated { get; set; } }
    public class BoardData { public string Man { get; set; } public string Mod { get; set; } public string Prod { get; set; } public string BSerial { get; set; } public string BiSerial { get; set; } public string BiDate { get; set; } }

    public class WinData { public string Name { get; set; } public string Build { get; set; } public string Host { get; set; } public string User { get; set; } public string Ip { get; set; } public string Mac { get; set; } public string IpCountryCode { get; set; } }

    public class IpGeoData { public string CountryCode { get; set; } = "--"; public string CountryName { get; set; } public string City { get; set; } }

    internal class SystemMetrics
    {
        public string RamUsed { get; set; }
        public string RamTotal { get; set; }
        public string RamFree { get; set; }
        public double RamPercent { get; set; }
        public string DiskUsed { get; set; }
        public string DiskTotal { get; set; }
        public string DiskFree { get; set; }
        public double DiskPercent { get; set; }
        public string CpuTemp { get; set; }
        public string GpuTemp { get; set; }
    }
    #endregion

    public partial class HomePage : System.Windows.Controls.Page
    {
        // ═════════════════════════════════════════════════════════════════
        // ███ CONSTANTS
        // ═════════════════════════════════════════════════════════════════

        #region Constants
        private const double BYTES_IN_GB = 1073741824.0;
        private const int MAX_TEMP_WAIT_MS = 10000;
        private const int STATS_REFRESH_MS = 3000;
        private const int CLOCK_UPDATE_MS = 1000;
        private const int BITSPIXEL = 12;
        private const int PLANES = 14;
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ FIELDS
        // ═════════════════════════════════════════════════════════════════

        #region Fields
        private DispatcherTimer _clockTimer, _statsTimer;
        private string _systemDrive;
        private bool _isDarkMode, _isClosing;
        private Computer _computer;
        private IHardware _cpuHardware, _gpuPrimaryHardware;
        private volatile bool _tempMonitorReady;
        private readonly object _hardwareLock = new object();

        // IP / MAC
        private bool _isIpVisible = false;
        private string _cachedIpAddress = "--";
        private bool _isIpConnected = true;
        private string _cachedMacAddress = "--";
        private bool _isMacConnected = true;

        // Clock cache
        private string _cachedDate = "";
        private int _cachedDay = -1;
        private int _cachedMonth = -1;
        private int _cachedYear = -1;

        // Language
        private string _currentLangCode = "en";
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ WIN32 API
        // ═════════════════════════════════════════════════════════════════

        #region Win32 API
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string lpDir,
            out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

        [DllImport("user32.dll")] private static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                          ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra, dmFields,
                         dmOrientation, dmPaperSize, dmPaperLength, dmPaperWidth, dmScale,
                         dmCopies, dmDefaultSource, dmPrintQuality, dmColor, dmDuplex,
                         dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags,
                         dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType,
                         dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ CONSTRUCTOR & PAGE EVENTS
        // ═════════════════════════════════════════════════════════════════

        #region Constructor
        public HomePage()
        {
            DisableConfigFileRequirement();
            InitializeComponent();

            this.Loaded += HomePage_Loaded;
            this.Unloaded += HomePage_Unloaded;

            ThemeManager.ThemeChanged += OnThemeChanged;
            ThemeManager.OpacityChanged += OnOpacityChanged;
            App.LanguageChanged += OnLanguageChanged;

            _isDarkMode = ThemeManager.IsDarkMode;
            _currentLangCode = LoadSavedLanguageCode();

            ApplyTheme(_isDarkMode);
            ApplySavedOpacity();

            if (LoadingOverlay != null)
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingOverlay.Opacity = 1;
            }

            _systemDrive = IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            InitTimers();
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
            => LoadSystemDataAsync();

        private void HomePage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.LanguageChanged -= OnLanguageChanged;
            App.FlowDirectionChanged -= OnFlowDirectionChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.OpacityChanged -= OnOpacityChanged;

            _clockTimer?.Stop();
            _statsTimer?.Stop();

            Cleanup();

            Debug.WriteLine("[HomePage] Unloaded - Cleanup complete");
        }

        private static string LoadSavedLanguageCode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Dreams");
                return key?.GetValue("Language")?.ToString() ?? "en";
            }
            catch { return "en"; }
        }

        private void ApplySavedOpacity() { /* handled by MainWindow */ }

        private void DisableConfigFileRequirement()
        {
            try
            {
                //AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", null);
                AppDomain.CurrentDomain.SetData("TargetFrameworkName", ".NETFramework,Version=v4.8");
            }
            catch (Exception ex) { Debug.WriteLine($"Config disable error: {ex.Message}"); }
        }

        private void InitTimers()
        {
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CLOCK_UPDATE_MS)
            };
            _clockTimer.Tick += (s, e) =>
            {
                UpdateClockAndDate();
                UpdateGreeting();
            };
            _clockTimer.Start();
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ THEME / LANGUAGE / OPACITY HANDLERS
        // ═════════════════════════════════════════════════════════════════

        #region Theme & Language
        private void OnThemeChanged(bool isDark)
        {
            Dispatcher.Invoke(() =>
            {
                _isDarkMode = isDark;
                ApplyTheme(_isDarkMode);
            });
        }

        private void OnOpacityChanged(double opacity)
        {
            Dispatcher.Invoke(() =>
            {
                var window = Window.GetWindow(this);
                if (window != null) window.Opacity = opacity;
            });
        }

        private async void OnLanguageChanged(string langCode)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                _currentLangCode = langCode;
                InvalidateDateCache();
                UpdateGreeting();
                await ReloadAllSystemData();
            });
        }

        private void OnFlowDirectionChanged(FlowDirection direction)
            => Dispatcher.Invoke(() => this.FlowDirection = direction);

        private void ApplyTheme(bool isDark)
        {
            try
            {
                if (LoadingOverlay == null) return;
                LoadingOverlay.Background = new SolidColorBrush(
                    isDark
                        ? Color.FromRgb(32, 32, 32)
                        : Color.FromRgb(241, 245, 249));
            }
            catch (Exception ex) { Debug.WriteLine($"ApplyTheme error: {ex.Message}"); }
        }

        public void SetDarkTheme() => ThemeManager.SetTheme(true);
        public void SetLightTheme() => ThemeManager.SetTheme(false);
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ INITIALIZATION
        // ═════════════════════════════════════════════════════════════════

        #region Initialization
        private async void LoadSystemDataAsync()
        {
            try
            {
                var cpuTask = Task.Run(() => GetCpuInfo());
                var gpuTask = Task.Run(() => GetGpuInfo());
                var boardTask = Task.Run(() => GetBoardInfo());
                var winTask = Task.Run(() => GetWinInfo());

                await RefreshDynamicStatsAsync();
                _ = Task.Run(() => InitializeTempMonitor());

                await Task.WhenAll(cpuTask, gpuTask, boardTask, winTask);
                PopulateUI(await cpuTask, await gpuTask, await boardTask, await winTask);

                await WaitForTemperatureDataAsync();

                _statsTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(STATS_REFRESH_MS)
                };
                _statsTimer.Tick += async (s, e) => await RefreshDynamicStatsAsync();
                _statsTimer.Start();

                HideLoadingScreen();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Initialization failed: {ex.Message}");
                HideLoadingScreen();
            }
        }

        private async Task ReloadAllSystemData()
        {
            if (_isClosing) return;
            try
            {
                _statsTimer?.Stop();
                await Task.Delay(100);

                var cpuTask = Task.Run(() => GetCpuInfo());
                var gpuTask = Task.Run(() => GetGpuInfo());
                var boardTask = Task.Run(() => GetBoardInfo());
                var winTask = Task.Run(() => GetWinInfo());

                await Task.WhenAll(cpuTask, gpuTask, boardTask, winTask);
                if (_isClosing) return;

                PopulateUI(await cpuTask, await gpuTask, await boardTask, await winTask);
                await RefreshDynamicStatsAsync();

                if (_tempMonitorReady && !_isClosing)
                {
                    var (cpu, gpu) = ReadTemperatures();
                    if (!string.IsNullOrEmpty(cpu) && cpu != "--") runCpuTemp.Text = cpu;
                    if (!string.IsNullOrEmpty(gpu) && gpu != "--") runGpuMainTemp.Text = gpu;
                }

                _statsTimer?.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReloadAllSystemData error: {ex.Message}");
                _statsTimer?.Start();
            }
        }

        private void Cleanup()
        {
            _isClosing = true;
            _statsTimer?.Stop();
            _clockTimer?.Stop();

            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.OpacityChanged -= OnOpacityChanged;
            App.LanguageChanged -= OnLanguageChanged;
            this.Loaded -= HomePage_Loaded;
            this.Unloaded -= HomePage_Unloaded;

            lock (_hardwareLock)
            {
                _tempMonitorReady = false;
                _cpuHardware = null;
                _gpuPrimaryHardware = null;
                if (_computer != null)
                {
                    try { _computer.Close(); (_computer as IDisposable)?.Dispose(); }
                    catch (Exception ex) { Debug.WriteLine($"Error closing computer: {ex.Message}"); }
                    _computer = null;
                }
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ HARDWARE MONITORING
        // ═════════════════════════════════════════════════════════════════

        #region Hardware Monitoring
        private void InitializeTempMonitor()
        {
            if (_tempMonitorReady || _isClosing) return;
            try
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = false,
                    IsMotherboardEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = false,
                    IsBatteryEnabled = false
                };
                computer.Open(false);
                foreach (var hw in computer.Hardware) hw.Update();

                var cpuHw = computer.Hardware
                    .FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

                var gpuHw = computer.Hardware
                    .FirstOrDefault(h =>
                        h.HardwareType == HardwareType.GpuNvidia ||
                        h.HardwareType == HardwareType.GpuAmd ||
                        h.HardwareType == HardwareType.GpuIntel)
                    ?? computer.Hardware
                        .FirstOrDefault(h => h.HardwareType.ToString().Contains("Gpu"));

                lock (_hardwareLock)
                {
                    if (_isClosing) { try { computer.Close(); } catch { } return; }
                    _computer = computer;
                    _cpuHardware = cpuHw;
                    _gpuPrimaryHardware = gpuHw;
                    _tempMonitorReady = true;
                }
                Debug.WriteLine("Hardware monitor initialized.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Hardware monitor init failed: {ex.Message}");
                _tempMonitorReady = false;
            }
        }

        private (string Cpu, string Gpu) ReadTemperatures()
        {
            if (!_tempMonitorReady || _isClosing) return ("--", "--");

            string deg = "°", cel = "C";
            try { Dispatcher.Invoke(() => { deg = TryGetResource("DegreeSymbol") ?? "°"; cel = TryGetResource("Celsius") ?? "C"; }); }
            catch { }
            string unit = $"{deg}{cel}";

            string cpu = "--", gpu = "--";
            try
            {
                lock (_hardwareLock)
                {
                    if (!_tempMonitorReady || _isClosing) return ("--", "--");

                    // ✅ في دالة تحديث الـ Hardware، أضف التحقق ده:

                    // CPU
                    if (_cpuHardware != null)
                    {
                        try
                        {
                            _cpuHardware.Update();

                            // ✅ تأكد إن Sensors مش null
                            if (_cpuHardware.Sensors != null)
                            {
                                var sensors = _cpuHardware.Sensors
                                    .Where(s => s != null && s.SensorType == SensorType.Temperature && s.Value.HasValue)
                                    .ToList();

                                if (sensors != null && sensors.Count > 0)
                                {
                                    var m = sensors.FirstOrDefault(s =>
                                        s.Name?.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        s.Name?.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        s.Name?.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0)
                                        ?? sensors[0];

                                    if (m?.Value.HasValue == true)
                                        cpu = $"{m.Value.Value:F0}{unit}";
                                }
                            }
                        }
                        catch (Exception ex) { Debug.WriteLine($"CPU temp read error: {ex.Message}"); }
                    }

                    // GPU
                    if (_gpuPrimaryHardware != null)
                    {
                        try
                        {
                            _gpuPrimaryHardware.Update();
                            var sensors = _gpuPrimaryHardware.Sensors
                                ?.Where(s => s != null && s.SensorType == SensorType.Temperature && s.Value.HasValue)
                                .ToList();
                            if (sensors?.Count > 0)
                            {
                                var m = sensors.FirstOrDefault(s =>
                                    s.Name?.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    s.Name?.IndexOf("Temperature", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    s.Name?.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                                    ?? sensors[0];
                                if (m?.Value.HasValue == true) gpu = $"{m.Value.Value:F0}{unit}";
                            }
                        }
                        catch (Exception ex) { Debug.WriteLine($"GPU temp read error: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"ReadTemperatures lock error: {ex.Message}"); }

            return (cpu, gpu);
        }

        private async Task WaitForTemperatureDataAsync()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < MAX_TEMP_WAIT_MS)
            {
                if (_tempMonitorReady)
                {
                    var (cpu, gpu) = ReadTemperatures();
                    if (cpu != "--" || gpu != "--")
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            runCpuTemp.Text = cpu;
                            runGpuMainTemp.Text = gpu;
                        });
                        break;
                    }
                }
                await Task.Delay(150);
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ UI UPDATES
        // ═════════════════════════════════════════════════════════════════

        #region UI Updates

        private async Task RefreshDynamicStatsAsync()
        {
            if (_isClosing) return;
            try
            {
                var metrics = await Task.Run(() => CollectSystemMetrics());
                if (!_isClosing)
                    await Dispatcher.InvokeAsync(() => ApplyMetricsToUI(metrics));

                await RefreshIpAddressAsync();
                await RefreshMacAddressAsync();
            }
            catch (Exception ex) { Debug.WriteLine($"Refresh stats error: {ex.Message}"); }
        }

        private SystemMetrics CollectSystemMetrics()
        {
            var m = new SystemMetrics();

            // RAM
            try
            {
                var mem = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX))
                };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    double total = mem.ullTotalPhys / BYTES_IN_GB;
                    double used = total - (mem.ullAvailPhys / BYTES_IN_GB);
                    m.RamUsed = $"{used:F1}";
                    m.RamTotal = $"{total:F1}";
                    m.RamFree = $"{(mem.ullAvailPhys / BYTES_IN_GB):F1}";
                    m.RamPercent = (used / total) * 100;
                }
            }
            catch { }

            // Disk
            try
            {
                if (GetDiskFreeSpaceEx(_systemDrive, out _, out ulong total, out ulong free))
                {
                    double totalGB = total / BYTES_IN_GB;
                    double usedGB = totalGB - (free / BYTES_IN_GB);
                    m.DiskUsed = $"{usedGB:F1}";
                    m.DiskTotal = $"{totalGB:F1}";
                    m.DiskFree = $"{(free / BYTES_IN_GB):F1}";
                    m.DiskPercent = (usedGB / totalGB) * 100;
                }
            }
            catch { }

            // Temperatures
            if (_tempMonitorReady && !_isClosing)
            {
                try { var (c, g) = ReadTemperatures(); m.CpuTemp = c; m.GpuTemp = g; }
                catch { m.CpuTemp = m.GpuTemp = "--"; }
            }
            else { m.CpuTemp = m.GpuTemp = "--"; }

            return m;
        }

        private void ApplyMetricsToUI(SystemMetrics m)
        {
            if (_isClosing) return;

            string gb = TryGetResource("GB") ?? "GB";
            string deg = TryGetResource("DegreeSymbol") ?? "°";
            string cel = TryGetResource("Celsius") ?? "C";
            string unit = $"{deg}{cel}";

            // ── RAM ──────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(m.RamUsed))
            {
                lblRamUsed.Text = $"{m.RamUsed} {gb}";
                lblRamTotal.Text = m.RamTotal;
                lblRamFree.Text = m.RamFree;
                UpdateWpfUiArc(ramArc, lblRamPerc, m.RamPercent);
            }

            // ── Disk ─────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(m.DiskUsed))
            {
                lblDiskUsed.Text = $"{m.DiskUsed} {gb}";
                lblDiskTotal.Text = m.DiskTotal;
                lblDiskFree.Text = m.DiskFree;
                UpdateWpfUiArc(diskArc, lblDiskPerc, m.DiskPercent);
            }

            // ── Temperatures ─────────────────────────────────────────────
            if (_tempMonitorReady)
            {
                if (!string.IsNullOrEmpty(m.CpuTemp) && m.CpuTemp != "--")
                {
                    string v = Regex.Match(m.CpuTemp, @"[\d\.]+").Value;
                    if (!string.IsNullOrEmpty(v)) runCpuTemp.Text = $"{v}{unit}";
                }
                if (!string.IsNullOrEmpty(m.GpuTemp) && m.GpuTemp != "--")
                {
                    string v = Regex.Match(m.GpuTemp, @"[\d\.]+").Value;
                    if (!string.IsNullOrEmpty(v)) runGpuMainTemp.Text = $"{v}{unit}";
                }
            }
        }

        private void UpdateWpfUiArc(Arc arc, System.Windows.Controls.TextBlock label, double percent)
        {
            if (arc == null || label == null) return;

            percent = Math.Max(0, Math.Min(100, percent));
            double targetAngle = percent <= 0 ? 1 : (percent / 100.0) * 359.9;
            if (targetAngle < 1) targetAngle = 1;

            double startAngle = arc.EndAngle;

            if (Math.Abs(startAngle - targetAngle) < 0.1)
            {
                arc.EndAngle = targetAngle;
                AnimatePercentageText(label, percent);
                return;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            int ticks = 0;
            int totalTicks = 40;

            timer.Tick += (sender, e) =>
            {
                ticks++;
                double easingProgress = 1 - Math.Pow(1 - ((double)ticks / totalTicks), 3);
                double currentAngle = startAngle + (targetAngle - startAngle) * easingProgress;
                arc.EndAngle = currentAngle;
                if (ticks >= totalTicks)
                {
                    timer.Stop();
                    arc.EndAngle = targetAngle;
                }
            };
            timer.Start();
            AnimatePercentageText(label, percent);
        }

        private void AnimatePercentageText(System.Windows.Controls.TextBlock label, double targetPercent)
        {
            if (!double.TryParse(label.Text.Replace("%", "").Trim(), out double currentPercent))
                currentPercent = 0;

            if (Math.Abs(targetPercent - currentPercent) > 0.5)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                double increment = (targetPercent - currentPercent) / 40.0;
                double current = currentPercent;

                timer.Tick += (s, e) =>
                {
                    current += increment;
                    if ((increment > 0 && current >= targetPercent) || (increment < 0 && current <= targetPercent))
                    {
                        label.Text = $"{Math.Round(targetPercent)}%";
                        timer.Stop();
                    }
                    else { label.Text = $"{Math.Round(current)}%"; }
                };
                timer.Start();
            }
            else { label.Text = $"{Math.Round(targetPercent)}%"; }
        }

        private void HideLoadingScreen()
        {
            Dispatcher.Invoke(() =>
            {
                if (LoadingOverlay == null) return;
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (s, e) =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    LoadingOverlay.Opacity = 1;
                };
                LoadingOverlay.BeginAnimation(OpacityProperty, anim);
            });
        }

        private void UpdateGreeting()
        {
            try
            {
                int h = DateTime.Now.Hour;
                string key = h < 12 ? "GoodMorning" : h < 18 ? "GoodAfternoon" : "GoodEvening";
                string greeting = TryGetResource(key) ?? "Good Day";

                // ✅ النص العادي (Good Morning,)
                lblGreetingPrefix.Text = $"{greeting},";

                // ✅ اسم اليوزر منفصل (هياخد لون الـ Gradient من XAML)
                lblUserName.Text = Environment.UserName;

                // ✅ أيقونة اليد موجودة في XAML (Segoe MDL2 Assets) - مش محتاجين نعدّلها
            }
            catch
            {
                lblGreetingPrefix.Text = "Good Day,";
                lblUserName.Text = Environment.UserName;
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ LOCALIZED CLOCK & DATE
        // ═════════════════════════════════════════════════════════════════

        #region Localized Clock & Date
        private void UpdateClockAndDate()
        {
            var now = DateTime.Now;

            lblTimeClock.Text = BuildLocalizedTime(now);

            if (now.Day != _cachedDay || now.Month != _cachedMonth || now.Year != _cachedYear)
            {
                _cachedDate = BuildLocalizedDate(now);
                _cachedDay = now.Day;
                _cachedMonth = now.Month;
                _cachedYear = now.Year;
            }

            if (lblFullDate.Text != _cachedDate)
                lblFullDate.Text = _cachedDate;
        }

        private void InvalidateDateCache()
            => _cachedDay = _cachedMonth = _cachedYear = -1;

        private string BuildLocalizedTime(DateTime now)
        {
            bool isAm = now.Hour < 12;
            string marker = TryGetResource(isAm ? "AM" : "PM") ?? (isAm ? "AM" : "PM");
            int h12 = now.Hour % 12;
            if (h12 == 0) h12 = 12;
            return $"{h12:D2}:{now.Minute:D2}:{now.Second:D2} {marker}";
        }

        private string BuildLocalizedDate(DateTime now)
        {
            string day = GetLocalizedDayName(now.DayOfWeek);
            string month = GetLocalizedMonthName(now.Month);
            return _currentLangCode == "ar"
                ? $"{day}، {now.Day:D2} {month} {now.Year}"
                : $"{day}, {now.Day:D2} {month} {now.Year}";
        }

        private string GetLocalizedDayName(DayOfWeek dow)
        {
            string key = dow.ToString();
            return TryGetResource(key) ?? CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(dow);
        }

        private string GetLocalizedMonthName(int month)
        {
            string key = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
            return TryGetResource(key) ?? CultureInfo.CurrentUICulture.DateTimeFormat.GetMonthName(month);
        }

        private string TryGetResource(string key)
        {
            try
            {
                var v = FindResource(key);
                if (v is string s && s.Length > 0) return s;
            }
            catch { }
            return null;
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ IP / MAC ADDRESS (OPTIMIZED)
        // ═════════════════════════════════════════════════════════════════

        #region IP & MAC

        // ✅ OPTIMIZED: Gets both IP and Geo data in a single network call
        private (string ip, string countryCode, bool isConnected) GetIpAndGeoData()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return ("No Network", "--", false);

            try
            {
                // Single API endpoint that returns both IP and country code
                string url = "http://ip-api.com/json/?fields=status,countryCode,query";
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                    client.DownloadStringCompleted += (s, e) => { }; // Prevent unhandled events
                    string json = client.DownloadString(url);

                    var statusMatch = Regex.Match(json, @"""status""\s*:\s*""([^""]+)""");
                    if (statusMatch.Success && statusMatch.Groups[1].Value == "success")
                    {
                        var ipMatch = Regex.Match(json, @"""query""\s*:\s*""([^""]+)""");
                        var countryCodeMatch = Regex.Match(json, @"""countryCode""\s*:\s*""([^""]+)""");

                        string ip = ipMatch.Success ? ipMatch.Groups[1].Value : "Unknown IP";
                        string countryCode = countryCodeMatch.Success ? countryCodeMatch.Groups[1].Value : "--";

                        return (ip, countryCode, true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetIpAndGeoData error: {ex.Message}");
                // Fallback to local IP if public IP fails
                try
                {
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                    {
                        socket.Connect("8.8.8.8", 65530);
                        var endPoint = socket.LocalEndPoint as IPEndPoint;
                        return (endPoint?.Address.ToString() ?? "Not Connected", "--", false);
                    }
                }
                catch { /* ignore fallback error */ }
            }

            return ("Error", "--", false);
        }

        // Wrapper that calls the optimized method
        private (string ip, string countryCode, bool isConnected) GetIpAddressWithStatus()
        {
            return GetIpAndGeoData();
        }

        private (string Mac, bool IsConnected) GetMacAddressWithStatus()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                    return (TryGetResource("NoConnection") ?? "No Connection", false);

                var active = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

                if (active == null)
                    return (TryGetResource("NoConnection") ?? "No Connection", false);

                string mac = active.GetPhysicalAddress().ToString();
                if (string.IsNullOrEmpty(mac) || mac == "000000000000")
                    return (TryGetResource("NoConnection") ?? "No Connection", false);

                return (string.Join(":", Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2))), true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetMacAddressWithStatus error: {ex.Message}");
                return (TryGetResource("NoConnection") ?? "No Connection", false);
            }
        }

        private async Task RefreshIpAddressAsync()
        {
            if (_isClosing) return;
            try
            {
                var (ip, countryCode, isConnected) = await Task.Run(() => GetIpAddressWithStatus());
                _cachedIpAddress = ip;
                _isIpConnected = isConnected;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_isClosing) return;
                    runIp.Text = string.IsNullOrEmpty(countryCode) || countryCode == "--" ? ip : $"{ip} ({countryCode})";

                    if (!isConnected)
                    {
                        runIp.SetResourceReference(ForegroundProperty, "DynamicWarning");
                        runIp.FontStyle = FontStyles.Italic;
                        IpMaskRect.Opacity = 0;
                        btnToggleIpVisibility.IsEnabled = false;
                        btnToggleIpVisibility.Opacity = 0.5;
                    }
                    else
                    {
                        runIp.SetResourceReference(ForegroundProperty, "DynamicMainText");
                        runIp.FontStyle = FontStyles.Normal;
                        IpMaskRect.Opacity = _isIpVisible ? 0 : 1;
                        btnToggleIpVisibility.IsEnabled = true;
                        btnToggleIpVisibility.Opacity = 1;
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshIpAddressAsync error: {ex.Message}");
                await Dispatcher.InvokeAsync(() => SetIpErrorState());
            }
        }

        private async Task RefreshMacAddressAsync()
        {
            if (_isClosing) return;
            try
            {
                var (mac, isConnected) = await Task.Run(() => GetMacAddressWithStatus());
                _cachedMacAddress = mac;
                _isMacConnected = isConnected;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_isClosing) return;
                    runMac.Text = mac;
                    runMac.SetResourceReference(ForegroundProperty, isConnected ? "DynamicMainText" : "DynamicWarning");
                });
            }
            catch (Exception ex) { Debug.WriteLine($"RefreshMacAddressAsync error: {ex.Message}"); }
        }

        private void SetIpErrorState()
        {
            runIp.Text = "Error";
            runIp.SetResourceReference(ForegroundProperty, "DynamicWarning");
            runIp.FontStyle = FontStyles.Italic;
            IpMaskRect.Opacity = 0;
            btnToggleIpVisibility.IsEnabled = false;
            btnToggleIpVisibility.Opacity = 0.5;
        }

        private void ToggleIpVisibility_Click(object sender, RoutedEventArgs e)
        {
            _isIpVisible = btnToggleIpVisibility.IsChecked == true;
            if (!_isIpConnected) { IpMaskRect.Opacity = 0; return; }

            var anim = new DoubleAnimation { To = _isIpVisible ? 0 : 1, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            IpMaskRect.BeginAnimation(OpacityProperty, anim);
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ SYSTEM DATA COLLECTION
        // ═════════════════════════════════════════════════════════════════

        #region System Data Collection
        private CpuData GetCpuInfo()
        {
            var d = new CpuData();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key != null)
                {
                    d.Name = Regex.Replace(key.GetValue("ProcessorNameString")?.ToString() ?? "--", @"\s+", " ").Trim();
                    int mhz = key.GetValue("~MHz") as int? ?? 0;
                    d.Speed = $"{(mhz / 1000.0):F2} GHz";
                }
                d.Cores = WMIHelper.Get("Win32_Processor", "NumberOfCores");
                d.Threads = WMIHelper.Get("Win32_Processor", "NumberOfLogicalProcessors");
            }
            catch { }
            return d;
        }

        private GpuData GetGpuInfo()
        {
            var d = new GpuData();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string name = mo["Name"]?.ToString() ?? "Unknown GPU";
                    if (name.ToLower().Contains("mirror")) continue;
                    long vram = GetVramFromRegistry(name);
                    if (vram == 0 && mo["AdapterRAM"] != null) vram = Convert.ToInt64(mo["AdapterRAM"]);
                    d.Gpus.Add(new GpuInfo { Name = name, VRAM = vram > 0 ? $"{(vram / BYTES_IN_GB):F0} GB" : "--", ColorDepth = GetColorDepth(), IsIntegrated = name.ToLower().Contains("intel") || name.ToLower().Contains("graphics") });
                }
                d.Gpus = d.Gpus.OrderBy(g => g.IsIntegrated).ToList();
                d.RefreshRate = GetRefreshRate();
            }
            catch { }
            return d;
        }

        private long GetVramFromRegistry(string gpuName)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (baseKey == null) return 0;
                foreach (string sub in baseKey.GetSubKeyNames())
                {
                    using var sk = baseKey.OpenSubKey(sub);
                    if (sk?.GetValue("DriverDesc")?.ToString() == gpuName)
                        return Convert.ToInt64(sk.GetValue("HardwareInformation.qwMemorySize"));
                }
            }
            catch { }
            return 0;
        }

        private string GetColorDepth()
        {
            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    int d = GetDeviceCaps(hdc, BITSPIXEL) * GetDeviceCaps(hdc, PLANES);
                    ReleaseDC(IntPtr.Zero, hdc);
                    return d.ToString();
                }
            }
            catch { }
            return "32";
        }

        private string GetRefreshRate()
        {
            try
            {
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                if (EnumDisplaySettings(null, -1, ref dm)) return dm.dmDisplayFrequency.ToString();
            }
            catch { }
            return "60";
        }

        private BoardData GetBoardInfo() => new BoardData
        {
            Man = WMIHelper.Get("Win32_BaseBoard", "Manufacturer"),
            Mod = WMIHelper.Get("Win32_ComputerSystem", "Model"),
            Prod = WMIHelper.Get("Win32_BaseBoard", "Product"),
            BSerial = WMIHelper.Get("Win32_BaseBoard", "SerialNumber"),
            BiSerial = WMIHelper.Get("Win32_BIOS", "SerialNumber"),
            BiDate = FormatBiosDate(WMIHelper.Get("Win32_BIOS", "ReleaseDate"))
        };

        private static string FormatBiosDate(string dt)
        {
            if (!string.IsNullOrEmpty(dt) && dt.Length >= 8)
                return $"{dt.Substring(6, 2)}/{dt.Substring(4, 2)}/{dt.Substring(0, 4)}";
            return "--";
        }

        // ✅ MODIFIED: GetWinInfo - No network calls here anymore (fast startup)
        private WinData GetWinInfo()
        {
            var d = new WinData
            {
                Host = Environment.MachineName,
                User = Environment.UserName,
                Name = WMIHelper.Get("Win32_OperatingSystem", "Caption").Replace("Microsoft", "").Trim()
            };
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    string ver = key.GetValue("DisplayVersion")?.ToString() ?? "Unknown";
                    string build = key.GetValue("CurrentBuild")?.ToString() ?? "0";
                    string ubr = key.GetValue("UBR")?.ToString() ?? "0";
                    d.Build = $"Version {ver} (Build {build}.{ubr})";
                }

                // ✅ MODIFIED: No network calls here - set placeholders only
                d.Ip = "--";
                d.IpCountryCode = "";
                d.Mac = "--";

                _isIpConnected = false;
                _isMacConnected = false;
                _cachedIpAddress = "--";
                _cachedMacAddress = "--";
            }
            catch { }
            return d;
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ UI POPULATION
        // ═════════════════════════════════════════════════════════════════

        #region UI Population
        private void PopulateUI(CpuData cpu, GpuData gpu, BoardData board, WinData win)
        {
            if (_isClosing) return;

            string gb = TryGetResource("GB") ?? "GB";
            string ghz = TryGetResource("GHz") ?? "GHz";
            string hz = TryGetResource("Hz") ?? "Hz";
            string bits = TryGetResource("Bits") ?? "bit";
            string mhz = TryGetResource("MHz") ?? "MHz";

            // CPU
            lblCpu.Text = cpu.Name;
            runCpuSpeed.Text = (cpu.Speed ?? "").Replace("GHz", ghz).Replace("MHz", mhz);
            runCpuCores.Text = cpu.Cores;
            runCpuThreads.Text = cpu.Threads;

            // GPU
            if (gpu.Gpus.Any())
            {
                var p = gpu.Gpus[0];
                runGpuMainName.Text = p.Name;
                runGpuMainVram.Text = (p.VRAM ?? "--").Replace("GB", gb);
                runColorDepth.Text = $"{p.ColorDepth} {bits}";
                lblHz.Text = $"{gpu.RefreshRate} {hz}";

                if (gpu.Gpus.Count > 1)
                {
                    pnlGpuSecondary.Visibility = Visibility.Visible;
                    runGpuSecName.Text = gpu.Gpus[1].Name;
                    runGpuSecVram.Text = (gpu.Gpus[1].VRAM ?? "--").Replace("GB", gb);
                }
            }

            // Display
            lblRes.Text = $"{(int)SystemParameters.PrimaryScreenWidth} × {(int)SystemParameters.PrimaryScreenHeight}";

            // Board / BIOS
            runBoardMan.Text = board.Man;
            runSysModel.Text = board.Mod;
            runBoardProd.Text = board.Prod;
            runBoardSerial.Text = board.BSerial;
            runBiosSerial.Text = board.BiSerial;
            runBiosDate.Text = board.BiDate;

            // OS
            lblWin.Text = win.Name;
            runWinBuild.Text = win.Build;
            runHost.Text = win.Host;
            runUser.Text = win.User;
            runMac.Text = win.Mac;

            // ✅ MODIFIED: Display IP with country code (placeholder initially)
            runIp.Text = string.IsNullOrEmpty(win.IpCountryCode) || win.IpCountryCode == "--" ? win.Ip : $"{win.Ip} ({win.IpCountryCode})";

            // ✅ MODIFIED: Start fetching network info AFTER UI is populated (async, non-blocking)
            _ = RefreshMacAddressAsync();
            _ = RefreshIpAddressAsync();

            // Drive label
            try
            {
                string drive = _systemDrive.TrimEnd('\\');
                lblDriveTitle.Text = $"{TryGetResource("SystemDisk") ?? "System Disk"} ({drive})";
            }
            catch { lblDriveTitle.Text = $"System Disk ({_systemDrive.TrimEnd('\\')})"; }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ NAVIGATION
        // ═════════════════════════════════════════════════════════════════

        #region Navigation
        private void btnGoToInstall_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mw)
            {
                // ✅ تحديث الصفحة
                mw.PagesNavigation.Navigate(new Uri("Pages/InstallPage.xaml", UriKind.RelativeOrAbsolute));

                // ✅ تحديث عنوان الصفحة العلوي
                mw.UpdatePageTitle("Installer");

                // ✅ تحديث القائمة الجانبية والمؤشر
                var installButton = mw.MenuStack.Children.OfType<RadioButton>()
                    .FirstOrDefault(rb => rb.Content?.ToString() == "Installer");

                if (installButton != null)
                {
                    installButton.IsChecked = true;
                    mw.MoveIndicatorTo(installButton);
                }
            }
        }

        private void btnOptimize_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.PagesNavigation?.Navigate(new Uri("Pages/OptimizePage.xaml", UriKind.RelativeOrAbsolute));
        }

        private void btnAbout_Click(object sender, RoutedEventArgs e)
            => new About { Owner = Window.GetWindow(this) }.ShowDialog();

        private void btnSettings_Click(object sender, RoutedEventArgs e)
            => new Settings { Owner = Window.GetWindow(this) }.ShowDialog();
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ WMI HELPER
        // ═════════════════════════════════════════════════════════════════

        #region WMI Helper
        public static class WMIHelper
        {
            public static string Get(string cls, string prop)
            {
                try
                {
                    using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
                    return s.Get().Cast<ManagementObject>().FirstOrDefault()?[prop]?.ToString().Trim() ?? "--";
                }
                catch { return "--"; }
            }
        }
        #endregion
    }
}