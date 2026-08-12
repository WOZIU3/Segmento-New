using System;
using System.Collections.Generic;
using System.IO;
using iText.IO.Font;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Layout.Element;
using iText.Layout.Properties;
using WpfColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;
using LayoutCanvas = iText.Layout.Canvas;

namespace Segmento.Editor
{
    /// <summary>
    /// Cache czcionek per dokument (PdfFont jest związany z dokumentem). Osadza TTF z IDENTITY_H
    /// dla poprawnej obsługi polskich znaków; fallback do Arial.
    /// </summary>
    public sealed class PdfFontCache
    {
        private readonly PdfDocument _doc;
        private readonly Dictionary<string, PdfFont> _cache = new();

        public PdfFontCache(PdfDocument doc) => _doc = doc;

        public PdfFont Get(string family, bool bold, bool italic)
        {
            string key = $"{family}|{bold}|{italic}";
            if (_cache.TryGetValue(key, out var f)) return f;

            PdfFont font;
            try
            {
                string? path = ResolvePath(family, bold, italic) ?? ResolvePath("Arial", bold, italic);
                if (path != null && File.Exists(path))
                    font = PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED, _doc);
                else
                    font = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            }
            catch
            {
                font = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            }
            _cache[key] = font;
            return font;
        }

        private static string? ResolvePath(string family, bool bold, bool italic)
        {
            string dir;
            try { dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); }
            catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;

            string f = (family ?? "").ToLowerInvariant();
            string[] names;
            if (f.Contains("segoe"))
                names = new[] { bi(bold, italic, "segoeui", "segoeuib", "segoeuii", "segoeuiz") };
            else if (f.Contains("times"))
                names = new[] { bi(bold, italic, "times", "timesbd", "timesi", "timesbi") };
            else if (f.Contains("calibri"))
                names = new[] { bi(bold, italic, "calibri", "calibrib", "calibrii", "calibriz") };
            else if (f.Contains("consol") || f.Contains("mono") || f.Contains("courier"))
                names = new[] { bi(bold, italic, "consola", "consolab", "consolai", "consolaz"), "cour" };
            else // Arial / domyślne
                names = new[] { bi(bold, italic, "arial", "arialbd", "ariali", "arialbi") };

            foreach (var n in names)
            {
                string p = Path.Combine(dir, n + ".ttf");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static string bi(bool b, bool i, string reg, string bold, string ital, string boldItal)
            => b && i ? boldItal : b ? bold : i ? ital : reg;
    }

    /// <summary>
    /// Kontekst zapisu jednej strony. Model trzyma współrzędne w pt PDF, origin lewy-górny, Y w dół;
    /// odbicie osi Y następuje WYŁĄCZNIE tutaj (ToPdfY).
    /// </summary>
    public sealed class PdfWriterContext
    {
        public PdfDocument Doc { get; }
        public PdfPage Page { get; }
        public PdfCanvas Canvas { get; }
        public double PageWidthPoints { get; }
        public double PageHeightPoints { get; }
        public PdfFontCache Fonts { get; }

        public PdfWriterContext(PdfDocument doc, PdfPage page, PdfCanvas canvas, double wPt, double hPt, PdfFontCache fonts)
        {
            Doc = doc; Page = page; Canvas = canvas;
            PageWidthPoints = wPt; PageHeightPoints = hPt; Fonts = fonts;
        }

        /// <summary>Górna współrzędna Y (model) → dolna współrzędna Y (PDF) dla prostokąta o wysokości h.</summary>
        public double ToPdfY(double yPoints, double heightPoints) => PageHeightPoints - (yPoints + heightPoints);

        /// <summary>Punkt modelu (origin lewy-górny) → punkt PDF (origin lewy-dolny).</summary>
        public Point ToPdfPoint(double x, double y) => new(x, PageHeightPoints - y);

