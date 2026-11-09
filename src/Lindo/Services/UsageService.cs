using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lindo.Core;
using Microsoft.Win32;

namespace Lindo.Services
{
    /// <summary>Dischi del PC e cartelle dei programmi sugli altri dischi.</summary>
    internal static class Drives
    {
        public static List<DriveInfo> Fixed()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .OrderBy(d => d.Name)
                    .ToList();
            }
            catch { return new List<DriveInfo>(); }
        }

        public static string Letter(string path)
        {
            var p = WinPath.Normalize(path);
            return p.Length >= 2 && p[1] == ':' ? char.ToUpperInvariant(p[0]) + ":" : string.Empty;
        }

        /// <summary>Aggiorna le radici dei programmi sugli altri dischi in base alle app installate.</summary>
        public static void UpdateExtraRoots(IEnumerable<AppInfo> apps)
        {
            var roots = SystemInfo.Roots;
            var system = Letter(SystemInfo.SystemDrive);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Fixed())
            {
                var l = d.Name.Substring(0, 2).ToUpperInvariant();
                if (l == system) continue;
                foreach (var name in new[] { "Program Files", "Program Files (x86)", "Programmi" })
                {
                    var p = Path.Combine(d.Name, name);
                    if (Directory.Exists(p)) set.Add(WinPath.Normalize(p));
                }
            }
            // Cartelle "libreria" sugli altri dischi (es. D:\Giochi contiene app installate)
            foreach (var a in apps ?? Enumerable.Empty<AppInfo>())
            {
                if (a.Source != AppSource.Desktop || string.IsNullOrWhiteSpace(a.InstallLocation)) continue;
                if (Letter(a.InstallLocation) == system || Letter(a.InstallLocation).Length == 0) continue;
                var parent = WinPath.GetParent(a.InstallLocation);
                if (WinPath.Depth(parent) >= 2) set.Add(parent);
            }
            // Le radici non devono essere annidate tra loro
            var list = set.OrderBy(x => x.Length).ToList();
            roots.ExtraProgramRoots = list.Where(x => !list.Any(y => !ReferenceEquals(x, y) && WinPath.IsStrictlyInside(x, y))).ToList();
        }
    }

    /// <summary>Quando è stata usata l'ultima volta ogni app (Prefetch, UserAssist, monitor di Lindo).</summary>
    internal static class UsageService
    {
        public static string StorePath => Path.Combine(SystemInfo.DataFolder, "usage.tsv");
        private const string RunValueName = "Lindo Monitor";

        public static UsageStore LoadStore()
        {
            try { return File.Exists(StorePath) ? UsageStore.Parse(File.ReadAllText(StorePath)) : new UsageStore(); }
            catch { return new UsageStore(); }
        }

        /// <summary>Ultimo avvio per nome di eseguibile, dalla cartella Prefetch (serve l'amministratore).</summary>
        private static Dictionary<string, DateTime> Prefetch()
        {
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.pf"))
                {
                    var name = f.Name;
                    var dash = name.LastIndexOf('-');
                    if (dash <= 0) continue;
                    var exe = name.Substring(0, dash);
                    if (!map.TryGetValue(exe, out var prev) || f.LastWriteTime > prev) map[exe] = f.LastWriteTime;
                }
            }
            catch { }
            return map;
        }

        /// <summary>Percorso (o AppUserModelID) → ultimo avvio dal registro UserAssist.</summary>
        private static Dictionary<string, DateTime> UserAssistData()
        {
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            string Folder(string key)
            {
                switch (key)
                {
                    case "ProgramFiles": return SystemInfo.Roots.ProgramFiles;
                    case "ProgramFilesX86": return SystemInfo.Roots.ProgramFilesX86;
                    case "Windows": return SystemInfo.Roots.Windows;
                    case "System": return Environment.SystemDirectory;
                    case "SystemX86": return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
                    case "LocalAppData": return SystemInfo.Roots.LocalAppData;
                    case "RoamingAppData": return SystemInfo.Roots.RoamingAppData;
                    case "Programs": return SystemInfo.Roots.UserStartPrograms;
                    case "CommonPrograms": return SystemInfo.Roots.CommonStartPrograms;
                    case "Desktop": return SystemInfo.Roots.UserDesktop;
                    case "UserProfile": return SystemInfo.Roots.UserProfile;
                    default: return null;
                }
            }
            try
            {
                using (var ua = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"))
                {
                    if (ua == null) return map;
                    foreach (var guid in ua.GetSubKeyNames())
                    {
                        using (var count = ua.OpenSubKey(guid + @"\Count"))
                        {
                            if (count == null) continue;
                            foreach (var v in count.GetValueNames())
                            {
                                var parsed = UserAssist.ParseValue(count.GetValue(v) as byte[]);
                                if (parsed?.LastRun == null) continue;
                                var name = UserAssist.ExpandKnownFolder(UserAssist.Rot13(v), Folder);
                                if (!map.TryGetValue(name, out var prev) || parsed.Value.LastRun.Value > prev) map[name] = parsed.Value.LastRun.Value;
                            }
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private static readonly string[] IgnoredExe = { "unins", "uninst", "setup", "update", "crash", "helper", "report", "install", "elevat", "service", "notif" };

        /// <summary>Calcola AppInfo.LastUsed per ogni app.</summary>
        public static void FillLastUsed(IEnumerable<AppInfo> apps)
        {
            var prefetch = Prefetch();
            var ua = UserAssistData();
            var store = LoadStore();

            foreach (var app in apps)
            {
                DateTime? best = null;
                void Consider(DateTime? d) { if (d.HasValue && (!best.HasValue || d > best)) best = d; }

                try
                {
                    if (app.Source == AppSource.Driver) continue;

                    var location = app.InstallLocation;
                    if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
                    {
                        var exe = WinPath.ExtractExecutable(app.DisplayIcon);
                        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) location = WinPath.GetParent(exe);
                    }
                    var validLocation = !string.IsNullOrWhiteSpace(location) && WinPath.Depth(location) >= 3 &&
                                        !WinPath.IsSameOrInside(location, SystemInfo.Roots.Windows);

                    if (app.Source == AppSource.Store && !string.IsNullOrEmpty(app.PackageFamilyName))
                    {
                        foreach (var kv in ua)
                            if (kv.Key.StartsWith(app.PackageFamilyName + "!", StringComparison.OrdinalIgnoreCase)) Consider(kv.Value);
                    }

                    if (validLocation)
                    {
                        foreach (var kv in ua)
                            if (WinPath.IsSameOrInside(kv.Key, location)) Consider(kv.Value);
                        foreach (var r in store.Records.Values)
                            if (WinPath.IsSameOrInside(r.Path, location)) Consider(r.LastSeen);

                        foreach (var exeName in ExecutablesIn(location))
                        {
                            var upper = exeName.ToUpperInvariant();
                            if (IgnoredExe.Any(i => upper.ToLowerInvariant().Contains(i))) continue;
                            if (prefetch.TryGetValue(upper, out var d)) Consider(d);
                        }
                    }
                }
                catch { }
                app.LastUsed = best;
            }
        }

        private static IEnumerable<string> ExecutablesIn(string dir)
        {
            var result = new List<string>();
            try
            {
                result.AddRange(Directory.EnumerateFiles(dir, "*.exe").Select(Path.GetFileName));
                foreach (var sub in Directory.EnumerateDirectories(dir).Take(40))
                {
                    try { result.AddRange(Directory.EnumerateFiles(sub, "*.exe").Take(50).Select(Path.GetFileName)); } catch { }
                }
            }
            catch { }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
        }

        // ------------------------------------------------------------------ monitor in background

        public static string MonitorExe => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LindoMonitor.exe");

        public static bool IsMonitorEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return k?.GetValue(RunValueName) != null;
        }

        public static bool IsMonitorRunning() => Process.GetProcessesByName("LindoMonitor").Length > 0;

        public static string SetMonitor(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (on)
                    {
                        if (!File.Exists(MonitorExe)) return "LindoMonitor.exe not found next to Lindo.exe";
                        k.SetValue(RunValueName, "\"" + MonitorExe + "\"");
                        if (!IsMonitorRunning()) Process.Start(new ProcessStartInfo(MonitorExe) { UseShellExecute = true });
                    }
                    else
                    {
                        k.DeleteValue(RunValueName, false);
                        foreach (var p in Process.GetProcessesByName("LindoMonitor")) { try { p.Kill(); } catch { } }
                    }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
