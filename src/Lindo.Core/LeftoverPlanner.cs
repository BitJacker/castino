using System;
using System.Collections.Generic;
using System.Linq;

namespace Lindo.Core
{
    /// <summary>Vista in sola lettura del file system (implementata su Windows, simulata nei test).</summary>
    public interface IFileSystemView
    {
        bool DirectoryExists(string path);
        bool FileExists(string path);
        /// <summary>Sottocartelle dirette (percorsi completi), esclusi i reparse point (junction/symlink).</summary>
        IEnumerable<string> ListDirectories(string path);
        /// <summary>File diretti (percorsi completi).</summary>
        IEnumerable<string> ListFiles(string path);
    }

    /// <summary>Vista in sola lettura del registro. Le chiavi sono nel formato "HKCU\Software\X" / "HKLM\SOFTWARE\X".</summary>
    public interface IRegistryView
    {
        bool KeyExists(string key);
        IEnumerable<string> SubKeyNames(string key);
    }

    /// <summary>
    /// Cerca i residui lasciati da un'applicazione (cartelle, collegamenti, chiavi di registro)
    /// e le cartelle orfane del sistema. Non elimina nulla: produce solo proposte.
    /// </summary>
    public sealed class LeftoverPlanner
    {
        private readonly SystemRoots _roots;
        private readonly IFileSystemView _fs;
        private readonly IRegistryView _reg;
        private readonly PathGuard _guard;

        /// <summary>Risolve la destinazione di un collegamento .lnk (opzionale).</summary>
        public Func<string, string> ShortcutTargetResolver { get; set; }

        public static readonly HashSet<string> ProtectedRegistryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Classes", "Policies", "Wow6432Node", "WOW6432Node", "Clients", "RegisteredApplications",
            "ODBC", "Intel", "Khronos", "Partner", "DefaultUserEnvironment", "Description", "Hardware", "Setup",
            "System", "Software", "AppDataLow", "Chromium", "Mozilla", "MozillaPlugins", "Google", "Apple Inc.", "Apple Computer, Inc.",
            "Python", "JavaSoft", "Oracle", "VMware, Inc.", "NVIDIA Corporation", "Realtek", "Synaptics", "Dolby",
            "Lindo",
        };

