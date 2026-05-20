<div align="center">

# 🌙 Dreams Software Collection

**A powerful all-in-one Windows optimization and management toolkit built with WPF (.NET 4.8)**

![Platform](https://img.shields.io/badge/Platform-Windows-0078D4?style=flat-square&logo=windows)
![Framework](https://img.shields.io/badge/.NET-4.8-512BD4?style=flat-square)
![Language](https://img.shields.io/badge/Language-C%23%20%2F%20XAML-239120?style=flat-square)
![Languages](https://img.shields.io/badge/UI%20Languages-5-orange?style=flat-square)
![Theme](https://img.shields.io/badge/Theme-Dark%20%2F%20Light-black?style=flat-square)

</div>

---

## 📖 Overview

Dreams Software is a feature-rich desktop application for Windows power users who want full control over their system. It combines system monitoring, junk cleaning, app installation, DNS management, and deep Windows tweaking into one sleek, theme-aware interface — complete with system tray support, multi-language UI, and adjustable transparency.

---

## ✨ Features at a Glance

| Feature | Description |
|---|---|
| 🖥️ System Dashboard | Real-time hardware stats, temperatures, and specs |
| 🧹 Optimizer | Deep junk cleaner with registry repair |
| 📦 App Installer | Offline batch installer with queue management |
| 🌐 Online App Store | Download and install apps directly from the internet |
| 🔒 DNS Manager | One-click DNS switching with latency testing |
| 🔧 Tweaks Engine | 50+ Windows registry and service tweaks |
| 🎨 Theme Engine | Dark/Light mode + adjustable window transparency |
| 🌍 Multi-Language | English, Arabic (RTL), French, Spanish, Russian |
| 📌 System Tray | Run silently in the background via tray icon |
| ⚙️ Settings | Centralized control for all preferences |

---

## 📄 Pages & Features

### 🏠 Home Page — System Dashboard

The Home Page is your live command center for system health.

- **CPU Info** — Name, clock speed, core/thread count
- **GPU Info** — Name, VRAM, color depth, integrated vs. dedicated detection, refresh rate
- **RAM Monitor** — Used / Free / Total with live percentage
- **Disk Monitor** — Drive usage with free space display
- **Motherboard & BIOS Info** — Manufacturer, model, BIOS version and date
- **Windows Info** — OS name, build number, hostname, username
- **Network Info** — IP address (toggle visibility), MAC address, IP geolocation with country code
- **Live Temperature** — Real-time CPU and GPU temperature via OpenHardwareMonitor
- **Live Clock** — Ticking clock with date display, fully localized per language
- **Smooth Loading** — Animated overlay during async hardware data fetch
- **Auto-Refresh** — Stats update every 3 seconds automatically

---

### 🧹 Optimize Page — System Cleaner

A comprehensive multi-category junk cleaner with admin-aware features.

**System Junk**
- Temp files, Recycle Bin, Windows Update cache
- Event logs, thumbnail cache, error reports
- Prefetch, driver cache, delivery optimization cache
- Notification cache, web cache, start menu/desktop leftovers

**Browser Junk**
- Cache, history, cookies, download history
- Saved passwords, form data, local storage, service workers, app cache

**App Junk**
- App logs, app temp files, recent documents
- Run history, clipboard, search history

**Privacy**
- Windows telemetry data cleanup, notification cache

**Registry Repair**
- Scans for invalid entries, orphaned DLL references, empty keys
- Uninstall remnants, app paths, type libraries, obsolete entries
- Protected paths are strictly excluded to avoid system damage

**Key Technical Features**
- Admin privilege detection — unlocks deeper cleaning
- Pause / Resume / Cancel during active clean
- Live size calculation with 5-minute cache for performance
- Threaded scanning with `CancellationToken` support
- RAM flush (working set trimmer) built-in

---

### 📦 Install Page — Offline App Installer

Batch-install your favorite applications from local installer files.

- **Folder-based structure** — organizes apps into categories automatically
- **Smart category tabs** — horizontal scrollable tabs, drag-to-scroll support
- **Custom category colors** — saved per-category, persisted in registry
- **Hi-resolution icon extraction** — uses Windows Shell API + `PrivateExtractIcons` for crisp icons
- **Selection order tracking** — installs apps in the exact order you selected them
- **Queue popup window** — floating panel showing your install queue, drag-to-reorder
- **Live progress** — per-app status indicators (waiting, installing, done, failed)
- **Pause / Resume / Cancel** — full control during batch installation
- **Installation timer** — live HH:MM:SS elapsed time counter
- **App count badge** — shows selected vs. total apps
- **Custom icons support** — drop custom icons in the `CustomIcons` folder

---

### 🌐 Online Page — Online App Store

Browse and install applications directly from the internet.

- **Categorized app grid** — Browsers, Communication, Media, Graphics, Gaming, Development, Utilities, VPN, Productivity, 3D Print
- **Category color coding** — each category has a distinctive color
- **Live download with speed meter** — shows real-time MB/s during download
- **Queue management** — select multiple apps, drag-to-reorder the install queue
- **Pause / Resume / Cancel** — full control over online installation
- **Uninstall support** — remove installed apps directly from the same interface
- **App status tracking** — per-app indicators (idle, downloading, installing, done, failed)
- **Responsive grid layout** — adapts column count to window width
- **Horizontal tab drag-scroll** — smooth category tab navigation
- **Timer** — tracks total elapsed installation time

---

### 🔒 DNS Page — DNS Manager

Switch your system DNS with one click, no technical knowledge needed.

**Built-in DNS Providers**
- **Google DNS** — `8.8.8.8` / `8.8.4.4` — Fast, reliable
- **Cloudflare** — `1.1.1.1` / `1.0.0.1` — Fast + Privacy + Security
- **OpenDNS** — `208.67.222.222` — Security-focused
- **Quad9** — `9.9.9.9` — Security + Privacy
- **AdGuard DNS** — `94.140.14.14` — Ad-blocking + Privacy
- **DHCP (Auto)** — Revert to ISP-assigned DNS

**Features**
- **Real-time latency testing** — pings all DNS servers and shows response time in ms
- **Active DNS detection** — automatically highlights your currently active DNS
- **Custom DNS** — enter any primary/secondary DNS manually
- **Filter bar** — filter providers by category (Recommended, Fast, Privacy, Security)
- **Provider cards** — clean visual cards with hover effects and active indicator
- **WMI-based detection** — reads current DNS from active network adapter

---

### 🔧 Tweaks Page — Windows Tweaks Engine

Apply safe, reversible system tweaks organized into 6 tabs.

| Tab | Description |
|---|---|
| **General** | Disable telemetry, Wi-Fi sense, fast startup, slow startup |
| **Gaming** | Game Mode, GPU scheduling, HAGS, Ultimate Performance power plan, disable Xbox Game Bar |
| **UI / Visual** | Disable animations, transparency, snap assist, Cortana, search highlights |
| **Services** | Disable unused Windows services (SysMain, DiagTrack, WAP Push, Print Spooler, etc.) |
| **Privacy** | Disable activity history, advertising ID, NVIDIA/Intel telemetry, app privacy settings |
| **Network** | TCP optimization, disable QoS throttling, optimize network stack settings |

**Technical Capabilities**
- Registry tweaks (`HKLM` / `HKCU`) with safe path validation
- Service tweaks (disable/enable Windows services)
- PowerShell command tweaks (Ultimate Performance plan, etc.)
- **Backup & Restore** — every tweak backs up original registry values before applying
- **Export backup** as `.reg` file for manual restoration
- **Undo all tweaks** with one click — full system rollback
- **Danger indicators** — high-risk tweaks are clearly marked
- **Restart prompts** — notifies when a reboot or Explorer restart is needed
- **Live process/service counter** in the Services tab
- **Thread-safe logging** — writes to `%LocalAppData%\Dreams\tweaks.log`

---

### 📌 System Tray

Dreams can run silently in the background with a system tray icon.

- **Left-click** — open/restore the main window
- **Double-click** — bring up the main window
- **Right-click** — opens a custom WPF context menu with quick-access shortcuts:
  - Dashboard, Optimizer, Installer, App Store, DNS, Tweaks, Settings, About, Exit
- Tray mode can be enabled/disabled from Settings
- Icon and tooltip adapt to the current language

---

### ⚙️ Settings Page

Central control for all application preferences.

| Setting | Options |
|---|---|
| Language | English, Arabic, French, Spanish, Russian |
| Theme | Dark / Light |
| Opacity | Adjustable slider (30% – 100%) |
| Hardware Acceleration | Enable / Disable (requires restart) |
| System Tray | Enable / Disable tray mode |
| Run at Startup | Add/remove from Windows startup |

All settings are persisted in the Windows Registry under `HKCU\SOFTWARE\Dreams`.

---

### ℹ️ About Window

- Developer links: GitHub, PayPal donation, Email contact
- Animated heart with pulse/scale animation
- Draggable, borderless, transparent window
- Respects current theme and opacity settings

---

## 🌍 Multi-Language Support

Dreams supports **5 languages** with full UI localization:

| Code | Language | Direction |
|---|---|---|
| `en` | English | LTR ↦ |
| `ar` | Arabic | RTL ↤ |
| `fr` | French | LTR ↦ |
| `es` | Spanish | LTR ↦ |
| `ru` | Russian | LTR ↦ |

- Language files are XAML `ResourceDictionary` files (`Lang/Lang-AR.xaml`, etc.)
- Arabic uses **RTL layout direction** applied globally to all windows
- Each language has its own **font family** resource (`Font_AR`, `Font_EN`, etc.)
- Culture-aware number and date formatting (Arabic uses Gregorian calendar with Western numerals)
- Language preference is saved and restored on next launch
- Changing language is **live** — no restart required

---

## 🎨 Theme & Transparency System

### Dark / Light Mode
- Full dark and light theme support via `ThemeManager`
- Theme is applied globally to all windows and pages
- Toggle button available in the main window toolbar
- Theme preference saved in registry

### Window Transparency
- Adjustable opacity from 30% to 100%
- Controlled via the Settings slider
- Applied to all open windows simultaneously via `ThemeManager.SetOpacity()`
- Saved and restored on next launch

---

## 🏗️ Architecture & Tech Stack

| Component | Technology |
|---|---|
| UI Framework | WPF (.NET Framework 4.8) |
| Language | C# |
| Hardware Monitoring | OpenHardwareMonitor |
| System Info | WMI (`System.Management`) |
| Registry | `Microsoft.Win32.Registry` |
| Network | `System.Net`, `HttpClient` |
| Tray Icon | `System.Windows.Forms.NotifyIcon` |
| UI Components | WPF-UI (`Wpf.Ui`) |
| Animations | WPF `Storyboard` / `DispatcherTimer` |
| Threading | `Task`, `CancellationToken`, `SemaphoreSlim` |

---

## 🔒 Safety & Reliability

- **Single instance enforcement** — Mutex prevents multiple app instances
- **Registry backup** before every tweak — safe undo at any time
- **Protected registry paths** — critical system paths are never touched
- **Admin detection** — features requiring elevation show appropriate UI
- **Error logging** — all errors logged to `%LocalAppData%\Dreams\tweaks.log`
- **Graceful shutdown** — cleanup of tray icon, mutex, and all windows on exit

---

## 📋 Requirements

- Windows 10 / Windows 11
- .NET Framework 4.8
- Administrator privileges (recommended for full feature access)

---

## 👨‍💻 Developer

Made with ❤️ by **Tarek Sadek**

- 🌐 GitHub: [github.com/Diagoo1](https://github.com/Diagoo1)
- 💌 Email: tarek.sadek44@gmail.com
- ☕ Support: [paypal.me/Diagoo1](https://paypal.me/Diagoo1)

---

<div align="center">
<sub>Dreams Software — Because your PC deserves better.</sub>
</div>
