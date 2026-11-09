using System;
using System.IO;
using System.Linq;
using System.Text;
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
    }
}
