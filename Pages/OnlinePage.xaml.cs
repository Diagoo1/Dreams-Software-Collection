using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dreams.Themes;

namespace Dreams.Pages
{
    public partial class OnlinePage : Page
    {
        #region Fields
        private List<AppInfo> _allApps = new();
        private string _currentCategory = "";
        private int _currentColumns = 3;
        private bool _isRunning = false;
        private bool _isPaused = false;
        private bool _isUninstalling = false;
        private bool _isInstallationDone = false;
        private CancellationTokenSource _cts;
        private DispatcherTimer _timer;
        private DispatcherTimer _syncTimer;
        private TimeSpan _elapsed;
        private List<AppInfo> _installQueue = new();

        private Window _queueWindow = null;
        private StackPanel _queueList = null;

        public bool IsInstalling => _isRunning;
        private long _selectionCounter = 0;
        private object _syncLock = new object();
        private bool _isSyncing = false;
        private Border _draggedRow = null;
        private Point _dragStartPoint;
        private bool _isDragging = false;

        private bool _isInitialized = false;
        private DispatcherTimer _resizeDebouncer;
        private List<(Border Header, Border Spacer2, Border Spacer3)> _categoryHeaders = new();

        // سحب التابات أفقياً
        private Point _tabsDragStartPoint;
        private double _tabsScrollStartOffset;
        private bool _isTabsDragging = false;
        private bool _tabsMouseDown = false;

        // الحقول الجديدة
        private AppInfo _currentInstallingApp = null;
        private Dictionary<AppInfo, DispatcherTimer> _appStatusTimers = new Dictionary<AppInfo, DispatcherTimer>();
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private static readonly Brush BrushWarning = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        private static readonly Brush BrushSuccess = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        private static readonly Brush BrushAccent = new SolidColorBrush(Color.FromRgb(14, 165, 233));
        private static readonly Brush BrushMainText = new SolidColorBrush(Color.FromRgb(30, 41, 59));
        private static readonly Brush BrushDanger = new SolidColorBrush(Color.FromRgb(220, 53, 69));

        private static readonly Dictionary<string, Color> CategoryColors = new()
        {
            { "str_CatBrowsers",       Color.FromRgb(99, 102, 241)  },
            { "str_CatCommunication",  Color.FromRgb(34, 197, 94)   },
            { "str_CatMedia",          Color.FromRgb(244, 63, 94)   },
            { "str_CatGraphics",       Color.FromRgb(168, 85, 247)  },
            { "str_CatGaming",         Color.FromRgb(245, 158, 11)  },
            { "str_CatDevelopment",    Color.FromRgb(14, 165, 233)  },
            { "str_CatUtilities",      Color.FromRgb(20, 184, 166)  },
            { "str_Cat3DPrint",        Color.FromRgb(236, 72, 153)  },
            { "str_CatVPN",            Color.FromRgb(239, 68, 68)   },
            { "str_CatProductivity",   Color.FromRgb(59, 130, 246)  },
        };

        private const string CARD_INSTALLING_TAG = "InstallingIndicator";
        #endregion

        #region Localization & Helpers
        private string Loc(string key, string fallback = "")
        {
            try
            {
                if (this.FindResource(key) is string s) return s;
                if (Application.Current?.Resources.Contains(key) == true &&
                    Application.Current.Resources[key] is string a) return a;
            }
            catch { }
            return string.IsNullOrEmpty(fallback) ? key : fallback;
        }

        private Brush GetBrush(string key, Brush fallback)
        {
            try { return TryFindResource(key) as Brush ?? fallback; }
            catch { return fallback; }
        }

        private Style GetStyleSafe(string key)
        {
            try { return TryFindResource(key) as Style; }
            catch { return null; }
        }

        private Color GetCategoryColor(string catKey)
        {
            return CategoryColors.TryGetValue(catKey, out var c) ? c : Color.FromRgb(14, 165, 233);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ✅ دالة تنسيق السرعة
        private static string FormatSpeed(long bytesPerSec)
        {
            if (bytesPerSec <= 0) return "";
            if (bytesPerSec < 1024)
                return $"{bytesPerSec} B/s";
            if (bytesPerSec < 1024 * 1024)
                return $"{bytesPerSec / 1024.0:F1} KB/s";
            if (bytesPerSec < 1024L * 1024 * 1024)
                return $"{bytesPerSec / (1024.0 * 1024):F1} MB/s";
            return $"{bytesPerSec / (1024.0 * 1024 * 1024):F2} GB/s";
        }
        #endregion

        #region Constructor & Events
        public OnlinePage()
        {
            InitializeComponent();
            App.LanguageChanged += _ => Dispatcher.Invoke(RefreshDynamicText);
            ThemeManager.ThemeChanged += _ => RefreshDynamicText();
            InitTimer();
            SetButtonToStart();
        }

        private void InitTimer()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, __) =>
            {
                _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
                lblTimer.Text = _elapsed.ToString(@"hh\:mm\:ss");
            };
            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _syncTimer.Tick += (_, __) => AutoSyncQueue();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshDynamicText();

            if (_isInitialized)
            {
                UpdateCounts();
                if (_queueWindow != null) _syncTimer?.Start();
                return;
            }

