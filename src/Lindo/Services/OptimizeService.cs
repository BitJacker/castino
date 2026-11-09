using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Lindo.Services
{
    internal sealed class OptimizeAction
    {
        public string Id { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Glyph { get; set; }
        public bool Reversible { get; set; } = true;
        public bool DefaultSelected { get; set; } = true;
        public Func<bool> IsApplied { get; set; }
        /// <summary>Applica. Restituisce null se ok, altrimenti un messaggio.</summary>
        public Func<string> Apply { get; set; }
        public Func<string> Restore { get; set; }
    }

    /// <summary>
    /// Ottimizzazioni sicure e reversibili. Prima di ogni modifica il valore originale viene salvato in
    /// %LOCALAPPDATA%\Lindo\ottimizzazioni-originali.tsv, così "Ripristina" rimette tutto com'era.
    /// </summary>
    internal static class OptimizeService
    {
        private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        private const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private static string BackupFile => Path.Combine(SystemInfo.DataFolder, "ottimizzazioni-originali.tsv");

        // ----------------------------------------------------------------- backup valori originali

        private static Dictionary<string, string> LoadBackup()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(BackupFile))
                    foreach (var l in File.ReadAllLines(BackupFile))
                    {
                        var i = l.IndexOf('\t');
                        if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                    }
            }
            catch { }
            return d;
        }

        private static void Remember(string key, string value)
        {
            var d = LoadBackup();
            if (d.ContainsKey(key)) return; // conserva sempre il valore ORIGINALE
            d[key] = value ?? "<null>";
            try { File.WriteAllLines(BackupFile, d.Select(kv => kv.Key + "\t" + kv.Value)); } catch { }
        }

        private static string Recall(string key) => LoadBackup().TryGetValue(key, out var v) ? v : null;

        private static void Forget(string key)
        {
            var d = LoadBackup();
            if (!d.Remove(key)) return;
            try { File.WriteAllLines(BackupFile, d.Select(kv => kv.Key + "\t" + kv.Value)); } catch { }
        }

        // ----------------------------------------------------------------- DWORD nel registro HKCU

        private static int? GetDword(string key, string name)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
            {
                var v = k?.GetValue(name);
                return v == null ? (int?)null : Convert.ToInt32(v);
            }
        }

        private static void SetDword(string key, string name, int? value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(key))
            {
                if (value.HasValue) k.SetValue(name, value.Value, RegistryValueKind.DWord);
                else k.DeleteValue(name, false);
            }
        }

        private static string ApplyDwords(string id, params (string Key, string Name, int Value)[] values)
        {
            try
            {
                foreach (var (key, name, value) in values)
                {
                    Remember(id + "|" + key + "|" + name, GetDword(key, name)?.ToString());
                    SetDword(key, name, value);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static string RestoreDwords(string id, params (string Key, string Name)[] values)
        {
            try
            {
                foreach (var (key, name) in values)
                {
                    var b = Recall(id + "|" + key + "|" + name);
                    if (b == null) continue;
                    SetDword(key, name, b == "<null>" ? (int?)null : int.Parse(b));
                    Forget(id + "|" + key + "|" + name);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ----------------------------------------------------------------- impostazioni di sistema (SystemParametersInfo)

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint uiParam, int[] pvParam, uint winIni);

        [StructLayout(LayoutKind.Sequential)]
        private struct STICKYKEYS { public uint cbSize; public uint dwFlags; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint uiParam, ref STICKYKEYS pvParam, uint winIni);

        private const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004;
        private const uint SPI_GETSTICKYKEYS = 0x003A, SPI_SETSTICKYKEYS = 0x003B;
        private const uint SKF_HOTKEYACTIVE = 0x00000004;
        private const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

        private static int[] GetMouse()
        {
            var v = new int[3];
            return SystemParametersInfo(SPI_GETMOUSE, 0, v, 0) ? v : null;
        }

        private static STICKYKEYS GetSticky()
        {
            var s = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf(typeof(STICKYKEYS)) };
            SystemParametersInfo(SPI_GETSTICKYKEYS, s.cbSize, ref s, 0);
            return s;
        }

        // ----------------------------------------------------------------- piano energetico

        private static string ActiveScheme()
        {
            var o = RunCapture(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/getactivescheme");
            var m = Regex.Match(o ?? "", @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        private static string RunCapture(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    return p.ExitCode == 0 ? o : null;
                }
            }
            catch { return null; }
        }

        // ----------------------------------------------------------------- elenco

        public static List<OptimizeAction> Actions(Func<List<StartupEntry>> startupProvider)
        {
            const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
            const string GameStoreKey = @"System\GameConfigStore";
            const string GameBarKey = @"Software\Microsoft\GameBar";
            const string VfxKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
            const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

            return new List<OptimizeAction>
            {
                new OptimizeAction
                {
                    Id = "power", Category = "Prestazioni", Glyph = "",
                    Title = "Piano energetico \"Prestazioni elevate\"",
                    Description = "Il processore non rallenta per risparmiare energia. Sui portatili la batteria dura un po' meno.",
                    IsApplied = () => { var a = ActiveScheme(); return a == HighPerformance || a == UltimatePerformance; },
                    Apply = () =>
                    {
                        var current = ActiveScheme();
                        if (current == HighPerformance || current == UltimatePerformance) return null;
                        Remember("power", current);
                        return RunCapture(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/setactive " + HighPerformance) != null
                            ? null : "Il piano \"Prestazioni elevate\" non è disponibile su questo PC";
                    },
                    Restore = () =>
                    {
                        var b = Recall("power");
                        if (b == null || b == "<null>") return null;
                        Forget("power");
                        return RunCapture(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/setactive " + b) != null ? null : "Impossibile ripristinare il piano energetico";
                    },
                },
                new OptimizeAction
                {
                    Id = "gamedvr", Category = "Gaming", Glyph = "",
                    Title = "Niente registrazione in background di Xbox Game Bar",
                    Description = "Game Bar non registra più di nascosto mentre giochi: spesso fa guadagnare FPS. Puoi sempre registrare a mano.",
                    IsApplied = () => GetDword(GameDvrKey, "AppCaptureEnabled") == 0 && GetDword(GameStoreKey, "GameDVR_Enabled") == 0,
                    Apply = () => ApplyDwords("gamedvr", (GameDvrKey, "AppCaptureEnabled", 0), (GameStoreKey, "GameDVR_Enabled", 0)),
                    Restore = () => RestoreDwords("gamedvr", (GameDvrKey, "AppCaptureEnabled"), (GameStoreKey, "GameDVR_Enabled")),
                },
                new OptimizeAction
                {
                    Id = "gamemode", Category = "Gaming", Glyph = "",
                    Title = "Modalità gioco attiva",
                    Description = "Windows dà priorità al gioco aperto e rimanda aggiornamenti e notifiche.",
                    IsApplied = () => GetDword(GameBarKey, "AutoGameModeEnabled") != 0,
                    Apply = () => ApplyDwords("gamemode", (GameBarKey, "AutoGameModeEnabled", 1)),
                    Restore = () => RestoreDwords("gamemode", (GameBarKey, "AutoGameModeEnabled")),
                },
                new OptimizeAction
                {
                    Id = "mouse", Category = "Gaming", Glyph = "",
                    Title = "Accelerazione del mouse disattivata",
                    Description = "Toglie \"Precisione puntatore avanzata\": il mirino si muove sempre della stessa quantità, utile negli sparatutto.",
                    IsApplied = () => { var m = GetMouse(); return m != null && m[2] == 0; },
                    Apply = () =>
                    {
                        var m = GetMouse();
                        if (m == null) return "Impossibile leggere le impostazioni del mouse";
                        if (m[2] == 0) return null;
                        Remember("mouse", string.Join(",", m));
                        return SystemParametersInfo(SPI_SETMOUSE, 0, new[] { 0, 0, 0 }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) ? null : "Impostazione del mouse non applicata";
                    },
                    Restore = () =>
                    {
                        var b = Recall("mouse");
                        if (b == null) return null;
                        var v = b.Split(',').Select(int.Parse).ToArray();
                        Forget("mouse");
                        return SystemParametersInfo(SPI_SETMOUSE, 0, v, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) ? null : "Impossibile ripristinare il mouse";
                    },
                },
                new OptimizeAction
                {
                    Id = "sticky", Category = "Gaming", Glyph = "",
                    Title = "Niente popup \"Tasti permanenti\" premendo Maiusc 5 volte",
                    Description = "Evita che il gioco venga interrotto dal messaggio dei Tasti permanenti.",
                    IsApplied = () => (GetSticky().dwFlags & SKF_HOTKEYACTIVE) == 0,
                    Apply = () =>
                    {
                        var s = GetSticky();
                        if ((s.dwFlags & SKF_HOTKEYACTIVE) == 0) return null;
                        Remember("sticky", s.dwFlags.ToString());
                        s.dwFlags &= ~SKF_HOTKEYACTIVE;
                        return SystemParametersInfo(SPI_SETSTICKYKEYS, s.cbSize, ref s, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) ? null : "Impostazione non applicata";
                    },
                    Restore = () =>
                    {
                        var b = Recall("sticky");
                        if (b == null) return null;
                        var s = GetSticky();
                        s.dwFlags = uint.Parse(b);
                        Forget("sticky");
                        return SystemParametersInfo(SPI_SETSTICKYKEYS, s.cbSize, ref s, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) ? null : "Impossibile ripristinare";
                    },
                },
                new OptimizeAction
                {
                    Id = "visual", Category = "Prestazioni", Glyph = "",
                    Title = "Effetti visivi leggeri",
                    Description = "Toglie animazioni, ombre e trasparenze di Windows: molto utile sui PC poco potenti. Vale dal prossimo accesso.",
                    IsApplied = () => GetDword(VfxKey, "VisualFXSetting") == 2 && GetDword(PersonalizeKey, "EnableTransparency") == 0,
                    Apply = () => ApplyDwords("visual", (VfxKey, "VisualFXSetting", 2), (PersonalizeKey, "EnableTransparency", 0)),
                    Restore = () => RestoreDwords("visual", (VfxKey, "VisualFXSetting"), (PersonalizeKey, "EnableTransparency")),
                },
                new OptimizeAction
                {
                    Id = "startup", Category = "Avvio", Glyph = "",
                    Title = "Togli le app inutili dall'avvio",
                    Description = "Disattiva all'avvio launcher, aggiornatori e app di chat/giochi (es. Steam, Discord, FortiClient). Antivirus, driver, audio e touchpad restano.",
                    IsApplied = () => !startupProvider().Any(e => e.Enabled && e.Suggested),
                    Apply = () =>
                    {
                        var errors = new List<string>();
                        foreach (var e in startupProvider().Where(x => x.Enabled && x.Suggested))
                        {
                            Remember("startup|" + e.Id, "1");
                            var err = StartupService.SetEnabled(e, false);
                            if (err != null) errors.Add(e.Name + ": " + err);
                        }
                        return errors.Count == 0 ? null : string.Join("; ", errors);
                    },
                    Restore = () =>
                    {
                        var ids = LoadBackup().Keys.Where(k => k.StartsWith("startup|")).Select(k => k.Substring(8)).ToList();
                        foreach (var e in startupProvider().Where(x => ids.Contains(x.Id)))
                        {
                            StartupService.SetEnabled(e, true);
                            Forget("startup|" + e.Id);
                        }
                        return null;
                    },
                },
                new OptimizeAction
                {
                    Id = "clean", Category = "Pulizia", Glyph = "", Reversible = false,
                    Title = "Pulisci file temporanei e cache",
                    Description = "Temporanei dell'utente e di Windows, cache delle miniature e rapporti di errore (come in Pulizia rapida).",
                    IsApplied = () => false,
                    Apply = () =>
                    {
                        foreach (var t in QuickClean.Targets().Where(t => !t.IsRecycleBin)) QuickClean.Clean(t);
                        return null;
                    },
                    Restore = () => null,
                },
            };
        }

        public static bool HasBackup(string id) => LoadBackup().Keys.Any(k => k == id || k.StartsWith(id + "|"));
    }
}
