using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lindo.Core
{
    /// <summary>Informazioni lette dalla sezione [Version] di un file .inf di driver.</summary>
    public sealed class InfInfo
    {
        public string Provider { get; set; } = string.Empty;
        public string Class { get; set; } = string.Empty;
        public string ClassGuid { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public string CatalogFile { get; set; } = string.Empty;
        /// <summary>Un nome leggibile (primo nome di dispositivo o descrizione), se presente.</summary>
        public string Description { get; set; } = string.Empty;
    }

    public static class InfParser
    {
        private static readonly Regex Section = new Regex(@"^\s*\[(?<n>[^\]]+)\]\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex KeyValue = new Regex(@"^\s*(?<k>[^=;]+?)\s*=\s*(?<v>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Token = new Regex(@"%(?<t>[^%]+)%", RegexOptions.CultureInvariant);

        /// <summary>Analizza il testo di un .inf. Non lancia eccezioni su file malformati.</summary>
        public static InfInfo Parse(string text)
        {
            var info = new InfInfo();
            if (string.IsNullOrEmpty(text)) return info;

            var sections = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> current = null;
            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = StripComment(raw);
                if (line.Trim().Length == 0) continue;
                var m = Section.Match(line);
                if (m.Success)
                {
                    var name = m.Groups["n"].Value.Trim();
                    if (!sections.TryGetValue(name, out current))
                    {
                        current = new List<KeyValuePair<string, string>>();
                        sections[name] = current;
                    }
                    continue;
                }
                if (current == null) continue;
                var kv = KeyValue.Match(line);
                if (kv.Success) current.Add(new KeyValuePair<string, string>(kv.Groups["k"].Value.Trim(), kv.Groups["v"].Value.Trim()));
                else current.Add(new KeyValuePair<string, string>(line.Trim(), string.Empty));
            }

            // Stringhe: preferisce la sezione neutra, poi inglese/italiano
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var secName in sections.Keys.Where(k => k.StartsWith("Strings", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(k => k.Equals("Strings", StringComparison.OrdinalIgnoreCase) ? 0 : k.EndsWith(".0409") ? 1 : k.EndsWith(".0410") ? 2 : 3))
            {
                foreach (var kv in sections[secName])
                    if (!strings.ContainsKey(kv.Key)) strings[kv.Key] = Unquote(kv.Value);
            }

            string Resolve(string v)
            {
                v = Unquote(v ?? string.Empty);
                return Token.Replace(v, t => strings.TryGetValue(t.Groups["t"].Value, out var s) ? s : t.Value).Trim();
            }

            if (sections.TryGetValue("Version", out var version))
            {
                foreach (var kv in version)
                {
                    switch (kv.Key.ToLowerInvariant())
                    {
                        case "provider": info.Provider = Resolve(kv.Value); break;
                        case "class": info.Class = Resolve(kv.Value); break;
                        case "classguid": info.ClassGuid = Resolve(kv.Value); break;
                        case "catalogfile": case "catalogfile.nt": case "catalogfile.ntamd64":
                            if (info.CatalogFile.Length == 0) info.CatalogFile = Resolve(kv.Value); break;
                        case "driverver":
                            var parts = Resolve(kv.Value).Split(',');
                            if (DateTime.TryParseExact(parts[0].Trim(), new[] { "MM/dd/yyyy", "M/d/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                                info.Date = d;
                            if (parts.Length > 1) info.Version = parts[1].Trim();
                            break;
                    }
                }
            }

            // Descrizione: primo nome di dispositivo nella sezione del produttore
            if (sections.TryGetValue("Manufacturer", out var manu) && manu.Count > 0)
            {
                var models = manu[0].Value.Split(',').Select(x => x.Trim()).ToList();
                var baseName = models.FirstOrDefault() ?? string.Empty;
                var candidates = new List<string> { baseName };
                candidates.AddRange(models.Skip(1).Select(dec => baseName + "." + dec));
                foreach (var c in candidates)
                {
                    if (c.Length == 0 || !sections.TryGetValue(c, out var list) || list.Count == 0) continue;
                    var desc = Resolve(list[0].Key);
                    if (desc.Length > 0 && !desc.Contains("%")) { info.Description = desc; break; }
                }
            }
            if (info.Description.Length == 0 && strings.Count > 0)
            {
                var guess = strings.FirstOrDefault(kv => kv.Key.IndexOf("desc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                         kv.Key.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0);
                info.Description = guess.Value ?? string.Empty;
            }
            return info;
        }

        private static string StripComment(string line)
        {
            // Il ';' fuori dalle virgolette inizia un commento
            var inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuotes = !inQuotes;
                else if (line[i] == ';' && !inQuotes) return line.Substring(0, i);
            }
            return line;
        }

        private static string Unquote(string s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return s.Replace("\"\"", "\"");
        }
    }

    public static class DriverRules
    {
        /// <summary>
        /// Classi di driver indispensabili al funzionamento del PC: Lindo non li elimina mai
        /// (scheda video, rete, dischi, chipset, tastiera, mouse, audio, USB…).
        /// </summary>
        public static readonly HashSet<string> CriticalClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Display", "Net", "NetService", "NetTrans", "NetClient", "System", "HDC", "SCSIAdapter", "DiskDrive", "Volume",
            "VolumeSnapshot", "Processor", "Firmware", "Battery", "Keyboard", "Mouse", "HIDClass", "USB", "USBDevice",
            "Bluetooth", "Monitor", "MEDIA", "AudioEndpoint", "AudioProcessingObject", "Computer", "SecurityDevices",
            "SmartCardReader", "Biometric", "Camera", "Sensor", "Extension", "SoftwareComponent", "Net Service",
            "MTD", "SDHost", "Ports", "Infrared", "Modem", "PCMCIA", "1394", "FDC", "FloppyDisk", "CDROM",
            "TapeDrive", "LegacyDriver", "Ucm", "UCMCxUcsi", "SoftwareDevice", "DiskDrive", "Intel", "AMD",
        };

        /// <summary>Editori i cui driver non vengono mai collegati a un'app (driver di sistema/hardware generico).</summary>
        private static readonly HashSet<string> SystemProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "intel", "amd", "advancedmicrodevices", "nvidia", "realtek", "realteksemiconductor", "qualcomm",
            "broadcom", "mediatek", "synaptics", "elan", "conexant", "idt", "dolby", "dts", "waves", "wavesaudio",
        };

        public static bool IsCritical(string driverClass) =>
            !string.IsNullOrWhiteSpace(driverClass) && CriticalClasses.Contains(driverClass.Trim());

        /// <summary>
        /// Quanto un driver sembra appartenere a un'app (es. driver "EPSON" della classe Printer per l'app "Epson Print and Scan").
        /// </summary>
        public static MatchStrength MatchDriverToApp(InfInfo driver, AppInfo app)
        {
            if (driver == null || app == null) return MatchStrength.None;
            if (IsCritical(driver.Class)) return MatchStrength.None;
            var prov = NameMatcher.PublisherKey(driver.Provider);
            if (prov.Length < 3 || SystemProviders.Contains(prov)) return MatchStrength.None;

            var appPub = NameMatcher.PublisherKey(app.Publisher);
            var appTokens = NameMatcher.Tokens(app.Name);
            var provTokens = NameMatcher.Tokens(driver.Provider);
            var descTokens = NameMatcher.Tokens(driver.Description);

            // Stesso editore (es. "SEIKO EPSON CORPORATION" e "EPSON")
            var samePublisher = appPub.Length >= 3 &&
                                (appPub == prov || appPub.Contains(prov) || prov.Contains(appPub) ||
                                 provTokens.Intersect(NameMatcher.Tokens(app.Publisher)).Any(t => t.Length >= 4));
            var nameMentionsProvider = provTokens.Intersect(appTokens).Any(t => t.Length >= 3);
            if (!samePublisher && !nameMentionsProvider) return MatchStrength.None;

            // Il nome del dispositivo cita l'app (es. "ScanSnap" in entrambi)
            var shared = descTokens.Intersect(appTokens.Where(t => !provTokens.Contains(t))).Any(t => t.Length >= 4);
            if (shared) return MatchStrength.Strong;

            // Classi tipiche delle app di periferiche
            var peripheral = new[] { "Printer", "PrintQueue", "Image", "WPD", "USB", "Ports", "Scanner", "SmartCardReader" }
                .Any(c => c.Equals(driver.Class, StringComparison.OrdinalIgnoreCase));
            return peripheral ? MatchStrength.Medium : MatchStrength.Weak;
        }
    }
}
