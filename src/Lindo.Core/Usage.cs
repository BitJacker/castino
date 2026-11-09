using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Lindo.Core
{
    /// <summary>
    /// Decodifica le voci UserAssist del registro (HKCU\...\Explorer\UserAssist\{GUID}\Count):
    /// nome = percorso in ROT13, dati (Windows 7+) = 72 byte con contatore all'offset 4 e
    /// ultimo avvio (FILETIME UTC) all'offset 60.
    /// </summary>
    public static class UserAssist
    {
        /// <summary>Cartelle note usate al posto dei percorsi (GUID → variabile).</summary>
        public static readonly Dictionary<string, string> KnownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "{6D809377-6AF0-444B-8957-A3773F02200E}", "ProgramFiles" },
            { "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", "ProgramFilesX86" },
            { "{905E63B6-C1BF-494E-B29C-65B732D3D21A}", "ProgramFiles" },
            { "{F38BF404-1D43-42F2-9305-67DE0B28FC23}", "Windows" },
            { "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", "System" },
            { "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", "SystemX86" },
            { "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", "LocalAppData" },
            { "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", "RoamingAppData" },
            { "{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}", "Programs" },
            { "{0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8}", "CommonPrograms" },
            { "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", "Desktop" },
            { "{5E6C858F-0E22-4760-9AFE-EA3317B67173}", "UserProfile" },
        };

        public static string Rot13(string s)
        {
            if (s == null) return null;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Sostituisce un GUID di cartella nota iniziale con il percorso reale.</summary>
        public static string ExpandKnownFolder(string decodedName, Func<string, string> folderResolver)
        {
            if (string.IsNullOrEmpty(decodedName) || decodedName[0] != '{' || folderResolver == null) return decodedName;
            var end = decodedName.IndexOf('}');
            if (end < 0) return decodedName;
            var guid = decodedName.Substring(0, end + 1);
            if (!KnownFolders.TryGetValue(guid, out var key)) return decodedName;
            var folder = folderResolver(key);
            if (string.IsNullOrEmpty(folder)) return decodedName;
            return folder.TrimEnd('\\') + decodedName.Substring(end + 1);
        }

        /// <summary>Restituisce (numero di avvii, ultimo avvio) oppure null se i dati non sono nel formato atteso.</summary>
        public static (int Count, DateTime? LastRun)? ParseValue(byte[] data)
        {
            if (data == null || data.Length < 68) return null;
            var count = BitConverter.ToInt32(data, 4);
            var ft = BitConverter.ToInt64(data, 60);
            DateTime? last = null;
            if (ft > 0)
            {
                try { last = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { last = null; }
            }
            return (count, last);
        }
    }

    /// <summary>
    /// Stato abilitato/disabilitato delle app all'avvio, come lo salva Gestione attività in
    /// ...\Explorer\StartupApproved\Run|Run32|StartupFolder: 12 byte, primo byte pari = attiva,
    /// dispari (es. 03) = disattivata, seguito dal FILETIME della disattivazione.
    /// </summary>
    public static class StartupApproved
    {
        public static bool IsEnabled(byte[] data)
        {
            if (data == null || data.Length == 0) return true; // nessuna voce = attiva
            return (data[0] & 1) == 0;
        }

        public static byte[] Make(bool enabled, DateTime? when = null)
        {
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled)
            {
                var ft = (when ?? DateTime.Now).ToFileTimeUtc();
                Array.Copy(BitConverter.GetBytes(ft), 0, data, 4, 8);
            }
            return data;
        }
    }

    /// <summary>Statistiche d'uso di un eseguibile raccolte dal monitor di Lindo.</summary>
    public sealed class UsageRecord
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        /// <summary>Secondi in cui il programma è stato visto in esecuzione.</summary>
        public long SecondsRunning { get; set; }
        /// <summary>Tempo di CPU accumulato (ms).</summary>
        public long CpuMs { get; set; }
        /// <summary>Memoria massima vista (MB) e somma per la media.</summary>
        public long PeakMemoryMb { get; set; }
        public long MemorySamplesMb { get; set; }
        public long Samples { get; set; }
        public int Launches { get; set; }

        public long AverageMemoryMb => Samples > 0 ? MemorySamplesMb / Samples : 0;
    }

    /// <summary>Archivio testuale (TSV) delle statistiche d'uso: piccolo e leggibile.</summary>
    public sealed class UsageStore
    {
        private const string Header = "#lindo-usage-v1";
        public Dictionary<string, UsageRecord> Records { get; } = new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);

        public UsageRecord Get(string path, string name)
        {
            if (!Records.TryGetValue(path, out var r))
            {
                r = new UsageRecord { Path = path, Name = name ?? string.Empty, FirstSeen = DateTime.Now };
                Records[path] = r;
            }
            return r;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var r in Records.Values.OrderByDescending(x => x.LastSeen))
            {
                sb.Append(Clean(r.Path)).Append('\t')
                  .Append(Clean(r.Name)).Append('\t')
                  .Append(r.FirstSeen.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.LastSeen.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.SecondsRunning.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.CpuMs.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.PeakMemoryMb.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.MemorySamplesMb.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.Samples.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.Launches.ToString(CultureInfo.InvariantCulture)).AppendLine();
            }
            return sb.ToString();
        }

        public static UsageStore Parse(string text)
        {
            var store = new UsageStore();
            if (string.IsNullOrEmpty(text)) return store;
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("#")) continue;
                var p = line.Split('\t');
                if (p.Length < 10) continue;
                try
                {
                    long L(int i) => long.Parse(p[i], CultureInfo.InvariantCulture);
                    var r = new UsageRecord
                    {
                        Path = p[0],
                        Name = p[1],
                        FirstSeen = new DateTime(L(2)),
                        LastSeen = new DateTime(L(3)),
                        SecondsRunning = L(4),
                        CpuMs = L(5),
                        PeakMemoryMb = L(6),
                        MemorySamplesMb = L(7),
                        Samples = L(8),
                        Launches = (int)L(9),
                    };
                    if (r.Path.Length > 0) store.Records[r.Path] = r;
                }
                catch { /* riga corrotta: ignorata */ }
            }
            return store;
        }

        /// <summary>Elimina i dati più vecchi di <paramref name="maxAge"/> per non far crescere il file.</summary>
        public void Prune(DateTime now, TimeSpan maxAge)
        {
            foreach (var k in Records.Where(kv => now - kv.Value.LastSeen > maxAge).Select(kv => kv.Key).ToList())
                Records.Remove(k);
        }

        private static string Clean(string s) => (s ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    public static class TimeText
    {
        private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

        /// <summary>"oggi", "ieri", "5 giorni fa", "3 mesi fa", "1 anno fa".</summary>
        public static string Ago(DateTime when, DateTime now)
        {
            var days = (now.Date - when.Date).TotalDays;
            if (days < 1) return "oggi";
            if (days < 2) return "ieri";
            if (days < 30) return (int)days + " giorni fa";
            var months = (int)(days / 30.4);
            if (months < 12) return months == 1 ? "1 mese fa" : months + " mesi fa";
            var years = (int)(days / 365.25);
            return years <= 1 ? "1 anno fa" : years + " anni fa";
        }

        public static string Duration(long seconds)
        {
            if (seconds < 60) return seconds + " s";
            var m = seconds / 60;
            if (m < 60) return m + " min";
            var h = m / 60;
            return h < 48 ? h + " h " + (m % 60) + " min" : (h / 24) + " giorni";
        }
    }
}
