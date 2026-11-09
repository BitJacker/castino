using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lindo.Core;

namespace Lindo.Services
{
    internal enum UninstallOutcome
    {
        Completed,       // il programma non è più nell'elenco
        StillInstalled,  // il disinstallatore è terminato ma la voce è ancora presente
        NoUninstaller,   // impossibile avviare il disinstallatore
        Skipped,         // l'utente ha scelto di non aspettare
    }

    internal static class Uninstaller
    {
        /// <summary>True se il disinstallatore dichiarato sembra esistere.</summary>
        public static bool HasWorkingUninstaller(AppInfo app)
        {
            if (app.Source == AppSource.Store) return true;
            if (app.IsMsi && !string.IsNullOrEmpty(app.ProductCode)) return true;
            var cmd = string.IsNullOrWhiteSpace(app.UninstallString) ? app.QuietUninstallString : app.UninstallString;
            if (string.IsNullOrWhiteSpace(cmd)) return false;
            var (file, _) = WinPath.SplitCommand(Environment.ExpandEnvironmentVariables(cmd));
            if (file.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.EndsWith("rundll32.exe", StringComparison.OrdinalIgnoreCase)) return true;
            try { return File.Exists(file); } catch { return false; }
        }

        public static bool IsStillInstalled(AppInfo app)
        {
            if (app.Source == AppSource.Store)
            {
                var o = PowerShell.Run("if (Get-AppxPackage | Where-Object { $_.PackageFullName -eq '" +
                                       app.PackageFullName.Replace("'", "''") + "' }) { 'yes' } else { 'no' }", TimeSpan.FromSeconds(20));
                return o != null && o.Trim() == "yes";
            }
            return new WindowsRegistry().KeyExists(app.RegistryKey);
        }

        /// <summary>
        /// Avvia il disinstallatore ufficiale dell'app e aspetta che finisca (inclusi i processi figli
        /// che molti disinstallatori avviano da una copia temporanea).
        /// </summary>
        public static async Task<UninstallOutcome> RunAsync(AppInfo app, CancellationToken skip, IProgress<string> status)
        {
            if (app.Source == AppSource.Store)
            {
                status?.Report("Rimozione dell'app dallo Store…");
                await Task.Run(() => PowerShell.Run("Remove-AppxPackage -Package '" + app.PackageFullName.Replace("'", "''") + "'",
                    TimeSpan.FromMinutes(5)));
                return IsStillInstalled(app) ? UninstallOutcome.StillInstalled : UninstallOutcome.Completed;
            }

            ProcessStartInfo psi;
            if (app.IsMsi && !string.IsNullOrEmpty(app.ProductCode))
            {
                psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/x " + app.ProductCode);
            }
            else
            {
                var cmd = string.IsNullOrWhiteSpace(app.UninstallString) ? app.QuietUninstallString : app.UninstallString;
                if (string.IsNullOrWhiteSpace(cmd)) return UninstallOutcome.NoUninstaller;
                var (file, args) = WinPath.SplitCommand(Environment.ExpandEnvironmentVariables(cmd));
                // Alcune voci MSI usano "MsiExec.exe /I{GUID}" che riapre l'installazione: forziamo /X
                if (file.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                    args = System.Text.RegularExpressions.Regex.Replace(args, @"/I\s*\{", "/X{", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                psi = new ProcessStartInfo(file, args);
            }
            psi.UseShellExecute = true;
            try { psi.WorkingDirectory = Path.GetDirectoryName(psi.FileName) ?? string.Empty; } catch { }

            var before = new HashSet<int>(Process.GetProcesses().Select(p => p.Id));
            var started = DateTime.Now;
            Process main;
            try
            {
                status?.Report("Avvio del programma di disinstallazione…");
                main = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Write("Impossibile avviare il disinstallatore di " + app.Name + ": " + ex.Message);
                return UninstallOutcome.NoUninstaller;
            }

            status?.Report("Segui le istruzioni nella finestra del programma di disinstallazione.");
            var uninstallerDir = SafeDir(psi.FileName);

            while (!skip.IsCancellationRequested)
            {
                await Task.Delay(800).ConfigureAwait(false);
                var mainAlive = main != null && !SafeHasExited(main);
                var children = FindHelperProcesses(before, started, uninstallerDir);
                if (!mainAlive && children.Count == 0)
                {
                    // Piccola attesa: alcuni disinstallatori cancellano la voce di registro alla fine
                    await Task.Delay(1500).ConfigureAwait(false);
                    if (FindHelperProcesses(before, started, uninstallerDir).Count > 0) continue;
                    break;
                }
                if (!mainAlive && !IsStillInstalled(app)) break;
            }

            if (skip.IsCancellationRequested) return UninstallOutcome.Skipped;
            return IsStillInstalled(app) ? UninstallOutcome.StillInstalled : UninstallOutcome.Completed;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetLongPathName(string shortPath, System.Text.StringBuilder longPath, int size);

        /// <summary>Converte un percorso con nomi brevi 8.3 (RUNNER~1) nella forma lunga.</summary>
        internal static string LongPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.IndexOf('~') < 0) return path;
                var sb = new System.Text.StringBuilder(1024);
                var n = GetLongPathName(path, sb, sb.Capacity);
                return n > 0 && n < sb.Capacity ? sb.ToString() : path;
            }
            catch { return path; }
        }

        private static string SafeDir(string file)
        {
            try { return Path.GetDirectoryName(file) ?? string.Empty; } catch { return string.Empty; }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        /// <summary>Processi nuovi che sembrano far parte della disinstallazione (copie in %TEMP%, Au_.exe di NSIS, ecc.).</summary>
        private static List<Process> FindHelperProcesses(HashSet<int> before, DateTime started, string uninstallerDir)
        {
            // Cartelle temporanee in forma lunga: GetTempPath può restituire nomi brevi 8.3
            // (es. C:\Users\RUNNER~1\...) mentre il percorso del processo è in forma lunga.
            var temps = new[]
            {
                Path.GetTempPath(),
                Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User) ?? "",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            }.Where(t => t.Length > 3).Select(t => LongPath(Environment.ExpandEnvironmentVariables(t))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            uninstallerDir = uninstallerDir.Length > 0 ? LongPath(uninstallerDir) : uninstallerDir;
            var result = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (before.Contains(p.Id)) continue;
                    if (p.StartTime < started.AddSeconds(-2)) continue;
                    var name = p.ProcessName.ToLowerInvariant();
                    // NSIS si copia in %TEMP%\~nsuA.tmp\ come Un.exe / Un_A.exe / Au_.exe e continua da lì
                    var looksLikeUninstaller = name == "un" || name == "au_" || name.StartsWith("au_") || name.StartsWith("un_") || name.Contains("unins") ||
                                               name.Contains("uninst") || name.StartsWith("_iu") || name == "msiexec" ||
                                               name.Contains("setup") || name.Contains("update");
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path != null) path = LongPath(path);
                    var inTemp = path != null && temps.Any(t => WinPath.IsSameOrInside(path, t));
                    var inDir = path != null && uninstallerDir.Length > 0 && WinPath.IsSameOrInside(path, uninstallerDir);
                    if ((looksLikeUninstaller || inTemp || inDir) && name != "lindo" && name != "conhost")
                        result.Add(p);
                }
                catch { /* processo di sistema non accessibile */ }
            }
            // msiexec resta in esecuzione come servizio: lo consideriamo solo se ha una finestra
            return result.Where(p =>
            {
                try { return p.ProcessName.ToLowerInvariant() != "msiexec" || p.MainWindowHandle != IntPtr.Zero; }
                catch { return false; }
            }).ToList();
        }
    }
}
