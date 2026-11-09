using System;
using System.IO;
using Lindo.Core;
using Microsoft.Win32;

namespace Lindo.Services
{
    internal static class SystemInfo
    {
        private static SystemRoots _roots;

        public static SystemRoots Roots => _roots ?? (_roots = Build());

        private static SystemRoots Build()
        {
            string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

            var pf = Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrWhiteSpace(pf)) pf = F(Environment.SpecialFolder.ProgramFiles);
            var pf86 = F(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrWhiteSpace(pf86)) pf86 = pf;
            var local = F(Environment.SpecialFolder.LocalApplicationData);
            var profile = F(Environment.SpecialFolder.UserProfile);
            var publicDesktop = F(Environment.SpecialFolder.CommonDesktopDirectory);

            return new SystemRoots
            {
                Windows = F(Environment.SpecialFolder.Windows),
                ProgramFiles = pf,
                ProgramFilesX86 = pf86,
                ProgramData = F(Environment.SpecialFolder.CommonApplicationData),
                UserProfile = profile,
                RoamingAppData = F(Environment.SpecialFolder.ApplicationData),
                LocalAppData = local,
                LocalLowAppData = Path.Combine(profile, "AppData", "LocalLow"),
                UserStartMenu = F(Environment.SpecialFolder.StartMenu),
                CommonStartMenu = F(Environment.SpecialFolder.CommonStartMenu),
                UserDesktop = F(Environment.SpecialFolder.DesktopDirectory),
                PublicDesktop = string.IsNullOrWhiteSpace(publicDesktop) ? @"C:\Users\Public\Desktop" : publicDesktop,
                Temp = Path.GetTempPath().TrimEnd('\\'),
            };
        }

        public static string DataFolder
        {
            get
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lindo");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string SystemDrive
        {
            get
            {
                var d = Environment.GetEnvironmentVariable("SystemDrive");
                return string.IsNullOrWhiteSpace(d) ? @"C:\" : d.TrimEnd('\\') + @"\";
            }
        }

        public static (long Free, long Total) DiskSpace(string root = null)
        {
            try
            {
                var di = new DriveInfo(root ?? SystemDrive);
                return (di.AvailableFreeSpace, di.TotalSize);
            }
            catch { return (0, 0); }
        }

        /// <summary>Apre una chiave di registro nel formato "HKLM\SOFTWARE\X" (vista a 64 bit nativa).</summary>
        public static RegistryKey OpenKey(string fullKey, bool writable = false)
        {
            if (!SplitKey(fullKey, out var hive, out var sub)) return null;
            try
            {
                var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
                var baseKey = RegistryKey.OpenBaseKey(hive, view);
                return sub.Length == 0 ? baseKey : baseKey.OpenSubKey(sub, writable);
            }
            catch { return null; }
        }

        public static bool SplitKey(string fullKey, out RegistryHive hive, out string sub)
        {
            hive = RegistryHive.CurrentUser;
            sub = string.Empty;
            if (string.IsNullOrWhiteSpace(fullKey)) return false;
            var k = fullKey.Trim().TrimEnd('\\');
            var i = k.IndexOf('\\');
            var root = i < 0 ? k : k.Substring(0, i);
            sub = i < 0 ? string.Empty : k.Substring(i + 1);
            switch (root.ToUpperInvariant())
            {
                case "HKCU": case "HKEY_CURRENT_USER": hive = RegistryHive.CurrentUser; return true;
                case "HKLM": case "HKEY_LOCAL_MACHINE": hive = RegistryHive.LocalMachine; return true;
                default: return false;
            }
        }
    }
}
