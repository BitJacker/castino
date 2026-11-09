using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lindo.Core;
using Microsoft.Win32;

namespace Lindo.Services
{
    internal enum StartupKind
    {
        Registry,
        StartupFolder,
        Service,
        Task,
    }

    internal sealed class StartupEntry
    {
        public string Id { get; set; }
        public StartupKind Kind { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
        public string ExePath { get; set; }
        public string Publisher { get; set; }
        public bool Enabled { get; set; }
        public bool AllUsers { get; set; }

        // Dettagli per attivare/disattivare
        public RegistryHive Hive { get; set; }
        public string ApprovedKey { get; set; }   // ...\StartupApproved\Run | Run32 | StartupFolder
        public string ApprovedValue { get; set; }
        public string ServiceName { get; set; }
        public bool ServiceDelayed { get; set; }
        public string TaskPath { get; set; }
        public string TaskName { get; set; }

        public bool IsEssential => StartupAdvisor.IsEssential(Name, Publisher, Command);
        public bool Suggested => Kind != StartupKind.Service && StartupAdvisor.SuggestDisable(Name, Publisher, Command);

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case StartupKind.Service: return "Servizio";
                    case StartupKind.Task: return "Attività pianificata";
                    case StartupKind.StartupFolder: return "Cartella Esecuzione automatica";
                    default: return AllUsers ? "Registro (tutti gli utenti)" : "Registro";
                }
            }
        }
    }

    /// <summary>
    /// Programmi che partono con Windows: chiavi Run, cartelle Esecuzione automatica, servizi e attività pianificate
    /// di terze parti. Le voci Run/cartella si attivano e disattivano come fa Gestione attività (StartupApproved).
    /// </summary>
    internal static class StartupService
    {
        private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        private static string BackupFile => Path.Combine(SystemInfo.DataFolder, "servizi-originali.tsv");

        public static List<StartupEntry> Scan()
        {
            var list = new List<StartupEntry>();
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;

            void ReadRun(RegistryHive hive, string runKey, string approvedSub, bool allUsers)
            {
                try
                {
                    using (var bk = RegistryKey.OpenBaseKey(hive, view))
                    using (var k = bk.OpenSubKey(runKey))
                    using (var ap = bk.OpenSubKey(Approved + approvedSub))
                    {
                        if (k == null) return;
                        foreach (var name in k.GetValueNames())
                        {
                            if (string.IsNullOrEmpty(name)) continue;
                            var cmd = k.GetValue(name)?.ToString() ?? "";
                            var exe = Environment.ExpandEnvironmentVariables(WinPath.ExtractExecutable(cmd));
                            list.Add(new StartupEntry
                            {
                                Id = hive + "|" + runKey + "|" + name,
                                Kind = StartupKind.Registry,
                                Name = name,
                                Command = cmd,
                                ExePath = exe,
                                Publisher = Company(exe),
                                Enabled = StartupApproved.IsEnabled(ap?.GetValue(name) as byte[]),
                                AllUsers = allUsers,
                                Hive = hive,
                                ApprovedKey = Approved + approvedSub,
                                ApprovedValue = name,
                            });
                        }
                    }
                }
                catch (Exception ex) { Log.Write("Avvio: " + runKey + " " + ex.Message); }
            }

            ReadRun(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", false);
            ReadRun(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", true);
            ReadRun(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", true);

            // Cartelle Esecuzione automatica
            foreach (var (folder, hive, allUsers) in new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, false),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, true),
            })
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    using (var bk = RegistryKey.OpenBaseKey(hive, view))
                    using (var ap = bk.OpenSubKey(Approved + "StartupFolder"))
                    {
                        foreach (var f in Directory.GetFiles(folder))
                        {
                            var fn = Path.GetFileName(f);
                            if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                            var target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.Resolve(f) ?? f : f;
                            list.Add(new StartupEntry
                            {
                                Id = "folder|" + f,
                                Kind = StartupKind.StartupFolder,
                                Name = Path.GetFileNameWithoutExtension(f),
                                Command = target,
                                ExePath = target,
                                Publisher = Company(target),
                                Enabled = StartupApproved.IsEnabled(ap?.GetValue(fn) as byte[]),
                                AllUsers = allUsers,
                                Hive = hive,
                                ApprovedKey = Approved + "StartupFolder",
                                ApprovedValue = fn,
                            });
                        }
                    }
                }
                catch { }
            }

            list.AddRange(ScanServices());
            list.AddRange(ScanTasks());

            // Mai mostrare Lindo stesso come "da togliere" se non lo vuole l'utente: resta comunque modificabile
            return list.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string Company(string exe)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return string.Empty;
                var vi = FileVersionInfo.GetVersionInfo(exe);
                return (vi.CompanyName ?? string.Empty).Trim();
            }
            catch { return string.Empty; }
        }

        private static string Description(string exe)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
                var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
            }
            catch { return null; }
        }

        /// <summary>Servizi di terze parti con avvio automatico (o messi in manuale da Lindo).</summary>
        private static IEnumerable<StartupEntry> ScanServices()
        {
            var result = new List<StartupEntry>();
            var backup = LoadBackup();
            var windows = SystemInfo.Roots.Windows;
            try
            {
                using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services == null) return result;
                    foreach (var name in services.GetSubKeyNames())
                    {
                        try
                        {
                            using (var s = services.OpenSubKey(name))
                            {
                                if (s == null) continue;
                                var type = Convert.ToInt32(s.GetValue("Type") ?? 0);
                                if ((type & 0x30) == 0) continue; // solo servizi Win32 (non driver)
                                var start = Convert.ToInt32(s.GetValue("Start") ?? 4);
                                var touchedByLindo = backup.ContainsKey(name);
                                if (start != 2 && !(start == 3 && touchedByLindo)) continue;

                                var image = Environment.ExpandEnvironmentVariables(s.GetValue("ImagePath")?.ToString() ?? "");
                                var exe = WinPath.ExtractExecutable(image);
                                if (exe.Length == 0 || WinPath.IsSameOrInside(exe, windows)) continue;
                                var company = Company(exe);
                                if (company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                                var display = s.GetValue("DisplayName")?.ToString() ?? name;
                                if (display.StartsWith("@")) display = Description(exe) ?? name;
                                result.Add(new StartupEntry
                                {
                                    Id = "svc|" + name,
                                    Kind = StartupKind.Service,
                                    Name = display,
                                    Command = image,
                                    ExePath = exe,
                                    Publisher = company,
                                    Enabled = start == 2,
                                    AllUsers = true,
                                    ServiceName = name,
                                    ServiceDelayed = Convert.ToInt32(s.GetValue("DelayedAutostart") ?? 0) == 1,
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Attività pianificate non di Microsoft che partono all'accesso o all'avvio del PC.</summary>
        private static IEnumerable<StartupEntry> ScanTasks()
        {
            var result = new List<StartupEntry>();
            const string script =
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' -and " +
                "($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' }) } | " +
                "ForEach-Object { $a = $_.Actions | Select-Object -First 1; " +
                "\"{0}`t{1}`t{2}`t{3}`t{4}\" -f $_.TaskPath,$_.TaskName,$_.State,$a.Execute,$a.Arguments }";
            var output = PowerShell.Run(script, TimeSpan.FromSeconds(40));
            if (output == null) return result;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                var exe = Environment.ExpandEnvironmentVariables(p[3].Trim('"'));
                if (exe.Length > 0 && WinPath.IsSameOrInside(exe, SystemInfo.Roots.Windows) && p[4].Length == 0) continue;
                result.Add(new StartupEntry
                {
                    Id = "task|" + p[0] + p[1],
                    Kind = StartupKind.Task,
                    Name = Description(exe) ?? p[1],
                    Command = (p[3] + " " + p[4]).Trim(),
                    ExePath = exe,
                    Publisher = Company(exe),
                    Enabled = !p[2].Equals("Disabled", StringComparison.OrdinalIgnoreCase),
                    AllUsers = true,
                    TaskPath = p[0],
                    TaskName = p[1],
                });
            }
            return result;
        }

        /// <summary>Attiva o disattiva una voce. Restituisce null se ok, altrimenti l'errore.</summary>
        public static string SetEnabled(StartupEntry e, bool enabled)
        {
            try
            {
                switch (e.Kind)
                {
                    case StartupKind.Registry:
                    case StartupKind.StartupFolder:
                    {
                        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
                        using (var bk = RegistryKey.OpenBaseKey(e.Hive, view))
                        using (var ap = bk.CreateSubKey(e.ApprovedKey))
                            ap.SetValue(e.ApprovedValue, StartupApproved.Make(enabled), RegistryValueKind.Binary);
                        break;
                    }
                    case StartupKind.Service:
                    {
                        var backup = LoadBackup();
                        if (!enabled && !backup.ContainsKey(e.ServiceName))
                        {
                            backup[e.ServiceName] = e.ServiceDelayed ? "delayed-auto" : "auto";
                            SaveBackup(backup);
                        }
                        var mode = enabled ? (backup.TryGetValue(e.ServiceName, out var m) ? m : "auto") : "demand";
                        var err = Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), "config \"" + e.ServiceName + "\" start= " + mode);
                        if (err != null) return err;
                        if (enabled) { backup.Remove(e.ServiceName); SaveBackup(backup); }
                        break;
                    }
                    case StartupKind.Task:
                    {
                        var cmd = (enabled ? "Enable" : "Disable") + "-ScheduledTask -TaskPath '" + e.TaskPath.Replace("'", "''") +
                                  "' -TaskName '" + e.TaskName.Replace("'", "''") + "' | Out-Null; 'ok'";
                        var o = PowerShell.Run(cmd, TimeSpan.FromSeconds(30));
                        if (o == null || !o.Contains("ok")) return "Impossibile modificare l'attività pianificata";
                        break;
                    }
                }
                e.Enabled = enabled;
                Log.Write("Avvio: " + e.Name + " → " + (enabled ? "attivata" : "disattivata"));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return p.ExitCode == 0 ? null : o.Trim();
            }
        }

        private static Dictionary<string, string> LoadBackup()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(BackupFile))
                    foreach (var l in File.ReadAllLines(BackupFile))
                    {
                        var p = l.Split('\t');
                        if (p.Length == 2) d[p[0]] = p[1];
                    }
            }
            catch { }
            return d;
        }

        private static void SaveBackup(Dictionary<string, string> d)
        {
            try { File.WriteAllLines(BackupFile, d.Select(kv => kv.Key + "\t" + kv.Value)); } catch { }
        }
    }
}
