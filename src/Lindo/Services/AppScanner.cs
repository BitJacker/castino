using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Lindo.Core;
using Microsoft.Win32;

namespace Lindo.Services
{
    /// <summary>Legge l'elenco delle app installate (programmi desktop + app dello Store).</summary>
    internal static class AppScanner
    {
        private const string UninstallPath = @"Microsoft\Windows\CurrentVersion\Uninstall";

        private static readonly string[] UninstallRoots =
        {
            @"HKLM\SOFTWARE\" + UninstallPath,
            @"HKLM\SOFTWARE\WOW6432Node\" + UninstallPath,
            @"HKCU\Software\" + UninstallPath,
            @"HKCU\Software\WOW6432Node\" + UninstallPath,
        };

        private static readonly Regex KbRegex = new Regex(@"^(KB|Security Update|Update for|Hotfix)", RegexOptions.IgnoreCase);
        private static readonly Regex GuidRegex = new Regex(@"^\{[0-9A-Fa-f\-]{36}\}$");

        /// <summary>
        /// Programmi desktop. Con <paramref name="includeHidden"/> include anche le voci nascoste
        /// (componenti di sistema, aggiornamenti, sotto-componenti di SDK): non si mostrano nell'elenco,
        /// ma servono a proteggere le loro cartelle nella ricerca dei residui.
        /// </summary>
        public static List<AppInfo> ScanDesktop(bool includeHidden = false)
        {
            var list = new List<AppInfo>();
            foreach (var root in UninstallRoots)
            {
                using (var rk = SystemInfo.OpenKey(root))
                {
                    if (rk == null) continue;
                    string[] names;
                    try { names = rk.GetSubKeyNames(); } catch { continue; }
                    foreach (var sub in names)
                    {
                        try
                        {
                            using (var k = rk.OpenSubKey(sub))
                            {
                                var app = Read(k, root + "\\" + sub, sub, root.Contains("WOW6432Node"), includeHidden);
                                if (app != null) list.Add(app);
                            }
                        }
                        catch (Exception ex) { Log.Write("Voce non leggibile " + sub + ": " + ex.Message); }
                    }
                }
            }

            if (includeHidden) return list;
            // Doppioni (stesso nome e versione): tiene la voce più completa
            return list
                .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
                .Select(g => g.OrderByDescending(a => string.IsNullOrEmpty(a.InstallLocation) ? 0 : 1)
                              .ThenByDescending(a => a.EstimatedSizeBytes).First())
                .ToList();
        }

