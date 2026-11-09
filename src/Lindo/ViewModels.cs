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
                if (parts.Count == 0) parts.Add(IsStore ? "Microsoft Store" : "Programma desktop");
                return string.Join("  ·  ", parts);
            }
        }

        public string DateText => Info.InstallDate.HasValue ? Info.InstallDate.Value.ToString("d MMM yyyy", new System.Globalization.CultureInfo("it-IT")) : "—";

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

        public string SearchText => (Info.Name + " " + Info.Publisher).ToLowerInvariant();
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

        public Leftover Model { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string Path => Model.Path;
        public string Reason => Model.Reason;

        public string KindText
        {
            get
            {
                switch (Model.Kind)
                {
                    case LeftoverKind.RegistryKey: return "Registro";
                    case LeftoverKind.Shortcut: return "Collegamento";
                    case LeftoverKind.File: return "File";
                    default: return "Cartella";
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
                    case LeftoverKind.Shortcut: return "";
                    case LeftoverKind.File: return "";
                    default: return "";
                }
            }
        }

        public string ConfidenceText =>
            Model.Confidence == Confidence.High ? "Sicuro" :
            Model.Confidence == Confidence.Medium ? "Probabile" : "Da verificare";

        public string ConfidenceKey => Model.Confidence.ToString();

        public long SizeBytes
        {
            get => _sizeBytes;
            set { if (Set(ref _sizeBytes, value)) { Model.SizeBytes = value; Raise(nameof(SizeText)); } }
        }

        public string SizeText => Model.Kind == LeftoverKind.RegistryKey ? "" : SizeFormat.Format(_sizeBytes);

        public string DateText => Model.LastModified.HasValue
            ? "Modificata il " + Model.LastModified.Value.ToString("d MMM yyyy", new System.Globalization.CultureInfo("it-IT"))
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

        public string SizeText => _sizeBytes < 0 ? "Calcolo…" : _sizeBytes == 0 ? "Già pulito" : SizeFormat.Format(_sizeBytes);
    }
}
