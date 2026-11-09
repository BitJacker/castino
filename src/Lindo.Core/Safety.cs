using System;
using System.Collections.Generic;
using System.Linq;

namespace Lindo.Core
{
    /// <summary>Un percorso che risulta "in uso" e il motivo (PATH, servizio, processo in esecuzione…).</summary>
    public sealed class UsedPath
    {
        public UsedPath(string path, string why) { Path = WinPath.Normalize(path); Why = why; }
        public string Path { get; }
        public string Why { get; }
        public override string ToString() => Path + " (" + Why + ")";
    }

    /// <summary>Indizi raccolti sul sistema reale per capire se una cartella "orfana" è in realtà ancora usata.</summary>
    public sealed class OrphanEvidence
    {
        public List<UsedPath> InUsePaths { get; } = new List<UsedPath>();
        /// <summary>Data dell'ultima modifica di un file nella cartella (anche nelle sottocartelle).</summary>
        public Func<string, DateTime?> NewestActivity { get; set; }
        public DateTime Now { get; set; } = DateTime.Now;
        /// <summary>Una cartella modificata negli ultimi N giorni non è mai "sicura".</summary>
        public int RecentDays { get; set; } = 90;

        public void Add(string path, string why)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var p = WinPath.Normalize(Environment_Expand(path));
            if (p.Length < 3 || p[1] != ':') return;
            // Una radice di disco o C:\Windows come "prova" renderebbe tutto "in uso": la scartiamo
            if (WinPath.Depth(p) <= 1) return;
            InUsePaths.Add(new UsedPath(p, why));
        }

        private static string Environment_Expand(string s)
        {
            try { return Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')); } catch { return s; }
        }
    }

    /// <summary>
    /// Strumenti per sviluppatori e runtime che spesso NON compaiono tra le app installate
    /// ma sono usati da altri programmi (npm, pip, Claude Code, Docker…). Non vengono mai proposti come residui.
    /// </summary>
    public static class KnownTools
    {
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // JavaScript / Node
            "npm", "npm-cache", "pnpm", "pnpm-cache", "pnpm-state", "pnpm-store", "yarn", "Yarn", "nvm", "fnm", "fnm_multishells",
            "node", "nodejs", "node-gyp", "bun", "deno", "volta", "corepack", "typescript", "ms-playwright", "puppeteer", "Cypress", "electron",
            "electron-builder", "node-gyp-cache", "turbo", "nx", "vercel", "netlify", "firebase",
            // Python
            "pip", "pypa", "Python", "py", "uv", "pipx", "conda", "Anaconda3", "miniconda3", "pypoetry", "poetry", "virtualenv",
            "jupyter", "Jupyter", "ipython", "matplotlib", "hatch", "pdm",
            // Claude / AI
            "Claude", "claude", "claude-code", "claude-cli-nodejs", "AnthropicClaude", "Anthropic", "ClaudeCode", "ollama", "Ollama",
            "LM Studio", "lm-studio", "cursor", "Cursor", "cursor-updater", "Codeium", "Windsurf", "GitHub Copilot", "github-copilot",
            // .NET / Java / altri linguaggi
            "dotnet", ".dotnet", "NuGet", "NuGetPackages", "nuget", "Gradle", "gradle", "Maven", "maven", "JetBrains", "Google\\AndroidStudio",
            "Android", "AndroidStudio", "Go", "go", "go-build", "golang", "rustup", "cargo", "Rust", "composer", "Composer", "php",
            "Ruby", "gem", "Gems", "Perl", "Strawberry", "flutter", "Flutter", "Pub", "dart-tool", "Dart", "kotlin",
            // Editor e strumenti
            "Code", "Code - Insiders", "VSCodium", "vscode", "Visual Studio Code", "Sublime Text", "Notepad++", "GitHub Desktop",
            "GitHubDesktop", "Git", "gh", "GitHub CLI", "git-credential-manager", "GitCredentialManager", "SourceTree", "Atlassian",
            "Postman", "Insomnia", "DBeaverData", "dbeaver", "mongodb", "MongoDB", "MySQL", "PostgreSQL", "Redis",
            // Container / cloud / infrastruttura
            "Docker", "Docker Desktop", "DockerDesktop", "rancher-desktop", "kube", "kubectl", "helm", "minikube", "terraform",
            "Terraform", "Pulumi", "Azure", ".azure", "AzureFunctionsTools", "gcloud", "Google\\Cloud SDK", "aws", "Amazon",
            "AWS", "Vagrant", "VirtualBox", "VirtualBox VMs", "VMware", "Hyper-V", "WSL", "wsl", "Packages",
            // Gestori di pacchetti
            "chocolatey", "Chocolatey", "ChocolateyHttpCache", "scoop", "WinGet", "winget", "Ninite",
            // Giochi e launcher (librerie con salvataggi)
            "Steam", "SteamLibrary", "Epic", "Epic Games", "EpicGamesLauncher", "UnrealEngine", "Riot Games", "Battle.net",
            "Blizzard Entertainment", "Ubisoft", "Ubisoft Game Launcher", "EA", "EA Desktop", "Origin", "GOG.com", "GOG Galaxy",
            "Rockstar Games", "Minecraft", ".minecraft", "Roblox", "itch",
            // Sistema / sicurezza / VPN / driver
            "runner", "actions-runner", "hostedtoolcache", "OpenSSH", "ssh", "gnupg", "GnuPG", "Fortinet", "FortiClient", "OpenVPN",
            "WireGuard", "Cisco", "NordVPN", "ProtonVPN", "Malwarebytes", "Avast Software", "AVG", "ESET", "Kaspersky Lab",
            "Norton", "McAfee", "Bitdefender", "Realtek", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Logitech",
            "Razer", "Corsair", "SteelSeries", "Synaptics", "Elan", "Dolby", "Waves", "Package Cache", "Installer",
            "ProgramData", "Users", "PerfLogs", "Recovery", "System Volume Information", "Config.Msi", "Documents and Settings",
            "Program Files", "Program Files (x86)", "Programmi", "Temp", "tmp", "inetpub", "Drivers", "drivers", "OneDriveTemp",
        };