        /// <summary>Chiavi in cui non si entra mai, nemmeno come "cartella dell'editore".</summary>
        public static readonly HashSet<string> HardProtectedRegistryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications",
            "ODBC", "Software", "System", "Hardware", "Setup", "Description", "Lindo",
        };

        public LeftoverPlanner(SystemRoots roots, IFileSystemView fs, IRegistryView reg, PathGuard guard = null)
        {
            _roots = roots ?? throw new ArgumentNullException(nameof(roots));
            _fs = fs ?? throw new ArgumentNullException(nameof(fs));
            _reg = reg;
            _guard = guard ?? new PathGuard(roots);
        }

        private static Confidence ToConfidence(MatchStrength s) =>
            s == MatchStrength.Strong ? Confidence.High :
            s == MatchStrength.Medium ? Confidence.Medium : Confidence.Low;

        // ------------------------------------------------------------------ app specifica

        public List<Leftover> FindLeftovers(AppInfo app, IEnumerable<AppInfo> allApps)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            var others = (allApps ?? Enumerable.Empty<AppInfo>())
                .Where(a => !ReferenceEquals(a, app) && a.Id != app.Id)
                // Le altre voci con lo stesso nome (es. versione 32 e 64 bit dello stesso prodotto) non contano come "altre app".
                .Where(a => !string.Equals(NameMatcher.Compact(a.Name), NameMatcher.Compact(app.Name), StringComparison.Ordinal))
                .ToList();

            var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);

            void Add(LeftoverKind kind, string path, Confidence conf, string reason)
            {
                var norm = kind == LeftoverKind.RegistryKey ? path.TrimEnd('\\') : WinPath.Normalize(path);
                if (kind != LeftoverKind.RegistryKey && !_guard.CanDelete(norm, kind)) return;
                if (found.TryGetValue(norm, out var existing))
                {
                    if (conf > existing.Confidence) { existing.Confidence = conf; existing.Reason = reason; }
                    return;
                }
                found[norm] = new Leftover { Kind = kind, Path = norm, Confidence = conf, Reason = reason };
            }

            var installFolder = app.InstallFolderName;

            // 1) Cartella di installazione dichiarata
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) && _fs.DirectoryExists(app.InstallLocation))
            {
                if (!IsSharedWithOthers(app.InstallLocation, others))
                    Add(LeftoverKind.Folder, app.InstallLocation, Confidence.High, "Cartella di installazione");
            }

            // 2) Cartella del disinstallatore / dell'icona, se ha il nome del programma
            foreach (var cmd in new[] { app.UninstallString, app.DisplayIcon })
            {
                var exe = WinPath.ExtractExecutable(cmd);
                if (exe.Length == 0) continue;
                var dir = WinPath.GetParent(exe);
                if (dir.Length == 0 || !_fs.DirectoryExists(dir)) continue;
                var s = NameMatcher.Match(WinPath.GetFileName(dir), app.Name, installFolder, app.Publisher);
                if (s >= MatchStrength.Medium && !IsSharedWithOthers(dir, others))
                    Add(LeftoverKind.Folder, dir, Confidence.High, "Cartella del programma");
            }

            // 3) Cartelle dati nelle radici note (+ un livello sotto la cartella dell'editore)
            foreach (var root in _roots.DataRoots().Where(_fs.DirectoryExists))
            {
                foreach (var dir in SafeList(() => _fs.ListDirectories(root)))
                {
                    var name = WinPath.GetFileName(dir);
                    if (PathGuard.SystemFolderNames.Contains(name)) continue;

                    if (IsPublisherFolder(name, app))
                    {
                        foreach (var sub in SafeList(() => _fs.ListDirectories(dir)))
                        {
                            var s2 = MatchInsidePublisher(WinPath.GetFileName(sub), app);
                            if (s2 > MatchStrength.None && !OwnedByOther(sub, s2, others))
                                Add(LeftoverKind.Folder, sub, ToConfidence(s2), "Dati del programma (" + name + ")");
                        }
                        continue;
                    }

                    var s = NameMatcher.Match(name, app.Name, installFolder, app.Publisher);
                    if (s > MatchStrength.None && !OwnedByOther(dir, s, others))
                        Add(LeftoverKind.Folder, dir, ToConfidence(s), "Dati del programma");
                }
            }

            // 4) Menu Start e Desktop
            var installDirs = found.Values.Where(l => l.Kind == LeftoverKind.Folder && l.Confidence == Confidence.High)
                .Select(l => l.Path).ToList();
            if (!string.IsNullOrWhiteSpace(app.InstallLocation)) installDirs.Add(WinPath.Normalize(app.InstallLocation));

            foreach (var startRoot in new[] { _roots.UserStartPrograms, _roots.CommonStartPrograms }.Where(_fs.DirectoryExists))
            {
                foreach (var dir in SafeList(() => _fs.ListDirectories(startRoot)))
                {
                    var name = WinPath.GetFileName(dir);
                    if (name.Equals("Startup", StringComparison.OrdinalIgnoreCase)) continue;
                    var s = IsPublisherFolder(name, app) ? MatchStrength.None : NameMatcher.Match(name, app.Name, installFolder, app.Publisher);
                    if (s >= MatchStrength.Medium && !OwnedByOther(dir, s, others))
                        Add(LeftoverKind.Shortcut, dir, ToConfidence(s), "Cartella nel menu Start");
                    else
                        ScanShortcuts(dir, app, others, installDirs, Add);
                }
                ScanShortcuts(startRoot, app, others, installDirs, Add);
            }
            foreach (var desk in new[] { _roots.UserDesktop, _roots.PublicDesktop }.Where(_fs.DirectoryExists))
                ScanShortcuts(desk, app, others, installDirs, Add);

            // 5) Registro
            if (_reg != null)
            {
                if (!string.IsNullOrWhiteSpace(app.RegistryKey) && _reg.KeyExists(app.RegistryKey))
                    Add(LeftoverKind.RegistryKey, app.RegistryKey, Confidence.High, "Voce di disinstallazione rimasta");

                foreach (var baseKey in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
                {
                    if (!_reg.KeyExists(baseKey)) continue;
                    foreach (var sub in SafeList(() => _reg.SubKeyNames(baseKey)))
                    {
                        if (HardProtectedRegistryNames.Contains(sub)) continue;
                        var full = baseKey + "\\" + sub;
                        if (IsPublisherFolder(sub, app))
                        {
                            foreach (var sub2 in SafeList(() => _reg.SubKeyNames(full)))
                            {
                                var s2 = MatchInsidePublisher(sub2, app);
                                if (s2 >= MatchStrength.Medium && !OwnedByOtherName(sub2, s2, others))
                                    Add(LeftoverKind.RegistryKey, full + "\\" + sub2, ToConfidence(s2), "Impostazioni nel registro");
                            }
                            continue;
                        }
                        if (ProtectedRegistryNames.Contains(sub)) continue;
                        var s = NameMatcher.Match(sub, app.Name, installFolder, app.Publisher);
                        if (s >= MatchStrength.Medium && !OwnedByOtherName(sub, s, others))
                            Add(LeftoverKind.RegistryKey, full, ToConfidence(s), "Impostazioni nel registro");
                    }
                }
            }

            // Rimuove le voci contenute in una cartella già proposta (verrebbero cancellate comunque)
            var folders = found.Values.Where(l => l.Kind == LeftoverKind.Folder || (l.Kind == LeftoverKind.Shortcut && _fs.DirectoryExists(l.Path))).ToList();
            var result = found.Values
                .Where(l => l.Kind == LeftoverKind.RegistryKey ||
                            !folders.Any(f => !ReferenceEquals(f, l) && WinPath.IsStrictlyInside(l.Path, f.Path)))
                .OrderByDescending(l => l.Confidence)
                .ThenBy(l => l.Kind)
                .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        private void ScanShortcuts(string dir, AppInfo app, List<AppInfo> others, List<string> installDirs,
            Action<LeftoverKind, string, Confidence, string> add)
        {
            foreach (var file in SafeList(() => _fs.ListFiles(dir)))
            {
                if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                var name = WinPath.GetFileName(file);
                name = name.Substring(0, name.Length - 4);

                // Collegamento che punta dentro la cartella del programma: certo
                if (ShortcutTargetResolver != null && file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = null;
                    try { target = ShortcutTargetResolver(file); } catch { /* collegamento illeggibile */ }
                    if (!string.IsNullOrWhiteSpace(target) && installDirs.Any(d => WinPath.IsSameOrInside(target, d)))
                    {
                        add(LeftoverKind.Shortcut, file, Confidence.High, "Collegamento al programma");
                        continue;
                    }
                }

                var s = NameMatcher.Match(name, app.Name, app.InstallFolderName, app.Publisher);
                if (s >= MatchStrength.Medium && !OwnedByOtherName(name, s, others))
                    add(LeftoverKind.Shortcut, file, ToConfidence(s), "Collegamento");
            }
        }

        /// <summary>Dentro la cartella dell'editore si confronta anche il nome senza editore ("Google\Chrome").</summary>
        private static MatchStrength MatchInsidePublisher(string name, AppInfo app)
        {
            var a = NameMatcher.Match(name, app.Name, app.InstallFolderName, app.Publisher);
            var stripped = NameMatcher.StripPublisher(app.Name, app.Publisher);
            var b = stripped == app.Name ? MatchStrength.None : NameMatcher.Match(name, stripped, app.InstallFolderName, null);
            return a > b ? a : b;
        }

        private static bool IsPublisherFolder(string name, AppInfo app)
        {
            var pub = NameMatcher.PublisherKey(app.Publisher);
            if (pub.Length < 2) return false;
            var c = NameMatcher.Compact(name);
            var raw = NameMatcher.RawCompact(name);
            if (c != pub && raw != NameMatcher.RawCompact(app.Publisher)) return false;
            // Se l'app si chiama esattamente come l'editore (es. "Spotify" di "Spotify AB") non è un contenitore
            return NameMatcher.Compact(app.Name) != c;
        }

        /// <summary>La cartella contiene o è contenuta nella cartella di installazione di un'altra app.</summary>
        private static bool IsSharedWithOthers(string dir, List<AppInfo> others) =>
            others.Any(o => !string.IsNullOrWhiteSpace(o.InstallLocation) &&
                            WinPath.Depth(o.InstallLocation) > 1 &&
                            (WinPath.IsSameOrInside(o.InstallLocation, dir) || WinPath.IsSameOrInside(dir, o.InstallLocation)));

        /// <summary>La cartella sembra appartenere (almeno altrettanto) a un'altra app ancora installata.</summary>
        private static bool OwnedByOther(string dir, MatchStrength mine, List<AppInfo> others)
        {
            if (IsSharedWithOthers(dir, others)) return true;
            return OwnedByOtherName(WinPath.GetFileName(dir), mine, others);
        }

        private static bool OwnedByOtherName(string name, MatchStrength mine, List<AppInfo> others)
        {
            foreach (var o in others)
            {
                var s = NameMatcher.Match(name, o.Name, o.InstallFolderName, o.Publisher);
                if (s == MatchStrength.None) continue;
                if (s >= mine) return true;
                if (s >= MatchStrength.Medium) return true;
            }
            // Cartella con il nome dell'editore di un'altra app installata
            var c = NameMatcher.Compact(name);
            return others.Any(o => NameMatcher.PublisherKey(o.Publisher) == c && c.Length >= 2);
        }

        // ------------------------------------------------------------------ orfani

        /// <summary>
        /// Cartelle di primo livello che non sembrano appartenere a nessuna app installata.
        /// Con <paramref name="evidence"/> vengono divise in "sicure" (Confidence.High) e "da controllare" (Low):
        /// una cartella è sicura solo se nessun indizio dice che è ancora usata.
        /// </summary>
        public List<Leftover> FindOrphans(IEnumerable<AppInfo> allApps, OrphanEvidence evidence = null)
        {
            var apps = (allApps ?? Enumerable.Empty<AppInfo>()).ToList();
            var publisherKeys = new HashSet<string>(apps.Select(a => NameMatcher.PublisherKey(a.Publisher)).Where(k => k.Length >= 2));
            var result = new List<Leftover>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in _roots.DataRoots().Where(_fs.DirectoryExists))
            {
                foreach (var dir in SafeList(() => _fs.ListDirectories(root)))
                {
                    var name = WinPath.GetFileName(dir);
                    if (!seen.Add(WinPath.Normalize(dir))) continue;
                    if (PathGuard.SystemFolderNames.Contains(name)) continue;
                    // Componenti di Windows / Microsoft: mai proposti come orfani
                    if (name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("{", StringComparison.Ordinal)) continue;
                    if (name.StartsWith("$", StringComparison.Ordinal)) continue;
                    if (LooksLikeId(name)) continue;
                    if (!_guard.CanDelete(dir)) continue;
                    if (IsOwnedByAny(dir, name, apps, publisherKeys)) continue;
                    if (KnownTools.IsKnownTool(name)) continue;

                    var item = new Leftover
                    {
                        Kind = LeftoverKind.Folder,
                        Path = WinPath.Normalize(dir),
                        Confidence = Confidence.Low,
                        Reason = "Nessun programma installato usa questa cartella",
                    };

                    if (evidence != null)
                    {
                        // Indizi forti di utilizzo: la cartella non viene proposta per niente
                        var used = evidence.InUsePaths.FirstOrDefault(p => WinPath.IsSameOrInside(p.Path, dir));
                        if (used != null) continue;

                        var newest = evidence.NewestActivity?.Invoke(dir);
                        item.LastModified = newest;
                        if (newest.HasValue && evidence.Now - newest.Value < TimeSpan.FromDays(evidence.RecentDays))
                        {
                            item.Confidence = Confidence.Low;
                            item.Reason = "Usata di recente (" + TimeText.Ago(newest.Value, evidence.Now) + "): forse serve ancora";
                        }
                        else
                        {
                            item.Confidence = Confidence.High;
                            item.Reason = newest.HasValue
                                ? "Nessun programma la usa, ferma da " + TimeText.Ago(newest.Value, evidence.Now).Replace(" fa", "")
                                : "Nessun programma la usa";
                        }
                    }
                    result.Add(item);
                }
            }
            return result;
        }

        private static bool LooksLikeId(string name)
        {
            // GUID, hash, cartelle numeriche: troppo incerte per essere proposte
            var raw = NameMatcher.RawCompact(name);
            if (raw.Length >= 16 && raw.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return true;
            return raw.All(char.IsDigit);
        }

        private static bool IsOwnedByAny(string dir, string name, List<AppInfo> apps, HashSet<string> publisherKeys)
        {
            var c = NameMatcher.Compact(name);
            if (c.Length > 0 && publisherKeys.Contains(c)) return true;
            var raw = NameMatcher.RawCompact(name);

            foreach (var a in apps)
            {
                if (!string.IsNullOrWhiteSpace(a.InstallLocation) && WinPath.Depth(a.InstallLocation) > 1 &&
                    (WinPath.IsSameOrInside(a.InstallLocation, dir) || WinPath.IsSameOrInside(dir, a.InstallLocation)))
                    return true;

                foreach (var cmd in new[] { a.UninstallString, a.DisplayIcon })
                {
                    var exe = WinPath.ExtractExecutable(cmd);
                    if (exe.Length > 0 && WinPath.IsSameOrInside(exe, dir)) return true;
                }

                if (NameMatcher.Match(name, a.Name, a.InstallFolderName, a.Publisher) > MatchStrength.None) return true;

                // Corrispondenza più permissiva per evitare falsi positivi: un token in comune di almeno 4 lettere
                var appRaw = NameMatcher.RawCompact(a.Name);
                if (raw.Length >= 4 && appRaw.Contains(raw)) return true;
                if (appRaw.Length >= 4 && raw.Contains(appRaw)) return true;
                if (NameMatcher.Tokens(name).Intersect(NameMatcher.Tokens(a.Name)).Any(t => t.Length >= 4)) return true;
                if (!string.IsNullOrWhiteSpace(a.PackageFamilyName) &&
                    NameMatcher.RawCompact(a.PackageFamilyName).Contains(raw) && raw.Length >= 4) return true;
            }
            return false;
        }

        private static IEnumerable<string> SafeList(Func<IEnumerable<string>> f)
        {
            try { return (f() ?? Enumerable.Empty<string>()).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }
    }
}
