using System;
using System.Collections.Generic;
using System.Linq;

namespace Lindo.Core
{
    /// <summary>
    /// Ultima linea di difesa: decide se un percorso può essere eliminato.
    /// Ogni eliminazione passa da qui, qualunque sia il motivo per cui è stata proposta.
    /// </summary>
    public sealed class PathGuard
    {
        private readonly SystemRoots _roots;
        private readonly List<string> _protectedExact = new List<string>();
        private readonly List<string> _protectedTrees = new List<string>();
        private readonly List<string> _allowedTrees = new List<string>();

        // Nomi di cartella (primo livello sotto una radice dati) che appartengono a Windows
        // o sono condivisi da molti programmi: mai eliminabili.
        public static readonly HashSet<string> SystemFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Common Files", "Windows Defender", "Windows Defender Advanced Threat Protection",
            "Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "Windows Portable Devices",
            "Windows Security", "Windows Sidebar", "WindowsPowerShell", "WindowsApps", "ModifiableWindowsApps",
            "Internet Explorer", "Microsoft.NET", "dotnet", "Reference Assemblies", "MSBuild", "Microsoft Update Health Tools",
            "Package Cache", "Packages", "Temp", "Programs", "Publishers", "VirtualStore", "Comms", "ConnectedDevicesPlatform",
            "D3DSCache", "PeerDistRepub", "CrashDumps", "SoftwareDistribution", "USOPrivate", "USOShared", "ssh",
            "regid.1991-06.com.microsoft", "Application Data", "Desktop", "Documents", "Start Menu", "Templates",
            "Favorites", "History", "IsolatedStorage", "Microsoft Help", "Microsoft OneDrive", "OneDrive",
            "PackageManagement", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Realtek", "Synaptics",
            "Windows Kits", "Microsoft SDKs", "Microsoft Visual Studio", "Microsoft SQL Server", "IIS", "IIS Express",
            "Uninstall Information", "MicrosoftEdgeBackups", "Diagnostics", "DeviceSync", "Device Stage",
            "Sun", "Oracle", "Java", "Mozilla Maintenance Service", "Package Store", "Sandbox", "SystemTemp",
            "PlaceholderTileLogoFolder", "WSL", "Microsoft Office", "Lindo",
        };

        public PathGuard(SystemRoots roots, IEnumerable<string> extraProtected = null)
        {
            _roots = roots ?? throw new ArgumentNullException(nameof(roots));

            // Radici che non devono mai essere eliminate in sé
            foreach (var p in new[]
            {
                roots.ProgramFiles, roots.ProgramFilesX86, roots.ProgramData, roots.UserProfile,
                roots.RoamingAppData, roots.LocalAppData, roots.LocalLowAppData, roots.UserPrograms,
                roots.UserStartMenu, roots.CommonStartMenu, roots.UserStartPrograms, roots.CommonStartPrograms,
                roots.UserDesktop, roots.PublicDesktop, roots.Temp,
                WinPath.GetParent(roots.UserProfile), // C:\Users
                WinPath.Combine(roots.ProgramFiles, "Common Files"),
                WinPath.Combine(roots.ProgramFilesX86, "Common Files"),
                WinPath.Combine(roots.UserProfile, "Documents"),
                WinPath.Combine(roots.UserProfile, "Downloads"),
                WinPath.Combine(roots.UserProfile, "Pictures"),
                WinPath.Combine(roots.UserProfile, "Music"),
                WinPath.Combine(roots.UserProfile, "Videos"),
                WinPath.Combine(roots.UserProfile, "AppData"),
                WinPath.Combine(roots.UserStartPrograms, "Startup"),
                WinPath.Combine(roots.CommonStartPrograms, "Startup"),
            })
            {
                if (!string.IsNullOrWhiteSpace(p)) _protectedExact.Add(WinPath.Normalize(p));
            }

            // Interi alberi intoccabili
            foreach (var p in new[]
            {
                roots.Windows,
                WinPath.Combine(roots.ProgramFiles, "WindowsApps"),
                WinPath.Combine(roots.ProgramFiles, "Windows Defender"),
                WinPath.Combine(roots.ProgramData, @"Microsoft\Windows Defender"),
                WinPath.Combine(roots.ProgramData, @"Microsoft\Windows"),
                WinPath.Combine(roots.ProgramData, "Packages"),
                WinPath.Combine(roots.LocalAppData, @"Microsoft\Windows"),
                WinPath.Combine(roots.RoamingAppData, @"Microsoft\Windows"),
                WinPath.Combine(roots.RoamingAppData, @"Microsoft\Protect"),
                WinPath.Combine(roots.RoamingAppData, @"Microsoft\Crypto"),
                WinPath.Combine(roots.RoamingAppData, @"Microsoft\SystemCertificates"),
                WinPath.Combine(roots.LocalAppData, @"Microsoft\Credentials"),
                WinPath.Combine(roots.RoamingAppData, @"Microsoft\Credentials"),
                WinPath.Combine(roots.LocalAppData, "Lindo"),
            })
            {
                if (!string.IsNullOrWhiteSpace(p)) _protectedTrees.Add(WinPath.Normalize(p));
            }

            foreach (var r in roots.ExtraProgramRoots.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                _protectedExact.Add(WinPath.Normalize(r));
                _allowedTrees.Add(WinPath.Normalize(r));
            }

            if (extraProtected != null)
                foreach (var p in extraProtected.Where(x => !string.IsNullOrWhiteSpace(x)))
                    _protectedExact.Add(WinPath.Normalize(p));

            // Solo qui dentro si può eliminare qualcosa
            foreach (var p in new[]
            {
                roots.ProgramFiles, roots.ProgramFilesX86, roots.ProgramData, roots.RoamingAppData,
                roots.LocalAppData, roots.LocalLowAppData, roots.UserStartMenu, roots.CommonStartMenu,
                roots.UserDesktop, roots.PublicDesktop,
            })
            {
                if (!string.IsNullOrWhiteSpace(p)) _allowedTrees.Add(WinPath.Normalize(p));
            }
        }