        public static bool IsKnownTool(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return false;
            if (Names.Contains(folderName.Trim())) return true;
            var raw = NameMatcher.RawCompact(folderName);
            return Names.Any(n => NameMatcher.RawCompact(n) == raw && raw.Length >= 3);
        }
    }

    /// <summary>Spiega in italiano un crash di un'app o una schermata blu.</summary>
    public static class CrashExplainer
    {
        private static readonly Dictionary<string, string> ExceptionCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "c0000005", "The program tried to read or write memory it doesn't own (access violation). Usually a bug in the program or one of its add-ons." },
            { "c0000409", "The program stopped itself because it detected a serious internal error (buffer overrun). Often a bug in the program." },
            { "c0000374", "The program's memory got corrupted (heap corruption). It can be a bug, a plugin or, rarely, the RAM." },
            { "c00000fd", "The program ran out of stack space (stack overflow): usually a bug." },
            { "c000001d", "The processor doesn't recognize an instruction: the program may not be compatible with this PC." },
            { "c0000006", "A program file couldn't be read from the disk: check the disk and reinstall the program." },
            { "c0000142", "The program failed to start (a DLL failed to initialize). Reinstalling it often fixes it." },
            { "c0000135", "A DLL the program needs is missing: reinstall it or install the Visual C++ Redistributable." },
            { "c000041d", "Unhandled error inside a user-interface callback: a bug in the program." },
            { "e0434352", "Unhandled error in a .NET program. Updating or reinstalling the program usually helps." },
            { "e06d7363", "Unhandled error in a C++ program: it's a bug in the program, try updating it." },
            { "80000003", "The program hit an internal breakpoint: usually a bug." },
            { "c0000417", "The program passed invalid parameters to a system function: it's a bug." },
            { "c0000602", "The program closed because of a critical error it detected itself." },
            { "cfffffff", "The program stopped responding and was closed." },
        };

        private static readonly (string Pattern, string Hint)[] Modules =
        {
            ("nvwgf2um", "The crash happens in the NVIDIA graphics driver: update (or reinstall) the NVIDIA driver."),
            ("nvd3dum", "The crash happens in the NVIDIA graphics driver: update (or reinstall) the NVIDIA driver."),
            ("nvoglv", "The crash happens in the NVIDIA OpenGL driver: update the graphics driver."),
            ("nvcuda", "The crash happens in the NVIDIA CUDA driver: update the graphics driver."),
            ("atiumd", "The crash happens in the AMD graphics driver: update the AMD Radeon driver."),
            ("amdxx", "The crash happens in the AMD graphics driver: update the AMD Radeon driver."),
            ("atio6axx", "The crash happens in the AMD OpenGL driver: update the graphics driver."),
            ("igd", "The crash happens in the Intel graphics driver: update the Intel graphics driver."),
            ("d3d11", "The crash happens in DirectX 11 graphics: update the graphics driver."),
            ("d3d12", "The crash happens in DirectX 12 graphics: update the graphics driver."),
            ("dxgi", "The crash involves DirectX graphics: update the graphics driver."),
            ("vcruntime", "The crash happens in the Visual C++ libraries: reinstall the \"Microsoft Visual C++ Redistributable\"."),
            ("msvcp", "The crash happens in the Visual C++ libraries: reinstall the \"Microsoft Visual C++ Redistributable\"."),
            ("ucrtbase", "The crash was reported by the Windows C library, but the cause is almost always in the program itself."),
            ("clr.dll", "The crash happens in the .NET runtime: update the program and Windows."),
            ("coreclr", "The crash happens in the .NET runtime: update the program."),
            ("kernelbase", "The crash was reported by Windows (KERNELBASE.dll), but the cause is almost always in the program itself."),
            ("ntdll", "The crash was reported by Windows (ntdll.dll), but the cause is almost always in the program or one of its plugins."),
            ("xinput", "The crash involves the game controller: unplug and replug it or update its driver."),
            ("audioses", "The crash involves audio: update the audio driver."),
            ("unityplayer", "The crash happens in the game engine (Unity): update the game and the graphics driver."),
            ("libcef", "The crash happens in the program's built-in browser (CEF): update the program."),
        };

        private static readonly Dictionary<uint, (string Name, string Hint)> BugChecks = new Dictionary<uint, (string, string)>
        {
            { 0x0000000A, ("IRQL_NOT_LESS_OR_EQUAL", "A driver used memory incorrectly. Update your drivers (especially network, graphics and chipset).") },
            { 0x0000001A, ("MEMORY_MANAGEMENT", "Memory management problem: check the RAM with \"Windows Memory Diagnostic\" and update your drivers.") },
            { 0x0000001E, ("KMODE_EXCEPTION_NOT_HANDLED", "A driver caused an error: update recently installed drivers.") },
            { 0x0000003B, ("SYSTEM_SERVICE_EXCEPTION", "Error in a driver or system service: update the graphics drivers and Windows.") },
            { 0x00000050, ("PAGE_FAULT_IN_NONPAGED_AREA", "Invalid memory access: it can be a driver, the antivirus or the RAM.") },
            { 0x0000007E, ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A system driver crashed: update your drivers, especially the graphics one.") },
            { 0x0000007F, ("UNEXPECTED_KERNEL_MODE_TRAP", "Hardware or driver error: check temperatures, RAM and overclocking.") },
            { 0x0000009F, ("DRIVER_POWER_STATE_FAILURE", "A driver didn't handle sleep/wake correctly: update your drivers (often network or USB).") },
            { 0x000000C5, ("DRIVER_CORRUPTED_EXPOOL", "A driver corrupted system memory: update or remove recently installed drivers.") },
            { 0x000000D1, ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver (often network or Wi-Fi) used memory incorrectly: update it.") },
            { 0x000000EF, ("CRITICAL_PROCESS_DIED", "A critical Windows process closed: run \"sfc /scannow\" and check the disk.") },
            { 0x000000F4, ("CRITICAL_OBJECT_TERMINATION", "A critical process closed, often because of disk problems: check the disk.") },
            { 0x00000116, ("VIDEO_TDR_FAILURE", "The graphics card stopped responding: update the graphics driver and check temperatures.") },
            { 0x00000117, ("VIDEO_TDR_TIMEOUT_DETECTED", "The graphics card stopped responding: update the graphics driver.") },
            { 0x00000124, ("WHEA_UNCORRECTABLE_ERROR", "Hardware error (processor, RAM or power supply). Check temperatures and remove any overclock.") },
            { 0x00000133, ("DPC_WATCHDOG_VIOLATION", "A driver or the SSD didn't respond in time: update the disk/chipset drivers and the SSD firmware.") },
            { 0x00000139, ("KERNEL_SECURITY_CHECK_FAILURE", "A driver corrupted system data: update your drivers and check the RAM.") },
            { 0x0000013A, ("KERNEL_MODE_HEAP_CORRUPTION", "A driver corrupted memory: update your drivers, especially the graphics one.") },
            { 0x00000154, ("UNEXPECTED_STORE_EXCEPTION", "Disk or memory problem: check the health of the disk.") },
            { 0x000001A5, ("ACPI_BIOS_ERROR", "BIOS problem: update the motherboard BIOS.") },
        };

        public static string ExplainException(string code)
        {
            var c = NormalizeCode(code);
            return ExceptionCodes.TryGetValue(c, out var t) ? t : "The program closed unexpectedly (code 0x" + c + ").";
        }

        public static string ExplainModule(string module)
        {
            if (string.IsNullOrWhiteSpace(module)) return null;
            var m = module.ToLowerInvariant();
            foreach (var (pattern, hint) in Modules)
                if (m.Contains(pattern)) return hint;
            return null;
        }

        public static (string Name, string Hint) ExplainBugCheck(uint code)
        {
            if (BugChecks.TryGetValue(code, out var v)) return v;
            return ("0x" + code.ToString("X8"), "Blue screen: update Windows and your drivers; if it happens again, check RAM and temperatures.");
        }

        /// <summary>Estrae il codice numerico da testi come "0x0000001a" o "0x1a (0x..., ...)".</summary>
        public static uint? ParseBugCheck(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            var idx = t.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var hex = new string(t.Substring(idx + 2).TakeWhile(Uri.IsHexDigit).ToArray());
            if (hex.Length == 0) return null;
            try { return Convert.ToUInt32(hex.Length > 8 ? hex.Substring(hex.Length - 8) : hex, 16); } catch { return null; }
        }

        private static string NormalizeCode(string code)
        {
            var c = (code ?? string.Empty).Trim().ToLowerInvariant();
            if (c.StartsWith("0x")) c = c.Substring(2);
            return c.PadLeft(8, '0');
        }
    }

    /// <summary>Decide quali app all'avvio sono indispensabili e quali si possono togliere senza problemi.</summary>
    public static class StartupAdvisor
    {
        private static readonly string[] Essential =
        {
            "securityhealth", "defender", "windows security", "antivirus", "avast", "avg", "kaspersky", "eset", "norton", "mcafee",
            "bitdefender", "malwarebytes", "sophos", "trend micro", "webroot", "realtek", "nvidia", "amd", "radeon", "intel",
            "igfx", "synaptics", "elan", "touchpad", "precision", "logitech", "wacom", "audio", "sound", "dolby", "waves", "maxx",
            "bluetooth", "keyboard", "hotkey", "fn key", "ctfmon", "onedrive", "google drive", "dropbox", "icloud", "lindo",
            "battery", "power manager", "asus", "lenovo vantage", "dell", "hp ", "msi center", "armoury", "acer",
        };

        private static readonly string[] Useless =
        {
            "update", "updater", "helper", "launcher", "steam", "discord", "spotify", "epic", "teams", "skype", "zoom",
            "ea app", "ea desktop", "origin", "ubisoft", "battle.net", "opera", "edge", "msedge", "cortana", "ccleaner", "utorrent",
            "bittorrent", "itunes", "jusched", "java", "adobe", "acrobat", "creative cloud", "whatsapp", "telegram", "messenger",
            "slack", "notion", "figma", "riot", "gog", "overwolf", "fortinet", "forticlient", "forti", "vpn", "phone link",
            "your phone", "copilot", "widgets", "xbox", "game bar", "grammarly", "dropbox update", "google update", "brave", "chrome",
        };

        public static bool IsEssential(string name, string publisher, string command)
        {
            var text = ((name ?? "") + " " + (publisher ?? "") + " " + (command ?? "")).ToLowerInvariant();
            return Essential.Any(text.Contains);
        }

        /// <summary>True se l'ottimizzatore può proporre di disattivarla.</summary>
        public static bool SuggestDisable(string name, string publisher, string command)
        {
            if (IsEssential(name, publisher, command)) return false;
            var text = ((name ?? "") + " " + (publisher ?? "") + " " + (command ?? "")).ToLowerInvariant();
            return Useless.Any(text.Contains);
        }
    }
}
