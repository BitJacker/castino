using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lindo.Core
{
    public enum MatchStrength
    {
        None = 0,
        Weak = 1,
        Medium = 2,
        Strong = 3,
    }

    /// <summary>
    /// Confronta nomi di cartelle / chiavi di registro con il nome di un'applicazione.
    /// È volutamente prudente: preferisce non trovare un residuo piuttosto che
    /// proporre la cancellazione di qualcosa che appartiene a un altro programma.
    /// </summary>
    public static class NameMatcher
    {
        // Parole che non identificano un programma da sole.
        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "for", "and", "of", "by", "a", "an", "to", "on", "in", "with",
            "x64", "x86", "x32", "64", "32", "bit", "64bit", "32bit", "amd64", "arm64", "win32", "win64",
            "version", "ver", "v", "edition", "update", "updater", "setup", "installer", "install",
            "uninstall", "uninstaller", "windows", "win", "pc", "desktop", "app", "application", "applications",
            "software", "program", "programs", "tool", "tools", "free", "portable", "beta", "release",
            "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "srl",
            "spa", "bv", "oy", "ab", "kk", "plc", "pty", "sas", "sarl", "technologies", "technology",
            "games", "studio", "studios", "entertainment", "interactive", "systems", "solutions", "group",
            "international", "labs", "lab", "foundation", "project", "team", "community", "official",
            "user", "machine", "all", "users", "en", "us", "it", "en-us", "it-it", "language", "pack",
        };

        // Nomi troppo generici: una cartella con questo nome non viene mai associata a un'app per somiglianza.
        private static readonly HashSet<string> GenericFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "windows", "common", "commonfiles", "data", "cache", "caches", "temp", "tmp", "logs", "log",
            "config", "settings", "bin", "lib", "programs", "program", "packages", "package", "apps", "app",
            "updates", "update", "backup", "backups", "users", "default", "public", "shared", "tools", "games",
            "crashdumps", "crashreports", "plugins", "extensions", "resources", "assets", "documents", "downloads",
            "desktop", "pictures", "music", "videos", "local", "roaming", "locallow", "appdata", "system", "system32",
        };

        private static readonly Regex VersionLike = new Regex(@"^v?\d+([._-]\d+)*[a-z]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Brackets = new Regex(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.CultureInvariant);
        private static readonly Regex NonAlnum = new Regex(@"[^a-z0-9]+", RegexOptions.CultureInvariant);

        public static string RemoveDiacritics(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var ch in formD)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>Token significativi di un nome (minuscoli, senza versioni né parole generiche).</summary>
        public static IReadOnlyList<string> Tokens(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Array.Empty<string>();
            var s = RemoveDiacritics(name).ToLowerInvariant();
            s = s.Replace("®", " ").Replace("™", " ").Replace("©", " ");
            s = Brackets.Replace(s, " ");
            // Spezza CamelCase prima di abbassare? Già abbassato: gestiamo separatori.
            var raw = NonAlnum.Split(s);
            var result = new List<string>();
            foreach (var t in raw)
            {
                if (t.Length == 0) continue;
                if (Noise.Contains(t)) continue;
                if (VersionLike.IsMatch(t)) continue;
                result.Add(t);
            }
            return result;
        }

        /// <summary>Forma compatta: token significativi concatenati ("Visual Studio Code" → "visualstudiocode").</summary>
        public static string Compact(string name) => string.Concat(Tokens(name));

        /// <summary>Forma compatta "grezza": solo lettere e cifre, senza scartare parole.</summary>
        public static string RawCompact(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var s = RemoveDiacritics(name).ToLowerInvariant();
            return NonAlnum.Replace(s, string.Empty);
        }

        public static bool IsGenericName(string folderName)
        {
            var raw = RawCompact(folderName);
            if (raw.Length < 3) return true;
            if (GenericFolderNames.Contains(raw)) return true;
            var compact = Compact(folderName);
            return compact.Length < 3;
        }

        /// <summary>Nome dell'editore ripulito dalle forme societarie ("Google LLC" → "google").</summary>
        public static string PublisherKey(string publisher) => Compact(publisher);

        /// <summary>Nome dell'app senza il nome dell'editore ("Google Chrome" di "Google LLC" → "Chrome").</summary>
        public static string StripPublisher(string appName, string publisher)
        {
            var pubTokens = new HashSet<string>(Tokens(publisher));
            if (pubTokens.Count == 0) return appName;
            var rest = Tokens(appName).Where(t => !pubTokens.Contains(t)).ToList();
            return rest.Count == 0 ? appName : string.Join(" ", rest);
        }

        /// <summary>
        /// Quanto il nome di una cartella/chiave corrisponde a un'applicazione.
        /// </summary>
        public static MatchStrength Match(string candidateName, string appName, string installFolderName = null, string publisher = null)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(appName))
                return MatchStrength.None;

            var candRaw = RawCompact(candidateName);
            var candCompact = Compact(candidateName);
            var appRaw = RawCompact(appName);
            var appCompact = Compact(appName);

            // Una cartella col nome dell'editore (es. "Google" per "Google Chrome") è un contenitore
            // condiviso: non va mai associata in blocco, si cercano invece le sue sottocartelle.
            var pubKey = PublisherKey(publisher);
            if (pubKey.Length >= 2 && candCompact == pubKey && appCompact != pubKey)
                return MatchStrength.None;

            // Il nome esatto della cartella di installazione è il segnale più forte.
            if (!string.IsNullOrWhiteSpace(installFolderName))
            {
                var instRaw = RawCompact(installFolderName);
                if (instRaw.Length >= 3 && instRaw == candRaw && !IsGenericName(installFolderName))
                    return MatchStrength.Strong;
            }

            if (candCompact.Length < 3 || appCompact.Length < 3) return MatchStrength.None;

            if (candRaw == appRaw || candCompact == appCompact)
                return IsGenericName(candidateName) ? MatchStrength.Weak : MatchStrength.Strong;

            if (IsGenericName(candidateName)) return MatchStrength.None;

            // "Spotify" vs "Spotify Music" / "VLC" vs "VLC media player"
            var appTokens = Tokens(appName);
            var candTokens = Tokens(candidateName);

            if (appTokens.Count > 1 && candCompact.Length >= 3 && appTokens[0] == candCompact)
                return MatchStrength.Medium; // "VLC" per "VLC media player"

            if (candCompact.Length >= 4 && appCompact.StartsWith(candCompact, StringComparison.Ordinal))
            {
                // La cartella è il prefisso del nome dell'app: forte se copre la maggior parte del nome
                // o se coincide con il primo token (nome del prodotto).
                if (appTokens.Count > 0 && appTokens[0] == candCompact) return MatchStrength.Medium;
                if (candCompact.Length * 2 >= appCompact.Length) return MatchStrength.Medium;
                return MatchStrength.Weak;
            }

            if (appCompact.Length >= 4 && candCompact.StartsWith(appCompact, StringComparison.Ordinal))
            {
                // "Discord" vs cartella "DiscordPTB": possibile ma meno sicuro
                return candCompact.Length - appCompact.Length <= 4 ? MatchStrength.Medium : MatchStrength.Weak;
            }

            // Stessi token in ordine diverso o sottoinsieme significativo
            if (candTokens.Count > 0 && appTokens.Count > 0)
            {
                var common = candTokens.Intersect(appTokens).ToList();
                if (common.Count == candTokens.Count && common.Count == appTokens.Count) return MatchStrength.Strong;
                if (common.Count == candTokens.Count && common.Sum(t => t.Length) >= 5) return MatchStrength.Weak;
            }

            return MatchStrength.None;
        }
    }
}
