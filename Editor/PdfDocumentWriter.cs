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
        private static bool _cryptoReady;

        /// <summary>
        /// iText 8 rozwiazuje adapter kryptografii przez Type.GetType z nazwa assembly.
        /// Adapter nie jest referencjonowany z kodu, wiec przy publikacji single-file
        /// bywa niezaladowany — ladujemy go jawnie, zanim powstanie pierwszy dokument.
        /// </summary>
        private static void EnsureBouncyCastle()
        {
            if (_cryptoReady) return;

            foreach (var name in new[] { "itext.bouncy-castle-adapter", "itext7.bouncy-castle-adapter" })
            {
                try
                {
                    System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(name));
                    _cryptoReady = true;
                    return;
                }
                catch { }
            }

            throw new InvalidOperationException(
                "brak biblioteki itext.bouncy-castle-adapter — dodaj pakiet NuGet i przebuduj aplikację");
        }

        public static byte[] RenderPage(EditorPage page, int pageNumber, int totalPages, EditorBatchSettings? batch)
        {
            EnsureBouncyCastle();
            var redactions = page.Annotations.OfType<RedactAnnotation>().Where(a => a.IsVisible).ToList();
            byte[] bytes = RenderContent(page, pageNumber, totalPages, batch);

            // Redakcja wymaga dokumentu otwartego do odczytu i zapisu — osobny przebieg.
            if (redactions.Count > 0)
                bytes = ApplyRedactions(bytes, redactions);

            return bytes;
        }

        /// <summary>Przebieg 1: kopia strony źródłowej + rotacja + adnotacje + operacje wsadowe + kadr.</summary>
        private static byte[] RenderContent(EditorPage page, int pageNumber, int totalPages, EditorBatchSettings? batch)
        {
            using var outMs = new MemoryStream();
            var destDoc = new PdfDocument(new PdfWriter(outMs));
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
                    var reader = new PdfReader(new MemoryStream(page.Source.SourceBytes));
                    reader.SetUnethicalReading(true);      // źródła z hasłem właściciela
                    srcDoc = new PdfDocument(reader);
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

                var drawables = page.Annotations
                    .Where(a => a.IsVisible && a is not RedactAnnotation)
                    .OrderBy(a => a.ZIndex)
                    .ToList();
                bool batchActive = batch != null && batch.Any;

                // Dodatkowy strumień treści tylko wtedy, gdy jest co rysować.
                if (drawables.Count > 0 || batchActive)
                {
                    var fonts = new PdfFontCache(destDoc);
                    var canvas = new PdfCanvas(destPage.NewContentStreamAfter(), destPage.GetResources(), destDoc);
                    var ctx = new PdfWriterContext(destDoc, destPage, canvas, wPt, hPt, fonts);

                    foreach (var ann in drawables)
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

                    if (batchActive)
                        ApplyBatch(canvas, fonts, wPt, hPt, pageNumber, totalPages, batch!);

                    canvas.Release();
                }

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

        /// <summary>
        /// Przebieg 2: trwałe usunięcie treści pod obszarami redakcji (pdfSweep wymaga trybu
        /// stamping) i naniesienie napisu zastępczego już po czyszczeniu.
        /// </summary>
        private static byte[] ApplyRedactions(byte[] pdfBytes, List<RedactAnnotation> redactions)
        {
            using var inMs = new MemoryStream(pdfBytes);
            using var outMs = new MemoryStream();
            var doc = new PdfDocument(new PdfReader(inMs), new PdfWriter(outMs));

            try
            {
                var pdfPage = doc.GetPage(1);
                float hPt = pdfPage.GetPageSize().GetHeight();

                var locs = new List<PdfCleanUpLocation>();
                foreach (var r in redactions)
                {
                    var b = r.BoundsPoints;
                    float y = (float)(hPt - (b.Y + b.Height));
                    locs.Add(new PdfCleanUpLocation(1,
                        new Rectangle((float)b.X, y, (float)b.Width, (float)b.Height),
                        ColorConverter(r.FillColor)));
                }
                PdfCleaner.CleanUp(doc, locs, new CleanUpProperties());

                var overlays = redactions.Where(r => !string.IsNullOrEmpty(r.OverlayText)).ToList();
                if (overlays.Count > 0)
                {
                    var fonts = new PdfFontCache(doc);
                    var canvas = new PdfCanvas(pdfPage.NewContentStreamAfter(), pdfPage.GetResources(), doc);
                    var ctx = new PdfWriterContext(doc, pdfPage, canvas,
                        pdfPage.GetPageSize().GetWidth(), hPt, fonts);

                    foreach (var r in overlays)
                    {
                        var contrast = (r.FillColor.R + r.FillColor.G + r.FillColor.B) / 3 < 128
                            ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black;
                        ctx.DrawText(r.BoundsPoints, r.OverlayText, fonts.Get("Segoe UI", false, false), 10f,
                            PdfWriterContext.Rgb(contrast), TextAlignment.CENTER, false, 1f, null, 2f);
                    }
                    canvas.Release();
                }

                doc.Close();
                return outMs.ToArray();
            }
            catch
            {
                try { doc.Close(); } catch { }
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
