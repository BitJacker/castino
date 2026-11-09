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

    public sealed class InverseBoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<DriveItem> _drives = new ObservableCollection<DriveItem>();
        private string _driveFilter;
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
            var orphanView = (ListCollectionView)CollectionViewSource.GetDefaultView(_orphans);
            orphanView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItem.Group)));
            orphanView.SortDescriptions.Add(new SortDescription(nameof(LeftoverItem.IsSafe), ListSortDirection.Descending));
            orphanView.SortDescriptions.Add(new SortDescription(nameof(LeftoverItem.SizeBytes), ListSortDirection.Descending));
            OrphansList.ItemsSource = orphanView;
            DrivesList.ItemsSource = _drives;
            InitPages();
            DlgLeftoversList.ItemsSource = _dlgLeftovers;
            CleanList.ItemsSource = _cleanTargets;

            SourceInitialized += (s, e) => ApplyWindowEffects();
            StateChanged += (s, e) => OnWindowStateChanged();
            Loaded += async (s, e) =>
            {
                foreach (var spinner in new[] { LoadingSpinner, OrphansSpinner, DlgSpinner, StartupSpinner, CrashSpinner }) Spin(spinner);
                UpdateDisk();
                await LoadAppsAsync();
                if (!string.IsNullOrEmpty(ScreenshotFolder)) await TakeScreenshotsAsync();
            };
        }

        // ================================================================== finestra

        // Con WindowStyle=None una finestra ingrandita coprirebbe anche la barra delle applicazioni:
        // rispondendo a WM_GETMINMAXINFO le diciamo di occupare solo l'area di lavoro del monitor.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            try
            {
                var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
                var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
                mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
                mmi.MaxSize.X = info.Work.Right - info.Work.Left;
                mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
            }
            catch { }
            return IntPtr.Zero;
        }

        private void ApplyWindowEffects()
        {
            try { HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc); } catch { }
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
            Root.Margin = new Thickness(0); // l'area di lavoro la decide WndProc (WM_GETMINMAXINFO)
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
            var selected = _driveFilter;
            _drives.Clear();
            foreach (var d in Drives.Fixed())
            {
                string label;
                try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel; } catch { label = ""; }
                _drives.Add(new DriveItem
                {
                    Letter = d.Name.Substring(0, 2).ToUpperInvariant(),
                    Label = label,
                    Free = d.AvailableFreeSpace,
                    Total = d.TotalSize,
                    IsSelected = d.Name.StartsWith(selected ?? "-", StringComparison.OrdinalIgnoreCase),
                });
            }

            // Filtro dischi nella pagina Applicazioni
            var current = DriveBox.SelectedItem as ComboBoxItem;
            var tag = current?.Tag as string;
            _suppressDriveEvent = true;
            DriveBox.Items.Clear();
            DriveBox.Items.Add(new ComboBoxItem { Content = "All drives", Tag = "" });
            foreach (var d in _drives) DriveBox.Items.Add(new ComboBoxItem { Content = "Drive " + d.Letter, Tag = d.Letter });
            DriveBox.SelectedItem = DriveBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (x.Tag as string) == (tag ?? "")) ?? DriveBox.Items[0];
            _suppressDriveEvent = false;
        }

        private bool _suppressDriveEvent;

        private void OnDriveFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDriveEvent) return;
            var tag = (DriveBox.SelectedItem as ComboBoxItem)?.Tag as string;
            _driveFilter = string.IsNullOrEmpty(tag) ? null : tag;
            foreach (var d in _drives) d.IsSelected = _driveFilter != null && d.Letter == _driveFilter;
            _view?.Refresh();
            UpdateEmptyState();
        }

        private void OnDriveClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is DriveItem d)) return;
            NavApps.IsChecked = true;
            var item = DriveBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (x.Tag as string) == (d.IsSelected ? "" : d.Letter));
            if (item != null) DriveBox.SelectedItem = item;
        }

        private void OnNavChanged(object sender, RoutedEventArgs e)
        {
            if (PageApps == null) return;
            PageApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLeftovers.Visibility = NavLeftovers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageClean.Visibility = NavClean.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageStartup.Visibility = NavStartup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageOptimize.Visibility = NavOptimize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageCrashes.Visibility = NavCrashes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavClean.IsChecked == true && !_cleanInitialized) _ = InitCleanAsync();
            if (NavStartup.IsChecked == true && !_startupLoaded) _ = LoadStartupAsync();
            if (NavOptimize.IsChecked == true && !_optimizeLoaded) _ = LoadOptimizeAsync();
            if (NavCrashes.IsChecked == true && !_crashesLoaded) _ = LoadCrashesAsync();
        }

        // ================================================================== elenco app

        private async Task<List<AppInfo>> ScanAllAsync(bool includeDrivers = false)
        {
            var desktop = Task.Run(() => AppScanner.ScanDesktop());
            var store = Task.Run(() => AppScanner.ScanStore());
            var drivers = includeDrivers ? Task.Run(() => DriverService.Scan()) : Task.FromResult(new List<AppInfo>());
            await Task.WhenAll(desktop, store, drivers);
            var all = desktop.Result.Concat(store.Result).ToList();
            Drives.UpdateExtraRoots(all);
            return all.Concat(drivers.Result).ToList();
        }

        private async Task LoadAppsAsync()
        {
            if (_loadingApps) return;
            _loadingApps = true;
            _sizeCts?.Cancel();
            AppsLoading.Visibility = Visibility.Visible;
            AppsEmpty.Visibility = Visibility.Collapsed;
            AppsSummary.Text = "Scanning…";
            try
            {
                _allInfos = await ScanAllAsync(includeDrivers: true);
                _apps.Clear();
                foreach (var info in _allInfos) _apps.Add(new AppItem(info));
                UpdateStats();
                AppsLoading.Visibility = Visibility.Collapsed;
                UpdateEmptyState();

                _sizeCts = new CancellationTokenSource();
                var ct = _sizeCts.Token;
                var items = _apps.ToList();
                var usage = Task.Run(() => UsageService.FillLastUsed(_allInfos));
                await Task.WhenAll(LoadIconsAsync(items, ct), MeasureSizesAsync(items, ct), usage);
                foreach (var a in items) a.RefreshUsage();
                UpdateStats();
                _view.Refresh();
            }
            catch (Exception ex)
            {
                Log.Write("Errore caricamento app: " + ex);
                AppsSummary.Text = "Couldn't read the app list: " + ex.Message;
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
                var icons = await Task.Run(() => batch.Select(a => a.IsDriver ? null : IconLoader.Load(a.Info)).ToList());
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
                    if (info.Source == AppSource.Driver) return;
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
            var total = _apps.Where(a => !a.IsDriver).Sum(a => a.SizeBytes);
            StatCount.Text = _apps.Count(a => !a.IsDriver).ToString();
            StatSize.Text = SizeFormat.Format(total);
            var biggest = _apps.Where(a => !a.IsDriver).OrderByDescending(a => a.SizeBytes).FirstOrDefault();
            StatBiggest.Text = biggest?.Name ?? "—";
            StatBiggestSize.Text = biggest != null ? SizeFormat.Format(biggest.SizeBytes) : "";
            var store = _apps.Count(a => a.IsStore);
            var drivers = _apps.Count(a => a.IsDriver);
            var unused = _apps.Count(a => a.IsUnused);
            AppsSummary.Text = $"{_apps.Count - store - drivers} programs, {store} Store apps and {drivers} drivers · pick what to remove and Lindo cleans up the leftovers too";
            FilterUnusedText.Text = unused > 0 ? "Unused (" + unused + ")" : "Unused";
            UnusedLink.Text = unused > 0 ? "See the " + unused + " apps you haven't used in 2 months →" : "See the apps you don't use →";
        }

        private bool FilterApp(object o)
        {
            if (!(o is AppItem a)) return false;
            if (FilterDrivers?.IsChecked == true) { if (!a.IsDriver) return false; }
            else if (a.IsDriver && FilterAll?.IsChecked != true) return false;
            if (FilterDesktop?.IsChecked == true && (a.IsStore || a.IsDriver)) return false;
            if (FilterStore?.IsChecked == true && !a.IsStore) return false;
            if (FilterUnused?.IsChecked == true && !a.IsUnused) return false;
            if (FilterAll?.IsChecked == true && a.IsDriver && string.IsNullOrEmpty(SearchBox?.Text)) return false; // i driver solo nella loro scheda o cercandoli
            if (_driveFilter != null && a.Drive != _driveFilter && !(a.IsStore && _driveFilter == Drives.Letter(SystemInfo.SystemDrive))) return false;
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
                        _view.SortDescriptions.Add(new SortDescription("Info.InstallDate", ListSortDirection.Descending));
                        break;
                    case 3:
                        _view.SortDescriptions.Add(new SortDescription("Info.LastUsed", ListSortDirection.Ascending));
                        _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.SizeBytes), ListSortDirection.Descending));
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
            string primaryText = "Done", Action primary = null, string secondaryText = null, Action secondary = null, bool danger = false)
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

        private List<AppItem> _batch = new List<AppItem>();
        private readonly Dictionary<AppItem, string> _batchNotes = new Dictionary<AppItem, string>();

        private void OnAppCheckClick(object sender, RoutedEventArgs e) => UpdateAppSelection();

        private void UpdateAppSelection()
        {
            var sel = _apps.Where(a => a.IsChecked).ToList();
            AppsSelectionBar.Visibility = sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppsSelectionText.Text = sel.Count == 1 ? "1 app selected · " + SizeFormat.Format(sel[0].SizeBytes)
                : sel.Count + " apps selected · " + SizeFormat.Format(sel.Sum(a => a.SizeBytes));
        }

        private void OnClearAppSelection(object sender, RoutedEventArgs e)
        {
            foreach (var a in _apps) a.IsChecked = false;
            UpdateAppSelection();
        }

        private void OnUninstallSelected(object sender, RoutedEventArgs e)
        {
            var sel = _apps.Where(a => a.IsChecked && a.CanUninstall).ToList();
            if (sel.Count == 0) return;
            if (sel.Count == 1) { ShowConfirm(new List<AppItem> { sel[0] }); return; }
            ShowConfirm(sel);
        }

        private void OnUninstallClick(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is AppItem item)) return;
            ShowConfirm(new List<AppItem> { item });
        }

        private void ShowConfirm(List<AppItem> items)
        {
            _batch = items;
            _current = items[0];
            var multi = items.Count > 1;
            var info = items[0].Info;

            if (multi)
            {
                DlgAvatar.Background = (Brush)FindResource("Accent");
                DlgInitials.Text = items.Count.ToString();
                DlgIconHost.Visibility = Visibility.Collapsed;
                DlgAppName.Text = items.Count + " apps to uninstall";
                var names = string.Join(", ", items.Take(4).Select(a => a.Name)) + (items.Count > 4 ? $" and {items.Count - 4} more" : "");
                DlgAppMeta.Text = names + "  ·  " + SizeFormat.Format(items.Sum(a => a.SizeBytes));
                DlgNoUninstaller.Visibility = items.Any(a => !Uninstaller.HasWorkingUninstaller(a.Info) && !a.IsDriver) ? Visibility.Visible : Visibility.Collapsed;
                DlgConfirmText.Text = "Uninstall " + items.Count + " apps";
                DlgStep1.Text = "I'll start the uninstallers one at a time.";
            }
            else
            {
                var item = items[0];
                DlgAvatar.Background = item.AvatarBrush;
                DlgInitials.Text = item.Initials;
                DlgIcon.Source = item.Icon;
                DlgIconHost.Visibility = item.HasIcon ? Visibility.Visible : Visibility.Collapsed;
                DlgAppName.Text = info.Name;
                var meta = new List<string>();
                if (!string.IsNullOrWhiteSpace(info.Publisher)) meta.Add(info.Publisher);
                if (item.SizeBytes > 0) meta.Add((item.SizeIsExact || item.IsDriver ? "" : "about ") + SizeFormat.Format(item.SizeBytes));
                if (item.IsDriver && !string.IsNullOrEmpty(info.DriverInUseBy)) meta.Add("used by " + info.DriverInUseBy);
                DlgAppMeta.Text = string.Join("  ·  ", meta);

                var works = item.IsDriver || Uninstaller.HasWorkingUninstaller(info);
                DlgNoUninstaller.Visibility = works ? Visibility.Collapsed : Visibility.Visible;
                DlgConfirmText.Text = item.IsDriver ? "Remove driver" : works ? "Uninstall" : "Force removal";
                DlgStep1.Text = item.IsDriver ? "I'll remove the driver with the Windows tool (pnputil)."
                             : !works ? "The uninstaller is missing: I'll go straight to the cleanup."
                             : info.Source == AppSource.Store ? "I'll remove the app through Windows (like from the Start menu)."
                             : "I'll start the app's official uninstaller.";
            }
            ShowPanel(DlgConfirm);
        }

        private async void OnConfirmUninstall(object sender, RoutedEventArgs e)
        {
            var batch = _batch.ToList();
            if (batch.Count == 0) return;
            var multi = batch.Count > 1;
            _batchNotes.Clear();
            var forced = new HashSet<AppItem>();

            for (int i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                var info = item.Info;
                _current = item;
                var prefix = multi ? $"{i + 1} of {batch.Count} · " : "";

                if (item.IsDriver)
                {
                    DlgRunningTitle.Text = prefix + "Removing the driver";
                    DlgRunningText.Text = info.Name;
                    DlgSkipWait.Visibility = Visibility.Collapsed;
                    ShowPanel(DlgRunning);
                    var err = await Task.Run(() => DriverService.Delete(info.DriverInf));
                    _batchNotes[item] = err == null ? "ok" : err;
                    continue;
                }

                if (!Uninstaller.HasWorkingUninstaller(info))
                {
                    forced.Add(item);
                    _batchNotes[item] = "forced";
                    continue;
                }

                DlgRunningTitle.Text = prefix + "Uninstalling " + info.Name;
                DlgRunningText.Text = "Starting…";
                DlgSkipWait.Content = multi && i < batch.Count - 1 ? "I'm done, go to the next one" : "I'm done, find leftovers";
                DlgSkipWait.Visibility = Visibility.Visible;
                ShowPanel(DlgRunning);

                _skipCts = new CancellationTokenSource();
                var progress = new Progress<string>(t => DlgRunningText.Text = t);
                UninstallOutcome outcome;
                try { outcome = await Uninstaller.RunAsync(info, _skipCts.Token, progress); }
                catch (Exception ex) { Log.Write("Disinstallazione fallita: " + ex); outcome = UninstallOutcome.NoUninstaller; }
                if (Overlay.Visibility != Visibility.Visible) return; // annullato

                if (!multi && outcome == UninstallOutcome.StillInstalled)
                {
                    ShowResult("The uninstall wasn't completed",
                        "The program still shows as installed. Maybe you cancelled the uninstaller, or a restart is needed.",
                        new[] { CheckLine.Info("You can try again, or do a force removal: Lindo deletes the files and the list entry directly.") },
                        success: false, primaryText: "Close", primary: CloseDialog,
                        secondaryText: "Force removal", secondary: async () => await ScanBatchLeftoversAsync(new List<AppItem> { item }, true));
                    return;
                }
                if (!multi && outcome == UninstallOutcome.NoUninstaller)
                {
                    ShowResult("Couldn't start the uninstaller", "The uninstaller didn't start.",
                        new[] { CheckLine.Info("You can do a force removal: Lindo deletes the leftover files and the list entry directly.") },
                        success: false, primaryText: "Force removal", primary: async () => await ScanBatchLeftoversAsync(new List<AppItem> { item }, true),
                        secondaryText: "Cancel", secondary: CloseDialog);
                    return;
                }
                _batchNotes[item] = outcome == UninstallOutcome.StillInstalled ? "still installed" : outcome == UninstallOutcome.NoUninstaller ? "not started" : "ok";
            }

            var appsToScan = batch.Where(b => !b.IsDriver).ToList();
            if (appsToScan.Count == 0)
            {
                var checks = batch.Select(b => _batchNotes.TryGetValue(b, out var n) && n == "ok"
                    ? CheckLine.Ok("Driver removed: " + b.Name) : CheckLine.Fail(b.Name + ": " + (_batchNotes.TryGetValue(b, out var m) ? m : "error"))).ToList();
                UpdateDisk();
                _ = LoadAppsAsync();
                ShowResult(checks.All(c => c.Glyph == "") ? "Drivers removed" : "Driver removal", null, checks, success: checks.All(c => c.Glyph == ""));
                return;
            }
            await ScanBatchLeftoversAsync(appsToScan, forced.Count == appsToScan.Count);
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
                if (l.Kind == LeftoverKind.RegistryKey || l.Kind == LeftoverKind.Driver) continue;
                if (Directory.Exists(l.Path)) l.SizeBytes = WindowsFileSystem.DirectorySize(l.Path);
                else if (File.Exists(l.Path)) { try { l.SizeBytes = new FileInfo(l.Path).Length; } catch { } }
                if (!l.LastModified.HasValue) l.LastModified = WindowsFileSystem.LastWrite(l.Path);
            }
        }

        private bool _wasForced;
        private List<AppItem> _scanned = new List<AppItem>();

        private async Task ScanBatchLeftoversAsync(List<AppItem> items, bool forced)
        {
            _batch = items;
            _current = items[0];
            _scanned = items;
            _wasForced = forced;
            var multi = items.Count > 1;
            DlgRunningTitle.Text = multi ? "Looking for leftovers of " + items.Count + " apps" : "Looking for leftovers of " + items[0].Info.Name;
            DlgRunningText.Text = "Checking Program Files, AppData, ProgramData, Start menu, Desktop, registry and related drivers…";
            DlgSkipWait.Visibility = Visibility.Collapsed;
            ShowPanel(DlgRunning);

            List<Leftover> found;
            List<AppItem> stillInstalled;
            try
            {
                var all = await ScanAllAsync();
                stillInstalled = items.Where(it => all.Any(a => a.Id == it.Info.Id)).ToList();
                found = await Task.Run(() =>
                {
                    var planner = CreatePlanner();
                    var res = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in items)
                    {
                        foreach (var l in planner.FindLeftovers(it.Info, all))
                            if (!res.ContainsKey(l.Path)) res[l.Path] = l;
                        try
                        {
                            foreach (var d in DriverService.FindRelated(it.Info))
                                if (!res.ContainsKey(d.Path)) res[d.Path] = d;
                        }
                        catch (Exception ex) { Log.Write("Driver collegati: " + ex.Message); }
                    }
                    var list = res.Values.ToList();
                    // Nessuna cartella dentro un'altra già proposta
                    list = list.Where(l => l.Kind == LeftoverKind.RegistryKey || l.Kind == LeftoverKind.Driver ||
                                           !list.Any(o => !ReferenceEquals(o, l) && o.Kind == LeftoverKind.Folder && WinPath.IsStrictlyInside(l.Path, o.Path))).ToList();
                    FillSizes(list);
                    return list.OrderBy(l => l.Kind == LeftoverKind.Driver ? 1 : 0).ThenByDescending(l => l.Confidence).ToList();
                });
            }
            catch (Exception ex)
            {
                Log.Write("Ricerca residui fallita: " + ex);
                ShowResult("The leftover search failed", ex.Message, null, success: false);
                return;
            }

            _dlgLeftovers.Clear();
            foreach (var l in found)
            {
                var vm = new LeftoverItem(l);
                if (l.Kind == LeftoverKind.Driver) vm.IsSelected = false; // decide l'utente con la domanda
                vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LeftoverItem.IsSelected)) UpdateDlgSelection(); };
                _dlgLeftovers.Add(vm);
            }

            var names = multi ? items.Count + " apps" : items[0].Info.Name;
            if (_dlgLeftovers.Count == 0)
            {
                _ = LoadAppsAsync();
                UpdateDisk();
                var any = stillInstalled.Count > 0;
                ShowResult(any ? "No leftovers found" : "Uninstall complete",
                    any ? "But some programs still show as installed." : names + (multi ? " were" : " was") + " removed completely.",
                    new[]
                    {
                        any ? CheckLine.Warn("Still in the app list: " + string.Join(", ", stillInstalled.Select(x => x.Name)))
                            : CheckLine.Ok("Removed from the app list"),
                        CheckLine.Ok("No folders left in Program Files, AppData and ProgramData"),
                        CheckLine.Ok("No shortcuts in the Start menu or on the Desktop"),
                        CheckLine.Ok("No registry keys or drivers left"),
                    }, success: !any);
                return;
            }

            var drivers = _dlgLeftovers.Where(l => l.IsDriver).ToList();
            DlgDriverQuestion.Visibility = drivers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (drivers.Count > 0)
            {
                var anyInUse = drivers.Any(d => d.Model.Confidence == Confidence.Low);
                DlgDriverText.Text = $"I found {drivers.Count} drivers related to {names} (for example {drivers[0].Model.DisplayName}). Do you want to delete the drivers too?" +
                                     (anyInUse ? " Warning: some are used by a connected device, which will stop working until you reinstall the driver." : "");
                _syncing = true;
                DlgDriverCheck.IsChecked = false;
                _syncing = false;
            }

            var total = found.Sum(f => f.SizeBytes);
            DlgLeftoversTitle.Text = forced ? "Force removal of " + names : "Here's what's left of " + names;
            DlgLeftoversText.Text = $"{found.Count} items · {SizeFormat.Format(total)}. " +
                                    "«Safe» and «Likely» items are already selected; you decide about the «Check» ones.";
            UpdateDlgSelection();
            ShowPanel(DlgLeftovers);
        }

        /// <summary>
        /// Vero mentre Lindo aggiorna da solo le caselle "seleziona tutto": in quel momento i loro eventi
        /// Checked/Unchecked non devono cambiare la selezione degli elementi.
        /// (Si usano Checked/Unchecked e non Click perché funzionino anche da tastiera e con le
        /// tecnologie assistive, che cambiano lo stato senza generare un clic.)
        /// </summary>
        private bool _syncing;

        private void OnDlgDriverCheck(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var on = DlgDriverCheck.IsChecked == true;
            foreach (var d in _dlgLeftovers.Where(l => l.IsDriver)) d.IsSelected = on;
        }

        private void UpdateDlgSelection()
        {
            var sel = _dlgLeftovers.Where(l => l.IsSelected).ToList();
            DlgDeleteButton.IsEnabled = sel.Count > 0;
            DlgDeleteText.Text = sel.Count == 0 ? "Delete selected"
                : $"Delete {sel.Count} " + (sel.Count == 1 ? "item" : "items") +
                  (sel.Sum(l => l.SizeBytes) > 0 ? " · " + SizeFormat.Format(sel.Sum(l => l.SizeBytes)) : "");
            _syncing = true;
            try
            {
                DlgSelectAll.IsChecked = sel.Count == _dlgLeftovers.Count && sel.Count > 0;
                var drivers = _dlgLeftovers.Where(l => l.IsDriver).ToList();
                if (drivers.Count > 0) DlgDriverCheck.IsChecked = drivers.All(d => d.IsSelected);
            }
            finally { _syncing = false; }
        }

        private void OnDlgSelectAll(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var on = DlgSelectAll.IsChecked == true;
            foreach (var l in _dlgLeftovers) l.IsSelected = on;
        }

        private void OnSkipLeftovers(object sender, RoutedEventArgs e)
        {
            var names = _scanned.Count > 1 ? _scanned.Count + " apps" : _current?.Info.Name;
            _ = LoadAppsAsync();
            UpdateDisk();
            ShowResult("Done", names != null ? "I didn't delete any leftovers of " + names + "." : null,
                new[] { CheckLine.Info("You can find them again at any time in «Leftovers».") }, success: true);
        }

        private async void OnDeleteLeftovers(object sender, RoutedEventArgs e)
        {
            var items = _scanned.ToList();
            var selected = _dlgLeftovers.Where(l => l.IsSelected).Select(l => l.Model).ToList();
            if (items.Count == 0 || selected.Count == 0) return;

            DlgRunningTitle.Text = "Cleaning up";
            DlgRunningText.Text = "Deleting the selected leftovers…";
            DlgSkipWait.Visibility = Visibility.Collapsed;
            ShowPanel(DlgRunning);

            var progress = new Progress<string>(t => DlgRunningText.Text = t);
            var result = await Task.Run(() => Cleaner.Delete(selected, progress));

            // Verifica finale: ricontrolla il sistema
            var all = await ScanAllAsync();
            var still = items.Where(it => all.Any(a => a.Id == it.Info.Id)).ToList();
            var remaining = await Task.Run(() => items.SelectMany(it => CreatePlanner().FindLeftovers(it.Info, all)).ToList());
            var remainingSelected = remaining.Count(r => selected.Any(s => string.Equals(s.Path, r.Path, StringComparison.OrdinalIgnoreCase)));

            var checks = new List<CheckLine>();
            checks.Add(still.Count > 0 ? CheckLine.Warn("Still in the app list: " + string.Join(", ", still.Select(x => x.Name)))
                                       : CheckLine.Ok(items.Count > 1 ? "All " + items.Count + " apps removed from the list" : "Program removed from the app list"));
            checks.Add(CheckLine.Ok($"{result.Deleted} {(result.Deleted == 1 ? "item deleted" : "items deleted")}"));
            var driversDeleted = selected.Count(x => x.Kind == LeftoverKind.Driver) - result.Failed.Count(f => selected.Any(x => x.Kind == LeftoverKind.Driver && f.StartsWith(x.DisplayName)));
            if (selected.Any(x => x.Kind == LeftoverKind.Driver)) checks.Add(CheckLine.Ok($"Drivers removed: {Math.Max(0, driversDeleted)}"));
            if (result.ScheduledOnReboot > 0)
                checks.Add(CheckLine.Warn($"{result.ScheduledOnReboot} files were in use: they'll be deleted at the next restart"));
            foreach (var f in result.Failed.Take(3)) checks.Add(CheckLine.Fail("Not deleted: " + f));
            if (result.Failed.Count > 3) checks.Add(CheckLine.Fail($"…and {result.Failed.Count - 3} more"));
            if (!string.IsNullOrEmpty(result.BackupFolder))
                checks.Add(CheckLine.Info("Registry backup saved in " + result.BackupFolder));
            if (remainingSelected > 0 && result.ScheduledOnReboot == 0)
                checks.Add(CheckLine.Warn("Check: some selected items are still there"));
            else if (remaining.Count == 0)
                checks.Add(CheckLine.Ok("Final check: no trace left"));
            else
                checks.Add(CheckLine.Info($"Final check: {remaining.Count} items you chose to keep are still there"));

            UpdateDisk();
            _ = LoadAppsAsync();
            var names = items.Count > 1 ? items.Count + " apps" : items[0].Info.Name;
            ShowResult(result.FreedBytes > 0 ? "Freed " + SizeFormat.Format(result.FreedBytes) : "Cleanup complete",
                names + (still.Count > 0 ? " cleaned up." : items.Count > 1 ? " removed completely." : " was removed completely."),
                checks, success: result.Failed.Count == 0,
                secondaryText: "Show activity log", secondary: () => OpenPath(Log.FilePath));
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
            OrphansLoadingText.Text = "Reading installed apps…";
            _orphans.Clear();
            try
            {
                var all = await ScanAllAsync();
                OrphansLoadingText.Text = "Collecting usage evidence (PATH, services, running programs, shortcuts)…";
                var evidence = await Task.Run(() => EvidenceService.Collect());
                OrphansLoadingText.Text = "Comparing folders on all drives with installed apps…";
                var found = await Task.Run(() =>
                {
                    // Anche le voci nascoste (componenti di SDK, runtime…) proteggono le loro cartelle
                    var owners = all.Concat(AppScanner.ScanDesktop(includeHidden: true)).ToList();
                    var list = CreatePlanner().FindOrphans(owners, evidence);
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
                            Confidence = Confidence.High,
                            Reason = "Ghost entry: «" + a.Name + "» is no longer installed",
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
                ScanOrphansText.Text = "Scan again";
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
            var selSize = sel.Sum(o => o.SizeBytes);
            OrphansSelection.Text = $"{sel.Count} of {_orphans.Count} selected · {(selSize > 0 ? SizeFormat.Format(selSize) : "0 B")} of {SizeFormat.Format(total)}";
            DeleteOrphansButton.IsEnabled = sel.Count > 0;
            var safe = _orphans.Where(o => o.IsSafe).ToList();
            _syncing = true;
            OrphansSelectAll.IsChecked = safe.Count > 0 && safe.All(o => o.IsSelected);
            _syncing = false;
        }

        private void OnOrphansSelectAll(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var on = OrphansSelectAll.IsChecked == true;
            foreach (var o in _orphans) o.IsSelected = on && o.IsSafe;
        }

        private void OnDeleteOrphansClick(object sender, RoutedEventArgs e)
        {
            var selected = _orphans.Where(o => o.IsSelected).Select(o => o.Model).ToList();
            if (selected.Count == 0) return;
            var lines = selected.Take(6).Select(s => CheckLine.Info(s.Path)).ToList();
            if (selected.Count > 6) lines.Add(CheckLine.Info($"…and {selected.Count - 6} more"));
            ShowResult($"Delete {selected.Count} {(selected.Count == 1 ? "item" : "items")}?",
                "This can't be undone (a backup of the registry is saved).",
                lines, success: false, danger: true,
                primaryText: "Delete", primary: async () => await DeleteOrphansAsync(selected),
                secondaryText: "Cancel", secondary: CloseDialog);
        }

        private async Task DeleteOrphansAsync(List<Leftover> selected)
        {
            DlgRunningTitle.Text = "Cleaning up";
            DlgRunningText.Text = "Deleting the selected items…";
            DlgSkipWait.Visibility = Visibility.Collapsed;
            ShowPanel(DlgRunning);
            var progress = new Progress<string>(t => DlgRunningText.Text = t);
            var result = await Task.Run(() => Cleaner.Delete(selected, progress));

            var checks = new List<CheckLine> { CheckLine.Ok($"{result.Deleted} items deleted") };
            if (result.ScheduledOnReboot > 0) checks.Add(CheckLine.Warn($"{result.ScheduledOnReboot} files in use will be deleted at restart"));
            foreach (var f in result.Failed.Take(3)) checks.Add(CheckLine.Fail("Not deleted: " + f));
            if (!string.IsNullOrEmpty(result.BackupFolder)) checks.Add(CheckLine.Info("Registry backup in " + result.BackupFolder));

            UpdateDisk();
            ShowResult(result.FreedBytes > 0 ? "Freed " + SizeFormat.Format(result.FreedBytes) : "Cleanup complete", null,
                checks, success: result.Failed.Count == 0, primary: async () => { CloseDialog(); await ScanOrphansAsync(); });
            _ = LoadAppsAsync();
        }

        private void OnOpenLeftoverClick(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is LeftoverItem l)) return;
            if (l.Model.Kind == LeftoverKind.Driver)
            {
                try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); } catch { }
                return;
            }
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
            CleanTotal.Text = "Calculating…";
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
            CleanTotal.Text = _cleanTargets.Any(t => t.SizeBytes < 0) && sel == 0 ? "Calculating…" : sel > 0 ? SizeFormat.Format(sel) : "0 MB";
        }

        private void OnCleanSelectionChanged(object sender, RoutedEventArgs e) => UpdateCleanTotal();

        private async void OnQuickCleanClick(object sender, RoutedEventArgs e)
        {
            var selected = _cleanTargets.Where(t => t.IsSelected).ToList();
            if (selected.Count == 0) return;
            CleanButton.IsEnabled = false;
            CleanStatus.Text = "Cleaning…";
            long freed = 0;
            foreach (var t in selected)
            {
                CleanStatus.Text = "Cleaning: " + t.Title + "…";
                freed += await Task.Run(() => QuickClean.Clean(t.Target));
            }
            CleanStatus.Text = "Last cleanup: freed " + SizeFormat.Format(freed) + ". Some files in use are skipped.";
            UpdateDisk();
            ShowToast(freed > 0 ? "Freed " + SizeFormat.Format(freed) : "Everything was already clean");
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
                Save("1-apps.png");

                var first = _apps.OrderByDescending(a => a.SizeBytes).FirstOrDefault();
                if (first != null)
                {
                    OnUninstallClick(new Button { Tag = first }, null);
                    await Task.Delay(600);
                    Save("2-confirm.png");
                    CloseDialog();
                }

                NavLeftovers.IsChecked = true;
                await ScanOrphansAsync();
                await Task.Delay(500);
                Save("3-leftovers.png");

                NavClean.IsChecked = true;
                await Task.Delay(300);
                while (_cleanTargets.Any(t => t.SizeBytes < 0)) await Task.Delay(200);
                await Task.Delay(400);
                Save("4-quick-clean.png");

                NavStartup.IsChecked = true;
                while (!_startupLoaded) await Task.Delay(200);
                await Task.Delay(600);
                Save("5-startup.png");

                NavOptimize.IsChecked = true;
                while (!_optimizeLoaded) await Task.Delay(200);
                await Task.Delay(3500); // lettura di CPU, RAM e scheda video
                Save("6-optimize.png");

                NavCrashes.IsChecked = true;
                while (!_crashesLoaded) await Task.Delay(200);
                await Task.Delay(600);
                Save("7-crashes.png");

                NavApps.IsChecked = true;
                FilterDrivers.IsChecked = true;
                await Task.Delay(800);
                Save("8-drivers.png");
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
