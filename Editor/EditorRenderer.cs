using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Segmento.Editor
{
    /// <summary>
    /// Renderuje bitmapy podkładu stron (PDFtoImage / dekodowanie obrazu) w tle,
    /// z cache LRU ograniczonym do 8 stron. Wynik zawsze Freeze() przed zwrotem.
    /// </summary>
    public sealed class EditorRenderer
    {
        private const int CacheLimit = 8;
        private const int MinWidth = 400;
        private const int MaxWidth = 2400;

        private readonly LinkedList<EditorPage> _lru = new();
        private readonly Dictionary<EditorPage, int> _cachedWidth = new();
        private readonly object _lock = new();

        /// <summary>Docelowa szerokość podkładu w px na podstawie szerokości ekranowej strony.</summary>
        public static int TargetWidth(double pageScreenWidthPx)
        {
            int w = (int)Math.Round(pageScreenWidthPx * 1.5);
            w = Math.Clamp(w, MinWidth, MaxWidth);
            return (w / 100) * 100; // kubełkowanie co 100 px — mniej re-renderów
        }

        /// <summary>Renderuje (lub zwraca z cache) podkład i ustawia go na stronie wraz z RenderDpi.</summary>
        public async Task<BitmapSource?> EnsureBackdropAsync(EditorPage page, int targetWidthPx)
        {
            lock (_lock)
            {
                if (page.Backdrop != null && _cachedWidth.TryGetValue(page, out int w) && w >= targetWidthPx)
                {
                    Touch(page);
                    return page.Backdrop;
                }
            }

            byte[] bytes = page.Source.SourceBytes;
            int pageIndex = page.Source.OriginalPageNumber - 1;
            bool isImage = page.IsImageSource;

            BitmapSource? bmp = await Task.Run(() =>
            {
                try
                {
                    return isImage
                        ? DecodeImage(bytes, targetWidthPx)
                        : RenderPdf(bytes, pageIndex, targetWidthPx);
                }
                catch { return null; }
            });

            if (bmp == null) return null;

            lock (_lock)
            {
                page.Backdrop = bmp;
                page.RenderDpi = (int)Math.Round(bmp.PixelWidth / page.WidthPoints * 72.0);
                _cachedWidth[page] = targetWidthPx;
                Touch(page);
                Evict();
            }
            return bmp;
        }

        public void Invalidate(EditorPage page)
        {
            lock (_lock)
            {
                page.Backdrop = null;
                _cachedWidth.Remove(page);
                _lru.Remove(page);
            }
        }

        private void Touch(EditorPage page)
        {
            _lru.Remove(page);
            _lru.AddLast(page);
        }

        private void Evict()
        {
            while (_lru.Count > CacheLimit)
            {
                var oldest = _lru.First!.Value;
                _lru.RemoveFirst();
                _cachedWidth.Remove(oldest);
                oldest.Backdrop = null;
            }
        }

        private static BitmapSource RenderPdf(byte[] pdfBytes, int pageIndex, int widthPx)
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            var opts = new PDFtoImage.RenderOptions { Width = widthPx, WithAspectRatio = true };
            using var skBitmap = PDFtoImage.Conversion.ToImage(pdfStream, page: (System.Index)pageIndex, options: opts);
            using var skImage = SkiaSharp.SKImage.FromBitmap(skBitmap);
            using var skData = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);
            return FromPng(skData.ToArray());
        }

        private static BitmapSource DecodeImage(byte[] imageBytes, int widthPx)
        {
            var src = new BitmapImage();
            using (var ms = new MemoryStream(imageBytes))
            {
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.DecodePixelWidth = widthPx;
                src.StreamSource = ms;
                src.EndInit();
            }
            src.Freeze();
            return src;
        }

        private static BitmapSource FromPng(byte[] png)
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
    }
}
