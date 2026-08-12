using System.Collections.Generic;
using System.Windows.Media;

namespace Segmento.Editor
{
    public enum BatchTextPosition
    {
        TopLeft, TopCenter, TopRight,
        BottomLeft, BottomCenter, BottomRight
    }

    public sealed class WatermarkOptions
    {
        public string Text = "";
        public byte[]? Image;
        public Color Color = Color.FromRgb(0x9E, 0x9E, 0x9E);
        public double Opacity = 0.25;
        public double FontSize = 48;
        public double AngleDegrees = 45;
        public HashSet<int> Pages = new();   // 1-based; puste = wszystkie
    }

    public sealed class PageNumberOptions
    {
        public string Format = "{n}";        // tokeny {n} {total}
        public string Prefix = "";
        public string Suffix = "";
        public BatchTextPosition Position = BatchTextPosition.BottomCenter;
        public double FontSize = 10;
        public Color Color = Colors.Black;
        public bool SkipFirst = false;
        public HashSet<int> Pages = new();
    }

    public sealed class HeaderFooterOptions
    {
        public string HeaderText = "";
        public string FooterText = "";
        public double FontSize = 10;
        public Color Color = Colors.Black;
        public HashSet<int> Pages = new();
    }

    public sealed class EditorBatchSettings
    {
        public WatermarkOptions? Watermark;
        public PageNumberOptions? PageNumbers;
        public HeaderFooterOptions? HeaderFooter;

        public bool Any => Watermark != null || PageNumbers != null || HeaderFooter != null;

        public static bool InRange(HashSet<int> pages, int pageNumber)
            => pages == null || pages.Count == 0 || pages.Contains(pageNumber);
    }
}
