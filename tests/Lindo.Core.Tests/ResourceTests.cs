using System;
using System.Linq;
using Lindo.Core;
using Xunit;

namespace Lindo.Core.Tests
{
    public class ResourceTests
    {
        [Fact]
        public void CpuPercentCountsIdleInsideKernelTime()
        {
            // 1000 tick totali (kernel 600 di cui 400 inattivi + user 400): occupato = 600 su 1000
            Assert.Equal(60, ResourceMath.CpuPercent(0, 0, 0, 400, 600, 400), 3);
            Assert.Equal(0, ResourceMath.CpuPercent(5, 5, 5, 5, 5, 5));
        }

        [Fact]
        public void ProcessCpuIsShareOfAllCores()
        {
            // 1 secondo di CPU in 1 secondo su 4 core = 25% del totale
            Assert.Equal(25, ResourceMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4), 3);
            Assert.Equal(0, ResourceMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        }

        [Fact]
        public void GroupSumsProcessesWithSameName()
        {
            var g = ResourceMath.Group(new[]
            {
                new ProcessUsage { Name = "chrome", Count = 1, MemoryBytes = 100, CpuPercent = 1 },
                new ProcessUsage { Name = "Chrome", Count = 1, MemoryBytes = 50, CpuPercent = 2 },
                new ProcessUsage { Name = "steam", Count = 1, MemoryBytes = 10, CpuPercent = 0 },
            });
            var chrome = g.Single(x => x.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, chrome.Count);
            Assert.Equal(150, chrome.MemoryBytes);
            Assert.Equal(3, chrome.CpuPercent, 3);
            Assert.Equal(2, g.Count);
        }

        [Fact]
        public void GpuPreferenceFormatMatchesWindowsSettings()
        {
            Assert.Equal("GpuPreference=2;", ResourceMath.GpuPreferenceValue(2));
            Assert.Equal(2, ResourceMath.ParseGpuPreference("GpuPreference=2;"));
            Assert.Equal(1, ResourceMath.ParseGpuPreference("SwapEffectUpgradeEnable=1;GpuPreference=1;"));
            Assert.Null(ResourceMath.ParseGpuPreference("SwapEffectUpgradeEnable=1;"));
        }

        [Fact]
        public void VirtualAdaptersAreNotRealGpus()
        {
            var gpus = ResourceMath.RealGpus(new[] { "NVIDIA GeForce GTX 1650", "Intel(R) UHD Graphics 630", "Microsoft Basic Display Adapter", "Parsec Virtual Display Adapter", "" });
            Assert.Equal(new[] { "NVIDIA GeForce GTX 1650", "Intel(R) UHD Graphics 630" }, gpus);
            Assert.Single(ResourceMath.RealGpus(new[] { "AMD Radeon RX 580", "AMD Radeon RX 580" }));
        }
    }
}
