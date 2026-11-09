using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Lindo.Core;
using Microsoft.Win32;

namespace Lindo.Services
{
    /// <summary>
    /// Raccoglie tutti gli indizi che una cartella è ancora usata: PATH e variabili d'ambiente (npm, Python…),
    /// servizi, app all'avvio, App Paths, programmi in esecuzione, collegamenti, dati del monitor.
    /// </summary>
    internal static class EvidenceService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        public static OrphanEvidence Collect(int recentDays = 90)
        {
            var ev = new OrphanEvidence { RecentDays = recentDays, NewestActivity = NewestWrite };

            // Variabili d'ambiente (utente e sistema): PATH, ANDROID_HOME, JAVA_HOME, NVM_HOME…
            foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                try
                {
                    foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables(target))
                    {
                        var name = kv.Key?.ToString() ?? "";
                        var value = kv.Value?.ToString() ?? "";
                        if (name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) || name.Equals("TMP", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var part in value.Split(';'))
                            if (part.Length > 3 && part[1] == ':') ev.Add(part, "Variabile d'ambiente " + name);
                    }
                }
                catch { }
            }

            // Servizi (anche disattivati: un servizio installato vuol dire programma presente)
            try
            {
                using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                    foreach (var n in services?.GetSubKeyNames() ?? new string[0])
                        try
                        {
                            using (var s = services.OpenSubKey(n))
                            {
                                var img = s?.GetValue("ImagePath")?.ToString();
                                if (!string.IsNullOrEmpty(img)) ev.Add(WinPath.ExtractExecutable(Environment.ExpandEnvironmentVariables(img)), "Servizio " + n);
                            }
                        }
                        catch { }
            }
            catch { }

            // App all'avvio, App Paths, gestori di protocolli comuni
            foreach (var (hive, key) in new[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            })
            {
                try
                {
                    using (var k = hive.OpenSubKey(key))
                        foreach (var v in k?.GetValueNames() ?? new string[0])
                            ev.Add(WinPath.ExtractExecutable(Environment.ExpandEnvironmentVariables(k.GetValue(v)?.ToString() ?? "")), "Avvio automatico");
                }
                catch { }
            }
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var ap = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                        foreach (var n in ap?.GetSubKeyNames() ?? new string[0])
                            using (var k = ap.OpenSubKey(n))
                            {
                                ev.Add(WinPath.ExtractExecutable(k?.GetValue("")?.ToString() ?? ""), "Registrato in App Paths");
                                ev.Add(k?.GetValue("Path")?.ToString() ?? "", "Registrato in App Paths");
                            }
                }
                catch { }
            }

            // Programmi in esecuzione adesso
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var h = OpenProcess(0x1000, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref size)) ev.Add(sb.ToString(0, size), "In esecuzione: " + p.ProcessName);
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
                finally { p.Dispose(); }
            }

            // Collegamenti nel menu Start, sul Desktop e nella barra delle applicazioni
            var roots = SystemInfo.Roots;
            var taskbar = Path.Combine(roots.RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            foreach (var dir in new[] { roots.UserStartPrograms, roots.CommonStartPrograms, roots.UserDesktop, roots.PublicDesktop, taskbar })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).Take(800))
                        ev.Add(ShortcutResolver.Resolve(lnk), "Collegamento " + Path.GetFileNameWithoutExtension(lnk));
                }
                catch { }
            }

            // Programmi visti dal monitor di Lindo negli ultimi N giorni
            var store = UsageService.LoadStore();
            foreach (var r in store.Records.Values.Where(r => DateTime.Now - r.LastSeen < TimeSpan.FromDays(recentDays)))
                ev.Add(r.Path, "Usato di recente (" + TimeText.Ago(r.LastSeen, DateTime.Now) + ")");

            Log.Write("Indizi d'uso raccolti: " + ev.InUsePaths.Count);
            return ev;
        }

        /// <summary>Data più recente di modifica di un file nella cartella (max 20.000 elementi esaminati).</summary>
        public static DateTime? NewestWrite(string dir)
        {
            DateTime? newest = null;
            var stack = new Stack<DirectoryInfo>();
            try { stack.Push(new DirectoryInfo(dir)); } catch { return null; }
            int seen = 0;
            while (stack.Count > 0 && seen < 20000)
            {
                var d = stack.Pop();
                try
                {
                    if (!newest.HasValue || d.LastWriteTime > newest) newest = d.LastWriteTime;
                    foreach (var e in d.EnumerateFileSystemInfos())
                    {
                        seen++;
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var t = e.LastWriteTime;
                        if (!newest.HasValue || t > newest) newest = t;
                        if (e is DirectoryInfo sub) stack.Push(sub);
                    }
                }
                catch { }
            }
            return newest;
        }
    }
}
