using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Segmento
{
    /// <summary>
    /// Reprezentuje pojedynczy zaimportowany plik PDF (źródło stron).
    /// </summary>
    public class PdfSource
    {
        public string Id { get; }
        public string FilePath { get; }
        public string FileName { get; }
        public byte[] Bytes { get; }
        public int PageCount { get; }
        public long FileSize { get; }

        public PdfSource(string filePath, byte[] bytes, int pageCount, long fileSize)
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            FilePath = filePath;
            FileName = System.IO.Path.GetFileName(filePath);
            Bytes = bytes;
            PageCount = pageCount;
            FileSize = fileSize;
        }
    }

    /// <summary>
    /// Reprezentuje pojedynczą stronę PDF (z konkretnego pliku źródłowego).
    /// </summary>
    public class PageItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isDropTarget;
        private BitmapImage? _thumbnail;
        private bool _hasThumbnail;
        private int _organizeOrder;

        // --- Multi-PDF metadata ---
        public string SourceFileId { get; }
        public string SourceFileName { get; }
        public int OriginalPageNumber { get; }
        public byte[] SourceBytes { get; }

        // Globalny indeks (kolejność dodania)
        public int PageIndex { get; set; }

        public string DisplayName => $"Strona {OriginalPageNumber}";

        /// <summary>Krótka nazwa pliku źródłowego (badge na miniaturze).</summary>
        public string SourceBadge
        {
            get
            {
                var name = SourceFileName;
                if (name.Length > 22)
                    name = name.Substring(0, 20) + "…";
                return name;
            }
        }

        public PageItem(string sourceFileId, string sourceFileName, int originalPageNumber, byte[] sourceBytes)
        {
            SourceFileId = sourceFileId;
            SourceFileName = sourceFileName;
            OriginalPageNumber = originalPageNumber;
            SourceBytes = sourceBytes;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectionBorderBrush));
                }
            }
        }

        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                if (_isDropTarget != value)
                {
                    _isDropTarget = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OrganizeBorderBrush));
                }
            }
        }

        public int OrganizeOrder
        {
            get => _organizeOrder;
            set { if (_organizeOrder != value) { _organizeOrder = value; OnPropertyChanged(); } }
        }

        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(LoadingVisibility));
            }
        }

        public bool HasThumbnail
        {
            get => _hasThumbnail;
            set
            {
                _hasThumbnail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(LoadingVisibility));
            }
        }

        public Visibility ThumbnailVisibility => HasThumbnail && Thumbnail != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LoadingVisibility => HasThumbnail && Thumbnail != null ? Visibility.Collapsed : Visibility.Visible;

        // Pomaranczowy gradient gdy zaznaczona - spojny z przyciskiem "Przejdz do organizacji"
        private static readonly Brush SelectedBorderBrush = CreateSelectionGradient();

        private static Brush CreateSelectionGradient()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC4, 0x3A, 0x00), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x5E, 0x00), 0.4));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0x78, 0x20), 1));
            brush.Freeze();
            return brush;
        }

        public Brush SelectionBorderBrush => IsSelected
            ? SelectedBorderBrush
            : new SolidColorBrush(Color.FromRgb(42, 42, 45));

        // Biały border w organize; biały też gdy drop target
        public Brush OrganizeBorderBrush => IsDropTarget
            ? new SolidColorBrush(Color.FromRgb(250, 250, 250))
            : new SolidColorBrush(Color.FromRgb(63, 63, 68));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
