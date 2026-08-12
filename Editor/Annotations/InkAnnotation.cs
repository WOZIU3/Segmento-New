using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Segmento.Editor;

namespace Segmento.Editor.Annotations
{
    /// <summary>Rysunek odręczny. Punkty pociągnięć przechowywane w PUNKTACH PDF (nie w pikselach).</summary>
    public sealed class InkAnnotation : AnnotationBase
    {
        private Color _color = Colors.Black;
        private double _thicknessPoints = 1.5;
        private bool _isHighlighter;

        public StrokeCollection Strokes { get; private set; } = new StrokeCollection();
        public Color Color { get => _color; set => Set(ref _color, value); }
        public double ThicknessPoints { get => _thicknessPoints; set => Set(ref _thicknessPoints, Math.Max(0.1, value)); }
        public bool IsHighlighter { get => _isHighlighter; set => Set(ref _isHighlighter, value); }

        /// <summary>Blokada sprzężenia zwrotnego Strokes ↔ BoundsPoints.</summary>
        private bool _syncingGeometry;

        /// <summary>Przelicza BoundsPoints z zakresu wszystkich pociągnięć (w punktach).</summary>
        public void RecalculateBounds()
        {
            Rect r = Rect.Empty;
            foreach (var s in Strokes)
                foreach (var p in s.StylusPoints)
                {
                    var pt = new Point(p.X, p.Y);
                    if (r.IsEmpty) r = new Rect(pt, pt); else r.Union(pt);
                }
            if (!r.IsEmpty)
            {
                double half = _thicknessPoints / 2.0;
                r.Inflate(half, half);
            }
            _syncingGeometry = true;
            try { BoundsPoints = r; }
            finally { _syncingGeometry = false; }
        }

        /// <summary>Przesuwa/skaluje pociągnięcia razem z prostokątem obejmującym.</summary>
        protected override void OnBoundsChanged(Rect oldBounds, Rect newBounds)
        {
            if (_syncingGeometry || Strokes.Count == 0) return;
            if (oldBounds.IsEmpty || newBounds.IsEmpty) return;
            if (oldBounds.Width <= 0 || oldBounds.Height <= 0) return;

            var m = Matrix.Identity;
            m.Translate(-oldBounds.X, -oldBounds.Y);
            m.Scale(newBounds.Width / oldBounds.Width, newBounds.Height / oldBounds.Height);
            m.Translate(newBounds.X, newBounds.Y);

            foreach (var stroke in Strokes)
            {
                var pts = stroke.StylusPoints;
                for (int i = 0; i < pts.Count; i++)
                {
                    var sp = pts[i];
                    var q = m.Transform(new Point(sp.X, sp.Y));
                    sp.X = q.X; sp.Y = q.Y;
                    pts[i] = sp;
                }
            }
        }

        public override AnnotationBase Clone()
        {
            var c = new InkAnnotation
            {
                _color = _color, _thicknessPoints = _thicknessPoints, _isHighlighter = _isHighlighter,
                Strokes = Strokes.Clone()
            };
            CopyBaseTo(c);
            return c;
        }

        public override void Render(DrawingContext dc, Rect pixelBounds, double scale)
        {
            if (Strokes.Count == 0) return;

            var brush = new SolidColorBrush(_color) { Opacity = _isHighlighter ? 0.4 : 1.0 };
            var pen = new Pen(brush, _thicknessPoints * scale)
            {
                StartLineCap = _isHighlighter ? PenLineCap.Square : PenLineCap.Round,
                EndLineCap = _isHighlighter ? PenLineCap.Square : PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };

            foreach (var stroke in Strokes)
            {
                var pts = stroke.StylusPoints;
                if (pts.Count == 0) continue;

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(pts[0].X * scale, pts[0].Y * scale), false, false);
                    for (int i = 1; i < pts.Count; i++)
                        ctx.LineTo(new Point(pts[i].X * scale, pts[i].Y * scale), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }

        public override void WriteToPdf(PdfWriterContext ctx)
        {
            var color = PdfWriterContext.Rgb(_color);
            foreach (var stroke in Strokes)
            {
                var pts = new List<(double X, double Y)>(stroke.StylusPoints.Count);
                foreach (var sp in stroke.StylusPoints) pts.Add((sp.X, sp.Y));
                ctx.StrokePolyline(pts, color, (float)_thicknessPoints, (float)Opacity, _isHighlighter, false);
            }
        }
    }
}
