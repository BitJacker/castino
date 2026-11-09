using System;
using System.Collections.Generic;
using System.Linq;

namespace Lindo.Core
{
    /// <summary>
    /// Operazioni su percorsi in stile Windows, implementate come pura manipolazione di stringhe
    /// (così funzionano e si possono testare anche fuori da Windows).
    /// </summary>
    public static class WinPath
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var p = path.Trim().Trim('"').Replace('/', '\\');
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);

            // Collassa i separatori doppi (tranne il prefisso UNC)
            var unc = p.StartsWith(@"\\", StringComparison.Ordinal);
            var body = unc ? p.Substring(2) : p;
            while (body.Contains(@"\\")) body = body.Replace(@"\\", @"\");
            p = unc ? @"\\" + body : body;

            // Risolve segmenti "." e ".."
            var parts = p.Split('\\').ToList();
            var stack = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                var s = parts[i];
                if (s == "." ) continue;
                if (s == "..")
                {
                    if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(s);
            }
            p = string.Join(@"\", stack);

            p = p.TrimEnd('\\');
            if (p.Length == 2 && p[1] == ':') p += @"\"; // "C:" -> "C:\"
            return p;
        }

        public static bool Equal(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>True se <paramref name="child"/> è uguale a <paramref name="parent"/> o si trova al suo interno.</summary>
        public static bool IsSameOrInside(string child, string parent)
        {
            var c = Normalize(child);
            var p = Normalize(parent);
            if (c.Length == 0 || p.Length == 0) return false;
            if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = p.EndsWith(@"\") ? p : p + @"\";
            return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsStrictlyInside(string child, string parent) =>
            IsSameOrInside(child, parent) && !Equal(child, parent);

        public static string GetFileName(string path)
        {
            var p = Normalize(path);
            var i = p.LastIndexOf('\\');
            return i < 0 ? p : p.Substring(i + 1);
        }

        public static string GetParent(string path)
        {
            var p = Normalize(path);
            var i = p.LastIndexOf('\\');
            if (i < 0) return string.Empty;
            if (i == 2 && p[1] == ':') return p.Length > 3 ? p.Substring(0, 3) : string.Empty;
            return p.Substring(0, i);
        }

        public static string Combine(string a, string b) => Normalize(Normalize(a) + @"\" + b);

        /// <summary>Profondità (numero di segmenti) del percorso. "C:\" = 1, "C:\A" = 2.</summary>
        public static int Depth(string path)
        {
            var p = Normalize(path).TrimEnd('\\');
            if (p.Length == 0) return 0;
            return p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        /// <summary>
        /// Estrae il percorso dell'eseguibile da una riga di comando tipo
        /// <c>"C:\Program Files\X\uninst.exe" /S</c> oppure <c>C:\X\uninst.exe /S</c>
        /// o da un riferimento icona tipo <c>C:\X\app.exe,0</c>.
        /// </summary>
        public static string ExtractExecutable(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
            var s = commandLine.Trim();
            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                return end > 1 ? Normalize(s.Substring(1, end - 1)) : Normalize(s.Trim('"'));
            }
            // Riferimento icona "file,indice"
            var comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s.Substring(comma + 1).Trim(), out _))
                s = s.Substring(0, comma);

            // Cerca la prima estensione eseguibile nota
            foreach (var ext in new[] { ".exe", ".msi", ".bat", ".cmd", ".dll", ".ico" })
            {
                var idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) return Normalize(s.Substring(0, idx + ext.Length));
            }
            var space = s.IndexOf(' ');
            return Normalize(space > 0 ? s.Substring(0, space) : s);
        }

        /// <summary>Separa eseguibile e argomenti di una riga di comando.</summary>
        public static (string File, string Arguments) SplitCommand(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return (string.Empty, string.Empty);
            var s = commandLine.Trim();
            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                if (end > 1) return (s.Substring(1, end - 1), s.Substring(end + 1).Trim());
                return (s.Trim('"'), string.Empty);
            }
            foreach (var ext in new[] { ".exe", ".bat", ".cmd" })
            {
                var idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var cut = idx + ext.Length;
                    if (cut == s.Length || s[cut] == ' ')
                        return (s.Substring(0, cut), s.Substring(cut).Trim());
                }
            }
            var sp = s.IndexOf(' ');
            return sp > 0 ? (s.Substring(0, sp), s.Substring(sp + 1).Trim()) : (s, string.Empty);
        }
    }
}
