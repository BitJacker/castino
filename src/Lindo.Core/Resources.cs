using System;
using System.Collections.Generic;
using System.Linq;

namespace Lindo.Core
{
    /// <summary>Uso di un programma (somma di tutti i suoi processi con lo stesso nome).</summary>
    public sealed class ProcessUsage
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public long MemoryBytes { get; set; }
        public double CpuPercent { get; set; }
    }

    /// <summary>Calcoli puri per la lettura delle risorse (testabili senza Windows).</summary>
    public static class ResourceMath
    {
        /// <summary>
        /// Percentuale di CPU occupata tra due letture di GetSystemTimes.
        /// Il tempo "kernel" comprende anche quello di inattività, come documentato da Microsoft.
        /// </summary>
        public static double CpuPercent(long idle1, long kernel1, long user1, long idle2, long kernel2, long user2)
        {
            var idle = idle2 - idle1;
            var total = (kernel2 - kernel1) + (user2 - user1);
            if (total <= 0) return 0;
            var busy = 100.0 * (total - idle) / total;
            return Math.Max(0, Math.Min(100, busy));
        }

        /// <summary>Percentuale della CPU totale usata da un processo in un intervallo.</summary>
        public static double ProcessCpuPercent(TimeSpan cpuDelta, TimeSpan elapsed, int processors)
        {
            if (elapsed.TotalMilliseconds <= 0 || processors <= 0) return 0;
            var p = 100.0 * cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * processors);
            return Math.Max(0, Math.Min(100, p));
        }

        /// <summary>Unisce i processi con lo stesso nome (es. tutte le schede di Chrome) e li ordina.</summary>
        public static List<ProcessUsage> Group(IEnumerable<ProcessUsage> processes)
        {
            return processes
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessUsage
                {
                    Name = g.First().Name,
                    Count = g.Sum(x => Math.Max(1, x.Count)),
                    MemoryBytes = g.Sum(x => x.MemoryBytes),
                    CpuPercent = g.Sum(x => x.CpuPercent),
                })
                .ToList();
        }

        /// <summary>Processi di sistema che non ha senso proporre come "colpevoli".</summary>
        public static bool IsSystemIdle(string name) =>
            string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "System Idle Process", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase);

        /// <summary>Testo da mettere nel valore di UserGpuPreferences (Impostazioni → Grafica): 2 = prestazioni elevate.</summary>
        public static string GpuPreferenceValue(int preference) => "GpuPreference=" + preference + ";";

        /// <summary>Legge la preferenza da un valore come "GpuPreference=2;" (eventualmente con altre opzioni).</summary>
        public static int? ParseGpuPreference(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            foreach (var part in value.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(kv[1].Trim(), out var n)) return n;
            }
            return null;
        }

        /// <summary>Le schede video "vere": esclude gli adattatori virtuali e quelli di base di Windows.</summary>
        public static List<string> RealGpus(IEnumerable<string> names)
        {
            var skip = new[] { "Microsoft Basic Display", "Microsoft Basic Render", "Microsoft Remote Display", "Remote Desktop", "Hyper-V", "Virtual", "Parsec", "Citrix", "Meta Virtual Monitor" };
            return names.Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n.Trim())
                        .Where(n => !skip.Any(s => n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }
    }
}
