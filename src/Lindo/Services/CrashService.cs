using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using Lindo.Core;

namespace Lindo.Services
{
    internal sealed class CrashGroup
    {
        public string Title { get; set; }
        public string Kind { get; set; }      // "App", "Blocco", "Schermata blu", "Spegnimento"
        public string Explanation { get; set; }
        public string Advice { get; set; }
        public string Details { get; set; }
        public int Count { get; set; }
        public DateTime Last { get; set; }
        public bool IsSystem { get; set; }
    }

    /// <summary>Legge i crash dal Registro eventi di Windows e li spiega in italiano.</summary>
    internal static class CrashService
    {
        public static List<CrashGroup> Scan(int days = 30)
        {
            var since = DateTime.Now.AddDays(-days).ToUniversalTime().ToString("o");
            var groups = new Dictionary<string, CrashGroup>(StringComparer.OrdinalIgnoreCase);

            void Add(string key, Func<CrashGroup> make, DateTime when)
            {
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = make();
                g.Count++;
                if (when > g.Last) g.Last = when;
            }

            // Crash delle app (Application Error 1000) e blocchi (Application Hang 1002)
            foreach (var r in Query("Application",
                $"*[System[(Provider[@Name='Application Error'] and EventID=1000 or Provider[@Name='Application Hang'] and EventID=1002) and TimeCreated[@SystemTime>='{since}']]]"))
            {
                using (r)
                {
                    var when = r.TimeCreated ?? DateTime.Now;
                    var p = r.Properties;
                    string P(int i) => i < p.Count ? p[i]?.Value?.ToString() ?? "" : "";
                    var app = P(0);
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    if (r.Id == 1002)
                    {
                        Add("hang|" + app, () => new CrashGroup
                        {
                            Kind = "Hang",
                            Title = app + " stopped responding",
                            Explanation = "The program froze and was closed (by you or by Windows).",
                            Advice = "If it happens often: update the program, close heavy background apps and check your free disk space.",
                            Details = "Version " + P(1),
                        }, when);
                        continue;
                    }
                    var module = P(3);
                    var code = P(6);
                    var path = P(10);
                    Add("crash|" + app + "|" + module + "|" + code, () =>
                    {
                        var hint = CrashExplainer.ExplainModule(module);
                        return new CrashGroup
                        {
                            Kind = "App",
                            Title = app + " closed unexpectedly",
                            Explanation = CrashExplainer.ExplainException(code),
                            Advice = hint ?? "Update the program to the latest version; if it keeps happening, reinstall it.",
                            Details = "Module: " + (string.IsNullOrEmpty(module) ? "?" : module) + "  ·  code 0x" + code.Replace("0x", "") +
                                      (string.IsNullOrEmpty(path) ? "" : "  ·  " + path),
                        };
                    }, when);
                }
            }

            // Schermate blu (riavvio dopo bugcheck)
            foreach (var r in Query("System",
                $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[@SystemTime>='{since}']]]"))
            {
                using (r)
                {
                    var text = r.Properties.Count > 0 ? r.Properties[0]?.Value?.ToString() : null;
                    var code = CrashExplainer.ParseBugCheck(text) ?? 0;
                    var (name, hint) = CrashExplainer.ExplainBugCheck(code);
                    Add("bsod|" + code, () => new CrashGroup
                    {
                        Kind = "Blue screen",
                        IsSystem = true,
                        Title = "Blue screen: " + name,
                        Explanation = "Windows stopped and the PC restarted because of a serious error.",
                        Advice = hint,
                        Details = text ?? "",
                    }, r.TimeCreated ?? DateTime.Now);
                }
            }

            // Spegnimenti improvvisi senza schermata blu (Kernel-Power 41)
            foreach (var r in Query("System",
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[@SystemTime>='{since}']]]"))
            {
                using (r)
                {
                    long bug = 0;
                    try { bug = Convert.ToInt64(r.Properties.Count > 0 ? r.Properties[0]?.Value : 0); } catch { }
                    if (bug != 0) continue; // c'è già la schermata blu corrispondente
                    Add("power", () => new CrashGroup
                    {
                        Kind = "Shutdown",
                        IsSystem = true,
                        Title = "The PC turned off without shutting down Windows",
                        Explanation = "The computer turned off or restarted suddenly, without a blue screen.",
                        Advice = "Usually a power cut, the power button held down, a flat battery or overheating. If it happens on its own, check temperatures and the power supply.",
                        Details = "",
                    }, r.TimeCreated ?? DateTime.Now);
                }
            }

            return groups.Values.OrderByDescending(g => g.Last).ToList();
        }

        private static IEnumerable<EventRecord> Query(string log, string xpath)
        {
            var list = new List<EventRecord>();
            try
            {
                using (var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true }))
                {
                    EventRecord r;
                    while ((r = reader.ReadEvent()) != null && list.Count < 500) list.Add(r);
                }
            }
            catch (Exception ex) { Log.Write("Registro eventi " + log + ": " + ex.Message); }
            return list;
        }

        public static string SaveReport(List<CrashGroup> groups)
        {
            var path = Path.Combine(SystemInfo.DataFolder, "rapporto-crash-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".txt");
            var sb = new StringBuilder();
            sb.AppendLine("Lindo crash report - " + DateTime.Now);
            sb.AppendLine();
            foreach (var g in groups)
            {
                sb.AppendLine($"[{g.Kind}] {g.Title}  ({g.Count} times, last: {g.Last:g})");
                sb.AppendLine("  What happened: " + g.Explanation);
                sb.AppendLine("  What to do: " + g.Advice);
                if (!string.IsNullOrEmpty(g.Details)) sb.AppendLine("  Details: " + g.Details);
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
    }
}
