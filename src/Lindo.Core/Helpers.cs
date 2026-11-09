using System;
using System.Globalization;
using System.Linq;

namespace Lindo.Core
{
    public static class SizeFormat
    {
        private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

        public static string Format(long bytes)
        {
            if (bytes <= 0) return "—";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            var fmt = u == 0 ? "0" : v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
            return v.ToString(fmt, It) + " " + units[u];
        }
    }

    /// <summary>Regole per decidere quali chiavi di registro si possono eliminare.</summary>
    public static class RegistryGuard
    {
        private static readonly string[] AllowedBases =
        {
            @"HKCU\Software\",
            @"HKLM\SOFTWARE\",
            @"HKLM\SOFTWARE\WOW6432Node\",
        };

        private static readonly string[] UninstallBases =
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",
            @"HKCU\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",
        };

        public static bool CanDelete(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            var k = key.Trim().TrimEnd('\\');

            // Voce di disinstallazione: esattamente un livello sotto "Uninstall"
            foreach (var u in UninstallBases)
            {
                if (k.StartsWith(u, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = k.Substring(u.Length);
                    return rest.Length > 0 && !rest.Contains("\\");
                }
            }

            foreach (var b in AllowedBases.OrderByDescending(x => x.Length))
            {
                if (!k.StartsWith(b, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = k.Substring(b.Length);
                var parts = rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts.Length > 2) return false;
                if (LeftoverPlanner.HardProtectedRegistryNames.Contains(parts[0])) return false;
                if (parts.Length == 1 && LeftoverPlanner.ProtectedRegistryNames.Contains(parts[0])) return false;
                // HKLM\SOFTWARE\WOW6432Node è gestita dalla sua base specifica
                if (b == @"HKLM\SOFTWARE\" && parts[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            return false;
        }
    }
}
