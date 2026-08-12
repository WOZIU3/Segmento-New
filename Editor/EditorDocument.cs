using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using iText.Kernel.Pdf;

namespace Segmento.Editor
{
    /// <summary>Dokument edycji zbudowany z wybranych stron (_organizePages). Trzyma historię i stan dirty.</summary>
    public sealed class EditorDocument : Observable
    {
        private EditorPage? _current;
        private bool _isDirty;

        public ObservableCollection<EditorPage> Pages { get; } = new();
        public UndoStack History { get; } = new();
        public EditorRenderer Renderer { get; } = new();

        public EditorPage? Current { get => _current; set => Set(ref _current, value); }
        public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

        /// <summary>Ustawienia operacji wsadowych (znak wodny, numeracja, nagłówek/stopka).</summary>
        public EditorBatchSettings Batch { get; set; } = new();

        public EditorDocument()
        {
            History.Changed += (_, _) => IsDirty = true;
        }

        public void MarkDirty() => IsDirty = true;
        public void MarkSaved() => IsDirty = false;

        /// <summary>
        /// Buduje dokument z listy stron (kolejność zachowana). Strony już wczytane są
        /// ponownie użyte — dzięki temu zmiana kolejności/zestawu stron w widoku „Organizuj”
        /// nie kasuje wykonanej pracy w edytorze.
        /// </summary>
        public void LoadFrom(IEnumerable<PageItem> pages)
        {
            var reuse = new Dictionary<PageItem, EditorPage>();
            foreach (var ep in Pages) reuse[ep.Source] = ep;

            Pages.Clear();
            History.Clear();

            // Cache otwartych dokumentów per bajty źródła — jeden odczyt na plik.
            var cache = new Dictionary<byte[], PdfDocument>();
            try
            {
                foreach (var p in pages)
                {
                    if (reuse.Remove(p, out var existing))
                    {
                        existing.IsDeleted = false;
                        Pages.Add(existing);
                        continue;
                    }

                    bool isImg = IsImageBytes(p.SourceBytes);
                    double wPt, hPt;

                    if (isImg)
                        (wPt, hPt) = ImageSizePoints(p.SourceBytes);
                    else
                        (wPt, hPt) = PdfPageSizePoints(cache, p.SourceBytes, p.OriginalPageNumber);

                    Pages.Add(new EditorPage(p, wPt, hPt, isImg));
                }
            }
            finally
            {
                foreach (var d in cache.Values) { try { d.Close(); } catch { } }
            }

            Current = Pages.FirstOrDefault(p => !p.IsDeleted) ?? Pages.FirstOrDefault();
            IsDirty = false;
        }

        /// <summary>
        /// Renderuje wszystkie niepomijane strony do jednostronicowych PDF-ów (wektorowo) i zwraca
        /// mapę PageItem→bajty. Strony bez zmian (brak adnotacji/rotacji/kadru i brak operacji wsadowych)
        /// są pomijane — eksport użyje wtedy źródła bez zmian.
        /// </summary>
        public Dictionary<PageItem, byte[]> Render(EditorBatchSettings? batch = null)
        {
            batch ??= Batch;
            bool batchActive = batch != null && batch.Any;
            var live = Pages.Where(p => !p.IsDeleted).ToList();
            int total = live.Count;
            var result = new Dictionary<PageItem, byte[]>();

            for (int i = 0; i < live.Count; i++)
            {
                var page = live[i];
                bool needs = page.Annotations.Count > 0 || page.Rotation != 0
                             || page.CropBoxPoints.HasValue || batchActive;
                if (!needs) continue;
                result[page.Source] = PdfDocumentWriter.RenderPage(page, i + 1, total, batch);
            }
            return result;
        }

        /// <summary>
        /// Zatwierdza wynik edycji do słownika edytowanych stron (_editedPages w MainWindow).
        /// Wpisy stron, które nie wymagają już renderowania (cofnięte zmiany, usunięte strony),
        /// są usuwane — inaczej eksport używałby nieaktualnej wersji.
        /// </summary>
        public void ApplyTo(IDictionary<PageItem, byte[]> editedPages, EditorBatchSettings? batch = null)
        {
            var rendered = Render(batch);
            foreach (var kv in rendered) editedPages[kv.Key] = kv.Value;
            foreach (var p in Pages)
                if (!rendered.ContainsKey(p.Source)) editedPages.Remove(p.Source);
        }

        /// <summary>Widoczny rozmiar strony PDF (CropBox z uwzględnieniem /Rotate) — jak w podglądzie.</summary>
        private static (double w, double h) PdfPageSizePoints(
            Dictionary<byte[], PdfDocument> cache, byte[] bytes, int pageNumber1Based)
        {
            try
            {
                if (!cache.TryGetValue(bytes, out var doc))
                {
                    doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
                    cache[bytes] = doc;
                }
                if (pageNumber1Based >= 1 && pageNumber1Based <= doc.GetNumberOfPages())
                {
                    var pg = doc.GetPage(pageNumber1Based);
                    return PdfPageSpace.VisibleSize(pg.GetCropBox(), pg.GetRotation());
                }
            }
            catch { }
            return (595.0, 842.0); // fallback A4
        }

        private static (double w, double h) ImageSizePoints(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var img = PdfSharp.Drawing.XImage.FromStream(ms);
                return (img.PointWidth, img.PointHeight);
            }
            catch { return (595.0, 842.0); }
        }

        private static bool IsImageBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return false;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true; // PNG
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true; // JPEG
            return false;
        }
    }
}
