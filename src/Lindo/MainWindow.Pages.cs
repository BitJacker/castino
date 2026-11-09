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
                        item.UsageText = "Media " + rec.AverageMemoryMb + " MB · CPU " + TimeText.Duration(rec.CpuMs / 1000);
                    _startup.Add(item);
                }
                var enabled = _startupEntries.Count(x => x.Enabled);
                var suggested = _startupEntries.Count(x => x.Enabled && x.Suggested);
                StartupSummary.Text = $"{enabled} elementi partono con Windows" +
                                      (suggested > 0 ? $", {suggested} si possono togliere senza problemi." : ".") +
                                      " Qui trovi anche servizi e attività che il pannello di Windows non mostra.";
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
                ShowToast(item.Name + (target ? " partirà con Windows" : item.IsService ? " ora parte solo quando serve" : " non partirà più con Windows"));
            }
            else
            {
                (sender as ToggleButton).IsChecked = item.Enabled;
                ShowToast("Impossibile modificare: " + err, ok: false);
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
            MonitorStatus.Text = (UsageService.IsMonitorRunning() ? "Attivo adesso. " : MonitorSwitch.IsChecked == true ? "Si avvierà al prossimo accesso. " : "Disattivato. ") +
                                 "Ogni 30 secondi LindoMonitor guarda quali programmi sono aperti e quanto consumano. I dati restano solo sul tuo PC.";
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
            ShowToast(on ? "Monitoraggio attivato: parte anche a ogni accesso" : "Monitoraggio disattivato");
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
            OptimizeScore.Text = "Analisi…";
            _optimize.Clear();
            var actions = OptimizeService.Actions(() => _startupEntries.Count > 0 ? _startupEntries : (_startupEntries = StartupService.Scan()));
            foreach (var a in actions) _optimize.Add(new OptimizeItem(a));
            await RefreshOptimizeStateAsync();
            _optimizeLoaded = true;
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
            OptimizeScore.Text = done == reversible.Count ? "Ottimizzato ✓" : $"{done} di {reversible.Count} ottimizzazioni attive";
            OptimizeStatus.Text = done == reversible.Count
                ? "Il PC è già configurato al meglio. Puoi comunque ripulire i file temporanei."
                : "Seleziona cosa vuoi migliorare e premi «Ottimizza ora». Alcune modifiche valgono dal prossimo accesso.";
            RestoreButton.IsEnabled = _optimize.Any(o => o.Reversible && OptimizeService.HasBackup(o.Action.Id));
        }

        private async void OnOptimizeClick(object sender, RoutedEventArgs e)
        {
            var selected = _optimize.Where(o => o.IsSelected).ToList();
            if (selected.Count == 0) { ShowToast("Seleziona almeno un'ottimizzazione", ok: false); return; }
            OptimizeButton.IsEnabled = false;
            var (freeBefore, _) = SystemInfo.DiskSpace();
            var errors = new List<string>();
            foreach (var o in selected)
            {
                OptimizeStatus.Text = "Applico: " + o.Title + "…";
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
            if (freeAfter > freeBefore) checks.Add(CheckLine.Info("Spazio liberato: " + SizeFormat.Format(freeAfter - freeBefore)));
            checks.Add(CheckLine.Info("Riavvia il PC (o esci e rientra) per applicare tutto. Puoi annullare con «Ripristina tutto»."));
            ShowResult(errors.Count == 0 ? "PC ottimizzato" : "Ottimizzazione completata con qualche avviso", null, checks, success: errors.Count == 0);
        }

        private async void OnOptimizeRestore(object sender, RoutedEventArgs e)
        {
            RestoreButton.IsEnabled = false;
            foreach (var o in _optimize.Where(x => x.Reversible))
            {
                var err = await Task.Run(() => { try { return o.Action.Restore(); } catch (Exception ex) { return ex.Message; } });
                Log.Write("Ripristina " + o.Action.Id + " → " + (err ?? "ok"));
            }
            _startupEntries = new List<StartupEntry>();
            _startupLoaded = false;
            foreach (var o in _optimize) o.IsSelected = o.Action.DefaultSelected;
            await RefreshOptimizeStateAsync();
            ShowToast("Impostazioni originali ripristinate");
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
                CrashStatHangs.Text = _crashGroups.Where(g => g.Kind == "Blocco").Sum(g => g.Count).ToString();
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
                ShowToast("Rapporto salvato");
                OpenPath(path, select: true);
            }
            catch (Exception ex) { ShowToast("Impossibile salvare: " + ex.Message, ok: false); }
        }
    }
}
