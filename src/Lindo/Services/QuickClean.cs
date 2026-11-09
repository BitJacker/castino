using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lindo.Services
{
    /// <summary>Una categoria di file sicuramente eliminabili (temporanei, cache, cestino…).</summary>
    internal sealed class CleanTarget
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Glyph { get; set; }
        /// <summary>Cartelle di cui si elimina il CONTENUTO (non la cartella stessa).</summary>
        public List<string> Folders { get; set; } = new List<string>();
        /// <summary>Filtro sui nomi dei file (null = tutti).</summary>
        public Func<string, bool> FileFilter { get; set; }
        /// <summary>Solo file più vecchi di questo intervallo (protegge i file in uso da installazioni in corso).</summary>
        public TimeSpan MinAge { get; set; } = TimeSpan.Zero;
        public bool Recurse { get; set; } = true;
        public bool IsRecycleBin { get; set; }
    }

    internal static class QuickClean
    {
        public static List<CleanTarget> Targets()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            return new List<CleanTarget>
            {
                new CleanTarget
                {
                    Title = "File temporanei",
                    Description = "File lasciati dalle app nella cartella Temp del tuo utente.",
                    Glyph = "\uE7B8",
                    Folders = { Path.GetTempPath() },
                    MinAge = TimeSpan.FromHours(24),
                },
                new CleanTarget
                {
                    Title = "Temporanei di Windows",
                    Description = "Cartella Temp di sistema (C:\\Windows\\Temp).",
                    Glyph = "\uE770",
                    Folders = { Path.Combine(win, "Temp") },
                    MinAge = TimeSpan.FromHours(24),
                },
                new CleanTarget
                {
                    Title = "Cestino",
                    Description = "I file nel Cestino occupano ancora spazio sul disco.",
                    Glyph = "\uE74D",
                    IsRecycleBin = true,
                },
                new CleanTarget
                {
                    Title = "Cache delle miniature",
                    Description = "Anteprime delle immagini: Windows le ricrea quando servono.",
                    Glyph = "\uE91B",
                    Folders = { Path.Combine(local, @"Microsoft\Windows\Explorer") },
                    FileFilter = f => Path.GetFileName(f).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) ||
                                      Path.GetFileName(f).StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase),
                    Recurse = false,
                },
                new CleanTarget
                {
                    Title = "Rapporti errori e crash",
                    Description = "Segnalazioni errori di Windows e file di dump dei crash.",
                    Glyph = "\uE9D9",
                    Folders =
                    {
                        Path.Combine(local, "CrashDumps"),
                        Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive"),
                        Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue"),
                        Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
                        Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"),
                        Path.Combine(win, "Minidump"),
                    },
                },
                new CleanTarget
                {
                    Title = "File di Windows Update già installati",
                    Description = "Pacchetti scaricati da Windows Update (vengono riscaricati se servono).",
                    Glyph = "\uE895",
                    Folders = { Path.Combine(win, @"SoftwareDistribution\Download") },
                    MinAge = TimeSpan.FromDays(3),
                },
            };
        }

        public static long Measure(CleanTarget t)
        {
            if (t.IsRecycleBin)
            {
                var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                try { return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0; } catch { return 0; }
            }
            return Files(t).Sum(f => { try { return f.Length; } catch { return 0L; } });
        }

        public static long Clean(CleanTarget t)
        {
            if (t.IsRecycleBin)
            {
                var size = Measure(t);
                try
                {
                    Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                        Native.RecycleFlags.NoConfirmation | Native.RecycleFlags.NoProgressUI | Native.RecycleFlags.NoSound);
                }
                catch { return 0; }
                return size;
            }

            long freed = 0;
            foreach (var f in Files(t))
            {
                try
                {
                    var len = f.Length;
                    f.Attributes = FileAttributes.Normal;
                    f.Delete();
                    freed += len;
                }
                catch { /* file in uso: si salta */ }
            }

            // Cartelle rimaste vuote (mai la cartella radice)
            if (t.Recurse && t.FileFilter == null)
            {
                foreach (var root in t.Folders.Where(Directory.Exists))
                {
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToList())
                        {
                            try
                            {
                                var di = new DirectoryInfo(d);
                                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                                if (!di.EnumerateFileSystemInfos().Any() && DateTime.Now - di.LastWriteTime >= t.MinAge) di.Delete(false);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            return freed;
        }

        private static IEnumerable<FileInfo> Files(CleanTarget t)
        {
            var now = DateTime.Now;
            foreach (var root in t.Folders)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(root));
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();
                    List<FileSystemInfo> entries;
                    try { entries = dir.EnumerateFileSystemInfos().ToList(); } catch { continue; }
                    foreach (var e in entries)
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (e is DirectoryInfo d) { if (t.Recurse) stack.Push(d); continue; }
                        if (!(e is FileInfo f)) continue;
                        if (t.FileFilter != null && !t.FileFilter(f.FullName)) continue;
                        if (t.MinAge > TimeSpan.Zero && now - f.LastWriteTime < t.MinAge) continue;
                        yield return f;
                    }
                }
            }
        }
    }
}
