using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Lindo.Core;
using Lindo.Services;

namespace Lindo
{
    public sealed class CheckLine
    {
        public string Glyph { get; set; }
        public Brush Brush { get; set; }
        public string Text { get; set; }

        public static CheckLine Ok(string t) => new CheckLine { Glyph = "", Brush = (Brush)Application.Current.FindResource("Success"), Text = t };
        public static CheckLine Warn(string t) => new CheckLine { Glyph = "", Brush = (Brush)Application.Current.FindResource("Warning"), Text = t };
        public static CheckLine Fail(string t) => new CheckLine { Glyph = "", Brush = (Brush)Application.Current.FindResource("Danger"), Text = t };
        public static CheckLine Info(string t) => new CheckLine { Glyph = "", Brush = (Brush)Application.Current.FindResource("Subtle"), Text = t };
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<AppItem> _apps = new ObservableCollection<AppItem>();
        private readonly ObservableCollection<LeftoverItem> _orphans = new ObservableCollection<LeftoverItem>();
        private readonly ObservableCollection<LeftoverItem> _dlgLeftovers = new ObservableCollection<LeftoverItem>();
        private readonly ObservableCollection<CleanTargetItem> _cleanTargets = new ObservableCollection<CleanTargetItem>();
        private ListCollectionView _view;
        private List<AppInfo> _allInfos = new List<AppInfo>();
        private CancellationTokenSource _sizeCts;
        private CancellationTokenSource _skipCts;
        private AppItem _current;
        private bool _loadingApps;
        private bool _cleanInitialized;
        private Action _resultPrimary;
        private Action _resultSecondary;

        /// <summary>Se impostato, l'app salva alcune schermate in questa cartella e si chiude (usato per i test automatici).</summary>
        public string ScreenshotFolder { get; set; }

        public MainWindow()
        {
            InitializeComponent();

            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_apps);
            _view.Filter = FilterApp;
            _view.IsLiveSorting = true;
            _view.LiveSortingProperties.Add(nameof(AppItem.SizeBytes));
            ApplySort();
            AppsList.ItemsSource = _view;
            OrphansList.ItemsSource = _orphans;
            DlgLeftoversList.ItemsSource = _dlgLeftovers;
            CleanList.ItemsSource = _cleanTargets;

            SourceInitialized += (s, e) => ApplyWindowEffects();
            StateChanged += (s, e) => OnWindowStateChanged();
            Loaded += async (s, e) =>
            {
                foreach (var spinner in new[] { LoadingSpinner, OrphansSpinner, DlgSpinner }) Spin(spinner);
                UpdateDisk();
                await LoadAppsAsync();
                if (!string.IsNullOrEmpty(ScreenshotFolder)) await TakeScreenshotsAsync();
            };
        }

        // ================================================================== finestra

