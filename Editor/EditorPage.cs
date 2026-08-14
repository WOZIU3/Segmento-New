using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using Segmento.Editor.Annotations;

namespace Segmento.Editor
{
    /// <summary>Jedna strona dokumentu edycji: źródło, wymiary w punktach PDF, adnotacje, podkład.</summary>
    public sealed class EditorPage : Observable
    {
        private int _rotation;
        private Rect? _cropBoxPoints;
        private bool _isDeleted;
        private BitmapSource? _backdrop;
        private int _renderDpi = 150;

        public PageItem Source { get; }

        /// <summary>Szerokość strony w punktach PDF (MediaBox / rozmiar obrazu).</summary>
        public double WidthPoints { get; }
        public double HeightPoints { get; }

        /// <summary>Czy źródłem jest obraz (PNG/JPG), a nie PDF.</summary>
        public bool IsImageSource { get; }

        public ObservableCollection<AnnotationBase> Annotations { get; } = new();

        public int Rotation { get => _rotation; set => Set(ref _rotation, ((value % 360) + 360) % 360); }
        public Rect? CropBoxPoints { get => _cropBoxPoints; set => Set(ref _cropBoxPoints, value); }
        public bool IsDeleted { get => _isDeleted; set => Set(ref _isDeleted, value); }

        /// <summary>Bitmapa podkładu (cache zarządzany przez EditorRenderer).</summary>
        public BitmapSource? Backdrop { get => _backdrop; set => Set(ref _backdrop, value); }

        /// <summary>DPI, w którym wyrenderowano bieżący podkład.</summary>
        public int RenderDpi { get => _renderDpi; set => Set(ref _renderDpi, value); }

        /// <summary>Szerokość widoczna po uwzględnieniu obrotu strony.</summary>
        public double DisplayWidthPoints => Rotation == 90 || Rotation == 270 ? HeightPoints : WidthPoints;
        public double DisplayHeightPoints => Rotation == 90 || Rotation == 270 ? WidthPoints : HeightPoints;

        public int AnnotationCount => Annotations.Count;

        public EditorPage(PageItem source, double widthPoints, double heightPoints, bool isImageSource)
        {
            Source = source;
            WidthPoints = widthPoints;
            HeightPoints = heightPoints;
            IsImageSource = isImageSource;
        }

        /// <summary>Punkty PDF → piksele podkładu (przestrzeń bitmapy Backdrop).</summary>
        public double PointsToPixels(double pt) => pt * RenderDpi / 72.0;
        public double PixelsToPoints(double px) => px * 72.0 / RenderDpi;
    }
}
