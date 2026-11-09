using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Lindo.Core;

namespace Lindo.Monitor
{
    /// <summary>
    /// Processo leggero (nessuna finestra) che ogni 30 secondi guarda quali programmi sono aperti
    /// e salva in %LOCALAPPDATA%\Lindo\usage.tsv: tempo di utilizzo, CPU, memoria, avvii.
    /// I dati restano sul PC: non vengono inviati da nessuna parte.
    /// </summary>
    internal static class Program
    {
        private const int SampleSeconds = 30;
        private const int SaveEverySamples = 10; // ~5 minuti

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\LindoMonitor", out created))
            {
                if (!created) return; // già in esecuzione

                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lindo");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "usage.tsv");
                var store = Load(file);
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                // pid → (percorso, cpu precedente in ms)
                var known = new Dictionary<int, (string Path, long CpuMs)>();
                int samples = 0;

                while (true)
                {
                    try
                    {
                        var now = DateTime.Now;
                        var seenThisRound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var alive = new HashSet<int>();
                        foreach (var p in Process.GetProcesses())
                        {
                            try
                            {
                                if (p.Id <= 4) continue;
                                alive.Add(p.Id);
                                var (path, cpu) = Inspect(p.Id);
                                if (string.IsNullOrEmpty(path) || WinPath.IsSameOrInside(path, windows)) continue;

                                var rec = store.Get(path, Path.GetFileNameWithoutExtension(path));
                                if (known.TryGetValue(p.Id, out var prev) && string.Equals(prev.Path, path, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (cpu >= prev.CpuMs) rec.CpuMs += cpu - prev.CpuMs;
                                }
                                else if (samples > 0 && !seenThisRound.Contains(path) && !IsRunningElsewhere(known, path, p.Id))
                                {
                                    rec.Launches++;
                                }
                                known[p.Id] = (path, cpu);

                                var memMb = p.WorkingSet64 / (1024 * 1024);
                                if (seenThisRound.Add(path))
                                {
                                    rec.SecondsRunning += SampleSeconds;
                                    rec.Samples++;
                                    rec.MemorySamplesMb += memMb;
                                }
                                else
                                {
                                    rec.MemorySamplesMb += memMb; // più processi dello stesso programma (es. browser)
                                }
                                if (memMb > rec.PeakMemoryMb) rec.PeakMemoryMb = memMb;
                                rec.LastSeen = now;
                            }
                            catch { }
                            finally { p.Dispose(); }
                        }
                        foreach (var dead in new List<int>(known.Keys)) if (!alive.Contains(dead)) known.Remove(dead);

                        if (samples++ % SaveEverySamples == 0)
                        {
                            store.Prune(now, TimeSpan.FromDays(180));
                            Save(file, store);
                        }
                    }
                    catch { }
                    Thread.Sleep(SampleSeconds * 1000);
                }
            }
        }

        private static bool IsRunningElsewhere(Dictionary<int, (string Path, long CpuMs)> known, string path, int pid)
        {
            foreach (var kv in known)
                if (kv.Key != pid && string.Equals(kv.Value.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static (string Path, long CpuMs) Inspect(int pid)
        {
            var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return (null, 0);
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                string path = QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
                long cpu = 0;
                if (GetProcessTimes(h, out _, out _, out var k, out var u)) cpu = (k + u) / 10000;
                return (path, cpu);
            }
            finally { CloseHandle(h); }
        }

        private static UsageStore Load(string file)
        {
            try { return File.Exists(file) ? UsageStore.Parse(File.ReadAllText(file)) : new UsageStore(); }
            catch { return new UsageStore(); }
        }

        private static void Save(string file, UsageStore store)
        {
            try
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, store.Serialize(), Encoding.UTF8);
                if (File.Exists(file)) File.Replace(tmp, file, null);
                else File.Move(tmp, file);
            }
            catch { }
        }
    }
}
