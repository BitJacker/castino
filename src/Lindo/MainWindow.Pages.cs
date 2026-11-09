using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Lindo.Core;
using Lindo.Services;

namespace Lindo
{
    /// <summary>Pagine "Avvio e attività", "Ottimizza" ed "Errori e crash".</summary>
    public partial class MainWindow
    {
        private readonly ObservableCollection<StartupItem> _startup = new ObservableCollection<StartupItem>();
        private readonly ObservableCollection<UsageRow> _usage = new ObservableCollection<UsageRow>();
        private readonly ObservableCollection<OptimizeItem> _optimize = new ObservableCollection<OptimizeItem>();
        private readonly ObservableCollection<CrashItem> _crashes = new ObservableCollection<CrashItem>();
        private List<StartupEntry> _startupEntries = new List<StartupEntry>();
        private bool _startupLoaded, _optimizeLoaded, _crashesLoaded;

        private void InitPages()
        {
            StartupList.ItemsSource = _startup;
            UsageList.ItemsSource = _usage;
            CrashList.ItemsSource = _crashes;
            var ov = (ListCollectionView)CollectionViewSource.GetDefaultView(_optimize);
            ov.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OptimizeItem.Category)));
            OptimizeList.ItemsSource = ov;
        }

        // ================================================================== Avvio e attività

        private async Task LoadStartupAsync()
        {
            StartupLoading.Visibility = Visibility.Visible;
            _startup.Clear();
            try
            {
                _startupEntries = await Task.Run(() => StartupService.Scan());
                var store = await Task.Run(() => UsageService.LoadStore());
                foreach (var e in _startupEntries)
                {
                    var item = new StartupItem(e);
                    if (!string.IsNullOrEmpty(e.ExePath) && store.Records.TryGetValue(e.ExePath, out var rec))
                        item.UsageText = "Average " + rec.AverageMemoryMb + " MB · CPU " + TimeText.Duration(rec.CpuMs / 1000);
                    _startup.Add(item);
                }
                var enabled = _startupEntries.Count(x => x.Enabled);
                var suggested = _startupEntries.Count(x => x.Enabled && x.Suggested);
                StartupSummary.Text = $"{enabled} items start with Windows" +
                                      (suggested > 0 ? $", {suggested} can safely be turned off." : ".") +
                                      " Here you also find services and tasks that the Windows panel doesn't show.";
                _ = Task.Run(() =>
                {
                    foreach (var it in _startup.ToList())
                    {
                        var icon = IconLoader.Load(new AppInfo { DisplayIcon = it.Entry.ExePath });
                        Dispatcher.Invoke(() => it.Icon = icon);
                    }
                });
            }
            catch (Exception ex) { Log.Write("Avvio: " + ex); }
            finally
            {
                StartupLoading.Visibility = Visibility.Collapsed;
                _startupLoaded = true;
            }
            LoadUsage();
        }

        private async void OnStartupToggle(object sender, RoutedEventArgs e)
        {
            if (!((sender as ToggleButton)?.Tag is StartupItem item)) return;
            var target = !item.Enabled;
            (sender as ToggleButton).IsEnabled = false;
            var err = await Task.Run(() => StartupService.SetEnabled(item.Entry, target));
            (sender as ToggleButton).IsEnabled = true;
            if (err == null)
            {
                item.Enabled = target;
                ShowToast(item.Name + (target ? " will start with Windows" : item.IsService ? " now starts only when needed" : " won't start with Windows anymore"));
            }
            else
            {
                (sender as ToggleButton).IsChecked = item.Enabled;
                ShowToast("Couldn't change it: " + err, ok: false);
            }
        }

        private async void OnStartupRefresh(object sender, RoutedEventArgs e)
        {
            _startupLoaded = false;
            await LoadStartupAsync();
        }

        private void OnStartupTab(object sender, RoutedEventArgs e)
        {
            if (StartupPanel == null) return;
            StartupPanel.Visibility = TabStartup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UsagePanel.Visibility = TabUsage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (TabUsage.IsChecked == true) LoadUsage();
        }

        private void LoadUsage()
        {
            MonitorSwitch.IsChecked = UsageService.IsMonitorEnabled();
            MonitorStatus.Text = (UsageService.IsMonitorRunning() ? "Running now. " : MonitorSwitch.IsChecked == true ? "It will start at the next sign-in. " : "Off. ") +
                                 "Every 30 seconds LindoMonitor checks which programs are open and how much they use. The data stays on your PC only.";
            var store = UsageService.LoadStore();
            var records = store.Records.Values.Where(r => r.Samples > 0).ToList();
            IEnumerable<UsageRecord> sorted =
                UsageByMemory.IsChecked == true ? records.OrderByDescending(r => r.AverageMemoryMb) :
                UsageByTime.IsChecked == true ? records.OrderByDescending(r => r.SecondsRunning) :
                records.OrderByDescending(r => r.CpuMs);
            var top = sorted.Take(60).ToList();
            double max = top.Count == 0 ? 1 :
                UsageByMemory.IsChecked == true ? top.Max(r => r.AverageMemoryMb) :
                UsageByTime.IsChecked == true ? top.Max(r => r.SecondsRunning) : top.Max(r => r.CpuMs);
            _usage.Clear();
            foreach (var r in top)
            {
                double v = UsageByMemory.IsChecked == true ? r.AverageMemoryMb : UsageByTime.IsChecked == true ? r.SecondsRunning : r.CpuMs;
                string name = r.Name;
                try
                {
                    var d = File.Exists(r.Path) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(r.Path).FileDescription : null;
                    if (!string.IsNullOrWhiteSpace(d)) name = d.Trim();
                }
                catch { }
                _usage.Add(new UsageRow { Record = r, Name = name, Ratio = max > 0 ? Math.Max(0.02, v / max) : 0 });
            }
            UsageEmpty.Visibility = _usage.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _ = Task.Run(() =>
            {
                foreach (var row in _usage.ToList())
                {
                    var icon = IconLoader.Load(new AppInfo { DisplayIcon = row.Path });
                    Dispatcher.Invoke(() => row.Icon = icon);
                }
            });
        }

        private void OnUsageSort(object sender, RoutedEventArgs e)
        {
            if (UsageList != null) LoadUsage();
        }

        private void OnMonitorToggle(object sender, RoutedEventArgs e)
        {
            var on = MonitorSwitch.IsChecked == true;
            var err = UsageService.SetMonitor(on);
            if (err != null)
            {
                MonitorSwitch.IsChecked = !on;
                ShowToast(err, ok: false);
                return;
            }
            ShowToast(on ? "Monitoring on: it also starts at every sign-in" : "Monitoring off");
            LoadUsage();
        }

        private void OnShowUnused(object sender, RoutedEventArgs e)
        {
            NavApps.IsChecked = true;
            FilterUnused.IsChecked = true;
        }

        // ================================================================== Ottimizza

        private async Task LoadOptimizeAsync()
        {
            OptimizeScore.Text = "Scanning…";
            _optimize.Clear();
            var actions = OptimizeService.Actions(() => _startupEntries.Count > 0 ? _startupEntries : (_startupEntries = StartupService.Scan()));
            foreach (var a in actions) _optimize.Add(new OptimizeItem(a));
            await RefreshOptimizeStateAsync();
            _optimizeLoaded = true;
            _ = RefreshResourcesAsync();
        }

        // ----------------------------------------------------------------- risorse (CPU, RAM, GPU)

        private sealed class UsageLine { public string Name { get; set; } public string Value { get; set; } }

        private GpuInfo _gpu;

        private static string Pretty(ProcessUsage u) => u.Count > 1 ? $"{u.Name} ({u.Count})" : u.Name;

        private async Task RefreshResourcesAsync()
        {
            RefreshResourcesButton.IsEnabled = false;
            try
            {
                // Prima la scheda video (avvia PowerShell), poi la misura: così Lindo non "sporca" i numeri con il proprio lavoro
                if (_gpu == null) { _gpu = await Task.Run(() => ResourceService.ReadGpu()); ShowGpu(); }
                await Task.Delay(400);
                var snap = await Task.Run(() => ResourceService.Measure());

                CpuValue.Text = Math.Round(snap.CpuPercent) + "%";
                CpuBar.Value = snap.CpuPercent;
                CpuTop.ItemsSource = snap.TopCpu.Count == 0
                    ? new List<UsageLine> { new UsageLine { Name = "The processor is idle", Value = "" } }
                    : snap.TopCpu.Select(u => new UsageLine { Name = Pretty(u), Value = u.CpuPercent.ToString("0.0") + "%" }).ToList();

                if (snap.RamTotal > 0)
                {
                    var used = snap.RamTotal - snap.RamAvailable;
                    RamValue.Text = snap.MemoryLoad + "%";
                    RamBar.Value = snap.MemoryLoad;
                    RamDetail.Text = $"{SizeFormat.Format(used)} used of {SizeFormat.Format(snap.RamTotal)} · {SizeFormat.Format(snap.RamAvailable)} free";
                }
                RamTop.ItemsSource = snap.TopRam.Select(u => new UsageLine { Name = Pretty(u), Value = SizeFormat.Format(u.MemoryBytes) }).ToList();

                ShowGpu();
            }
            catch (Exception ex) { Log.Write("Risorse: " + ex.Message); }
            finally { RefreshResourcesButton.IsEnabled = true; }
        }

        private void ShowGpu()
        {
            if (_gpu == null) return;
            GpuNames.Text = _gpu.Names.Count > 0 ? string.Join("\n", _gpu.Names)
                : _gpu.AllNames.Count > 0 ? _gpu.AllNames[0] + " (virtual adapter)"
                : "Graphics card not recognized";
            GpuScheduling.Text = _gpu.HardwareScheduling == true
                ? "Hardware-accelerated GPU scheduling: on"
                : _gpu.HardwareScheduling == false
                    ? "Hardware-accelerated GPU scheduling: off. You can try it from «Graphics settings» (needs a restart; it helps in some games, not in others)."
                    : "Hardware-accelerated GPU scheduling: not available or never set on this PC.";
            GpuGameButton.Visibility = _gpu.HasTwoGpus ? Visibility.Visible : Visibility.Collapsed;
            var games = ResourceService.LindoGpuPreferences();
            GpuGames.Text = !_gpu.HasTwoGpus
                ? "You have one graphics card: games already use it."
                : games.Count == 0 ? "You have two graphics cards: pick a game to run on the more powerful one."
                : "On the dedicated card: " + string.Join(", ", games.Select(System.IO.Path.GetFileNameWithoutExtension));
        }

        private async void OnRefreshResources(object sender, RoutedEventArgs e) => await RefreshResourcesAsync();

        private void OnOpenGraphicsSettings(object sender, RoutedEventArgs e) => ResourceService.OpenGraphicsSettings();

        private void OnGameOnDedicatedGpu(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick the game (.exe file) to run on the dedicated graphics card",
                Filter = "Programs (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            var err = ResourceService.SetHighPerformanceGpu(dlg.FileName);
            if (err != null) { ShowToast("Failed: " + err, ok: false); return; }
            ShowToast(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) + " will use the dedicated graphics card (from the next time the game starts)");
            RestoreButton.IsEnabled = true;
            ShowGpu();
        }

        private async Task RefreshOptimizeStateAsync()
        {
            var states = await Task.Run(() => _optimize.Select(o =>
            {
                try { return o.Action.IsApplied(); } catch { return false; }
            }).ToList());
            for (int i = 0; i < _optimize.Count; i++)
            {
                _optimize[i].IsApplied = states[i];
                if (states[i] && _optimize[i].Reversible) _optimize[i].IsSelected = false;
            }
            var reversible = _optimize.Where(o => o.Reversible).ToList();
            var done = reversible.Count(o => o.IsApplied);
            OptimizeScore.Text = done == reversible.Count ? "Optimized ✓" : $"{done} of {reversible.Count} optimizations on";
            OptimizeStatus.Text = done == reversible.Count
                ? "Your PC is already set up for the best performance. You can still clean temporary files."
                : "Select what you want to improve and press «Optimize now». Some changes apply from the next sign-in.";
            RestoreButton.IsEnabled = _optimize.Any(o => o.Reversible && OptimizeService.HasBackup(o.Action.Id)) ||
                                      ResourceService.LindoGpuPreferences().Count > 0;
        }

        private async void OnOptimizeClick(object sender, RoutedEventArgs e)
        {
            var selected = _optimize.Where(o => o.IsSelected).ToList();
            if (selected.Count == 0) { ShowToast("Select at least one optimization", ok: false); return; }
            OptimizeButton.IsEnabled = false;
            var (freeBefore, _) = SystemInfo.DiskSpace();
            var errors = new List<string>();
            foreach (var o in selected)
            {
                OptimizeStatus.Text = "Applying: " + o.Title + "…";
                var err = await Task.Run(() => { try { return o.Action.Apply(); } catch (Exception ex) { return ex.Message; } });
                if (err != null) errors.Add(o.Title + ": " + err);
                Log.Write("Ottimizza " + o.Action.Id + " → " + (err ?? "ok"));
            }
            _startupEntries = new List<StartupEntry>();
            _startupLoaded = false;
            await RefreshOptimizeStateAsync();
            OptimizeButton.IsEnabled = true;
            UpdateDisk();
            var (freeAfter, _) = SystemInfo.DiskSpace();

            var checks = selected.Where(o => !errors.Any(x => x.StartsWith(o.Title))).Select(o => CheckLine.Ok(o.Title)).ToList();
            checks.AddRange(errors.Select(CheckLine.Fail));
            if (freeAfter > freeBefore) checks.Add(CheckLine.Info("Space freed: " + SizeFormat.Format(freeAfter - freeBefore)));
            checks.Add(CheckLine.Info("Restart the PC (or sign out and back in) to apply everything. You can undo it with «Restore all»."));
            ShowResult(errors.Count == 0 ? "PC optimized" : "Optimization done with some warnings", null, checks, success: errors.Count == 0);
        }

        private async void OnOptimizeRestore(object sender, RoutedEventArgs e)
        {
            RestoreButton.IsEnabled = false;
            foreach (var o in _optimize.Where(x => x.Reversible))
            {
                var err = await Task.Run(() => { try { return o.Action.Restore(); } catch (Exception ex) { return ex.Message; } });
                Log.Write("Ripristina " + o.Action.Id + " → " + (err ?? "ok"));
            }
            await Task.Run(() => ResourceService.RestoreGpuPreferences());
            ShowGpu();
            _startupEntries = new List<StartupEntry>();
            _startupLoaded = false;
            foreach (var o in _optimize) o.IsSelected = o.Action.DefaultSelected;
            await RefreshOptimizeStateAsync();
            ShowToast("Original settings restored");
        }

        // ================================================================== Errori e crash

        private List<CrashGroup> _crashGroups = new List<CrashGroup>();

        private async Task LoadCrashesAsync()
        {
            CrashLoading.Visibility = Visibility.Visible;
            CrashEmpty.Visibility = Visibility.Collapsed;
            _crashes.Clear();
            try
            {
                _crashGroups = await Task.Run(() => CrashService.Scan(30));
                foreach (var g in _crashGroups) _crashes.Add(new CrashItem(g));
                CrashStatApps.Text = _crashGroups.Where(g => g.Kind == "App").Sum(g => g.Count).ToString();
                CrashStatHangs.Text = _crashGroups.Where(g => g.Kind == "Hang").Sum(g => g.Count).ToString();
                CrashStatSystem.Text = _crashGroups.Where(g => g.IsSystem).Sum(g => g.Count).ToString();
            }
            catch (Exception ex) { Log.Write("Crash: " + ex); }
            finally
            {
                CrashLoading.Visibility = Visibility.Collapsed;
                CrashEmpty.Visibility = _crashes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _crashesLoaded = true;
            }
        }

        private async void OnCrashRefresh(object sender, RoutedEventArgs e) => await LoadCrashesAsync();

        private void OnCrashSave(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = CrashService.SaveReport(_crashGroups);
                ShowToast("Report saved");
                OpenPath(path, select: true);
            }
            catch (Exception ex) { ShowToast("Couldn't save: " + ex.Message, ok: false); }
        }
    }
}
