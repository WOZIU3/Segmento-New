using System;
using System.Windows;
using System.Windows.Media;
using Segmento.Editor;

namespace Segmento.Editor.Annotations
{
    public enum HighlightKind { Highlight, Underline, StrikeOut, Squiggly }

    /// <summary>Podświetlenie prostokątne po obszarze BoundsPoints (uproszczenie względem QuadPoints).</summary>
    public sealed class HighlightAnnotation : AnnotationBase
    {
        private Color _color = Colors.Yellow;
        private HighlightKind _kind = HighlightKind.Highlight;

        public Color Color { get => _color; set => Set(ref _color, value); }
        public HighlightKind Kind { get => _kind; set => Set(ref _kind, value); }

        public override AnnotationBase Clone()
        {
            var c = new HighlightAnnotation { _color = _color, _kind = _kind };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            var brush = new SolidColorBrush(_color);
            switch (_kind)
            {
                case HighlightKind.Highlight:
                    brush.Opacity = 0.4;
                    dc.DrawRectangle(brush, null, pixelBounds);
                    break;
                case HighlightKind.Underline:
                    DrawLineAt(dc, brush, scale, pixelBounds, pixelBounds.Bottom - 1 * scale);
                    break;
                case HighlightKind.StrikeOut:
                    DrawLineAt(dc, brush, scale, pixelBounds, pixelBounds.Y + pixelBounds.Height / 2);
                    break;
                case HighlightKind.Squiggly:
                    DrawSquiggly(dc, brush, scale, pixelBounds);
                    break;
            }
        }

        private static void DrawLineAt(DrawingContext dc, Brush b, double scale, Rect r, double y)
        {
            var pen = new Pen(b, 1.5 * scale);
            dc.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
        }

        private static void DrawSquiggly(DrawingContext dc, Brush b, double scale, Rect r)
        {
            var pen = new Pen(b, 1.0 * scale);
            double y = r.Bottom - 1 * scale, amp = 2 * scale, step = 3 * scale;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(r.Left, y), false, false);
                bool up = true;
                for (double x = r.Left; x < r.Right; x += step)
                { ctx.LineTo(new Point(x, up ? y - amp : y + amp), true, true); up = !up; }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        public override void WriteToPdf(PdfWriterContext ctx)
        {
            var color = PdfWriterContext.Rgb(_color);
            var b = BoundsPoints;
            switch (_kind)
            {
                case HighlightKind.Highlight:
                    ctx.DrawRectangle(b, color, null, 0, 0.4f);
                    break;
                case HighlightKind.Underline:
                    ctx.StrokePolyline(new[] { (b.Left, b.Bottom - 1), (b.Right, b.Bottom - 1) }, color, 1.5f, (float)Opacity, false, false);
                    break;
                case HighlightKind.StrikeOut:
                    double mid = b.Y + b.Height / 2;
                    ctx.StrokePolyline(new[] { (b.Left, mid), (b.Right, mid) }, color, 1.5f, (float)Opacity, false, false);
                    break;
                case HighlightKind.Squiggly:
                    ctx.StrokePolyline(new[] { (b.Left, b.Bottom - 1), (b.Right, b.Bottom - 1) }, color, 1.0f, (float)Opacity, false, false);
                    break;
            }
        }
    }
}
