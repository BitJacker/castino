using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lindo.Core;

namespace Lindo.Services
{
    internal sealed class CleanResult
    {
        public long FreedBytes;
        public int Deleted;
        public int ScheduledOnReboot;
        public List<string> Failed = new List<string>();
        public string BackupFolder;
    }

    /// <summary>Elimina i residui confermati dall'utente, ricontrollando ogni percorso con le regole di sicurezza.</summary>
    internal static class Cleaner
    {
        public static CleanResult Delete(IEnumerable<Leftover> items, IProgress<string> progress = null)
        {
            var result = new CleanResult();
            var guard = new PathGuard(SystemInfo.Roots);
            var list = items.ToList();

            // 1) Registro: prima un backup .reg (pochi KB), poi l'eliminazione
            var regItems = list.Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();
            if (regItems.Count > 0)
            {
                result.BackupFolder = Path.Combine(SystemInfo.DataFolder, "Registry backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(result.BackupFolder);
            }
            int n = 0;
            foreach (var item in regItems)
            {
                progress?.Report("Registry: " + item.Path);
                if (!RegistryGuard.CanDelete(item.Path))
                {
                    result.Failed.Add(item.Path + " (protetta)");
                    continue;
                }
                try
                {
                    ExportKey(item.Path, Path.Combine(result.BackupFolder, (++n).ToString("000") + ".reg"));
                    if (!SystemInfo.SplitKey(item.Path, out var hive, out var sub)) continue;
                    var view = Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Default;
                    using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view))
                        baseKey.DeleteSubKeyTree(sub, false);
                    result.Deleted++;
                    Log.Write("Eliminata chiave " + item.Path);
                }
                catch (Exception ex)
                {
                    result.Failed.Add(item.Path);
                    Log.Write("Errore chiave " + item.Path + ": " + ex.Message);
                }
            }

            // 2) Driver (pnputil)
            foreach (var item in list.Where(i => i.Kind == LeftoverKind.Driver))
            {
                progress?.Report("Driver: " + (string.IsNullOrEmpty(item.DisplayName) ? item.DriverInf : item.DisplayName));
                var err = DriverService.Delete(item.DriverInf);
                if (err == null) { result.Deleted++; result.FreedBytes += item.SizeBytes; }
                else result.Failed.Add((string.IsNullOrEmpty(item.DisplayName) ? item.DriverInf : item.DisplayName) + " (" + err + ")");
            }

            // 3) File, cartelle, collegamenti
            foreach (var item in list.Where(i => i.Kind != LeftoverKind.RegistryKey && i.Kind != LeftoverKind.Driver))
            {
                progress?.Report("Deleting: " + item.Path);
                var isDir = Directory.Exists(item.Path);
                var why = guard.WhyBlocked(item.Path, isDir ? LeftoverKind.Folder : item.Kind);
                if (why != null)
                {
                    result.Failed.Add(item.Path + " (" + why + ")");
                    Log.Write("Bloccato " + item.Path + ": " + why);
                    continue;
                }
                try
                {
                    if (isDir)
                    {
                        var before = WindowsFileSystem.DirectorySize(item.Path);
                        var scheduled = DeleteDirectory(item.Path);
                        var after = Directory.Exists(item.Path) ? WindowsFileSystem.DirectorySize(item.Path) : 0;
                        result.FreedBytes += Math.Max(0, before - after);
                        result.ScheduledOnReboot += scheduled;
                    }
                    else if (File.Exists(item.Path))
                    {
                        var len = new FileInfo(item.Path).Length;
                        if (DeleteFile(item.Path)) result.FreedBytes += len;
                        else result.ScheduledOnReboot++;
                    }
                    result.Deleted++;
                    RemoveEmptyParents(item.Path, guard);
                    Log.Write("Eliminato " + item.Path);
                }
                catch (Exception ex)
                {
                    result.Failed.Add(item.Path);
                    Log.Write("Errore " + item.Path + ": " + ex.Message);
                }
            }
            return result;
        }

        private static void ExportKey(string key, string file)
        {
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var psi = new ProcessStartInfo(reg, "export \"" + key + "\" \"" + file + "\" /y")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi)) p?.WaitForExit(15000);
        }

        /// <summary>Elimina una cartella senza seguire junction/symlink. Restituisce quanti elementi sono stati programmati al riavvio.</summary>
        public static int DeleteDirectory(string path)
        {
            int scheduled = 0;
            var di = new DirectoryInfo(path);
            if (!di.Exists) return 0;

            if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                di.Delete(false); // rimuove solo il collegamento, non la destinazione
                return 0;
            }

            IEnumerable<FileSystemInfo> entries;
            try { entries = di.EnumerateFileSystemInfos().ToList(); }
            catch { entries = Enumerable.Empty<FileSystemInfo>(); }

            foreach (var e in entries)
            {
                try
                {
                    if (e is DirectoryInfo sub) scheduled += DeleteDirectory(sub.FullName);
                    else if (!DeleteFile(e.FullName)) scheduled++;
                }
                catch { scheduled++; }
            }

            try
            {
                di.Attributes = FileAttributes.Normal;
                di.Delete(false);
            }
            catch
            {
                Native.MoveFileEx(path, null, Native.MoveFileFlags.DelayUntilReboot);
                scheduled++;
            }
            return scheduled;
        }

        /// <summary>True se eliminato subito, false se programmato al riavvio.</summary>
        public static bool DeleteFile(string path)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch
            {
                Native.MoveFileEx(path, null, Native.MoveFileFlags.DelayUntilReboot);
                return false;
            }
        }

        /// <summary>Dopo aver tolto "VideoLAN\VLC" elimina anche "VideoLAN" se è rimasta vuota.</summary>
        private static void RemoveEmptyParents(string path, PathGuard guard)
        {
            var parent = WinPath.GetParent(path);
            for (int i = 0; i < 2 && parent.Length > 3; i++)
            {
                try
                {
                    if (!Directory.Exists(parent) || !guard.CanDelete(parent)) return;
                    if (Directory.EnumerateFileSystemEntries(parent).Any()) return;
                    Directory.Delete(parent, false);
                    Log.Write("Rimossa cartella vuota " + parent);
                }
                catch { return; }
                parent = WinPath.GetParent(parent);
            }
        }
    }
}
