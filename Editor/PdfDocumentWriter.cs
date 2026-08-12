using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Segmento.Editor.Annotations;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.PdfCleanup;
using LayoutCanvas = iText.Layout.Canvas;

namespace Segmento.Editor
{
    /// <summary>
    /// Renderuje pojedynczą stronę modelu do jednostronicowego PDF (wektorowo, bez rasteryzacji):
    /// kopiuje stronę źródłową zachowując warstwę tekstową, nakłada rotację/kadr, wykonuje redakcję
    /// (pdfSweep), rysuje adnotacje i operacje wsadowe. Wynik trafia do _editedPages i jest scalany
    /// przez istniejący ExportMergedPdf.
    /// </summary>
    public static class PdfDocumentWriter
    {
        public static byte[] RenderPage(EditorPage page, int pageNumber, int totalPages, EditorBatchSettings? batch)
        {
            using var outMs = new MemoryStream();
            var writer = new PdfWriter(outMs);
            var destDoc = new PdfDocument(writer);

            PdfDocument? srcDoc = null;
            try
            {
                double wPt = page.WidthPoints, hPt = page.HeightPoints;
                PdfPage destPage;

                if (page.IsImageSource || page.Source?.SourceBytes == null)
                {
                    destPage = destDoc.AddNewPage(new PageSize((float)wPt, (float)hPt));
                    var img = TryImage(page.Source?.SourceBytes);
                    if (img != null)
                    {
                        var canvas0 = new PdfCanvas(destPage);
                        canvas0.AddImageFittedIntoRectangle(img, new Rectangle(0, 0, (float)wPt, (float)hPt), false);
                    }
                }
                else
                {
                    srcDoc = new PdfDocument(new PdfReader(new MemoryStream(page.Source.SourceBytes)));
                    int idx = Math.Clamp(page.Source.OriginalPageNumber, 1, srcDoc.GetNumberOfPages());
                    srcDoc.CopyPagesTo(idx, idx, destDoc);
                    destPage = destDoc.GetPage(1);
                    wPt = destPage.GetPageSize().GetWidth();
                    hPt = destPage.GetPageSize().GetHeight();
                }

                // Rotacja (dodawana do istniejącej /Rotate strony)
                if (page.Rotation != 0)
                {
                    int cur = destPage.GetRotation();
                    destPage.SetRotation(((cur + page.Rotation) % 360 + 360) % 360);
                }

                var fonts = new PdfFontCache(destDoc);

                // Redakcja (pdfSweep) — raz, po skopiowaniu treści, przed rysowaniem adnotacji
                var redactions = page.Annotations.OfType<RedactAnnotation>().Where(a => a.IsVisible).ToList();
                if (redactions.Count > 0)
                {
                    var locs = new List<PdfCleanUpLocation>();
                    foreach (var r in redactions)
                    {
                        var b = r.BoundsPoints;
                        float y = (float)(hPt - (b.Y + b.Height));
                        locs.Add(new PdfCleanUpLocation(1, new Rectangle((float)b.X, y, (float)b.Width, (float)b.Height),
                            ColorConverter(r.FillColor)));
                    }
                    PdfCleaner.CleanUp(destDoc, locs, new CleanUpProperties());
                }

                // Świeży canvas po ewentualnym czyszczeniu
                var canvas = new PdfCanvas(destPage.NewContentStreamAfter(), destPage.GetResources(), destDoc);
                var ctx = new PdfWriterContext(destDoc, destPage, canvas, wPt, hPt, fonts);

                // Adnotacje (bez redakcji) wg ZIndex, z obsługą obrotu per obiekt
                foreach (var ann in page.Annotations.Where(a => a.IsVisible && a is not RedactAnnotation).OrderBy(a => a.ZIndex))
                {
                    bool rotated = Math.Abs(ann.RotationDegrees) > 0.01;
                    if (rotated)
                    {
                        var b = ann.BoundsPoints;
                        var center = ctx.ToPdfPoint(b.X + b.Width / 2, b.Y + b.Height / 2);
                        var at = AffineTransform.GetRotateInstance(-ann.RotationDegrees * Math.PI / 180.0, center.x, center.y);
                        canvas.SaveState();
                        canvas.ConcatMatrix(at);
                    }
                    ann.WriteToPdf(ctx);
                    if (rotated) canvas.RestoreState();
                }

                // Overlay redakcji (po czyszczeniu, na wierzchu)
                foreach (var r in redactions.Where(r => !string.IsNullOrEmpty(r.OverlayText)))
                {
                    var contrast = (r.FillColor.R + r.FillColor.G + r.FillColor.B) / 3 < 128
                        ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black;
                    var f = fonts.Get("Segoe UI", false, false);
                    ctx.DrawText(r.BoundsPoints, r.OverlayText, f, 10f, PdfWriterContext.Rgb(contrast),
                        TextAlignment.CENTER, false, 1f, null, 2f);
                }

                // Operacje wsadowe
                if (batch != null && batch.Any)
                    ApplyBatch(canvas, fonts, wPt, hPt, pageNumber, totalPages, batch);

                // Kadr (na końcu; przycina widok)
                if (page.CropBoxPoints is System.Windows.Rect crop && crop.Width > 0 && crop.Height > 0)
                {
                    float cy = (float)(hPt - (crop.Y + crop.Height));
                    destPage.SetCropBox(new Rectangle((float)crop.X, cy, (float)crop.Width, (float)crop.Height));
                }

                destDoc.Close();
                srcDoc?.Close();
                return outMs.ToArray();
            }
            catch
            {
                try { destDoc.Close(); } catch { }
                try { srcDoc?.Close(); } catch { }
                throw;
            }
        }

