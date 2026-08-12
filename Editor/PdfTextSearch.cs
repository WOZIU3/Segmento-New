using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace Segmento.Editor
{
    public readonly struct SearchHit
    {
        public readonly int Page;             // 1-based (w obrębie pliku źródłowego)
        public readonly Rect BoundsPoints;    // model: origin lewy-górny, pt
        public readonly string Text;
        public SearchHit(int page, Rect bounds, string text) { Page = page; BoundsPoints = bounds; Text = text; }
    }

    public static class PdfTextSearch
    {
        /// <summary>Cały tekst strony (1-based) źródłowego pliku PDF.</summary>
        public static string ExtractPageText(byte[] pdfBytes, int page1Based)
        {
            try
            {
                using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)));
                if (page1Based < 1 || page1Based > doc.GetNumberOfPages()) return "";
                return PdfTextExtractor.GetTextFromPage(doc.GetPage(page1Based), new LocationTextExtractionStrategy());
            }
            catch { return ""; }
        }

        /// <summary>Znajduje wystąpienia frazy na stronie (1-based) i zwraca ich prostokąty w pt (model).</summary>
        public static List<SearchHit> Find(byte[] pdfBytes, int page1Based, string query, bool caseSensitive)
        {
            var hits = new List<SearchHit>();
            if (string.IsNullOrEmpty(query)) return hits;
            try
            {
                using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)));
                if (page1Based < 1 || page1Based > doc.GetNumberOfPages()) return hits;

                var page = doc.GetPage(page1Based);
                double h = page.GetPageSize().GetHeight();
                string pattern = (caseSensitive ? "" : "(?i)") + Regex.Escape(query);

                var strategy = new RegexBasedLocationExtractionStrategy(pattern);
                var processor = new PdfCanvasProcessor(strategy);
                processor.ProcessPageContent(page);

                foreach (var loc in strategy.GetResultantLocations())
                {
                    var r = loc.GetRectangle();
                    if (r == null) continue;
                    double y = h - (r.GetY() + r.GetHeight());
                    hits.Add(new SearchHit(page1Based, new Rect(r.GetX(), y, r.GetWidth(), r.GetHeight()), loc.GetText()));
                }
            }
            catch { }
            return hits;
        }
    }
}