        public static DeviceRgb Rgb(WpfColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        // ── Tekst ────────────────────────────────────────────────────────
        public void DrawText(WpfRect bounds, string text, PdfFont font, float sizePt, DeviceRgb color,
            TextAlignment align, bool underline, float opacity, WpfColor? background, float padding)
        {
            double pdfY = ToPdfY(bounds.Y, bounds.Height);

            if (background is WpfColor bg)
            {
                Canvas.SaveState();
                if (opacity < 1f) Canvas.SetExtGState(new PdfExtGState().SetFillOpacity(opacity));
                Canvas.SetFillColor(Rgb(bg))
                      .Rectangle(bounds.X, pdfY, bounds.Width, bounds.Height)
                      .Fill();
                Canvas.RestoreState();
            }

            if (string.IsNullOrEmpty(text)) return;

            var rect = new Rectangle(
                (float)(bounds.X + padding), (float)(pdfY + padding),
                (float)Math.Max(1, bounds.Width - 2 * padding), (float)Math.Max(1, bounds.Height - 2 * padding));

            var lc = new LayoutCanvas(Canvas, rect);
            var p = new Paragraph(text)
                .SetFont(font)
                .SetFontSize(sizePt)
                .SetFontColor(color)
                .SetTextAlignment(align)
                .SetMargin(0);
            if (underline) p.SetUnderline();
            if (opacity < 1f) p.SetOpacity(opacity);
            lc.Add(p);
            lc.Close();
        }

        // ── Obraz ────────────────────────────────────────────────────────
        public void DrawImage(WpfRect bounds, byte[] imageBytes, float opacity)
        {
            if (imageBytes == null || imageBytes.Length == 0) return;
            var img = ImageDataFactory.Create(imageBytes);
            double pdfY = ToPdfY(bounds.Y, bounds.Height);
            var rect = new Rectangle((float)bounds.X, (float)pdfY, (float)bounds.Width, (float)bounds.Height);

            Canvas.SaveState();
            if (opacity < 1f) Canvas.SetExtGState(new PdfExtGState().SetFillOpacity(opacity));
            Canvas.AddImageFittedIntoRectangle(img, rect, false);
            Canvas.RestoreState();
        }

        // ── Ścieżka (ink / kształt) ──────────────────────────────────────
        /// <summary>Rysuje polilinię z punktów modelu (pt, origin lewy-górny). closed→domknięta.</summary>
        public void StrokePolyline(IReadOnlyList<(double X, double Y)> pts, DeviceRgb stroke, float width,
            float opacity, bool highlighter, bool closed, float[]? dash = null)
        {
            if (pts.Count < 2) return;
            Canvas.SaveState();
            var gs = new PdfExtGState();
            if (highlighter) { gs.SetBlendMode(PdfName.Multiply); gs.SetStrokeOpacity(opacity <= 0 ? 0.4f : opacity); }
            else if (opacity < 1f) gs.SetStrokeOpacity(opacity);
            Canvas.SetExtGState(gs);
            Canvas.SetStrokeColor(stroke).SetLineWidth(width).SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND)
                  .SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);
            if (dash != null && dash.Length > 0) Canvas.SetLineDash(dash, 0);

            var p0 = ToPdfPoint(pts[0].X, pts[0].Y);
            Canvas.MoveTo(p0.x, p0.y);
            for (int i = 1; i < pts.Count; i++)
            {
                var p = ToPdfPoint(pts[i].X, pts[i].Y);
                Canvas.LineTo(p.x, p.y);
            }
            if (closed) Canvas.ClosePath();
            Canvas.Stroke();
            Canvas.RestoreState();
        }

        public void DrawRectangle(WpfRect bounds, DeviceRgb? fill, DeviceRgb? stroke, float width, float opacity, float[]? dash = null)
        {
            double pdfY = ToPdfY(bounds.Y, bounds.Height);
            Canvas.SaveState();
            if (opacity < 1f) Canvas.SetExtGState(new PdfExtGState().SetFillOpacity(opacity).SetStrokeOpacity(opacity));
            if (dash != null && dash.Length > 0) Canvas.SetLineDash(dash, 0);
            Canvas.Rectangle(bounds.X, pdfY, bounds.Width, bounds.Height);
            ApplyPaint(fill, stroke, width);
            Canvas.RestoreState();
        }

        public void DrawEllipse(WpfRect bounds, DeviceRgb? fill, DeviceRgb? stroke, float width, float opacity)
        {
            double pdfY = ToPdfY(bounds.Y, bounds.Height);
            Canvas.SaveState();
            if (opacity < 1f) Canvas.SetExtGState(new PdfExtGState().SetFillOpacity(opacity).SetStrokeOpacity(opacity));
            Canvas.Ellipse(bounds.X, pdfY, bounds.X + bounds.Width, pdfY + bounds.Height);
            ApplyPaint(fill, stroke, width);
            Canvas.RestoreState();
        }

        private void ApplyPaint(DeviceRgb? fill, DeviceRgb? stroke, float width)
        {
            if (fill != null) Canvas.SetFillColor(fill);
            if (stroke != null) Canvas.SetStrokeColor(stroke).SetLineWidth(width);
            if (fill != null && stroke != null) Canvas.FillStroke();
            else if (fill != null) Canvas.Fill();
            else if (stroke != null) Canvas.Stroke();
            else Canvas.EndPath();
        }
    }
}
