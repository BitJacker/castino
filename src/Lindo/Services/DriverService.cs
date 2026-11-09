using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Lindo.Core;

namespace Lindo.Services
{
    /// <summary>Driver di terze parti installati (C:\Windows\INF\oem*.inf) e loro rimozione con pnputil.</summary>
    internal static class DriverService
    {
        private static List<(AppInfo App, InfInfo Inf)> _cache;

        public static List<AppInfo> Scan() => ScanWithInfo().Select(x => x.App).ToList();

        public static List<(AppInfo App, InfInfo Inf)> ScanWithInfo(bool refresh = true)
        {
            if (!refresh && _cache != null) return _cache;
            var result = new List<(AppInfo, InfInfo)>();
            var infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            string[] files;
            try { files = Directory.GetFiles(infDir, "oem*.inf"); } catch { return result; }

            var inUse = DevicesUsingDrivers();
            var store = DriverStoreIndex();

            foreach (var file in files)
            {
                try
                {
                    var text = ReadInf(file);
                    var inf = InfParser.Parse(text);
                    var published = Path.GetFileName(file);
                    var storeDir = FindStoreFolder(file, store);
                    long size = storeDir != null ? WindowsFileSystem.DirectorySize(storeDir) : new FileInfo(file).Length;
                    inUse.TryGetValue(published, out var devices);

                    var name = !string.IsNullOrWhiteSpace(inf.Description) ? inf.Description
                             : !string.IsNullOrWhiteSpace(inf.Provider) ? "Driver " + inf.Provider
                             : published;
                    var app = new AppInfo
                    {
                        Id = "driver:" + published,
                        Source = AppSource.Driver,
                        Name = name,
                        Publisher = inf.Provider,
                        Version = inf.Version,
                        InstallDate = inf.Date,
                        EstimatedSizeBytes = size,
                        InstallLocation = storeDir ?? string.Empty,
                        DriverInf = published,
                        DriverClass = inf.Class,
                        DriverInUseBy = devices ?? string.Empty,
                        DriverIsCritical = DriverRules.IsCritical(inf.Class),
                    };
                    result.Add((app, inf));
                }
                catch (Exception ex) { Log.Write("Driver non leggibile " + file + ": " + ex.Message); }
            }
            _cache = result;
            return result;
        }

        private static string ReadInf(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.Default.GetString(bytes);
        }

        /// <summary>Indice dei .inf presenti nel DriverStore, per dimensione del file.</summary>
        private static Dictionary<long, List<string>> DriverStoreIndex()
        {
            var index = new Dictionary<long, List<string>>();
            var repo = Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(repo))
                {
                    try
                    {
                        foreach (var inf in Directory.EnumerateFiles(dir, "*.inf"))
                        {
                            var len = new FileInfo(inf).Length;
                            if (!index.TryGetValue(len, out var l)) index[len] = l = new List<string>();
                            l.Add(inf);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return index;
        }

        private static string FindStoreFolder(string oemInf, Dictionary<long, List<string>> index)
        {
            try
            {
                var len = new FileInfo(oemInf).Length;
                if (!index.TryGetValue(len, out var candidates)) return null;
                var bytes = File.ReadAllBytes(oemInf);
                foreach (var c in candidates)
                {
                    try { if (File.ReadAllBytes(c).SequenceEqual(bytes)) return Path.GetDirectoryName(c); } catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>oemNN.inf → nomi dei dispositivi collegati che lo usano adesso.</summary>
        private static Dictionary<string, string> DevicesUsingDrivers()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var output = PowerShell.Run(
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.InfName -like 'oem*' } | " +
                "ForEach-Object { \"{0}`t{1}\" -f $_.InfName,$_.DeviceName }", TimeSpan.FromSeconds(60));
            if (output == null) return map;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length < 2 || string.IsNullOrWhiteSpace(p[1])) continue;
                map[p[0]] = map.TryGetValue(p[0], out var prev) && !prev.Contains(p[1]) ? prev + ", " + p[1] : p[1];
            }
            return map;
        }

        /// <summary>Driver che sembrano appartenere a un'app (stesso editore / stesso prodotto).</summary>
        public static List<Leftover> FindRelated(AppInfo app)
        {
            var list = new List<Leftover>();
            if (app == null || app.Source == AppSource.Driver) return list;
            foreach (var (drv, inf) in ScanWithInfo(refresh: _cache == null))
            {
                if (drv.DriverIsCritical) continue;
                var s = DriverRules.MatchDriverToApp(inf, app);
                if (s < MatchStrength.Medium) continue;
                var inUse = !string.IsNullOrWhiteSpace(drv.DriverInUseBy);
                list.Add(new Leftover
                {
                    Kind = LeftoverKind.Driver,
                    Path = drv.DriverInf,
                    DriverInf = drv.DriverInf,
                    DisplayName = drv.Name + " (" + (string.IsNullOrEmpty(drv.DriverClass) ? "driver" : drv.DriverClass) + ")",
                    SizeBytes = drv.EstimatedSizeBytes,
                    Confidence = inUse ? Confidence.Low : (s == MatchStrength.Strong ? Confidence.High : Confidence.Medium),
                    Reason = inUse ? "Driver di " + drv.Publisher + " usato da: " + drv.DriverInUseBy
                                   : "Driver di " + drv.Publisher + " (nessun dispositivo collegato lo usa)",
                });
            }
            return list;
        }

        /// <summary>Rimuove un pacchetto driver. Restituisce null se ok, altrimenti il messaggio di errore.</summary>
        public static string Delete(string publishedInf)
        {
            if (string.IsNullOrWhiteSpace(publishedInf) ||
                !publishedInf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ||
                !publishedInf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ||
                publishedInf.IndexOfAny(new[] { '\\', '/', '"', ' ' }) >= 0)
                return "Nome driver non valido";

            // Ultimo controllo: mai i driver di classi critiche
            var known = ScanWithInfo(refresh: false).FirstOrDefault(x => x.App.DriverInf.Equals(publishedInf, StringComparison.OrdinalIgnoreCase));
            if (known.App != null && known.App.DriverIsCritical) return "Driver di sistema: non viene rimosso";

            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                    "/delete-driver " + publishedInf + " /uninstall")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    Log.Write("pnputil " + publishedInf + " → " + p.ExitCode + " " + output.Trim());
                    if (p.ExitCode == 0 || p.ExitCode == 3010) { _cache = null; return null; }
                    return output.Trim().Split('\n').LastOrDefault()?.Trim() ?? ("codice " + p.ExitCode);
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