        private static void ApplyBatch(PdfCanvas canvas, PdfFontCache fonts, double wPt, double hPt,
            int pageNumber, int totalPages, EditorBatchSettings batch)
        {
            var pageRect = new Rectangle(0, 0, (float)wPt, (float)hPt);

            if (batch.Watermark is WatermarkOptions wm && EditorBatchSettings.InRange(wm.Pages, pageNumber))
            {
                if (wm.Image is byte[] wimg && wimg.Length > 0)
                {
                    var img = TryImage(wimg);
                    if (img != null)
                    {
                        float iw = (float)(wPt * 0.5), ih = iw * img.GetHeight() / img.GetWidth();
                        canvas.SaveState();
                        canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)wm.Opacity));
                        canvas.AddImageFittedIntoRectangle(img,
                            new Rectangle((float)(wPt - iw) / 2, (float)(hPt - ih) / 2, iw, ih), false);
                        canvas.RestoreState();
                    }
                }
                else if (!string.IsNullOrEmpty(wm.Text))
                {
                    canvas.SaveState();
                    canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)wm.Opacity));
                    var lc = new LayoutCanvas(canvas, pageRect);
                    lc.SetFont(fonts.Get("Segoe UI", true, false)).SetFontSize((float)wm.FontSize).SetFontColor(PdfWriterContext.Rgb(wm.Color));
                    lc.ShowTextAligned(wm.Text, (float)(wPt / 2), (float)(hPt / 2),
                        TextAlignment.CENTER, VerticalAlignment.MIDDLE, (float)(wm.AngleDegrees * Math.PI / 180.0));
                    lc.Close();
                    canvas.RestoreState();
                }
            }

            if (batch.PageNumbers is PageNumberOptions pn && EditorBatchSettings.InRange(pn.Pages, pageNumber)
                && !(pn.SkipFirst && pageNumber == 1))
            {
                string txt = pn.Prefix + pn.Format.Replace("{n}", pageNumber.ToString()).Replace("{total}", totalPages.ToString()) + pn.Suffix;
                var (x, y, align) = PositionPoint(pn.Position, wPt, hPt);
                var lc = new LayoutCanvas(canvas, pageRect);
                lc.SetFont(fonts.Get("Segoe UI", false, false)).SetFontSize((float)pn.FontSize).SetFontColor(PdfWriterContext.Rgb(pn.Color));
                lc.ShowTextAligned(txt, x, y, align, VerticalAlignment.MIDDLE, 0);
                lc.Close();
            }

            if (batch.HeaderFooter is HeaderFooterOptions hf && EditorBatchSettings.InRange(hf.Pages, pageNumber))
            {
                var font = fonts.Get("Segoe UI", false, false);
                var color = PdfWriterContext.Rgb(hf.Color);
                if (!string.IsNullOrEmpty(hf.HeaderText))
                {
                    var lc = new LayoutCanvas(canvas, pageRect);
                    lc.SetFont(font).SetFontSize((float)hf.FontSize).SetFontColor(color);
                    lc.ShowTextAligned(hf.HeaderText, (float)(wPt / 2), (float)(hPt - 24), TextAlignment.CENTER, VerticalAlignment.MIDDLE, 0);
                    lc.Close();
                }
                if (!string.IsNullOrEmpty(hf.FooterText))
                {
                    var lc = new LayoutCanvas(canvas, pageRect);
                    lc.SetFont(font).SetFontSize((float)hf.FontSize).SetFontColor(color);
                    lc.ShowTextAligned(hf.FooterText, (float)(wPt / 2), 24, TextAlignment.CENTER, VerticalAlignment.MIDDLE, 0);
                    lc.Close();
                }
            }
        }

        private static (float x, float y, TextAlignment align) PositionPoint(BatchTextPosition pos, double w, double h)
        {
            const float m = 24;
            return pos switch
            {
                BatchTextPosition.TopLeft => (m, (float)h - m, TextAlignment.LEFT),
                BatchTextPosition.TopCenter => ((float)w / 2, (float)h - m, TextAlignment.CENTER),
                BatchTextPosition.TopRight => ((float)w - m, (float)h - m, TextAlignment.RIGHT),
                BatchTextPosition.BottomLeft => (m, m, TextAlignment.LEFT),
                BatchTextPosition.BottomRight => ((float)w - m, m, TextAlignment.RIGHT),
                _ => ((float)w / 2, m, TextAlignment.CENTER)
            };
        }

        private static ImageData? TryImage(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try { return ImageDataFactory.Create(bytes); } catch { return null; }
        }

        private static Color ColorConverter(System.Windows.Media.Color c) => new DeviceRgb(c.R / 255f, c.G / 255f, c.B / 255f);
    }
}
