using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Lindo.Core;
using Lindo.Services;

namespace Lindo
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public sealed class AppItem : Observable
    {
        private static readonly Color[][] Palette =
        {
            new[] { Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0xB0, 0x6A, 0xFF) },
            new[] { Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x3B, 0x82, 0xF6) },
            new[] { Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xFB, 0x71, 0x85) },
            new[] { Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x10, 0xB9, 0x81) },
            new[] { Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF9, 0x73, 0x16) },
            new[] { Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x81, 0x8C, 0xF8) },
        };

        private ImageSource _icon;
        private long _sizeBytes;
        private double _sizeRatio;
        private bool _sizeIsExact;

        public AppItem(AppInfo info)
        {
            Info = info;
            _sizeBytes = info.EstimatedSizeBytes;
            var words = NameMatcher.Tokens(info.Name).Where(t => char.IsLetterOrDigit(t[0])).ToList();
            Initials = words.Count >= 2 ? (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant()
                     : words.Count == 1 ? words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant()
                     : info.Name.Substring(0, Math.Min(1, info.Name.Length)).ToUpperInvariant();
            var c = Palette[Math.Abs(StableHash(info.Name)) % Palette.Length];
            AvatarBrush = new LinearGradientBrush(c[0], c[1], 45);
            AvatarBrush.Freeze();
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (var ch in s ?? string.Empty) h = h * 31 + ch;
                return h == int.MinValue ? 0 : h;
            }
        }

        public AppInfo Info { get; }
        public string Name => Info.Name;
        public string Initials { get; }
        public Brush AvatarBrush { get; }
        public bool IsStore => Info.Source == AppSource.Store;

        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Info.Publisher)) parts.Add(Info.Publisher);
                if (!string.IsNullOrWhiteSpace(Info.Version)) parts.Add("v" + Info.Version.TrimStart('v', 'V'));
                if (parts.Count == 0) parts.Add(IsStore ? "Microsoft Store" : "Desktop program");
                return string.Join("  ·  ", parts);
            }
        }

        public string DateText => Info.InstallDate.HasValue ? Info.InstallDate.Value.ToString("d MMM yyyy", new System.Globalization.CultureInfo("en-US")) : "—";

        public ImageSource Icon
        {
            get => _icon;
            set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); }
        }

        public bool HasIcon => _icon != null;

        public long SizeBytes
        {
            get => _sizeBytes;
            set { if (Set(ref _sizeBytes, value)) Raise(nameof(SizeText)); }
        }

        /// <summary>True quando la dimensione è stata misurata sul disco (e non stimata dal registro).</summary>
        public bool SizeIsExact
        {
            get => _sizeIsExact;
            set => Set(ref _sizeIsExact, value);
        }

        public string SizeText => SizeFormat.Format(_sizeBytes);

        /// <summary>0..1 rispetto all'app più grande (per la barra).</summary>
        public double SizeRatio
        {
            get => _sizeRatio;
            set => Set(ref _sizeRatio, value);
        }

        public string SearchText => (Info.Name + " " + Info.Publisher + " " + Info.DriverClass).ToLowerInvariant();

        private bool _isChecked;
        /// <summary>Selezionata per la disinstallazione multipla.</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        public bool IsDriver => Info.Source == AppSource.Driver;
        public bool CanUninstall => !(IsDriver && Info.DriverIsCritical);
        public string Drive => Services.Drives.Letter(Info.InstallLocation);

        /// <summary>Installata da più di 30 giorni e non usata da 60 (o mai vista in uso).</summary>
        public bool IsUnused =>
            !IsDriver &&
            (!Info.LastUsed.HasValue || DateTime.Now - Info.LastUsed.Value > TimeSpan.FromDays(60)) &&
            (!Info.InstallDate.HasValue || DateTime.Now - Info.InstallDate.Value > TimeSpan.FromDays(30));

        public string ColumnLabel => IsDriver ? "Device" : "Last used";

        public string ColumnText
        {
            get
            {
                if (IsDriver)
                    return Info.DriverIsCritical ? "System" : string.IsNullOrEmpty(Info.DriverInUseBy) ? "Not connected" : "In use";
                return Info.LastUsed.HasValue ? TimeText.Ago(Info.LastUsed.Value, DateTime.Now) : "No recorded use";
            }
        }

        public string ColumnTooltip => IsDriver
            ? (string.IsNullOrEmpty(Info.DriverInUseBy) ? "No connected device uses this driver" : "Used by: " + Info.DriverInUseBy)
            : "Installed: " + DateText;

        public void RefreshUsage()
        {
            Raise(nameof(ColumnText));
            Raise(nameof(IsUnused));
            Raise(nameof(ColumnTooltip));
        }
    }

    public sealed class DriveItem : Observable
    {
        private bool _isSelected;
        public string Letter { get; set; }
        public string Label { get; set; }
        public long Free { get; set; }
        public long Total { get; set; }
        public string FreeText => SizeFormat.Format(Free);
        public string Detail => "free of " + SizeFormat.Format(Total);
        public double UsedRatio => Total > 0 ? 1.0 - (double)Free / Total : 0;
        public bool IsLow => Total > 0 && (double)Free / Total < 0.08;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    }

    public sealed class StartupItem : Observable
    {
        private bool _enabled;
        private ImageSource _icon;
        internal Services.StartupEntry Entry { get; }

        internal StartupItem(Services.StartupEntry e)
        {
            Entry = e;
            _enabled = e.Enabled;
        }

        public string Name => Entry.Name;
        public string Subtitle => (string.IsNullOrWhiteSpace(Entry.Publisher) ? "" : Entry.Publisher + "  ·  ") + Entry.KindText;
        public string Command => Entry.Command;
        public bool IsEssential => Entry.IsEssential;
        public bool IsSuggested => Entry.Suggested && _enabled;
        public bool IsService => Entry.Kind == Services.StartupKind.Service;
        public string StateText => _enabled ? "Starts with Windows" : (IsService ? "Manual start" : "Disabled");

        public bool Enabled
        {
            get => _enabled;
            set { if (Set(ref _enabled, value)) { Raise(nameof(StateText)); Raise(nameof(IsSuggested)); } }
        }

        public ImageSource Icon { get => _icon; set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
        public bool HasIcon => _icon != null;
        public string Initials => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        public string UsageText { get; set; } = "";
    }

    public sealed class UsageRow : Observable
    {
        private ImageSource _icon;
        public UsageRecord Record { get; set; }
        public string Name { get; set; }
        public string Path => Record.Path;
        public string TimeText => Core.TimeText.Duration(Record.SecondsRunning);
        public string CpuText => Core.TimeText.Duration(Record.CpuMs / 1000);
        public string MemoryText => Record.AverageMemoryMb + " MB (max " + Record.PeakMemoryMb + ")";
        public string LastText => Core.TimeText.Ago(Record.LastSeen, DateTime.Now);
        public double Ratio { get; set; }
        public ImageSource Icon { get => _icon; set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
        public bool HasIcon => _icon != null;
        public string Initials => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
    }

    public sealed class OptimizeItem : Observable
    {
        private bool _isSelected;
        private bool _isApplied;
        internal Services.OptimizeAction Action { get; }

        internal OptimizeItem(Services.OptimizeAction a)
        {
            Action = a;
            _isSelected = a.DefaultSelected;
        }

        public string Title => Action.Title;
        public string Description => Action.Description;
        public string Category => Action.Category;
        public string Glyph => Action.Glyph;
        public bool Reversible => Action.Reversible;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        public bool IsApplied
        {
            get => _isApplied;
            set { if (Set(ref _isApplied, value)) Raise(nameof(StateText)); }
        }

        public string StateText => !Reversible ? "One-off" : _isApplied ? "Already optimized" : "To optimize";
    }

    public sealed class CrashItem
    {
        internal Services.CrashGroup Group { get; }
        internal CrashItem(Services.CrashGroup g) { Group = g; }
        public string Title => Group.Title;
        public string Kind => Group.Kind;
        public string When => (Group.Count == 1 ? "once" : Group.Count + " times") + "  ·  last " + Core.TimeText.Ago(Group.Last, DateTime.Now) +
                              " (" + Group.Last.ToString("d MMM HH:mm", new System.Globalization.CultureInfo("en-US")) + ")";
        public string Explanation => Group.Explanation;
        public string Advice => Group.Advice;
        public string Details => Group.Details;
        public bool HasDetails => !string.IsNullOrEmpty(Group.Details);
        public bool IsSystem => Group.IsSystem;
        public string Glyph => Group.Kind == "Blue screen" ? "\uE7BA" : Group.Kind == "Shutdown" ? "\uE7E8" : Group.Kind == "Hang" ? "\uE916" : "\uE783";
    }

    public sealed class LeftoverItem : Observable
    {
        private bool _isSelected;
        private long _sizeBytes;

        public LeftoverItem(Leftover l)
        {
            Model = l;
            _sizeBytes = l.SizeBytes;
            _isSelected = l.Confidence >= Confidence.Medium;
        }

        /// <summary>Etichetta del gruppo nella pagina Residui.</summary>
        public string Group => Model.Confidence == Confidence.High ? "Safe to delete" : "Check first";
        public bool IsSafe => Model.Confidence == Confidence.High;

        public Leftover Model { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string Path => Model.Kind == LeftoverKind.Driver && !string.IsNullOrEmpty(Model.DisplayName)
            ? Model.DisplayName + "  ·  " + Model.DriverInf
            : Model.Path;
        public bool IsDriver => Model.Kind == LeftoverKind.Driver;
        public string Reason => Model.Reason;

        public string KindText
        {
            get
            {
                switch (Model.Kind)
                {
                    case LeftoverKind.RegistryKey: return "Registry";
                    case LeftoverKind.Driver: return "Driver";
                    case LeftoverKind.Shortcut: return "Shortcut";
                    case LeftoverKind.File: return "File";
                    default: return "Folder";
                }
            }
        }

        public string Glyph
        {
            get
            {
                switch (Model.Kind)
                {
                    case LeftoverKind.RegistryKey: return "\uE713";
                    case LeftoverKind.Driver: return "\uE772";
                    case LeftoverKind.Shortcut: return "";
                    case LeftoverKind.File: return "";
                    default: return "";
                }
            }
        }

        public string ConfidenceText =>
            Model.Confidence == Confidence.High ? "Safe" :
            Model.Confidence == Confidence.Medium ? "Likely" : "Check";

        public string ConfidenceKey => Model.Confidence.ToString();

        public long SizeBytes
        {
            get => _sizeBytes;
            set { if (Set(ref _sizeBytes, value)) { Model.SizeBytes = value; Raise(nameof(SizeText)); } }
        }

        public string SizeText => Model.Kind == LeftoverKind.RegistryKey ? "" : SizeFormat.Format(_sizeBytes);

        public string DateText => Model.LastModified.HasValue
            ? Model.LastModified.Value.ToString("d MMM yyyy", new System.Globalization.CultureInfo("en-US"))
            : string.Empty;
    }

    public sealed class CleanTargetItem : Observable
    {
        private bool _isSelected = true;
        private long _sizeBytes = -1;

        internal CleanTargetItem(CleanTarget t) { Target = t; }

        internal CleanTarget Target { get; }
        public string Title => Target.Title;
        public string Description => Target.Description;
        public string Glyph => Target.Glyph;

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public long SizeBytes
        {
            get => _sizeBytes;
            set { if (Set(ref _sizeBytes, value)) Raise(nameof(SizeText)); }
        }

        public string SizeText => _sizeBytes < 0 ? "Calculating…" : _sizeBytes == 0 ? "Already clean" : SizeFormat.Format(_sizeBytes);
    }
}
