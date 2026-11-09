using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lindo.Core;
using Lindo.Services;

namespace Lindo
{
    /// <summary>
    /// Autotest senza interfaccia: legge le app reali, cerca residui e orfani e verifica le regole di sicurezza.
    /// NON elimina nulla. Uso: Lindo.exe --selftest report.txt
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string output)
        {
            var sb = new StringBuilder();
            int failures = 0;
            void Check(bool ok, string what)
            {
                sb.AppendLine((ok ? "PASS " : "FAIL ") + what);
                if (!ok) failures++;
            }

            try
            {
                var roots = SystemInfo.Roots;
                var guard = new PathGuard(roots);
                sb.AppendLine("Lindo autotest " + DateTime.Now);
                sb.AppendLine("Roots: PF=" + roots.ProgramFiles + " PF86=" + roots.ProgramFilesX86 + " Local=" + roots.LocalAppData);

                // Regole di sicurezza sui percorsi reali di questa macchina
                foreach (var p in new[] { roots.Windows, Environment.SystemDirectory, roots.ProgramFiles, roots.ProgramFilesX86,
                                          roots.ProgramData, roots.UserProfile, roots.LocalAppData, roots.RoamingAppData,
                                          Path.Combine(roots.ProgramFiles, "Common Files"), Path.Combine(roots.LocalAppData, "Microsoft"),
                                          SystemInfo.SystemDrive, roots.UserDesktop })
                    Check(!guard.CanDelete(p), "protetto: " + p);

                var apps = AppScanner.ScanDesktop();
                var store = AppScanner.ScanStore();
                Check(apps.Count > 0, "programmi desktop trovati: " + apps.Count);
                sb.AppendLine("App Store trovate: " + store.Count);
                var all = apps.Concat(store).ToList();

                foreach (var a in all.OrderBy(x => x.Name).Take(400))
                    sb.AppendLine($"  APP [{a.Source}] {a.Name} | {a.Publisher} | {a.Version} | {SizeFormat.Format(a.EstimatedSizeBytes)} | {a.InstallLocation} | uninstaller ok={Uninstaller.HasWorkingUninstaller(a)}");

                var planner = new LeftoverPlanner(roots, new WindowsFileSystem(), new WindowsRegistry())
                {
                    ShortcutTargetResolver = ShortcutResolver.Resolve,
                };

                foreach (var a in all.Where(x => x.Source == AppSource.Desktop).Take(25))
                {
                    var res = planner.FindLeftovers(a, all);
                    sb.AppendLine($"RESIDUI di {a.Name}: {res.Count}");
                    foreach (var r in res)
                    {
                        sb.AppendLine($"    {r.Confidence,-6} {r.Kind,-11} {r.Path}  ({r.Reason})");
                        if (r.Kind == LeftoverKind.RegistryKey) Check(RegistryGuard.CanDelete(r.Path), "chiave eliminabile: " + r.Path);
                        else Check(guard.CanDelete(r.Path, r.Kind), "percorso eliminabile: " + r.Path);
                    }
                }

                var orphans = planner.FindOrphans(all);
                sb.AppendLine("ORFANI: " + orphans.Count);
                foreach (var o in orphans)
                {
                    sb.AppendLine("    " + o.Path + "  " + SizeFormat.Format(WindowsFileSystem.DirectorySize(o.Path)));
                    Check(guard.CanDelete(o.Path), "orfano eliminabile: " + o.Path);
                }

                foreach (var t in QuickClean.Targets())
                    sb.AppendLine("PULIZIA " + t.Title + ": " + SizeFormat.Format(QuickClean.Measure(t)));
            }
            catch (Exception ex)
            {
                sb.AppendLine("FAIL eccezione: " + ex);
                failures++;
            }

            sb.AppendLine(failures == 0 ? "RISULTATO: OK" : "RISULTATO: " + failures + " errori");
            try { File.WriteAllText(output, sb.ToString(), Encoding.UTF8); } catch { }
            return failures == 0 ? 0 : 1;
        }

        private static void Write(string output, StringBuilder sb)
        {
            try { File.WriteAllText(output, sb.ToString(), Encoding.UTF8); } catch { }
        }

        /// <summary>
        /// Verifica di sicurezza dei residui: tra i "sicuri" devono comparire SOLO le cartelle attese
        /// (finti residui creati dal test) e tutte devono essere trovate.
        /// Uso: Lindo.exe --safety-test report.txt --expect "C:\a;C:\b" [--recent-days 90]
        /// </summary>
        public static int SafetyTest(string output, string[] expected, int recentDays)
        {
            var sb = new StringBuilder();
            int failures = 0;
            void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
            try
            {
                var apps = AppScanner.ScanDesktop().Concat(AppScanner.ScanStore()).ToList();
                Drives.UpdateExtraRoots(apps);
                sb.AppendLine("App installate: " + apps.Count + "  Radici extra: " + string.Join(", ", SystemInfo.Roots.ExtraProgramRoots));
                var ev = EvidenceService.Collect(recentDays);
                sb.AppendLine("Indizi d'uso: " + ev.InUsePaths.Count);
                var planner = new LeftoverPlanner(SystemInfo.Roots, new WindowsFileSystem(), new WindowsRegistry());
                var orphans = planner.FindOrphans(apps, ev);
                var safe = orphans.Where(o => o.Confidence == Confidence.High).ToList();
                var check = orphans.Where(o => o.Confidence != Confidence.High).ToList();
                sb.AppendLine("SICURI: " + safe.Count);
                foreach (var o in safe) sb.AppendLine("    " + o.Path + "  (" + o.Reason + ")");
                sb.AppendLine("DA CONTROLLARE: " + check.Count);
                foreach (var o in check) sb.AppendLine("    " + o.Path + "  (" + o.Reason + ")");

                foreach (var e in expected)
                    Check(safe.Any(o => WinPath.Equal(o.Path, e)), "residuo finto trovato tra i sicuri: " + e);
                foreach (var o in safe)
                    Check(expected.Any(e => WinPath.Equal(e, o.Path)), "nessun falso positivo tra i sicuri: " + o.Path);
                foreach (var a in apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallLocation)))
                    foreach (var o in safe)
                        Check(!WinPath.IsSameOrInside(a.InstallLocation, o.Path), $"la cartella di {a.Name} non è tra i sicuri ({o.Path})");
            }
            catch (Exception ex) { sb.AppendLine("FAIL eccezione: " + ex); failures++; }
            sb.AppendLine(failures == 0 ? "RISULTATO: OK" : "RISULTATO: " + failures + " errori");
            Write(output, sb);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Disinstalla davvero le app indicate (in modalità silenziosa), elimina i residui proposti come
        /// "Sicuro"/"Probabile" (come farebbe l'utente lasciando la selezione predefinita) e verifica che
        /// tutte le altre app e i percorsi indicati siano ancora intatti.
        /// Uso: Lindo.exe --test-uninstall report.txt --apps "VLC;7-Zip" --must-survive "C:\x;C:\y"
        /// </summary>
        public static int UninstallTest(string output, string[] names, string[] mustSurvive)
        {
            var sb = new LiveReport(output);
            int failures = 0;
            void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
            try
            {
                var before = AppScanner.ScanDesktop().Concat(AppScanner.ScanStore()).ToList();
                Drives.UpdateExtraRoots(before);
                var targets = new List<AppInfo>();
                foreach (var n in names)
                {
                    var a = before.FirstOrDefault(x => x.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                    Check(a != null, "app da disinstallare trovata: " + n + (a != null ? " → " + a.Name : ""));
                    if (a != null) targets.Add(a);
                }
                var others = before.Where(b => !targets.Contains(b)).ToList();
                var survivorsDirs = others.Where(o => !string.IsNullOrWhiteSpace(o.InstallLocation) && Directory.Exists(o.InstallLocation))
                                          .Select(o => (o.Name, o.InstallLocation)).ToList();
                sb.AppendLine("Altre app con cartella da proteggere: " + survivorsDirs.Count);

                foreach (var t in targets)
                {
                    sb.AppendLine("== " + t.Name);
                    var ok = RunSilentUninstall(t, sb);
                    Check(ok, "disinstallatore eseguito: " + t.Name);
                    for (int i = 0; i < 60 && new WindowsRegistry().KeyExists(t.RegistryKey); i++) Thread.Sleep(1000);

                    var all = AppScanner.ScanDesktop().Concat(AppScanner.ScanStore()).ToList();
                    var planner = new LeftoverPlanner(SystemInfo.Roots, new WindowsFileSystem(), new WindowsRegistry()) { ShortcutTargetResolver = ShortcutResolver.Resolve };
                    var leftovers = planner.FindLeftovers(t, all);
                    foreach (var l in leftovers) sb.AppendLine($"    residuo {l.Confidence,-6} {l.Kind,-11} {l.Path} ({l.Reason})");
                    var chosen = leftovers.Where(l => l.Confidence >= Confidence.Medium).ToList();
                    var res = Cleaner.Delete(chosen);
                    sb.AppendLine($"    eliminati {res.Deleted}, al riavvio {res.ScheduledOnReboot}, errori {res.Failed.Count}");
                    foreach (var f in res.Failed) sb.AppendLine("    errore: " + f);

                    Check(!new WindowsRegistry().KeyExists(t.RegistryKey), "voce rimossa dall'elenco: " + t.Name);
                    if (!string.IsNullOrWhiteSpace(t.InstallLocation))
                        Check(!Directory.Exists(t.InstallLocation) || res.ScheduledOnReboot > 0, "cartella di installazione rimossa: " + t.InstallLocation);
                    var after = planner.FindLeftovers(t, AppScanner.ScanDesktop());
                    Check(after.Count(l => l.Confidence >= Confidence.Medium) == 0 || res.ScheduledOnReboot > 0, "verifica finale senza residui sicuri: " + t.Name);
                }

                foreach (var (name, dir) in survivorsDirs)
                    Check(Directory.Exists(dir), "ancora intatta: " + name + " (" + dir + ")");
                foreach (var p in mustSurvive.Where(x => x.Length > 0))
                {
                    var e = Environment.ExpandEnvironmentVariables(p);
                    Check(Directory.Exists(e) || File.Exists(e), "ancora presente: " + e);
                }
            }
            catch (Exception ex) { sb.AppendLine("FAIL eccezione: " + ex); failures++; }
            sb.AppendLine(failures == 0 ? "RISULTATO: OK" : "RISULTATO: " + failures + " errori");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Rapporto che viene scritto su disco a ogni riga (si vede anche se il test si blocca).</summary>
        private sealed class LiveReport
        {
            private readonly string _path;
            public LiveReport(string path) { _path = path; try { File.WriteAllText(path, ""); } catch { } }
            public void AppendLine(string line)
            {
                try { File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }

        private static bool RunSilentUninstall(AppInfo a, LiveReport sb)
        {
            string file, args;
            if (!string.IsNullOrWhiteSpace(a.QuietUninstallString))
                (file, args) = WinPath.SplitCommand(Environment.ExpandEnvironmentVariables(a.QuietUninstallString));
            else if (a.IsMsi && !string.IsNullOrEmpty(a.ProductCode))
                (file, args) = ("msiexec.exe", "/x " + a.ProductCode + " /qn /norestart");
            else
            {
                (file, args) = WinPath.SplitCommand(Environment.ExpandEnvironmentVariables(a.UninstallString));
                var fn = Path.GetFileName(file).ToLowerInvariant();
                if (file.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || fn == "msiexec")
                    args = System.Text.RegularExpressions.Regex.Replace(args, @"/I\s*\{", "/X{", System.Text.RegularExpressions.RegexOptions.IgnoreCase) + " /qn /norestart";
                else if (fn.StartsWith("unins")) args += " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
                else args += " /S";
            }
            sb.AppendLine("    comando: " + file + " " + args);
            try
            {
                var started = DateTime.Now;
                using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }))
                {
                    if (p != null && !p.WaitForExit(180000))
                    {
                        sb.AppendLine("    il disinstallatore non ha finito in 3 minuti: lo chiudo");
                        try { p.Kill(); } catch { }
                    }
                }
                // I disinstallatori NSIS/Inno si copiano in %TEMP% e continuano: aspettiamo che finiscano
                for (int i = 0; i < 120; i++)
                {
                    var helpers = Process.GetProcesses().Where(x =>
                    {
                        try
                        {
                            var n = x.ProcessName.ToLowerInvariant();
                            return (n.StartsWith("au_") || n.StartsWith("un_") || n.Contains("unins") || n.StartsWith("_iu")) && x.StartTime >= started.AddSeconds(-2);
                        }
                        catch { return false; }
                    }).ToList();
                    if (helpers.Count == 0) break;
                    if (i == 119) { sb.AppendLine("    processi di disinstallazione ancora aperti: li chiudo"); foreach (var h in helpers) { try { h.Kill(); } catch { } } }
                    Thread.Sleep(1000);
                }
                sb.AppendLine("    disinstallatore terminato dopo " + (int)(DateTime.Now - started).TotalSeconds + " s");
                return true;
            }
            catch (Exception ex)
            {
                sb.AppendLine("    impossibile avviare: " + ex.Message);
                return false;
            }
        }
    }
}
