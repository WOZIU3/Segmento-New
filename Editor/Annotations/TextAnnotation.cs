using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Segmento.Editor;
using iTextAlign = iText.Layout.Properties.TextAlignment;

namespace Segmento.Editor.Annotations
{
    public sealed class TextAnnotation : AnnotationBase
    {
        private string _text = "";
        private string _fontFamily = "Segoe UI Variable";
        private double _fontSizePoints = 12;
        private Color _foreground = Colors.Black;
        private bool _bold, _italic, _underline;
        private TextAlignment _textAlignment = TextAlignment.Left;
        private Color? _background;
        private double _paddingPoints = 2;
        private bool _autoSize = true;

        public string Text { get => _text; set => Set(ref _text, value ?? ""); }
        public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
        public double FontSizePoints { get => _fontSizePoints; set => Set(ref _fontSizePoints, Math.Max(1, value)); }
        public Color Foreground { get => _foreground; set => Set(ref _foreground, value); }
        public bool Bold { get => _bold; set => Set(ref _bold, value); }
        public bool Italic { get => _italic; set => Set(ref _italic, value); }
        public bool Underline { get => _underline; set => Set(ref _underline, value); }
        public TextAlignment TextAlignment { get => _textAlignment; set => Set(ref _textAlignment, value); }
        public Color? Background { get => _background; set => Set(ref _background, value); }
        public double PaddingPoints { get => _paddingPoints; set => Set(ref _paddingPoints, Math.Max(0, value)); }
        public bool AutoSize { get => _autoSize; set => Set(ref _autoSize, value); }

        public override AnnotationBase Clone()
        {
            var c = new TextAnnotation
            {
                _text = _text, _fontFamily = _fontFamily, _fontSizePoints = _fontSizePoints,
                _foreground = _foreground, _bold = _bold, _italic = _italic, _underline = _underline,
                _textAlignment = _textAlignment, _background = _background,
                _paddingPoints = _paddingPoints, _autoSize = _autoSize
            };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            if (Background is Color bg)
                dc.DrawRectangle(new SolidColorBrush(bg), null, pixelBounds);

            if (string.IsNullOrEmpty(_text)) return;

            var typeface = new Typeface(
                new FontFamily(_fontFamily),
                _italic ? FontStyles.Italic : FontStyles.Normal,
                _bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

            double pad = _paddingPoints * scale;
            var brush = new SolidColorBrush(_foreground);

            var ft = new FormattedText(
                _text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, _fontSizePoints * scale, brush, 1.0)
            {
                MaxTextWidth = Math.Max(0, pixelBounds.Width - 2 * pad),
                MaxTextHeight = Math.Max(0, pixelBounds.Height - 2 * pad),
                TextAlignment = _textAlignment,
                Trimming = TextTrimming.None
            };
            if (_underline) ft.SetTextDecorations(TextDecorations.Underline);

            dc.DrawText(ft, new Point(pixelBounds.X + pad, pixelBounds.Y + pad));
        }

        public override void WriteToPdf(PdfWriterContext ctx)
        {
            var font = ctx.Fonts.Get(_fontFamily, _bold, _italic);
            var color = PdfWriterContext.Rgb(_foreground);
            var align = _textAlignment switch
            {
                TextAlignment.Center => iTextAlign.CENTER,
                TextAlignment.Right => iTextAlign.RIGHT,
                TextAlignment.Justify => iTextAlign.JUSTIFIED,
                _ => iTextAlign.LEFT
            };
            ctx.DrawText(BoundsPoints, _text, font, (float)_fontSizePoints, color, align,
                _underline, (float)Opacity, _background, (float)_paddingPoints);
        }
    }
}
