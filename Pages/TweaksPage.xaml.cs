using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Windows.Threading;
using Dreams.Themes;

namespace Dreams
{
    #region ==================== DATA MODELS ====================

    public class TweakResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public bool RequiresRestart { get; set; }
        public bool RequiresExplorerRestart { get; set; }

        public static TweakResult Ok(bool restart = false, bool explorerRestart = false) =>
            new TweakResult
            {
                Success = true,
                RequiresRestart = restart,
                RequiresExplorerRestart = explorerRestart
            };

        public static TweakResult Fail(string msg) =>
            new TweakResult { Success = false, Message = msg };
    }

    public class BackupEntry
    {
        public string TweakId { get; set; }
        public string Hive { get; set; }
        public string KeyPath { get; set; }
        public string ValueName { get; set; }
        public string OriginalValue { get; set; }
        public RegistryValueKind ValueKind { get; set; }
        public bool ValueExisted { get; set; }
        public string ServiceName { get; set; }
        public string OriginalServiceStartMode { get; set; }
        public DateTime BackupDate { get; set; } = DateTime.UtcNow;
    }

    #endregion


    #region ==================== LOGGING SERVICE ====================

    public static class TweakLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dreams", "tweaks.log");

        private static readonly object LockObject = new object();

        static TweakLogger()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)); } catch { }
        }

        public static void Log(string level, string message)
        {
            try
            {
                lock (LockObject)
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
                }
            }
            catch { }
            Debug.WriteLine($"[{level}] {message}");
        }

        public static void Info(string msg) => Log("INFO", msg);
        public static void Error(string msg) => Log("ERROR", msg);
        public static void Warn(string msg) => Log("WARN", msg);
    }

    #endregion


    #region ==================== REGISTRY OPERATIONS ====================

    public static class RegistryHelper
    {
        public static RegistryKey GetBaseKey(string hive)
        {
            return hive.ToUpper() switch
            {
                "HKLM" => Registry.LocalMachine,
                "HKCU" => Registry.CurrentUser,
                "HKCR" => Registry.ClassesRoot,
                "HKU" => Registry.Users,
                _ => Registry.CurrentUser
            };
        }

        public static (string hive, string keyPath) SplitPath(string fullPath)
        {
            var parts = fullPath.Split(new[] { '\\' }, 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "");
        }

        public static object GetValue(string fullPath, string name)
        {
            try
            {
                var (hive, keyPath) = SplitPath(fullPath);
                using var key = GetBaseKey(hive).OpenSubKey(keyPath);
                string actualName = (name == "(Default)" || name == "(default)") ? "" : name;
                return key?.GetValue(actualName);
            }
            catch { return null; }
        }

        public static RegistryValueKind GetValueKind(string fullPath, string name)
        {
            try
            {
                var (hive, keyPath) = SplitPath(fullPath);
                using var key = GetBaseKey(hive).OpenSubKey(keyPath);
                string actualName = (name == "(Default)" || name == "(default)") ? "" : name;
                return key?.GetValueKind(actualName) ?? RegistryValueKind.DWord;
            }
            catch { return RegistryValueKind.DWord; }
        }

        public static bool SetValue(string fullPath, string name,
            object value, RegistryValueKind kind)
        {
            try
            {
                var (hive, keyPath) = SplitPath(fullPath);
                using var key = GetBaseKey(hive).CreateSubKey(keyPath, true);
                if (key == null) return false;
                string actualName = (name == "(Default)" || name == "(default)") ? "" : name;
                key.SetValue(actualName, value, kind);
                return true;
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"SetValue failed [{fullPath}\\{name}]: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteValue(string fullPath, string name)
        {
            try
            {
                var (hive, keyPath) = SplitPath(fullPath);
                using var key = GetBaseKey(hive).OpenSubKey(keyPath, true);
                string actualName = (name == "(Default)" || name == "(default)") ? "" : name;
                key?.DeleteValue(actualName, false);
                return true;
            }
            catch { return false; }
        }

        public static bool ValueEquals(string fullPath, string name, object expected)
        {
            var val = GetValue(fullPath, name);
            if (val == null && expected == null) return true;
            if (val == null || expected == null) return false;

            if (int.TryParse(val.ToString(), out int v1)
                && int.TryParse(expected.ToString(), out int v2))
                return v1 == v2;

            return val.ToString().Trim()
                .Equals(expected.ToString().Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion


    #region ==================== USER PREFERENCE STORAGE ====================

    public static class UserPreferenceManager
    {
        private const string StateRegistryKey = @"SOFTWARE\Dreams\TweakStates";

        public static void SetState(string tweakId, bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StateRegistryKey);
                key?.SetValue(tweakId, enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) { TweakLogger.Error($"SetState failed: {ex.Message}"); }
        }

        public static bool GetState(string tweakId)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StateRegistryKey);
                var val = key?.GetValue(tweakId);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { return false; }
        }

        public static bool HasState(string tweakId)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StateRegistryKey);
                return key?.GetValue(tweakId) != null;
            }
            catch { return false; }
        }

        public static void RemoveState(string tweakId)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StateRegistryKey, true);
                key?.DeleteValue(tweakId, false);
            }
            catch { }
        }
    }

    #endregion


    #region ==================== EXPLORER PROCESS MANAGEMENT ====================

    public static class ExplorerManager
    {
        private static readonly HashSet<string> ExplorerDependentTweaks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AlignTaskbarLeft", "HideTaskView", "ShowSeconds", "CompactExplorer",
            "ShowSnapLayouts", "RestoreFolderWindows", "DisableTaskbarDelay",
            "DisableCopilotUI", "OpenQuickAccess", "TaskbarTransparency",
            "DisableOverlayBadges", "StandardStartMenu", "EnableDarkMode",
            "EnableEndTask", "ShowTaskbarIcons", "EnableTooltips",
            "DarkTaskbar", "ShowPersonalizedAds", "DisableTipsSuggestions",
            "TaskbarAlignLeft", "DarkThemeApps"
        };

        public static bool RequiresExplorerRestart(string tweakId) =>
            ExplorerDependentTweaks.Contains(tweakId);

        public static void RestartExplorer()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("explorer"))
                    try { process.Kill(); } catch { }

                Thread.Sleep(500);
                if (Process.GetProcessesByName("explorer").Length == 0)
                    Process.Start("explorer.exe");

                TweakLogger.Info("Explorer restarted successfully");
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"RestartExplorer failed: {ex.Message}");
            }
        }
    }

    #endregion


    #region ==================== BACKUP AND RESTORE SERVICE ====================

    public static class BackupService
    {
        private const string BackupRegistryKey = @"SOFTWARE\Dreams\Backup";

        public static void Save(BackupEntry entry)
        {
            try
            {
                using var key = Registry.CurrentUser
                    .CreateSubKey(BackupRegistryKey + "\\" + entry.TweakId);
                if (key == null) return;
                key.SetValue("Hive", entry.Hive ?? "");
                key.SetValue("KeyPath", entry.KeyPath ?? "");
                key.SetValue("ValueName", entry.ValueName ?? "");
                key.SetValue("OriginalValue", entry.OriginalValue ?? "");
                key.SetValue("ValueKind", (int)entry.ValueKind, RegistryValueKind.DWord);
                key.SetValue("ValueExisted", entry.ValueExisted ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ServiceName", entry.ServiceName ?? "");
                key.SetValue("OriginalServiceStartMode",
                    entry.OriginalServiceStartMode ?? "");
                key.SetValue("BackupDate", entry.BackupDate.ToString("O"));
            }
            catch (Exception ex) { TweakLogger.Error($"Backup save failed: {ex.Message}"); }
        }

        public static BackupEntry Load(string tweakId)
        {
            try
            {
                using var key = Registry.CurrentUser
                    .OpenSubKey(BackupRegistryKey + "\\" + tweakId);
                if (key == null) return null;
                return new BackupEntry
                {
                    TweakId = tweakId,
                    Hive = key.GetValue("Hive")?.ToString(),
                    KeyPath = key.GetValue("KeyPath")?.ToString(),
                    ValueName = key.GetValue("ValueName")?.ToString(),
                    OriginalValue = key.GetValue("OriginalValue")?.ToString(),
                    ValueKind = (RegistryValueKind)(int)(key.GetValue("ValueKind") ?? 4),
                    ValueExisted = ((int)(key.GetValue("ValueExisted") ?? 0)) == 1,
                    ServiceName = key.GetValue("ServiceName")?.ToString(),
                    OriginalServiceStartMode =
                        key.GetValue("OriginalServiceStartMode")?.ToString()
                };
            }
            catch { return null; }
        }

        public static void Delete(string tweakId)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(BackupRegistryKey, true);
                key?.DeleteSubKeyTree(tweakId, false);
            }
            catch { }
        }

        public static BackupEntry CreateRegistryBackup(string tweakId,
            string fullPath, string valueName)
        {
            var existing = Load(tweakId);
            if (existing != null
                && !string.IsNullOrEmpty(existing.KeyPath)
                && !string.IsNullOrEmpty(existing.Hive))
                return existing;

            if (existing != null) Delete(tweakId);

            var (hive, keyPath) = RegistryHelper.SplitPath(fullPath);
            var currentVal = RegistryHelper.GetValue(fullPath, valueName);

            var entry = new BackupEntry
            {
                TweakId = tweakId,
                Hive = hive,
                KeyPath = keyPath,
                ValueName = valueName,
                ValueExisted = currentVal != null,
                OriginalValue = currentVal?.ToString() ?? "",
                ValueKind = currentVal != null
                    ? RegistryHelper.GetValueKind(fullPath, valueName)
                    : RegistryValueKind.DWord
            };
            Save(entry);
            return entry;
        }

        public static bool RestoreRegistry(string tweakId)
        {
            var entry = Load(tweakId);
            if (entry == null) return false;
            string fullPath = $"{entry.Hive}\\{entry.KeyPath}";

            if (!entry.ValueExisted)
            {
                RegistryHelper.DeleteValue(fullPath, entry.ValueName);
                Delete(tweakId);
                return true;
            }

            object val = entry.ValueKind switch
            {
                RegistryValueKind.DWord =>
                    int.TryParse(entry.OriginalValue, out var i) ? (object)i : 0,
                RegistryValueKind.QWord =>
                    long.TryParse(entry.OriginalValue, out var l) ? (object)l : 0L,
                _ => entry.OriginalValue
            };

            bool ok = RegistryHelper.SetValue(fullPath, entry.ValueName, val, entry.ValueKind);
            if (ok) Delete(tweakId);
            return ok;
        }

        public static bool ExportBackupToRegFile(string outputPath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();
                using var rootKey = Registry.CurrentUser.OpenSubKey(BackupRegistryKey);
                if (rootKey == null) return false;

                foreach (var subName in rootKey.GetSubKeyNames())
                {
                    var entry = Load(subName);
                    if (entry == null || string.IsNullOrEmpty(entry.KeyPath)
                        || !entry.ValueExisted) continue;

                    sb.AppendLine($"[{entry.Hive}\\{entry.KeyPath}]");
                    string formattedValue = entry.ValueKind switch
                    {
                        RegistryValueKind.DWord =>
                            $"dword:{int.Parse(entry.OriginalValue):x8}",
                        RegistryValueKind.QWord =>
                            $"hex(b):{string.Join(",", BitConverter.GetBytes(long.Parse(entry.OriginalValue)).Select(b => b.ToString("x2")))}",
                        _ =>
                            $"\"{entry.OriginalValue.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
                    };
                    sb.AppendLine($"\"{entry.ValueName}\"={formattedValue}");
                    sb.AppendLine();
                }

                File.WriteAllText(outputPath, sb.ToString(), Encoding.Unicode);
                return true;
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"Export failed: {ex.Message}");
                return false;
            }
        }
    }

    #endregion


    #region ==================== TWEAK ABSTRACTION LAYER ====================

    public interface ITweak
    {
        string Id { get; }
        bool IsDangerous { get; }
        bool RequiresRestart { get; }
        Task<TweakResult> ApplyAsync();
        Task<TweakResult> RevertAsync();
        bool IsApplied();
    }

    public class RegistryTweak : ITweak
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string ValueName { get; set; }
        public object EnabledValue { get; set; }
        public object DisabledValue { get; set; }
        public RegistryValueKind Kind { get; set; } = RegistryValueKind.DWord;
        public bool IsDangerous { get; set; }
        public bool RequiresRestart { get; set; }

        public Task<TweakResult> ApplyAsync()
        {
            BackupService.CreateRegistryBackup(Id, Path, ValueName);
            bool ok = RegistryHelper.SetValue(Path, ValueName, EnabledValue, Kind);
            return Task.FromResult(ok
                ? TweakResult.Ok(RequiresRestart, ExplorerManager.RequiresExplorerRestart(Id))
                : TweakResult.Fail($"Failed to write {Path}\\{ValueName}"));
        }

        public Task<TweakResult> RevertAsync()
        {
            var backup = BackupService.Load(Id);
            if (backup != null)
            {
                bool restored = BackupService.RestoreRegistry(Id);
                if (restored)
                    return Task.FromResult(TweakResult.Ok(RequiresRestart,
                        ExplorerManager.RequiresExplorerRestart(Id)));
            }

            bool ok = DisabledValue == null
                ? RegistryHelper.DeleteValue(Path, ValueName)
                : RegistryHelper.SetValue(Path, ValueName, DisabledValue, Kind);

            return Task.FromResult(ok
                ? TweakResult.Ok(RequiresRestart, ExplorerManager.RequiresExplorerRestart(Id))
                : TweakResult.Fail($"Revert failed for {Id}"));
        }

        public bool IsApplied() =>
            RegistryHelper.ValueEquals(Path, ValueName, EnabledValue);
    }

    public class ServiceTweak : ITweak
    {
        public string Id { get; set; }
        public string ServiceName { get; set; }
        public string DisabledMode { get; set; } = "Disabled";
        public string DefaultMode { get; set; } = "Manual";
        public bool IsDangerous { get; set; }
        public bool RequiresRestart { get; set; }

        private bool? _cachedIsApplied;
        private DateTime _cacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

        public Task<TweakResult> ApplyAsync() => Task.Run(() =>
        {
            var existing = BackupService.Load(Id);
            if (existing == null || string.IsNullOrEmpty(existing.OriginalServiceStartMode))
            {
                var current = GetServiceStartMode(ServiceName);
                BackupService.Save(new BackupEntry
                {
                    TweakId = Id,
                    ServiceName = ServiceName,
                    OriginalServiceStartMode = current ?? DefaultMode
                });
            }

            bool ok = SetServiceStartup(ServiceName, DisabledMode);
            _cachedIsApplied = ok ? true : (bool?)null;
            _cacheTime = DateTime.Now;
            return ok
                ? TweakResult.Ok(RequiresRestart)
                : TweakResult.Fail($"Service {ServiceName} not changed");
        });

        public Task<TweakResult> RevertAsync() => Task.Run(() =>
        {
            var entry = BackupService.Load(Id);
            string target = entry?.OriginalServiceStartMode ?? DefaultMode;
            bool ok = SetServiceStartup(ServiceName, target);
            if (ok)
            {
                BackupService.Delete(Id);
                _cachedIsApplied = false;
                _cacheTime = DateTime.Now;
            }
            return ok
                ? TweakResult.Ok(RequiresRestart)
                : TweakResult.Fail("Service revert failed");
        });

        public bool IsApplied()
        {
            if (_cachedIsApplied.HasValue
                && (DateTime.Now - _cacheTime) < CacheLifetime)
                return _cachedIsApplied.Value;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");

                if (key == null) { _cachedIsApplied = false; }
                else
                {
                    var startValue = key.GetValue("Start");
                    if (startValue == null) { _cachedIsApplied = false; }
                    else
                    {
                        int start = Convert.ToInt32(startValue);
                        bool isDisabled = start == 4;
                        _cachedIsApplied = string.Equals(DisabledMode, "Disabled",
                            StringComparison.OrdinalIgnoreCase) ? isDisabled : !isDisabled;
                    }
                }
                _cacheTime = DateTime.Now;
                return _cachedIsApplied.Value;
            }
            catch
            {
                _cachedIsApplied = false;
                return false;
            }
        }

        private static string GetServiceStartMode(string name)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{name}");
                if (key?.GetValue("Start") is int start)
                    return start switch { 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Manual" };
                return null;
            }
            catch { return null; }
        }

        private static bool SetServiceStartup(string name, string mode)
        {
            try
            {
                int startValue = mode.ToLower() switch
                {
                    "disabled" => 4,
                    "manual" => 3,
                    "automatic" => 2,
                    _ => 3
                };
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{name}", true);
                if (key == null) return false;
                key.SetValue("Start", startValue, RegistryValueKind.DWord);
                return true;
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"SetServiceStartup({name},{mode}): {ex.Message}");
                return false;
            }
        }
    }

    public class CommandTweak : ITweak
    {
        public string Id { get; set; }
        public string ApplyCommand { get; set; }
        public string RevertCommand { get; set; }
        public Func<bool> CheckApplied { get; set; }
        public bool IsDangerous { get; set; }
        public bool RequiresRestart { get; set; }

        public async Task<TweakResult> ApplyAsync()
        {
            bool ok = await PowerShellExecutor.RunAsync(ApplyCommand);
            return ok
                ? TweakResult.Ok(RequiresRestart, ExplorerManager.RequiresExplorerRestart(Id))
                : TweakResult.Fail("Command failed");
        }

        public async Task<TweakResult> RevertAsync()
        {
            if (string.IsNullOrWhiteSpace(RevertCommand)) return TweakResult.Ok();
            bool ok = await PowerShellExecutor.RunAsync(RevertCommand);
            return ok
                ? TweakResult.Ok(RequiresRestart, ExplorerManager.RequiresExplorerRestart(Id))
                : TweakResult.Fail("Revert failed");
        }

        public bool IsApplied()
        {
            try { return CheckApplied?.Invoke() ?? false; }
            catch { return false; }
        }
    }

    #endregion


    #region ==================== POWERSHELL EXECUTION ENGINE ====================

    public static class PowerShellExecutor
    {
        public static Task<bool> RunAsync(string script) => Task.Run(() =>
        {
            try
            {
                var bytes = Encoding.Unicode.GetBytes(script);
                string encoded = Convert.ToBase64String(bytes);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string err = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(60_000))
                {
                    try { proc.Kill(); } catch { }
                    TweakLogger.Error("PS timeout");
                    return false;
                }
                if (proc.ExitCode != 0) TweakLogger.Error($"PS exit={proc.ExitCode}: {err}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"PS failed: {ex.Message}");
                return false;
            }
        });
    }

    #endregion


    #region ==================== RESOURCE LOADER ====================

    public static class ResourceLoader
    {
        private static readonly Dictionary<string, string> _cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static string GetString(string key, string fallback = null)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            string result = fallback ?? key;
            try
            {
                if (Application.Current?.Resources.Contains(key) == true
                    && Application.Current.Resources[key] is string value)
                    result = value;
            }
            catch { }
            _cache[key] = result;
            return result;
        }

        public static string GetString(string key, params object[] args)
            => string.Format(GetString(key), args);

        public static void ClearCache() => _cache.Clear();
    }

    #endregion


    #region ==================== TWEAK DEFINITION REGISTRY ====================

    public static class TweakCatalog
    {
        private static Dictionary<string, ITweak> _allTweaks;
        private static readonly object BuildLock = new object();

        public static Dictionary<string, ITweak> All
        {
            get
            {
                if (_allTweaks == null)
                    lock (BuildLock)
                        if (_allTweaks == null)
                            _allTweaks = BuildTweakDictionary();
                return _allTweaks;
            }
        }

        public static Task PreWarmAsync() => Task.Run(() => { var _ = All; });

        private static Dictionary<string, ITweak> BuildTweakDictionary()
        {
            var d = new Dictionary<string, ITweak>(StringComparer.OrdinalIgnoreCase);

            void Reg(string id, string path, string name,
                     object enabled, object disabled = null,
                     RegistryValueKind kind = RegistryValueKind.DWord,
                     bool dangerous = false, bool restart = false)
            {
                if (d.ContainsKey(id)) return;
                d[id] = new RegistryTweak
                {
                    Id = id,
                    Path = path,
                    ValueName = name,
                    EnabledValue = enabled,
                    DisabledValue = disabled,
                    Kind = kind,
                    IsDangerous = dangerous,
                    RequiresRestart = restart
                };
            }

            void Svc(string id, string svc,
                     string disabledMode = "Disabled",
                     string defaultMode = "Manual",
                     bool dangerous = false)
            {
                if (d.ContainsKey(id)) return;
                d[id] = new ServiceTweak
                {
                    Id = id,
                    ServiceName = svc,
                    DisabledMode = disabledMode,
                    DefaultMode = defaultMode,
                    IsDangerous = dangerous
                };
            }

            void Cmd(string id, string apply, string revert,
                     Func<bool> check, bool dangerous = false, bool restart = false)
            {
                if (d.ContainsKey(id)) return;
                d[id] = new CommandTweak
                {
                    Id = id,
                    ApplyCommand = apply,
                    RevertCommand = revert,
                    CheckApplied = check,
                    IsDangerous = dangerous,
                    RequiresRestart = restart
                };
            }

            // ── System Optimization ──
            Reg("DisableTelemetry",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                "AllowTelemetry", 0, 3);
            Reg("DisableCopilot",
                @"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot",
                "TurnOffWindowsCopilot", 1, 0);
            Reg("DetailedBSOD",
                @"HKLM\SYSTEM\CurrentControlSet\Control\CrashControl",
                "DisplayParameters", 1, 0);
            Reg("DisableKernelIsolation",
                @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                "Enabled", 0, 1, dangerous: true, restart: true);
            Reg("DisableBackgroundMSStore",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                "LetAppsRunInBackground", 2, 0);
            Reg("DisableDefenderRTP",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender",
                "DisableAntiSpyware", 1, 0, dangerous: true);
            Reg("DisableFastStartup",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                "HiberbootEnabled", 0, 1);
            Reg("DisableWindowsRecall",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                "DisableAIDataAnalysis", 1, 0);
            Reg("RemoveBingIntegration",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                "DisableWebSearch", 1, 0);
            Reg("AlignTaskbarLeft",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "TaskbarAl", 0, 1);
            Reg("DisableAutorun",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                "NoDriveTypeAutoRun", 255, 0);
            Reg("DisableStickyKeys",
                @"HKCU\Control Panel\Accessibility\StickyKeys",
                "Flags", "506", "510", RegistryValueKind.String);
            Reg("DisableUAC",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                "EnableLUA", 0, 1, dangerous: true, restart: true);
            Reg("DisableSmartScreen",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                "EnableSmartScreen", 0, 1, dangerous: true);
            Reg("DisableWiFiSense",
                @"HKLM\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config",
                "AutoConnectAllowedOEM", 0, 1);
            Reg("PauseWindowsUpdates",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
                "NoAutoUpdate", 1, 0);
            Reg("DisableReservedStorage",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager",
                "ShippedWithReserves", 0, 1);
            Reg("DisableDHPT",
                @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                "DisableTaskOffload", 1, 0);
            Reg("DisableMPO",
                @"HKLM\SOFTWARE\Microsoft\Windows\Dwm",
                "OverlayTestMode", 5, 0);
            Reg("DisableLastAccessTime",
                @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem",
                "NtfsDisableLastAccessUpdate", 1, 0);
            Reg("DisableConsoleLockDisplay",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization",
                "NoLockScreen", 1, 0);
            Reg("DisableShutdownDialog",
                @"HKCU\Control Panel\Desktop",
                "AutoEndTasks", "1", "0", RegistryValueKind.String);
            Reg("DisableControlFlowGuard",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                "EnableCfg", 0, 1);
            Reg("DisableFSCache",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                "DisablePagingExecutive", 1, 0);
            Reg("DisableMemDiag",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Reliability",
                "TimeStampInterval", 0, 1);
            Cmd("DisableHibFast", "powercfg /h off", "powercfg /h on",
                () => !File.Exists(@"C:\hiberfil.sys"));
            Reg("DisableDefrag",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OptimalLayout",
                "EnableAutoLayout", 0, 1);

            // ── Gaming Performance ──
            Reg("DisableGameBar",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR",
                "AppCaptureEnabled", 0, 1);
            Reg("DisableMouseAcceleration",
                @"HKCU\Control Panel\Mouse",
                "MouseSpeed", "0", "1", RegistryValueKind.String);
            Reg("EnableGameMode",
                @"HKCU\Software\Microsoft\GameBar",
                "AutoGameModeEnabled", 1, 0);
            Reg("EnableHAGS",
                @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                "HwSchMode", 2, 1, restart: true);
            Reg("OptimizeWindowedGames",
                @"HKCU\Software\Microsoft\DirectX\GraphicsSettings",
                "SwapEffectUpgradeEnable", 1, 0);
            Reg("DisableSlowStartup",
                @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness", 0, 20);
            Reg("DisablePointerPrecision",
                @"HKCU\Control Panel\Mouse",
                "MouseThreshold1", "0", "6", RegistryValueKind.String);
            Reg("PrioritySeparation",
                @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl",
                "Win32PrioritySeparation", 38, 2);
            Reg("UseDefaultPower",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Power",
                "CsEnabled", 0, 1);

            // ── User Interface ──
            Reg("EnableDarkMode",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0, 1);
            Reg("DarkTaskbar",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0, 1);
            Reg("EnableEndTask",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings",
                "TaskbarEndTask", 1, 0);
            Reg("HideTaskView",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "ShowTaskViewButton", 0, 1);
            Reg("MenuShowDelayZero",
                @"HKCU\Control Panel\Desktop",
                "MenuShowDelay", "0", "400", RegistryValueKind.String);
            Reg("ShowSeconds",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "ShowSecondsInSystemClock", 1, 0);
            Reg("CompactExplorer",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "UseCompactMode", 1, 0);
            Reg("ShowSnapLayouts",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "EnableSnapAssistFlyout", 1, 0);
            Reg("RestoreFolderWindows",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "PersistBrowsers", 1, 0);
            Reg("DisableTaskbarDelay",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "ExtendedUIHoverTime", 100, 400);
            Reg("DisableTipsSuggestions",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "SubscribedContent-310093Enabled", 0, 1);
            Reg("ShowPersonalizedAds",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                "Enabled", 0, 1);
            Reg("DisableCopilotUI",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "ShowCopilotButton", 0, 1);
            Reg("OpenQuickAccess",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "LaunchTo", 1, 2);
            Reg("WallpaperJPEGCompression",
                @"HKCU\Control Panel\Desktop",
                "JPEGImportQuality", 100, 85);
            Reg("DisableLockScreenTips",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                "RotatingLockScreenOverlayEnabled", 0, 1);
            Reg("TaskbarTransparency",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 1, 0);
            Reg("DisableOverlayBadges",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "TaskbarBadges", 0, 1);
            Reg("StandardStartMenu",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "Start_Layout", 0, 1);
            Reg("StandardButtonsSize",
                @"HKCU\Control Panel\Desktop\WindowMetrics",
                "CaptionWidth", "-330", "-345", RegistryValueKind.String);
            Reg("StandardCursorFlicker",
                @"HKCU\Control Panel\Desktop",
                "CursorBlinkRate", "530", "1200", RegistryValueKind.String);
            Reg("StandardScrollbarSize",
                @"HKCU\Control Panel\Desktop\WindowMetrics",
                "ScrollWidth", "-255", "-285", RegistryValueKind.String);
            Reg("EnableTooltips",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "ShowInfoTip", 1, 0);
            Reg("DisableAutoBrightness",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AdaptiveDisplayBrightness",
                "AdaptiveBrightness", 0, 1);
            Reg("ShowTaskbarIcons",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer",
                "EnableAutoTray", 0, 1);
            Reg("DisableTipsNotif",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested",
                "Enabled", 0, 1);

            // ── Privacy ──
            Reg("DisableAdvertisingId",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                "Enabled", 0, 1);
            Reg("DisableLocationTracking",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
                "Value", "Deny", "Allow", RegistryValueKind.String);
            Reg("DisableBehaviorRecording",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy",
                "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1);
            Reg("DisableHandwritingData",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\TabletPC",
                "PreventHandwritingDataSharing", 1, 0);
            Reg("DisableSpeechUpdates",
                @"HKLM\SOFTWARE\Policies\Microsoft\Speech",
                "AllowSpeechModelUpdate", 0, 1);
            Reg("DisableNVIDIATelemetry",
                @"HKLM\SOFTWARE\NVIDIA Corporation\Global\FTS",
                "EnableRID44231", 0, 1);
            Reg("DisableAppInstallData",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                "AITEnable", 0, 1);
            Svc("BlockMSDomains", "DoSvc", defaultMode: "Manual");
            Reg("DisableFeedbackRequests",
                @"HKCU\Software\Microsoft\Siuf\Rules",
                "NumberOfSIUFInPeriod", 0, 1);
            Reg("DisableHiddenMonitoring",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                "PublishUserActivities", 0, 1);
            Reg("DisableCovertCollection",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                "UploadUserActivities", 0, 1);
            Reg("DisableOfflineMapsUpdate",
                @"HKLM\SYSTEM\CurrentControlSet\Services\MapsBroker",
                "AutoUpdateEnabled", 0, 1);
            Reg("DisableDataSyncing",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\SettingSync",
                "DisableSettingSync", 2, 0);
            Reg("DisableSchedulerData",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance",
                "MaintenanceDisabled", 1, 0);
            Reg("DisableAppUsageStats",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                "EnableActivityFeed", 0, 1);
            Reg("DisableHWConfigData",
                @"HKLM\SOFTWARE\Microsoft\SQMClient\Windows",
                "CEIPEnable", 0, 1);
            Reg("DisableUserLocation",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                "DisableLocation", 1, 0);
            Reg("DisableHiddenExperiments",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                "AllowExperimentation", 0, 1);
            Reg("DisableEventsLogging",
                @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                "DisableEnterpriseAuthProxy", 1, 0);
            Reg("DisableIntelTelemetry",
                @"HKLM\SOFTWARE\Intel\Intel(R) Computing Improvement Program",
                "Opt-In", 0, 1);

            // ── Network ──
            Reg("OptimizeNetwork",
                @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                "Tcp1323Opts", 1, 0);
            Reg("DisableWPBT",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager",
                "DisableWpbtExecution", 1, 0);

            Cmd("DisableTeredoIPv6",
                "netsh interface teredo set state disabled",
                "netsh interface teredo set state default",
                () =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("netsh",
                            "interface teredo show state")
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p == null) return false;
                        string output = p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return false; }
                        return output.IndexOf("disabled",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                });

            Cmd("DisableFirewall",
                "Set-NetFirewallProfile -All -Enabled False",
                "Set-NetFirewallProfile -All -Enabled True",
                () =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("powershell",
                            "-NoProfile -Command " +
                            "\"(Get-NetFirewallProfile -Profile Domain,Public,Private).Enabled\"")
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p == null) return false;
                        string output = p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return false; }
                        return output.IndexOf("False",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                }, dangerous: true);

            // ── Services ──
            Svc("SvcWindowsSearch", "WSearch", defaultMode: "Automatic");
            Svc("SvcWindowsStore", "InstallService", defaultMode: "Manual");
            Svc("SvcBluetooth", "bthserv");
            Svc("SvcFax", "Fax");
            Svc("SvcSystemComponents", "SysMain", defaultMode: "Automatic");
            Svc("SvcVPN", "RasMan");
            Svc("SvcEventLogs", "EventLog", defaultMode: "Automatic", dangerous: true);
            Svc("SvcKiosk", "AssignedAccessManagerSvc");
            Svc("SvcBgSecurity", "SecurityHealthService", defaultMode: "Manual", dangerous: true);
            Svc("SvcHyperV", "vmickvpexchange");
            Svc("SvcXbox", "XblAuthManager");
            Svc("SvcPerfCounters", "PerfHost");
            Svc("SvcPrinter", "Spooler", defaultMode: "Automatic");
            Svc("SvcTabletMode", "TabletInputService");
            Svc("SvcLocalNetwork", "fdPHost");
            Svc("SvcWindowsMedia", "WMPNetworkSvc");
            Svc("SvcWebDAV", "WebClient");
            Svc("SvcFileEncryption", "EFS");
            Svc("SvcBgDiagnostics", "WdiSystemHost");
            Svc("SvcPushNotifications", "WpnService", defaultMode: "Automatic");
            Svc("SvcWindowsUpdate", "wuauserv", defaultMode: "Manual", dangerous: true);
            Svc("SvcBiometric", "WbioSrvc");
            Svc("SvcScanner", "WiaRpc");
            Svc("SvcAdditionalMonitors", "DisplayEnhancementService");
            Svc("SvcUSBModems", "Wcmsvc");
            Svc("SvcRemoteDesktop", "TermService");
            Svc("SvcSmartCard", "SCardSvr");
            Svc("SvcLocalization", "MapsBroker");
            Svc("SvcCorporateTools", "ALG");
            Svc("SvcStoreDemoMode", "RetailDemo");
            Svc("SvcMMScheduler", "MMCSS");

            // ── Advanced Commands ──
            Cmd("ShowFoldersThisPC",
                @"$keys = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}'
                  );
                  foreach ($k in $keys) { if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null } }",
                @"$keys = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}'
                  );
                  foreach ($k in $keys) { Remove-Item -Path $k -Force -ErrorAction SilentlyContinue }",
                () =>
                {
                    try
                    {
                        using var k = Registry.LocalMachine.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}");
                        return k != null;
                    }
                    catch { return false; }
                });

            Cmd("Remove3DObjects",
                @"$keys = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}'
                  );
                  foreach ($k in $keys) { Remove-Item -Path $k -Force -ErrorAction SilentlyContinue }",
                @"$keys = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}'
                  );
                  foreach ($k in $keys) { if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null } }",
                () =>
                {
                    try
                    {
                        using var k = Registry.LocalMachine.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}");
                        return k == null;
                    }
                    catch { return true; }
                });

            Cmd("StrippedContextMenu",
                @"$key = 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32';
                  if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
                  Set-ItemProperty -Path $key -Name '(default)' -Value '' -ErrorAction SilentlyContinue",
                @"Remove-Item -Path 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}' -Recurse -Force -ErrorAction SilentlyContinue",
                () =>
                {
                    try
                    {
                        using var k = Registry.CurrentUser.OpenSubKey(
                            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32");
                        return k != null;
                    }
                    catch { return false; }
                });

            Cmd("UltimatePerformance",
                "powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61; powercfg /s e9a42b02-d5df-448d-aa00-03f14749eb61",
                "powercfg /s 381b4222-f694-41f0-9685-ff5bb260df2e",
                () => CheckPowerScheme("e9a42b02-d5df-448d-aa00-03f14749eb61"));

            Cmd("DisableHibernation",
                "powercfg -h off", "powercfg -h on",
                () => { try { return !File.Exists(@"C:\hiberfil.sys"); } catch { return false; } });

            Cmd("DisableDynamicTicketing",
                "bcdedit /set disabledynamictick yes",
                "bcdedit /set disabledynamictick no",
                () =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("bcdedit", "/enum {current}")
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p == null) return false;
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        return output.IndexOf("disabledynamictick",
                                   StringComparison.OrdinalIgnoreCase) >= 0
                               && output.IndexOf("Yes",
                                   StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                }, restart: true);

            Cmd("RemoveOneDrive",
                @"Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue;
                  if (Test-Path ""$env:SystemRoot\SysWOW64\OneDriveSetup.exe"") {
                      Start-Process ""$env:SystemRoot\SysWOW64\OneDriveSetup.exe"" -ArgumentList '/uninstall' -Wait
                  }",
                "",
                () => !File.Exists(Environment.ExpandEnvironmentVariables(
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe")),
                dangerous: true);

            Cmd("DebloatWindows",
                "Get-AppxPackage -AllUsers | Where-Object {$_.Name -like '*Bing*' -or $_.Name -like '*Xbox*' -or $_.Name -like '*GetStarted*' -or $_.Name -like '*ZuneMusic*'} | Remove-AppxPackage -ErrorAction SilentlyContinue",
                "",
                () =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("powershell",
                            "-NoProfile -Command \"Get-AppxPackage -Name '*BingWeather*','*XboxApp*','*GetStarted*','*ZuneMusic*' | Measure-Object\"")
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p == null) return true;
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(10000);
                        return output.IndexOf("Count : 0",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                });

            Cmd("DisableInsiderTasks",
                @"Get-ScheduledTask -TaskPath '\Microsoft\Windows\Feedback\Siuf\' | Disable-ScheduledTask -ErrorAction SilentlyContinue",
                @"Get-ScheduledTask -TaskPath '\Microsoft\Windows\Feedback\Siuf\' | Enable-ScheduledTask -ErrorAction SilentlyContinue",
                () => false);

            Cmd("SetPowerShell7",
                "if (!(Get-Command pwsh -ErrorAction SilentlyContinue)) { winget install --id Microsoft.PowerShell -e --accept-source-agreements --accept-package-agreements }",
                "",
                () => File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe"));

            Cmd("SetServicesManual",
                "Get-Service | Where-Object {$_.StartType -eq 'Automatic'} | Set-Service -StartupType Manual -ErrorAction SilentlyContinue",
                "",
                () =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("powershell",
                            "-NoProfile -Command \"(Get-Service | Where-Object {$_.StartType -eq 'Automatic'} | Measure-Object).Count\"")
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p == null) return false;
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(10000);
                        return int.TryParse(output.Trim(), out int c) && c < 10;
                    }
                    catch { return false; }
                }, dangerous: true);

            // ── Aliases ──
            AddAlias(d, "DisableBingSearch", "RemoveBingIntegration");
            AddAlias(d, "TaskbarAlignLeft", "AlignTaskbarLeft");
            AddAlias(d, "DarkThemeApps", "DarkTaskbar");
            AddAlias(d, "DisableWinTelemetry", "DisableTelemetry");

            TweakLogger.Info($"✅ Built {d.Count} tweaks");
            return d;
        }

        private static void AddAlias(Dictionary<string, ITweak> dict,
            string aliasId, string originalId)
        {
            if (dict.ContainsKey(aliasId)) return;
            if (!dict.TryGetValue(originalId, out var original)) return;

            if (original is RegistryTweak rt)
            {
                dict[aliasId] = new RegistryTweak
                {
                    Id = aliasId,
                    Path = rt.Path,
                    ValueName = rt.ValueName,
                    EnabledValue = rt.EnabledValue,
                    DisabledValue = rt.DisabledValue,
                    Kind = rt.Kind,
                    IsDangerous = rt.IsDangerous,
                    RequiresRestart = rt.RequiresRestart
                };
            }
            else dict[aliasId] = original;
        }

        private static bool CheckPowerScheme(string guid)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return false; }
                return output.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }

    #endregion


    #region ==================== FAST STATE READER ====================

    public static class FastStateReader
    {
        public static Dictionary<string, bool> ReadAllStates(IEnumerable<string> tweakIds)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ids = tweakIds.ToList();

            var registryByPath = new Dictionary<string,
                List<(string id, RegistryTweak t)>>(StringComparer.OrdinalIgnoreCase);
            var serviceTweaks = new List<(string id, ServiceTweak t)>();
            var commandTweaks = new List<(string id, CommandTweak t)>();

            foreach (var id in ids)
            {
                if (!TweakCatalog.All.TryGetValue(id, out var tweak))
                { result[id] = false; continue; }

                switch (tweak)
                {
                    case RegistryTweak rt:
                        if (!registryByPath.TryGetValue(rt.Path, out var list))
                        { list = new List<(string, RegistryTweak)>(); registryByPath[rt.Path] = list; }
                        list.Add((id, rt));
                        break;
                    case ServiceTweak st: serviceTweaks.Add((id, st)); break;
                    case CommandTweak ct: commandTweaks.Add((id, ct)); break;
                }
            }

            // Registry - one key open per path
            foreach (var kvp in registryByPath)
            {
                RegistryKey key = null;
                try
                {
                    var (hive, keyPath) = RegistryHelper.SplitPath(kvp.Key);
                    key = RegistryHelper.GetBaseKey(hive).OpenSubKey(keyPath);

                    foreach (var (id, rt) in kvp.Value)
                    {
                        bool applied = false;
                        try
                        {
                            if (key != null)
                            {
                                string name = (rt.ValueName == "(Default)"
                                    || rt.ValueName == "(default)") ? "" : rt.ValueName;
                                var val = key.GetValue(name);
                                applied = CompareValues(val, rt.EnabledValue);
                            }
                        }
                        catch { }
                        result[id] = applied;
                    }
                }
                catch { foreach (var (id, _) in kvp.Value) result[id] = false; }
                finally { key?.Dispose(); }
            }

            // Services
            foreach (var (id, st) in serviceTweaks)
            {
                try { result[id] = st.IsApplied(); }
                catch { result[id] = false; }
            }

            // CommandTweaks - saved state only (no process launch)
            foreach (var (id, _) in commandTweaks)
            {
                result[id] = UserPreferenceManager.HasState(id)
                             && UserPreferenceManager.GetState(id);
            }

            return result;
        }

        private static bool CompareValues(object actual, object expected)
        {
            if (actual == null) return expected == null;
            if (expected == null) return false;
            if (int.TryParse(actual.ToString(), out int a)
                && int.TryParse(expected.ToString(), out int e))
                return a == e;
            return actual.ToString().Trim()
                .Equals(expected.ToString().Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion


    #region ==================== UI CONTROLLER: TWEAKS PAGE ====================

    public partial class TweaksPage : Page
    {
        private bool _isDarkMode;
        private int _enabledTweaksCount = 0;

        private readonly Dictionary<string, CheckBox> _tweakCheckboxes =
            new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _processingTags = new HashSet<string>();
        private bool _isInitializing = true;

        public static bool NeedsExplorerRestart { get; set; } = false;

        private readonly Dictionary<string, DateTime> _lastApplied =
            new Dictionary<string, DateTime>();
        private static readonly TimeSpan RateLimitWindow =
            TimeSpan.FromMilliseconds(800);

        private static (bool, string, int, bool)? _cachedActivation;
        private static DateTime _activationCacheTime;

        public TweaksPage()
        {
            InitializeComponent();
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ──────────────────────────────────────────────
        //  LOADED  (Progressive Loading)
        // ──────────────────────────────────────────────
        private async void TweaksPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _isDarkMode = ThemeManager.IsDarkMode;
                ThemeManager.ThemeChanged += OnThemeChanged;
                App.LanguageChanged += OnLanguageChanged;
                ShowLoadingPlaceholders();

                // 1. Register checkboxes on UI thread (fast)
                RegisterAllTweaks();

                // 2. Snapshot tags for background use
                var tagsSnapshot = _tweakCheckboxes.Keys.ToList();

                // 3. Load checkbox states in background
                var states = await Task.Run(() =>
                    FastStateReader.ReadAllStates(tagsSnapshot));

                ApplyStatesToCheckboxes(states);
                UpdateTweaksStats();
                _isInitializing = false;

                // 4. Windows activation (fire & forget)
                _ = Task.Run(() =>
                {
                    try
                    {
                        var activation = CheckWindowsActivationSafe();
                        Dispatcher.BeginInvoke(new Action(() =>
                            UpdateActivationStatusUI(activation)),
                            DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    { TweakLogger.Error($"Activation bg: {ex.Message}"); }
                });

                // 5. Process / Service counts (fire & forget)
                _ = Task.Run(() =>
                {
                    try
                    {
                        var counts = GetProcServCountsSafe();
                        Dispatcher.BeginInvoke(new Action(() =>
                            UpdateProcessesCountUI(counts.Item1, counts.Item2)),
                            DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    { TweakLogger.Error($"Counts bg: {ex.Message}"); }
                });

                // 6. Background refresh for CommandTweaks actual state
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cmdIds = tagsSnapshot
                            .Where(id => TweakCatalog.All.TryGetValue(id, out var t)
                                         && t is CommandTweak)
                            .ToList();

                        foreach (var id in cmdIds)
                        {
                            if (!TweakCatalog.All.TryGetValue(id, out var t)) continue;
                            if (!(t is CommandTweak ct)) continue;
                            try
                            {
                                bool applied = ct.IsApplied();
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (_tweakCheckboxes.TryGetValue(id, out var cb))
                                        cb.IsChecked = applied;
                                }, DispatcherPriority.Background);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    { TweakLogger.Error($"CommandTweaks refresh: {ex.Message}"); }
                });

                // 7. Admin warning last
                if (!IsRunningAsAdmin())
                    _ = ShowAdminWarningAsync();
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"TweaksPage_Loaded: {ex}");
                _isInitializing = false;
            }
        }

        private async Task ShowAdminWarningAsync()
        {
            await Task.Delay(1200);
            await ModernMessageBox.Show(this,
                ResourceLoader.GetString("AdminRequired",
                    "Administrator privileges required"),
                ResourceLoader.GetString("AdminRequiredMsg",
                    "Application requires Administrator privileges to apply certain tweaks."),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void TweaksPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ThemeManager.ThemeChanged -= OnThemeChanged;
                App.LanguageChanged -= OnLanguageChanged;
            }
            catch (Exception ex) { TweakLogger.Error($"Unloaded: {ex.Message}"); }
        }

        // ──────────────────────────────────────────────
        //  REGISTER CHECKBOXES
        // ──────────────────────────────────────────────
        private void RegisterAllTweaks()
        {
            var allTabs = new ScrollViewer[]
            {
                contentGeneral, contentGaming, contentUI,
                contentServices, contentPrivacy, contentNetwork
            };

            int unmapped = 0;
            foreach (var container in allTabs)
            {
                if (container == null) continue;
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(container);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var child in LogicalTreeHelper
                        .GetChildren(current).OfType<DependencyObject>())
                    {
                        if (child is CheckBox cb)
                        {
                            string tag = cb.Tag?.ToString();
                            if (!string.IsNullOrEmpty(tag))
                            {
                                _tweakCheckboxes[tag] = cb;
                                if (!TweakCatalog.All.ContainsKey(tag)) unmapped++;

                                cb.Checked -= Tweak_Checked;
                                cb.Unchecked -= Tweak_Unchecked;
                                cb.PreviewMouseLeftButtonDown -= Toggle_PreviewClick;
                                cb.PreviewMouseLeftButtonDown += Toggle_PreviewClick;
                            }
                        }
                        queue.Enqueue(child);
                    }
                }
            }
            TweakLogger.Info(
                $"✅ Registered {_tweakCheckboxes.Count} checkboxes ({unmapped} unmapped)");
        }

        private void ApplyStatesToCheckboxes(Dictionary<string, bool> states)
        {
            foreach (var kvp in states)
            {
                if (!_tweakCheckboxes.TryGetValue(kvp.Key, out var cb)) continue;
                cb.IsChecked = kvp.Value;
            }
        }

        private bool IsRateLimited(string tag)
        {
            if (_lastApplied.TryGetValue(tag, out var lastTime)
                && (DateTime.Now - lastTime) < RateLimitWindow)
                return true;
            _lastApplied[tag] = DateTime.Now;
            return false;
        }

        // ──────────────────────────────────────────────
        //  TOGGLE HANDLER
        // ──────────────────────────────────────────────
        private async void Toggle_PreviewClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var cb = sender as CheckBox;
            string tag = cb?.Tag?.ToString();
            if (string.IsNullOrEmpty(tag) || _isInitializing) return;
            if (_processingTags.Contains(tag)) return;
            if (IsRateLimited(tag)) { TweakLogger.Warn($"Rate limited: {tag}"); return; }

            bool currentState = cb.IsChecked == true;
            bool targetState = !currentState;

            if (!TweakCatalog.All.TryGetValue(tag, out var tweak))
            {
                TweakLogger.Warn($"No definition: {tag}");
                return;
            }

            // Dangerous confirmation
            if (targetState && tweak.IsDangerous)
            {
                var confirm = await ModernMessageBox.Show(this,
                    ResourceLoader.GetString("WarningTitle", "Warning"),
                    ResourceLoader.GetString("DangerousTweak",
                        "This modification is potentially dangerous. Continue?"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;
            }

            SetToggleState(cb, targetState);
            _processingTags.Add(tag);
            cb.IsEnabled = false;

            try
            {
                TweakResult result;

                if (targetState)
                {
                    result = (tweak is ServiceTweak || tweak is CommandTweak)
                        ? await Task.Run(async () => await tweak.ApplyAsync())
                        : await tweak.ApplyAsync();

                    if (result.Success)
                    {
                        UserPreferenceManager.SetState(tag, true);
                        TweakLogger.Info($"Applied: {tag}");

                        if (result.RequiresExplorerRestart) NeedsExplorerRestart = true;
                        if (result.RequiresRestart)
                        {
                            await ModernMessageBox.Show(this,
                                ResourceLoader.GetString("RestartTitle", "Restart required"),
                                ResourceLoader.GetString("RestartMsg",
                                    "This modification requires a system restart."),
                                MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    else
                    {
                        SetToggleState(cb, false);
                        await ModernMessageBox.Show(this,
                            ResourceLoader.GetString("Error", "Error"),
                            result.Message ?? ResourceLoader.GetString("ApplyFailed",
                                "Failed to apply modification"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    result = (tweak is ServiceTweak || tweak is CommandTweak)
                        ? await Task.Run(async () => await tweak.RevertAsync())
                        : await tweak.RevertAsync();

                    if (result.Success)
                    {
                        UserPreferenceManager.RemoveState(tag);
                        TweakLogger.Info($"Reverted: {tag}");

                        if (result.RequiresExplorerRestart) NeedsExplorerRestart = true;
                    }
                    else
                    {
                        SetToggleState(cb, true);
                        await ModernMessageBox.Show(this,
                            ResourceLoader.GetString("Error", "Error"),
                            result.Message ?? ResourceLoader.GetString("RevertFailed",
                                "Failed to revert modification"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"Toggle {tag}: {ex.Message}");
                SetToggleState(cb, currentState);
            }
            finally
            {
                _processingTags.Remove(tag);
                cb.IsEnabled = true;
                UpdateTweaksStats();

                if (NeedsExplorerRestart && !_isInitializing)
                {
                    NeedsExplorerRestart = false;
                    _ = PromptExplorerRestartAsync();
                }
            }
        }

        private void SetToggleState(CheckBox cb, bool value) => cb.IsChecked = value;

        private async Task PromptExplorerRestartAsync()
        {
            await Task.Delay(200);
            var result = await ModernMessageBox.Show(this,
                ResourceLoader.GetString("ExplorerRestartTitle", "Restart Explorer"),
                ResourceLoader.GetString("ExplorerRestartMsg",
                    "Some changes require restarting Windows Explorer. Restart now?"),
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                await Task.Run(() => ExplorerManager.RestartExplorer());
        }

        // Kept for XAML compatibility
        private void Tweak_Checked(object sender, RoutedEventArgs e) { }
        private void Tweak_Unchecked(object sender, RoutedEventArgs e) { }

        // ──────────────────────────────────────────────
        //  UI HELPERS
        // ──────────────────────────────────────────────
        private void ShowLoadingPlaceholders()
        {
            if (ActivationDot != null)
                ActivationDot.Fill = (Brush)FindResource("DynamicBorder");
            if (lblActivationStatus != null)
                lblActivationStatus.Text = "...";
            if (lblActivationDate != null)
            {
                lblActivationDate.Text = ResourceLoader.GetString("Loading", "Loading...");
                lblActivationDate.Foreground = (Brush)FindResource("DynamicSubText");
            }
            if (btnActivateWindows != null)
                btnActivateWindows.IsEnabled = false;
            if (lblProcServCount != null)
                lblProcServCount.Text = "... / ...";
            if (lblProcServDesc != null)
                lblProcServDesc.Text = ResourceLoader.GetString("Loading", "Loading...");
        }

        private void UpdateTweaksStats()
        {
            _enabledTweaksCount = _tweakCheckboxes.Count(kvp => kvp.Value.IsChecked == true);
            if (lblTweaksCount != null)
                lblTweaksCount.Text = $"{_enabledTweaksCount} / {_tweakCheckboxes.Count}";
        }

        // ──────────────────────────────────────────────
        //  WINDOWS ACTIVATION
        // ──────────────────────────────────────────────
        private (bool, string, int, bool) CheckWindowsActivationSafe()
        {
            if (_cachedActivation.HasValue
                && (DateTime.Now - _activationCacheTime).TotalMinutes < 5)
                return _cachedActivation.Value;

            try
            {
                var result = CheckWindowsActivationWMIDirect();
                if (result.HasValue)
                {
                    _cachedActivation = result.Value;
                    _activationCacheTime = DateTime.Now;
                    return result.Value;
                }
            }
            catch (Exception ex)
            { TweakLogger.Warn($"WMI failed, using fallback: {ex.Message}"); }

            var fallback = GetActivationFromRegistry();
            _cachedActivation = fallback;
            _activationCacheTime = DateTime.Now;
            return fallback;
        }

        private (bool, string, int, bool)? CheckWindowsActivationWMIDirect()
        {
            try
            {
                var options = new EnumerationOptions
                {
                    ReturnImmediately = true,
                    Rewindable = false,
                    Timeout = TimeSpan.FromSeconds(5)
                };

                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Options.Timeout = TimeSpan.FromSeconds(5);
                scope.Connect();
                if (!scope.IsConnected) return null;

                var query = new ObjectQuery(
                    "SELECT LicenseStatus, GracePeriodRemaining " +
                    "FROM SoftwareLicensingProduct " +
                    "WHERE PartialProductKey IS NOT NULL AND " +
                    "ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'");

                using var searcher = new ManagementObjectSearcher(scope, query, options);
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        uint status = Convert.ToUInt32(obj["LicenseStatus"] ?? 0);
                        uint grace = Convert.ToUInt32(obj["GracePeriodRemaining"] ?? 0);
                        bool active = status == 1;

                        if (!active) return (false, "", 0, false);
                        if (grace == 0) return (true, "", 0, true);

                        int days = (int)(grace / 60 / 24);
                        var expiry = DateTime.Now.AddMinutes(grace);
                        return (true, expiry.ToShortDateString(), days, false);
                    }
                }
                return (false, "", 0, false);
            }
            catch (Exception ex)
            {
                TweakLogger.Error($"WMI direct: {ex.Message}");
                return null;
            }
        }

        private (bool, string, int, bool) GetActivationFromRegistry()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform");
                var status = key?.GetValue("SkipRearm")?.ToString();
                bool likelyActive = status != "1";
                return (likelyActive, "", 0, likelyActive);
            }
            catch { return (false, "", 0, false); }
        }

        private void UpdateActivationStatusUI(
            (bool IsActivated, string ExpiryDate, int DaysRemaining, bool IsPermanent) info)
        {
            var (isActivated, expiryDate, daysRemaining, isPermanent) = info;

            if (ActivationDot != null)
                ActivationDot.Fill = isActivated
                    ? (Brush)FindResource("DynamicSuccess")
                    : (Brush)FindResource("DynamicWarning");

            if (lblActivationStatus != null)
                lblActivationStatus.Text = isActivated
                    ? ResourceLoader.GetString("Activated", "Activated")
                    : ResourceLoader.GetString("NotActivated", "Not Activated");

            if (lblActivationDate != null)
            {
                if (!isActivated)
                {
                    lblActivationDate.Text =
                        ResourceLoader.GetString("NotActivatedMsg", "Windows is not activated");
                    lblActivationDate.Foreground = (Brush)FindResource("DynamicError");
                }
                else if (isPermanent)
                {
                    lblActivationDate.Text =
                        ResourceLoader.GetString("Permanent", "Permanent activation");
                    lblActivationDate.Foreground = (Brush)FindResource("DynamicSuccess");
                }
                else if (daysRemaining > 0)
                {
                    lblActivationDate.Text = string.Format(
                        ResourceLoader.GetString("DaysRemaining", "{0} days remaining"),
                        daysRemaining);
                    lblActivationDate.Foreground = daysRemaining <= 7
                        ? (Brush)FindResource("DynamicError")
                        : daysRemaining <= 30
                            ? (Brush)FindResource("DynamicWarning")
                            : (Brush)FindResource("DynamicSuccess");
                }
                else
                {
                    lblActivationDate.Text = string.Format(
                        ResourceLoader.GetString("ExpiresOn", "Expires on: {0}"), expiryDate);
                    lblActivationDate.Foreground = (Brush)FindResource("DynamicError");
                }
            }

            if (btnActivateWindows != null)
                btnActivateWindows.IsEnabled = !isActivated;
        }

        // ──────────────────────────────────────────────
        //  PROCESS / SERVICE COUNTS
        // ──────────────────────────────────────────────
        private (int, int) GetProcServCountsSafe()
        {
            int procCount = 0, servCount = 0;
            try { procCount = Process.GetProcesses().Length; }
            catch (Exception ex) { TweakLogger.Error($"ProcCount: {ex.Message}"); }
            try
            {
                servCount = ServiceController.GetServices()
                    .Count(s => s.Status == ServiceControllerStatus.Running);
            }
            catch (Exception ex) { TweakLogger.Error($"ServCount: {ex.Message}"); }
            return (procCount, servCount);
        }

        private void UpdateProcessesCountUI(int procCount, int servCount)
        {
            if (lblProcServCount != null)
                lblProcServCount.Text = $"{procCount} / {servCount}";
            if (lblProcServDesc != null)
                lblProcServDesc.Text =
                    $"{procCount} active processes / {servCount} active services";
        }

        // ──────────────────────────────────────────────
        //  TAB SWITCHING
        // ──────────────────────────────────────────────
        private void TabGeneral_Checked(object sender, RoutedEventArgs e) => SwitchTab(contentGeneral);
        private void TabGaming_Checked(object sender, RoutedEventArgs e) => SwitchTab(contentGaming);
        private void TabUI_Checked(object sender, RoutedEventArgs e) => SwitchTab(contentUI);
        private void TabServices_Checked(object sender, RoutedEventArgs e) => SwitchTab(contentServices);
        private void TabPrivacy_Checked(object sender, RoutedEventArgs e) => SwitchTab(contentPrivacy);
        private void TabNetwork_Checked(object sender, RoutedEventArgs e) => SwitchTab(contentNetwork);

        private void SwitchTab(ScrollViewer targetTab)
        {
            var tabs = new[]
            {
                contentGeneral, contentGaming, contentUI,
                contentServices, contentPrivacy, contentNetwork
            };
            foreach (var tab in tabs)
                if (tab != null) tab.Visibility = Visibility.Collapsed;
            if (targetTab != null)
                targetTab.Visibility = Visibility.Visible;
        }

        // ──────────────────────────────────────────────
        //  MISC HANDLERS
        // ──────────────────────────────────────────────
        private async void btnActivateWindows_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ModernMessageBox.Show(this,
                    ResourceLoader.GetString("ActivationTitle", "Activate Windows"),
                    ResourceLoader.GetString("ActivationMessage", "Opening activation tool..."),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { TweakLogger.Error($"Activate click: {ex.Message}"); }
        }

        private void TweaksCountUpdated(object sender, RoutedEventArgs e) { }

        private void OnThemeChanged(bool isDark) =>
            Dispatcher.Invoke(() => { _isDarkMode = isDark; });

        private void OnLanguageChanged(string langCode)
        {
            ResourceLoader.ClearCache();
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var info = await Task.Run(() => CheckWindowsActivationSafe());
                    UpdateActivationStatusUI(info);
                }
                catch (Exception ex)
                { TweakLogger.Error($"LanguageChanged: {ex.Message}"); }
            });
        }
    }

    #endregion


    #region ==================== MODERN MESSAGE BOX ====================

    public static class ModernMessageBox
    {
        private static string GetLocalizedString(DependencyObject owner,
            string resourceKey, string fallback)
        {
            try
            {
                if (owner is FrameworkElement fe
                    && fe.FindResource(resourceKey) is string s) return s;
                if (Application.Current?.Resources.Contains(resourceKey) == true
                    && Application.Current.Resources[resourceKey] is string s2) return s2;
            }
            catch { }
            return fallback ?? resourceKey;
        }

        public static async Task<MessageBoxResult> Show(
            DependencyObject owner,
            string title,
            string message,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.Information)
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>();

            double parentOpacity = 1.0;
            if (owner is FrameworkElement fe2
                && Window.GetWindow(fe2) is Window pw)
                parentOpacity = pw.Opacity;

            var window = new Window
            {
                Width = 450,
                Height = 260,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            Brush GetRes(string key, Brush fallback)
            {
                try
                {
                    if (Application.Current?.Resources.Contains(key) == true
                        && Application.Current.Resources[key] is Brush b) return b;
                    if (owner is FrameworkElement fe3
                        && fe3.TryFindResource(key) is Brush b2) return b2;
                }
                catch { }
                return fallback;
            }

            Brush mainBg = GetRes("DynamicCardBg", Brushes.White);
            Brush borderBrush = GetRes("DynamicBorderBrush",
                new SolidColorBrush(Color.FromRgb(220, 220, 220)));
            Brush mainText = GetRes("DynamicMainText", Brushes.Black);
            Brush subText = GetRes("DynamicSubText",
                new SolidColorBrush(Color.FromRgb(90, 90, 90)));
            Brush accentColor = GetRes("DynamicAccent",
                new SolidColorBrush(Color.FromRgb(0, 120, 212)));

            string iconChar = icon switch
            {
                MessageBoxImage.Warning => "\uE7BA",
                MessageBoxImage.Information => "\uE946",
                MessageBoxImage.Error => "\uEB90",
                MessageBoxImage.Question => "\uE897",
                _ => "\uE946"
            };

            Color iconColorValue = icon switch
            {
                MessageBoxImage.Warning => Color.FromRgb(255, 193, 7),
                MessageBoxImage.Error => Color.FromRgb(220, 53, 69),
                MessageBoxImage.Question => Color.FromRgb(0, 192, 192),
                _ => ((SolidColorBrush)accentColor)?.Color
                     ?? Color.FromRgb(0, 120, 212)
            };

            Brush iconColorBrush = new SolidColorBrush(iconColorValue);
            Brush lightCircleBrush = new SolidColorBrush(
                Color.FromArgb(38, iconColorValue.R, iconColorValue.G, iconColorValue.B));

            var backgroundBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            backgroundBrush.GradientStops.Add(new GradientStop(iconColorValue, 0));
            backgroundBrush.GradientStops.Add(new GradientStop(iconColorValue, 0.023));
            backgroundBrush.GradientStops.Add(new GradientStop(
                ((SolidColorBrush)mainBg).Color, 0.0231));
            backgroundBrush.GradientStops.Add(new GradientStop(
                ((SolidColorBrush)mainBg).Color, 1));

            var border = new Border
            {
                Background = backgroundBrush,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                BorderBrush = borderBrush,
                ClipToBounds = true
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var iconBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(32),
                Background = lightCircleBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 12)
            };
            iconBorder.Child = new TextBlock
            {
                Text = iconChar,
                FontSize = 32,
                Foreground = iconColorBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe MDL2 Assets")
            };
            Grid.SetRow(iconBorder, 0);

            var contentPanel = new StackPanel
            { Margin = new Thickness(30, 0, 30, 20) };
            contentPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = mainText,
                Margin = new Thickness(0, 0, 0, 8),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            contentPanel.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = subText,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            Grid.SetRow(contentPanel, 1);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };

            string txtOK = GetLocalizedString(owner, "OK", "OK");
            string txtCancel = GetLocalizedString(owner, "Cancel", "Cancel");
            string txtYes = GetLocalizedString(owner, "Yes", "Yes");
            string txtNo = GetLocalizedString(owner, "No", "No");

            void AddBtn(string text, MessageBoxResult result,
                bool outline = false)
            {
                var btn = CreateModernButton(text, icon,
                    outline ? mainText : iconColorBrush,
                    borderBrush, mainText, outline);
                btn.Click += (s, ev) =>
                {
                    tcs.TrySetResult(result);
                    window.Close();
                };
                buttonPanel.Children.Add(btn);
            }

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    AddBtn(txtOK, MessageBoxResult.OK);
                    break;
                case MessageBoxButton.OKCancel:
                    AddBtn(txtCancel, MessageBoxResult.Cancel, true);
                    AddBtn(txtOK, MessageBoxResult.OK);
                    break;
                case MessageBoxButton.YesNo:
                    AddBtn(txtNo, MessageBoxResult.No, true);
                    AddBtn(txtYes, MessageBoxResult.Yes);
                    break;
                case MessageBoxButton.YesNoCancel:
                    AddBtn(txtCancel, MessageBoxResult.Cancel, true);
                    AddBtn(txtNo, MessageBoxResult.No, true);
                    AddBtn(txtYes, MessageBoxResult.Yes);
                    break;
            }

            Grid.SetRow(buttonPanel, 2);
            mainGrid.Children.Add(iconBorder);
            mainGrid.Children.Add(contentPanel);
            mainGrid.Children.Add(buttonPanel);
            border.Child = mainGrid;
            window.Content = border;

            window.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                DependencyObject src = ev.OriginalSource as DependencyObject;
                bool isBtn = false;
                while (src != null && src != window)
                {
                    if (src is Button) { isBtn = true; break; }
                    src = VisualTreeHelper.GetParent(src);
                }
                if (!isBtn && window.WindowState == WindowState.Normal)
                    window.DragMove();
            };

            window.Cursor = Cursors.Arrow;
            window.Loaded += (s, ev) =>
            {
                var anim = new DoubleAnimation(0, parentOpacity,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                window.BeginAnimation(Window.OpacityProperty, anim);
            };

            window.Closed += (s, ev) => tcs.TrySetResult(MessageBoxResult.None);
            window.ShowDialog();
            return await tcs.Task;
        }

        private static Button CreateModernButton(
            string text, MessageBoxImage iconType,
            Brush iconColor, Brush borderBrush,
            Brush hoverForeground, bool isOutline = false)
        {
            Brush buttonBackground = iconType switch
            {
                MessageBoxImage.Warning => new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                MessageBoxImage.Error => new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                MessageBoxImage.Question => new SolidColorBrush(Color.FromRgb(0, 192, 192)),
                MessageBoxImage.Information => new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                _ => iconColor
            };

            var button = new Button
            {
                Content = text,
                Width = 100,
                Height = 38,
                Margin = new Thickness(8, 0, 8, 0),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Padding = new Thickness(0)
            };

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetValue(Border.BorderThicknessProperty,
                isOutline ? new Thickness(1.5) : new Thickness(0));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cp);
            template.VisualTree = borderFactory;
            button.Template = template;

            if (isOutline)
            {
                button.Background = Brushes.Transparent;
                button.Foreground = hoverForeground;
                button.BorderBrush = buttonBackground;
                button.MouseEnter += (s, e) =>
                {
                    if (buttonBackground is SolidColorBrush solid)
                        button.Background = new SolidColorBrush(Color.FromArgb(
                            35, solid.Color.R, solid.Color.G, solid.Color.B));
                };
                button.MouseLeave += (s, e) => button.Background = Brushes.Transparent;
            }
            else
            {
                button.Background = buttonBackground;
                button.Foreground = Brushes.White;
                button.BorderBrush = buttonBackground;
                button.MouseEnter += (s, e) =>
                {
                    if (buttonBackground is SolidColorBrush solid)
                        button.Background = new SolidColorBrush(Color.FromRgb(
                            (byte)(solid.Color.R * 0.88),
                            (byte)(solid.Color.G * 0.88),
                            (byte)(solid.Color.B * 0.88)));
                };
                button.MouseLeave += (s, e) => button.Background = buttonBackground;
            }

            button.PreviewMouseLeftButtonDown += (s, e) => button.Opacity = 0.85;
            button.PreviewMouseLeftButtonUp += (s, e) => button.Opacity = 1.0;
            return button;
        }
    }

    #endregion
}