        /// <summary>Restituisce null se il percorso si può eliminare, altrimenti il motivo del rifiuto.</summary>
        public string WhyBlocked(string path, LeftoverKind kind = LeftoverKind.Folder)
        {
            var p = WinPath.Normalize(path);
            if (p.Length == 0) return "Empty path";
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return "Network path";
            if (p.Length < 3 || p[1] != ':') return "Not an absolute path";
            if (WinPath.Depth(p) <= 1) return "Root of a drive";

            if (_protectedExact.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                return "System folder";

            // Non si può eliminare un antenato di una cartella protetta (es. C:\Users\Nome)
            if (kind == LeftoverKind.Folder &&
                _protectedExact.Concat(_protectedTrees).Any(x => WinPath.IsSameOrInside(x, p)))
                return "Contains system folders";

            // I collegamenti del menu Start stanno dentro ...\Microsoft\Windows, ma sono eliminabili
            var inStartMenu = WinPath.IsStrictlyInside(p, _roots.UserStartPrograms) || WinPath.IsStrictlyInside(p, _roots.CommonStartPrograms);
            if (inStartMenu)
            {
                foreach (var startup in new[] { WinPath.Combine(_roots.UserStartPrograms, "Startup"), WinPath.Combine(_roots.CommonStartPrograms, "Startup") })
                    if (WinPath.IsSameOrInside(p, startup) && kind == LeftoverKind.Folder) return "Startup folder";
                return null;
            }

            if (_protectedTrees.Any(x => WinPath.IsSameOrInside(p, x)))
                return "Protected Windows area";

            if (!_allowedTrees.Any(x => WinPath.IsStrictlyInside(p, x)))
                return "Outside the program folders";

            // Primo livello sotto una radice dati: blocca i nomi di sistema
            foreach (var root in _allowedTrees)
            {
                if (WinPath.IsStrictlyInside(p, root) && WinPath.Depth(p) == WinPath.Depth(root) + 1)
                {
                    if (SystemFolderNames.Contains(WinPath.GetFileName(p)))
                        return "Shared system folder";
                }
            }

            // Cartelle Microsoft condivise di secondo livello (es. AppData\Local\Microsoft\Edge è ok solo se proposta esplicitamente)
            return null;
        }

        public bool CanDelete(string path, LeftoverKind kind = LeftoverKind.Folder) => WhyBlocked(path, kind) == null;
    }
}
