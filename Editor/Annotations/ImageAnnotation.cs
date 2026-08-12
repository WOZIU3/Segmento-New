using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Segmento.Editor;

namespace Segmento.Editor.Annotations
{
    public sealed class ImageAnnotation : AnnotationBase
    {
        private byte[] _imageBytes = Array.Empty<byte>();
        private bool _preserveAspect = true;
        private BitmapSource? _decoded;

        public byte[] ImageBytes
        {
            get => _imageBytes;
            set { if (Set(ref _imageBytes, value ?? Array.Empty<byte>())) _decoded = null; }
        }

        public bool PreserveAspect { get => _preserveAspect; set => Set(ref _preserveAspect, value); }

        /// <summary>Naturalny stosunek szerokości do wysokości (do zachowania proporcji przy skalowaniu).</summary>
        public double NaturalAspect
        {
            get { var d = GetDecoded(); return d != null && d.PixelHeight > 0 ? (double)d.PixelWidth / d.PixelHeight : 1.0; }
        }

        private BitmapSource? GetDecoded()
        {
            if (_decoded == null && _imageBytes.Length > 0)
            {
                try
                {
                    var bmp = new BitmapImage();
                    using var ms = new MemoryStream(_imageBytes);
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    _decoded = bmp;
                }
                catch { _decoded = null; }
            }
            return _decoded;
        }

        public override AnnotationBase Clone()
        {
            var c = new ImageAnnotation { _imageBytes = _imageBytes, _preserveAspect = _preserveAspect };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            var img = GetDecoded();
            if (img == null) return;
            dc.DrawImage(img, pixelBounds);
        }

        public override void WriteToPdf(PdfWriterContext ctx)
            => ctx.DrawImage(BoundsPoints, _imageBytes, (float)Opacity);
    }
}
