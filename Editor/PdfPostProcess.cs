using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using iText.Kernel.Pdf;

namespace Segmento.Editor
{
    public sealed class DocMetadata
    {
        public string? Title;
        public string? Author;
        public string? Subject;
        public string? Keywords;
        public bool Any => Title != null || Author != null || Subject != null || Keywords != null;
    }

    public sealed class SecurityOptions
    {
        public bool Enabled;
        public string UserPassword = "";
        public string OwnerPassword = "";
        public bool AllowPrint = true;
        public bool AllowCopy = true;
        public bool AllowModify = false;
    }

    public static class PdfPostProcess
    {
        /// <summary>Nakłada metadane i/lub szyfrowanie na gotowy (scalony) PDF. Zwraca nowe bajty.</summary>
        public static byte[] ApplyMetadataAndSecurity(byte[] pdf, DocMetadata? meta, SecurityOptions? sec)
        {
            if ((meta == null || !meta.Any) && (sec == null || !sec.Enabled))
                return pdf;

            var props = new WriterProperties();
            if (sec != null && sec.Enabled)
            {
                int perms = 0;
                if (sec.AllowPrint) perms |= EncryptionConstants.ALLOW_PRINTING;
                if (sec.AllowCopy) perms |= EncryptionConstants.ALLOW_COPY;
                if (sec.AllowModify) perms |= EncryptionConstants.ALLOW_MODIFY_CONTENTS;
                byte[] user = Encoding.UTF8.GetBytes(sec.UserPassword ?? "");
                string owner = string.IsNullOrEmpty(sec.OwnerPassword) ? (sec.UserPassword ?? "") : sec.OwnerPassword;
                props.SetStandardEncryption(user, Encoding.UTF8.GetBytes(owner), perms, EncryptionConstants.ENCRYPTION_AES_256);
            }

            using var outMs = new MemoryStream();
            var src = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            var dest = new PdfDocument(new PdfWriter(outMs, props));
            try
            {
                src.CopyPagesTo(1, src.GetNumberOfPages(), dest);
                if (meta != null && meta.Any)
                {
                    var info = dest.GetDocumentInfo();
                    if (meta.Title != null) info.SetTitle(meta.Title);
                    if (meta.Author != null) info.SetAuthor(meta.Author);
                    if (meta.Subject != null) info.SetSubject(meta.Subject);
                    if (meta.Keywords != null) info.SetKeywords(meta.Keywords);
                }
            }
            finally
            {
                dest.Close();
                src.Close();
            }
            return outMs.ToArray();
        }

        /// <summary>
        /// Eksportuje każdą stronę PDF do pliku PNG w podanym katalogu. Numerowanie zaczyna się od
        /// <paramref name="startNumber"/>, dzięki czemu można wołać po jednej stronie bez kolizji nazw.
        /// Zwraca liczbę zapisanych plików.
        /// </summary>
        public static int ExportPagesToPng(byte[] pdf, string directory, string baseName, int dpi, int startNumber = 1)
        {
            Directory.CreateDirectory(directory);
            var widthsPt = new List<double>();
            using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf))))
            {
                int n = doc.GetNumberOfPages();
                for (int i = 1; i <= n; i++)
                    widthsPt.Add(doc.GetPage(i).GetPageSize().GetWidth());
            }

            int count = 0;
            for (int i = 0; i < widthsPt.Count; i++)
            {
                int widthPx = Math.Max(1, (int)Math.Round(widthsPt[i] / 72.0 * dpi));
                using var stream = new MemoryStream(pdf);
                var opts = new PDFtoImage.RenderOptions { Width = widthPx, WithAspectRatio = true };
                using var sk = PDFtoImage.Conversion.ToImage(stream, page: (System.Index)i, options: opts);
                using var img = SkiaSharp.SKImage.FromBitmap(sk);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);
                File.WriteAllBytes(Path.Combine(directory, $"{baseName}_{startNumber + i}.png"), data.ToArray());
                count++;
            }
            return count;
        }
    }
}