            BuildCategoryTabs();
            InitializeApps();
            UpdateCounts();
            _isInitialized = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdateColumnCount();
            }));
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _timer?.Stop();
            _syncTimer?.Stop();
            _cts?.Cancel();
            _queueWindow?.Close();
        }
        #endregion

        #region Dynamic Text & Button States
        private void RefreshDynamicText()
        {
            RefreshMainButtonText();
            btnUninstallSelected.Content = Loc("str_UninstallSelected", "Uninstall Selected");
            UpdateSelectAllText();
            UpdateStatusLabel();
            _currentCategory = Loc("str_CatAll", "All");
        }

        private void UpdateSelectAllText()
        {
            if (btnSelectAll == null) return;
            var visible = _allApps.Where(a => IsAppVisible(a)).ToList();
            bool allChecked = visible.Any() && visible.All(a => a.CheckBox?.IsChecked == true);
            btnSelectAll.Content = allChecked
                ? Loc("str_DeselectAll", "Deselect All")
                : Loc("str_SelectAll", "Select All");
            btnSelectAll.Tag = allChecked ? "Deselect" : null;
        }

        private void SetButtonToStart()
        {
            _isInstallationDone = false;
            try { btnStart.Content = Loc("str_StartInstall", "Start Installation"); btnStart.Tag = "\uE768"; var s = GetStyleSafe("StartBtn"); if (s != null) btnStart.Style = s; }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToStart error: {ex.Message}"); }
            btnStart.Opacity = 1;
        }

        private void SetButtonToStop()
        {
            _isInstallationDone = false;
            try { btnStart.Content = Loc("str_StopInstall", "Stop Installation"); btnStart.Tag = "\uE71A"; var s = GetStyleSafe("StopBtn"); if (s != null) btnStart.Style = s; }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToStop error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void SetButtonToResume()
        {
            _isInstallationDone = false;
            try { btnStart.Content = Loc("str_ResumeInstall", "Resume Installation"); btnStart.Tag = "\uE768"; var s = GetStyleSafe("ResumeBtn"); if (s != null) btnStart.Style = s; }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToResume error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void SetButtonToDone()
        {
            _isInstallationDone = true;
            try { btnStart.Content = Loc("str_InstallComplete", "Installation Complete!"); btnStart.Tag = "\uE73E"; var s = GetStyleSafe("DoneBtn"); if (s != null) btnStart.Style = s; }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToDone error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void RefreshMainButtonText()
        {
            if (_isRunning) SetButtonToStop();
            else if (_isPaused) SetButtonToResume();
            else if (_isInstallationDone) SetButtonToDone();
            else SetButtonToStart();
        }
        #endregion

        #region Category Tabs & App Data
        private static readonly (string Key, string Fallback)[] CategoryKeys =
        {
            ("str_CatAll", "All"), ("str_CatBrowsers", "Browsers"), ("str_CatCommunication","Communication"),
            ("str_CatProductivity", "Productivity"), ("str_CatMedia", "Media"), ("str_CatGraphics", "Graphics"),
            ("str_CatGaming", "Gaming"), ("str_CatDevelopment", "Development"), ("str_CatUtilities", "Utilities"),
            ("str_Cat3DPrint", "3D Printing"), ("str_CatVPN", "VPN & Security"),
        };

        private void BuildCategoryTabs()
        {
            CategoryTabsPanel.Children.Clear();
            bool first = true;
            foreach (var (key, fallback) in CategoryKeys)
            {
                var btn = new Button { Style = (Style)FindResource("CategoryTab"), Tag = first ? "Active" : null };
                btn.SetResourceReference(Button.ContentProperty, key);
                btn.Click += CategoryTab_Click;
                CategoryTabsPanel.Children.Add(btn);
                first = false;
            }
            _currentCategory = Loc("str_CatAll", "All");
        }

        private Border CreateCategoryHeader(string catName, string catKey = null)
        {
            Color color = catKey != null ? GetCategoryColor(catKey) : Color.FromRgb(14, 165, 233);
            var brush = new SolidColorBrush(color);

            var hdr = new Border
            {
                Margin = new Thickness(6, 18, 6, 12),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Tag = "CategoryHeader"
            };

            var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var accentBar = new Border
            {
                Width = 5,
                Height = 26,
                CornerRadius = new CornerRadius(3),
                Background = brush,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var hdrTb = new TextBlock
            {
                Text = catName,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = brush
            };

            stackPanel.Children.Add(accentBar);
            stackPanel.Children.Add(hdrTb);
            hdr.Child = stackPanel;

            return hdr;
        }

        private static readonly (string NKey, string DKey, string ID, string Color, string CatKey, string Icon, string Domain)[] AppData =
{
    // ==================== 🌐 BROWSERS ====================
    ("str_App_Firefox", "str_Desc_Firefox", "Mozilla.Firefox", "#FF6B35", "str_CatBrowsers", "Mozilla.Firefox.png", "mozilla.org"),
    ("str_App_FirefoxNightly", "str_Desc_FirefoxNightly", "Mozilla.Firefox.MSIX.Nightly", "#0090ED", "str_CatBrowsers", "Mozilla.Firefox.MSIX.Nightly.png", "mozilla.org"),
    ("str_App_Chrome", "str_Desc_Chrome", "Google.Chrome", "#4285F4", "str_CatBrowsers", "Google.Chrome.png", "google.com"),
    ("str_App_Brave", "str_Desc_Brave", "Brave.Brave", "#FB542B", "str_CatBrowsers", "Brave.Brave.png", "brave.com"),
    ("str_App_BraveNightly", "str_Desc_BraveNightly", "Brave.Brave.Nightly", "#FB542B", "str_CatBrowsers", "Brave.Brave.Nightly.png", "brave.com"),
    ("str_App_Opera", "str_Desc_Opera", "Opera.Opera", "#FF1B2D", "str_CatBrowsers", "Opera.Opera.png", "opera.com"),
    ("str_App_OperaGX", "str_Desc_OperaGX", "Opera.OperaGX", "#FF1B2D", "str_CatBrowsers", "Opera.OperaGX.png", "opera.com"),
    ("str_App_Vivaldi", "str_Desc_Vivaldi", "Vivaldi.Vivaldi", "#EF3939", "str_CatBrowsers", "Vivaldi.Vivaldi.png", "vivaldi.com"),
    ("str_App_TorBrowser", "str_Desc_TorBrowser", "TorProject.TorBrowser", "#7D4698", "str_CatBrowsers", "TorProject.TorBrowser.png", "torproject.org"),
    ("str_App_LibreWolf", "str_Desc_LibreWolf", "LibreWolf.LibreWolf", "#00ACFF", "str_CatBrowsers", "LibreWolf.LibreWolf.png", "librewolf.net"),
    ("str_App_MSEdge", "str_Desc_MSEdge", "Microsoft.Edge", "#0078D7", "str_CatBrowsers", "Microsoft.Edge.png", "microsoft.com"),
    ("str_App_ArcBrowser", "str_Desc_ArcBrowser", "TheBrowserCompany.Arc", "#FF6F61", "str_CatBrowsers", "TheBrowserCompany.Arc.png", "arc.net"),
    ("str_App_UngoogledChromium", "str_Desc_UngoogledChromium", "eloston.ungoogled-chromium", "#4285F4", "str_CatBrowsers", "eloston.ungoogled-chromium.png", "github.com"),
    ("str_App_ZenBrowser", "str_Desc_ZenBrowser", "Zen-Team.Zen-Browser", "#F76B15", "str_CatBrowsers", "Zen-Team.Zen-Browser.png", "zen-browser.app"),
    ("str_App_HibbikiChromium", "str_Desc_HibbikiChromium", "Hibbiki.Chromium", "#4285F4", "str_CatBrowsers", "Hibbiki.Chromium.png", "github.com"),

    // ==================== 💬 COMMUNICATION ====================
    ("str_App_Discord", "str_Desc_Discord", "Discord.Discord", "#5865F2", "str_CatCommunication", "Discord.Discord.png", "discord.com"),
    ("str_App_Vesktop", "str_Desc_Vesktop", "Vencord.Vesktop", "#5865F2", "str_CatCommunication", "Vencord.Vesktop.png", "vencord.dev"),
    ("str_App_Zoom", "str_Desc_Zoom", "Zoom.Zoom", "#2D8CFF", "str_CatCommunication", "Zoom.Zoom.png", "zoom.us"),
    ("str_App_Telegram", "str_Desc_Telegram", "Telegram.TelegramDesktop", "#0088CC", "str_CatCommunication", "Telegram.TelegramDesktop.png", "telegram.org"),
    ("str_App_WhatsApp", "str_Desc_WhatsApp", "WhatsApp.WhatsApp", "#25D366", "str_CatCommunication", "WhatsApp_icon.png", "whatsapp.com"),
    ("str_App_Teams", "str_Desc_Teams", "Microsoft.Teams", "#6264A7", "str_CatCommunication", "Microsoft.Teams.png", "microsoft.com"),
    ("str_App_Slack", "str_Desc_Slack", "SlackTechnologies.Slack", "#4A154B", "str_CatCommunication", "SlackTechnologies.Slack.png", "slack.com"),
    ("str_App_Signal", "str_Desc_Signal", "OpenWhisperSystems.Signal", "#3B45FD", "str_CatCommunication", "OpenWhisperSystems.Signal.png", "signal.org"),
    ("str_App_Skype", "str_Desc_Skype", "Microsoft.Skype", "#00AFF0", "str_CatCommunication", "Microsoft.Skype.png", "skype.com"),
    ("str_App_Guilded", "str_Desc_Guilded", "Guilded.Guilded", "#F5C400", "str_CatCommunication", "Guilded.Guilded.png", "guilded.gg"),

    // ==================== 🎵 MEDIA ====================
    ("str_App_Spotify", "str_Desc_Spotify", "Spotify.Spotify", "#1DB954", "str_CatMedia", "Spotify.Spotify.png", "spotify.com"),
    ("str_App_YouTubeMusic", "str_Desc_YouTubeMusic", "Google.YouTubeMusic", "#FF0000", "str_CatMedia", "Google.YouTubeMusic.png", "music.youtube.com"),
    ("str_App_AIMP", "str_Desc_AIMP", "AIMP.AIMP", "#1A4F8B", "str_CatMedia", "AIMP.AIMP.png", "aimp.ru"),
    ("str_App_Audacity", "str_Desc_Audacity", "Audacity.Audacity", "#0000CC", "str_CatMedia", "Audacity.Audacity.png", "audacityteam.org"),
    ("str_App_FxSound", "str_Desc_FxSound", "FxSound.FxSound", "#FF6B00", "str_CatMedia", "FxSound.FxSound.png", "fxsound.com"),
    ("str_App_VLC", "str_Desc_VLC", "VideoLAN.VLC", "#FF8800", "str_CatMedia", "VideoLAN.VLC.png", "videolan.org"),
    ("str_App_MPCBE", "str_Desc_MPCBE", "MPC-BE.MPC-BE", "#FFC107", "str_CatMedia", "MPC-BE.MPC-BE.png", "sourceforge.net"),
    ("str_App_Kodi", "str_Desc_Kodi", "XBMCFoundation.Kodi", "#17B2E7", "str_CatMedia", "XBMCFoundation.Kodi.png", "kodi.tv"),
    ("str_App_Plex", "str_Desc_Plex", "Plex.Plex", "#E5A00D", "str_CatMedia", "Plex.Plex.png", "plex.tv"),
    ("str_App_OBS", "str_Desc_OBS", "OBSProject.OBSStudio", "#302E31", "str_CatMedia", "OBSProject.OBSStudio.png", "obsproject.com"),
    ("str_App_ShareX", "str_Desc_ShareX", "ShareX.ShareX", "#2B85FF", "str_CatMedia", "ShareX.ShareX.png", "getsharex.com"),
    ("str_App_HandBrake", "str_Desc_HandBrake", "HandBrake.HandBrake", "#838383", "str_CatMedia", "HandBrake.HandBrake.png", "handbrake.fr"),
    ("str_App_Medal", "str_Desc_Medal", "MedalB.V.Medal", "#FFC83D", "str_CatMedia", "MedalB.V.Medal.png", "medal.tv"),

    // ==================== 🎨 GRAPHICS ====================
    ("str_App_Krita", "str_Desc_Krita", "KDE.Krita", "#F245FB", "str_CatGraphics", "KDE.Krita.png", "krita.org"),
    ("str_App_Kate", "str_Desc_Kate", "KDE.Kate", "#1D99F3", "str_CatGraphics", "KDE.Kate.png", "kate-editor.org"),
    ("str_App_PaintDotNet", "str_Desc_PaintDotNet", "dotPDN.PaintDotNet", "#0078D7", "str_CatGraphics", "dotPDN.PaintDotNet.png", "getpaint.net"),
    ("str_App_GIMP", "str_Desc_GIMP", "GIMP.GIMP.2", "#5C5543", "str_CatGraphics", "GIMP.GIMP.2.png", "gimp.org"),
    ("str_App_Inkscape", "str_Desc_Inkscape", "Inkscape.Inkscape", "#000000", "str_CatGraphics", "Inkscape.Inkscape.png", "inkscape.org"),
    ("str_App_Blender", "str_Desc_Blender", "BlenderFoundation.Blender", "#E87D0D", "str_CatGraphics", "BlenderFoundation.Blender.png", "blender.org"),
    ("str_App_IrfanView", "str_Desc_IrfanView", "IrfanSkiljan.IrfanView", "#FF6B00", "str_CatGraphics", "IrfanSkiljan.IrfanView.png", "irfanview.com"),

    // ==================== 🎮 GAMING ====================
    ("str_App_EpicGames", "str_Desc_EpicGames", "EpicGames.EpicGamesLauncher", "#313131", "str_CatGaming", "EpicGames.EpicGamesLauncher.png", "epicgames.com"),
    ("str_App_Steam", "str_Desc_Steam", "Valve.Steam", "#1B2838", "str_CatGaming", "Valve.Steam.png", "steampowered.com"),
    ("str_App_EA", "str_Desc_EA", "ElectronicArts.EADesktop", "#FF4747", "str_CatGaming", "ElectronicArts.EADesktop.png", "ea.com"),
    ("str_App_BattleNet", "str_Desc_BattleNet", "Blizzard.BattleNet", "#00AEFF", "str_CatGaming", "Blizzard.BattleNet.png", "battle.net"),
    ("str_App_UbisoftConnect", "str_Desc_UbisoftConnect", "Ubisoft.Connect", "#0083BE", "str_CatGaming", "Ubisoft.Connect.png", "ubisoft.com"),
    ("str_App_GOGGalaxy", "str_Desc_GOGGalaxy", "GOG.Galaxy", "#8C9E5E", "str_CatGaming", "GOG.Galaxy.png", "gog.com"),
    ("str_App_XboxApp", "str_Desc_XboxApp", "Microsoft.XboxApp", "#107C10", "str_CatGaming", "Microsoft.XboxApp.png", "xbox.com"),
    ("str_App_Minecraft", "str_Desc_Minecraft", "Mojang.MinecraftLauncher", "#62B47A", "str_CatGaming", "Mojang.MinecraftLauncher.png", "minecraft.net"),
    ("str_App_PrismLauncher", "str_Desc_PrismLauncher", "PrismLauncher.PrismLauncher", "#9356A5", "str_CatGaming", "PrismLauncher.PrismLauncher.png", "prismlauncher.org"),
    ("str_App_Modrinth", "str_Desc_Modrinth", "Modrinth.ModrinthApp", "#1BD96A", "str_CatGaming", "Modrinth.ModrinthApp.png", "modrinth.com"),
    ("str_App_Bloxstrap", "str_Desc_Bloxstrap", "pizzaboxer.Bloxstrap", "#FF0000", "str_CatGaming", "pizzaboxer.Bloxstrap.png", "bloxstraplabs.com"),
    ("str_App_Fishstrap", "str_Desc_Fishstrap", "Fishstrap.Fishstrap", "#3498DB", "str_CatGaming", "Fishstrap.Fishstrap.png", "github.com"),
    ("str_App_Playnite", "str_Desc_Playnite", "Playnite.Playnite", "#FF6B35", "str_CatGaming", "Playnite.Playnite.png", "playnite.link"),
    ("str_App_Cemu", "str_Desc_Cemu", "Cemu.Cemu", "#0099CC", "str_CatGaming", "Cemu.Cemu.png", "cemu.info"),
    ("str_App_GeForceNOW", "str_Desc_GeForceNOW", "NVIDIA.GeForceNOW", "#76B900", "str_CatGaming", "NVIDIA.GeForceNOW.png", "nvidia.com"),
    ("str_App_Moonlight", "str_Desc_Moonlight", "MoonlightGameStreamingProject.Moonlight", "#76B900", "str_CatGaming", "MoonlightGameStreamingProject.Moonlight.png", "moonlight-stream.org"),
    ("str_App_Sunshine", "str_Desc_Sunshine", "LizardByte.Sunshine", "#FFC83D", "str_CatGaming", "LizardByte.Sunshine.png", "lizardbyte.dev"),

    // ==================== 💻 DEVELOPMENT ====================
    ("str_App_VSCode", "str_Desc_VSCode", "Microsoft.VisualStudioCode", "#007ACC", "str_CatDevelopment", "Microsoft.VisualStudioCode.png", "code.visualstudio.com"),
    ("str_App_VSCodeInsiders", "str_Desc_VSCodeInsiders", "Microsoft.VisualStudioCode.Insiders", "#24BFA5", "str_CatDevelopment", "Microsoft.VisualStudioCode.Insiders.png", "code.visualstudio.com"),
    ("str_App_VS2022", "str_Desc_VS2022", "Microsoft.VisualStudio.2022", "#5C2D91", "str_CatDevelopment", "Microsoft.VisualStudio.2022.png", "visualstudio.microsoft.com"),
    ("str_App_NotepadPP", "str_Desc_NotepadPP", "Notepad++.Notepad++", "#228B22", "str_CatDevelopment", "Notepad++.Notepad++.png", "notepad-plus-plus.org"),
    ("str_App_SublimeText", "str_Desc_SublimeText", "SublimeHQ.SublimeText", "#FF9800", "str_CatDevelopment", "SublimeHQ.SublimeText.png", "sublimetext.com"),
    ("str_App_Git", "str_Desc_Git", "Git.Git", "#F05032", "str_CatDevelopment", "Git.Git.png", "git-scm.com"),
    ("str_App_GitHubDesktop", "str_Desc_GitHubDesktop", "GitHub.GitHubDesktop", "#181717", "str_CatDevelopment", "GitHub.GitHubDesktop.png", "desktop.github.com"),
    ("str_App_Docker", "str_Desc_Docker", "Docker.DockerDesktop", "#0DB7ED", "str_CatDevelopment", "Docker.DockerDesktop.png", "docker.com"),
    ("str_App_NodeJS", "str_Desc_NodeJS", "OpenJS.NodeJS", "#339933", "str_CatDevelopment", "OpenJS.NodeJS.png", "nodejs.org"),
    ("str_App_Python313", "str_Desc_Python313", "Python.Python.3.13", "#3776AB", "str_CatDevelopment", "Python.Python.3.13.png", "python.org"),
    ("str_App_Go", "str_Desc_Go", "GoLang.Go", "#00ADD8", "str_CatDevelopment", "GoLang.Go.png", "go.dev"),
    ("str_App_Rust", "str_Desc_Rust", "Rustlang.Rust.MSVC", "#DEA584", "str_CatDevelopment", "Rustlang.Rust.MSVC.png", "rust-lang.org"),
    ("str_App_Bun", "str_Desc_Bun", "Oven-sh.Bun", "#FBF0DF", "str_CatDevelopment", "Oven-sh.Bun.png", "bun.sh"),
    ("str_App_uv", "str_Desc_uv", "astral-sh.uv", "#DE5FE9", "str_CatDevelopment", "astral-sh.uv.png", "github.com"),
    ("str_App_TemurinJDK17", "str_Desc_TemurinJDK17", "EclipseAdoptium.Temurin.17.JDK", "#FF1F58", "str_CatDevelopment", "EclipseAdoptium.Temurin.17.JDK.png", "adoptium.net"),
    ("str_App_DotNetSDK8", "str_Desc_DotNetSDK8", "Microsoft.DotNet.SDK.8", "#512BD4", "str_CatDevelopment", "Microsoft.DotNet.SDK.8.png", "dotnet.microsoft.com"),
    ("str_App_AndroidStudio", "str_Desc_AndroidStudio", "Google.AndroidStudio", "#3DDC84", "str_CatDevelopment", "Google.AndroidStudio.png", "developer.android.com"),
    ("str_App_UnityHub", "str_Desc_UnityHub", "Unity.UnityHub", "#000000", "str_CatDevelopment", "Unity.UnityHub.png", "unity.com"),
    ("str_App_Arduino", "str_Desc_Arduino", "ArduinoSA.IDE.stable", "#00979D", "str_CatDevelopment", "ArduinoSA.IDE.stable.png", "arduino.cc"),
    ("str_App_CMake", "str_Desc_CMake", "Kitware.CMake", "#064F8C", "str_CatDevelopment", "Kitware.CMake.png", "cmake.org"),
    ("str_App_Neovim", "str_Desc_Neovim", "Neovim.Neovim", "#57A143", "str_CatDevelopment", "Neovim.Neovim.png", "neovim.io"),
    ("str_App_Vim", "str_Desc_Vim", "vim.vim", "#019733", "str_CatDevelopment", "vim.vim.nightly.png", "vim.org"),
    ("str_App_OhMyPosh", "str_Desc_OhMyPosh", "JanDeDobbeleer.OhMyPosh", "#FF6B9D", "str_CatDevelopment", "JanDeDobbeleer.OhMyPosh.png", "ohmyposh.dev"),
    ("str_App_Postman", "str_Desc_Postman", "Postman.Postman", "#FF6C37", "str_CatDevelopment", "Postman.Postman.png", "postman.com"),
    ("str_App_DBeaver", "str_Desc_DBeaver", "DBeaver.DBeaver", "#382923", "str_CatDevelopment", "DBeaver.DBeaver.png", "dbeaver.io"),
    ("str_App_MongoDBCompass", "str_Desc_MongoDBCompass", "MongoDB.Compass", "#47A248", "str_CatDevelopment", "MongoDB.Compass.png", "mongodb.com"),
    ("str_App_MySQL", "str_Desc_MySQL", "Oracle.MySQL", "#4479A1", "str_CatDevelopment", "Oracle.MySQL.png", "mysql.com"),
    ("str_App_PostgreSQL", "str_Desc_PostgreSQL", "PostgreSQL.PostgreSQL", "#336791", "str_CatDevelopment", "PostgreSQL.PostgreSQL.png", "postgresql.org"),
    ("str_App_Tabby", "str_Desc_Tabby", "Eugeny.Tabby", "#1923A3", "str_CatDevelopment", "Eugeny.Tabby.png", "tabby.sh"),

    // ==================== 🛠 UTILITIES ====================
    ("str_App_7Zip", "str_Desc_7Zip", "7zip.7zip", "#3C3C3C", "str_CatUtilities", "7zip.7zip.png", "7-zip.org"),
    ("str_App_NanaZip", "str_Desc_NanaZip", "M2Team.NanaZip", "#3C3C3C", "str_CatUtilities", "M2Team.NanaZip.png", "github.com"),
    ("str_App_WinRAR", "str_Desc_WinRAR", "RARLab.WinRAR", "#CB1818", "str_CatUtilities", "RARLab.WinRAR.png", "rarlab.com"),
    ("str_App_PeaZip", "str_Desc_PeaZip", "Giorgiotani.Peazip", "#FFC107", "str_CatUtilities", "Giorgiotani.Peazip.png", "peazip.org"),
    ("str_App_PowerToys", "str_Desc_PowerToys", "Microsoft.PowerToys", "#0078D4", "str_CatUtilities", "Microsoft.PowerToys.png", "microsoft.com"),
    ("str_App_Rufus", "str_Desc_Rufus", "Rufus.Rufus", "#FFA500", "str_CatUtilities", "Rufus.Rufus.png", "rufus.ie"),
    ("str_App_Ventoy", "str_Desc_Ventoy", "Ventoy.Ventoy", "#FF6B35", "str_CatUtilities", "Ventoy.Ventoy.png", "ventoy.net"),
    ("str_App_Etcher", "str_Desc_Etcher", "Balena.Etcher", "#5BC0DE", "str_CatUtilities", "Balena.Etcher.png", "balena.io"),
    ("str_App_Everything", "str_Desc_Everything", "voidtools.Everything", "#2ECC71", "str_CatUtilities", "voidtools.Everything.png", "voidtools.com"),
    ("str_App_Files", "str_Desc_Files", "FilesCommunity.Files", "#0078D4", "str_CatUtilities", "FilesCommunity.Files.png", "files.community"),
    ("str_App_TotalCommander", "str_Desc_TotalCommander", "Ghisler.TotalCommander", "#FF6B00", "str_CatUtilities", "Ghisler.TotalCommander.png", "ghisler.com"),
    ("str_App_qBittorrent", "str_Desc_qBittorrent", "qBittorrent.qBittorrent", "#3498DB", "str_CatUtilities", "qBittorrent.qBittorrent.png", "qbittorrent.org"),
    ("str_App_Deluge", "str_Desc_Deluge", "Deluge.Deluge", "#9DB6D2", "str_CatUtilities", "Deluge.Deluge.png", "deluge-torrent.org"),
    ("str_App_Transmission", "str_Desc_Transmission", "Transmission.Transmission", "#CD2026", "str_CatUtilities", "Transmission.Transmission.png", "transmissionbt.com"),
    ("str_App_WebTorrent", "str_Desc_WebTorrent", "WebTorrent.WebTorrentDesktop", "#FFB000", "str_CatUtilities", "WebTorrent.WebTorrentDesktop.png", "webtorrent.io"),
    ("str_App_JDownloader", "str_Desc_JDownloader", "AppWork.JDownloader", "#34A853", "str_CatUtilities", "AppWork.JDownloader.png", "jdownloader.org"),
    ("str_App_FDM", "str_Desc_FDM", "SoftDeluxe.FreeDownloadManager", "#00B847", "str_CatUtilities", "SoftDeluxe.FreeDownloadManager.png", "freedownloadmanager.org"),
    ("str_App_ABDownloadManager", "str_Desc_ABDownloadManager", "ABDownloadManager.ABDownloadManager", "#3498DB", "str_CatUtilities", "ABDownloadManager.ABDownloadManager.png", "github.com"),
    ("str_App_VirtualBox", "str_Desc_VirtualBox", "Oracle.VirtualBox", "#183A61", "str_CatUtilities", "Oracle.VirtualBox.png", "virtualbox.org"),
    ("str_App_AnyDesk", "str_Desc_AnyDesk", "AnyDesk.AnyDesk", "#EF443B", "str_CatUtilities", "AnyDesk.AnyDesk.png", "anydesk.com"),
    ("str_App_TeamViewer", "str_Desc_TeamViewer", "TeamViewer.TeamViewer", "#0E8EE9", "str_CatUtilities", "TeamViewer.TeamViewer.png", "teamviewer.com"),
    ("str_App_RustDesk", "str_Desc_RustDesk", "RustDesk.RustDesk", "#1683C5", "str_CatUtilities", "RustDesk.RustDesk.png", "rustdesk.com"),
    ("str_App_Parsec", "str_Desc_Parsec", "Parsec.Parsec", "#00B5FF", "str_CatUtilities", "Parsec.Parsec.png", "parsec.app"),
    ("str_App_PuTTY", "str_Desc_PuTTY", "PuTTY.PuTTY", "#000000", "str_CatUtilities", "PuTTY.PuTTY.png", "putty.org"),
    ("str_App_WinSCP", "str_Desc_WinSCP", "WinSCP.WinSCP", "#0078D4", "str_CatUtilities", "WinSCP.WinSCP.png", "winscp.net"),
    ("str_App_FileZilla", "str_Desc_FileZilla", "FileZilla.FileZilla", "#CC0000", "str_CatUtilities", "FileZilla.FileZilla.png", "filezilla-project.org"),
    ("str_App_GPUZ", "str_Desc_GPUZ", "TechPowerUp.GPU-Z", "#FF6B00", "str_CatUtilities", "TechPowerUp.GPU-Z.png", "techpowerup.com"),
    ("str_App_CPUZ", "str_Desc_CPUZ", "CPUID.CPU-Z", "#0066CC", "str_CatUtilities", "CPUID.CPU-Z.png", "cpuid.com"),
    ("str_App_HWMonitor", "str_Desc_HWMonitor", "CPUID.HWMonitor", "#0066CC", "str_CatUtilities", "CPUID.HWMonitor.png", "cpuid.com"),
    ("str_App_HWiNFO", "str_Desc_HWiNFO", "REALiX.HWiNFO", "#003366", "str_CatUtilities", "REALiX.HWiNFO.png", "hwinfo.com"),
    ("str_App_CrystalDiskInfo", "str_Desc_CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo", "#0099CC", "str_CatUtilities", "CrystalDewWorld.CrystalDiskInfo.png", "crystalmark.info"),
    ("str_App_Afterburner", "str_Desc_Afterburner", "Guru3D.Afterburner", "#FF6B00", "str_CatUtilities", "Guru3D.Afterburner.png", "msi.com"),
    ("str_App_FanControl", "str_Desc_FanControl", "Rem0o.FanControl", "#0099CC", "str_CatUtilities", "Rem0o.FanControl.png", "github.com"),
    ("str_App_OpenRGB", "str_Desc_OpenRGB", "OpenRGB.OpenRGB", "#D81B60", "str_CatUtilities", "OpenRGB.OpenRGB.png", "openrgb.org"),
    ("str_App_SignalRGB", "str_Desc_SignalRGB", "WhirlwindFX.SignalRgb", "#7C3AED", "str_CatUtilities", "WhirlwindFX.SignalRgb.png", "signalrgb.com"),
    ("str_App_TwinkleTray", "str_Desc_TwinkleTray", "xanderfrangos.twinkletray", "#3498DB", "str_CatUtilities", "xanderfrangos.twinkletray.png", "twinkletray.com"),
    ("str_App_WizTree", "str_Desc_WizTree", "AntibodySoftware.WizTree", "#0E76A8", "str_CatUtilities", "AntibodySoftware.WizTree.png", "diskanalyzer.com"),
    ("str_App_BCU", "str_Desc_BCU", "Klocman.BulkCrapUninstaller", "#FF6B00", "str_CatUtilities", "Klocman.BulkCrapUninstaller.png", "github.com"),
    ("str_App_GeekUninstaller", "str_Desc_GeekUninstaller", "GeekUninstaller.GeekUninstaller", "#34A853", "str_CatUtilities", "GeekUninstaller.GeekUninstaller.png", "geekuninstaller.com"),
    ("str_App_RevoUninstaller", "str_Desc_RevoUninstaller", "RevoUninstaller.RevoUninstaller", "#1976D2", "str_CatUtilities", "RevoUninstaller.RevoUninstaller.png", "revouninstaller.com"),
    ("str_App_BleachBit", "str_Desc_BleachBit", "BleachBit.BleachBit", "#0099CC", "str_CatUtilities", "BleachBit.BleachBit.png", "bleachbit.org"),
    ("str_App_Recuva", "str_Desc_Recuva", "Piriform.Recuva", "#00B0F0", "str_CatUtilities", "Piriform.Recuva.png", "ccleaner.com"),
    ("str_App_DDU", "str_Desc_DDU", "Wagnardsoft.DisplayDriverUninstaller", "#FF0000", "str_CatUtilities", "Wagnardsoft.DisplayDriverUninstaller.png", "wagnardsoft.com"),
    ("str_App_Autoruns", "str_Desc_Autoruns", "Microsoft.Sysinternals.Autoruns", "#0078D4", "str_CatUtilities", "Microsoft.Sysinternals.Autoruns.png", "microsoft.com"),
    ("str_App_Wireshark", "str_Desc_Wireshark", "WiresharkFoundation.Wireshark", "#1679A7", "str_CatUtilities", "WiresharkFoundation.Wireshark.png", "wireshark.org"),
    ("str_App_GlassWire", "str_Desc_GlassWire", "GlassWire.GlassWire", "#00C9A7", "str_CatUtilities", "GlassWire.GlassWire.png", "glasswire.com"),
    ("str_App_Greenshot", "str_Desc_Greenshot", "Greenshot.Greenshot", "#85B83A", "str_CatUtilities", "Greenshot.Greenshot.png", "getgreenshot.org"),
    ("str_App_CopyQ", "str_Desc_CopyQ", "hluk.copyq", "#3498DB", "str_CatUtilities", "hluk.copyq.png", "hluk.github.io"),
    ("str_App_FileConverter", "str_Desc_FileConverter", "AdrienAllard.FileConverter", "#7C3AED", "str_CatUtilities", "AdrienAllard.FileConverter.png", "file-converter.org"),
    ("str_App_BulkRename", "str_Desc_BulkRename", "TGRMNSoftware.BulkRenameUtility", "#FF6B00", "str_CatUtilities", "TGRMNSoftware.BulkRenameUtility.png", "bulkrenameutility.co.uk"),
    ("str_App_WinMerge", "str_Desc_WinMerge", "WinMerge.WinMerge", "#3498DB", "str_CatUtilities", "WinMerge.WinMerge.png", "winmerge.org"),
    ("str_App_Rainmeter", "str_Desc_Rainmeter", "Rainmeter.Rainmeter", "#2D6CDF", "str_CatUtilities", "Rainmeter.Rainmeter.png", "rainmeter.net"),
    ("str_App_YASB", "str_Desc_YASB", "AmN.yasb", "#7C3AED", "str_CatUtilities", "AmN.yasb.png", "github.com"),
    ("str_App_LivelyWallpaper", "str_Desc_LivelyWallpaper", "rocksdanister.LivelyWallpaper", "#FF6B9D", "str_CatUtilities", "rocksdanister.LivelyWallpaper.png", "rocksdanister.github.io"),
    ("str_App_FlowLauncher", "str_Desc_FlowLauncher", "Flow-Launcher.Flow-Launcher", "#26A0DA", "str_CatUtilities", "Flow-Launcher.Flow-Launcher.png", "flowlauncher.com"),
    ("str_App_SuperF4", "str_Desc_SuperF4", "StefanSundin.Superf4", "#FF0000", "str_CatUtilities", "StefanSundin.Superf4.png", "stefansundin.github.io"),
    ("str_App_Windhawk", "str_Desc_Windhawk", "RamenSoftware.Windhawk", "#7C3AED", "str_CatUtilities", "RamenSoftware.Windhawk.png", "windhawk.net"),
    ("str_App_Cryptomator", "str_Desc_Cryptomator", "Cryptomator.Cryptomator", "#48BB78", "str_CatUtilities", "Cryptomator.Cryptomator.png", "cryptomator.org"),
    ("str_App_SyncThing", "str_Desc_SyncThing", "SyncThing.SyncThing", "#0891D1", "str_CatUtilities", "SyncThing.SyncThing.png", "syncthing.net"),
    ("str_App_ZeroTier", "str_Desc_ZeroTier", "ZeroTier.ZeroTierOne", "#FFB432", "str_CatUtilities", "ZeroTier.ZeroTierOne.png", "zerotier.com"),
    ("str_App_Fastfetch", "str_Desc_Fastfetch", "Fastfetch-cli.Fastfetch", "#1E90FF", "str_CatUtilities", "Fastfetch-cli.Fastfetch.png", "github.com"),

    // ==================== 📝 PRODUCTIVITY ====================
    ("str_App_Notion", "str_Desc_Notion", "Notion.Notion", "#000000", "str_CatProductivity", "Notion.Notion.png", "notion.so"),
    ("str_App_Obsidian", "str_Desc_Obsidian", "Obsidian.Obsidian", "#7B68EE", "str_CatProductivity", "Obsidian.Obsidian.png", "obsidian.md"),
    ("str_App_Logseq", "str_Desc_Logseq", "Logseq.Logseq", "#002B36", "str_CatProductivity", "Logseq.Logseq.png", "logseq.com"),
    ("str_App_SumatraPDF", "str_Desc_SumatraPDF", "SumatraPDF.SumatraPDF", "#F39C12", "str_CatProductivity", "SumatraPDF.SumatraPDF.png", "sumatrapdfreader.org"),
    ("str_App_AdobeReader", "str_Desc_AdobeReader", "Adobe.Acrobat.Reader.64-bit", "#EC1C24", "str_CatProductivity", "Adobe.Acrobat.Reader.64-bit.png", "adobe.com"),
    ("str_App_MSOffice", "str_Desc_MSOffice", "Microsoft.Office", "#D83B01", "str_CatProductivity", "Microsoft.Office.png", "office.com"),
    ("str_App_LibreOffice", "str_Desc_LibreOffice", "TheDocumentFoundation.LibreOffice", "#18A303", "str_CatProductivity", "TheDocumentFoundation.LibreOffice.png", "libreoffice.org"),
    ("str_App_Dropbox", "str_Desc_Dropbox", "Dropbox.Dropbox", "#0061FF", "str_CatProductivity", "Dropbox.Dropbox.png", "dropbox.com"),

    // ==================== 🖨️ 3D PRINTING ====================
    ("str_App_BambuStudio", "str_Desc_BambuStudio", "Bambulab.Bambustudio", "#FF6B35", "str_Cat3DPrint", "Bambulab.Bambustudio.png", "bambulab.com"),
    ("str_App_PrusaSlicer", "str_Desc_PrusaSlicer", "Prusa3D.PrusaSlicer", "#FF6B35", "str_Cat3DPrint", "Prusa3D.PrusaSlicer.png", "prusa3d.com"),
    ("str_App_OrcaSlicer", "str_Desc_OrcaSlicer", "SoftFever.OrcaSlicer", "#FF6B00", "str_Cat3DPrint", "SoftFever.OrcaSlicer.png", "github.com"),
    ("str_App_CrealityPrint", "str_Desc_CrealityPrint", "Creality.CrealityPrint", "#00B4DB", "str_Cat3DPrint", "Creality.CrealityPrint.png", "creality.com"),
    ("str_App_Cura", "str_Desc_Cura", "Ultimaker.Cura", "#0078D4", "str_Cat3DPrint", "Ultimaker.Cura.png", "ultimaker.com"),

        // ==================== 🔒 VPN & SECURITY ====================
    ("str_App_OpenVPN", "str_Desc_OpenVPN", "OpenVPNTechnologies.OpenVPN", "#EA7E20", "str_CatVPN", "OpenVPNTechnologies.OpenVPN.png", "openvpn.net"),
    ("str_App_WireGuard", "str_Desc_WireGuard", "WireGuard.WireGuard", "#88171A", "str_CatVPN", "WireGuard.WireGuard.png", "wireguard.com"),
    ("str_App_Tailscale", "str_Desc_Tailscale", "Tailscale.Tailscale", "#000000", "str_CatVPN", "Tailscale.Tailscale.png", "tailscale.com"),
    ("str_App_NordVPN", "str_Desc_NordVPN", "NordSecurity.NordVPN", "#4687FF", "str_CatVPN", "NordSecurity.NordVPN.png", "nordvpn.com"),
    ("str_App_ExpressVPN", "str_Desc_ExpressVPN", "ExpressVPN.ExpressVPN", "#DA3940", "str_CatVPN", "ExpressVPN.ExpressVPN.png", "expressvpn.com"),
    ("str_App_ProtonVPN", "str_Desc_ProtonVPN", "Proton.ProtonVPN", "#6D4AFF", "str_CatVPN", "Proton.ProtonVPN.png", "protonvpn.com"),
    ("str_App_Surfshark", "str_Desc_Surfshark", "Surfshark.Surfshark", "#1EBFBF", "str_CatVPN", "Surfshark.Surfshark.png", "surfshark.com"),
    ("str_App_MullvadVPN", "str_Desc_MullvadVPN", "MullvadVPN.MullvadVPN", "#294D73", "str_CatVPN", "MullvadVPN.MullvadVPN.png", "mullvad.net"),
    ("str_App_Windscribe", "str_Desc_Windscribe", "Windscribe.Windscribe", "#1BAEEA", "str_CatVPN", "Windscribe.Windscribe.png", "windscribe.com"),
    ("str_App_PIA", "str_Desc_PIA", "PrivateInternetAccess.PrivateInternetAccess", "#5DDF5A", "str_CatVPN", "PrivateInternetAccess.PrivateInternetAccess.png", "privateinternetaccess.com"),
    ("str_App_IPVanish", "str_Desc_IPVanish", "IPVanish.IPVanish", "#74B43A", "str_CatVPN", "IPVanish.IPVanish.png", "ipvanish.com"),
    ("str_App_Bitwarden", "str_Desc_Bitwarden", "Bitwarden.Bitwarden", "#175DDC", "str_CatVPN", "Bitwarden.Bitwarden.png", "bitwarden.com"),
    ("str_App_KeePassXC", "str_Desc_KeePassXC", "KeePassXCTeam.KeePassXC", "#6C3483", "str_CatVPN", "KeePassXCTeam.KeePassXC.png", "keepassxc.org"),
    ("str_App_1Password", "str_Desc_1Password", "1Password.1Password", "#0572EC", "str_CatVPN", "1Password.1Password.png", "1password.com"),
    ("str_App_Dashlane", "str_Desc_Dashlane", "Dashlane.Dashlane", "#0E7C50", "str_CatVPN", "Dashlane.Dashlane.png", "dashlane.com"),
    ("str_App_LastPass", "str_Desc_LastPass", "LastPass.LastPass", "#D32D27", "str_CatVPN", "LastPass.LastPass.png", "lastpass.com"),
    ("str_App_Malwarebytes", "str_Desc_Malwarebytes", "Malwarebytes.Malwarebytes", "#002D62", "str_CatVPN", "Malwarebytes.Malwarebytes.png", "malwarebytes.com"),

    // ==================== ⌨️ PERIPHERALS ====================
    ("str_App_LogitechGHUB", "str_Desc_LogitechGHUB", "Logitech.GHUB", "#00B8FC", "str_CatUtilities", "Logitech.GHUB.png", "logitech.com"),
    ("str_App_RazerSynapse4", "str_Desc_RazerSynapse4", "Razer.RazerSynapse", "#44D62C", "str_CatUtilities", "Razer.RazerSynapse.png", "razer.com"),
    ("str_App_SteelSeriesGG", "str_Desc_SteelSeriesGG", "SteelSeries.GG", "#FF5500", "str_CatUtilities", "SteelSeries.GG.png", "steelseries.com"),
};

        private void InitializeApps()
        {
            Column1Container.Items.Clear();
            Column2Container.Items.Clear();
            Column3Container.Items.Clear();

            Col2Def.Width = new GridLength(0);
            Col3Def.Width = new GridLength(0);

            var grouped = AppData
                .GroupBy(row => row.CatKey)
                .Select(g => new
                {
                    CatKey = g.Key,
                    CatName = Loc(g.Key, g.Key),
                    Items = g.ToList()
                })
                .ToList();

            foreach (var group in grouped)
            {
                var header = CreateCategoryHeader(group.CatName, group.CatKey);
                header.HorizontalAlignment = HorizontalAlignment.Stretch;
                Column1Container.Items.Add(header);

                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Tag = "CategoryWrap"
                };

                foreach (var row in group.Items)
                {
                    var card = BuildAppCard(row.NKey, row.DKey, row.ID, row.Color,
                                            row.CatKey, row.Icon, row.Domain);
                    wrap.Children.Add(card);
                }

                Column1Container.Items.Add(wrap);
                _categoryHeaders.Add((header, null, null));
            }

            UpdateCardsWidth();
        }

        private Border BuildAppCard(string nameKey, string descKey, string id,
                                    string colorHex, string catKey,
                                    string iconFile, string domain)
        {
            var checkBox = new CheckBox
            {
                Style = (Style)FindResource("ModernCheckBox"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var card = BuildCard(nameKey, descKey, id, colorHex, Loc(catKey, catKey),
                                 checkBox, iconFile,
                                 out Border iconBorder, out Image iconImg,
                                 out TextBlock iconFallback, out ProgressBar downloadBar,
                                 out TextBlock statusQuickText);

            card.Tag = Loc(catKey, catKey);
            card.Margin = new Thickness(4);

            var appRef = new AppInfo
            {
                NameKey = nameKey,
                DescKey = descKey,
                ID = id,
                Category = Loc(catKey, catKey),
                CategoryKey = catKey,              // ✅ حفظ الـ Key
                Color = colorHex,
                IconFile = iconFile,
                Domain = domain,
                CheckBox = checkBox,
                Card = card,
                IconBorder = iconBorder,
                IconImage = iconImg,
                IconFallbackText = iconFallback,
                DownloadProgressBar = downloadBar,
                StatusQuickText = statusQuickText
            };
            _allApps.Add(appRef);

            checkBox.Checked += (_, __) =>
            {
                if (appRef.SelectionOrder == 0) appRef.SelectionOrder = ++_selectionCounter;
                appRef.CancelRequested = false;
                UpdateCounts();
                if (_isInstallationDone)
                {
                    _isInstallationDone = false;
                    SetButtonToStart();
                    UpdateStatusLabel();
                }
                ShowAppQuickStatus(appRef, $"{Loc("str_Queued", "Queued")} 📋", BrushAccent);
            };

            // ✅ تعديل Unchecked ليدعم الإلغاء الفوري
            checkBox.Unchecked += (_, __) =>
            {
                appRef.SelectionOrder = 0;
                UpdateCounts();

                if (_isRunning && _currentInstallingApp == appRef)
                {
                    // ✅ Cancel فوري للتطبيق الحالي
                    appRef.CancelApp();
                }
                else if (appRef.Status == AppStatus.None)
                {
                    ShowAppQuickStatus(appRef,
                        $"{Loc("str_Removed", "Removed")} ❌",
                        GetBrush("Warning", BrushWarning));
                }

                if (_isInstallationDone)
                {
                    _isInstallationDone = false;
                    SetButtonToStart();
                    UpdateStatusLabel();
                }
            };

            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => LoadLocalIcon(appRef)));

            return card;
        }

        #region Tabs Horizontal Drag
        private void CategoryTabsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void CategoryTabsScroll_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            _tabsMouseDown = true;
            _isTabsDragging = false;
            _tabsDragStartPoint = e.GetPosition(sv);
            _tabsScrollStartOffset = sv.HorizontalOffset;
        }

        private void CategoryTabsScroll_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_tabsMouseDown || sender is not ScrollViewer sv) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point current = e.GetPosition(sv);
            double deltaX = current.X - _tabsDragStartPoint.X;

            if (!_isTabsDragging && Math.Abs(deltaX) > 5)
            {
                _isTabsDragging = true;
                sv.CaptureMouse();
                sv.Cursor = Cursors.SizeAll;
            }

            if (_isTabsDragging)
            {
                sv.ScrollToHorizontalOffset(_tabsScrollStartOffset - deltaX);
            }
        }

        private void CategoryTabsScroll_MouseUp(object sender, MouseEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            if (_isTabsDragging)
            {
                sv.ReleaseMouseCapture();
                sv.Cursor = Cursors.Hand;
                if (e is MouseButtonEventArgs mbe)
                    mbe.Handled = true;
            }

            _tabsMouseDown = false;
            _isTabsDragging = false;
        }

        private void CategoryTab_Click(object sender, RoutedEventArgs e)
        {
            if (_isTabsDragging) { e.Handled = true; return; }

            if (sender is not Button btn) return;
            foreach (UIElement child in CategoryTabsPanel.Children)
                if (child is Button b) b.Tag = null;
            btn.Tag = "Active";

            // ✅ نحفظ الـ content كـ string صح
            _currentCategory = btn.Content?.ToString() ?? Loc("str_CatAll", "All");

            FilterApps();
        }
        #endregion
        #endregion

        #region Build Cards & Embedded PNG Loading
        private Border BuildCard(string nameKey, string descKey, string id, string colorHex, string catName, CheckBox checkBox, string iconFile, out Border iconBorder, out Image iconImage, out TextBlock iconFallback, out ProgressBar downloadBar, out TextBlock statusQuickText)
        {
            var card = new Border { Style = (Style)FindResource("AppCardStyle"), Tag = catName };
            var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(checkBox, 0);

            iconBorder = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Margin = new Thickness(12, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Cursor = Cursors.Arrow, Tag = "IconBorder" };
            iconImage = new Image { Width = 50, Height = 50, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.Fant);
            RenderOptions.SetEdgeMode(iconImage, EdgeMode.Aliased);
            iconFallback = new TextBlock { Text = GetAppFirstLetter(nameKey), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

            var iconContainer = new Grid();
            iconContainer.Children.Add(iconImage);
            iconContainer.Children.Add(iconFallback);
            iconBorder.Child = iconContainer;
            Grid.SetColumn(iconBorder, 1);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameTb = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            nameTb.SetResourceReference(TextBlock.TextProperty, nameKey);
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "DynamicMainText");

            // ✅ استخدام Grid بدل ProgressBar لشريط التحميل (حل أكثر موثوقية)
            var progressContainer = new Grid
            {
                Height = 4,
                Margin = new Thickness(0, 4, 0, 2),
                Background = new SolidColorBrush(Color.FromArgb(40, 100, 100, 100)),
                Visibility = Visibility.Collapsed
            };
            progressContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) }); // fill
            progressContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // empty

            var progressFill = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new LinearGradientBrush(
                    Color.FromRgb(14, 165, 233),
                    Color.FromRgb(139, 92, 246), 0)
            };
            Grid.SetColumn(progressFill, 0);
            progressContainer.Children.Add(progressFill);

            var descTb = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            descTb.SetResourceReference(TextBlock.TextProperty, descKey);
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "DynamicSubText");

            stack.Children.Add(nameTb);
            stack.Children.Add(progressContainer);
            stack.Children.Add(descTb);
            Grid.SetColumn(stack, 2);

            statusQuickText = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0),
                Visibility = Visibility.Collapsed,
                TextAlignment = TextAlignment.Right,
                MinWidth = 95
            };
            Grid.SetColumn(statusQuickText, 3);

            var extBtn = new Button { Style = (Style)FindResource("ExternalLinkBtn"), Tag = id, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            extBtn.SetResourceReference(ToolTipService.ToolTipProperty, "str_OpenWebsite");
            extBtn.Click += ExternalBtn_Click;
            Grid.SetColumn(extBtn, 4);

            grid.Children.Add(checkBox);
            grid.Children.Add(iconBorder);
            grid.Children.Add(stack);
            grid.Children.Add(statusQuickText);
            grid.Children.Add(extBtn);
            card.Child = grid;

            card.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                var cur = src;
                while (cur != null && cur != card)
                {
                    if (cur is Button btn && btn.Tag?.ToString() != null)
                    {
                        if (btn.Style == FindResource("ExternalLinkBtn")) return;
                    }
                    if (cur is CheckBox) return;
                    cur = VisualTreeHelper.GetParent(cur);
                }
                e.Handled = true;
                checkBox.IsChecked = !checkBox.IsChecked;
            };

            // تخزين مراجع إضافية في AppInfo
            downloadBar = null; // لم نعد نستخدم ProgressBar
            return card;
        }

        private string GetAppFirstLetter(string nameKey)
        {
            try { string name = Loc(nameKey, nameKey); if (!string.IsNullOrEmpty(name)) return name[0].ToString().ToUpper(); } catch { }
            return "?";
        }

        private void LoadLocalIcon(AppInfo app)
        {
            if (string.IsNullOrWhiteSpace(app.IconFile)) { ShowFallbackIcon(app); return; }

            try
            {
                string packUri = $"pack://application:,,,/Assets/IconsOnline/{app.IconFile}";
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.DecodePixelWidth = 64;
                bitmapImage.UriSource = new Uri(packUri, UriKind.Absolute);
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                if (app.IconImage != null)
                {
                    app.IconImage.Source = bitmapImage;
                    app.IconImage.Visibility = Visibility.Visible;
                }
                if (app.IconBorder != null) app.IconBorder.Background = Brushes.Transparent;
                if (app.IconFallbackText != null) app.IconFallbackText.Visibility = Visibility.Collapsed;
                app.IconLoaded = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Icon Error [{app.IconFile}]: {ex.Message}");
                ShowFallbackIcon(app);
            }
        }

        private void ShowFallbackIcon(AppInfo app)
        {
            Dispatcher.Invoke(() =>
            {
                if (app.IconBorder != null)
                {
                    try { app.IconBorder.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(app.Color)); }
                    catch { app.IconBorder.Background = BrushAccent; }
                }
                if (app.IconFallbackText != null) app.IconFallbackText.Visibility = Visibility.Visible;
                if (app.IconImage != null) app.IconImage.Visibility = Visibility.Collapsed;
            });
        }
        #endregion

        #region Card Indicators
        private void ShowCardInstallingIndicator(AppInfo app)
        {
            if (app.Card == null) return;
            var grid = app.Card.Child as Grid;
            if (grid == null) return;

            var stack = grid.Children.OfType<StackPanel>().FirstOrDefault();
            if (stack == null) return;

            var existing = stack.Children.OfType<StackPanel>()
                .FirstOrDefault(s => s.Tag?.ToString() == CARD_INSTALLING_TAG);
            if (existing != null) stack.Children.Remove(existing);

            var indicator = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0),
                Tag = CARD_INSTALLING_TAG
            };

            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = GetBrush("Warning", BrushWarning),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var pulseAnim = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            dot.BeginAnimation(UIElement.OpacityProperty, pulseAnim);

            var lbl = new TextBlock
            {
                Text = Loc("str_Installing", "Installing..."),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("Warning", BrushWarning),
                VerticalAlignment = VerticalAlignment.Center
            };

            indicator.Children.Add(dot);
            indicator.Children.Add(lbl);
            stack.Children.Add(indicator);

            app.Card.BorderBrush = GetBrush("Warning", BrushWarning);
            app.Card.BorderThickness = new Thickness(2);
        }

        private void HideCardInstallingIndicator(AppInfo app)
        {
            if (app.Card == null) return;
            var grid = app.Card.Child as Grid;
            if (grid == null) return;

            var stack = grid.Children.OfType<StackPanel>().FirstOrDefault();
            if (stack == null) return;

            var indicator = stack.Children.OfType<StackPanel>()
                .FirstOrDefault(s => s.Tag?.ToString() == CARD_INSTALLING_TAG);

            if (indicator != null)
            {
                var dot = indicator.Children.OfType<Border>().FirstOrDefault();
                dot?.BeginAnimation(UIElement.OpacityProperty, null);

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                fadeOut.Completed += (_, _) => stack.Children.Remove(indicator);
                indicator.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }

            app.Card.BorderThickness = new Thickness(1);
            app.Card.SetResourceReference(Border.BorderBrushProperty, "DynamicBorderBrush");
        }

        private void ShowAppQuickStatus(AppInfo app, string text, Brush colorBrush)
        {
            if (app?.StatusQuickText == null) return;

            if (_appStatusTimers.TryGetValue(app, out var existing))
            {
                existing.Stop();
                _appStatusTimers.Remove(app);
            }

            var tb = app.StatusQuickText;
            tb.BeginAnimation(UIElement.OpacityProperty, null);
            tb.Text = text;
            tb.Foreground = colorBrush;
            tb.Opacity = 0;
            tb.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            tb.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            string capturedText = text;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _appStatusTimers[app] = timer;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                _appStatusTimers.Remove(app);
                if (tb.Text != capturedText) return;

                tb.BeginAnimation(UIElement.OpacityProperty, null);
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (tb.Text == capturedText)
                    {
                        tb.Text = "";
                        tb.Opacity = 1;
                        tb.Visibility = Visibility.Collapsed;
                    }
                };
                tb.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            timer.Start();
        }
        #endregion

        #region Download Progress & Main Install Logic

        // ✅ دالة جديدة للحصول على حاوية التحميل والتعبئة من البطاقة
        private (Grid Container, Border Fill) GetDownloadProgressElements(AppInfo app)
        {
            if (app.Card == null) return (null, null);
            var grid = app.Card.Child as Grid;
            if (grid == null) return (null, null);

            var stack = grid.Children.OfType<StackPanel>().FirstOrDefault();
            if (stack == null) return (null, null);

            // البحث عن Grid التحميل
            var progressContainer = stack.Children.OfType<Grid>().FirstOrDefault(g => g.Height == 4 && g.Background is SolidColorBrush);
            if (progressContainer == null) return (null, null);

            var fill = progressContainer.Children.OfType<Border>().FirstOrDefault();
            return (progressContainer, fill);
        }

        private void ShowAppDownloadBar(AppInfo app)
        {
            var (container, fill) = GetDownloadProgressElements(app);
            if (container == null) return;

            container.Visibility = Visibility.Visible;
            container.Opacity = 1;

            // إعادة تعيين عرض العمودين
            if (container.ColumnDefinitions.Count >= 2)
            {
                container.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Star);
                container.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            }
        }

        private void UpdateAppDownloadBar(AppInfo app, double percent)
        {
            var (container, fill) = GetDownloadProgressElements(app);
            if (container == null) return;

            if (container.Visibility != Visibility.Visible)
                ShowAppDownloadBar(app);

            // تحديث عرض العمودين بناءً على النسبة المئوية
            if (container.ColumnDefinitions.Count >= 2)
            {
                container.ColumnDefinitions[0].Width = new GridLength(percent, GridUnitType.Star);
                container.ColumnDefinitions[1].Width = new GridLength(100 - percent, GridUnitType.Star);
            }
        }

        private void UpdateDownloadStatus(AppInfo app, long current, long total)
        {
            if (app.StatusQuickText == null) return;
            string currentStr = FormatBytes(current);
            string totalStr = FormatBytes(total);
            app.StatusQuickText.Text = $"{currentStr} / {totalStr}";
            app.StatusQuickText.Foreground = BrushAccent;
            app.StatusQuickText.Visibility = Visibility.Visible;
            app.StatusQuickText.Opacity = 1;
        }

        private void HideAppDownloadBar(AppInfo app)
        {
            var (container, fill) = GetDownloadProgressElements(app);
            if (container == null) return;

            if (container.Visibility != Visibility.Visible) return;

            var fadeOut = new DoubleAnimation(container.Opacity, 0, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                container.Visibility = Visibility.Collapsed;
                container.BeginAnimation(UIElement.OpacityProperty, null);
                container.Opacity = 1;

                // إعادة تعيين العرض
                if (container.ColumnDefinitions.Count >= 2)
                {
                    container.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Star);
                    container.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                }
            };
            container.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // ✅ دالة تنظيف الملفات الجزئية
        private Task CleanupPartialDownload(string appId)
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "DreamsInstaller");
                if (!Directory.Exists(tempDir)) return Task.CompletedTask;

                var files = Directory.GetFiles(tempDir, $"{appId}*");
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
            return Task.CompletedTask;
        }

        private async Task<(string Url, string InstallerType, string SilentArgs)> GetWingetDownloadInfoAsync(string packageId, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"show --id {packageId} -e --disable-interactivity",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (null, null, null);

                string output = await proc.StandardOutput.ReadToEndAsync();

                await Task.Run(() => proc.WaitForExit(), ct);

                if (ct.IsCancellationRequested) return (null, null, null);

                var urlMatch = Regex.Match(output, @"Installer Url:\s*(https?://\S+)", RegexOptions.IgnoreCase);
                var typeMatch = Regex.Match(output, @"Installer Type:\s*(\w+)", RegexOptions.IgnoreCase);

                if (!urlMatch.Success) return (null, null, null);

                string url = urlMatch.Groups[1].Value.Trim();
                string type = typeMatch.Success ? typeMatch.Groups[1].Value.Trim().ToLower() : "exe";

                string silentArgs = type switch
                {
                    "msi" => "/quiet /norestart",
                    "msix" => "",
                    "appx" => "",
                    "inno" => "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                    "nullsoft" => "/S",
                    "wix" => "/quiet /norestart",
                    "burn" => "/quiet /norestart",
                    _ => "/S /quiet /VERYSILENT /SILENT /norestart"
                };

                return (url, type, silentArgs);
            }
            catch (OperationCanceledException) { return (null, null, null); }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetWingetDownloadInfo error: {ex.Message}");
                return (null, null, null);
            }
        }

        // ✅ نسخة محسنة من DownloadWithRealProgressAsync تدعم الإلغاء الفوري والسرعة
        private async Task<string> DownloadWithRealProgressAsync(AppInfo app, string url, CancellationToken globalCt)
        {
            string fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
                fileName = $"{app.ID}_installer.exe";

            string tempDir = Path.Combine(Path.GetTempPath(), "DreamsInstaller");
            Directory.CreateDirectory(tempDir);
            string filePath = Path.Combine(tempDir, fileName);

            // ✅ CTS مدمج: Global + App-specific
            using var appCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
            app.AppCts = appCts;
            var ct = appCts.Token;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Dreams/1.0");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            app.TotalDownloadSize = totalBytes;
            app.DownloadedBytes = 0;
            app.ResetSpeed(); // ✅ إعادة تعيين السرعة

            await Dispatcher.InvokeAsync(() => ShowAppDownloadBar(app));

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int lastPercent = -1;
            var lastUpdate = DateTime.MinValue;

            while (true)
            {
                // ✅ فحص فوري قبل كل read
                ct.ThrowIfCancellationRequested();
                if (app.CancelRequested) throw new OperationCanceledException();

                int read;
                try
                {
                    read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (read == 0) break;

                await fileStream.WriteAsync(buffer, 0, read, ct);
                totalRead += read;
                app.DownloadedBytes = totalRead;
                app.UpdateSpeed(totalRead); // ✅ تحديث السرعة

                if (totalBytes > 0)
                {
                    int percent = (int)((totalRead * 100) / totalBytes);
                    var now = DateTime.UtcNow;

                    if (percent != lastPercent && (now - lastUpdate).TotalMilliseconds >= 100)
                    {
                        lastPercent = percent;
                        lastUpdate = now;

                        int p = percent;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            UpdateAppDownloadBar(app, p);
                            RefreshQueuePopupIfOpen(); // ✅ هيعرض السرعة تلقائياً
                        });
                    }
                }
            }

            await Dispatcher.InvokeAsync(() => UpdateAppDownloadBar(app, 100));

            // ✅ تنظيف الـ CTS
            app.AppCts = null;

            return filePath;
        }

        private async Task<bool> RunInstallerSilentAsync(string installerPath, string installerType, string silentArgs, CancellationToken ct)
        {
            try
            {
                ProcessStartInfo psi;

                if (installerType == "msi" || installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "msiexec.exe",
                        Arguments = $"/i \"{installerPath}\" /quiet /norestart",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        Verb = "runas"
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = silentArgs,
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        Verb = "runas"
                    };
                }

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                {
                    await Task.Run(() => proc.WaitForExit(), ct);
                }

                return proc.ExitCode == 0 || proc.ExitCode == 3010;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Install error: {ex.Message}");
                return false;
            }
        }

        // ✅ نسخة محسنة من FallbackWingetInstall تدعم الإلغاء الفوري
        private async Task<bool> FallbackWingetInstall(AppInfo app, CancellationToken globalCt)
        {
            try
            {
                // ✅ CTS مدمج
                using var appCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
                app.AppCts = appCts;
                var ct = appCts.Token;

                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"install --id {app.ID} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                app.CurrentProcess = proc;

                // ✅ Kill فوري لو اتكنسل
                using (ct.Register(() =>
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                        }
                    }
                    catch { }
                }))
                {
                    await Task.Run(() => proc.WaitForExit(), ct);
                }

                app.CurrentProcess = null;
                app.AppCts = null;

                return !app.CancelRequested && proc.ExitCode == 0;
            }
            catch (OperationCanceledException) { return false; }
            catch { return false; }
        }

        // ✅ نسخة محسنة من RunWingetWithProgressAsync
        private async Task<bool> RunWingetWithProgressAsync(AppInfo app, CancellationToken globalCt)
        {
            try
            {
                app.CancelRequested = false;

                var (url, type, silentArgs) = await GetWingetDownloadInfoAsync(app.ID, globalCt);

                if (string.IsNullOrWhiteSpace(url))
                {
                    return await FallbackWingetInstall(app, globalCt);
                }

                string installerPath;
                try
                {
                    installerPath = await DownloadWithRealProgressAsync(app, url, globalCt);
                }
                catch (OperationCanceledException)
                {
                    // ✅ حذف الملف لو اتنزل جزء منه
                    await CleanupPartialDownload(app.ID);
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Download failed: {ex.Message}");
                    return await FallbackWingetInstall(app, globalCt);
                }

                // ✅ فحص بعد التنزيل
                if (app.CancelRequested || globalCt.IsCancellationRequested)
                {
                    try { File.Delete(installerPath); } catch { }
                    return false;
                }

                bool success = await RunInstallerSilentAsync(installerPath, type, silentArgs, globalCt);

                try { File.Delete(installerPath); } catch { }

                if (success) await Task.Delay(400);
                return success;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }

        private void ResetInstallationState()
        {
            _timer?.Stop();
            _elapsed = TimeSpan.Zero;
            lblTimer.Text = "00:00:00";

            installProgressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            installProgressBar.Value = 0;

            foreach (var app in _allApps)
            {
                if (app.Status != AppStatus.Installed && app.Status != AppStatus.Uninstalled)
                    app.Status = AppStatus.None;

                HideAppDownloadBar(app);
            }
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstallationDone)
            {
                SetButtonToStart();
                UpdateStatusLabel();
                return;
            }

            if (_isRunning)
            {
                _isPaused = true;
                _timer?.Stop();
                SetButtonToResume();
                UpdateStatusLabel();

                var ans = await OnlineMessageBox.Show(this,
                    Loc("str_StopInstallTitle", "Stop Installation"),
                    Loc("str_AreYouSureStop", "Are you sure you want to stop?"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (ans == MessageBoxResult.Yes)
                {
                    _cts?.Cancel();
                    _isRunning = false;
                    _isPaused = false;
                    _timer?.Stop();
                    SetButtonToStart();
                    UpdateStatusLabel();
                }
                else
                {
                    _isPaused = false;
                    _isRunning = true;
                    _timer?.Start();
                    SetButtonToStop();
                    UpdateStatusLabel();
                }
                UpdateCounts();
                return;
            }

            if (_isPaused)
            {
                _cts = new CancellationTokenSource();
                _isRunning = true;
                _isPaused = false;
                SetButtonToStop();
                _timer?.Start();
                UpdateStatusLabel();
                await RunWingetInstalls(_installQueue);
                return;
            }

            var selected = _allApps
                .Where(a => a.CheckBox?.IsChecked == true)
                .OrderBy(a => a.SelectionOrder)
                .ToList();

            if (selected.Count == 0)
            {
                await OnlineMessageBox.Show(this,
                    Loc("str_NoSelectionTitle", "No Selection"),
                    Loc("str_NoSelectionMsg", "Please select at least one application."),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResetInstallationState();
            foreach (var app in selected) app.Status = AppStatus.None;
            _installQueue = new List<AppInfo>(selected);
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _isInstallationDone = false;

            SetButtonToStop();
            UpdateStatusLabel();
            _timer.Start();
            await RunWingetInstalls(_installQueue);
        }

        private async Task RunWingetInstalls(List<AppInfo> apps)
        {
            if (apps == null || apps.Count == 0)
            {
                await FinishInstall(cancelled: false, noApps: true);
                return;
            }

            int processedCount = 0;
            int i = 0;
            bool anyCancelledByUser = false; // ✅ تتبع الإلغاء من المستخدم

            while (i < _installQueue.Count)
            {
                while (_isPaused && !_cts.Token.IsCancellationRequested)
                    await Task.Delay(150);

                if (_cts.Token.IsCancellationRequested) break;

                var app = _installQueue[i];

                if (app.CheckBox?.IsChecked != true && app.Status == AppStatus.None)
                {
                    app.CancelApp();
                    i++;
                    continue;
                }

                if (app.Status == AppStatus.Installed)
                {
                    i++;
                    processedCount++;
                    UpdateProgressBar(processedCount, _installQueue.Count);
                    continue;
                }

                _currentInstallingApp = app;
                app.CancelRequested = false;

                Dispatcher.Invoke(() =>
                {
                    string appName = Loc(app.NameKey, app.NameKey);
                    lblStatusText.Text = $"{Loc("str_InstallingApp", "Installing")}: {appName}";
                    statusIconText.Text = "\uE711";
                    lblStatusText.Foreground = GetBrush("Warning", BrushWarning);
                    statusIconText.Foreground = GetBrush("Warning", BrushWarning);
                    ShowCardInstallingIndicator(app);
                });

                bool success = false;
                try
                {
                    success = await RunWingetWithProgressAsync(app, _cts.Token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"winget error [{app.ID}]: {ex.Message}");
                }

                _currentInstallingApp = null;

                // ✅ لو اتشال من القائمة أثناء التثبيت
                if (app.CancelRequested)
                {
                    anyCancelledByUser = true; // ✅ سجّل الإلغاء

                    Dispatcher.Invoke(() =>
                    {
                        ShowAppQuickStatus(app,
                            $"{Loc("str_Cancelled", "Cancelled")} 🚫", BrushDanger);
                        HideCardInstallingIndicator(app);
                        HideAppDownloadBar(app);
                    });

                    app.CancelRequested = false;

                    // ✅ لو مفيش حاجة تانية في القائمة
                    bool hasMorePending = _installQueue
                        .Skip(i + 1)
                        .Any(a => a.CheckBox?.IsChecked == true
                               && a.Status == AppStatus.None);

                    if (!hasMorePending)
                    {
                        // ✅ مفيش برامج تانية - إنهاء بدون نجاح
                        await Dispatcher.InvokeAsync(async () =>
                            await FinishInstall(
                                cancelled: true,  // ✅ cancelled = true
                                noApps: false,
                                allDone: false)); // ✅ allDone = false
                        return;
                    }

                    i++;
                    continue;
                }

                if (_cts.Token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() =>
                    {
                        HideCardInstallingIndicator(app);
                        HideAppDownloadBar(app);
                    });
                    break;
                }

                Dispatcher.Invoke(() =>
                {
                    if (success)
                    {
                        if (app.CheckBox != null) app.CheckBox.IsChecked = false;
                        app.Status = AppStatus.Installed;
                        ShowAppQuickStatus(app,
                            $"{Loc("str_Installed", "Installed")} ✓", BrushSuccess);
                    }
                    else
                    {
                        app.Status = AppStatus.None;
                        ShowAppQuickStatus(app,
                            $"{Loc("str_Failed", "Failed")} ⚠", BrushDanger);
                    }
                    HideCardInstallingIndicator(app);
                    HideAppDownloadBar(app);
                    RefreshQueuePopupIfOpen();
                });

                processedCount++;
                UpdateProgressBar(processedCount, _installQueue.Count);
                i++;
            }

            bool globalCancelled = _cts.Token.IsCancellationRequested;

            if (!_isPaused)
            {
                // ✅ حساب النتيجة الحقيقية
                bool anyInstalled = _installQueue
                    .Any(a => a.Status == AppStatus.Installed);

                bool allSelectedDone = _installQueue
                    .Where(a => a.CheckBox?.IsChecked == true
                             || a.Status == AppStatus.Installed)
                    .All(a => a.Status == AppStatus.Installed);

                // ✅ النجاح الحقيقي: مفيش إلغاء + فيه برامج اتثبتت فعلاً
                bool realSuccess = !globalCancelled
                                && !anyCancelledByUser
                                && anyInstalled
                                && allSelectedDone;

                await Dispatcher.InvokeAsync(async () =>
                    await FinishInstall(
                        cancelled: globalCancelled || anyCancelledByUser,
                        noApps: false,
                        allDone: realSuccess));
            }
        }

        private void UpdateProgressBar(int current, int total)
        {
            if (total <= 0) return;
            double percent = (current * 100.0) / total;
            Dispatcher.Invoke(() =>
            {
                var anim = new DoubleAnimation(percent, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                installProgressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, anim);
            });
        }

        private async Task FinishInstall(
    bool cancelled, bool noApps = false, bool allDone = false)
        {
            _timer.Stop();
            _isRunning = false;
            _isPaused = false;
            _currentInstallingApp = null;

            if (noApps)
            {
                // ✅ مفيش برامج اتختارت
                SetButtonToStart();
                UpdateStatusLabel();
                UpdateCounts();
                return;
            }

            if (allDone && !cancelled)
            {
                // ✅ نجاح حقيقي فقط
                var doneAnim = new DoubleAnimation(100, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                installProgressBar.BeginAnimation(
                    System.Windows.Controls.ProgressBar.ValueProperty, doneAnim);

                SetButtonToDone();
                UpdateStatusLabel();

                await OnlineMessageBox.Show(this,
                    Loc("str_InstallDoneTitle", "Installation Complete"),
                    Loc("str_InstallDoneMsg", "All selected applications installed!"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (cancelled)
            {
                // ✅ اتكنسل - رجوع للبداية
                installProgressBar.BeginAnimation(
                    System.Windows.Controls.ProgressBar.ValueProperty, null);
                installProgressBar.Value = 0;

                SetButtonToStart();
                UpdateStatusLabel();
            }
            else
            {
                // ✅ فيه failures - رجوع للبداية بدون رسالة
                SetButtonToStart();
                UpdateStatusLabel();
            }

            UpdateCounts();
        }

        private async void btnUninstallSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allApps.Where(a => a.CheckBox?.IsChecked == true).OrderBy(a => a.SelectionOrder).ToList();
            if (selected.Count == 0) { await OnlineMessageBox.Show(this, Loc("str_NoSelectionTitle", "No Selection"), Loc("str_NoSelectionMsg", "Please select at least one application."), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string names = string.Join("\n• ", selected.Select(a => Loc(a.NameKey, a.NameKey))); string msg = string.Format(Loc("str_UninstallConfirm", "Uninstall {0} app(s)?\n\n• {1}"), selected.Count, names);
            var res = await OnlineMessageBox.Show(this, Loc("str_UninstallTitle", "Confirm Uninstall"), msg, MessageBoxButton.YesNo, MessageBoxImage.Warning); if (res != MessageBoxResult.Yes) return;
            _isUninstalling = true; _cts = new CancellationTokenSource(); UpdateCounts(); installProgressBar.Value = 0; UpdateStatusLabel(); int processed = 0;
            try { for (int i = 0; i < selected.Count; i++) { if (_cts.Token.IsCancellationRequested) break; var app = selected[i]; string name = Loc(app.NameKey, app.NameKey); lblStatusText.Text = $"{Loc("str_Uninstalling", "Uninstalling")}: {name}"; statusIconText.Text = "\uE74D"; lblStatusText.Foreground = GetBrush("Warning", BrushWarning); statusIconText.Foreground = GetBrush("Warning", BrushWarning); try { await Task.Run(() => { var p = Process.Start(new ProcessStartInfo { FileName = "winget", Arguments = $"uninstall --id {app.ID} -e --silent", UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(); }, _cts.Token); } catch (OperationCanceledException) { break; } catch (Exception ex) { Debug.WriteLine($"Uninstall error [{app.ID}]: {ex.Message}"); } if (_cts.Token.IsCancellationRequested) break; if (app.CheckBox != null) app.CheckBox.IsChecked = false; app.Status = AppStatus.Uninstalled; app.SelectionOrder = 0; RefreshQueuePopupIfOpen(); processed++; UpdateProgressBar(processed, selected.Count); } }
            finally { _isUninstalling = false; if (_cts?.Token.IsCancellationRequested == true) { lblStatusText.Text = Loc("str_UninstallCancelled", "Uninstall Cancelled"); statusIconText.Text = "\uE711"; } else { lblStatusText.Text = Loc("str_UninstallComplete", "Uninstall Complete ✓"); statusIconText.Text = "\uE73E"; lblStatusText.Foreground = GetBrush("Success", BrushSuccess); statusIconText.Foreground = GetBrush("Success", BrushSuccess); } UpdateCounts(); }
        }
        #endregion

        #region Responsive, Filter, Counts, Queue, DragDrop, AutoSync, Misc
        private void AppsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_resizeDebouncer == null)
            {
                _resizeDebouncer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _resizeDebouncer.Tick += (_, __) =>
                {
                    _resizeDebouncer.Stop();
                    UpdateColumnCount();
                };
            }
            _resizeDebouncer.Stop();
            _resizeDebouncer.Start();
        }

        private void UpdateColumnCount()
        {
            Col1Def.Width = new GridLength(1, GridUnitType.Star);
            Col2Def.Width = new GridLength(0);
            Col3Def.Width = new GridLength(0);
            UpdateCardsWidth();
        }

        private void UpdateCardsWidth()
        {
            double available = AppsScrollViewer.ActualWidth;
            if (available <= 0) return;

            int cols = available >= 900 ? 3 : available >= 580 ? 2 : 1;
            _currentColumns = cols;

            double usable = available - 40;
            double cardWidth = (usable / cols) - 12;
            if (cardWidth < 200) cardWidth = 200;

            foreach (var app in _allApps)
            {
                if (app.Card != null)
                {
                    app.Card.BeginAnimation(FrameworkElement.WidthProperty, null);
                    app.Card.Width = cardWidth;
                }
            }
        }

        private void txtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (txtSearch.IsLoaded)
            {
                var clearBtn = txtSearch.Template?.FindName("ClearBtn", txtSearch) as Button;
                if (clearBtn != null) clearBtn.Visibility = string.IsNullOrEmpty(txtSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            FilterApps();
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e) => txtSearch.Text = string.Empty;

        // ✅ دالة بحث منفصلة وواضحة
        private bool IsAppMatchSearch(AppInfo app, string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;

            // ✅ تنظيف الـ search
            string s = search.ToLowerInvariant().Trim();

            string name = Loc(app.NameKey, app.NameKey).ToLowerInvariant();
            string desc = Loc(app.DescKey, app.DescKey).ToLowerInvariant();
            string id = app.ID.ToLowerInvariant();
            string cat = Loc(app.CategoryKey, app.CategoryKey).ToLowerInvariant();

            return name.Contains(s)
                || desc.Contains(s)
                || id.Contains(s)
                || cat.Contains(s);
        }

        private void FilterApps()
        {
            string allKey = "str_CatAll";
            string allLabel = Loc(allKey, "All");
            string search = txtSearch.Text.Trim();

            // ✅ إيجاد الـ Key بتاع الـ Category المختارة
            string selectedCatKey = null;
            if (_currentCategory != allLabel)
            {
                foreach (var (key, fallback) in CategoryKeys)
                {
                    if (Loc(key, fallback) == _currentCategory)
                    {
                        selectedCatKey = key;
                        break;
                    }
                }
            }

            foreach (var app in _allApps)
            {
                // ✅ فحص الـ Category بالـ Key مش بالـ Value
                bool catOk = selectedCatKey == null
                             || app.CategoryKey == selectedCatKey;

                // ✅ فحص الـ Search
                bool srcOk = IsAppMatchSearch(app, search);

                if (app.Card != null)
                    app.Card.Visibility = (catOk && srcOk)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }

            // باقي كود الـ headers
            Border lastHeader = null;
            bool isFiltering = !string.IsNullOrEmpty(search)
                               || selectedCatKey != null;

            foreach (var item in Column1Container.Items)
            {
                if (item is Border hdr && hdr.Tag?.ToString() == "CategoryHeader")
                {
                    lastHeader = hdr;
                }
                else if (item is WrapPanel wp && wp.Tag?.ToString() == "CategoryWrap")
                {
                    bool hasVisible = wp.Children
                        .OfType<Border>()
                        .Any(b => b.Visibility == Visibility.Visible);

                    wp.Visibility = hasVisible
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    if (lastHeader != null)
                    {
                        lastHeader.Visibility = (!isFiltering || hasVisible)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
            }

            UpdateCounts();
        }

        private bool IsAppVisible(AppInfo app)
        {
            return app.Card?.Visibility == Visibility.Visible;
        }

        private void btnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            var visible = _allApps.Where(a => IsAppVisible(a)).ToList();
            bool shouldCheck = visible.Any(a => a.CheckBox?.IsChecked != true);
            foreach (var app in visible) if (app.CheckBox != null) app.CheckBox.IsChecked = shouldCheck;
        }

        private void UpdateCounts()
        {
            int selected = _allApps.Count(a => a.CheckBox?.IsChecked == true);
            int total = _allApps.Count;
            lblSelectedCount.Text = selected.ToString();
            lblTotalCount.Text = total.ToString();
            btnStart.IsEnabled = !_isUninstalling;
            btnStart.Opacity = _isUninstalling ? 0.5 : 1.0;
            btnUninstallSelected.IsEnabled = selected > 0 && !_isRunning && !_isPaused && !_isUninstalling;
            btnUninstallSelected.Opacity = (_isRunning || _isPaused || _isUninstalling) ? 0.5 : 1.0;
            UpdateSelectAllText();
            UpdateStatusLabel();
            RefreshQueuePopupIfOpen();
        }

        private void UpdateStatusLabel()
        {
            if (_isRunning && !string.IsNullOrEmpty(lblStatusText.Text) && lblStatusText.Text.Contains(":")) return;

            if (_isRunning) { lblStatusText.Text = Loc("str_Installing", "Installing..."); statusIconText.Text = "\uE711"; lblStatusText.Foreground = GetBrush("Warning", BrushWarning); statusIconText.Foreground = GetBrush("Warning", BrushWarning); }
            else if (_isPaused) { lblStatusText.Text = Loc("str_Paused", "Paused"); statusIconText.Text = "\uE769"; lblStatusText.Foreground = GetBrush("Warning", BrushWarning); statusIconText.Foreground = GetBrush("Warning", BrushWarning); }
            else if (_isUninstalling) { lblStatusText.Text = Loc("str_Uninstalling", "Uninstalling..."); statusIconText.Text = "\uE74D"; lblStatusText.Foreground = GetBrush("Warning", BrushWarning); statusIconText.Foreground = GetBrush("Warning", BrushWarning); }
            else if (_isInstallationDone) { lblStatusText.Text = Loc("str_InstallComplete", "Complete ✓"); statusIconText.Text = "\uE73E"; lblStatusText.Foreground = GetBrush("Success", BrushSuccess); statusIconText.Foreground = GetBrush("Success", BrushSuccess); }
            else { int sel = _allApps.Count(a => a.CheckBox?.IsChecked == true); if (sel == 0) { lblStatusText.Text = Loc("str_ReadyToStart", "Ready to start"); statusIconText.Text = "\uE81E"; lblStatusText.Foreground = GetBrush("DynamicMainText", BrushMainText); statusIconText.Foreground = GetBrush("DynamicAccent", BrushAccent); } else { lblStatusText.Text = $"{sel} {Loc("str_AppsSelected", "apps selected")}"; statusIconText.Text = "\uE7BA"; lblStatusText.Foreground = GetBrush("Success", BrushSuccess); statusIconText.Foreground = GetBrush("Success", BrushSuccess); } }
        }

        private void btnQueueList_Click(object sender, RoutedEventArgs e) { if (_queueWindow != null && _queueWindow.IsLoaded) { _queueWindow.Activate(); return; } OpenQueuePopup(); }

        private void OpenQueuePopup()
        {
            var apps = _allApps.Where(a => a.CheckBox?.IsChecked == true || a.Status != AppStatus.None).OrderBy(a => a.Status != AppStatus.None ? 0 : 1).ThenBy(a => a.SelectionOrder).ToList();
            double parentOpacity = 1.0; if (Window.GetWindow(this) is Window pw) parentOpacity = pw.Opacity;
            _queueWindow = new Window { Width = 440, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, SizeToContent = SizeToContent.Height, MaxHeight = 600, Opacity = 0 };
            InjectStylesIntoWindow(_queueWindow);
            _queueWindow.Closed += (_, __) => { _queueWindow = null; _queueList = null; _syncTimer?.Stop(); };
            Brush cardBg = GetBrush("DynamicCardBg", new SolidColorBrush(Colors.White));
            Brush borderBr = GetBrush("DynamicBorderBrush", new SolidColorBrush(Color.FromRgb(220, 220, 220)));
            Brush mainText = GetBrush("DynamicMainText", BrushMainText);
            Brush subText = GetBrush("DynamicSubText", new SolidColorBrush(Color.FromRgb(100, 100, 100)));
            Brush accent = GetBrush("DynamicAccent", BrushAccent);
            Color accentColor = ((SolidColorBrush)accent).Color;
            Brush hoverBg = new SolidColorBrush(Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));
            var root = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5), BorderBrush = borderBr, Background = cardBg };
            var rootStack = new StackPanel();
            var header = new Border { Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = borderBr };
            var hGrid = new Grid(); hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var hIcon = new TextBlock { Text = "\uE71D", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }; Grid.SetColumn(hIcon, 0);
            var hTitleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; hTitleStack.Children.Add(new TextBlock { Text = Loc("str_QueueListTitle", "Install Queue"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = mainText });
            var hSubTitle = new TextBlock { FontSize = 11, Foreground = subText, Margin = new Thickness(0, 2, 0, 0) }; RefreshQueueSubtitle(hSubTitle, apps); hTitleStack.Children.Add(hSubTitle); Grid.SetColumn(hTitleStack, 1);
            var closeBtn = MakeIconButton("\uE711", subText, hoverBg); closeBtn.Click += (_, __) => _queueWindow?.Close(); Grid.SetColumn(closeBtn, 2);
            hGrid.Children.Add(hIcon); hGrid.Children.Add(hTitleStack); hGrid.Children.Add(closeBtn); header.Child = hGrid; rootStack.Children.Add(header);
            var scroll = new ScrollViewer { MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0), Background = Brushes.Transparent };
            _queueList = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            BuildQueueRows(apps, _queueList, hoverBg, accent, accentColor, mainText, subText, borderBr);
            scroll.Content = _queueList; rootStack.Children.Add(scroll);
            var footer = new Border { Padding = new Thickness(16, 12, 16, 16), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = borderBr };
            var okBtn = new Button { Height = 40, Background = accent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold, FontSize = 13, Content = Loc("str_OK", "OK"), Template = MakeRoundedTemplate(12) };
            okBtn.MouseEnter += (_, __) => okBtn.Background = new SolidColorBrush(Color.FromRgb((byte)(accentColor.R * 0.85), (byte)(accentColor.G * 0.85), (byte)(accentColor.B * 0.85)));
            okBtn.MouseLeave += (_, __) => okBtn.Background = accent;
            okBtn.Click += (_, __) => _queueWindow?.Close();
            footer.Child = okBtn; rootStack.Children.Add(footer); root.Child = rootStack; _queueWindow.Content = root;

            _queueWindow.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                if (_isDragging || _draggedRow != null) return;
                var src = ev.OriginalSource as DependencyObject;
                while (src != null && src != _queueWindow)
                {
                    if (src is Button) return;
                    if (src is Border bd && bd.Tag is AppInfo) return;
                    if (src is ScrollViewer || src is ScrollBar) return;
                    src = VisualTreeHelper.GetParent(src);
                }
                try { if (_queueWindow.WindowState == WindowState.Normal) _queueWindow.DragMove(); }
                catch { }
            };

            _queueWindow.Loaded += (_, __) => { var fa = new DoubleAnimation(0, parentOpacity, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }; _queueWindow.BeginAnimation(Window.OpacityProperty, fa); _syncTimer?.Start(); };
            _queueWindow.Show();
        }

        private void InjectStylesIntoWindow(Window win) { try { var scrollBarStyle = this.Resources[typeof(ScrollBar)] as Style; if (scrollBarStyle != null) win.Resources[typeof(ScrollBar)] = scrollBarStyle; if (this.Resources.Contains("AccentGradient")) win.Resources["AccentGradient"] = this.Resources["AccentGradient"]; else if (Application.Current.Resources.Contains("AccentGradient")) win.Resources["AccentGradient"] = Application.Current.Resources["AccentGradient"]; } catch (Exception ex) { Debug.WriteLine($"Style injection error: {ex.Message}"); } }

        private void BuildQueueRows(List<AppInfo> apps, StackPanel list, Brush hoverBg, Brush accent, Color accentColor, Brush mainText, Brush subText, Brush borderBr)
        {
            list.Children.Clear();
            if (apps.Count == 0)
            {
                var emptyPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 30) };
                emptyPanel.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 36, Foreground = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), HorizontalAlignment = HorizontalAlignment.Center });
                emptyPanel.Children.Add(new TextBlock { Text = Loc("str_QueueEmpty", "No apps selected"), FontSize = 13, Foreground = subText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
                list.Children.Add(emptyPanel);
                return;
            }

            int order = 1;
            foreach (var app in apps)
            {
                var localApp = app;
                bool isDone = app.Status == AppStatus.Installed || app.Status == AppStatus.Uninstalled;
                bool isSelected = app.CheckBox?.IsChecked == true;
                bool isDraggable = !isDone && !_isRunning;
                bool isDownloading = _isRunning && app.Status == AppStatus.None && app.TotalDownloadSize > 0;

                var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 4), Background = Brushes.Transparent, Opacity = isDone ? 0.75 : 1.0, Tag = localApp, AllowDrop = isDraggable };
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dragHandle = new TextBlock { Text = "\uE76F", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = subText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Cursor = isDraggable ? Cursors.SizeAll : Cursors.Arrow, Opacity = isDraggable ? 0.6 : 0.2 };
                Grid.SetColumn(dragHandle, 0);

                var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                badge.Child = new TextBlock { Text = order.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (isSelected) order++;
                Grid.SetColumn(badge, 1);

                var iconBd = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
                if (app.IconLoaded && app.IconImage?.Source != null)
                {
                    iconBd.Background = Brushes.Transparent;
                    iconBd.Child = new Image { Source = app.IconImage.Source, Width = 36, Height = 36, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                else
                {
                    try { iconBd.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(app.Color)); } catch { }
                    iconBd.Child = new TextBlock { Text = GetAppFirstLetter(app.NameKey), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                Grid.SetColumn(iconBd, 2);

                var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameTb = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = mainText, TextTrimming = TextTrimming.CharacterEllipsis };
                nameTb.SetResourceReference(TextBlock.TextProperty, app.NameKey);
                nameStack.Children.Add(nameTb);

                // ✅ عرض الحجم والسرعة في Queue List
                if (isDownloading && app.TotalDownloadSize > 0)
                {
                    var sizeText = new TextBlock
                    {
                        FontSize = 9,
                        Foreground = accent,
                        Margin = new Thickness(0, 2, 0, 0)
                    };

                    string downloaded = FormatBytes(app.DownloadedBytes);
                    string total = FormatBytes(app.TotalDownloadSize);
                    string speed = FormatSpeed(app.DownloadSpeedBps);

                    // ✅ دمج الحجم والسرعة في سطر واحد
                    sizeText.Text = string.IsNullOrEmpty(speed)
                        ? $"{downloaded} / {total}"
                        : $"{downloaded} / {total}  •  {speed}";

                    nameStack.Children.Add(sizeText);
                }
                else if (!isDone && !isDownloading)
                {
                    nameStack.Children.Add(new TextBlock { Text = app.ID, FontSize = 10, Foreground = subText, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                }
                else
                {
                    nameStack.Children.Add(new TextBlock { Text = app.ID, FontSize = 10, Foreground = subText, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                }

                Grid.SetColumn(nameStack, 3);

                UIElement rightElement;
                if (app.Status == AppStatus.Installed)
                {
                    rightElement = new TextBlock { Text = "\uE73E", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = BrushSuccess, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = Loc("str_InstalledStatus", "Installed") };
                }
                else if (app.Status == AppStatus.Uninstalled)
                {
                    rightElement = new TextBlock { Text = "\uE74D", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = BrushDanger, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = Loc("str_UninstalledStatus", "Uninstalled") };
                }
                else
                {
                    var removeBtn = MakeIconButton("\uE711", subText, hoverBg, 24, 0.6);
                    removeBtn.ToolTip = Loc("str_RemoveFromQueue", "Remove");
                    removeBtn.Click += (_, __) => { if (localApp.CheckBox != null) localApp.CheckBox.IsChecked = false; localApp.SelectionOrder = 0; };
                    rightElement = removeBtn;
                }
                Grid.SetColumn(rightElement, 4);

                rowGrid.Children.Add(dragHandle);
                rowGrid.Children.Add(badge);
                rowGrid.Children.Add(iconBd);
                rowGrid.Children.Add(nameStack);
                rowGrid.Children.Add(rightElement);
                row.Child = rowGrid;

                if (!isDone)
                {
                    row.MouseEnter += (_, __) => { if (_draggedRow != row) row.Background = hoverBg; };
                    row.MouseLeave += (_, __) => { if (_draggedRow != row) row.Background = Brushes.Transparent; };
                }

                if (isDraggable)
                {
                    row.PreviewMouseLeftButtonDown += Row_PreviewMouseLeftButtonDown;
                    row.PreviewMouseMove += Row_PreviewMouseMove;
                    row.PreviewMouseLeftButtonUp += Row_PreviewMouseLeftButtonUp;
                    row.DragOver += Row_DragOver;
                    row.Drop += Row_Drop;
                }
                list.Children.Add(row);
            }
        }

        private void RefreshQueuePopupIfOpen()
        {
            if (_queueWindow == null || !_queueWindow.IsLoaded || _queueList == null) return;
            var apps = _allApps.Where(a => a.CheckBox?.IsChecked == true || a.Status != AppStatus.None).OrderBy(a => a.Status != AppStatus.None ? 0 : 1).ThenBy(a => a.SelectionOrder).ToList();
            Brush accent = GetBrush("DynamicAccent", BrushAccent);
            Color accentColor = ((SolidColorBrush)accent).Color;
            Brush hoverBg = new SolidColorBrush(Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));
            Brush mainText = GetBrush("DynamicMainText", BrushMainText);
            Brush subText = GetBrush("DynamicSubText", new SolidColorBrush(Color.FromRgb(100, 100, 100)));
            Brush borderBr = GetBrush("DynamicBorderBrush", new SolidColorBrush(Color.FromRgb(220, 220, 220)));
            BuildQueueRows(apps, _queueList, hoverBg, accent, accentColor, mainText, subText, borderBr);
            try
            {
                var root = _queueWindow.Content as Border;
                var rootStack = root?.Child as StackPanel;
                var header = rootStack?.Children[0] as Border;
                var hGrid = header?.Child as Grid;
                if (hGrid != null && hGrid.Children.Count >= 2 && hGrid.Children[1] is StackPanel titleStack && titleStack.Children.Count >= 2 && titleStack.Children[1] is TextBlock subTb)
                    RefreshQueueSubtitle(subTb, apps);
            }
            catch { }
        }

        private void RefreshQueueSubtitle(TextBlock tb, List<AppInfo> apps)
        {
            int pending = apps.Count(a => a.CheckBox?.IsChecked == true);
            tb.Text = $"{pending} {Loc("str_AppsSelected", "apps selected")}";
        }

        private static Button MakeIconButton(string icon, Brush fg, Brush hoverBg, double size = 28, double opacity = 1.0)
        {
            var btn = new Button { Width = size, Height = size, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Opacity = opacity };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetValue(TextBlock.TextProperty, icon);
            tb.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe MDL2 Assets"));
            tb.SetValue(TextBlock.FontSizeProperty, size * 0.45);
            tb.SetValue(TextBlock.ForegroundProperty, fg);
            tb.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(tb);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            btn.MouseEnter += (_, __) => btn.Background = hoverBg;
            btn.MouseLeave += (_, __) => btn.Background = Brushes.Transparent;
            return btn;
        }

        private static ControlTemplate MakeRoundedTemplate(double radius)
        {
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            return tpl;
        }

        private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border row) return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null && src != row)
            {
                if (src is Button) return;
                src = VisualTreeHelper.GetParent(src);
            }
            _dragStartPoint = e.GetPosition(_queueList);
            _draggedRow = row;
        }

        private void Row_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedRow == null || e.LeftButton != MouseButtonState.Pressed) return;
            if (_isDragging) return;
            Point currentPos = e.GetPosition(_queueList);
            Vector diff = currentPos - _dragStartPoint;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
                _draggedRow.Opacity = 0.5;
                try { DragDrop.DoDragDrop(_draggedRow, _draggedRow, DragDropEffects.Move); }
                catch { }
                finally
                {
                    if (_draggedRow != null) _draggedRow.Opacity = 1.0;
                    _draggedRow = null;
                    _isDragging = false;
                }
            }
        }

        private void Row_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) _draggedRow = null;
        }

        private void Row_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(Border)) is Border source && sender is Border target && source != target)
            {
                e.Effects = DragDropEffects.Move;
                target.Background = GetBrush("DynamicHoverBg", new SolidColorBrush(Color.FromArgb(40, 14, 165, 233)));
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Row_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(Border)) is not Border source) return;
            if (sender is not Border target) return;
            if (source == target) return;
            var sourceApp = source.Tag as AppInfo;
            var targetApp = target.Tag as AppInfo;
            if (sourceApp == null || targetApp == null) return;
            long temp = sourceApp.SelectionOrder;
            sourceApp.SelectionOrder = targetApp.SelectionOrder;
            targetApp.SelectionOrder = temp;
            target.Background = Brushes.Transparent;
            RefreshQueuePopupIfOpen();
            e.Handled = true;
        }

        private void AutoSyncQueue()
        {
            if (_isSyncing) return;
            if (_queueWindow == null || !_queueWindow.IsLoaded) return;
            lock (_syncLock)
            {
                _isSyncing = true;
                try
                {
                    bool hasChange = false;
                    foreach (var app in _allApps)
                    {
                        bool isChecked = app.CheckBox?.IsChecked == true;
                        if (isChecked && app.SelectionOrder == 0)
                        {
                            app.SelectionOrder = ++_selectionCounter;
                            hasChange = true;
                        }
                        else if (!isChecked && app.SelectionOrder != 0 && app.Status == AppStatus.None)
                        {
                            app.SelectionOrder = 0;
                            hasChange = true;
                        }
                    }
                    if (hasChange)
                    {
                        RefreshQueuePopupIfOpen();
                        if (_isRunning && _installQueue != null) SyncRunningQueue();
                    }
                }
                finally { _isSyncing = false; }
            }
        }

        private void SyncRunningQueue()
        {
            var newSelected = _allApps.Where(a => a.CheckBox?.IsChecked == true && a.Status == AppStatus.None && !_installQueue.Contains(a)).OrderBy(a => a.SelectionOrder).ToList();
            foreach (var app in newSelected) _installQueue.Add(app);
            var toRemove = _installQueue.Where(a => a.Status == AppStatus.None && a.CheckBox?.IsChecked != true).ToList();
            foreach (var app in toRemove) _installQueue.Remove(app);
        }

        private void ExternalBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is string id)
            {
                try { Process.Start(new ProcessStartInfo { FileName = $"https://winget.run/pkg/{id.Replace(".", "/")}", UseShellExecute = true }); }
                catch (Exception ex) { Debug.WriteLine($"Link error: {ex.Message}"); }
            }
        }

        public async Task<bool> RequestSafeExitAsync()
        {
            if (!_isRunning && !_isUninstalling) return true;
            bool wasInstalling = _isRunning, wasUninstalling = _isUninstalling;
            if (wasInstalling) _isPaused = true;
            await Task.Delay(100);
            string title = wasUninstalling ? Loc("str_ExitUninstallTitle", "Exit Uninstallation") : Loc("str_ExitInstallTitle", "Exit Installation");
            string message = wasUninstalling ? Loc("str_AreYouSureExitUninstall", "Uninstallation is in progress. Are you sure you want to exit?") : Loc("str_AreYouSureExit", "Installation is in progress. Are you sure you want to exit?");
            var res = await OnlineMessageBox.Show(this, title, message, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _cts?.Cancel();
                _timer?.Stop();
                _isRunning = false;
                _isUninstalling = false;
                _isPaused = false;
                return true;
            }
            else
            {
                if (wasInstalling) _isPaused = false;
                return false;
            }
        }
        #endregion
    }

    public enum AppStatus { None, Installed, Uninstalled }

    public class AppInfo
    {
        public string NameKey { get; set; }
        public string DescKey { get; set; }
        public string ID { get; set; }
        public string Category { get; set; }
        public string CategoryKey { get; set; } // ✅ نحفظ الـ Key مش الـ Value
        public string Color { get; set; }
        public string IconFile { get; set; }
        public string Domain { get; set; }
        public CheckBox CheckBox { get; set; }
        public Border Card { get; set; }
        public AppStatus Status { get; set; } = AppStatus.None;
        public long SelectionOrder { get; set; } = 0;
        public Border IconBorder { get; set; }
        public Image IconImage { get; set; }
        public TextBlock IconFallbackText { get; set; }
        public ProgressBar DownloadProgressBar { get; set; }
        public TextBlock StatusQuickText { get; set; }
        public Process CurrentProcess { get; set; } = null;
        public bool CancelRequested { get; set; } = false;
        public bool IconLoaded { get; set; } = false;

        public long TotalDownloadSize { get; set; } = 0;
        public long DownloadedBytes { get; set; } = 0;
        public long DownloadSpeedBps { get; set; } = 0;

        private long _lastSpeedBytes = 0;
        private DateTime _lastSpeedTime = DateTime.MinValue;

        // ✅ إضافة CTS خاص بكل تطبيق
        private CancellationTokenSource _appCts;

        public CancellationTokenSource AppCts
        {
            get => _appCts;
            set
            {
                _appCts?.Cancel();
                _appCts?.Dispose();
                _appCts = value;
            }
        }

        public void UpdateSpeed(long currentBytes)
        {
            var now = DateTime.UtcNow;
            if (_lastSpeedTime == DateTime.MinValue)
            {
                _lastSpeedTime = now;
                _lastSpeedBytes = currentBytes;
                return;
            }

            double elapsed = (now - _lastSpeedTime).TotalSeconds;
            if (elapsed >= 0.5) // تحديث كل نص ثانية
            {
                DownloadSpeedBps = (long)((currentBytes - _lastSpeedBytes) / elapsed);
                _lastSpeedBytes = currentBytes;
                _lastSpeedTime = now;
            }
        }

        public void ResetSpeed()
        {
            DownloadSpeedBps = 0;
            _lastSpeedBytes = 0;
            _lastSpeedTime = DateTime.MinValue;
        }

        public void CancelApp()
        {
            CancelRequested = true;
            try { _appCts?.Cancel(); } catch { }
            try
            {
                if (CurrentProcess != null && !CurrentProcess.HasExited)
                    CurrentProcess.Kill();
            }
            catch { }
        }
    }

    public static class OnlineMessageBox
    {
        private static string Loc(DependencyObject owner, string key, string fallback)
        {
            try
            {
                if (owner is FrameworkElement fe && fe.FindResource(key) is string s) return s;
                if (Application.Current?.Resources.Contains(key) == true && Application.Current.Resources[key] is string a) return a;
            }
            catch { }
            return string.IsNullOrEmpty(fallback) ? key : fallback;
        }

        public static async Task<MessageBoxResult> Show(DependencyObject owner, string title, string message, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>();
            double parentOpacity = 1.0;
            if (owner is FrameworkElement fe2 && Window.GetWindow(fe2) is Window pw) parentOpacity = pw.Opacity;

            Brush GetRes(string key, Brush fb)
            {
                try
                {
                    if (Application.Current?.Resources.Contains(key) == true && Application.Current.Resources[key] is Brush b) return b;
                    if (owner is FrameworkElement fe3 && fe3.TryFindResource(key) is Brush b2) return b2;
                }
                catch { }
                return fb;
            }

            Brush mainBg = GetRes("DynamicCardBg", Brushes.White), borderBr = GetRes("DynamicBorderBrush", new SolidColorBrush(Color.FromRgb(220, 220, 220))), mainText = GetRes("DynamicMainText", Brushes.Black), subText = GetRes("DynamicSubText", new SolidColorBrush(Color.FromRgb(90, 90, 90))), accent = GetRes("DynamicAccent", new SolidColorBrush(Color.FromRgb(0, 120, 212)));

            string iconChar = icon switch
            {
                MessageBoxImage.Warning => "\uE7BA",
                MessageBoxImage.Error => "\uEB90",
                MessageBoxImage.Question => "\uE897",
                _ => "\uE946"
            };

            Color iconColor = icon switch
            {
                MessageBoxImage.Warning => Color.FromRgb(255, 193, 7),
                MessageBoxImage.Error => Color.FromRgb(220, 53, 69),
                MessageBoxImage.Question => Color.FromRgb(0, 192, 192),
                _ => ((SolidColorBrush)accent).Color
            };

            Brush iconBrush = new SolidColorBrush(iconColor), circleBrush = new SolidColorBrush(Color.FromArgb(38, iconColor.R, iconColor.G, iconColor.B));
            var bgBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            bgBrush.GradientStops.Add(new GradientStop(iconColor, 0));
            bgBrush.GradientStops.Add(new GradientStop(iconColor, 0.023));
            bgBrush.GradientStops.Add(new GradientStop(((SolidColorBrush)mainBg).Color, 0.0231));
            bgBrush.GradientStops.Add(new GradientStop(((SolidColorBrush)mainBg).Color, 1));

            var win = new Window { Width = 450, Height = 270, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Opacity = 0, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var outerBorder = new Border { Background = bgBrush, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = borderBr, ClipToBounds = true };
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var iconCircle = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = circleBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 12) };
            iconCircle.Child = new TextBlock { Text = iconChar, FontSize = 32, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Foreground = iconBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(iconCircle, 0);

            var content = new StackPanel { Margin = new Thickness(30, 0, 30, 20) };
            content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = mainText, Margin = new Thickness(0, 0, 0, 8), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = message, FontSize = 13, Foreground = subText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetRow(content, 1);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) };
            string ok = Loc(owner, "str_OK", "OK"), cancel = Loc(owner, "str_Cancel", "Cancel"), yes = Loc(owner, "str_Yes", "Yes"), no = Loc(owner, "str_No", "No");

            void AddBtn(string text, MessageBoxResult result, bool outline = false)
            {
                var b = MakeDialogBtn(text, iconColor, iconBrush, mainText, outline);
                b.Click += (_, __) => { tcs.TrySetResult(result); win.Close(); };
                btnPanel.Children.Add(b);
            }

            switch (buttons)
            {
                case MessageBoxButton.OK: AddBtn(ok, MessageBoxResult.OK); break;
                case MessageBoxButton.OKCancel: AddBtn(cancel, MessageBoxResult.Cancel, true); AddBtn(ok, MessageBoxResult.OK); break;
                case MessageBoxButton.YesNo: AddBtn(no, MessageBoxResult.No, true); AddBtn(yes, MessageBoxResult.Yes); break;
                case MessageBoxButton.YesNoCancel: AddBtn(cancel, MessageBoxResult.Cancel, true); AddBtn(no, MessageBoxResult.No, true); AddBtn(yes, MessageBoxResult.Yes); break;
            }

            Grid.SetRow(btnPanel, 2);
            rootGrid.Children.Add(iconCircle);
            rootGrid.Children.Add(content);
            rootGrid.Children.Add(btnPanel);
            outerBorder.Child = rootGrid;
            win.Content = outerBorder;

            win.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                var src = ev.OriginalSource as DependencyObject;
                while (src != null && src != win)
                {
                    if (src is Button) return;
                    src = VisualTreeHelper.GetParent(src);
                }
                if (win.WindowState == WindowState.Normal) win.DragMove();
            };

            win.Loaded += (_, __) =>
            {
                var fa = new DoubleAnimation(0, parentOpacity, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                win.BeginAnimation(Window.OpacityProperty, fa);
            };

            win.ShowDialog();
            return await tcs.Task;
        }

        private static Button MakeDialogBtn(string text, Color ic, Brush ib, Brush mainText, bool outline)
        {
            Brush fill = new SolidColorBrush(ic);
            var btn = new Button { Content = text, Width = 100, Height = 38, Margin = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold, FontSize = 13 };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.BorderThicknessProperty, outline ? new Thickness(1.5) : new Thickness(0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;

            if (outline)
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = mainText;
                btn.BorderBrush = fill;
                btn.MouseEnter += (_, __) => btn.Background = new SolidColorBrush(Color.FromArgb(35, ic.R, ic.G, ic.B));
                btn.MouseLeave += (_, __) => btn.Background = Brushes.Transparent;
            }
            else
            {
                btn.Background = fill;
                btn.Foreground = Brushes.White;
                btn.BorderBrush = fill;
                btn.MouseEnter += (_, __) => btn.Background = new SolidColorBrush(Color.FromRgb((byte)(ic.R * .85), (byte)(ic.G * .85), (byte)(ic.B * .85)));
                btn.MouseLeave += (_, __) => btn.Background = fill;
            }

            btn.PreviewMouseLeftButtonDown += (_, __) => btn.Opacity = .85;
            btn.PreviewMouseLeftButtonUp += (_, __) => btn.Opacity = 1;
            return btn;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    }
}