using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Dreams.Themes;
using System.Collections.Concurrent;

// ✅ استخدام alias لتجنب التعارض مع System.Windows.Media
using WinForms = System.Drawing;
using WinFormsImaging = System.Drawing.Imaging;

namespace Dreams.Pages
{
    public partial class InstallPage : Page
    {
        // ═════════════════════════════════════════════════════════════════
        // ███ FIELDS
        // ═════════════════════════════════════════════════════════════════

        #region Fields
        private const string PROGRAMS_PATH = "Programs";

        private bool _isDarkMode, _isInstalling, _isPaused,
                     _isUpdatingSelection, _isInstallationDone;
        public bool IsInstalling => _isInstalling;

        private int _totalApps, _currentInstallIndex, _completedApps,
                    _seconds, _minutes, _hours;

        private List<CheckBox> _allCheckBoxes = new List<CheckBox>();
        private List<TextBlock> _statusTexts = new List<TextBlock>();
        private List<Border> _categoryBorders = new List<Border>();

        private List<string> _selectedAppsPaths = new List<string>();
        private List<string> _selectedAppsNames = new List<string>();
        private HashSet<string> _cancelledApps = new HashSet<string>();
        private volatile bool _isExitPending = false;
        private TaskCompletionSource<bool> _exitDecisionTcs = null;
        private volatile bool _isNavigatingHome = false;

        private Border _currentInstallingCard;
        private TextBlock _currentInstallingStatus;
        private Border _currentInstallingIndicator;

        private CancellationTokenSource _cts;
        private DispatcherTimer _timer, _blinkTimer, _syncTimer;
        private object _selectionLock = new object();
        private string _rootPath;

        private const string PULSE_STORYBOARD_KEY = "PulseStoryboard";
        private Dictionary<CheckBox, DispatcherTimer> _statusTimers =
            new Dictionary<CheckBox, DispatcherTimer>();

