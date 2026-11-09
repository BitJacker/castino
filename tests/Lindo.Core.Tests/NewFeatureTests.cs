using System;
using System.Collections.Generic;
using System.Linq;
using Lindo.Core;
using Xunit;

namespace Lindo.Core.Tests
{
    public class InfParserTests
    {
        private const string EpsonInf = @"
; Driver stampante
[Version]
Signature=""$Windows NT$""
Class=Printer
ClassGUID={4D36E979-E325-11CE-BFC1-08002BE10318}
Provider=%EPSON%
DriverVer=03/15/2023,3.1.0.0 ; versione
CatalogFile=E_YFPS.CAT

[Manufacturer]
%EPSON%=EPSON,NTamd64

[EPSON.NTamd64]
""EPSON ET-2810 Series"" = EPET2810.GPD, USBPRINT\EPSONET-2810_SERIES

[Strings]
EPSON=""EPSON""
";

        [Fact]
        public void ParsesVersionSection()
        {
            var i = InfParser.Parse(EpsonInf);
            Assert.Equal("Printer", i.Class);
            Assert.Equal("EPSON", i.Provider);
            Assert.Equal("3.1.0.0", i.Version);
            Assert.Equal(new DateTime(2023, 3, 15), i.Date);
            Assert.Equal("EPSON ET-2810 Series", i.Description);
        }

        [Fact]
        public void GarbageDoesNotThrow()
        {
            var i = InfParser.Parse("[[[[\n=\n;;;\n[Version\nProvider=%x%");
            Assert.NotNull(i);
        }

        [Fact]
        public void PrinterDriverMatchesPrinterApp()
        {
            var d = InfParser.Parse(EpsonInf);
            var app = new AppInfo { Name = "Epson Print and Scan", Publisher = "SEIKO EPSON CORPORATION" };
            Assert.True(DriverRules.MatchDriverToApp(d, app) >= MatchStrength.Medium);
            var other = new AppInfo { Name = "VLC media player", Publisher = "VideoLAN" };
            Assert.Equal(MatchStrength.None, DriverRules.MatchDriverToApp(d, other));
        }

        [Fact]
        public void CriticalDriversNeverMatch()
        {
            var gpu = new InfInfo { Class = "Display", Provider = "EPSON", Description = "Epson display" };
            Assert.Equal(MatchStrength.None, DriverRules.MatchDriverToApp(gpu, new AppInfo { Name = "Epson Display", Publisher = "EPSON" }));
            var nvidia = new InfInfo { Class = "Printer", Provider = "NVIDIA" };
            Assert.Equal(MatchStrength.None, DriverRules.MatchDriverToApp(nvidia, new AppInfo { Name = "NVIDIA App", Publisher = "NVIDIA Corporation" }));
            Assert.True(DriverRules.IsCritical("Net"));
            Assert.False(DriverRules.IsCritical("Printer"));
        }
    }

    public class UsageTests
    {
        [Fact]
        public void Rot13RoundTrip()
        {
            Assert.Equal(@"P:\Clguba27\clguba.rkr", UserAssist.Rot13(@"C:\Python27\python.exe"));
            Assert.Equal(@"C:\Python27\python.exe", UserAssist.Rot13(UserAssist.Rot13(@"C:\Python27\python.exe")));
        }

        [Fact]
        public void ParsesUserAssistValue()
        {
            var data = new byte[72];
            BitConverter.GetBytes(7).CopyTo(data, 4);
            var when = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            BitConverter.GetBytes(when.ToFileTimeUtc()).CopyTo(data, 60);
            var r = UserAssist.ParseValue(data);
            Assert.NotNull(r);
            Assert.Equal(7, r.Value.Count);
            Assert.Equal(when.ToLocalTime(), r.Value.LastRun);
            Assert.Null(UserAssist.ParseValue(new byte[16]));
        }

        [Fact]
        public void ExpandsKnownFolders()
        {
            var p = UserAssist.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\VideoLAN\VLC\vlc.exe",
                k => k == "ProgramFiles" ? @"C:\Program Files" : null);
            Assert.Equal(@"C:\Program Files\VideoLAN\VLC\vlc.exe", p);
        }

