using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Segmento.Editor;
using iTextAlign = iText.Layout.Properties.TextAlignment;

namespace Segmento.Editor.Annotations
{
    public enum StampKind { Text, Image }

    public sealed class StampAnnotation : AnnotationBase
    {
        private StampKind _kind = StampKind.Text;
        private byte[] _imageBytes = Array.Empty<byte>();
        private string _text = "";
        private string _preset = "";

        public StampKind Kind { get => _kind; set => Set(ref _kind, value); }
        public byte[] ImageBytes { get => _imageBytes; set => Set(ref _imageBytes, value ?? Array.Empty<byte>()); }
        public string Text { get => _text; set => Set(ref _text, value ?? ""); }
        public string Preset { get => _preset; set => Set(ref _preset, value ?? ""); }

        public static Color PresetColor(string preset) => preset switch
        {
            "Zatwierdzone" => Color.FromRgb(0x2E, 0x7D, 0x32),
            "Poufne" => Color.FromRgb(0xC6, 0x28, 0x28),
            "Wersja robocza" => Color.FromRgb(0xE8, 0x5E, 0x00),
            _ => Color.FromRgb(0x42, 0x42, 0x45)
        };

        public override AnnotationBase Clone()
        {
            var c = new StampAnnotation { _kind = _kind, _imageBytes = _imageBytes, _text = _text, _preset = _preset };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            if (_kind == StampKind.Image)
            {
                var img = TryDecode();
                if (img != null) dc.DrawImage(img, pixelBounds);
                return;
            }

            var color = PresetColor(_preset);
            var pen = new Pen(new SolidColorBrush(color), 2 * scale);
            dc.DrawRoundedRectangle(null, pen, pixelBounds, 4 * scale, 4 * scale);

            string txt = string.IsNullOrEmpty(_text) ? _preset : _text;
            if (string.IsNullOrEmpty(txt)) return;
            var ft = new System.Windows.Media.FormattedText(
                txt, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                Math.Max(8, pixelBounds.Height * 0.5), new SolidColorBrush(color), 1.0)
            { MaxTextWidth = pixelBounds.Width, TextAlignment = TextAlignment.Center };
            dc.DrawText(ft, new Point(pixelBounds.X, pixelBounds.Y + (pixelBounds.Height - ft.Height) / 2));
        }

        private System.Windows.Media.Imaging.BitmapImage? TryDecode()
        {
            if (_imageBytes.Length == 0) return null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using var ms = new System.IO.MemoryStream(_imageBytes);
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public override void WriteToPdf(PdfWriterContext ctx)
        {
            if (_kind == StampKind.Image)
            {
                ctx.DrawImage(BoundsPoints, _imageBytes, (float)Opacity);
                return;
            }

            var color = PdfWriterContext.Rgb(PresetColor(_preset));
            ctx.DrawRectangle(BoundsPoints, null, color, 2f, (float)Opacity);

            string txt = string.IsNullOrEmpty(_text) ? _preset : _text;
            if (string.IsNullOrEmpty(txt)) return;
            var font = ctx.Fonts.Get("Segoe UI", true, false);
            float size = (float)Math.Max(8, BoundsPoints.Height * 0.5);
            ctx.DrawText(BoundsPoints, txt, font, size, color, iTextAlign.CENTER, false, (float)Opacity, null, 2f);
        }
    }
}