        private void ApplyWindowEffects()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1;
                Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // barra scura
                int round = 2;
                Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // angoli arrotondati (Windows 11)
            }
            catch { /* Windows 10: nessun effetto */ }
        }

        private void OnWindowStateChanged()
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void OnMaximize(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private static void Spin(FrameworkElement el)
        {
            if (!(el.RenderTransform is RotateTransform rt)) return;
            rt.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void UpdateDisk()
        {
            var (free, total) = SystemInfo.DiskSpace();
            DiskTitle.Text = "Disco " + SystemInfo.SystemDrive.TrimEnd('\\');
            DiskFree.Text = SizeFormat.Format(free);
            DiskDetail.Text = total > 0 ? "liberi su " + SizeFormat.Format(total) + " totali" : "";
            var used = total > 0 ? 1.0 - (double)free / total : 0;
            DiskBar.Width = Math.Max(4, 184 * used);
            // Disco quasi pieno: barra rossa
            DiskBar.Background = total > 0 && (double)free / total < 0.08
                ? (Brush)FindResource("DangerGradient")
                : (Brush)FindResource("AccentHorizontal");
        }

        private void OnNavChanged(object sender, RoutedEventArgs e)
        {
            if (PageApps == null) return;
            PageApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLeftovers.Visibility = NavLeftovers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageClean.Visibility = NavClean.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavClean.IsChecked == true && !_cleanInitialized) _ = InitCleanAsync();
        }

        // ================================================================== elenco app

        private async Task<List<AppInfo>> ScanAllAsync()
        {
            var desktop = Task.Run(() => AppScanner.ScanDesktop());
            var store = Task.Run(() => AppScanner.ScanStore());
            await Task.WhenAll(desktop, store);
            return desktop.Result.Concat(store.Result).ToList();
        }

        private async Task LoadAppsAsync()
        {
            if (_loadingApps) return;
            _loadingApps = true;
            _sizeCts?.Cancel();
            AppsLoading.Visibility = Visibility.Visible;
            AppsEmpty.Visibility = Visibility.Collapsed;
            AppsSummary.Text = "Analisi in corso…";
            try
            {
                _allInfos = await ScanAllAsync();
                _apps.Clear();
                foreach (var info in _allInfos) _apps.Add(new AppItem(info));
                UpdateStats();
                AppsLoading.Visibility = Visibility.Collapsed;
                UpdateEmptyState();

                _sizeCts = new CancellationTokenSource();
                var ct = _sizeCts.Token;
                var items = _apps.ToList();
                await Task.WhenAll(LoadIconsAsync(items, ct), MeasureSizesAsync(items, ct));
            }
            catch (Exception ex)
            {
                Log.Write("Errore caricamento app: " + ex);
                AppsSummary.Text = "Impossibile leggere l'elenco delle app: " + ex.Message;
                AppsLoading.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loadingApps = false;
            }
        }

        private async Task LoadIconsAsync(List<AppItem> items, CancellationToken ct)
        {
            foreach (var chunk in items.Select((x, i) => (x, i)).GroupBy(t => t.i / 12))
            {
                if (ct.IsCancellationRequested) return;
                var batch = chunk.Select(t => t.x).ToList();
                var icons = await Task.Run(() => batch.Select(a => IconLoader.Load(a.Info)).ToList());
                for (int i = 0; i < batch.Count; i++) batch[i].Icon = icons[i];
            }
        }

        private async Task MeasureSizesAsync(List<AppItem> items, CancellationToken ct)
        {
            var guard = new PathGuard(SystemInfo.Roots);
            var gate = new SemaphoreSlim(3);
            var local = SystemInfo.Roots.LocalAppData;
            var lastUpdate = DateTime.MinValue;

            var tasks = items.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(true);
                try
                {
                    var info = item.Info;
                    var size = await Task.Run(() =>
                    {
                        long total = 0;
                        var loc = info.InstallLocation;
                        bool shared = _allInfos.Any(o => !ReferenceEquals(o, info) && !string.IsNullOrWhiteSpace(o.InstallLocation)
                                                         && WinPath.IsStrictlyInside(o.InstallLocation, loc));
                        if (!string.IsNullOrWhiteSpace(loc) && !shared &&
                            (info.Source == AppSource.Store || guard.CanDelete(loc)) && Directory.Exists(loc))
                            total += WindowsFileSystem.DirectorySize(loc, ct);
                        if (info.Source == AppSource.Store && !string.IsNullOrEmpty(info.PackageFamilyName))
                        {
                            var data = Path.Combine(local, "Packages", info.PackageFamilyName);
                            if (Directory.Exists(data)) total += WindowsFileSystem.DirectorySize(data, ct);
                        }
                        return total;
                    }, ct).ConfigureAwait(true);

                    if (size > 0)
                    {
                        item.SizeBytes = size;
                        item.SizeIsExact = true;
                    }
                    if (DateTime.Now - lastUpdate > TimeSpan.FromMilliseconds(400))
                    {
                        lastUpdate = DateTime.Now;
                        UpdateStats();
                    }
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            }).ToList();

            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            if (!ct.IsCancellationRequested) UpdateStats();
        }

        private void UpdateStats()
        {
            var max = _apps.Count == 0 ? 0 : _apps.Max(a => a.SizeBytes);
            foreach (var a in _apps) a.SizeRatio = max > 0 ? Math.Max(0.015, (double)a.SizeBytes / max) : 0;
            var total = _apps.Sum(a => a.SizeBytes);
            StatCount.Text = _apps.Count.ToString();
            StatSize.Text = SizeFormat.Format(total);
            var biggest = _apps.OrderByDescending(a => a.SizeBytes).FirstOrDefault();
            StatBiggest.Text = biggest?.Name ?? "—";
            StatBiggestSize.Text = biggest != null ? SizeFormat.Format(biggest.SizeBytes) : "";
            var store = _apps.Count(a => a.IsStore);
            AppsSummary.Text = $"{_apps.Count - store} programmi e {store} app dello Store · scegli cosa togliere e Lindo pulisce anche i residui";
        }

        private bool FilterApp(object o)
        {
            if (!(o is AppItem a)) return false;
            if (FilterDesktop?.IsChecked == true && a.IsStore) return false;
            if (FilterStore?.IsChecked == true && !a.IsStore) return false;
            var q = SearchBox?.Text?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(q) || a.SearchText.Contains(q);
        }

        private void ApplySort()
        {
            if (_view == null) return;
            using (_view.DeferRefresh())
            {
                _view.SortDescriptions.Clear();
                _view.LiveSortingProperties.Clear();
                switch (SortBox?.SelectedIndex ?? 0)
                {
                    case 1:
                        _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.Name), ListSortDirection.Ascending));
                        break;
                    case 2:
                        _view.CustomSort = null;
                        _view.SortDescriptions.Add(new SortDescription("Info.InstallDate", ListSortDirection.Descending));
                        break;
                    default:
                        _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.SizeBytes), ListSortDirection.Descending));
                        _view.LiveSortingProperties.Add(nameof(AppItem.SizeBytes));
                        break;
                }
            }
        }

        private void UpdateEmptyState()
        {
            AppsEmpty.Visibility = !_loadingApps || _apps.Count > 0
                ? (_view.Count == 0 ? Visibility.Visible : Visibility.Collapsed)
                : Visibility.Collapsed;
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) { _view?.Refresh(); UpdateEmptyState(); }
        private void OnFilterChanged(object sender, RoutedEventArgs e) { _view?.Refresh(); if (_view != null) UpdateEmptyState(); }
        private void OnSortChanged(object sender, SelectionChangedEventArgs e) => ApplySort();
        private async void OnRefreshClick(object sender, RoutedEventArgs e) { UpdateDisk(); await LoadAppsAsync(); }

        // ================================================================== dialogo

        private void ShowPanel(FrameworkElement panel)
        {
            foreach (var p in new FrameworkElement[] { DlgConfirm, DlgRunning, DlgLeftovers, DlgResult })
                p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;

            if (Overlay.Visibility != Visibility.Visible)
            {
                Overlay.Visibility = Visibility.Visible;
                Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                var st = (ScaleTransform)DialogCard.RenderTransform;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
        }

        private void CloseDialog()
        {
            _skipCts?.Cancel();
            Overlay.Visibility = Visibility.Collapsed;
            _current = null;
        }

        private void OnDialogCancel(object sender, RoutedEventArgs e) => CloseDialog();

        private void ShowResult(string title, string subtitle, IEnumerable<CheckLine> checks, bool success,
            string primaryText = "Fatto", Action primary = null, string secondaryText = null, Action secondary = null, bool danger = false)
        {
            DlgResultTitle.Text = title;
            DlgResultSubtitle.Text = subtitle ?? "";
            DlgResultSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            var list = checks?.ToList() ?? new List<CheckLine>();
            DlgChecks.ItemsSource = list;
            DlgChecksHost.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            DlgResultBadge.Background = (Brush)FindResource(success ? "SuccessSoft" : danger ? "DangerSoft" : "WarningSoft");
            DlgResultGlyph.Foreground = (Brush)FindResource(success ? "Success" : danger ? "Danger" : "Warning");
            DlgResultGlyph.Text = success ? "" : danger ? "" : "";

            DlgResultPrimary.Content = primaryText;
            DlgResultPrimary.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
            _resultPrimary = primary ?? CloseDialog;
            _resultSecondary = secondary;
            DlgResultSecondary.Content = secondaryText;
            DlgResultSecondary.Visibility = secondaryText != null ? Visibility.Visible : Visibility.Collapsed;
            ShowPanel(DlgResult);
        }

        private void OnResultPrimary(object sender, RoutedEventArgs e) => _resultPrimary?.Invoke();
        private void OnResultSecondary(object sender, RoutedEventArgs e) => (_resultSecondary ?? CloseDialog).Invoke();

        // ================================================================== disinstallazione

        private void OnUninstallClick(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is AppItem item)) return;
            _current = item;
            var info = item.Info;

            DlgAvatar.Background = item.AvatarBrush;
            DlgInitials.Text = item.Initials;
            DlgIcon.Source = item.Icon;
            DlgIconHost.Visibility = item.HasIcon ? Visibility.Visible : Visibility.Collapsed;
            DlgAppName.Text = info.Name;
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.Publisher)) meta.Add(info.Publisher);
            if (item.SizeBytes > 0) meta.Add((item.SizeIsExact ? "" : "circa ") + SizeFormat.Format(item.SizeBytes));
            DlgAppMeta.Text = string.Join("  ·  ", meta);

            var works = Uninstaller.HasWorkingUninstaller(info);
            DlgNoUninstaller.Visibility = works ? Visibility.Collapsed : Visibility.Visible;
            DlgConfirmText.Text = works ? "Disinstalla" : "Rimozione forzata";
            DlgStep1.Text = !works ? "Il disinstallatore manca: passo direttamente alla pulizia."
                         : info.Source == AppSource.Store ? "Rimuovo l'app tramite Windows (come dal menu Start)."
                         : "Avvio il programma di disinstallazione ufficiale dell'app.";
            ShowPanel(DlgConfirm);
        }

        private async void OnConfirmUninstall(object sender, RoutedEventArgs e)
        {
            var item = _current;
            if (item == null) return;
            var info = item.Info;

            if (!Uninstaller.HasWorkingUninstaller(info))
            {
                await ScanAppLeftoversAsync(item, forced: true);
                return;
            }

            DlgRunningTitle.Text = "Disinstallazione di " + info.Name;
            DlgRunningText.Text = "Avvio…";
            DlgSkipWait.Visibility = Visibility.Visible;
            ShowPanel(DlgRunning);

            _skipCts = new CancellationTokenSource();
            var progress = new Progress<string>(t => DlgRunningText.Text = t);
            UninstallOutcome outcome;
            try { outcome = await Uninstaller.RunAsync(info, _skipCts.Token, progress); }
            catch (Exception ex)
            {
                Log.Write("Disinstallazione fallita: " + ex);
                outcome = UninstallOutcome.NoUninstaller;
            }
            if (_current != item) return; // dialogo chiuso nel frattempo

            switch (outcome)
            {
                case UninstallOutcome.Completed:
                case UninstallOutcome.Skipped:
                    await ScanAppLeftoversAsync(item, forced: false);
                    break;
                case UninstallOutcome.StillInstalled:
                    ShowResult("La disinstallazione non è stata completata",
                        "Il programma risulta ancora installato. Forse hai annullato la procedura, oppure serve un riavvio.",
                        new[] { CheckLine.Info("Puoi riprovare, oppure fare una rimozione forzata: Lindo elimina direttamente i file e la voce dall'elenco.") },
                        success: false, primaryText: "Chiudi", primary: CloseDialog,
                        secondaryText: "Rimozione forzata", secondary: async () => await ScanAppLeftoversAsync(item, forced: true));
                    break;
                default:
                    ShowResult("Impossibile avviare il disinstallatore",
                        "Il programma di disinstallazione non si è avviato.",
                        new[] { CheckLine.Info("Puoi fare una rimozione forzata: Lindo elimina direttamente i file rimasti e la voce dall'elenco.") },
                        success: false, primaryText: "Rimozione forzata", primary: async () => await ScanAppLeftoversAsync(item, forced: true),
                        secondaryText: "Annulla", secondary: CloseDialog);
                    break;
            }
        }

        private void OnSkipWait(object sender, RoutedEventArgs e)
        {
            _skipCts?.Cancel();
            DlgSkipWait.Visibility = Visibility.Collapsed;
        }

        private LeftoverPlanner CreatePlanner() =>
            new LeftoverPlanner(SystemInfo.Roots, new WindowsFileSystem(), new WindowsRegistry())
            {
                ShortcutTargetResolver = ShortcutResolver.Resolve,
            };

        private static void FillSizes(IEnumerable<Leftover> items)
        {
            foreach (var l in items)
            {
                if (l.Kind == LeftoverKind.RegistryKey) continue;
                if (Directory.Exists(l.Path)) l.SizeBytes = WindowsFileSystem.DirectorySize(l.Path);
                else if (File.Exists(l.Path)) { try { l.SizeBytes = new FileInfo(l.Path).Length; } catch { } }
                l.LastModified = WindowsFileSystem.LastWrite(l.Path);
            }
        }

        private bool _wasForced;

        private async Task ScanAppLeftoversAsync(AppItem item, bool forced)
        {
            _current = item;
            _wasForced = forced;
            DlgRunningTitle.Text = "Cerco i residui di " + item.Info.Name;
            DlgRunningText.Text = "Controllo Programmi, AppData, ProgramData, menu Start, Desktop e registro…";
            DlgSkipWait.Visibility = Visibility.Collapsed;
            ShowPanel(DlgRunning);

            List<Leftover> found;
            bool stillInstalled;
            try
            {
                var all = await ScanAllAsync();
                stillInstalled = all.Any(a => a.Id == item.Info.Id);
                found = await Task.Run(() =>
                {
                    var res = CreatePlanner().FindLeftovers(item.Info, all);
                    FillSizes(res);
                    return res;
                });
            }
            catch (Exception ex)
            {
                Log.Write("Ricerca residui fallita: " + ex);
                ShowResult("Ricerca dei residui non riuscita", ex.Message, null, success: false);
                return;
            }
            if (_current != item) return;

            _dlgLeftovers.Clear();
            foreach (var l in found)
            {
                var vm = new LeftoverItem(l);
                vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LeftoverItem.IsSelected)) UpdateDlgSelection(); };
                _dlgLeftovers.Add(vm);
            }

            if (_dlgLeftovers.Count == 0)
            {
                _ = LoadAppsAsync();
                UpdateDisk();
                ShowResult(stillInstalled ? "Nessun residuo trovato" : "Disinstallazione completata",
                    stillInstalled ? "Il programma risulta però ancora installato." : item.Info.Name + " è stato rimosso completamente.",
                    new[]
                    {
                        stillInstalled ? CheckLine.Warn("Il programma è ancora nell'elenco delle app") : CheckLine.Ok("Programma rimosso dall'elenco delle app"),
                        CheckLine.Ok("Nessuna cartella rimasta in Programmi, AppData e ProgramData"),
                        CheckLine.Ok("Nessun collegamento nel menu Start o sul Desktop"),
                        CheckLine.Ok("Nessuna chiave di registro rimasta"),
                    }, success: !stillInstalled);
                return;
            }

            var total = found.Sum(f => f.SizeBytes);
            DlgLeftoversTitle.Text = forced ? "Rimozione forzata di " + item.Info.Name : "Ecco cosa è rimasto di " + item.Info.Name;
            DlgLeftoversText.Text = $"{found.Count} elementi · {SizeFormat.Format(total)}. " +
                                    "Gli elementi «Sicuro» e «Probabile» sono già selezionati; quelli «Da verificare» decidili tu.";
            UpdateDlgSelection();
            ShowPanel(DlgLeftovers);
        }

        private void UpdateDlgSelection()
        {
            var sel = _dlgLeftovers.Where(l => l.IsSelected).ToList();
            DlgDeleteButton.IsEnabled = sel.Count > 0;
            DlgDeleteText.Text = sel.Count == 0 ? "Elimina selezionati"
                : $"Elimina {sel.Count} " + (sel.Count == 1 ? "elemento" : "elementi") +
                  (sel.Sum(l => l.SizeBytes) > 0 ? " · " + SizeFormat.Format(sel.Sum(l => l.SizeBytes)) : "");
            DlgSelectAll.IsChecked = sel.Count == _dlgLeftovers.Count && sel.Count > 0;
        }

        private void OnDlgSelectAll(object sender, RoutedEventArgs e)
        {
            var on = DlgSelectAll.IsChecked == true;
            foreach (var l in _dlgLeftovers) l.IsSelected = on;
        }

        private void OnSkipLeftovers(object sender, RoutedEventArgs e)
        {
            var item = _current;
            _ = LoadAppsAsync();
            UpdateDisk();
            ShowResult("Fatto", item != null ? "Non ho eliminato nessun residuo di " + item.Info.Name + "." : null,
                new[] { CheckLine.Info("Potrai ritrovarli in qualsiasi momento in «Residui e verifica».") }, success: true);
        }

        private async void OnDeleteLeftovers(object sender, RoutedEventArgs e)
        {
            var item = _current;
            var selected = _dlgLeftovers.Where(l => l.IsSelected).Select(l => l.Model).ToList();
            if (item == null || selected.Count == 0) return;

            DlgRunningTitle.Text = "Pulizia in corso";
            DlgRunningText.Text = "Elimino i residui selezionati…";
            DlgSkipWait.Visibility = Visibility.Collapsed;
            ShowPanel(DlgRunning);

            var progress = new Progress<string>(t => DlgRunningText.Text = t);
            var result = await Task.Run(() => Cleaner.Delete(selected, progress));

            // Verifica finale: ricontrolla il sistema
            var all = await ScanAllAsync();
            var stillInstalled = all.Any(a => a.Id == item.Info.Id);
            var remaining = await Task.Run(() => CreatePlanner().FindLeftovers(item.Info, all));
            var remainingSelected = remaining.Count(r => selected.Any(s => string.Equals(s.Path, r.Path, StringComparison.OrdinalIgnoreCase)));

            var checks = new List<CheckLine>();
            checks.Add(stillInstalled ? CheckLine.Warn("Il programma risulta ancora nell'elenco delle app")
                                      : CheckLine.Ok("Programma rimosso dall'elenco delle app"));
            checks.Add(CheckLine.Ok($"{result.Deleted} {(result.Deleted == 1 ? "elemento eliminato" : "elementi eliminati")}"));
            if (result.ScheduledOnReboot > 0)
                checks.Add(CheckLine.Warn($"{result.ScheduledOnReboot} file erano in uso: verranno eliminati al prossimo riavvio"));
            foreach (var f in result.Failed.Take(3)) checks.Add(CheckLine.Fail("Non eliminato: " + f));
            if (result.Failed.Count > 3) checks.Add(CheckLine.Fail($"…e altri {result.Failed.Count - 3}"));
            if (!string.IsNullOrEmpty(result.BackupFolder))
                checks.Add(CheckLine.Info("Copia di sicurezza del registro salvata in " + result.BackupFolder));
            if (remainingSelected - result.ScheduledOnReboot > 0 && result.ScheduledOnReboot == 0)
                checks.Add(CheckLine.Warn("Verifica: alcuni elementi selezionati sono ancora presenti"));
            else if (remaining.Count == 0)
                checks.Add(CheckLine.Ok("Verifica finale: non è rimasta nessuna traccia"));
            else
                checks.Add(CheckLine.Info($"Verifica finale: restano {remaining.Count} elementi che hai scelto di tenere"));

            UpdateDisk();
            _ = LoadAppsAsync();
            ShowResult(result.FreedBytes > 0 ? "Liberati " + SizeFormat.Format(result.FreedBytes) : "Pulizia completata",
                item.Info.Name + (stillInstalled ? " è stato ripulito." : " è stato rimosso completamente."),
                checks, success: result.Failed.Count == 0,
                secondaryText: "Mostra registro attività", secondary: () => OpenPath(Log.FilePath));
        }

        // ================================================================== residui globali

        private async void OnScanOrphansClick(object sender, RoutedEventArgs e) => await ScanOrphansAsync();

        private async Task ScanOrphansAsync()
        {
            ScanOrphansButton.IsEnabled = false;
            OrphansIntro.Visibility = Visibility.Collapsed;
            OrphansClean.Visibility = Visibility.Collapsed;
            OrphansActionBar.Visibility = Visibility.Collapsed;
            OrphanWarning.Visibility = Visibility.Collapsed;
            OrphansLoading.Visibility = Visibility.Visible;
            OrphansLoadingText.Text = "Leggo le app installate…";
            _orphans.Clear();
            try
            {
                var all = await ScanAllAsync();
                OrphansLoadingText.Text = "Confronto le cartelle con le app installate…";
                var found = await Task.Run(() =>
                {
                    var list = CreatePlanner().FindOrphans(all);
                    // Voci "fantasma": app nell'elenco il cui disinstallatore e la cui cartella non esistono più
                    foreach (var a in all.Where(x => x.Source == AppSource.Desktop))
                    {
                        if (Uninstaller.HasWorkingUninstaller(a)) continue;
                        if (!string.IsNullOrWhiteSpace(a.InstallLocation) && Directory.Exists(a.InstallLocation)) continue;
                        if (!RegistryGuard.CanDelete(a.RegistryKey)) continue;
                        list.Add(new Leftover
                        {
                            Kind = LeftoverKind.RegistryKey,
                            Path = a.RegistryKey,
                            Confidence = Confidence.Medium,
                            Reason = "Voce fantasma: «" + a.Name + "» non è più installato",
                        });
                    }
                    FillSizes(list);
                    return list.OrderByDescending(l => l.Kind == LeftoverKind.RegistryKey ? -1 : l.SizeBytes).ToList();
                });

                foreach (var l in found)
                {
                    var vm = new LeftoverItem(l) { IsSelected = false };
                    vm.PropertyChanged += (s, ev) => { if (ev.PropertyName == nameof(LeftoverItem.IsSelected)) UpdateOrphanSelection(); };
                    _orphans.Add(vm);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Analisi residui fallita: " + ex);
            }
            finally
            {
                OrphansLoading.Visibility = Visibility.Collapsed;
                ScanOrphansButton.IsEnabled = true;
                ScanOrphansText.Text = "Analizza di nuovo";
            }

            var any = _orphans.Count > 0;
            OrphansClean.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            OrphansActionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            OrphanWarning.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            UpdateOrphanSelection();
        }

        private void UpdateOrphanSelection()
        {
            var sel = _orphans.Where(o => o.IsSelected).ToList();
            var total = _orphans.Sum(o => o.SizeBytes);
            OrphansSelection.Text = $"{sel.Count} di {_orphans.Count} selezionati · {SizeFormat.Format(sel.Sum(o => o.SizeBytes))} su {SizeFormat.Format(total)}";
            DeleteOrphansButton.IsEnabled = sel.Count > 0;
            OrphansSelectAll.IsChecked = sel.Count > 0 && sel.Count == _orphans.Count;
        }

        private void OnOrphansSelectAll(object sender, RoutedEventArgs e)
        {
            var on = OrphansSelectAll.IsChecked == true;
            foreach (var o in _orphans) o.IsSelected = on;
        }

        private void OnDeleteOrphansClick(object sender, RoutedEventArgs e)
        {
            var selected = _orphans.Where(o => o.IsSelected).Select(o => o.Model).ToList();
            if (selected.Count == 0) return;
            var lines = selected.Take(6).Select(s => CheckLine.Info(s.Path)).ToList();
            if (selected.Count > 6) lines.Add(CheckLine.Info($"…e altri {selected.Count - 6}"));
            ShowResult($"Eliminare {selected.Count} {(selected.Count == 1 ? "elemento" : "elementi")}?",
                "L'operazione non si può annullare (per il registro viene salvata una copia di sicurezza).",
                lines, success: false, danger: true,
                primaryText: "Elimina", primary: async () => await DeleteOrphansAsync(selected),
                secondaryText: "Annulla", secondary: CloseDialog);
        }

        private async Task DeleteOrphansAsync(List<Leftover> selected)
        {
            DlgRunningTitle.Text = "Pulizia in corso";
            DlgRunningText.Text = "Elimino gli elementi selezionati…";
            DlgSkipWait.Visibility = Visibility.Collapsed;
            ShowPanel(DlgRunning);
            var progress = new Progress<string>(t => DlgRunningText.Text = t);
            var result = await Task.Run(() => Cleaner.Delete(selected, progress));

            var checks = new List<CheckLine> { CheckLine.Ok($"{result.Deleted} elementi eliminati") };
            if (result.ScheduledOnReboot > 0) checks.Add(CheckLine.Warn($"{result.ScheduledOnReboot} file in uso verranno eliminati al riavvio"));
            foreach (var f in result.Failed.Take(3)) checks.Add(CheckLine.Fail("Non eliminato: " + f));
            if (!string.IsNullOrEmpty(result.BackupFolder)) checks.Add(CheckLine.Info("Copia di sicurezza del registro in " + result.BackupFolder));

            UpdateDisk();
            ShowResult(result.FreedBytes > 0 ? "Liberati " + SizeFormat.Format(result.FreedBytes) : "Pulizia completata", null,
                checks, success: result.Failed.Count == 0, primary: async () => { CloseDialog(); await ScanOrphansAsync(); });
            _ = LoadAppsAsync();
        }

        private void OnOpenLeftoverClick(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is LeftoverItem l)) return;
            if (l.Model.Kind == LeftoverKind.RegistryKey)
            {
                try
                {
                    // Apre l'Editor del Registro direttamente sulla chiave
                    using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                        k?.SetValue("LastKey", "Computer\\" + l.Path.Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\").Replace("HKCU\\", "HKEY_CURRENT_USER\\"));
                    Process.Start(new ProcessStartInfo("regedit.exe", "/m") { UseShellExecute = true });
                }
                catch { }
                return;
            }
            OpenPath(l.Path, select: true);
        }

        private static void OpenPath(string path, bool select = false)
        {
            try
            {
                if (select && (File.Exists(path) || Directory.Exists(path)))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (File.Exists(path) || Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        // ================================================================== pulizia rapida

        private async Task InitCleanAsync()
        {
            _cleanInitialized = true;
            _cleanTargets.Clear();
            foreach (var t in QuickClean.Targets()) _cleanTargets.Add(new CleanTargetItem(t));
            await MeasureCleanAsync();
        }

        private async Task MeasureCleanAsync()
        {
            CleanTotal.Text = "Calcolo…";
            CleanButton.IsEnabled = false;
            foreach (var t in _cleanTargets) t.SizeBytes = -1;
            var tasks = _cleanTargets.Select(async t =>
            {
                t.SizeBytes = await Task.Run(() => QuickClean.Measure(t.Target));
                UpdateCleanTotal();
            });
            await Task.WhenAll(tasks);
            UpdateCleanTotal();
            CleanButton.IsEnabled = true;
        }

        private void UpdateCleanTotal()
        {
            var sel = _cleanTargets.Where(t => t.IsSelected && t.SizeBytes > 0).Sum(t => t.SizeBytes);
            CleanTotal.Text = _cleanTargets.Any(t => t.SizeBytes < 0) && sel == 0 ? "Calcolo…" : sel > 0 ? SizeFormat.Format(sel) : "0 MB";
        }

        private void OnCleanSelectionChanged(object sender, RoutedEventArgs e) => UpdateCleanTotal();

        private async void OnQuickCleanClick(object sender, RoutedEventArgs e)
        {
            var selected = _cleanTargets.Where(t => t.IsSelected).ToList();
            if (selected.Count == 0) return;
            CleanButton.IsEnabled = false;
            CleanStatus.Text = "Pulizia in corso…";
            long freed = 0;
            foreach (var t in selected)
            {
                CleanStatus.Text = "Pulizia: " + t.Title + "…";
                freed += await Task.Run(() => QuickClean.Clean(t.Target));
            }
            CleanStatus.Text = "Ultima pulizia: liberati " + SizeFormat.Format(freed) + ". Alcuni file in uso vengono saltati.";
            UpdateDisk();
            ShowToast(freed > 0 ? "Liberati " + SizeFormat.Format(freed) : "Era già tutto pulito");
            await MeasureCleanAsync();
        }

        private void ShowToast(string text, bool ok = true)
        {
            ToastText.Text = text;
            ToastGlyph.Text = ok ? "" : "";
            ToastGlyph.Foreground = (Brush)FindResource(ok ? "Success" : "Warning");
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2800))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
            Toast.BeginAnimation(OpacityProperty, anim);
        }

        // ================================================================== schermate automatiche (test)

        private async Task TakeScreenshotsAsync()
        {
            try
            {
                Directory.CreateDirectory(ScreenshotFolder);
                await Task.Delay(2500);
                Save("1-applicazioni.png");

                var first = _apps.OrderByDescending(a => a.SizeBytes).FirstOrDefault();
                if (first != null)
                {
                    OnUninstallClick(new Button { Tag = first }, null);
                    await Task.Delay(600);
                    Save("2-conferma.png");
                    CloseDialog();
                }

                NavLeftovers.IsChecked = true;
                await ScanOrphansAsync();
                await Task.Delay(500);
                Save("3-residui.png");

                NavClean.IsChecked = true;
                await Task.Delay(300);
                while (_cleanTargets.Any(t => t.SizeBytes < 0)) await Task.Delay(200);
                await Task.Delay(400);
                Save("4-pulizia.png");
            }
            catch (Exception ex)
            {
                Log.Write("Screenshot: " + ex);
            }
            finally
            {
                Application.Current.Shutdown();
            }
        }

        private void Save(string name)
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(ScreenshotFolder, name))) enc.Save(fs);
        }
    }
}