        private static AppInfo Read(RegistryKey k, string fullKey, string subName, bool wow, bool includeHidden = false)
        {
            if (k == null) return null;
            string S(string n) { try { return (k.GetValue(n) as string ?? k.GetValue(n)?.ToString() ?? string.Empty).Trim(); } catch { return string.Empty; } }
            int I(string n) { try { var v = k.GetValue(n); return v is int i ? i : int.TryParse(v?.ToString(), out var r) ? r : 0; } catch { return 0; } }

            var name = S("DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;
            var uninstall = S("UninstallString");
            var quiet = S("QuietUninstallString");
            var isMsi = I("WindowsInstaller") == 1;
            if (!includeHidden)
            {
                if (I("SystemComponent") == 1) return null;
                if (!string.IsNullOrEmpty(S("ParentKeyName"))) return null;
                var release = S("ReleaseType");
                if (release.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    release.IndexOf("hotfix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    release.IndexOf("service pack", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                if (KbRegex.IsMatch(name)) return null;
                if (string.IsNullOrEmpty(uninstall) && string.IsNullOrEmpty(quiet) && !isMsi) return null;
            }

            DateTime? date = null;
            var ds = S("InstallDate");
            if (DateTime.TryParseExact(ds, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;

            var size = (long)Math.Max(0, I("EstimatedSize")) * 1024L;
            var location = S("InstallLocation").Trim('"');

            return new AppInfo
            {
                Id = fullKey,
                Name = name,
                Publisher = S("Publisher"),
                Version = S("DisplayVersion"),
                InstallLocation = location,
                UninstallString = uninstall,
                QuietUninstallString = quiet,
                DisplayIcon = S("DisplayIcon"),
                IsMsi = isMsi,
                ProductCode = GuidRegex.IsMatch(subName) ? subName : string.Empty,
                EstimatedSizeBytes = size,
                InstallDate = date,
                Source = AppSource.Desktop,
                RegistryKey = fullKey,
                Is32BitView = wow,
            };
        }

        // ------------------------------------------------------------------ Store

        public static List<AppInfo> ScanStore()
        {
            var result = new List<AppInfo>();
            const string script =
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and -not $_.IsResourcePackage -and $_.SignatureKind -eq 'Store' } | " +
                "ForEach-Object { \"{0}`t{1}`t{2}`t{3}`t{4}\" -f $_.Name,$_.PackageFullName,$_.PackageFamilyName,$_.InstallLocation,$_.Version }";
            var output = PowerShell.Run(script, TimeSpan.FromSeconds(40));
            if (output == null) return result;

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                var app = new AppInfo
                {
                    Source = AppSource.Store,
                    Id = "store:" + p[1],
                    PackageFullName = p[1],
                    PackageFamilyName = p[2],
                    InstallLocation = p[3],
                    Version = p[4],
                    Name = Prettify(p[0]),
                };
                try { ReadManifest(app, p[1]); } catch { /* manifest non leggibile */ }
                try
                {
                    var dir = new DirectoryInfo(app.InstallLocation);
                    if (dir.Exists) app.InstallDate = dir.CreationTime.Date;
                }
                catch { }
                result.Add(app);
            }
            return result;
        }

        private static void ReadManifest(AppInfo app, string fullName)
        {
            var manifest = Path.Combine(app.InstallLocation, "AppxManifest.xml");
            if (!File.Exists(manifest)) return;
            var doc = new XmlDocument();
            doc.Load(manifest);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", doc.DocumentElement?.NamespaceURI ?? string.Empty);
            var props = doc.SelectSingleNode("/m:Package/m:Properties", ns);
            if (props == null) return;

            var dn = props.SelectSingleNode("m:DisplayName", ns)?.InnerText;
            var pub = props.SelectSingleNode("m:PublisherDisplayName", ns)?.InnerText;
            var logo = props.SelectSingleNode("m:Logo", ns)?.InnerText;
            var pkgName = doc.SelectSingleNode("/m:Package/m:Identity/@Name", ns)?.Value ?? string.Empty;

            dn = ResolveResource(dn, fullName, pkgName);
            pub = ResolveResource(pub, fullName, pkgName);
            if (!string.IsNullOrWhiteSpace(dn)) app.Name = dn;
            if (!string.IsNullOrWhiteSpace(pub)) app.Publisher = pub;
            if (!string.IsNullOrWhiteSpace(logo)) app.DisplayIcon = FindScaledAsset(Path.Combine(app.InstallLocation, logo));
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

        private static string ResolveResource(string value, string fullName, string pkgName)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
            var res = value.Substring("ms-resource:".Length);
            var candidates = new List<string>();
            if (res.StartsWith("//")) candidates.Add("ms-resource:" + res);
            else if (res.StartsWith("/")) candidates.Add("ms-resource://" + pkgName + res);
            else
            {
                candidates.Add("ms-resource://" + pkgName + "/resources/" + res);
                candidates.Add("ms-resource://" + pkgName + "/" + res);
            }
            foreach (var c in candidates)
            {
                var sb = new StringBuilder(1024);
                try
                {
                    if (SHLoadIndirectString("@{" + fullName + "?" + c + "}", sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                        return sb.ToString();
                }
                catch { }
            }
            return null;
        }

        /// <summary>"Logo.png" spesso esiste solo come "Logo.scale-100.png" / "Logo.targetsize-48.png".</summary>
        private static string FindScaledAsset(string path)
        {
            if (File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            if (dir == null || !Directory.Exists(dir)) return string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var files = Directory.GetFiles(dir, baseName + ".*" + ext);
            string Pick(string token) => files.FirstOrDefault(f => f.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                                                                   && f.IndexOf("contrast", StringComparison.OrdinalIgnoreCase) < 0);
            return Pick("scale-200") ?? Pick("scale-100") ?? Pick("targetsize-48") ?? Pick("scale") ??
                   files.FirstOrDefault(f => f.IndexOf("contrast", StringComparison.OrdinalIgnoreCase) < 0) ?? string.Empty;
        }

        /// <summary>"SpotifyAB.SpotifyMusic" → "Spotify Music".</summary>
        public static string Prettify(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return packageName;
            var part = packageName.Contains(".") ? packageName.Substring(packageName.IndexOf('.') + 1) : packageName;
            part = part.Replace('.', ' ').Replace('_', ' ');
            part = Regex.Replace(part, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            return part.Trim();
        }
    }

    internal static class PowerShell
    {
        public static string Run(string script, TimeSpan timeout)
        {
            try
            {
                var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(exe)) exe = "powershell.exe";
                var psi = new ProcessStartInfo(exe,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        try { p.Kill(); } catch { }
                        Log.Write("PowerShell: timeout");
                        return null;
                    }
                    var err = errTask.Result;
                    if (!string.IsNullOrWhiteSpace(err)) Log.Write("PowerShell stderr: " + err.Trim());
                    return outTask.Result;
                }
            }
            catch (Exception ex)
            {
                Log.Write("PowerShell non disponibile: " + ex.Message);
                return null;
            }
        }
    }
}
