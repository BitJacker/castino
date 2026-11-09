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
            { "c0000005", "Il programma ha provato a leggere o scrivere memoria non sua (violazione di accesso). Di solito è un bug del programma o di un suo componente aggiuntivo." },
            { "c0000409", "Il programma si è fermato da solo perché ha rilevato un errore interno grave (buffer overrun). Spesso è un bug del programma." },
            { "c0000374", "La memoria del programma si è corrotta (heap corruption). Può dipendere da un bug, da un plugin o, raramente, dalla RAM." },
            { "c00000fd", "Il programma ha esaurito lo spazio dello stack (stack overflow): di solito è un bug." },
            { "c000001d", "Il processore non riconosce un'istruzione: il programma potrebbe non essere compatibile con questo PC." },
            { "c0000006", "Non è stato possibile leggere un file del programma dal disco: controlla il disco e reinstalla il programma." },
            { "c0000142", "Il programma non è riuscito ad avviarsi (errore di inizializzazione di una DLL). Reinstallarlo spesso risolve." },
            { "c0000135", "Manca una DLL necessaria al programma: reinstallalo o installa i Visual C++ Redistributable." },
            { "c000041d", "Errore non gestito dentro un callback dell'interfaccia: bug del programma." },
            { "e0434352", "Errore non gestito in un programma .NET. Aggiornare o reinstallare il programma di solito aiuta." },
            { "e06d7363", "Errore non gestito in un programma C++: è un bug del programma, prova ad aggiornarlo." },
            { "80000003", "Il programma ha raggiunto un punto di arresto interno: di solito è un bug." },
            { "c0000417", "Il programma ha passato parametri non validi a una funzione di sistema: è un bug." },
            { "c0000602", "Il programma si è chiuso per un errore critico rilevato da sé stesso." },
            { "cfffffff", "Il programma ha smesso di rispondere ed è stato chiuso." },
        };

        private static readonly (string Pattern, string Hint)[] Modules =
        {
            ("nvwgf2um", "Il crash avviene nel driver della scheda video NVIDIA: aggiorna (o reinstalla) il driver NVIDIA."),
            ("nvd3dum", "Il crash avviene nel driver della scheda video NVIDIA: aggiorna (o reinstalla) il driver NVIDIA."),
            ("nvoglv", "Il crash avviene nel driver OpenGL NVIDIA: aggiorna il driver della scheda video."),
            ("nvcuda", "Il crash avviene nel driver CUDA NVIDIA: aggiorna il driver della scheda video."),
            ("atiumd", "Il crash avviene nel driver della scheda video AMD: aggiorna il driver AMD Radeon."),
            ("amdxx", "Il crash avviene nel driver della scheda video AMD: aggiorna il driver AMD Radeon."),
            ("atio6axx", "Il crash avviene nel driver OpenGL AMD: aggiorna il driver della scheda video."),
            ("igd", "Il crash avviene nel driver grafico Intel: aggiorna il driver della scheda video Intel."),
            ("d3d11", "Il crash avviene nella grafica DirectX 11: aggiorna il driver della scheda video."),
            ("d3d12", "Il crash avviene nella grafica DirectX 12: aggiorna il driver della scheda video."),
            ("dxgi", "Il crash riguarda la grafica DirectX: aggiorna il driver della scheda video."),
            ("vcruntime", "Il crash avviene nelle librerie Visual C++: reinstalla i \"Microsoft Visual C++ Redistributable\"."),
            ("msvcp", "Il crash avviene nelle librerie Visual C++: reinstalla i \"Microsoft Visual C++ Redistributable\"."),
            ("ucrtbase", "Il crash è stato segnalato dalla libreria C di Windows, ma la causa è quasi sempre nel programma stesso."),
            ("clr.dll", "Il crash avviene nel runtime .NET: aggiorna il programma e Windows."),
            ("coreclr", "Il crash avviene nel runtime .NET: aggiorna il programma."),
            ("kernelbase", "Il crash è stato segnalato da Windows (KERNELBASE.dll), ma la causa è quasi sempre nel programma stesso."),
            ("ntdll", "Il crash è stato segnalato da Windows (ntdll.dll), ma la causa è quasi sempre nel programma o in un suo plugin."),
            ("xinput", "Il crash riguarda il controller di gioco: scollega e ricollega il controller o aggiorna il suo driver."),
            ("audioses", "Il crash riguarda l'audio: aggiorna il driver audio."),
            ("unityplayer", "Il crash avviene nel motore del gioco (Unity): aggiorna il gioco e il driver della scheda video."),
            ("libcef", "Il crash avviene nel browser integrato del programma (CEF): aggiorna il programma."),
        };

        private static readonly Dictionary<uint, (string Name, string Hint)> BugChecks = new Dictionary<uint, (string, string)>
        {
            { 0x0000000A, ("IRQL_NOT_LESS_OR_EQUAL", "Un driver ha usato la memoria in modo errato. Aggiorna i driver (soprattutto rete, video e chipset).") },
            { 0x0000001A, ("MEMORY_MANAGEMENT", "Problema nella gestione della memoria: controlla la RAM con \"Diagnostica memoria Windows\" e aggiorna i driver.") },
            { 0x0000001E, ("KMODE_EXCEPTION_NOT_HANDLED", "Un driver ha causato un errore: aggiorna i driver installati di recente.") },
            { 0x0000003B, ("SYSTEM_SERVICE_EXCEPTION", "Errore in un driver o servizio di sistema: aggiorna i driver video e Windows.") },
            { 0x00000050, ("PAGE_FAULT_IN_NONPAGED_AREA", "Accesso a memoria non valida: può dipendere da un driver, dall'antivirus o dalla RAM.") },
            { 0x0000007E, ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Un driver di sistema si è bloccato: aggiorna i driver, soprattutto quello video.") },
            { 0x0000007F, ("UNEXPECTED_KERNEL_MODE_TRAP", "Errore hardware o di driver: controlla temperature, RAM e overclock.") },
            { 0x0000009F, ("DRIVER_POWER_STATE_FAILURE", "Un driver non ha gestito bene sospensione/risveglio: aggiorna i driver (spesso rete o USB).") },
            { 0x000000C5, ("DRIVER_CORRUPTED_EXPOOL", "Un driver ha corrotto la memoria di sistema: aggiorna o rimuovi i driver installati di recente.") },
            { 0x000000D1, ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Un driver (spesso rete o Wi-Fi) ha usato la memoria in modo errato: aggiornalo.") },
            { 0x000000EF, ("CRITICAL_PROCESS_DIED", "Un processo fondamentale di Windows si è chiuso: esegui \"sfc /scannow\" e controlla il disco.") },
            { 0x000000F4, ("CRITICAL_OBJECT_TERMINATION", "Un processo fondamentale si è chiuso, spesso per problemi del disco: controlla il disco.") },
            { 0x00000116, ("VIDEO_TDR_FAILURE", "La scheda video ha smesso di rispondere: aggiorna il driver video e controlla le temperature.") },
            { 0x00000117, ("VIDEO_TDR_TIMEOUT_DETECTED", "La scheda video ha smesso di rispondere: aggiorna il driver video.") },
            { 0x00000124, ("WHEA_UNCORRECTABLE_ERROR", "Errore hardware (processore, RAM o alimentazione). Controlla temperature e togli eventuali overclock.") },
            { 0x00000133, ("DPC_WATCHDOG_VIOLATION", "Un driver o il disco SSD non ha risposto in tempo: aggiorna i driver del disco/chipset e il firmware dell'SSD.") },
            { 0x00000139, ("KERNEL_SECURITY_CHECK_FAILURE", "Un driver ha corrotto dati del sistema: aggiorna i driver e controlla la RAM.") },
            { 0x0000013A, ("KERNEL_MODE_HEAP_CORRUPTION", "Un driver ha corrotto la memoria: aggiorna i driver, soprattutto quello video.") },
            { 0x00000154, ("UNEXPECTED_STORE_EXCEPTION", "Problema col disco o con la memoria: controlla lo stato del disco.") },
            { 0x000001A5, ("ACPI_BIOS_ERROR", "Problema del BIOS: aggiorna il BIOS della scheda madre.") },
        };

        public static string ExplainException(string code)
        {
            var c = NormalizeCode(code);
            return ExceptionCodes.TryGetValue(c, out var t) ? t : "Il programma si è chiuso in modo imprevisto (codice 0x" + c + ").";
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
            return ("0x" + code.ToString("X8"), "Schermata blu: aggiorna Windows e i driver; se si ripete, controlla RAM e temperature.");
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