        private static readonly System.Windows.Media.Brush FallbackWarningBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        private static readonly System.Windows.Media.Brush FallbackSuccessBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
        private static readonly System.Windows.Media.Brush FallbackAccentBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 165, 233));
        private static readonly System.Windows.Media.Brush FallbackMainTextBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59));
        private static readonly System.Windows.Media.Brush FallbackSubTextBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));

        private static readonly System.Windows.Media.Color[] RandomCategoryPalette = new[]
        {
            System.Windows.Media.Color.FromRgb(99, 102, 241),
            System.Windows.Media.Color.FromRgb(139, 92, 246),
            System.Windows.Media.Color.FromRgb(236, 72, 153),
            System.Windows.Media.Color.FromRgb(244, 63, 94),
            System.Windows.Media.Color.FromRgb(245, 158, 11),
            System.Windows.Media.Color.FromRgb(16, 185, 129),
            System.Windows.Media.Color.FromRgb(6, 182, 212),
            System.Windows.Media.Color.FromRgb(59, 130, 246),
            System.Windows.Media.Color.FromRgb(130, 135, 145),
            System.Windows.Media.Color.FromRgb(168, 85, 247)
        };

        // 🎨 تخزين دائم لألوان الفولدرات المخصصة (غير الـ default)
        private static readonly string _customColorsRegistryPath = @"SOFTWARE\Dreams\CustomCategoryColors";
        private Dictionary<string, System.Windows.Media.Color> _customCategoryColors =
            new Dictionary<string, System.Windows.Media.Color>(StringComparer.OrdinalIgnoreCase);

        // ✅ حقول التابات والتخطيط الجديد
        private string _currentCategory = "All";
        private bool _isTabsDragging = false;
        private bool _tabsMouseDown = false;
        private System.Windows.Point _tabsDragStartPoint;
        private double _tabsScrollStartOffset;
        private DispatcherTimer _resizeDebouncer;
        private List<Border> _categoryHeaders = new List<Border>();
        private List<(string CategoryName, Border Header, WrapPanel Wrap, List<Border> Cards)> _categoryGroups
            = new List<(string, Border, WrapPanel, List<Border>)>();

        // ✅ حقول الـ Quick Menu (Queue Popup)
        private Window _queueWindow = null;
        private StackPanel _queueList = null;

        // ✅ نظام تتبع ترتيب الاختيار
        private long _selectionCounter = 0;
        private Dictionary<string, long> _selectionOrders = new Dictionary<string, long>(); // ExePath → Order

        // ✅ Drag & Drop في الـ Queue
        private Border _draggedRow = null;
        private System.Windows.Point _dragStartPoint;
        private bool _isDragging = false;
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ ICON EXTRACTOR (Hi-Resolution) - ULTIMATE VERSION
        // ═════════════════════════════════════════════════════════════════

        #region Icon Extractor (Hi-Resolution) - ULTIMATE VERSION

        private static readonly ConcurrentDictionary<string, ImageSource> _iconCache
            = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        // ✅ Throttle: max 6 استخراجات في وقت واحد
        private static readonly SemaphoreSlim _iconSemaphore = new SemaphoreSlim(6, 6);

        private static readonly string _customIconsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dreams", "CustomIcons");
        private const int ICON_DISPLAY_SIZE = 28;

        #region Win32 API

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct IMAGELISTDRAWPARAMS
        {
            public int cbSize;
            public IntPtr himl;
            public int i;
            public IntPtr hdcDst;
            public int x, y, cx, cy, xBitmap, yBitmap;
            public uint rgbBk, rgbFg;
            public uint fStyle, dwRop, fState, Frame, crEffect;
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [System.Runtime.InteropServices.PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [System.Runtime.InteropServices.PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [System.Runtime.InteropServices.PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [System.Runtime.InteropServices.PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [System.Runtime.InteropServices.PreserveSig] int AddMasked(IntPtr hbmImage, uint crMask, ref int pi);
            [System.Runtime.InteropServices.PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
            [System.Runtime.InteropServices.PreserveSig] int Remove(int i);
            [System.Runtime.InteropServices.PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("Shell32.dll", EntryPoint = "ExtractIconExW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern int ExtractIconEx(string sFile, int iIndex, IntPtr[] piLargeVersion, IntPtr[] piSmallVersion, int amountIcons);

        // 🔥 السلاح السري: PrivateExtractIcons — بيتجاوز Shell Cache كلياً
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int PrivateExtractIcons(
            string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, int[] piconid, int nIcons, uint flags);

        // ✅ SHChangeNotify لإجبار Shell على إعادة تحميل الـ icon cache لملف معين
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_FLUSH = 0x1000;

        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_LARGEICON = 0x0;

        private const int SHIL_LARGE = 0x0;
        private const int SHIL_EXTRALARGE = 0x2;
        private const int SHIL_JUMBO = 0x4;

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // 🔥 المحرك الأساسي الجديد — PrivateExtractIcons
        // ═════════════════════════════════════════════════════════════════
        private static ImageSource ExtractIconViaPrivateAPI(string filePath, int size = 256)
        {
            IntPtr[] hIcons = new IntPtr[1] { IntPtr.Zero };
            int[] iconIds = new int[1];

            try
            {
                // 🎯 PrivateExtractIcons بيقرأ من الـ EXE مباشرة، مش من Shell Cache
                int result = PrivateExtractIcons(filePath, 0, size, size, hIcons, iconIds, 1, 0);

                if (result <= 0 || hIcons[0] == IntPtr.Zero) return null;

                using (var icon = WinForms.Icon.FromHandle(hIcons[0]))
                using (var bmp = icon.ToBitmap())
                {
                    if (bmp.Width < 16 || IsBitmapEmpty(bmp)) return null;

                    // ✅ بنشيك بس على لو الحجم Jumbo، وحتى لو ديفولت في 256، نجرب 48 برضو
                    if (size >= 128 && IsDefaultExeIcon(bmp)) return null;

                    return BitmapToImageSource(bmp);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PrivateExtractIcons: {ex.Message}");
                return null;
            }
            finally
            {
                if (hIcons[0] != IntPtr.Zero) DestroyIcon(hIcons[0]);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 🔥 الـ Pipeline الرئيسي
        // ═════════════════════════════════════════════════════════════════
        private static ImageSource ExtractHiResIcon(string filePath, bool bypassCache = false)
        {
            try
            {
                // 1️⃣ أولاً: PrivateExtractIcons بحجم كبير (256) — الأنقى والأسرع
                var icon = ExtractIconViaPrivateAPI(filePath, 256);
                if (icon != null) return icon;

                // 2️⃣ جرب بحجم متوسط (128)
                icon = ExtractIconViaPrivateAPI(filePath, 128);
                if (icon != null) return icon;

                // 3️⃣ جرب بحجم 48
                icon = ExtractIconViaPrivateAPI(filePath, 48);
                if (icon != null) return icon;

                // 4️⃣ Fallback: ExtractIconEx من الـ EXE resources
                var fromResources = TryExtractIconFromExeResources(filePath);
                if (fromResources != null) return fromResources;

                // 5️⃣ Fallback أخير: Shell IImageList (لو ال PrivateAPI فشلت)
                if (!bypassCache)
                {
                    var fromShell = TryExtractFromImageList(filePath, SHIL_JUMBO);
                    if (fromShell != null) return fromShell;
                }

                // 6️⃣ آخر محاولة: Associated icon
                using (var sysIcon = WinForms.Icon.ExtractAssociatedIcon(filePath))
                {
                    if (sysIcon == null) return null;
                    using (var bmp = sysIcon.ToBitmap())
                        return BitmapToImageSource(bmp);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractHiResIcon: {ex.Message}");
                return null;
            }
        }

        private static ImageSource TryExtractIconFromExeResources(string exePath)
        {
            IntPtr[] large = new IntPtr[1] { IntPtr.Zero };
            IntPtr[] small = new IntPtr[1] { IntPtr.Zero };
            try
            {
                int count = ExtractIconEx(exePath, 0, large, small, 1);
                if (count <= 0 || large[0] == IntPtr.Zero) return null;

                using (var icon = WinForms.Icon.FromHandle(large[0]))
                using (var bmp = icon.ToBitmap())
                {
                    if (bmp.Width < 16 || IsBitmapEmpty(bmp)) return null;
                    return BitmapToImageSource(bmp);
                }
            }
            catch { return null; }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }

        private static ImageSource TryExtractFromImageList(string filePath, int imageListSize)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                var shinfo = new SHFILEINFO();
                SHGetFileInfo(filePath, 0, ref shinfo,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
                    SHGFI_SYSICONINDEX | SHGFI_LARGEICON);

                var iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                if (SHGetImageList(imageListSize, ref iidImageList, out IImageList imageList) != 0 || imageList == null)
                    return null;

                imageList.GetIcon(shinfo.iIcon, 0x00000001, ref hIcon);
                if (hIcon == IntPtr.Zero) return null;

                using (var bitmap = WinForms.Icon.FromHandle(hIcon).ToBitmap())
                {
                    if (bitmap.Width < 32 || IsBitmapEmpty(bitmap)) return null;
                    if (IsDefaultExeIcon(bitmap)) return null;
                    return BitmapToImageSource(bitmap);
                }
            }
            catch { return null; }
            finally { if (hIcon != IntPtr.Zero) DestroyIcon(hIcon); }
        }

        // ═════════════════════════════════════════════════════════════════
        // ✅ كشف Default Icon — Fast (LockBits)
        // ═════════════════════════════════════════════════════════════════
        private static bool IsDefaultExeIcon(WinForms.Bitmap bmp)
        {
            if (bmp == null || bmp.Width < 16) return false;

            WinFormsImaging.BitmapData data = null;
            try
            {
                var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                data = bmp.LockBits(rect, WinFormsImaging.ImageLockMode.ReadOnly,
                    WinFormsImaging.PixelFormat.Format32bppArgb);

                int bytes = Math.Abs(data.Stride) * bmp.Height;
                byte[] rgbValues = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, rgbValues, 0, bytes);

                var colorCounts = new Dictionary<int, int>();
                int totalPixels = 0;
                int stepX = Math.Max(1, bmp.Width / 24);
                int stepY = Math.Max(1, bmp.Height / 24);

                for (int y = 0; y < bmp.Height; y += stepY)
                {
                    int rowStart = y * data.Stride;
                    for (int x = 0; x < bmp.Width; x += stepX)
                    {
                        int idx = rowStart + x * 4;
                        if (idx + 3 >= bytes) continue;

                        byte a = rgbValues[idx + 3];
                        if (a < 50) continue;

                        int colorKey = ((rgbValues[idx + 2] >> 4) << 16) |
                                       ((rgbValues[idx + 1] >> 4) << 8) |
                                       (rgbValues[idx] >> 4);
                        if (!colorCounts.ContainsKey(colorKey)) colorCounts[colorKey] = 0;
                        colorCounts[colorKey]++;
                        totalPixels++;
                    }
                }

                if (totalPixels < 10) return false;
                if (colorCounts.Count > 30) return false;

                var topColors = colorCounts.OrderByDescending(kv => kv.Value).Take(3).ToList();
                double topRatio = (double)topColors.Sum(kv => kv.Value) / totalPixels;

                if (topRatio > 0.80 && colorCounts.Count < 12)
                {
                    int grayishCount = topColors.Count(kv =>
                    {
                        int r = (kv.Key >> 16) & 0xFF;
                        int g = (kv.Key >> 8) & 0xFF;
                        int b = kv.Key & 0xFF;
                        int diff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
                        return diff < 4;
                    });
                    return grayishCount >= 2;
                }
                return false;
            }
            catch { return false; }
            finally { if (data != null) bmp.UnlockBits(data); }
        }

        private static bool IsBitmapEmpty(WinForms.Bitmap bmp)
        {
            WinFormsImaging.BitmapData data = null;
            try
            {
                var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                data = bmp.LockBits(rect, WinFormsImaging.ImageLockMode.ReadOnly,
                    WinFormsImaging.PixelFormat.Format32bppArgb);

                int stride = data.Stride;
                int bytes = stride * bmp.Height;
                byte[] buffer = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);

                int checks = 0, transparent = 0;
                int step = Math.Max(1, bmp.Width / 8);
                for (int y = 0; y < bmp.Height; y += step)
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        int idx = y * stride + x * 4 + 3;
                        if (idx >= bytes) continue;
                        checks++;
                        if (buffer[idx] == 0) transparent++;
                    }
                return checks > 0 && transparent == checks;
            }
            catch { return false; }
            finally { if (data != null) bmp.UnlockBits(data); }
        }

        private static ImageSource BitmapToImageSource(WinForms.Bitmap bitmap)
        {
            try
            {
                WinForms.Bitmap targetBitmap = bitmap;
                bool needsDispose = false;

                if (bitmap.Width < 48)
                {
                    int newSize = 96;
                    targetBitmap = new WinForms.Bitmap(newSize, newSize, WinFormsImaging.PixelFormat.Format32bppArgb);
                    targetBitmap.SetResolution(96, 96);

                    using (var g = WinForms.Graphics.FromImage(targetBitmap))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.Clear(System.Drawing.Color.Transparent);

                        double scale = Math.Min((double)newSize / bitmap.Width, (double)newSize / bitmap.Height);
                        int drawW = (int)(bitmap.Width * scale);
                        int drawH = (int)(bitmap.Height * scale);
                        g.DrawImage(bitmap, (newSize - drawW) / 2, (newSize - drawH) / 2, drawW, drawH);
                    }
                    needsDispose = true;
                }

                var hBitmap = targetBitmap.GetHbitmap();
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(hBitmap);
                    if (needsDispose) targetBitmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BitmapToImageSource: {ex.Message}");
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // ✅ Extract Async مع Semaphore Throttling
        // ═════════════════════════════════════════════════════════════════
        private static async Task<ImageSource> ExtractIconAsync(string exePath, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;

            string customIconPath = GetCustomIconPath(exePath);
            bool hasCustom = File.Exists(customIconPath);
            string cacheKey = hasCustom ? $"{exePath}|custom" : exePath;

            if (!forceRefresh && _iconCache.TryGetValue(cacheKey, out var cached))
                return cached;

            await _iconSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var imageSource = await Task.Run(() =>
                {
                    try
                    {
                        if (hasCustom) return LoadImageFromFile(customIconPath);
                        if (!File.Exists(exePath)) return null;

                        // ✅ لو forceRefresh = true، نطلب من Shell يحدث الـ cache الأول
                        if (forceRefresh)
                        {
                            try
                            {
                                IntPtr pathPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(exePath);
                                try
                                {
                                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, pathPtr, IntPtr.Zero);
                                }
                                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pathPtr); }
                            }
                            catch { }
                        }

                        return ExtractHiResIcon(exePath, bypassCache: forceRefresh);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ExtractIcon: {ex.Message}");
                        return null;
                    }
                }).ConfigureAwait(true);

                if (imageSource != null)
                    _iconCache[cacheKey] = imageSource;   // ✅ overwrite بدل TryAdd

                return imageSource;
            }
            finally { _iconSemaphore.Release(); }
        }

        private static ImageSource LoadImageFromFile(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }

        private static string GetCustomIconPath(string exePath)
        {
            string hash = exePath.GetHashCode().ToString("X");
            string safeName = Path.GetFileNameWithoutExtension(exePath);
            return Path.Combine(_customIconsPath, $"{safeName}_{hash}.png");
        }

        private async Task LoadIconForImageAsync(System.Windows.Controls.Image imageControl, string exePath)
        {
            try
            {
                var iconSource = await ExtractIconAsync(exePath, forceRefresh: false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (iconSource != null)
                    {
                        imageControl.Source = iconSource;
                        imageControl.Opacity = 1;
                        imageControl.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        imageControl.Visibility = Visibility.Collapsed;
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex) { Debug.WriteLine($"LoadIconForImageAsync: {ex.Message}"); }
        }

        private async Task RefreshIconAsync(System.Windows.Controls.Image imageControl, string exePath)
        {
            _iconCache.TryRemove(exePath, out _);
            _iconCache.TryRemove($"{exePath}|custom", out _);
            imageControl.Visibility = Visibility.Visible;
            imageControl.Opacity = 0;
            await LoadIconForImageAsync(imageControl, exePath);
        }

        // ✅ Refresh قوي بيتجاوز كل caches (تطبيق + Shell + Windows)
        private async Task ForceRefreshIconAsync(System.Windows.Controls.Image iconImage, string exePath)
        {
            try
            {
                // 1️⃣ امسح cache التطبيق
                _iconCache.TryRemove(exePath, out _);
                _iconCache.TryRemove($"{exePath}|custom", out _);

                // 2️⃣ Animation: fade out + spinner
                await Dispatcher.InvokeAsync(() =>
                {
                    var fadeOut = new DoubleAnimation(iconImage.Opacity, 0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                    };
                    iconImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                });
                await Task.Delay(160);

                await Dispatcher.InvokeAsync(() =>
                {
                    iconImage.BeginAnimation(UIElement.OpacityProperty, null);
                    iconImage.Source = null;
                    iconImage.Opacity = 0;
                });

                // 3️⃣ ⚡ القلب: امسح Shell Icon Cache و طبّق forceRefresh
                await Task.Run(() =>
                {
                    try
                    {
                        // إخطار Shell إن الملف اتغير
                        IntPtr pathPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(exePath);
                        try
                        {
                            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, pathPtr, IntPtr.Zero);
                            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
                        }
                        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pathPtr); }
                    }
                    catch (Exception ex) { Debug.WriteLine($"SHChangeNotify: {ex.Message}"); }
                });

                // 4️⃣ Small delay عشان Shell يستوعب التغيير
                await Task.Delay(50);

                // 5️⃣ استخراج بـ forceRefresh
                var iconSource = await ExtractIconAsync(exePath, forceRefresh: true);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (iconSource != null)
                    {
                        iconImage.Source = iconSource;
                        iconImage.Visibility = Visibility.Visible;
                        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                        };
                        iconImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    }
                    else
                    {
                        iconImage.Visibility = Visibility.Collapsed;
                    }
                }, DispatcherPriority.Background);

                Debug.WriteLine($"✅ Icon refreshed: {Path.GetFileName(exePath)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ForceRefreshIconAsync error: {ex.Message}");
            }
        }

        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ LOCALIZATION HELPERS
        // ═════════════════════════════════════════════════════════════════

        #region Localization Helpers
        private string GetLocalizedString(string key, string fallback = "")
        {
            try
            {
                if (this.FindResource(key) is string localized) return localized;
                if (Application.Current?.Resources.Contains(key) == true &&
                    Application.Current.Resources[key] is string appLocalized)
                    return appLocalized;
            }
            catch { }
            return fallback;
        }

        private System.Windows.Media.Brush GetBrushSafe(string resourceKey, System.Windows.Media.Brush fallback)
        {
            try { return TryFindResource(resourceKey) as System.Windows.Media.Brush ?? fallback; }
            catch { return fallback; }
        }

        private Style GetStyleSafe(string resourceKey)
        {
            try { return TryFindResource(resourceKey) as Style; }
            catch { return null; }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ CONSTRUCTOR & INIT
        // ═════════════════════════════════════════════════════════════════

        #region Constructor & Init
        public InstallPage()
        {
            InitializeComponent();
            _rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PROGRAMS_PATH);

            App.LanguageChanged += OnLanguageChanged;
            App.FlowDirectionChanged += OnFlowDirectionChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            ThemeManager.OpacityChanged += OnOpacityChanged;

            this.Loaded += InstallPage_Loaded;
            this.Unloaded += InstallPage_Unloaded;

            LoadThemePreference();
            ApplySavedOpacity();
            InitTimers();
            LoadPrograms();
            SetButtonToStart();
        }

        private void ApplySavedOpacity()
        {
            try
            {
                double opacity = ThemeManager.GetSavedOpacity();
                var window = Window.GetWindow(this);
                if (window != null) window.Opacity = opacity;
            }
            catch (Exception ex) { Debug.WriteLine($"Error applying opacity: {ex.Message}"); }
        }

        private void OnThemeChanged(bool isDark)
        {
            Dispatcher.Invoke(() =>
            {
                var mw = Window.GetWindow(this) as MainWindow;
                if (mw?.FindName("btnTheme") is Button themeBtn)
                    themeBtn.Content = isDark ? "\uE706" : "\uE708";
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

        private void OnLanguageChanged(string langCode)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusText();
                UpdateCategoryColors();
                UpdateSelectAllButtonText();
                RefreshMainButtonText();
            });
        }

        private void OnFlowDirectionChanged(FlowDirection direction)
        {
            Dispatcher.Invoke(() =>
            {
                this.FlowDirection = direction;
            });
        }

        private void InstallPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isDarkMode = ThemeManager.IsDarkMode;
            ApplyTheme(_isDarkMode);
        }

        private void InstallPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void Cleanup()
        {
            App.LanguageChanged -= OnLanguageChanged;
            App.FlowDirectionChanged -= OnFlowDirectionChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.OpacityChanged -= OnOpacityChanged;
            this.Loaded -= InstallPage_Loaded;
            this.Unloaded -= InstallPage_Unloaded;

            _timer?.Stop();
            _blinkTimer?.Stop();
            _syncTimer?.Stop();
            _cts?.Cancel();

            _resizeDebouncer?.Stop();
            try { _queueWindow?.Close(); } catch { }
        }

        private void InitTimers()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                _seconds++;
                if (_seconds == 60) { _seconds = 0; _minutes++; }
                if (_minutes == 60) { _minutes = 0; _hours++; }
                lblTimer.Text = $"{_hours:D2}:{_minutes:D2}:{_seconds:D2}";
            };

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _blinkTimer.Tick += (s, e) =>
            {
                if (lblStatusText != null)
                    lblStatusText.Visibility = lblStatusText.Visibility == Visibility.Visible
                        ? Visibility.Collapsed : Visibility.Visible;
            };

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _syncTimer.Tick += async (s, e) =>
            {
                if (_isInstalling && !_isPaused) await AutoSyncQueue();
            };
            _syncTimer.Start();
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ LOAD PROGRAMS (NEW VERSION WITH TABS & WRAPPANEL)
        // ═════════════════════════════════════════════════════════════════

        #region Load Programs
        private void LoadPrograms()
        {
            try
            {
                if (!Directory.Exists(_customIconsPath)) Directory.CreateDirectory(_customIconsPath);
                CreateDefaultFolderStructure();
                LoadCustomCategoryColors();

                _allCheckBoxes.Clear(); _statusTexts.Clear(); _categoryHeaders.Clear(); _categoryGroups.Clear();
                Column1Container.Items.Clear();

                if (!Directory.Exists(_rootPath))
                {
                    try { Directory.CreateDirectory(_rootPath); } catch { }
                    ShowEmptyMessage();
                    BuildCategoryTabs(new List<string>());
                    return;
                }

                // ✅ قائمة الأقسام بالـ Keys (ديناميكية)
                string[] categoryKeys = new[]
                {
            "Category_Compression", "Category_Design", "Category_Drivers", "Category_Gaming",
            "Category_Internet", "Category_Media", "Category_Office", "Category_PDF",
            "Category_Security", "Category_System", "Category_Tools", "Category_Utilities"
        };

                var allDirs = Directory.GetDirectories(_rootPath);
                if (allDirs.Length == 0)
                {
                    ShowEmptyMessage();
                    BuildCategoryTabs(new List<string>());
                    return;
                }

                // ترتيب المجلدات حسب الـ Keys
                var categories = allDirs.OrderBy(p =>
                {
                    var name = Path.GetFileName(p);
                    var index = Array.IndexOf(categoryKeys, name);
                    return index == -1 ? int.MaxValue : index;
                }).ToList();

                int totalApps = 0, validCategories = 0;
                var categoryNames = new List<string>();

                foreach (var categoryPath in categories)
                {
                    try
                    {
                        var appEntries = new List<(string DisplayName, string ExePath)>();
                        var directExes = Directory.GetFiles(categoryPath, "*.exe", SearchOption.TopDirectoryOnly);
                        foreach (var exe in directExes)
                            appEntries.Add((Path.GetFileNameWithoutExtension(exe), exe));

                        var subDirs = Directory.GetDirectories(categoryPath);
                        foreach (var subDir in subDirs)
                        {
                            var subExes = Directory.GetFiles(subDir, "*.exe", SearchOption.TopDirectoryOnly);
                            if (subExes.Length == 0) continue;
                            var bestExe = subExes.FirstOrDefault(f =>
                            {
                                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                                return n == "setup" || n == "install";
                            }) ?? subExes.OrderBy(f => f).First();
                            appEntries.Add((Path.GetFileName(subDir), bestExe));
                        }

                        if (appEntries.Count == 0) continue;

                        string catName = Path.GetFileName(categoryPath);
                        string localizedCatName = GetLocalizedCategoryName(catName); // ✅ اسم ديناميكي
                        validCategories++;
                        categoryNames.Add(localizedCatName);

                        var header = CreateCategoryHeader(localizedCatName, catName);
                        Column1Container.Items.Add(header);

                        var wrap = new WrapPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Margin = new Thickness(0, 0, 0, 10),
                            Tag = catName
                        };
                        var cardsList = new List<Border>();

                        foreach (var (displayName, exePath) in appEntries)
                        {
                            var card = CreateAppCard(displayName, exePath, catName);
                            if (card != null)
                            {
                                wrap.Children.Add(card);
                                cardsList.Add(card);
                                totalApps++;
                            }
                        }

                        Column1Container.Items.Add(wrap);
                        _categoryHeaders.Add(header);
                        _categoryGroups.Add((localizedCatName, header, wrap, cardsList));
                    }
                    catch (Exception ex) { Debug.WriteLine($"Error loading category {categoryPath}: {ex.Message}"); }
                }

                if (validCategories == 0 || totalApps == 0)
                {
                    ShowEmptyMessage();
                    BuildCategoryTabs(new List<string>());
                    return;
                }

                _totalApps = totalApps;
                lblTotalApps.Text = totalApps.ToString();
                lblTotalAppsCount.Text = "0";

                BuildCategoryTabs(categoryNames);
                UpdateSelectAllButtonText();
                UpdateStatusText();

                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    UpdateCardsWidth();
                    StartLazyIconLoading();
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadPrograms Error: {ex.Message}");
                ShowEmptyMessage();
            }
        }
        private string GetLocalizedCategoryName(string folderName)
        {
            // ✅ تحويل اسم الفولدر إلى مفتاح الترجمة
            string key = folderName switch
            {
                "Compression" => "Category_Compression",
                "Design" => "Category_Design",
                "Drivers" => "Category_Drivers",
                "Gaming" => "Category_Gaming",
                "Internet" => "Category_Internet",
                "Media" => "Category_Media",
                "Office" => "Category_Office",
                "PDF" => "Category_PDF",
                "Security" => "Category_Security",
                "System" => "Category_System",
                "Tools" => "Category_Tools",
                "Utilities" => "Category_Utilities",
                _ => folderName
            };

            string localized = GetLocalizedString(key, folderName);
            return localized;
        }
        // ✅ تحميل الأيقونات بشكل تدريجي بعد رسم الـ UI (لمنع الفريز)
        private async void StartLazyIconLoading()
        {
            try
            {
                // اعمل قائمة بكل الأيقونات اللي محتاجين نحملها
                var iconTasks = new List<(System.Windows.Controls.Image Img, string Path)>();

                foreach (var (_, _, _, cards) in _categoryGroups)
                {
                    foreach (var card in cards)
                    {
                        var grid = card.Child as Grid;
                        var img = grid?.Children.OfType<System.Windows.Controls.Image>().FirstOrDefault();
                        var chk = grid?.Children.OfType<CheckBox>().FirstOrDefault();
                        string path = chk?.Tag?.ToString();
                        if (img != null && !string.IsNullOrEmpty(path))
                            iconTasks.Add((img, path));
                    }
                }

                // ✅ Batch من 10 + delay صغير لكل دفعة
                const int batchSize = 10;
                for (int i = 0; i < iconTasks.Count; i += batchSize)
                {
                    var batch = iconTasks.Skip(i).Take(batchSize).ToList();
                    var tasks = batch.Select(t => LoadIconForImageAsync(t.Img, t.Path)).ToArray();
                    await Task.WhenAll(tasks);
                    await Task.Delay(10);  // فسحة للـ UI
                }
            }
            catch (Exception ex) { Debug.WriteLine($"StartLazyIconLoading: {ex.Message}"); }
        }

        private void CreateDefaultFolderStructure()
        {
            try
            {
                if (!Directory.Exists(_rootPath)) Directory.CreateDirectory(_rootPath);
                string[] defaultCategories = new[]
                {
                    "Compression", "Design", "Drivers", "Gaming", "Internet",
                    "Media", "Office", "PDF", "Security", "System", "Tools", "Utilities"
                };
                foreach (string category in defaultCategories)
                {
                    string categoryPath = Path.Combine(_rootPath, category);
                    if (!Directory.Exists(categoryPath)) Directory.CreateDirectory(categoryPath);
                }
                string readmePath = Path.Combine(_rootPath, "README.txt");
                if (!File.Exists(readmePath))
                {
                    string readmeContent = "Dreams Software Installer\n\nPlace .exe files in category folders.";
                    File.WriteAllText(readmePath, readmeContent, System.Text.Encoding.UTF8);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"CreateDefaultFolderStructure error: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════
        // ███ CUSTOM CATEGORY COLORS (Persistent)
        // ═════════════════════════════════════════════════════════════════

        private void LoadCustomCategoryColors()
        {
            try
            {
                _customCategoryColors.Clear();
                using var key = Registry.CurrentUser.OpenSubKey(_customColorsRegistryPath);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    var colorStr = key.GetValue(valueName)?.ToString();
                    if (string.IsNullOrEmpty(colorStr)) continue;

                    try
                    {
                        var color = (System.Windows.Media.Color)
                            System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
                        _customCategoryColors[valueName] = color;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"LoadCustomCategoryColors: {ex.Message}"); }
        }

        private void SaveCustomCategoryColor(string categoryName, System.Windows.Media.Color color)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(_customColorsRegistryPath);
                string colorStr = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                key.SetValue(categoryName, colorStr);
                _customCategoryColors[categoryName] = color;
            }
            catch (Exception ex) { Debug.WriteLine($"SaveCustomCategoryColor: {ex.Message}"); }
        }

        private (string Icon, System.Windows.Media.Color Color) GetCategoryIcon(string name)
        {
            // ✅ الفولدرات الافتراضية بألوانها الثابتة
            var icons = new Dictionary<string, (string, System.Windows.Media.Color)>(StringComparer.OrdinalIgnoreCase)
            {
                { "Compression", ("\uF012", System.Windows.Media.Color.FromRgb(230, 126, 34)) },
                { "Design",      ("\uE790", System.Windows.Media.Color.FromRgb(155, 89, 182)) },
                { "Drivers",     ("\uE773", System.Windows.Media.Color.FromRgb(52, 152, 219)) },
                { "Gaming",      ("\uE7FC", System.Windows.Media.Color.FromRgb(231, 76, 60)) },
                { "Internet",    ("\uE774", System.Windows.Media.Color.FromRgb(46, 204, 113)) },
                { "Media",       ("\uE7F3", System.Windows.Media.Color.FromRgb(241, 196, 15)) },
                { "Office",      ("\uF000", System.Windows.Media.Color.FromRgb(230, 126, 34)) },
                { "PDF",         ("\uE160", System.Windows.Media.Color.FromRgb(231, 76, 60)) },
                { "Security",    ("\uEA18", System.Windows.Media.Color.FromRgb(52, 152, 219)) },
                { "System",      ("\uE74C", System.Windows.Media.Color.FromRgb(149, 165, 166)) },
                { "Tools",       ("\uE90F", System.Windows.Media.Color.FromRgb(52, 152, 219)) },
                { "Utilities",   ("\uE821", System.Windows.Media.Color.FromRgb(149, 15, 166)) }
            };

            if (icons.TryGetValue(name, out var result)) return result;

            // ✅ فولدر مخصص: استخدم الأيقونة \uF158 (Package/Badge icon)
            const string customIcon = "\uF158";

            // 🎨 لو اللون متخزن قبل كده → ارجعه زي ما هو
            if (_customCategoryColors.TryGetValue(name, out var savedColor))
                return (customIcon, savedColor);

            // 🎲 لو فولدر جديد → اختار لون عشوائي واحفظه
            var random = new Random(name.GetHashCode()); // ✅ Deterministic بناءً على الاسم
            var newColor = RandomCategoryPalette[random.Next(RandomCategoryPalette.Length)];
            SaveCustomCategoryColor(name, newColor);
            return (customIcon, newColor);
        }

        private Border CreateCategoryHeader(string displayName, string folderName)
        {
            var (icon, color) = GetCategoryIcon(folderName);
            var categoryColorBrush = new SolidColorBrush(color);

            var hdr = new Border
            {
                Margin = new Thickness(6, 15, 6, 10),
                Padding = new Thickness(0, 4, 0, 4),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = "CategoryHeader"
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var accentBar = new Border
            {
                Width = 5,
                Height = 28,
                CornerRadius = new CornerRadius(3),
                Background = categoryColorBrush,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(accentBar, 0);

            var iconBd = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = categoryColorBrush,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBd.Child = new TextBlock
            {
                Text = icon,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconBd, 1);

            var tb = new TextBlock
            {
                Text = displayName.ToUpper(),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = categoryColorBrush
            };
            Grid.SetColumn(tb, 2);

            grid.Children.Add(accentBar);
            grid.Children.Add(iconBd);
            grid.Children.Add(tb);
            hdr.Child = grid;

            hdr.MouseEnter += (s, e) =>
            {
                accentBar.Opacity = 0.85;
                iconBd.Opacity = 0.85;
                tb.Opacity = 0.85;
            };
            hdr.MouseLeave += (s, e) =>
            {
                accentBar.Opacity = 1;
                iconBd.Opacity = 1;
                tb.Opacity = 1;
            };

            hdr.PreviewMouseDown += (s, e) =>
            {
                e.Handled = true;
                var group = _categoryGroups.FirstOrDefault(g => g.CategoryName == displayName);
                if (group.Cards == null) return;

                var checkBoxes = new List<CheckBox>();
                foreach (var card in group.Cards)
                {
                    var cardGrid = card.Child as Grid;
                    var chk = cardGrid?.Children.OfType<CheckBox>().FirstOrDefault();
                    if (chk != null && chk.IsEnabled) checkBoxes.Add(chk);
                }
                if (checkBoxes.Count > 0)
                {
                    bool allSelected = checkBoxes.All(c => c.IsChecked == true);
                    foreach (var chk in checkBoxes) chk.IsChecked = !allSelected;
                    UpdateSelection();
                }
            };

            return hdr;
        }

        private Border CreateAppCard(string displayName, string exePath, string categoryName)
        {
            try
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Height = 58,
                    Margin = new Thickness(5),
                    Cursor = Cursors.Hand,
                    Tag = categoryName
                };
                card.SetResourceReference(Border.BackgroundProperty, "DynamicCardBg");
                card.SetResourceReference(Border.BorderBrushProperty, "DynamicBorderBrush");

                card.MouseEnter += (s, e) =>
                {
                    var g = card.Child as Grid;
                    var chk = g?.Children.OfType<CheckBox>().FirstOrDefault();
                    if (chk != null && chk.IsEnabled)
                        card.Background = GetBrushSafe("DynamicHoverBg", FallbackAccentBrush);
                };
                card.MouseLeave += (s, e) =>
                {
                    var g = card.Child as Grid;
                    var chk = g?.Children.OfType<CheckBox>().FirstOrDefault();
                    if (chk != null && chk.IsEnabled)
                        card.SetResourceReference(Border.BackgroundProperty, "DynamicCardBg");
                };
                card.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    var g = card.Child as Grid;
                    var chk = g?.Children.OfType<CheckBox>().FirstOrDefault();
                    if (chk == null || !chk.IsEnabled) { e.Handled = true; return; }
                    var src = e.OriginalSource as DependencyObject;
                    var cur = src;
                    while (cur != null && cur != card)
                    {
                        if (cur is CheckBox) return;
                        cur = VisualTreeHelper.GetParent(cur);
                    }
                    e.Handled = true;
                    chk.IsChecked = !chk.IsChecked;
                    UpdateSelection();
                };

                var checkBox = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    Tag = exePath,
                    Cursor = Cursors.Hand
                };
                try { var style = GetStyleSafe("ModernCheckBox"); if (style != null) checkBox.Style = style; } catch { }

                checkBox.Checked += (s, e) =>
                {
                    // ✅ تسجيل ترتيب الاختيار
                    string path = checkBox.Tag?.ToString();
                    if (!string.IsNullOrEmpty(path) && !_selectionOrders.ContainsKey(path))
                        _selectionOrders[path] = ++_selectionCounter;

                    UpdateSelection();
                    if (!_isUpdatingSelection)
                    {
                        if (_isInstalling) AddToInstallationQueue(checkBox.Tag.ToString(), checkBox);
                        else
                        {
                            if (_isInstallationDone) SetButtonToStart();
                            ShowQuickStatus(checkBox, "Queued", "DynamicAccent", "📋");
                        }
                    }
                    RefreshQueuePopupIfOpen();
                };

                checkBox.Unchecked += (s, e) =>
                {
                    // ✅ إلغاء ترتيب الاختيار
                    string path = checkBox.Tag?.ToString();
                    if (!string.IsNullOrEmpty(path)) _selectionOrders.Remove(path);

                    UpdateSelection();
                    if (!_isUpdatingSelection)
                    {
                        bool isCurrentlyActive = false;
                        if (_currentInstallingCard != null)
                        {
                            var currentGrid = _currentInstallingCard.Child as Grid;
                            var currentCheckBox = currentGrid?.Children.OfType<CheckBox>().FirstOrDefault();
                            if (currentCheckBox == checkBox) isCurrentlyActive = true;
                        }
                        if (isCurrentlyActive) MarkCurrentInstallingAsCancelled();
                        else if (_isInstalling) RemoveFromInstallationQueue(checkBox.Tag.ToString(), checkBox);
                        else ShowQuickStatus(checkBox, "Removed", "Warning", "❌");
                    }
                    RefreshQueuePopupIfOpen();
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(checkBox, 0);
                _allCheckBoxes.Add(checkBox);

                var iconImage = new System.Windows.Controls.Image
                {
                    Width = ICON_DISPLAY_SIZE,
                    Height = ICON_DISPLAY_SIZE,
                    MinWidth = ICON_DISPLAY_SIZE,
                    MinHeight = ICON_DISPLAY_SIZE,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.Both,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                };
                RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(iconImage, EdgeMode.Aliased);
                Grid.SetColumn(iconImage, 1);

                // ⚠️ تم إزالة التحميل الفوري هنا - سيتم التحميل من StartLazyIconLoading
                // _ = LoadIconForImageAsync(iconImage, exePath); // تم حذفه

                var capturedExePath = exePath;
                iconImage.MouseRightButtonUp += (s, ev) =>
                {
                    ev.Handled = true;
                    ShowIconContextMenu(iconImage, capturedExePath);
                };
                iconImage.Cursor = Cursors.Hand;
                iconImage.ToolTip = GetLocalizedString("RightClickForOptions", "Right-click for options");

                var nameText = new TextBlock
                {
                    Text = displayName,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 10, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = displayName
                };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "DynamicMainText");
                Grid.SetColumn(nameText, 2);

                var statusText = new TextBlock
                {
                    Text = "",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Visibility = Visibility.Collapsed,
                    Opacity = 1,
                    MinWidth = 80,
                    TextAlignment = TextAlignment.Right
                };
                statusText.Foreground = FallbackSuccessBrush;
                Grid.SetColumn(statusText, 3);
                _statusTexts.Add(statusText);

                var installingIndicator = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                    Visibility = Visibility.Collapsed,
                    Background = FallbackWarningBrush
                };
                Grid.SetColumn(installingIndicator, 4);

                grid.Children.Add(checkBox);
                grid.Children.Add(iconImage);
                grid.Children.Add(nameText);
                grid.Children.Add(statusText);
                grid.Children.Add(installingIndicator);
                card.Child = grid;

                return card;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CreateAppCard error: {ex.Message}");
                return null;
            }
        }

        private void ShowEmptyMessage()
        {
            try
            {
                Column1Container.Items.Clear();
                var messageBorder = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(8),
                    Padding = new Thickness(12),
                    BorderThickness = new Thickness(1)
                };
                messageBorder.SetResourceReference(Border.BackgroundProperty, "DynamicCardBg");
                messageBorder.SetResourceReference(Border.BorderBrushProperty, "DynamicBorderBrush");
                var stackPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var titleBlock = new TextBlock
                {
                    Text = GetLocalizedString("NoProgramsFound", "No Programs Found"),
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 50, 0, 10),
                    TextAlignment = TextAlignment.Center
                };
                titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "DynamicAccent");
                var pathBlock = new TextBlock
                {
                    Text = $"{GetLocalizedString("AddProgramFoldersTo", "Add program folders to:")}\n{_rootPath}",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 50)
                };
                pathBlock.SetResourceReference(TextBlock.ForegroundProperty, "DynamicSubText");
                stackPanel.Children.Add(titleBlock); stackPanel.Children.Add(pathBlock);
                messageBorder.Child = stackPanel;
                Column1Container.Items.Add(messageBorder);
            }
            catch (Exception ex) { Debug.WriteLine($"ShowEmptyMessage error: {ex.Message}"); }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ CATEGORY TABS SYSTEM
        // ═════════════════════════════════════════════════════════════════
        #region Category Tabs
        private void BuildCategoryTabs(List<string> categoryNames)
        {
            CategoryTabsPanel.Children.Clear();

            var allBtn = new Button
            {
                Style = (Style)FindResource("CategoryTab"),
                Content = GetLocalizedString("AllCategories", "All"),
                Tag = "Active"
            };
            allBtn.Click += (s, e) => SelectCategoryTab(allBtn, "All");
            CategoryTabsPanel.Children.Add(allBtn);

            foreach (var cat in categoryNames)
            {
                // نحتاج إلى الـ folderName الأصلي للحصول على الأيقونة واللون
                string folderName = GetFolderNameFromDisplayName(cat);
                var (icon, color) = GetCategoryIcon(folderName);

                var btn = new Button { Style = (Style)FindResource("CategoryTab") };
                var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };
                var iconTb = new TextBlock
                {
                    Text = icon,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13,
                    Foreground = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                var nameTb = new TextBlock
                {
                    Text = cat,
                    VerticalAlignment = VerticalAlignment.Center
                };
                stackPanel.Children.Add(iconTb);
                stackPanel.Children.Add(nameTb);
                btn.Content = stackPanel;
                string capturedCat = cat;
                btn.Click += (s, e) => SelectCategoryTab(btn, capturedCat);
                CategoryTabsPanel.Children.Add(btn);
            }
            _currentCategory = GetLocalizedString("AllCategories", "All");
        }
        private string GetFolderNameFromDisplayName(string displayName)
        {
            // البحث عن الـ folderName الأصلي من الـ Dictionary
            foreach (var group in _categoryGroups)
            {
                if (group.CategoryName == displayName)
                {
                    // الـ Tag في الـ WrapPanel يحتوي على الـ folderName الأصلي
                    if (group.Wrap?.Tag is string folderName)
                        return folderName;
                }
            }

            // Fallback: تحويل الاسم المعروض إلى اسم فولدر
            return displayName switch
            {
                "Compression" or "ضغط" => "Compression",
                "Design" or "تصميم" => "Design",
                "Drivers" or "برامج تشغيل" => "Drivers",
                "Gaming" or "ألعاب" => "Gaming",
                "Internet" or "إنترنت" => "Internet",
                "Media" or "وسائط" => "Media",
                "Office" or "أوفيس" => "Office",
                "PDF" => "PDF",
                "Security" or "أمان" => "Security",
                "System" or "نظام" => "System",
                "Tools" or "أدوات" => "Tools",
                "Utilities" or "مساعدات" => "Utilities",
                _ => displayName
            };
        }

        private void SelectCategoryTab(Button activeBtn, string categoryName)
        {
            if (_isTabsDragging) return;
            foreach (UIElement child in CategoryTabsPanel.Children)
                if (child is Button b) b.Tag = null;
            activeBtn.Tag = "Active";
            _currentCategory = categoryName;
            FilterByCategory(categoryName);
        }

        private void FilterByCategory(string categoryName)
        {
            bool showAll = (categoryName == "All");
            foreach (var (catName, header, wrap, cards) in _categoryGroups)
            {
                bool visible = showAll || (catName == categoryName);
                header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                wrap.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!showAll && visible) header.Visibility = Visibility.Collapsed;
            }
        }

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

            System.Windows.Point current = e.GetPosition(sv);
            double deltaX = current.X - _tabsDragStartPoint.X;

            if (!_isTabsDragging && Math.Abs(deltaX) > 5)
            {
                _isTabsDragging = true;
                sv.CaptureMouse();
                sv.Cursor = Cursors.SizeAll;
            }

            if (_isTabsDragging)
                sv.ScrollToHorizontalOffset(_tabsScrollStartOffset - deltaX);
        }

        private void CategoryTabsScroll_MouseUp(object sender, MouseEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;

            if (_isTabsDragging)
            {
                sv.ReleaseMouseCapture();
                sv.Cursor = Cursors.Hand;
                if (e is MouseButtonEventArgs mbe) mbe.Handled = true;
            }
            _tabsMouseDown = false;
            _isTabsDragging = false;
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ RESPONSIVE CARDS WIDTH
        // ═════════════════════════════════════════════════════════════════
        #region Responsive Cards
        private void AppsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_resizeDebouncer == null)
            {
                _resizeDebouncer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _resizeDebouncer.Tick += (_, __) =>
                {
                    _resizeDebouncer.Stop();
                    UpdateCardsWidth();
                };
            }
            _resizeDebouncer.Stop();
            _resizeDebouncer.Start();
        }

        private void UpdateCardsWidth()
        {
            double available = AppsScrollViewer?.ActualWidth ?? 0;
            if (available <= 0) return;
            int cols = available >= 1100 ? 3 : available >= 720 ? 2 : 1;
            double usable = available - 40;
            double cardWidth = (usable / cols) - 14;
            if (cardWidth < 220) cardWidth = 220;
            foreach (var (_, _, wrap, cards) in _categoryGroups)
                foreach (var card in cards)
                    card.Width = cardWidth;
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ SELECTION & UI STATE
        // ═════════════════════════════════════════════════════════════════
        #region Selection Management
        private void UpdateSelection()
        {
            int selected = _allCheckBoxes.Count(c => c.IsChecked == true && c.IsEnabled);
            lblTotalAppsCount.Text = selected.ToString();
            UpdateSelectAllButtonText();
            UpdateStatusText();
            if (_isInstallationDone && selected > 0 && !_isInstalling && !_isPaused) SetButtonToStart();
            if (_isPaused && !_isInstalling) RefreshSelectedAppsList();
        }

        private void RefreshSelectedAppsList()
        {
            var newSelectedPaths = new List<string>();
            var newSelectedNames = new List<string>();
            var selectedCheckBoxes = _allCheckBoxes.Where(c => c.IsChecked == true && c.IsEnabled).ToList();
            foreach (var chk in selectedCheckBoxes)
            {
                newSelectedPaths.Add(chk.Tag.ToString());
                var parentGrid = chk.Parent as Grid;
                if (parentGrid != null)
                {
                    foreach (var child in parentGrid.Children)
                    {
                        if (child is TextBlock tb && Grid.GetColumn(tb) == 2)
                        {
                            newSelectedNames.Add(tb.Text);
                            break;
                        }
                    }
                }
            }
            var completedPaths = new List<string>();
            var completedNames = new List<string>();
            for (int i = 0; i < _completedApps && i < _selectedAppsPaths.Count; i++)
            {
                completedPaths.Add(_selectedAppsPaths[i]);
                completedNames.Add(_selectedAppsNames[i]);
            }
            _selectedAppsPaths.Clear(); _selectedAppsNames.Clear();
            foreach (var path in completedPaths) _selectedAppsPaths.Add(path);
            foreach (var name in completedNames) _selectedAppsNames.Add(name);
            foreach (var path in newSelectedPaths)
            {
                if (!_selectedAppsPaths.Contains(path))
                {
                    int index = newSelectedPaths.IndexOf(path);
                    _selectedAppsPaths.Add(path);
                    if (index < newSelectedNames.Count) _selectedAppsNames.Add(newSelectedNames[index]);
                }
            }
            if (_selectedAppsPaths.Count == _completedApps || _selectedAppsPaths.Count == 0)
                _currentInstallIndex = _selectedAppsPaths.Count;
            else
                _currentInstallIndex = Math.Max(_completedApps, Math.Min(_currentInstallIndex, _selectedAppsPaths.Count));
        }

        private void UpdateSelectAllButtonText()
        {
            if (btnSelectAll == null) return;
            int totalEnabled = _allCheckBoxes.Count(c => c.IsEnabled);
            int selected = _allCheckBoxes.Count(c => c.IsChecked == true && c.IsEnabled);
            btnSelectAll.Content = (totalEnabled > 0 && selected == totalEnabled)
                ? GetLocalizedString("DeselectAll", "Deselect All")
                : GetLocalizedString("SelectAll", "Select All");
        }

        private void UpdateStatusText()
        {
            try
            {
                int selected = _allCheckBoxes.Count(c => c.IsChecked == true && c.IsEnabled);
                if (_isInstalling)
                {
                    _blinkTimer?.Stop();
                    if (lblStatusText != null)
                    {
                        lblStatusText.Visibility = Visibility.Visible;
                        lblStatusText.Text = GetLocalizedString("InstallingBtn", "Installing...");
                        lblStatusText.Foreground = GetBrushSafe("Warning", FallbackWarningBrush);
                    }
                    if (statusIconText != null)
                    {
                        statusIconText.Text = "\uE711";
                        statusIconText.Visibility = Visibility.Visible;
                        statusIconText.Foreground = GetBrushSafe("Warning", FallbackWarningBrush);
                    }
                }
                else if (_isPaused)
                {
                    var warningBrush = GetBrushSafe("Warning", FallbackWarningBrush);
                    if (lblStatusText != null) { lblStatusText.Text = GetLocalizedString("Paused", "Stopped"); lblStatusText.Foreground = warningBrush; }
                    if (statusIconText != null) { statusIconText.Text = "\uE769"; statusIconText.Foreground = warningBrush; }
                    _blinkTimer?.Start();
                }
                else if (_isInstallationDone && _completedApps > 0 && _completedApps == _selectedAppsPaths.Count && _selectedAppsPaths.Count > 0)
                {
                    var successBrush = GetBrushSafe("Success", FallbackSuccessBrush);
                    if (lblStatusText != null) { lblStatusText.Text = GetLocalizedString("InstallationCompleteTitle", "Installation Complete!"); lblStatusText.Foreground = successBrush; }
                    if (statusIconText != null) { statusIconText.Text = "\uE73E"; statusIconText.Foreground = successBrush; }
                    _blinkTimer?.Start();
                }
                else if (selected == 0)
                {
                    _blinkTimer?.Stop();
                    if (lblStatusText != null)
                    {
                        lblStatusText.Visibility = Visibility.Visible;
                        lblStatusText.Text = GetLocalizedString("ReadyToStart", "Ready to start");
                        lblStatusText.Foreground = GetBrushSafe("DynamicMainText", FallbackMainTextBrush);
                    }
                    if (statusIconText != null)
                    {
                        statusIconText.Text = "\uE81E";
                        statusIconText.Foreground = GetBrushSafe("DynamicAccent", FallbackAccentBrush);
                        statusIconText.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    var successBrush = GetBrushSafe("Success", FallbackSuccessBrush);
                    _blinkTimer?.Stop();
                    if (lblStatusText != null)
                    {
                        lblStatusText.Visibility = Visibility.Visible;
                        lblStatusText.Text = $"{selected} {GetLocalizedString("AppsSelected", "apps selected")}";
                        lblStatusText.Foreground = successBrush;
                    }
                    if (statusIconText != null)
                    {
                        statusIconText.Text = "\uE7BA";
                        statusIconText.Foreground = successBrush;
                        statusIconText.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"UpdateStatusText error: {ex.Message}"); }
        }

        private void UpdateCategoryColors()
        {
            foreach (var (_, header, wrap, cards) in _categoryGroups)
            {
                if (header != null)
                {
                    var panel = header.Child as StackPanel;
                    if (panel?.Children.Count > 1 && panel.Children[1] is TextBlock headerText)
                        headerText.SetResourceReference(TextBlock.ForegroundProperty, "DynamicMainText");
                }
                foreach (var card in cards)
                {
                    var grid = card.Child as Grid;
                    if (grid == null) continue;
                    foreach (var child in grid.Children)
                    {
                        if (child is TextBlock tb)
                        {
                            if (Grid.GetColumn(tb) == 2)
                                tb.SetResourceReference(TextBlock.ForegroundProperty, "DynamicMainText");
                            else if (Grid.GetColumn(tb) == 3)
                                tb.Foreground = FallbackSuccessBrush;
                        }
                    }
                }
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ BUTTON STATE MANAGEMENT
        // ═════════════════════════════════════════════════════════════════
        #region Button State Management
        private void SetButtonToStart()
        {
            _isInstallationDone = false;
            try
            {
                btnStart.Content = GetLocalizedString("StartInstallation", "Start Installation");
                btnStart.Tag = "\uE768";
                var style = GetStyleSafe("StartBtn");
                if (style != null) btnStart.Style = style;
            }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToStart error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void SetButtonToStop()
        {
            _isInstallationDone = false;
            try
            {
                btnStart.Content = GetLocalizedString("StopInstallation", "Stop Installation");
                btnStart.Tag = "\uE103";
                var style = GetStyleSafe("StopBtn");
                if (style != null) btnStart.Style = style;
            }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToStop error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void SetButtonToResume()
        {
            _isInstallationDone = false;
            try
            {
                btnStart.Content = GetLocalizedString("ResumeInstallation", "Resume Installation");
                btnStart.Tag = "\uE768";
                var style = GetStyleSafe("ResumeBtn");
                if (style != null) btnStart.Style = style;
            }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToResume error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void SetButtonToDone()
        {
            _isInstallationDone = true;
            try
            {
                btnStart.Content = GetLocalizedString("InstallationCompleteTitle", "Installation Complete!");
                btnStart.Tag = "\uE73E";
                var style = GetStyleSafe("DoneBtn");
                if (style != null) btnStart.Style = style;
            }
            catch (Exception ex) { Debug.WriteLine($"SetButtonToDone error: {ex.Message}"); }
            btnStart.IsEnabled = true; btnStart.Opacity = 1;
        }

        private void RefreshMainButtonText()
        {
            if (_isInstalling) SetButtonToStop();
            else if (_isPaused) SetButtonToResume();
            else if (_isInstallationDone) SetButtonToDone();
            else SetButtonToStart();
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ INSTALLATION UI HELPERS
        // ═════════════════════════════════════════════════════════════════
        #region Installation UI Helpers
        private void StartPulsingAnimation(Border indicator)
        {
            if (indicator == null) return;
            StopPulsingAnimation(indicator);
            indicator.Visibility = Visibility.Visible; indicator.Opacity = 1;
            var storyboard = new Storyboard();
            var opacityAnim = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(500))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(opacityAnim, indicator);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));
            storyboard.Children.Add(opacityAnim);
            indicator.Resources[PULSE_STORYBOARD_KEY] = storyboard;
            storyboard.Begin();
        }

        private void StopPulsingAnimation(Border indicator)
        {
            if (indicator == null) return;
            if (indicator.Resources.Contains(PULSE_STORYBOARD_KEY))
            {
                if (indicator.Resources[PULSE_STORYBOARD_KEY] is Storyboard sb) sb.Stop();
                indicator.Resources.Remove(PULSE_STORYBOARD_KEY);
            }
            indicator.Opacity = 1; indicator.Visibility = Visibility.Collapsed;
        }

        private void ShowInstallingStatus(TextBlock statusText, Border indicator, Border card)
        {
            Dispatcher.Invoke(() =>
            {
                var warningBrush = GetBrushSafe("Warning", FallbackWarningBrush);
                if (statusText != null)
                {
                    statusText.BeginAnimation(UIElement.OpacityProperty, null);
                    statusText.Text = GetLocalizedString("Installing", "Installing...");
                    statusText.Foreground = warningBrush;
                    statusText.Visibility = Visibility.Visible; statusText.Opacity = 1;
                }
                if (indicator != null)
                {
                    indicator.Background = warningBrush; indicator.Visibility = Visibility.Visible;
                    StartPulsingAnimation(indicator);
                }
                if (card != null) { card.BorderBrush = warningBrush; card.BorderThickness = new Thickness(2); card.Cursor = Cursors.Hand; }
            }, DispatcherPriority.Send);
        }

        private void ShowDoneStatus(TextBlock statusText, Border indicator, Border card, CheckBox checkBox)
        {
            Dispatcher.Invoke(() =>
            {
                var successBrush = GetBrushSafe("Success", FallbackSuccessBrush);
                if (card != null) { card.SetResourceReference(Border.BorderBrushProperty, "DynamicBorderBrush"); card.BorderThickness = new Thickness(1); card.Cursor = Cursors.Arrow; }
                if (checkBox != null) { checkBox.IsEnabled = false; checkBox.IsChecked = true; checkBox.Cursor = Cursors.Arrow; }
                if (statusText != null)
                {
                    statusText.BeginAnimation(UIElement.OpacityProperty, null);
                    statusText.Text = GetLocalizedString("Installed", "Done ✔️");
                    statusText.Foreground = successBrush;
                    statusText.Visibility = Visibility.Visible; statusText.Opacity = 1;
                }
                if (indicator != null) { StopPulsingAnimation(indicator); indicator.Visibility = Visibility.Collapsed; }
            }, DispatcherPriority.Send);
        }

        private void ShowCancelledStatus(TextBlock statusText, Border indicator, Border card)
        {
            Dispatcher.Invoke(() =>
            {
                var cancelBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69));
                if (card != null) { card.SetResourceReference(Border.BorderBrushProperty, "DynamicBorderBrush"); card.BorderThickness = new Thickness(1); card.Cursor = Cursors.Hand; }
                if (indicator != null) { StopPulsingAnimation(indicator); indicator.Visibility = Visibility.Collapsed; }
                if (statusText != null)
                {
                    statusText.BeginAnimation(UIElement.OpacityProperty, null);
                    statusText.Text = GetLocalizedString("InstallationCancelled", "Cancelled 🚫");
                    statusText.Foreground = cancelBrush;
                    statusText.Visibility = Visibility.Visible; statusText.Opacity = 1;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    statusText.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    hideTimer.Tick += (ts, te) =>
                    {
                        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
                        fadeOut.Completed += (cs, ce) =>
                        {
                            if (statusText.Text == GetLocalizedString("InstallationCancelled", "Cancelled 🚫"))
                            {
                                statusText.Text = ""; statusText.Opacity = 1; statusText.Visibility = Visibility.Collapsed;
                            }
                        };
                        statusText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                        hideTimer.Stop();
                    };
                    hideTimer.Start();
                }
            }, DispatcherPriority.Send);
        }

        private void MarkCurrentInstallingAsCancelled()
        {
            if (_currentInstallingCard == null) return;
            var grid = _currentInstallingCard.Child as Grid;
            if (grid == null) return;
            var checkBox = grid.Children.OfType<CheckBox>().FirstOrDefault();
            if (checkBox == null) return;
            var statusText = GetStatusTextFromCheckBox(checkBox);
            var indicator = GetInstallingIndicatorFromCheckBox(checkBox);
            var card = _currentInstallingCard;
            ShowCancelledStatus(statusText, indicator, card);
            if (checkBox != null) { checkBox.IsEnabled = true; checkBox.IsChecked = false; checkBox.Cursor = Cursors.Hand; }
            string cancelledPath = null;
            if (_currentInstallIndex < _selectedAppsPaths.Count)
            {
                cancelledPath = _selectedAppsPaths[_currentInstallIndex];
                int index = _selectedAppsPaths.IndexOf(cancelledPath);
                if (index != -1) { _selectedAppsPaths.RemoveAt(index); _selectedAppsNames.RemoveAt(index); if (_currentInstallIndex > 0) _currentInstallIndex--; }
            }
            if (cancelledPath != null) _cancelledApps.Add(cancelledPath);
            if (_isInstalling && !_isPaused) _cts?.Cancel();
            _currentInstallingCard = null; _currentInstallingStatus = null; _currentInstallingIndicator = null;
            UpdateSelection(); UpdateStatusText();
        }

        private void ShowQuickStatus(CheckBox checkBox, string statusKey, string colorResource, string emoji)
        {
            var statusBlock = GetStatusTextFromCheckBox(checkBox);
            if (statusBlock == null) return;
            if (_statusTimers.TryGetValue(checkBox, out var existingTimer)) { existingTimer.Stop(); _statusTimers.Remove(checkBox); }
            statusBlock.BeginAnimation(UIElement.OpacityProperty, null);
            var colorBrush = GetBrushSafe(colorResource, FallbackAccentBrush);
            string statusText = GetLocalizedString(statusKey, statusKey);
            statusBlock.Opacity = 1; statusBlock.Visibility = Visibility.Visible;
            statusBlock.Text = $"{statusText} {emoji}"; statusBlock.Foreground = colorBrush;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            statusBlock.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            string capturedText = $"{statusText} {emoji}";
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimers[checkBox] = hideTimer;
            hideTimer.Tick += (ts, te) =>
            {
                hideTimer.Stop(); _statusTimers.Remove(checkBox);
                if (statusBlock.Text != capturedText) return;
                statusBlock.BeginAnimation(UIElement.OpacityProperty, null);
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                fadeOut.Completed += (cs, ce) =>
                {
                    if (statusBlock.Text == capturedText)
                    {
                        statusBlock.Text = ""; statusBlock.Opacity = 1; statusBlock.Visibility = Visibility.Collapsed;
                    }
                };
                statusBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            hideTimer.Start();
        }

        private Border GetCardFromCheckBox(CheckBox checkBox) => (checkBox.Parent as Grid)?.Parent as Border;

        private Border GetInstallingIndicatorFromCheckBox(CheckBox checkBox)
        {
            var parentGrid = checkBox.Parent as Grid;
            if (parentGrid != null)
                foreach (var child in parentGrid.Children)
                    if (child is Border indicator && Grid.GetColumn(indicator) == 4) return indicator;
            return null;
        }

        private TextBlock GetStatusTextFromCheckBox(CheckBox checkBox)
        {
            if (checkBox == null) return null;
            var parentGrid = checkBox.Parent as Grid;
            if (parentGrid != null)
                foreach (var child in parentGrid.Children)
                    if (child is TextBlock tb && Grid.GetColumn(tb) == 3) return tb;
            return null;
        }

        public async Task<bool> RequestSafeExitAsync()
        {
            if (!_isInstalling) return true;

            _isPaused = true;
            await Task.Delay(100);

            var result = await ModernMessageBox.Show(this,
                GetLocalizedString("ExitInstallationTitle", "Exit Installation"),
                GetLocalizedString("AreYouSureExit", "Installation is in progress. Are you sure you want to exit?"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _cts?.Cancel();
                _timer?.Stop();
                _blinkTimer?.Stop();
                _isInstalling = false;
                return true;
            }
            else
            {
                _isPaused = false;
                return false;
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ INSTALLATION WORKFLOW
        // ═════════════════════════════════════════════════════════════════
        #region Installation Workflow
        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstallationDone) { SetButtonToStart(); UpdateStatusText(); return; }
            if (_isInstalling)
            {
                if (_completedApps == _selectedAppsPaths.Count && _selectedAppsPaths.Count > 0) { SetButtonToStart(); return; }
                _cts?.Cancel(); _timer?.Stop();
                var result = await ModernMessageBox.Show(this,
                    GetLocalizedString("StopInstallationTitle", "Stop Installation"),
                    GetLocalizedString("AreYouSureStop", "Are you sure you want to stop the installation?"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _isInstalling = false; _isPaused = true; SetButtonToResume(); UpdateStatusText(); _blinkTimer?.Start();
                }
                else
                {
                    _cts = new CancellationTokenSource(); _isPaused = false; _isInstalling = true; _timer?.Start();
                    SetButtonToStop(); UpdateStatusText(); _blinkTimer?.Stop();
                    if (lblStatusText != null) lblStatusText.Visibility = Visibility.Visible;
                    await ContinueInstallation();
                }
                return;
            }
            if (_isPaused)
            {
                RefreshSelectedAppsList();
                if (_selectedAppsPaths.Count == 0 || _completedApps >= _selectedAppsPaths.Count)
                {
                    CleanupCurrentInstallingUI(); _isInstalling = false; _isPaused = false; _timer?.Stop();
                    var animation = new DoubleAnimation(100, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    installProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
                    if (_completedApps > 0) { SetButtonToDone(); UpdateStatusText(); await ModernMessageBox.Show(this, GetLocalizedString("InstallationSuccessTitle", "Installation Complete"), GetLocalizedString("AllApplicationsInstalled", "All selected programs have been installed successfully!"), MessageBoxButton.OK, MessageBoxImage.Information); }
                    else { SetButtonToStart(); UpdateStatusText(); }
                    return;
                }
                _isPaused = false; _isInstalling = true; _cts = new CancellationTokenSource(); _timer?.Start();
                SetButtonToStop(); UpdateStatusText(); _blinkTimer?.Stop();
                if (lblStatusText != null) lblStatusText.Visibility = Visibility.Visible;
                await ContinueInstallation();
                return;
            }
            var selected = _allCheckBoxes.Where(c => c.IsChecked == true && c.IsEnabled).ToList();
            if (selected.Count == 0)
            {
                await ModernMessageBox.Show(this, GetLocalizedString("NoSelectionTitle", "No Selection"),
                    GetLocalizedString("PleaseSelectPrograms", "Please select at least one program to install!"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _cts = new CancellationTokenSource(); _isInstalling = true; _isPaused = false; _isInstallationDone = false;
            _completedApps = 0; _currentInstallIndex = 0; _timer?.Start();
            _seconds = _minutes = _hours = 0; SetButtonToStop(); installProgressBar.Value = 0;
            UpdateStatusText(); _blinkTimer?.Stop();
            if (lblStatusText != null) lblStatusText.Visibility = Visibility.Visible;
            PrepareSelectedApps(); await StartInstallation();
        }

        private async Task StartInstallation()
        {
            try
            {
                App.TrayManager?.UpdateTrayIconBusyState(true);

                for (_currentInstallIndex = 0; _currentInstallIndex < _selectedAppsPaths.Count; _currentInstallIndex++)
                {
                    while (_isPaused && !_cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(150, _cts.Token);
                    }
                    if (_cts.Token.IsCancellationRequested) break;

                    if (_isExitPending && _exitDecisionTcs != null)
                    {
                        bool shouldExit = await _exitDecisionTcs.Task;
                        if (shouldExit) { _cts?.Cancel(); return; }
                        _isExitPending = false;
                    }
                    if (_cts.Token.IsCancellationRequested) break;

                    string exePath = _selectedAppsPaths[_currentInstallIndex];
                    string appName = _selectedAppsNames[_currentInstallIndex];

                    Border card = null; TextBlock statusText = null; CheckBox checkBox = null; Border installingIndicator = null;

                    foreach (var chk in _allCheckBoxes)
                    {
                        if (chk.Tag.ToString() != exePath) continue;
                        checkBox = chk;
                        var parentGrid = chk.Parent as Grid;
                        if (parentGrid != null)
                        {
                            foreach (var child in parentGrid.Children)
                            {
                                if (child is TextBlock tb && Grid.GetColumn(tb) == 3) statusText = tb;
                                if (child is Border ind && Grid.GetColumn(ind) == 4) installingIndicator = ind;
                            }
                            card = parentGrid.Parent as Border;
                        }
                        break;
                    }

                    _currentInstallingCard = card; _currentInstallingStatus = statusText; _currentInstallingIndicator = installingIndicator;

                    if (lblStatusText != null) { lblStatusText.Text = $"{GetLocalizedString("InstallingApplication", "Installing")}: {appName}"; lblStatusText.UpdateLayout(); }
                    if (statusIconText != null) { statusIconText.Text = "\uE711"; statusIconText.UpdateLayout(); }

                    ShowInstallingStatus(statusText, installingIndicator, card);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                    try
                    {
                        await Task.Run(() => { using (var process = Process.Start(exePath)) process?.WaitForExit(); }, _cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Debug.WriteLine($"Install error: {ex.Message}"); }

                    if (_cts.Token.IsCancellationRequested) break;

                    ShowDoneStatus(statusText, installingIndicator, card, checkBox);
                    _currentInstallingCard = null; _currentInstallingStatus = null; _currentInstallingIndicator = null;

                    _completedApps++;
                    double percent = (_completedApps * 100.0) / _selectedAppsPaths.Count;
                    var animation = new DoubleAnimation(percent, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    installProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
                    UpdateSelection();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Installation error: {ex.Message}"); }
            finally
            {
                if (!_isNavigatingHome)
                {
                    if (!_cts.Token.IsCancellationRequested && _completedApps == _selectedAppsPaths.Count)
                    {
                        _isInstalling = false; _isPaused = false; _timer.Stop(); SetButtonToDone(); installProgressBar.Value = 100; UpdateStatusText();
                        await ModernMessageBox.Show(this, GetLocalizedString("InstallationSuccessTitle", "Installation Complete"), GetLocalizedString("AllApplicationsInstalled", "All selected programs have been installed successfully!"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (_cts.Token.IsCancellationRequested) { btnStart.IsEnabled = true; btnStart.Opacity = 1; }
                    App.TrayManager?.UpdateTrayIconBusyState(false);
                }
            }
        }

        private async Task ContinueInstallation()
        {
            try
            {
                App.TrayManager?.UpdateTrayIconBusyState(true);
                if (_selectedAppsPaths.Count == 0 || _completedApps >= _selectedAppsPaths.Count) return;

                for (; _currentInstallIndex < _selectedAppsPaths.Count; _currentInstallIndex++)
                {
                    while (_isPaused && !_cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(150, _cts.Token);
                    }
                    if (_cts.Token.IsCancellationRequested) break;

                    if (_isExitPending && _exitDecisionTcs != null)
                    {
                        bool shouldExit = await _exitDecisionTcs.Task;
                        if (shouldExit) { _cts?.Cancel(); return; }
                        _isExitPending = false;
                    }
                    if (_cts.Token.IsCancellationRequested) break;

                    string exePath = _selectedAppsPaths[_currentInstallIndex];
                    string appName = _selectedAppsNames[_currentInstallIndex];

                    Border card = null; TextBlock statusText = null; CheckBox checkBox = null; Border installingIndicator = null;

                    foreach (var chk in _allCheckBoxes)
                    {
                        if (chk.Tag.ToString() != exePath) continue;
                        checkBox = chk;
                        var parentGrid = chk.Parent as Grid;
                        if (parentGrid != null)
                        {
                            foreach (var child in parentGrid.Children)
                            {
                                if (child is TextBlock tb && Grid.GetColumn(tb) == 3) statusText = tb;
                                if (child is Border ind && Grid.GetColumn(ind) == 4) installingIndicator = ind;
                            }
                            card = parentGrid.Parent as Border;
                        }
                        break;
                    }

                    _currentInstallingCard = card; _currentInstallingStatus = statusText; _currentInstallingIndicator = installingIndicator;

                    if (lblStatusText != null) { lblStatusText.Text = $"{GetLocalizedString("InstallingApplication", "Installing")}: {appName}"; lblStatusText.UpdateLayout(); }
                    if (statusIconText != null) { statusIconText.Text = "\uE711"; statusIconText.UpdateLayout(); }

                    ShowInstallingStatus(statusText, installingIndicator, card);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                    try
                    {
                        await Task.Run(() => { using (var process = Process.Start(exePath)) process?.WaitForExit(); }, _cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Debug.WriteLine($"Install error: {ex.Message}"); }

                    if (_cts.Token.IsCancellationRequested) break;

                    ShowDoneStatus(statusText, installingIndicator, card, checkBox);
                    _currentInstallingCard = null; _currentInstallingStatus = null; _currentInstallingIndicator = null;

                    _completedApps++;
                    double percent = (_completedApps * 100.0) / _selectedAppsPaths.Count;
                    var animation = new DoubleAnimation(percent, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    installProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
                    UpdateSelection();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"ContinueInstallation error: {ex.Message}"); }
            finally
            {
                if (!_isNavigatingHome)
                {
                    if (!_cts.Token.IsCancellationRequested && (_completedApps >= _selectedAppsPaths.Count || _selectedAppsPaths.Count == 0))
                    {
                        CleanupCurrentInstallingUI(); _isInstalling = false; _isPaused = false; _timer.Stop();
                        var animation = new DoubleAnimation(100, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                        installProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
                        if (_completedApps > 0 && _selectedAppsPaths.Count > 0)
                        {
                            SetButtonToDone(); UpdateStatusText();
                            await ModernMessageBox.Show(this, GetLocalizedString("InstallationSuccessTitle", "Installation Complete"), GetLocalizedString("AllApplicationsInstalled", "All selected programs have been installed successfully!"), MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else { SetButtonToStart(); UpdateStatusText(); }
                    }
                    else if (_cts.Token.IsCancellationRequested) { btnStart.IsEnabled = true; btnStart.Opacity = 1; }
                    App.TrayManager?.UpdateTrayIconBusyState(false);
                }
            }
        }

        private void CleanupCurrentInstallingUI()
        {
            if (_currentInstallingCard != null) { _currentInstallingCard.SetResourceReference(Border.BorderBrushProperty, "DynamicBorderBrush"); _currentInstallingCard.BorderThickness = new Thickness(1); }
            if (_currentInstallingStatus != null) { _currentInstallingStatus.Text = ""; _currentInstallingStatus.Visibility = Visibility.Collapsed; }
            if (_currentInstallingIndicator != null) { StopPulsingAnimation(_currentInstallingIndicator); _currentInstallingIndicator.Visibility = Visibility.Collapsed; }
            _currentInstallingCard = null; _currentInstallingStatus = null; _currentInstallingIndicator = null;
        }

        private void PrepareSelectedApps()
        {
            _selectedAppsPaths.Clear();
            _selectedAppsNames.Clear();

            // ✅ ترتيب حسب SelectionOrder
            var selectedCheckBoxes = _allCheckBoxes
                .Where(c => c.IsChecked == true && c.IsEnabled)
                .Select(c => new
                {
                    CheckBox = c,
                    Path = c.Tag?.ToString() ?? "",
                    Order = _selectionOrders.TryGetValue(c.Tag?.ToString() ?? "", out var o) ? o : long.MaxValue
                })
                .OrderBy(x => x.Order)
                .ToList();

            foreach (var item in selectedCheckBoxes)
            {
                _selectedAppsPaths.Add(item.Path);
                var parentGrid = item.CheckBox.Parent as Grid;
                if (parentGrid != null)
                {
                    foreach (var child in parentGrid.Children)
                        if (child is TextBlock tb && Grid.GetColumn(tb) == 2)
                        {
                            _selectedAppsNames.Add(tb.Text);
                            break;
                        }
                }
            }
        }

        private void btnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            var enabledCheckBoxes = _allCheckBoxes.Where(c => c.IsEnabled).ToList();
            if (enabledCheckBoxes.Count == 0) return;
            bool allSelected = enabledCheckBoxes.All(c => c.IsChecked == true);

            _isUpdatingSelection = true; // تجنب الحدث المتكرر
            try
            {
                if (allSelected)
                {
                    _selectionOrders.Clear();
                    foreach (var chk in enabledCheckBoxes) chk.IsChecked = false;
                }
                else
                {
                    foreach (var chk in enabledCheckBoxes)
                    {
                        chk.IsChecked = true;
                        string p = chk.Tag?.ToString();
                        if (!string.IsNullOrEmpty(p) && !_selectionOrders.ContainsKey(p))
                            _selectionOrders[p] = ++_selectionCounter;
                    }
                }
            }
            finally { _isUpdatingSelection = false; }

            UpdateSelection();
            RefreshQueuePopupIfOpen();
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ QUEUE MANAGEMENT
        // ═════════════════════════════════════════════════════════════════
        #region Queue Management
        private void AddToInstallationQueue(string exePath, CheckBox checkBox)
        {
            lock (_selectionLock)
            {
                if (_isUpdatingSelection) return;
                _isUpdatingSelection = true;
                try
                {
                    if (_selectedAppsPaths.Contains(exePath)) return;
                    int indexInPaths = _selectedAppsPaths.IndexOf(exePath);
                    if (indexInPaths != -1 && _completedApps > indexInPaths) return;
                    string appName = GetAppNameFromCheckBox(checkBox);
                    int insertIndex = _currentInstallIndex;
                    _selectedAppsPaths.Insert(insertIndex, exePath);
                    _selectedAppsNames.Insert(insertIndex, appName);
                    _totalApps++; UpdateSelection();
                    Dispatcher.Invoke(() => { UpdateStatusText(); UpdateCategoryColors(); ShowQuickStatus(checkBox, "Queued", "DynamicAccent", "📋"); });
                }
                finally { _isUpdatingSelection = false; }
            }
        }

        private void RemoveFromInstallationQueue(string exePath, CheckBox checkBox)
        {
            lock (_selectionLock)
            {
                if (_isUpdatingSelection) return;
                _isUpdatingSelection = true;
                try
                {
                    int index = _selectedAppsPaths.IndexOf(exePath);
                    if (index != -1 && index == _currentInstallIndex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var statusText = GetStatusTextFromCheckBox(checkBox);
                            var installingIndicator = GetInstallingIndicatorFromCheckBox(checkBox);
                            var card = GetCardFromCheckBox(checkBox);
                            ShowCancelledStatus(statusText, installingIndicator, card);
                            if (checkBox != null) { checkBox.IsEnabled = false; checkBox.IsChecked = false; checkBox.Cursor = Cursors.Arrow; }
                        }, DispatcherPriority.Send);
                        if (index != -1) { _selectedAppsPaths.RemoveAt(index); _selectedAppsNames.RemoveAt(index); if (_currentInstallIndex > 0) _currentInstallIndex--; }
                        if (_isInstalling && !_isPaused) _cts?.Cancel();
                        return;
                    }
                    if (index == -1) { Dispatcher.Invoke(() => ShowQuickStatus(checkBox, "Removed", "Warning", "❌")); return; }
                    if (index < _completedApps) { Dispatcher.Invoke(() => ShowQuickStatus(checkBox, "Removed", "Warning", "❌")); return; }
                    if (index > _currentInstallIndex)
                    {
                        _selectedAppsPaths.RemoveAt(index); _selectedAppsNames.RemoveAt(index);
                        UpdateSelection();
                        Dispatcher.Invoke(() => { UpdateStatusText(); UpdateCategoryColors(); ShowQuickStatus(checkBox, "Removed", "Warning", "❌"); });
                        return;
                    }
                    if (index < _currentInstallIndex && index >= _completedApps)
                    {
                        _selectedAppsPaths.RemoveAt(index); _selectedAppsNames.RemoveAt(index);
                        _currentInstallIndex--; UpdateSelection();
                        Dispatcher.Invoke(() => { UpdateStatusText(); UpdateCategoryColors(); ShowQuickStatus(checkBox, "Removed", "Warning", "❌"); });
                        return;
                    }
                }
                finally { _isUpdatingSelection = false; }
            }
        }

        private Task AutoSyncQueue()
        {
            if (_isUpdatingSelection || !_isInstalling || _isPaused) return Task.CompletedTask;
            lock (_selectionLock)
            {
                _isUpdatingSelection = true;
                try
                {
                    var currentSelections = _allCheckBoxes.Where(c => c.IsChecked == true && c.IsEnabled).Select(c => c.Tag.ToString()).ToList();
                    var completed = _selectedAppsPaths.Take(_completedApps).ToList();
                    var newQueue = new List<string>(completed);
                    foreach (var selection in currentSelections)
                        if (!completed.Contains(selection) && !newQueue.Contains(selection)) newQueue.Add(selection);
                    bool changed = newQueue.Count != _selectedAppsPaths.Count;
                    if (!changed) for (int i = 0; i < newQueue.Count; i++) if (newQueue[i] != _selectedAppsPaths[i]) { changed = true; break; }
                    if (changed)
                    {
                        var newNames = new List<string>();
                        foreach (var path in newQueue)
                        {
                            var chk = _allCheckBoxes.FirstOrDefault(c => c.Tag.ToString() == path);
                            newNames.Add(chk != null ? GetAppNameFromCheckBox(chk) : "Unknown");
                        }
                        _selectedAppsPaths = newQueue; _selectedAppsNames = newNames;
                        _currentInstallIndex = Math.Max(_completedApps, Math.Min(_currentInstallIndex, _selectedAppsPaths.Count));
                        Dispatcher.Invoke(() => { UpdateSelection(); UpdateStatusText(); if (_selectedAppsPaths.Count > 0) AnimateProgress((_completedApps * 100.0) / _selectedAppsPaths.Count); });
                    }
                }
                finally { _isUpdatingSelection = false; }
            }
            return Task.CompletedTask;
        }

        private void AnimateProgress(double targetValue)
        {
            var animation = new DoubleAnimation(targetValue, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            installProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation);
        }

        private string GetAppNameFromCheckBox(CheckBox checkBox)
        {
            var parentGrid = checkBox.Parent as Grid;
            if (parentGrid != null)
                foreach (var child in parentGrid.Children)
                    if (child is TextBlock tb && Grid.GetColumn(tb) == 2) return tb.Text;
            return "Unknown";
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ QUICK MENU (Queue Popup)
        // ═════════════════════════════════════════════════════════════════
        #region Queue Popup
        private void btnQueueList_Click(object sender, RoutedEventArgs e)
        {
            if (_queueWindow != null && _queueWindow.IsLoaded)
            {
                _queueWindow.Activate();
                return;
            }
            OpenQueuePopup();
        }

        private void OpenQueuePopup()
        {
            var items = GetCurrentQueueItems();
            double parentOpacity = 1.0;
            if (Window.GetWindow(this) is Window pw) parentOpacity = pw.Opacity;

            _queueWindow = new Window
            {
                Width = 440,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 600,
                Opacity = 0
            };
            _queueWindow.Closed += (_, __) => { _queueWindow = null; _queueList = null; };

            var cardBg = GetBrushSafe("DynamicCardBg", new SolidColorBrush(System.Windows.Media.Colors.White));
            var borderBr = GetBrushSafe("DynamicBorderBrush", new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)));
            var mainText = GetBrushSafe("DynamicMainText", FallbackMainTextBrush);
            var subText = GetBrushSafe("DynamicSubText", FallbackSubTextBrush);
            var accent = GetBrushSafe("DynamicAccent", FallbackAccentBrush);
            var accentColor = ((SolidColorBrush)accent).Color;
            var hoverBg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));

            var root = new Border
            {
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1.5),
                BorderBrush = borderBr,
                Background = cardBg
            };
            var rootStack = new StackPanel();

            // Header
            var header = new Border
            {
                Padding = new Thickness(16, 14, 16, 14),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = borderBr
            };
            var hGrid = new Grid();
            hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hIcon = new TextBlock
            {
                Text = "\uE71D",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(hIcon, 0);

            var hTitleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            hTitleStack.Children.Add(new TextBlock
            {
                Text = GetLocalizedString("str_QueueListTitle", "Install Queue"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = mainText
            });
            var hSubTitle = new TextBlock
            {
                Text = $"{items.Count} {GetLocalizedString("AppsSelected", "apps selected")}",
                FontSize = 11,
                Foreground = subText,
                Margin = new Thickness(0, 2, 0, 0)
            };
            hTitleStack.Children.Add(hSubTitle);
            Grid.SetColumn(hTitleStack, 1);

            var closeBtn = MakeIconButton("\uE711", subText, hoverBg);
            closeBtn.Click += (_, __) => _queueWindow?.Close();
            Grid.SetColumn(closeBtn, 2);

            hGrid.Children.Add(hIcon);
            hGrid.Children.Add(hTitleStack);
            hGrid.Children.Add(closeBtn);
            header.Child = hGrid;
            rootStack.Children.Add(header);

            // List
            var scroll = new ScrollViewer
            {
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = System.Windows.Media.Brushes.Transparent
            };
            _queueList = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            BuildQueueRows(items, _queueList, hoverBg, accent, accentColor, mainText, subText);
            scroll.Content = _queueList;
            rootStack.Children.Add(scroll);

            // Footer
            var footer = new Border
            {
                Padding = new Thickness(16, 12, 16, 16),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = borderBr
            };
            var okBtn = new Button
            {
                Height = 40,
                Background = accent,
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Content = GetLocalizedString("OK", "OK"),
                Template = MakeRoundedTemplate(12)
            };
            okBtn.Click += (_, __) => _queueWindow?.Close();
            footer.Child = okBtn;
            rootStack.Children.Add(footer);

            root.Child = rootStack;
            _queueWindow.Content = root;

            // ✅ حقن ScrollBar Style
            try
            {
                var scrollBarStyle = this.TryFindResource(typeof(ScrollBar)) as Style;
                if (scrollBarStyle != null)
                    _queueWindow.Resources[typeof(ScrollBar)] = scrollBarStyle;

                if (this.TryFindResource("AccentGradient") != null)
                    _queueWindow.Resources["AccentGradient"] = this.FindResource("AccentGradient");
                else if (Application.Current?.Resources.Contains("AccentGradient") == true)
                    _queueWindow.Resources["AccentGradient"] = Application.Current.Resources["AccentGradient"];
            }
            catch (Exception ex) { Debug.WriteLine($"ScrollBar style inject: {ex.Message}"); }

            // ✅ نفس حدث سحب النافذة الموجود في OnlinePage بالضبط
            _queueWindow.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                if (_isDragging || _draggedRow != null) return;
                var src = ev.OriginalSource as DependencyObject;
                while (src != null && src != _queueWindow)
                {
                    if (src is Button) return;
                    if (src is Border bd && bd.Tag is string) return;
                    if (src is ScrollViewer || src is ScrollBar) return;
                    src = VisualTreeHelper.GetParent(src);
                }
                try { if (_queueWindow.WindowState == WindowState.Normal) _queueWindow.DragMove(); }
                catch { }
            };

            _queueWindow.Loaded += (_, __) =>
            {
                var fa = new DoubleAnimation(0, parentOpacity, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                _queueWindow.BeginAnimation(Window.OpacityProperty, fa);
            };

            _queueWindow.Show();
        }

        private void QueueRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border row) return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null && src != row)
            {
                if (src is Button) return; // مش نسحب لما نضغط على زر
                src = VisualTreeHelper.GetParent(src);
            }
            _dragStartPoint = e.GetPosition(_queueList);
            _draggedRow = row;
        }

        private void QueueRow_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedRow == null || e.LeftButton != MouseButtonState.Pressed) return;
            if (_isDragging) return;

            System.Windows.Point currentPos = e.GetPosition(_queueList);
            Vector diff = currentPos - _dragStartPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
                _draggedRow.Opacity = 0.5;
                try
                {
                    DragDrop.DoDragDrop(_draggedRow, _draggedRow, DragDropEffects.Move);
                }
                catch { }
                finally
                {
                    if (_draggedRow != null) _draggedRow.Opacity = 1.0;
                    _draggedRow = null;
                    _isDragging = false;
                }
            }
        }

        private void QueueRow_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(Border)) is Border source &&
                sender is Border target && source != target)
            {
                e.Effects = DragDropEffects.Move;
                target.Background = GetBrushSafe("DynamicHoverBg",
                    new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 14, 165, 233)));
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void QueueRow_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border target && target != _draggedRow)
                target.Background = System.Windows.Media.Brushes.Transparent;
        }

        private void QueueRow_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(Border)) is not Border source) return;
            if (sender is not Border target) return;
            if (source == target) return;

            string sourcePath = source.Tag as string;
            string targetPath = target.Tag as string;
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetPath)) return;

            // ✅ بدّل بين الترتيبين
            if (_selectionOrders.TryGetValue(sourcePath, out long sourceOrder) &&
                _selectionOrders.TryGetValue(targetPath, out long targetOrder))
            {
                _selectionOrders[sourcePath] = targetOrder;
                _selectionOrders[targetPath] = sourceOrder;
            }

            target.Background = System.Windows.Media.Brushes.Transparent;

            // ✅ تحديث الـ Queue الجاري لو الـ Install شغّال
            if (_isInstalling && !_isPaused)
            {
                Task.Run(async () => await AutoSyncQueue());
            }

            RefreshQueuePopupIfOpen();
            e.Handled = true;
        }

        private List<(CheckBox CheckBox, string AppName, string ExePath, bool IsInstalling, bool IsCompleted)> GetCurrentQueueItems()
        {
            var items = new List<(CheckBox, string, string, bool, bool)>();

            // 1️⃣ المكتمل أولاً
            for (int i = 0; i < _completedApps && i < _selectedAppsPaths.Count; i++)
            {
                var path = _selectedAppsPaths[i];
                var chk = _allCheckBoxes.FirstOrDefault(c => c.Tag?.ToString() == path);
                var name = i < _selectedAppsNames.Count ? _selectedAppsNames[i] : Path.GetFileNameWithoutExtension(path);
                items.Add((chk, name, path, false, true));
            }

            // 2️⃣ الجاري حالياً
            if (_isInstalling && _currentInstallIndex < _selectedAppsPaths.Count && _currentInstallIndex >= _completedApps)
            {
                var path = _selectedAppsPaths[_currentInstallIndex];
                var chk = _allCheckBoxes.FirstOrDefault(c => c.Tag?.ToString() == path);
                var name = _currentInstallIndex < _selectedAppsNames.Count
                    ? _selectedAppsNames[_currentInstallIndex]
                    : Path.GetFileNameWithoutExtension(path);
                items.Add((chk, name, path, true, false));
            }

            // 3️⃣ المنتظر — مرتب حسب SelectionOrder
            var pendingChecked = _allCheckBoxes
                .Where(c => c.IsChecked == true && c.IsEnabled)
                .Select(c => new
                {
                    CheckBox = c,
                    Path = c.Tag?.ToString() ?? "",
                    Order = _selectionOrders.TryGetValue(c.Tag?.ToString() ?? "", out var o) ? o : long.MaxValue
                })
                .OrderBy(x => x.Order)
                .ToList();

            foreach (var p in pendingChecked)
            {
                if (string.IsNullOrEmpty(p.Path)) continue;
                if (items.Any(it => it.Item3 == p.Path)) continue;
                items.Add((p.CheckBox, GetAppNameFromCheckBox(p.CheckBox), p.Path, false, false));
            }

            return items;
        }

        private void BuildQueueRows(
    List<(CheckBox CheckBox, string AppName, string ExePath, bool IsInstalling, bool IsCompleted)> items,
    StackPanel list,
    System.Windows.Media.Brush hoverBg,
    System.Windows.Media.Brush accent,
    System.Windows.Media.Color accentColor,
    System.Windows.Media.Brush mainText,
    System.Windows.Media.Brush subText)
        {
            list.Children.Clear();

            if (items.Count == 0)
            {
                var empty = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 30, 0, 30)
                };
                empty.Children.Add(new TextBlock
                {
                    Text = "\uE7BA",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 36,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 150, 150, 150)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                empty.Children.Add(new TextBlock
                {
                    Text = GetLocalizedString("str_QueueEmpty", "No apps selected"),
                    FontSize = 13,
                    Foreground = subText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                list.Children.Add(empty);
                return;
            }

            int order = 1;
            foreach (var item in items)
            {
                var capturedItem = item;
                bool isDraggable = !item.IsCompleted && !item.IsInstalling;

                var row = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Background = System.Windows.Media.Brushes.Transparent,
                    Opacity = item.IsCompleted ? 0.75 : 1.0,
                    Tag = item.ExePath,
                    AllowDrop = isDraggable
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // drag handle
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // badge
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // ✅ Drag Handle
                var dragHandle = new TextBlock
                {
                    Text = "\uE76F",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = subText,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = isDraggable ? Cursors.SizeAll : Cursors.Arrow,
                    Opacity = isDraggable ? 0.6 : 0.2
                };
                Grid.SetColumn(dragHandle, 0);

                // Badge
                var badge = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(7),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                badge.Child = new TextBlock
                {
                    Text = order.ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(badge, 1);
                order++;

                // ✅ الأيقونة من الكاش مباشرة (بدون تأخير!)
                var iconImg = new System.Windows.Controls.Image
                {
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);

                // 🚀 جرب الكاش الأول
                string customIconPath = GetCustomIconPath(item.ExePath);
                string cacheKey = File.Exists(customIconPath) ? $"{item.ExePath}|custom" : item.ExePath;
                if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
                {
                    iconImg.Source = cachedIcon;
                }
                else
                {
                    // لو مش في الكاش، حمّلها بدون انتظار
                    _ = LoadIconForImageAsync(iconImg, item.ExePath);
                }
                Grid.SetColumn(iconImg, 2);

                // Name
                var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                nameStack.Children.Add(new TextBlock
                {
                    Text = item.AppName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = mainText,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                nameStack.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(item.ExePath),
                    FontSize = 10,
                    Foreground = subText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
                Grid.SetColumn(nameStack, 3);

                // Right Element
                UIElement rightEl;
                if (item.IsCompleted)
                {
                    rightEl = new TextBlock
                    {
                        Text = "\uE73E",
                        FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                        FontSize = 16,
                        Foreground = FallbackSuccessBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = GetLocalizedString("Installed", "Done")
                    };
                }
                else if (item.IsInstalling)
                {
                    rightEl = new TextBlock
                    {
                        Text = "\uE895",
                        FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                        FontSize = 16,
                        Foreground = FallbackWarningBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = GetLocalizedString("Installing", "Installing...")
                    };
                }
                else
                {
                    var removeBtn = MakeIconButton("\uE711", subText, hoverBg, 24, 0.7);
                    removeBtn.ToolTip = GetLocalizedString("str_RemoveFromQueue", "Remove");
                    var capturedChk = item.CheckBox;
                    removeBtn.Click += (_, __) =>
                    {
                        if (capturedChk != null) capturedChk.IsChecked = false;
                    };
                    rightEl = removeBtn;
                }
                Grid.SetColumn(rightEl, 4);

                rowGrid.Children.Add(dragHandle);
                rowGrid.Children.Add(badge);
                rowGrid.Children.Add(iconImg);
                rowGrid.Children.Add(nameStack);
                rowGrid.Children.Add(rightEl);
                row.Child = rowGrid;

                if (!item.IsCompleted && !item.IsInstalling)
                {
                    row.MouseEnter += (_, __) => { if (_draggedRow != row) row.Background = hoverBg; };
                    row.MouseLeave += (_, __) => { if (_draggedRow != row) row.Background = System.Windows.Media.Brushes.Transparent; };
                }

                // ✅ Drag & Drop
                if (isDraggable)
                {
                    row.PreviewMouseLeftButtonDown += QueueRow_PreviewMouseLeftButtonDown;
                    row.PreviewMouseMove += QueueRow_PreviewMouseMove;
                    row.DragOver += QueueRow_DragOver;
                    row.DragLeave += QueueRow_DragLeave;
                    row.Drop += QueueRow_Drop;
                }

                list.Children.Add(row);
            }
        }

        private void RefreshQueuePopupIfOpen()
        {
            if (_queueWindow == null || !_queueWindow.IsLoaded || _queueList == null) return;

            var accent = GetBrushSafe("DynamicAccent", FallbackAccentBrush);
            var accentColor = ((SolidColorBrush)accent).Color;
            var hoverBg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));
            var mainText = GetBrushSafe("DynamicMainText", FallbackMainTextBrush);
            var subText = GetBrushSafe("DynamicSubText", FallbackSubTextBrush);

            var items = GetCurrentQueueItems();
            BuildQueueRows(items, _queueList, hoverBg, accent, accentColor, mainText, subText);
        }

        private static Button MakeIconButton(string icon, System.Windows.Media.Brush fg, System.Windows.Media.Brush hoverBg, double size = 28, double opacity = 1.0)
        {
            var btn = new Button
            {
                Width = size,
                Height = size,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Opacity = opacity
            };
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
            btn.MouseLeave += (_, __) => btn.Background = System.Windows.Media.Brushes.Transparent;
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
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ ICON CONTEXT MENU (Right-Click)
        // ═════════════════════════════════════════════════════════════════
        #region Icon Context Menu
        private void ShowIconContextMenu(System.Windows.Controls.Image iconImage, string exePath)
        {
            try
            {
                var contextMenu = BuildModernContextMenu(iconImage, exePath);
                contextMenu.PlacementTarget = iconImage;
                contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                contextMenu.IsOpen = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowIconContextMenu error: {ex.Message}");
            }
        }

        // ✅ BuildModernContextMenu مع إضافة Refresh Icon
        private ContextMenu BuildModernContextMenu(System.Windows.Controls.Image iconImage, string exePath)
        {
            var bgBrush = GetBrushSafe("DynamicCardBg", new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)));
            var borderBrush = GetBrushSafe("DynamicBorderBrush", new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)));
            var textBrush = GetBrushSafe("DynamicMainText", FallbackMainTextBrush);
            var subTextBrush = GetBrushSafe("DynamicSubText", FallbackSubTextBrush);
            var accentBrush = GetBrushSafe("DynamicAccent", FallbackAccentBrush);
            var hoverBrush = GetBrushSafe("DynamicHoverBg", new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 100, 100, 100)));

            var menu = new ContextMenu
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HasDropShadow = false,
                Foreground = textBrush,
                StaysOpen = false
            };

            var menuTemplate = new ControlTemplate(typeof(ContextMenu));
            var menuBorder = new FrameworkElementFactory(typeof(Border));
            menuBorder.SetValue(Border.BackgroundProperty, bgBrush);
            menuBorder.SetValue(Border.BorderBrushProperty, borderBrush);
            menuBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            menuBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            menuBorder.SetValue(Border.PaddingProperty, new Thickness(4));
            menuBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            menuBorder.AppendChild(itemsPresenter);
            menuTemplate.VisualTree = menuBorder;
            menu.Template = menuTemplate;

            bool hasCustom = File.Exists(GetCustomIconPath(exePath));

            // 1️⃣ Change Icon
            var changeItem = CreateModernMenuItem(
                "\uE70F",
                GetLocalizedString("ChangeIcon", "Change Icon"),
                textBrush, hoverBrush, accentBrush, true);
            changeItem.Click += async (s, e) => await ChangeIconAsync(iconImage, exePath);
            menu.Items.Add(changeItem);

            // 2️⃣ ✨ Refresh Icon (جديد)
            var refreshItem = CreateModernMenuItem(
                "\uE72C",  // ↻ Refresh icon
                GetLocalizedString("RefreshIcon", "Refresh Icon"),
                textBrush, hoverBrush, accentBrush, true);
            refreshItem.Click += async (s, e) => await ForceRefreshIconAsync(iconImage, exePath);
            menu.Items.Add(refreshItem);

            menu.Items.Add(CreateSeparator(borderBrush));

            // 3️⃣ Restore Original
            var resetItem = CreateModernMenuItem(
                "\uE777",  // ↶ Reset/Undo icon
                GetLocalizedString("RestoreOriginalIcon", "Restore Original"),
                hasCustom ? textBrush : subTextBrush,
                hoverBrush,
                hasCustom ? accentBrush : subTextBrush,
                hasCustom);
            if (hasCustom)
            {
                resetItem.Click += async (s, e) => await ResetIconAsync(iconImage, exePath);
            }
            menu.Items.Add(resetItem);

            return menu;
        }

        private Separator CreateSeparator(System.Windows.Media.Brush color)
        {
            var sep = new Separator
            {
                Margin = new Thickness(6, 4, 6, 4),
                Background = color,
                Height = 1,
                Opacity = 0.5
            };

            var template = new ControlTemplate(typeof(Separator));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.HeightProperty, 1.0);
            border.SetValue(Border.BackgroundProperty, color);
            border.SetValue(Border.MarginProperty, new Thickness(8, 4, 8, 4));
            template.VisualTree = border;
            sep.Template = template;

            return sep;
        }

        private MenuItem CreateModernMenuItem(
            string iconChar,
            string text,
            System.Windows.Media.Brush textBrush,
            System.Windows.Media.Brush hoverBrush,
            System.Windows.Media.Brush accentBrush,
            bool isEnabled)
        {
            var item = new MenuItem
            {
                Foreground = textBrush,
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = isEnabled ? Cursors.Hand : Cursors.Arrow,
                FontSize = 13,
                BorderThickness = new Thickness(0),
                IsEnabled = isEnabled,
                Opacity = isEnabled ? 1.0 : 0.55
            };

            var contentPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconBlock = new TextBlock
            {
                Text = iconChar,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Width = 18,
                TextAlignment = TextAlignment.Center
            };
            var textBlock = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Medium,
                Foreground = textBrush
            };
            contentPanel.Children.Add(iconBlock);
            contentPanel.Children.Add(textBlock);
            item.Header = contentPanel;

            var template = new ControlTemplate(typeof(MenuItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ItemBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 8, 16, 8));
            border.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
            border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.AppendChild(cp);
            template.VisualTree = border;

            if (isEnabled)
            {
                var hoverTrigger = new Trigger
                {
                    Property = UIElement.IsMouseOverProperty,
                    Value = true
                };
                hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "ItemBorder"));
                template.Triggers.Add(hoverTrigger);
            }

            item.Template = template;
            return item;
        }

        private async Task ChangeIconAsync(System.Windows.Controls.Image iconImage, string exePath)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = GetLocalizedString("SelectIconFile", "Select Icon File"),
                    Filter = "Image Files (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp|All Files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true) return;

                string sourcePath = dialog.FileName;

                bool success = await Task.Run(() =>
                {
                    try
                    {
                        if (!Directory.Exists(_customIconsPath))
                            Directory.CreateDirectory(_customIconsPath);

                        string destPath = GetCustomIconPath(exePath);
                        ProcessAndSaveIcon(sourcePath, destPath);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ChangeIcon error: {ex.Message}");
                        return false;
                    }
                });

                if (success)
                {
                    await RefreshIconAsync(iconImage, exePath);
                }
                else
                {
                    await ModernMessageBox.Show(this,
                        GetLocalizedString("Error", "Error"),
                        GetLocalizedString("FailedToChangeIcon", "Failed to change icon. Please try another file."),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ChangeIconAsync error: {ex.Message}");
            }
        }

        private static void ProcessAndSaveIcon(string sourcePath, string destPath)
        {
            using (var original = new WinForms.Bitmap(sourcePath))
            {
                int targetSize = 256;

                int width = original.Width;
                int height = original.Height;

                if (width > targetSize || height > targetSize)
                {
                    double ratio = Math.Min((double)targetSize / width, (double)targetSize / height);
                    width = (int)(width * ratio);
                    height = (int)(height * ratio);
                }

                using (var resized = new WinForms.Bitmap(targetSize, targetSize, WinFormsImaging.PixelFormat.Format32bppArgb))
                {
                    resized.SetResolution(96, 96);
                    using (var g = WinForms.Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                        g.Clear(System.Drawing.Color.Transparent);

                        int x = (targetSize - width) / 2;
                        int y = (targetSize - height) / 2;
                        g.DrawImage(original, x, y, width, height);
                    }

                    resized.Save(destPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        private async Task ResetIconAsync(System.Windows.Controls.Image iconImage, string exePath)
        {
            try
            {
                string customPath = GetCustomIconPath(exePath);

                await Task.Run(() =>
                {
                    try
                    {
                        if (File.Exists(customPath)) File.Delete(customPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ResetIcon delete error: {ex.Message}");
                    }
                });

                await RefreshIconAsync(iconImage, exePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResetIconAsync error: {ex.Message}");
            }
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ NAVIGATION
        // ═════════════════════════════════════════════════════════════════
        #region Navigation
        private void NavigateToHome(object sender, RoutedEventArgs e)
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.PagesNavigation?.Navigate(new Uri("Pages/HomePage.xaml", UriKind.RelativeOrAbsolute));
        }

        private void btnOptimize_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            mainWindow?.PagesNavigation?.Navigate(new Uri("Pages/OptimizePage.xaml", UriKind.RelativeOrAbsolute));
        }

        private void btnAbout_Click(object sender, RoutedEventArgs e)
        {
            new About { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            new Settings { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ THEME MANAGEMENT
        // ═════════════════════════════════════════════════════════════════
        #region Theme Management
        private void ToggleTheme(object sender, RoutedEventArgs e) { ThemeManager.ToggleTheme(); }

        private void ApplyTheme(bool isDark) { }

        private void SaveThemePreference()
        {
            try { using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Dreams"); key.SetValue("Theme", _isDarkMode ? "Dark" : "Light"); }
            catch { }
        }

        private void LoadThemePreference()
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Dreams"); _isDarkMode = key?.GetValue("Theme")?.ToString() == "Dark"; }
            catch { _isDarkMode = false; }
            ApplyTheme(_isDarkMode);
            UpdateCategoryColors();
        }

        public void SetDarkTheme() { _isDarkMode = true; ApplyTheme(true); UpdateCategoryColors(); }
        public void SetLightTheme() { _isDarkMode = false; ApplyTheme(false); UpdateCategoryColors(); }
        public void SetThemeFromHome(bool isDark) { _isDarkMode = isDark; ApplyTheme(isDark); UpdateCategoryColors(); }
        #endregion

        // ═════════════════════════════════════════════════════════════════
        // ███ MODERN MESSAGEBOX
        // ═════════════════════════════════════════════════════════════════
        #region ModernMessageBox
        public static class ModernMessageBox
        {
            private static string GetLocalizedString(DependencyObject owner, string resourceKey, string fallback)
            {
                try
                {
                    if (owner is FrameworkElement fe && fe.FindResource(resourceKey) is string localized) return localized;
                    if (Application.Current?.Resources.Contains(resourceKey) == true && Application.Current.Resources[resourceKey] is string appLocalized) return appLocalized;
                }
                catch { }
                return fallback ?? resourceKey;
            }

            public static async Task<MessageBoxResult> Show(DependencyObject owner, string title, string message, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
            {
                var tcs = new TaskCompletionSource<MessageBoxResult>();
                double parentOpacity = 1.0;
                if (owner is FrameworkElement fe && Window.GetWindow(fe) is Window parentWindow) parentOpacity = parentWindow.Opacity;

                var window = new Window
                {
                    Width = 450,
                    Height = 260,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Opacity = parentOpacity,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                System.Windows.Media.Brush GetResource(string key, System.Windows.Media.Brush fallback)
                {
                    try
                    {
                        if (Application.Current?.Resources.Contains(key) == true && Application.Current.Resources[key] is System.Windows.Media.Brush b) return b;
                        if (owner is FrameworkElement fe2 && fe2.TryFindResource(key) is System.Windows.Media.Brush b2) return b2;
                    }
                    catch { }
                    return fallback;
                }

                System.Windows.Media.Brush mainBg = GetResource("DynamicCardBg", System.Windows.Media.Brushes.White);
                System.Windows.Media.Brush borderBrush = GetResource("DynamicBorderBrush", new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)));
                System.Windows.Media.Brush mainText = GetResource("DynamicMainText", System.Windows.Media.Brushes.Black);
                System.Windows.Media.Brush subText = GetResource("DynamicSubText", new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 90, 90)));
                System.Windows.Media.Brush accentColor = GetResource("DynamicAccent", new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)));

                string iconChar = icon switch { MessageBoxImage.Warning => "\uE7BA", MessageBoxImage.Information => "\uE946", MessageBoxImage.Error => "\uEB90", MessageBoxImage.Question => "\uE897", _ => "\uE946" };
                System.Windows.Media.Color iconColorValue = icon switch
                {
                    MessageBoxImage.Warning => System.Windows.Media.Color.FromRgb(255, 193, 7),
                    MessageBoxImage.Error => System.Windows.Media.Color.FromRgb(220, 53, 69),
                    MessageBoxImage.Question => System.Windows.Media.Color.FromRgb(0, 192, 192),
                    _ => ((SolidColorBrush)accentColor).Color
                };
                System.Windows.Media.Brush iconColorBrush = new SolidColorBrush(iconColorValue);
                System.Windows.Media.Brush lightCircleBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(38, iconColorValue.R, iconColorValue.G, iconColorValue.B));

                var backgroundBrush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0.5, 0), EndPoint = new System.Windows.Point(0.5, 1) };
                backgroundBrush.GradientStops.Add(new GradientStop(iconColorValue, 0));
                backgroundBrush.GradientStops.Add(new GradientStop(iconColorValue, 0.023));
                backgroundBrush.GradientStops.Add(new GradientStop(((SolidColorBrush)mainBg).Color, 0.0231));
                backgroundBrush.GradientStops.Add(new GradientStop(((SolidColorBrush)mainBg).Color, 1));

                var border = new Border { Background = backgroundBrush, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = borderBrush, ClipToBounds = true };
                var mainGrid = new Grid();
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var iconPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 12) };
                var iconBorder = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = lightCircleBrush, HorizontalAlignment = HorizontalAlignment.Center };
                var iconTextBlock = new TextBlock { Text = iconChar, FontSize = 32, Foreground = iconColorBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets") };
                iconBorder.Child = iconTextBlock; iconPanel.Children.Add(iconBorder);
                Grid.SetRow(iconPanel, 0);

                var contentPanel = new StackPanel { Margin = new Thickness(30, 0, 30, 20) };
                var titleText = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = mainText, Margin = new Thickness(0, 0, 0, 8), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                var messageText = new TextBlock { Text = message, FontSize = 13, Foreground = subText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                contentPanel.Children.Add(titleText); contentPanel.Children.Add(messageText);
                Grid.SetRow(contentPanel, 1);

                var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) };
                string txtOK = GetLocalizedString(owner, "OK", "OK"), txtCancel = GetLocalizedString(owner, "Cancel", "Cancel"), txtYes = GetLocalizedString(owner, "Yes", "Yes"), txtNo = GetLocalizedString(owner, "No", "No");

                Button CreateModernButton(string text, bool isOutline = false)
                {
                    System.Windows.Media.Brush buttonBackground = icon switch
                    {
                        MessageBoxImage.Warning => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)),
                        MessageBoxImage.Error => new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69)),
                        MessageBoxImage.Question => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 192, 192)),
                        _ => iconColorBrush
                    };
                    var btn = new Button
                    {
                        Content = text,
                        Width = 100,
                        Height = 38,
                        Margin = new Thickness(8, 0, 8, 0),
                        Cursor = Cursors.Hand,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 13
                    };
                    var template = new ControlTemplate(typeof(Button));
                    var borderFactory = new FrameworkElementFactory(typeof(Border));
                    borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                    borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                    borderFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                    borderFactory.SetValue(Border.BorderThicknessProperty, isOutline ? new Thickness(1.5) : new Thickness(0));
                    var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                    cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                    cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                    cp.SetBinding(System.Windows.Documents.TextElement.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                    borderFactory.AppendChild(cp);
                    template.VisualTree = borderFactory;
                    btn.Template = template;

                    if (isOutline)
                    {
                        btn.Background = System.Windows.Media.Brushes.Transparent;
                        btn.Foreground = mainText;
                        btn.BorderBrush = buttonBackground;
                        btn.MouseEnter += (s, ev) => { if (buttonBackground is SolidColorBrush solid) btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, solid.Color.R, solid.Color.G, solid.Color.B)); };
                        btn.MouseLeave += (s, ev) => { btn.Background = System.Windows.Media.Brushes.Transparent; };
                    }
                    else
                    {
                        btn.Background = buttonBackground;
                        btn.Foreground = System.Windows.Media.Brushes.White;
                        btn.BorderBrush = buttonBackground;
                        btn.MouseEnter += (s, ev) => { if (buttonBackground is SolidColorBrush solid) btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(solid.Color.R * 0.88), (byte)(solid.Color.G * 0.88), (byte)(solid.Color.B * 0.88))); };
                        btn.MouseLeave += (s, ev) => { btn.Background = buttonBackground; };
                    }
                    btn.PreviewMouseLeftButtonDown += (s, ev) => { btn.Opacity = 0.85; };
                    btn.PreviewMouseLeftButtonUp += (s, ev) => { btn.Opacity = 1; };
                    return btn;
                }

                if (buttons == MessageBoxButton.OK)
                {
                    var okBtn = CreateModernButton(txtOK, false);
                    okBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.OK); window.Close(); };
                    buttonPanel.Children.Add(okBtn);
                }
                else if (buttons == MessageBoxButton.OKCancel)
                {
                    var cancelBtn = CreateModernButton(txtCancel, true);
                    cancelBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.Cancel); window.Close(); };
                    var okBtn = CreateModernButton(txtOK, false);
                    okBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.OK); window.Close(); };
                    buttonPanel.Children.Add(cancelBtn); buttonPanel.Children.Add(okBtn);
                }
                else if (buttons == MessageBoxButton.YesNo)
                {
                    var noBtn = CreateModernButton(txtNo, true);
                    noBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.No); window.Close(); };
                    var yesBtn = CreateModernButton(txtYes, false);
                    yesBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.Yes); window.Close(); };
                    buttonPanel.Children.Add(noBtn); buttonPanel.Children.Add(yesBtn);
                }
                else if (buttons == MessageBoxButton.YesNoCancel)
                {
                    var cancelBtn = CreateModernButton(txtCancel, true);
                    cancelBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.Cancel); window.Close(); };
                    var noBtn = CreateModernButton(txtNo, true);
                    noBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.No); window.Close(); };
                    var yesBtn = CreateModernButton(txtYes, false);
                    yesBtn.Click += (s, ev) => { tcs.SetResult(MessageBoxResult.Yes); window.Close(); };
                    buttonPanel.Children.Add(cancelBtn); buttonPanel.Children.Add(noBtn); buttonPanel.Children.Add(yesBtn);
                }

                Grid.SetRow(buttonPanel, 2);
                mainGrid.Children.Add(iconPanel); mainGrid.Children.Add(contentPanel); mainGrid.Children.Add(buttonPanel);
                border.Child = mainGrid; window.Content = border;
                window.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    DependencyObject source = e.OriginalSource as DependencyObject;
                    bool isButton = false;
                    while (source != null && source != window) { if (source is Button) { isButton = true; break; } source = VisualTreeHelper.GetParent(source); }
                    if (!isButton && window.WindowState == WindowState.Normal) window.DragMove();
                };
                window.Cursor = Cursors.Arrow;
                window.Loaded += (s, ev) =>
                {
                    var fadeAnimation = new DoubleAnimation(0, parentOpacity, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    window.BeginAnimation(Window.OpacityProperty, fadeAnimation);
                };
                window.ShowDialog();
                return await tcs.Task;
            }
        }
        #endregion
    }
}