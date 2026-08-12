using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Segmento.Editor;

namespace Segmento.Editor.Annotations
{
    public enum ShapeKind { Rectangle, Ellipse, Line, Arrow, Polyline }

    public sealed class ShapeAnnotation : AnnotationBase
    {
        private ShapeKind _kind = ShapeKind.Rectangle;
        private Color _stroke = Colors.Red;
        private double _strokeThicknessPoints = 1.5;
        private Color? _fill;
        private bool _dashed;

        public ShapeKind Kind { get => _kind; set => Set(ref _kind, value); }
        public Color Stroke { get => _stroke; set => Set(ref _stroke, value); }
        public double StrokeThicknessPoints { get => _strokeThicknessPoints; set => Set(ref _strokeThicknessPoints, Math.Max(0.1, value)); }
        public Color? Fill { get => _fill; set => Set(ref _fill, value); }
        public bool Dashed { get => _dashed; set => Set(ref _dashed, value); }

        /// <summary>Wierzchołki linii/strzałki/łamanej w pt PDF (dla Rectangle/Ellipse nieużywane).</summary>
        public List<Point> Points { get; set; } = new();

        public bool UsesPoints => _kind is ShapeKind.Line or ShapeKind.Arrow or ShapeKind.Polyline;

        public void RecalculateBounds()
        {
            if (!UsesPoints || Points.Count == 0) return;
            Rect r = new(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            double half = _strokeThicknessPoints / 2.0;
            r.Inflate(half, half);
            BoundsPoints = r;
        }

        public override AnnotationBase Clone()
        {
            var c = new ShapeAnnotation
            {
                _kind = _kind, _stroke = _stroke, _strokeThicknessPoints = _strokeThicknessPoints,
                _fill = _fill, _dashed = _dashed, Points = new List<Point>(Points)
            };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            var brush = new SolidColorBrush(_stroke);
            var pen = new Pen(brush, _strokeThicknessPoints * scale) { LineJoin = PenLineJoin.Round };
            if (_dashed) pen.DashStyle = new DashStyle(new double[] { 4, 2 }, 0);
            Brush? fill = _fill.HasValue ? new SolidColorBrush(_fill.Value) : null;

            switch (_kind)
            {
                case ShapeKind.Rectangle:
                    dc.DrawRectangle(fill, pen, pixelBounds);
                    break;
                case ShapeKind.Ellipse:
                    dc.DrawEllipse(fill, pen,
                        new Point(pixelBounds.X + pixelBounds.Width / 2, pixelBounds.Y + pixelBounds.Height / 2),
                        pixelBounds.Width / 2, pixelBounds.Height / 2);
                    break;
                case ShapeKind.Line:
                    if (Points.Count >= 2)
                        dc.DrawLine(pen, S(Points[0], scale), S(Points[^1], scale));
                    break;
                case ShapeKind.Arrow:
                    if (Points.Count >= 2) DrawArrow(dc, pen, S(Points[0], scale), S(Points[^1], scale), scale);
                    break;
                case ShapeKind.Polyline:
                    for (int i = 1; i < Points.Count; i++)
                        dc.DrawLine(pen, S(Points[i - 1], scale), S(Points[i], scale));
                    break;
            }
        }

        private static Point S(Point p, double scale) => new(p.X * scale, p.Y * scale);

        private void DrawArrow(DrawingContext dc, Pen pen, Point a, Point b, double scale)
        {
            dc.DrawLine(pen, a, b);
            foreach (var barb in Arrowhead(a, b, 12 * scale))
                dc.DrawLine(pen, b, barb);
        }

        private static IEnumerable<Point> Arrowhead(Point a, Point b, double len)
        {
            var dir = b - a;
            double m = dir.Length; if (m < 1e-6) yield break;
            dir /= m;
            double ang = 25 * Math.PI / 180;
            for (int s = -1; s <= 1; s += 2)
            {
                double c = Math.Cos(s * ang), sn = Math.Sin(s * ang);
                var rot = new Vector(dir.X * c - dir.Y * sn, dir.X * sn + dir.Y * c);
                yield return b - rot * len;
            }
        }

        public override void WriteToPdf(PdfWriterContext ctx)
        {
            var stroke = PdfWriterContext.Rgb(_stroke);
            iText.Kernel.Colors.DeviceRgb? fill = _fill.HasValue ? PdfWriterContext.Rgb(_fill.Value) : null;
            float[]? dash = _dashed ? new float[] { 4, 2 } : null;
            float w = (float)_strokeThicknessPoints;

            switch (_kind)
            {
                case ShapeKind.Rectangle:
                    ctx.DrawRectangle(BoundsPoints, fill, stroke, w, (float)Opacity, dash);
                    break;
                case ShapeKind.Ellipse:
                    ctx.DrawEllipse(BoundsPoints, fill, stroke, w, (float)Opacity);
                    break;
                case ShapeKind.Line:
                    if (Points.Count >= 2)
                        ctx.StrokePolyline(new[] { P(Points[0]), P(Points[^1]) }, stroke, w, (float)Opacity, false, false, dash);
                    break;
                case ShapeKind.Arrow:
                    if (Points.Count >= 2)
                    {
                        var a = Points[0]; var b = Points[^1];
                        ctx.StrokePolyline(new[] { P(a), P(b) }, stroke, w, (float)Opacity, false, false, dash);
                        foreach (var barb in Arrowhead(a, b, 12))
                            ctx.StrokePolyline(new[] { P(b), P(barb) }, stroke, w, (float)Opacity, false, false);
                    }
                    break;
                case ShapeKind.Polyline:
                    if (Points.Count >= 2)
                        ctx.StrokePolyline(Points.Select(P).ToArray(), stroke, w, (float)Opacity, false, false, dash);
                    break;
            }
        }

        private static (double X, double Y) P(Point p) => (p.X, p.Y);
    }
}
