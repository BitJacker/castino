using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lindo.Core;

namespace Lindo.Services
{
    /// <summary>Implementazione reale di <see cref="IFileSystemView"/>.</summary>
    internal sealed class WindowsFileSystem : IFileSystemView
    {
        public bool DirectoryExists(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path); }
            catch { return false; }
        }

        public bool FileExists(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
            catch { return false; }
        }

        public IEnumerable<string> ListDirectories(string path)
        {
            try
            {
                return new DirectoryInfo(path).EnumerateDirectories()
                    .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                    .Select(d => d.FullName)
                    .ToList();
            }
            catch { return Enumerable.Empty<string>(); }
        }

        public IEnumerable<string> ListFiles(string path)
        {
            try { return Directory.EnumerateFiles(path).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }

        /// <summary>Dimensione di una cartella (non segue junction/symlink, ignora le cartelle inaccessibili).</summary>
        public static long DirectorySize(string path, System.Threading.CancellationToken ct = default)
        {
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            try { stack.Push(new DirectoryInfo(path)); } catch { return 0; }
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos().ToList(); }
                catch { continue; }
                foreach (var e in entries)
                {
                    try
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (e is FileInfo f) total += f.Length;
                        else if (e is DirectoryInfo d) stack.Push(d);
                    }
                    catch { /* ignora */ }
                }
            }
            return total;
        }

        public static DateTime? LastWrite(string path)
        {
            try
            {
                if (Directory.Exists(path)) return Directory.GetLastWriteTime(path);
                if (File.Exists(path)) return File.GetLastWriteTime(path);
            }
            catch { }
            return null;
        }
    }

    internal sealed class WindowsRegistry : IRegistryView
    {
        public bool KeyExists(string key)
        {
            using (var k = SystemInfo.OpenKey(key)) return k != null;
        }

        public IEnumerable<string> SubKeyNames(string key)
        {
            using (var k = SystemInfo.OpenKey(key))
            {
                if (k == null) return Enumerable.Empty<string>();
                try { return k.GetSubKeyNames().ToList(); }
                catch { return Enumerable.Empty<string>(); }
            }
        }
    }

    internal static class ShortcutResolver
    {
        /// <summary>Legge la destinazione di un collegamento .lnk tramite WScript.Shell (COM).</summary>
        public static string Resolve(string lnkPath)
        {
            object shell = null, link = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                link = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                return link?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null) as string;
            }
            catch { return null; }
            finally
            {
                if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
