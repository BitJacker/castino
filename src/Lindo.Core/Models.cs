using System;
using System.Collections.Generic;

namespace Lindo.Core
{
    public enum AppSource
    {
        Desktop,
        Store,
        Driver,
    }

    /// <summary>Descrizione "pura" di un'app installata (indipendente dall'interfaccia).</summary>
    public sealed class AppInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string InstallLocation { get; set; } = string.Empty;
        public string UninstallString { get; set; } = string.Empty;
        public string QuietUninstallString { get; set; } = string.Empty;
        public string DisplayIcon { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public bool IsMsi { get; set; }
        public long EstimatedSizeBytes { get; set; }
        public DateTime? InstallDate { get; set; }
        public AppSource Source { get; set; }

        /// <summary>Chiave di registro della voce di disinstallazione, es. HKLM\SOFTWARE\...\Uninstall\{GUID}.</summary>
        public string RegistryKey { get; set; } = string.Empty;

        /// <summary>Vista del registro (true = 32 bit / WOW6432Node).</summary>
        public bool Is32BitView { get; set; }

        /// <summary>Per le app dello Store.</summary>
        public string PackageFullName { get; set; } = string.Empty;
        public string PackageFamilyName { get; set; } = string.Empty;

        /// <summary>Per i driver: nome pubblicato (oemNN.inf), classe e dispositivi che lo usano.</summary>
        public string DriverInf { get; set; } = string.Empty;
        public string DriverClass { get; set; } = string.Empty;
        public string DriverInUseBy { get; set; } = string.Empty;
        public bool DriverIsCritical { get; set; }

        /// <summary>Ultimo utilizzo noto (Prefetch, UserAssist, monitor di Lindo). Null = nessun dato.</summary>
        public DateTime? LastUsed { get; set; }

        public string InstallFolderName =>
            string.IsNullOrWhiteSpace(InstallLocation) ? string.Empty : WinPath.GetFileName(InstallLocation);

        public override string ToString() => Name;
    }

    public enum LeftoverKind
    {
        Folder,
        File,
        Shortcut,
        RegistryKey,
        Driver,
    }

    public enum Confidence
    {
        Low = 1,
        Medium = 2,
        High = 3,
    }

    public sealed class Leftover
    {
        public LeftoverKind Kind { get; set; }
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public Confidence Confidence { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime? LastModified { get; set; }

        /// <summary>Per i driver: il nome pubblicato oemNN.inf da passare a pnputil.</summary>
        public string DriverInf { get; set; } = string.Empty;
        /// <summary>Testo da mostrare al posto del percorso (es. nome del driver).</summary>
        public string DisplayName { get; set; } = string.Empty;

        public override string ToString() => $"{Kind} {Path} ({Confidence})";
    }

    /// <summary>Le cartelle "radice" del sistema dove i programmi lasciano dati.</summary>
    public sealed class SystemRoots
    {
        public string Windows { get; set; } = @"C:\Windows";
        public string ProgramFiles { get; set; } = @"C:\Program Files";
        public string ProgramFilesX86 { get; set; } = @"C:\Program Files (x86)";
        public string ProgramData { get; set; } = @"C:\ProgramData";
        public string UserProfile { get; set; } = @"C:\Users\User";
        public string RoamingAppData { get; set; } = @"C:\Users\User\AppData\Roaming";
        public string LocalAppData { get; set; } = @"C:\Users\User\AppData\Local";
        public string LocalLowAppData { get; set; } = @"C:\Users\User\AppData\LocalLow";
        public string UserStartMenu { get; set; } = @"C:\Users\User\AppData\Roaming\Microsoft\Windows\Start Menu";
        public string CommonStartMenu { get; set; } = @"C:\ProgramData\Microsoft\Windows\Start Menu";
        public string UserDesktop { get; set; } = @"C:\Users\User\Desktop";
        public string PublicDesktop { get; set; } = @"C:\Users\Public\Desktop";
        public string Temp { get; set; } = @"C:\Users\User\AppData\Local\Temp";

        /// <summary>
        /// Cartelle dei programmi sugli altri dischi (es. D:\Program Files, D:\Giochi se contiene app installate).
        /// Si cercano residui solo qui dentro, mai nel resto dei dischi dati.
        /// </summary>
        public List<string> ExtraProgramRoots { get; set; } = new List<string>();

        public string UserPrograms => WinPath.Combine(LocalAppData, "Programs");
        public string UserStartPrograms => WinPath.Combine(UserStartMenu, "Programs");
        public string CommonStartPrograms => WinPath.Combine(CommonStartMenu, "Programs");

        /// <summary>Cartelle in cui un'app installa o salva dati e in cui cerchiamo residui (primo livello).</summary>
        public IEnumerable<string> DataRoots()
        {
            yield return ProgramFiles;
            if (!WinPath.Equal(ProgramFilesX86, ProgramFiles)) yield return ProgramFilesX86;
            yield return ProgramData;
            yield return RoamingAppData;
            yield return LocalAppData;
            yield return LocalLowAppData;
            yield return UserPrograms;
            foreach (var r in ExtraProgramRoots) yield return r;
        }
    }
}
