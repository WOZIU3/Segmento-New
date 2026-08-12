using System.Windows;
using System.Windows.Media;
using Segmento.Editor;

namespace Segmento.Editor.Annotations
{
    /// <summary>
    /// Obszar redakcji. W modelu to zwykły, odwracalny obiekt (undo działa). Trwałe usunięcie
    /// treści realizuje PdfDocumentWriter przez pdfSweep; OverlayText dorysowywany po czyszczeniu.
    /// Dlatego WriteToPdf jest tu no-op — writer obsługuje redakcje osobno.
    /// </summary>
    public sealed class RedactAnnotation : AnnotationBase
    {
        private Color _fillColor = Colors.Black;
        private string _overlayText = "";

        public Color FillColor { get => _fillColor; set => Set(ref _fillColor, value); }
        public string OverlayText { get => _overlayText; set => Set(ref _overlayText, value ?? ""); }

        public override AnnotationBase Clone()
        {
            var c = new RedactAnnotation { _fillColor = _fillColor, _overlayText = _overlayText };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            dc.DrawRectangle(new SolidColorBrush(_fillColor), null, pixelBounds);
            if (!string.IsNullOrEmpty(_overlayText))
            {
                var contrast = (_fillColor.R + _fillColor.G + _fillColor.B) / 3 < 128 ? Colors.White : Colors.Black;
                var ft = new System.Windows.Media.FormattedText(
                    _overlayText, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Variable"), 10 * scale, new SolidColorBrush(contrast), 1.0)
                { MaxTextWidth = pixelBounds.Width, TextAlignment = TextAlignment.Center };
                dc.DrawText(ft, new Point(pixelBounds.X, pixelBounds.Y + (pixelBounds.Height - ft.Height) / 2));
            }
        }

        // Zapis realizuje PdfDocumentWriter (pdfSweep + overlay). Tu celowo pusto.
        public override void WriteToPdf(PdfWriterContext ctx) { }
    }
}