        [Fact]
        public void StartupApprovedFormat()
        {
            Assert.True(StartupApproved.IsEnabled(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.True(StartupApproved.IsEnabled(new byte[] { 6, 0, 0, 0 }));
            Assert.False(StartupApproved.IsEnabled(new byte[] { 3, 0, 0, 0 }));
            Assert.True(StartupApproved.IsEnabled(null));
            var off = StartupApproved.Make(false);
            Assert.Equal(12, off.Length);
            Assert.False(StartupApproved.IsEnabled(off));
            Assert.True(StartupApproved.IsEnabled(StartupApproved.Make(true)));
        }

        [Fact]
        public void UsageStoreRoundTrip()
        {
            var s = new UsageStore();
            var r = s.Get(@"C:\Program Files\App\app.exe", "App");
            r.LastSeen = new DateTime(2026, 1, 2);
            r.SecondsRunning = 3600; r.CpuMs = 1234; r.PeakMemoryMb = 300; r.MemorySamplesMb = 1000; r.Samples = 10; r.Launches = 3;
            var back = UsageStore.Parse(s.Serialize());
            var b = back.Records[@"c:\program files\app\app.exe"];
            Assert.Equal(3600, b.SecondsRunning);
            Assert.Equal(100, b.AverageMemoryMb);
            Assert.Equal(3, b.Launches);
            Assert.Empty(UsageStore.Parse("garbage\tline").Records);
        }

        [Fact]
        public void TimeTextItalian()
        {
            var now = new DateTime(2026, 11, 9);
            Assert.Equal("oggi", TimeText.Ago(now, now));
            Assert.Equal("ieri", TimeText.Ago(now.AddDays(-1), now));
            Assert.Equal("5 giorni fa", TimeText.Ago(now.AddDays(-5), now));
            Assert.Equal("3 mesi fa", TimeText.Ago(now.AddDays(-95), now));
            Assert.Equal("2 anni fa", TimeText.Ago(now.AddDays(-800), now));
        }
    }

    public class OrphanSafetyTests
    {
        private static readonly SystemRoots R = new SystemRoots();

        [Fact]
        public void ToolsAndUsedFoldersAreNeverSafe()
        {
            var fs = new FakeFs().Dir(
                @"C:\Users\User\AppData\Roaming\npm",
                @"C:\Users\User\AppData\Local\npm-cache",
                @"C:\Users\User\AppData\Local\AnthropicClaude",
                @"C:\Users\User\AppData\Roaming\Claude",
                @"C:\Program Files\SomeTool\bin",
                @"C:\Program Files\RunningThing",
                @"C:\ProgramData\RecentStuff",
                @"C:\Program Files (x86)\OldGame",
                @"C:\Users\User\AppData\Roaming\OldEditor");
            var ev = new OrphanEvidence { Now = new DateTime(2026, 11, 9) };
            ev.Add(@"C:\Program Files\SomeTool\bin", "PATH");
            ev.Add(@"C:\Program Files\RunningThing\x.exe", "In esecuzione");
            ev.Add(@"C:\", "radice: deve essere ignorata");
            ev.NewestActivity = p => p.Contains("Recent") ? new DateTime(2026, 11, 1) : new DateTime(2025, 1, 1);

            var res = new LeftoverPlanner(R, fs, null).FindOrphans(new List<AppInfo>(), ev);
            var safe = res.Where(r => r.Confidence == Confidence.High).Select(r => r.Path).ToList();
            var check = res.Where(r => r.Confidence == Confidence.Low).Select(r => r.Path).ToList();

            Assert.Equal(new[] { @"C:\Program Files (x86)\OldGame", @"C:\Users\User\AppData\Roaming\OldEditor" }.OrderBy(x => x),
                         safe.OrderBy(x => x));
            Assert.Equal(new[] { @"C:\ProgramData\RecentStuff" }, check);
            Assert.DoesNotContain(res, r => r.Path.Contains("npm") || r.Path.Contains("Claude") || r.Path.Contains("SomeTool") || r.Path.Contains("RunningThing"));
        }

        [Fact]
        public void ExtraDriveRootsAreScannedAndGuarded()
        {
            var roots = new SystemRoots { ExtraProgramRoots = { @"D:\Giochi" } };
            var fs = new FakeFs().Dir(@"D:\Giochi\VecchioGioco", @"D:\Giochi\GiocoInstallato", @"D:\Foto\Vacanze");
            var app = new AppInfo { Id = "g", Name = "Gioco Installato", InstallLocation = @"D:\Giochi\GiocoInstallato" };
            var res = new LeftoverPlanner(roots, fs, null).FindOrphans(new[] { app }).Select(r => r.Path).ToList();
            Assert.Equal(new[] { @"D:\Giochi\VecchioGioco" }, res);

            var g = new PathGuard(roots);
            Assert.False(g.CanDelete(@"D:\Giochi"));
            Assert.False(g.CanDelete(@"D:\Foto\Vacanze"));
            Assert.False(g.CanDelete(@"D:\"));
            Assert.True(g.CanDelete(@"D:\Giochi\VecchioGioco"));
        }

        [Fact]
        public void KnownToolsList()
        {
            Assert.True(KnownTools.IsKnownTool("npm"));
            Assert.True(KnownTools.IsKnownTool("npm-cache"));
            Assert.True(KnownTools.IsKnownTool("claude-code"));
            Assert.True(KnownTools.IsKnownTool("Docker Desktop"));
            Assert.False(KnownTools.IsKnownTool("OldGame"));
        }
    }

    public class CrashAndStartupTests
    {
        [Fact]
        public void ExplainsCommonCrashes()
        {
            Assert.Contains("memoria", CrashExplainer.ExplainException("0xc0000005"));
            Assert.Contains(".NET", CrashExplainer.ExplainException("e0434352"));
            Assert.Contains("0x12345678", CrashExplainer.ExplainException("0x12345678"));
            Assert.Contains("NVIDIA", CrashExplainer.ExplainModule("nvwgf2umx.dll"));
            Assert.Null(CrashExplainer.ExplainModule("geometrydash.exe"));
            Assert.Equal("VIDEO_TDR_FAILURE", CrashExplainer.ExplainBugCheck(0x116).Name);
            Assert.Equal(0x1Au, CrashExplainer.ParseBugCheck("0x0000001a (0x0000000000041792, ...)"));
            Assert.Equal(0x133u, CrashExplainer.ParseBugCheck("0x00000133"));
        }

        [Fact]
        public void StartupAdvice()
        {
            Assert.True(StartupAdvisor.SuggestDisable("FortiTray", "Fortinet Inc", @"C:\Program Files\Fortinet\FortiClient\FortiTray.exe"));
            Assert.True(StartupAdvisor.SuggestDisable("Discord", "Discord Inc.", "Update.exe --processStart Discord.exe"));
            Assert.False(StartupAdvisor.SuggestDisable("SecurityHealth", "Microsoft", "SecurityHealthSystray.exe"));
            Assert.False(StartupAdvisor.SuggestDisable("RtkAudUService", "Realtek", "RtkAudUService64.exe"));
            Assert.False(StartupAdvisor.SuggestDisable("OneDrive", "Microsoft", "OneDrive.exe /background"));
        }
    }
}
