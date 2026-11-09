using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Lindo.Core;
using Microsoft.Win32;

namespace Lindo.Services
{
    internal sealed class ResourceSnapshot
    {
        public double CpuPercent { get; set; }
        public long RamTotal { get; set; }
        public long RamAvailable { get; set; }
        public int MemoryLoad { get; set; }
        public List<ProcessUsage> TopCpu { get; set; } = new List<ProcessUsage>();
        public List<ProcessUsage> TopRam { get; set; } = new List<ProcessUsage>();
    }

    internal sealed class GpuInfo
    {
        public List<string> Names { get; set; } = new List<string>();
        /// <summary>Tutti gli adattatori, compresi quelli virtuali (per dire almeno cosa c'è).</summary>
        public List<string> AllNames { get; set; } = new List<string>();
        /// <summary>true = attiva, false = non attiva, null = impostazione non presente (scheda o driver non compatibili).</summary>
        public bool? HardwareScheduling { get; set; }
        public bool HasTwoGpus => Names.Count >= 2;
    }

    /// <summary>
    /// Lettura di CPU, RAM e schede video, e preferenza "scheda video dedicata" per i giochi.
    /// La preferenza si scrive nello stesso posto in cui la scrive Impostazioni → Sistema → Schermo → Grafica
    /// (HKCU\Software\Microsoft\DirectX\UserGpuPreferences) e il valore originale viene salvato per «Ripristina tutto».
    /// </summary>
    internal static class ResourceService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint Low; public uint High; public long Value => ((long)High << 32) | Low; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        private const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

        /// <summary>Misura per circa un secondo l'uso di CPU e RAM e i programmi che ne usano di più.</summary>
        public static ResourceSnapshot Measure(int top = 4)
        {
            var snap = new ResourceSnapshot();

            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                snap.RamTotal = (long)mem.ullTotalPhys;
                snap.RamAvailable = (long)mem.ullAvailPhys;
                snap.MemoryLoad = (int)mem.dwMemoryLoad;
            }

            var first = new Dictionary<int, (string Name, TimeSpan Cpu)>();
            foreach (var p in Process.GetProcesses())
            {
                try { first[p.Id] = (p.ProcessName, p.TotalProcessorTime); } catch { /* processo protetto */ }
                finally { p.Dispose(); }
            }
            GetSystemTimes(out var i1, out var k1, out var u1);
            var sw = Stopwatch.StartNew();
            Thread.Sleep(1000);
            GetSystemTimes(out var i2, out var k2, out var u2);
            var elapsed = sw.Elapsed;
            snap.CpuPercent = ResourceMath.CpuPercent(i1.Value, k1.Value, u1.Value, i2.Value, k2.Value, u2.Value);

            var usages = new List<ProcessUsage>();
            int selfId;
            using (var self = Process.GetCurrentProcess()) selfId = self.Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (ResourceMath.IsSystemIdle(p.ProcessName)) continue;
                    if (p.Id == selfId) continue; // Lindo stesso sta misurando
                    double cpu = 0;
                    try
                    {
                        if (first.TryGetValue(p.Id, out var f) && f.Name == p.ProcessName)
                            cpu = ResourceMath.ProcessCpuPercent(p.TotalProcessorTime - f.Cpu, elapsed, Environment.ProcessorCount);
                    }
                    catch { }
                    long ram = 0;
                    try { ram = p.WorkingSet64; } catch { }
                    usages.Add(new ProcessUsage { Name = p.ProcessName, Count = 1, MemoryBytes = ram, CpuPercent = cpu });
                }
                catch { }
                finally { p.Dispose(); }
            }
            var grouped = ResourceMath.Group(usages);
            snap.TopCpu = grouped.Where(g => g.CpuPercent >= 0.1).OrderByDescending(g => g.CpuPercent).Take(top).ToList();
            snap.TopRam = grouped.OrderByDescending(g => g.MemoryBytes).Take(top).ToList();
            return snap;
        }

        /// <summary>Nomi delle schede video e stato della "pianificazione GPU con accelerazione hardware".</summary>
        public static GpuInfo ReadGpu()
        {
            var info = new GpuInfo();
            var output = PowerShell.Run(
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }", TimeSpan.FromSeconds(30));
            var all = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            info.AllNames = all;
            info.Names = ResourceMath.RealGpus(all);
            try
            {
                using (var k = SystemInfo.OpenKey(@"HKLM\" + GraphicsDriversKey))
                {
                    var v = k?.GetValue("HwSchMode");
                    // 2 = attiva (valore scritto da Impostazioni). Non lo modifichiamo mai: si cambia solo da Impostazioni.
                    info.HardwareScheduling = v == null ? (bool?)null : Convert.ToInt32(v) == 2;
                }
            }
            catch { }
            return info;
        }

        /// <summary>Apre Impostazioni → Grafica (URI documentato da Microsoft: ms-settings:display-advancedgraphics).</summary>
        public static void OpenGraphicsSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Impostazioni grafica non apribili: " + ex.Message); }
        }

        /// <summary>Fa usare la scheda video dedicata (prestazioni elevate) al programma indicato.</summary>
        public static string SetHighPerformanceGpu(string exe)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return "File not found";
                using (var k = Registry.CurrentUser.CreateSubKey(GpuPrefKey))
                {
                    OptimizeService.RememberValue("gpupref|" + exe, k.GetValue(exe) as string);
                    k.SetValue(exe, ResourceMath.GpuPreferenceValue(2), RegistryValueKind.String);
                }
                Log.Write("Scheda video dedicata per " + exe);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Programmi a cui Lindo ha assegnato la scheda dedicata.</summary>
        public static List<string> LindoGpuPreferences() =>
            OptimizeService.BackupKeys("gpupref|").ToList();

        /// <summary>Rimette le preferenze grafiche com'erano prima di Lindo.</summary>
        public static void RestoreGpuPreferences()
        {
            foreach (var exe in OptimizeService.BackupKeys("gpupref|").ToList())
            {
                try
                {
                    var original = OptimizeService.RecallValue("gpupref|" + exe);
                    using (var k = Registry.CurrentUser.CreateSubKey(GpuPrefKey))
                    {
                        if (original == null || original == "<null>") k.DeleteValue(exe, false);
                        else k.SetValue(exe, original, RegistryValueKind.String);
                    }
                    OptimizeService.ForgetValue("gpupref|" + exe);
                }
                catch (Exception ex) { Log.Write("Ripristino grafica " + exe + ": " + ex.Message); }
            }
        }
    }
}
