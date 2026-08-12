using System;
using System.Windows;
using System.Windows.Media;
using Segmento.Editor;

namespace Segmento.Editor.Annotations
{
    /// <summary>
    /// Bazowy obiekt edytora. Wszystkie współrzędne i wymiary w PUNKTACH PDF (1/72"),
    /// układ o początku w lewym górnym rogu strony, oś Y w dół (jak WPF).
    /// Odbicie osi Y następuje wyłącznie przy zapisie (Etap 2, PdfDocumentWriter).
    /// </summary>
    public abstract class AnnotationBase : Observable
    {
        private Rect _boundsPoints;
        private double _rotationDegrees;
        private double _opacity = 1.0;
        private int _zIndex;
        private bool _isLocked;
        private bool _isVisible = true;
        private string _name = "";

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Prostokąt obejmujący w punktach PDF.</summary>
        public Rect BoundsPoints { get => _boundsPoints; set => Set(ref _boundsPoints, value); }

        /// <summary>Obrót wokół środka BoundsPoints, w stopniach (zgodnie z ruchem wskazówek).</summary>
        public double RotationDegrees { get => _rotationDegrees; set => Set(ref _rotationDegrees, value); }

        public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.0, 1.0)); }
        public int ZIndex { get => _zIndex; set => Set(ref _zIndex, value); }
        public bool IsLocked { get => _isLocked; set => Set(ref _isLocked, value); }
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

        /// <summary>Etykieta w panelu warstw.</summary>
        public string Name { get => _name; set => Set(ref _name, value ?? ""); }

        /// <summary>Kopia z NOWYM Id (semantyka duplikatu).</summary>
        public abstract AnnotationBase Clone();

        /// <summary>
        /// Rysuje adnotację w przestrzeni ekranu. <paramref name="pixelBounds"/> to BoundsPoints
        /// przeliczone na piksele ekranu; <paramref name="scale"/> to liczba pikseli ekranu na 1 pt.
        /// </summary>
        public abstract void Render(DrawingContext dc, Rect pixelBounds, double scale);

        /// <summary>Zapis wektorowy do content streamu strony (iText). BoundsPoints w pt PDF, origin lewy-górny.</summary>
        public abstract void WriteToPdf(PdfWriterContext ctx);

        /// <summary>Kopiuje pola wspólne do celu (używane w Clone() klas pochodnych).</summary>
        protected void CopyBaseTo(AnnotationBase target)
        {
            target._boundsPoints = _boundsPoints;
            target._rotationDegrees = _rotationDegrees;
            target._opacity = _opacity;
            target._zIndex = _zIndex;
            target._isLocked = _isLocked;
            target._isVisible = _isVisible;
            target._name = _name;
        }
    }
}
