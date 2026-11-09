using System;
using System.Collections.Generic;
using System.Linq;
using Lindo.Core;
using Xunit;

namespace Lindo.Core.Tests
{
    internal sealed class FakeFs : IFileSystemView
    {
        private readonly HashSet<string> _dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public FakeFs Dir(params string[] paths)
        {
            foreach (var p in paths)
            {
                var n = WinPath.Normalize(p);
                while (n.Length > 3) { _dirs.Add(n); n = WinPath.GetParent(n); }
            }
            return this;
        }

        public FakeFs File(params string[] paths)
        {
            foreach (var p in paths) { _files.Add(WinPath.Normalize(p)); Dir(WinPath.GetParent(p)); }
            return this;
        }

        public bool DirectoryExists(string path) => _dirs.Contains(WinPath.Normalize(path));
        public bool FileExists(string path) => _files.Contains(WinPath.Normalize(path));
        public IEnumerable<string> ListDirectories(string path) =>
            _dirs.Where(d => WinPath.Equal(WinPath.GetParent(d), path));
        public IEnumerable<string> ListFiles(string path) =>
            _files.Where(f => WinPath.Equal(WinPath.GetParent(f), path));
    }

    internal sealed class FakeReg : IRegistryView
    {
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public FakeReg Key(params string[] keys)
        {
            foreach (var k in keys)
            {
                var parts = k.Split('\\');
                for (int i = 1; i <= parts.Length; i++) _keys.Add(string.Join("\\", parts.Take(i)));
            }
            return this;
        }
        public bool KeyExists(string key) => _keys.Contains(key);
        public IEnumerable<string> SubKeyNames(string key)
        {
            var prefix = key + "\\";
            return _keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !k.Substring(prefix.Length).Contains("\\"))
                .Select(k => k.Substring(prefix.Length));
        }
    }

    public class WinPathTests
    {
        [Theory]
        [InlineData(@"C:\Program Files\X\", @"C:\Program Files\X")]
        [InlineData(@"c:/program files//x", @"c:\program files\x")]
        [InlineData(@"""C:\A\B""", @"C:\A\B")]
        [InlineData(@"C:\A\..\B", @"C:\B")]
        [InlineData(@"C:", @"C:\")]
        public void Normalize(string input, string expected) => Assert.Equal(expected, WinPath.Normalize(input));

        [Fact]
        public void Inside()
        {
            Assert.True(WinPath.IsSameOrInside(@"C:\Program Files\App\bin", @"C:\Program Files\App"));
            Assert.False(WinPath.IsSameOrInside(@"C:\Program Files\App2", @"C:\Program Files\App"));
            Assert.True(WinPath.IsStrictlyInside(@"C:\A\B", @"C:\A"));
            Assert.False(WinPath.IsStrictlyInside(@"C:\A", @"C:\A\"));
        }

        [Theory]
        [InlineData(@"""C:\Program Files\VLC\uninstall.exe"" /S", @"C:\Program Files\VLC\uninstall.exe")]
        [InlineData(@"C:\Program Files\VLC\uninstall.exe /S", @"C:\Program Files\VLC\uninstall.exe")]
        [InlineData(@"C:\Program Files\VLC\vlc.exe,0", @"C:\Program Files\VLC\vlc.exe")]
        [InlineData(@"MsiExec.exe /X{1234}", @"MsiExec.exe")]
        public void ExtractExecutable(string cmd, string exe) => Assert.Equal(exe, WinPath.ExtractExecutable(cmd));

        [Fact]
        public void SplitCommand()
        {
            var (f, a) = WinPath.SplitCommand(@"C:\Program Files\App Dir\unins000.exe /SILENT");
            Assert.Equal(@"C:\Program Files\App Dir\unins000.exe", f);
            Assert.Equal("/SILENT", a);
            (f, a) = WinPath.SplitCommand(@"""C:\x y\u.exe"" --uninstall");
            Assert.Equal(@"C:\x y\u.exe", f);
            Assert.Equal("--uninstall", a);
        }
    }

    public class NameMatcherTests
    {
        [Theory]
        [InlineData("VLC", "VLC media player", MatchStrength.Medium)]
        [InlineData("Spotify", "Spotify", MatchStrength.Strong)]
        [InlineData("Notepad++", "Notepad++ (64-bit x64)", MatchStrength.Strong)]
        [InlineData("7-Zip", "7-Zip 23.01 (x64)", MatchStrength.Strong)]
        [InlineData("Microsoft", "Microsoft Teams", MatchStrength.None)]
        [InlineData("Common Files", "Common App", MatchStrength.None)]
        [InlineData("Data", "Data Recovery Pro", MatchStrength.None)]
        [InlineData("Zoom", "Discord", MatchStrength.None)]
        [InlineData("Google", "Google Chrome", MatchStrength.None)] // con editore Google -> contenitore
        public void Matches(string folder, string app, MatchStrength expected)
        {
            var publisher = app.StartsWith("Google") ? "Google LLC" : null;
            Assert.Equal(expected, NameMatcher.Match(folder, app, null, publisher));
        }

        [Fact]
        public void InstallFolderNameIsStrong() =>
            Assert.Equal(MatchStrength.Strong, NameMatcher.Match("ObsStudioX", "OBS Studio", "ObsStudioX"));

        [Fact]
        public void PublisherKeyStripsCompanySuffix()
        {
            Assert.Equal("google", NameMatcher.PublisherKey("Google LLC"));
            Assert.Equal("videolan", NameMatcher.PublisherKey("VideoLAN"));
            Assert.Equal("mozilla", NameMatcher.PublisherKey("Mozilla Corporation"));
        }
    }

    public class PathGuardTests
    {
        private readonly PathGuard _g = new PathGuard(new SystemRoots());

        [Theory]
        [InlineData(@"C:\")]
        [InlineData(@"C:\Windows\System32")]
        [InlineData(@"C:\Windows")]
        [InlineData(@"C:\Program Files")]
        [InlineData(@"C:\Program Files (x86)")]
        [InlineData(@"C:\Program Files\Common Files")]
        [InlineData(@"C:\Program Files\WindowsApps\Foo")]
        [InlineData(@"C:\ProgramData")]
        [InlineData(@"C:\ProgramData\Microsoft")]
        [InlineData(@"C:\Users")]
        [InlineData(@"C:\Users\User")]
        [InlineData(@"C:\Users\User\AppData")]
        [InlineData(@"C:\Users\User\AppData\Local")]
        [InlineData(@"C:\Users\User\AppData\Local\Microsoft")]
        [InlineData(@"C:\Users\User\AppData\Local\Microsoft\Windows\Explorer")]
        [InlineData(@"C:\Users\User\AppData\Local\Programs")]
        [InlineData(@"C:\Users\User\AppData\Local\Temp")]
        [InlineData(@"C:\Users\User\Documents\Lavoro")]
        [InlineData(@"C:\Users\User\Desktop")]
        [InlineData(@"D:\Giochi\Qualcosa")]
        [InlineData(@"\\server\share\x")]
        [InlineData(@"C:\Program Files\..\Windows")]
        public void Blocks(string path) => Assert.False(_g.CanDelete(path), path);

        [Theory]
        [InlineData(@"C:\Program Files\VideoLAN\VLC")]
        [InlineData(@"C:\Program Files (x86)\Steam")]
        [InlineData(@"C:\ProgramData\Spotify")]
        [InlineData(@"C:\Users\User\AppData\Roaming\Spotify")]
        [InlineData(@"C:\Users\User\AppData\Local\Programs\Microsoft VS Code")]
        [InlineData(@"C:\Users\User\Desktop\VLC.lnk")]
        public void Allows(string path) => Assert.True(_g.CanDelete(path, path.EndsWith(".lnk") ? LeftoverKind.Shortcut : LeftoverKind.Folder), _g.WhyBlocked(path));
    }

    public class RegistryGuardTests
    {
        [Theory]
        [InlineData(@"HKCU\Software\Spotify", true)]
        [InlineData(@"HKLM\SOFTWARE\VideoLAN\VLC", true)]
        [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", true)]
        [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ABC}", true)]
        [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false)]
        [InlineData(@"HKLM\SOFTWARE\Microsoft", false)]
        [InlineData(@"HKLM\SOFTWARE\Microsoft\Office", false)]
        [InlineData(@"HKCU\Software\Classes\foo", false)]
        [InlineData(@"HKLM\SOFTWARE", false)]
        [InlineData(@"HKLM\SOFTWARE\WOW6432Node", false)]
        [InlineData(@"HKLM\SYSTEM\CurrentControlSet", false)]
        [InlineData(@"HKCU\Software\A\B\C", false)]
        public void Rules(string key, bool allowed) => Assert.Equal(allowed, RegistryGuard.CanDelete(key));
    }

    public class LeftoverPlannerTests
    {
        private static readonly SystemRoots R = new SystemRoots();

        private static AppInfo Vlc() => new AppInfo
        {
            Id = "vlc", Name = "VLC media player", Publisher = "VideoLAN", InstallLocation = @"C:\Program Files\VideoLAN\VLC",
            UninstallString = @"""C:\Program Files\VideoLAN\VLC\uninstall.exe""", DisplayIcon = @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VLC media player",
        };

        private static AppInfo Chrome() => new AppInfo
        {
            Id = "chrome", Name = "Google Chrome", Publisher = "Google LLC",
            InstallLocation = @"C:\Program Files\Google\Chrome\Application",
        };

        private static AppInfo Drive() => new AppInfo
        {
            Id = "drive", Name = "Google Drive", Publisher = "Google LLC",
            InstallLocation = @"C:\Program Files\Google\Drive File Stream",
        };

        [Fact]
        public void FindsVlcLeftoversButNothingElse()
        {
            var fs = new FakeFs()
                .Dir(@"C:\Program Files\VideoLAN\VLC\plugins", @"C:\Users\User\AppData\Roaming\vlc",
                     @"C:\Program Files\Google\Chrome\Application", @"C:\Users\User\AppData\Roaming\Spotify",
                     @"C:\ProgramData\Microsoft\Windows")
                .File(@"C:\Users\User\Desktop\VLC media player.lnk", @"C:\Users\User\Desktop\Spotify.lnk",
                      @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\VideoLAN\VLC media player.lnk");
            var reg = new FakeReg().Key(@"HKCU\Software\VideoLAN\VLC", @"HKCU\Software\Spotify",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VLC media player");

            var apps = new List<AppInfo> { Vlc(), Chrome(), new AppInfo { Id = "sp", Name = "Spotify", Publisher = "Spotify AB" } };
            var planner = new LeftoverPlanner(R, fs, reg);
            var res = planner.FindLeftovers(apps[0], apps);
            var paths = res.Select(r => r.Path).ToList();

            Assert.Contains(@"C:\Program Files\VideoLAN\VLC", paths);
            Assert.Contains(@"C:\Users\User\AppData\Roaming\vlc", paths);
            Assert.Contains(@"C:\Users\User\Desktop\VLC media player.lnk", paths);
            Assert.Contains(@"HKCU\Software\VideoLAN\VLC", paths);
            Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VLC media player", paths);

            Assert.DoesNotContain(paths, p => p.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.DoesNotContain(paths, p => p.IndexOf("Google", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\VideoLAN\VLC media player.lnk", paths);
            Assert.DoesNotContain(paths, p => p.StartsWith(@"C:\ProgramData\Microsoft\Windows", StringComparison.OrdinalIgnoreCase) && !p.Contains("Start Menu"));
            Assert.All(res.Where(r => r.Kind != LeftoverKind.RegistryKey), r => Assert.True(new PathGuard(R).CanDelete(r.Path, r.Kind)));
        }

        [Fact]
        public void SharedPublisherFolderIsNeverProposed()
        {
            var fs = new FakeFs().Dir(
                @"C:\Program Files\Google\Chrome\Application",
                @"C:\Program Files\Google\Drive File Stream",
                @"C:\Users\User\AppData\Local\Google\Chrome\User Data",
                @"C:\Users\User\AppData\Local\Google\DriveFS");
            var apps = new List<AppInfo> { Chrome(), Drive() };
            var res = new LeftoverPlanner(R, fs, new FakeReg()).FindLeftovers(apps[0], apps);
            var paths = res.Select(r => r.Path).ToList();

            Assert.Contains(@"C:\Program Files\Google\Chrome", paths);
            Assert.Contains(@"C:\Users\User\AppData\Local\Google\Chrome", paths);
            Assert.DoesNotContain(@"C:\Program Files\Google", paths);
            Assert.DoesNotContain(@"C:\Users\User\AppData\Local\Google", paths);
            Assert.DoesNotContain(paths, p => p.Contains("Drive"));
        }

        [Fact]
        public void InstallLocationContainingOtherAppIsSkipped()
        {
            var fs = new FakeFs().Dir(@"C:\Program Files\Suite\Editor", @"C:\Program Files\Suite\Viewer");
            var a = new AppInfo { Id = "a", Name = "Suite", Publisher = "Acme", InstallLocation = @"C:\Program Files\Suite" };
            var b = new AppInfo { Id = "b", Name = "Suite Viewer", Publisher = "Acme", InstallLocation = @"C:\Program Files\Suite\Viewer" };
            var res = new LeftoverPlanner(R, fs, null).FindLeftovers(a, new[] { a, b });
            Assert.DoesNotContain(res, r => r.Path == @"C:\Program Files\Suite");
        }

        [Fact]
        public void BogusInstallLocationIsRejected()
        {
            var fs = new FakeFs().Dir(@"C:\Program Files\Something", @"C:\Windows\System32");
            var a = new AppInfo { Id = "a", Name = "Bad App", InstallLocation = @"C:\Program Files", UninstallString = @"C:\Windows\System32\msiexec.exe /x{1}" };
            var res = new LeftoverPlanner(R, fs, null).FindLeftovers(a, new[] { a });
            Assert.Empty(res);
        }

        [Fact]
        public void OrphansIgnoreSystemAndOwnedFolders()
        {
            var fs = new FakeFs().Dir(
                @"C:\Program Files\VideoLAN\VLC",
                @"C:\Program Files\OldGame",
                @"C:\Program Files\Common Files\x",
                @"C:\ProgramData\Microsoft\x",
                @"C:\ProgramData\Package Cache\x",
                @"C:\ProgramData\{2F1A0000-1111-2222-3333-444455556666}",
                @"C:\Users\User\AppData\Roaming\vlc",
                @"C:\Users\User\AppData\Roaming\LeftoverTool",
                @"C:\Users\User\AppData\Local\Google\Chrome",
                @"C:\Users\User\AppData\Local\Programs\Ghost App");
            var apps = new List<AppInfo> { Vlc(), Chrome() };
            var res = new LeftoverPlanner(R, fs, null).FindOrphans(apps).Select(r => r.Path).ToList();

            Assert.Contains(@"C:\Program Files\OldGame", res);
            Assert.Contains(@"C:\Users\User\AppData\Roaming\LeftoverTool", res);
            Assert.Contains(@"C:\Users\User\AppData\Local\Programs\Ghost App", res);
            Assert.Equal(3, res.Count);
        }
    }

    public class SizeFormatTests
    {
        [Fact]
        public void Formats()
        {
            Assert.Equal("—", SizeFormat.Format(0));
            Assert.Equal("512 B", SizeFormat.Format(512));
            Assert.Equal("1.5 KB", SizeFormat.Format(1536));
            Assert.Equal("1 GB", SizeFormat.Format(1024L * 1024 * 1024));
        }
    }
}
