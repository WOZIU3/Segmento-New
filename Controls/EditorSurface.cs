using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Segmento.Editor;
using Segmento.Editor.Annotations;

namespace Segmento.Controls
{
    public enum SurfaceTool
    {
        Select, Text, Image, Ink, Highlighter,
        Rectangle, Ellipse, Line, Arrow, Polyline, Highlight, Redact
    }

    /// <summary>
    /// Powierzchnia edycji jednej strony. Rysuje podkład + adnotacje w OnRender, obsługuje
    /// zaznaczanie, transformacje (8 uchwytów + obrót), marquee, snap i klawiaturę.
    /// Wszystkie modyfikacje przez komendy z EditorCommands. Współrzędne modelu w pt PDF;
    /// przeliczenie pt↔px ekranu wyłącznie tutaj (P = pt * Scale).
    /// </summary>
    public sealed class EditorSurface : Canvas
    {
        // --- Zależności zewnętrzne ---
        private EditorDocument? _doc;
        private EditorPage? _page;
        private EditorSelection _selection = new();

        // --- Widok ---
        private double _scale = 1.0;                 // px ekranu na 1 pt
        private SurfaceTool _tool = SurfaceTool.Select;

        // --- Stan interakcji ---
        private enum DragMode { None, Move, Resize, Rotate, Marquee, DrawText, DrawInk, DrawRect, DrawLine }
        private DragMode _drag = DragMode.None;
        private Point _dragStartScreen;
        private Point _dragStartPt;                  // w punktach
        private int _resizeHandle = -1;              // 0..7
        private Rect _marqueeScreen;
        private readonly List<(AnnotationBase Ann, Rect Bounds, double Rot)> _dragSnapshot = new();
        private Rect _selBoundsAtDragStart;
        private Stroke? _liveStroke;
        private InkAnnotation? _liveInk;
        private readonly List<(double X1, double Y1, double X2, double Y2)> _guides = new();

        // --- Edycja tekstu ---
        private TextBox? _editBox;
        private TextAnnotation? _editing;

        private Point _dragCurrentScreen;
        private readonly List<Point> _polyPoints = new();   // punkty łamanej w pt PDF

        // Domyślne parametry nowo tworzonych obiektów (ustawiane przez host z paska narzędzi)
        public Color NewStroke = Colors.Red;
        public Color? NewFill = null;
        public double NewThickness = 1.5;
        public bool NewDashed = false;
        public Color NewHighlightColor = Colors.Yellow;
        public HighlightKind NewHighlightKind = HighlightKind.Highlight;
        public Color NewRedactFill = Colors.Black;
        public string NewRedactOverlay = "";

        private const double HandleSize = 8;
        private const double RotateOffset = 24;
        private const double SnapPx = 6;

        // Macierz pt strony -> px ekranu (skala + obrot strony) i odwrotna.
        private Matrix _toScreen = Matrix.Identity;
        private Matrix _toPage = Matrix.Identity;

        public event EventHandler? SelectionChanged;
        public event EventHandler? ContentChanged;
        /// <summary>Narzedzie zakonczylo prace (prawy przycisk myszy) - host wraca do Zaznacz.</summary>
        public event EventHandler? ToolFinished;

        public EditorSurface()
        {
            Background = Brushes.Transparent;
            Focusable = true;
            ClipToBounds = false;
            SnapsToDevicePixels = true;
            _selection.Changed += (_, _) => { InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty); };
        }

        // ── Konfiguracja ─────────────────────────────────────────────────

        public EditorSelection Selection => _selection;
        public SurfaceTool CurrentTool
        {
            get => _tool;
            set
            {
                _tool = value; CommitEdit();
                if (_polyPoints.Count > 0) { _polyPoints.Clear(); InvalidateVisual(); }
                Cursor = _tool == SurfaceTool.Select ? Cursors.Arrow : Cursors.Cross;
            }
        }

        public double Scale
        {
            get => _scale;
            set { double v = Math.Clamp(value, 0.1, 8.0); if (Math.Abs(v - _scale) < 1e-6) return; _scale = v; UpdateSize(); InvalidateVisual(); }
        }

        public void Attach(EditorDocument doc) => _doc = doc;

        public void SetPage(EditorPage? page)
        {
            CommitEdit();
            if (_page != null) { _page.PropertyChanged -= Page_PropertyChanged; HookAnnotations(_page, false); }
            _page = page;
            if (_page != null) { _page.PropertyChanged += Page_PropertyChanged; HookAnnotations(_page, true); }
            _selection.Clear();
            UpdateSize();
            InvalidateVisual();
        }

        /// <summary>
        /// Nasluch zmian adnotacji — panel warstw i wlasciwosci moga zmieniac model bezposrednio
        /// (widocznosc, blokada, przezroczystosc), a podglad ma sie odswiezyc natychmiast.
        /// </summary>
        private void HookAnnotations(EditorPage page, bool on)
        {
            if (on)
            {
                page.Annotations.CollectionChanged += Annotations_CollectionChanged;
                foreach (var a in page.Annotations) a.PropertyChanged += Annotation_PropertyChanged;
            }
            else
            {
                page.Annotations.CollectionChanged -= Annotations_CollectionChanged;
                foreach (var a in page.Annotations) a.PropertyChanged -= Annotation_PropertyChanged;
            }
        }

        private void Annotations_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (AnnotationBase a in e.OldItems) a.PropertyChanged -= Annotation_PropertyChanged;
            if (e.NewItems != null)
                foreach (AnnotationBase a in e.NewItems) a.PropertyChanged += Annotation_PropertyChanged;
            InvalidateVisual();
        }

        private void Annotation_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            => InvalidateVisual();

        private void Page_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorPage.Rotation))
            {
                CommitEdit();
                UpdateSize();
                InvalidateVisual();
            }
            else if (e.PropertyName == nameof(EditorPage.Backdrop))
            {
                InvalidateVisual();
            }
        }

        public EditorPage? Page => _page;

        /// <summary>Wymusza przerysowanie (np. po asynchronicznym renderze podkładu).</summary>
        public void Refresh() => InvalidateVisual();

        private void UpdateSize()
        {
            if (_page == null)
            {
                Width = 0; Height = 0;
                _toScreen = Matrix.Identity; _toPage = Matrix.Identity;
                return;
            }

            Width = _page.DisplayWidthPoints * _scale;
            Height = _page.DisplayHeightPoints * _scale;

            var m = new Matrix();
            m.Scale(_scale, _scale);
            m.Append(RotationMatrix());
            _toScreen = m;
            _toPage = m;
            _toPage.Invert();
        }

        /// <summary>Obrot strony w przestrzeni juz przeskalowanej (bez skali).</summary>
        private Matrix RotationMatrix()
        {
            var m = Matrix.Identity;
            if (_page == null) return m;
            switch (_page.Rotation)
            {
                case 90:
                    m.Rotate(90);
                    m.Translate(_page.HeightPoints * _scale, 0);
                    break;
                case 180:
                    m.Rotate(180);
                    m.Translate(_page.WidthPoints * _scale, _page.HeightPoints * _scale);
                    break;
                case 270:
                    m.Rotate(270);
                    m.Translate(0, _page.WidthPoints * _scale);
                    break;
            }
            return m;
        }

        // ── Konwersje ────────────────────────────────────────────────────

        /// <summary>Punkty strony → rzeczywiste px ekranu (z obrotem strony).</summary>
        private Point ToScreen(Point pt) => _toScreen.Transform(pt);
        private Point ToPoints(Point screen) => _toPage.Transform(screen);

        /// <summary>Prostokat w rzeczywistych px ekranu (z obrotem) — chrom zaznaczenia, hit-test.</summary>
        private Rect ScreenRect(Rect ptRect) => new(ToScreen(ptRect.TopLeft), ToScreen(ptRect.BottomRight));

        /// <summary>Prostokat w przestrzeni przeskalowanej strony (bez obrotu) — render adnotacji.</summary>
        private Rect ScaleRect(Rect ptRect) => new(ptRect.X * _scale, ptRect.Y * _scale, ptRect.Width * _scale, ptRect.Height * _scale);

        // ── Render ───────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (_page == null) return;

            var pageScreen = new Rect(0, 0, _page.WidthPoints * _scale, _page.HeightPoints * _scale);

            // Zawartosc strony rysowana w przestrzeni strony; obrot nakladany jednym transformem.
            dc.PushTransform(new MatrixTransform(RotationMatrix()));

            // Podkład (biały pod spodem, żeby przezroczyste PDF nie mrugały)
            dc.DrawRectangle(Brushes.White, null, pageScreen);
            if (_page.Backdrop != null)
                dc.DrawImage(_page.Backdrop, pageScreen);

            // Adnotacje wg ZIndex
            foreach (var ann in _page.Annotations.OrderBy(a => a.ZIndex))
            {
                if (!ann.IsVisible) continue;
                var r = ScaleRect(ann.BoundsPoints);
                bool pushed = false;
                if (Math.Abs(ann.RotationDegrees) > 0.01)
                {
                    dc.PushTransform(new RotateTransform(ann.RotationDegrees, r.X + r.Width / 2, r.Y + r.Height / 2));
                    pushed = true;
                }
                if (ann.Opacity < 1.0) { dc.PushOpacity(ann.Opacity); }
                ann.Render(dc, r, _scale);
                if (ann.Opacity < 1.0) dc.Pop();
                if (pushed) dc.Pop();
            }

            // Rysowany na żywo ink (jeszcze nie w Annotations)
            if (_liveInk != null)
                _liveInk.Render(dc, ScaleRect(_liveInk.BoundsPoints), _scale);

            dc.Pop();

            // Prowadnice snap
            var guidePen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x5E, 0x00)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            guidePen.Freeze();
            foreach (var g in _guides)
                dc.DrawLine(guidePen, new Point(g.X1, g.Y1), new Point(g.X2, g.Y2));

            // Chrom zaznaczenia
            if (!_selection.IsEmpty)
                DrawSelectionChrome(dc);

            // Podgląd zaznaczenia obszarem / tworzenia prostokąta / tekstu
            if (_drag is DragMode.Marquee or DragMode.DrawText or DragMode.DrawRect)
            {
                var fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212));
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 212)), 1) { DashStyle = new DashStyle(new double[] { 4, 2 }, 0) };
                dc.DrawRectangle(fill, pen, _marqueeScreen);
            }

            // Podgląd linii/strzałki
            if (_drag == DragMode.DrawLine)
            {
                var pen = new Pen(new SolidColorBrush(NewStroke), NewThickness * _scale);
                dc.DrawLine(pen, _dragStartScreen, _dragCurrentScreen);
            }

            // Podgląd łamanej
            if (_tool == SurfaceTool.Polyline && _polyPoints.Count > 0)
            {
                var pen = new Pen(new SolidColorBrush(NewStroke), NewThickness * _scale);
                for (int i = 1; i < _polyPoints.Count; i++)
                    dc.DrawLine(pen, ToScreen(_polyPoints[i - 1]), ToScreen(_polyPoints[i]));
                dc.DrawLine(pen, ToScreen(_polyPoints[^1]), _dragCurrentScreen);
            }
        }

        private void DrawSelectionChrome(DrawingContext dc)
        {
            var b = ScreenRect(_selection.BoundsPoints);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(74, 144, 226)), 1);
            pen.Freeze();
            dc.DrawRectangle(null, pen, b);

            var fill = new SolidColorBrush(Color.FromRgb(74, 144, 226));
            var stroke = new Pen(Brushes.White, 1);
            foreach (var h in HandlePoints(b))
                dc.DrawEllipse(fill, stroke, h, HandleSize / 2, HandleSize / 2);

            // Uchwyt obrotu
            var rot = new Point(b.X + b.Width / 2, b.Y - RotateOffset);
            dc.DrawLine(pen, new Point(b.X + b.Width / 2, b.Y), rot);
            dc.DrawEllipse(fill, stroke, rot, HandleSize / 2, HandleSize / 2);
        }

        // Kolejność: 0=NW 1=N 2=NE 3=E 4=SE 5=S 6=SW 7=W
        private static Point[] HandlePoints(Rect b) => new[]
        {
            new Point(b.Left, b.Top), new Point(b.Left + b.Width / 2, b.Top), new Point(b.Right, b.Top),
            new Point(b.Right, b.Top + b.Height / 2), new Point(b.Right, b.Bottom),
            new Point(b.Left + b.Width / 2, b.Bottom), new Point(b.Left, b.Bottom),
            new Point(b.Left, b.Top + b.Height / 2)
        };

        // ── Hit-test ─────────────────────────────────────────────────────

        private AnnotationBase? HitAnnotation(Point screen)
        {
            if (_page == null) return null;
            foreach (var ann in _page.Annotations.Where(a => a.IsVisible && !a.IsLocked).OrderByDescending(a => a.ZIndex))
            {
                var r = ScreenRect(ann.BoundsPoints);
                var test = screen;
                if (Math.Abs(ann.RotationDegrees) > 0.01)
                {
                    double ang = ann.RotationDegrees + (_page?.Rotation ?? 0);
                    var m = new RotateTransform(-ang, r.X + r.Width / 2, r.Y + r.Height / 2).Value;
                    test = m.Transform(screen);
                }
                if (r.Contains(test)) return ann;
            }
            return null;
        }

        private int HitHandle(Point screen)
        {
            if (_selection.IsEmpty) return -1;
            var b = ScreenRect(_selection.BoundsPoints);
            var pts = HandlePoints(b);
            for (int i = 0; i < pts.Length; i++)
                if ((screen - pts[i]).Length <= HandleSize) return i;
            return -1;
        }

        private bool HitRotate(Point screen)
        {
            if (_selection.IsEmpty) return false;
            var b = ScreenRect(_selection.BoundsPoints);
            var rot = new Point(b.X + b.Width / 2, b.Y - RotateOffset);
            return (screen - rot).Length <= HandleSize;
        }

        // ── Mysz ─────────────────────────────────────────────────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (_page == null) return;
            Focus();
            var screen = e.GetPosition(this);
            _dragStartScreen = screen;
            _dragStartPt = ToPoints(screen);

            switch (_tool)
            {
                case SurfaceTool.Text: BeginDrawText(screen); break;
                case SurfaceTool.Ink:
                case SurfaceTool.Highlighter: BeginInk(screen); break;
                case SurfaceTool.Rectangle:
                case SurfaceTool.Ellipse:
                case SurfaceTool.Highlight:
                case SurfaceTool.Redact: BeginDrawRect(screen); break;
                case SurfaceTool.Line:
                case SurfaceTool.Arrow: BeginDrawLine(screen); break;
                case SurfaceTool.Polyline: PolylineClick(screen, e.ClickCount); e.Handled = true; return;
                default: BeginSelectOrTransform(screen, e); break;
            }
            CaptureMouse();
            e.Handled = true;
        }

        private void BeginSelectOrTransform(Point screen, MouseButtonEventArgs e)
        {
            if (HitRotate(screen)) { _drag = DragMode.Rotate; SnapshotDrag(); return; }
            int h = HitHandle(screen);
            if (h >= 0) { _drag = DragMode.Resize; _resizeHandle = h; _selBoundsAtDragStart = _selection.BoundsPoints; SnapshotDrag(); return; }

            var hit = HitAnnotation(screen);
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (hit != null)
            {
                if (ctrl) _selection.Toggle(hit);
                else if (!_selection.Contains(hit)) _selection.Set(hit);
                if (!_selection.IsEmpty) { _drag = DragMode.Move; SnapshotDrag(); }
            }
            else
            {
                if (!ctrl) _selection.Clear();
                _drag = DragMode.Marquee;
                _marqueeScreen = new Rect(screen, screen);
            }
        }

        private void SnapshotDrag()
        {
            _dragSnapshot.Clear();
            foreach (var a in _selection.Items) _dragSnapshot.Add((a, a.BoundsPoints, a.RotationDegrees));
        }

        /// <summary>
        /// Prawy przycisk: zapisuje trwajaca operacje (lamana, tekst) i zglasza
        /// zakonczenie narzedzia — host przelacza na Zaznacz.
        /// </summary>
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (_tool == SurfaceTool.Select) return;

            if (IsMouseCaptured) ReleaseMouseCapture();
            CommitEdit();
            if (_tool == SurfaceTool.Polyline) CommitPolyline();
            _polyPoints.Clear();
            _drag = DragMode.None;
            _guides.Clear();
            InvalidateVisual();
            ToolFinished?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_tool == SurfaceTool.Polyline && _polyPoints.Count > 0)
            { _dragCurrentScreen = e.GetPosition(this); InvalidateVisual(); return; }
            if (_page == null || _drag == DragMode.None) { UpdateCursor(e.GetPosition(this)); return; }
            var screen = e.GetPosition(this);

            switch (_drag)
            {
                case DragMode.Move: DoMove(screen); break;
                case DragMode.Resize: DoResize(screen); break;
                case DragMode.Rotate: DoRotate(screen); break;
                case DragMode.Marquee: _marqueeScreen = new Rect(_dragStartScreen, screen); InvalidateVisual(); break;
                case DragMode.DrawText:
                case DragMode.DrawRect: _marqueeScreen = new Rect(_dragStartScreen, screen); InvalidateVisual(); break;
                case DragMode.DrawLine: _dragCurrentScreen = screen; InvalidateVisual(); break;
                case DragMode.DrawInk: DoInk(screen); break;
            }
        }

        private void UpdateCursor(Point screen)
        {
            if (_tool != SurfaceTool.Select) return;
            if (HitRotate(screen)) { Cursor = Cursors.Hand; return; }
            int h = HitHandle(screen);
            if (h >= 0)
            {
                Cursor = (h == 0 || h == 4) ? Cursors.SizeNWSE
                    : (h == 2 || h == 6) ? Cursors.SizeNESW
                    : (h == 1 || h == 5) ? Cursors.SizeNS : Cursors.SizeWE;
                return;
            }
            Cursor = HitAnnotation(screen) != null ? Cursors.SizeAll : Cursors.Arrow;
        }

        private void DoMove(Point screen)
        {
            double dxPt = (screen.X - _dragStartScreen.X) / _scale;
            double dyPt = (screen.Y - _dragStartScreen.Y) / _scale;

            // Snap bounding boxa zaznaczenia
            _guides.Clear();
            var startBounds = SnapshotBounds();
            var moved = new Rect(startBounds.X + dxPt, startBounds.Y + dyPt, startBounds.Width, startBounds.Height);
            var (sdx, sdy) = ComputeSnap(moved);
            dxPt += sdx; dyPt += sdy;

            foreach (var (ann, bounds, _) in _dragSnapshot)
                ann.BoundsPoints = new Rect(bounds.X + dxPt, bounds.Y + dyPt, bounds.Width, bounds.Height);
            InvalidateVisual();
        }

        private Rect SnapshotBounds()
        {
            Rect r = Rect.Empty;
            foreach (var (_, b, _) in _dragSnapshot) if (r.IsEmpty) r = b; else r.Union(b);
            return r;
        }

        private void DoResize(Point screen)
        {
            var pt = ToPoints(screen);
            var start = _selBoundsAtDragStart;
            double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

            switch (_resizeHandle)
            {
                case 0: left = pt.X; top = pt.Y; break;
                case 1: top = pt.Y; break;
                case 2: right = pt.X; top = pt.Y; break;
                case 3: right = pt.X; break;
                case 4: right = pt.X; bottom = pt.Y; break;
                case 5: bottom = pt.Y; break;
                case 6: left = pt.X; bottom = pt.Y; break;
                case 7: left = pt.X; break;
            }

            double nw = Math.Max(4, right - left), nh = Math.Max(4, bottom - top);
            bool proportional = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (proportional && start.Width > 0 && start.Height > 0)
            {
                double ar = start.Width / start.Height;
                if (nw / nh > ar) nw = nh * ar; else nh = nw / ar;
                if (_resizeHandle is 0 or 6 or 7) left = right - nw;
                if (_resizeHandle is 0 or 1 or 2) top = bottom - nh;
            }
            var newBounds = new Rect(left, top, nw, nh);
            ScaleSelectionTo(newBounds);
            InvalidateVisual();
        }

        private void ScaleSelectionTo(Rect target)
        {
            var start = _selBoundsAtDragStart;
            if (start.Width <= 0 || start.Height <= 0) return;
            double sx = target.Width / start.Width, sy = target.Height / start.Height;
            foreach (var (ann, bounds, _) in _dragSnapshot)
            {
                double nx = target.X + (bounds.X - start.X) * sx;
                double ny = target.Y + (bounds.Y - start.Y) * sy;
                ann.BoundsPoints = new Rect(nx, ny, bounds.Width * sx, bounds.Height * sy);
            }
        }

        private void DoRotate(Point screen)
        {
            var b = _selection.BoundsPoints;
            var centerScreen = ToScreen(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            double ang = Math.Atan2(screen.Y - centerScreen.Y, screen.X - centerScreen.X) * 180 / Math.PI + 90;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ang = Math.Round(ang / 15) * 15;
            foreach (var (ann, _, _) in _dragSnapshot) ann.RotationDegrees = ang;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
            _guides.Clear();

            switch (_drag)
            {
                case DragMode.Move:
                case DragMode.Resize:
                case DragMode.Rotate:
                    CommitTransform();
                    break;
                case DragMode.Marquee:
                    CommitMarquee();
                    break;
                case DragMode.DrawText:
                    CommitDrawText(e.GetPosition(this));
                    break;
                case DragMode.DrawRect:
                    CommitDrawRect(e.GetPosition(this));
                    break;
                case DragMode.DrawLine:
                    CommitDrawLine(e.GetPosition(this));
                    break;
                case DragMode.DrawInk:
                    CommitInk();
                    break;
            }
            _drag = DragMode.None;
            _resizeHandle = -1;
            InvalidateVisual();
        }

        private void CommitTransform()
        {
            if (_dragSnapshot.Count == 0 || _doc == null) return;
            var states = _dragSnapshot
                .Select(t => new TransformAnnotationsCommand.State(t.Ann, t.Bounds, t.Rot, t.Ann.BoundsPoints, t.Ann.RotationDegrees))
                .Where(s => s.OldBounds != s.NewBounds || Math.Abs(s.OldRotation - s.NewRotation) > 0.001)
                .ToList();
            _dragSnapshot.Clear();
            if (states.Count == 0) return;
            // Ustaw stan startowy, komenda przez Do() nałoży docelowy
            foreach (var s in states) { s.Ann.BoundsPoints = s.OldBounds; s.Ann.RotationDegrees = s.OldRotation; }
            _doc.History.Push(new TransformAnnotationsCommand(states));
            RaiseContentChanged();
        }

        private void CommitMarquee()
        {
            if (_page == null) return;
            var ptRect = new Rect(ToPoints(_marqueeScreen.TopLeft), ToPoints(_marqueeScreen.BottomRight));
            var hits = _page.Annotations.Where(a => a.IsVisible && !a.IsLocked && ptRect.IntersectsWith(a.BoundsPoints)).ToList();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                foreach (var a in hits) _selection.Add(a);
            else
                _selection.SetRange(hits);
        }

        // ── Narzędzie: tekst ─────────────────────────────────────────────

        private void BeginDrawText(Point screen) { _drag = DragMode.DrawText; _marqueeScreen = new Rect(screen, screen); }

        private void CommitDrawText(Point screen)
        {
            if (_page == null || _doc == null) return;
            var r = new Rect(_dragStartScreen, screen);
            double wPt = Math.Max(r.Width / _scale, 80 / _scale);
            double hPt = Math.Max(r.Height / _scale, 24 / _scale);
            var start = ToPoints(new Point(Math.Min(_dragStartScreen.X, screen.X), Math.Min(_dragStartScreen.Y, screen.Y)));

            var ann = new TextAnnotation
            {
                BoundsPoints = new Rect(start.X, start.Y, wPt, hPt),
                FontSizePoints = 12,
                Name = "Tekst"
            };
            _doc.History.Push(new AddAnnotationCommand(_page, ann));
            _selection.Set(ann);
            RaiseContentChanged();
            BeginEdit(ann);
        }

        private void BeginEdit(TextAnnotation ann)
        {
            CommitEdit();
            _editing = ann;
            var r = ScreenRect(ann.BoundsPoints);
            _editBox = new TextBox
            {
                Text = ann.Text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(ann.Foreground),
                BorderBrush = new SolidColorBrush(Color.FromRgb(74, 144, 226)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily(ann.FontFamily),
                FontSize = ann.FontSizePoints * _scale,
                Padding = new Thickness(ann.PaddingPoints * _scale),
                Width = r.Width,
                Height = r.Height
            };
            if (_page != null && _page.Rotation != 0)
            {
                _editBox.Width = ann.BoundsPoints.Width * _scale;
                _editBox.Height = ann.BoundsPoints.Height * _scale;
                _editBox.RenderTransformOrigin = new Point(0.5, 0.5);
                _editBox.RenderTransform = new RotateTransform(_page.Rotation);
                SetLeft(_editBox, r.X + r.Width / 2 - _editBox.Width / 2);
                SetTop(_editBox, r.Y + r.Height / 2 - _editBox.Height / 2);
            }
            else
            {
                SetLeft(_editBox, r.X);
                SetTop(_editBox, r.Y);
            }
            Children.Add(_editBox);
            _editBox.KeyDown += EditBox_KeyDown;
            _editBox.LostFocus += (_, _) => CommitEdit();
            _editBox.PreviewMouseRightButtonDown += (_, ev) =>
            {
                CommitEdit();
                ToolFinished?.Invoke(this, EventArgs.Empty);
                ev.Handled = true;
            };
            _editBox.Focus();
            _editBox.SelectAll();
        }

        private void EditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CommitEdit(); e.Handled = true; }
        }

        private void CommitEdit()
        {
            if (_editBox == null || _editing == null) { RemoveEditBox(); return; }
            string newText = _editBox.Text;
            var ann = _editing;
            string old = ann.Text;
            RemoveEditBox();
            if (newText != old && _doc != null)
            {
                ann.Text = old;
                _doc.History.Push(new ChangePropertyCommand<string>(v => ann.Text = v, old, newText, "Edytuj tekst"));
                RaiseContentChanged();
            }
            InvalidateVisual();
        }

        private void RemoveEditBox()
        {
            if (_editBox != null) { Children.Remove(_editBox); _editBox = null; }
            _editing = null;
        }

        // ── Narzędzia: prostokąt / elipsa / podświetlenie / redakcja / stempel ──

        private void BeginDrawRect(Point screen) { _drag = DragMode.DrawRect; _marqueeScreen = new Rect(screen, screen); }

        private void CommitDrawRect(Point screen)
        {
            if (_page == null || _doc == null) return;
            var tl = new Point(Math.Min(_dragStartScreen.X, screen.X), Math.Min(_dragStartScreen.Y, screen.Y));
            var start = ToPoints(tl);
            double wPt = Math.Max(Math.Abs(screen.X - _dragStartScreen.X) / _scale, 12);
            double hPt = Math.Max(Math.Abs(screen.Y - _dragStartScreen.Y) / _scale, 12);
            var bounds = new Rect(start.X, start.Y, wPt, hPt);

            AnnotationBase ann = _tool switch
            {
                SurfaceTool.Rectangle => new ShapeAnnotation { Kind = ShapeKind.Rectangle, Stroke = NewStroke, Fill = NewFill, StrokeThicknessPoints = NewThickness, Dashed = NewDashed, Name = "Prostokąt" },
                SurfaceTool.Ellipse => new ShapeAnnotation { Kind = ShapeKind.Ellipse, Stroke = NewStroke, Fill = NewFill, StrokeThicknessPoints = NewThickness, Dashed = NewDashed, Name = "Elipsa" },
                SurfaceTool.Highlight => new HighlightAnnotation { Color = NewHighlightColor, Kind = NewHighlightKind, Name = "Podświetlenie" },
                SurfaceTool.Redact => new RedactAnnotation { FillColor = NewRedactFill, OverlayText = NewRedactOverlay, Name = "Redakcja" },
                _ => new ShapeAnnotation { Kind = ShapeKind.Rectangle }
            };
            ann.BoundsPoints = bounds;
            _doc.History.Push(new AddAnnotationCommand(_page, ann));
            _selection.Set(ann);
            RaiseContentChanged();
        }

        // ── Narzędzia: linia / strzałka ──────────────────────────────────

        private void BeginDrawLine(Point screen) { _drag = DragMode.DrawLine; _dragCurrentScreen = screen; }

        private void CommitDrawLine(Point screen)
        {
            if (_page == null || _doc == null) return;
            var a = ToPoints(_dragStartScreen);
            var b = ToPoints(screen);
            if ((b - a).Length < 3) return;
            var ann = new ShapeAnnotation
            {
                Kind = _tool == SurfaceTool.Arrow ? ShapeKind.Arrow : ShapeKind.Line,
                Stroke = NewStroke, StrokeThicknessPoints = NewThickness, Dashed = NewDashed,
                Points = { }, Name = _tool == SurfaceTool.Arrow ? "Strzałka" : "Linia"
            };
            ann.Points.Add(a); ann.Points.Add(b);
            ann.RecalculateBounds();
            _doc.History.Push(new AddAnnotationCommand(_page, ann));
            _selection.Set(ann);
            RaiseContentChanged();
        }

        // ── Narzędzie: łamana (tryb klikany, Enter/dwuklik = zakończ, Esc = anuluj) ──

        private void PolylineClick(Point screen, int clickCount)
        {
            if (clickCount >= 2) { CommitPolyline(); return; }
            _polyPoints.Add(ToPoints(screen));
            _dragCurrentScreen = screen;
            InvalidateVisual();
        }

        private void CommitPolyline()
        {
            if (_page == null || _doc == null) { _polyPoints.Clear(); return; }
            if (_polyPoints.Count >= 2)
            {
                var ann = new ShapeAnnotation
                {
                    Kind = ShapeKind.Polyline, Stroke = NewStroke, StrokeThicknessPoints = NewThickness,
                    Dashed = NewDashed, Points = new List<Point>(_polyPoints), Name = "Łamana"
                };
                ann.RecalculateBounds();
                _doc.History.Push(new AddAnnotationCommand(_page, ann));
                _selection.Set(ann);
                RaiseContentChanged();
            }
            _polyPoints.Clear();
            InvalidateVisual();
        }

        // ── Narzędzie: obraz ─────────────────────────────────────────────

        public void PlaceImage(byte[] imageBytes, double naturalAspect)
        {
            if (_page == null || _doc == null) return;
            double wPt = Math.Min(_page.WidthPoints * 0.5, 300);
            double hPt = naturalAspect > 0 ? wPt / naturalAspect : wPt;
            double x = (_page.WidthPoints - wPt) / 2, y = (_page.HeightPoints - hPt) / 2;
            var ann = new ImageAnnotation
            {
                ImageBytes = imageBytes,
                BoundsPoints = new Rect(x, y, wPt, hPt),
                Name = "Obraz"
            };
            _doc.History.Push(new AddAnnotationCommand(_page, ann));
            _selection.Set(ann);
            RaiseContentChanged();
        }

        // ── Narzędzie: rysunek / zakreślacz ──────────────────────────────

        private void BeginInk(Point screen)
        {
            if (_page == null) return;
            _drag = DragMode.DrawInk;
            bool hl = _tool == SurfaceTool.Highlighter;
            _liveInk = new InkAnnotation
            {
                Color = hl ? Colors.Yellow : Colors.Black,
                ThicknessPoints = hl ? 12 : 2,
                IsHighlighter = hl,
                Name = hl ? "Zakreślacz" : "Rysunek"
            };
            var p = ToPoints(screen);
            _liveStroke = new Stroke(new StylusPointCollection(new[] { new StylusPoint(p.X, p.Y) }));
            _liveInk.Strokes.Add(_liveStroke);
        }

        private void DoInk(Point screen)
        {
            if (_liveStroke == null) return;
            var p = ToPoints(screen);
            _liveStroke.StylusPoints.Add(new StylusPoint(p.X, p.Y));
            InvalidateVisual();
        }

        private void CommitInk()
        {
            if (_page == null || _doc == null || _liveInk == null) { _liveInk = null; _liveStroke = null; return; }
            var ann = _liveInk;
            ann.RecalculateBounds();
            _liveInk = null; _liveStroke = null;
            bool tooSmall = ann.Strokes.Count == 0 || ann.BoundsPoints.IsEmpty
                            || (ann.BoundsPoints.Width < 1 && ann.BoundsPoints.Height < 1);
            if (tooSmall) { InvalidateVisual(); return; }
            _doc.History.Push(new AddAnnotationCommand(_page, ann));
            RaiseContentChanged();
        }

        // ── Klawiatura ───────────────────────────────────────────────────

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_page == null || _doc == null) return;
            if (_editBox != null) return;

            if (_tool == SurfaceTool.Polyline && _polyPoints.Count > 0)
            {
                if (e.Key == Key.Enter) { CommitPolyline(); e.Handled = true; return; }
                if (e.Key == Key.Escape) { _polyPoints.Clear(); InvalidateVisual(); e.Handled = true; return; }
            }

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;

            if (ctrl && e.Key == Key.Z) { _doc.History.Undo(); _selection.Clear(); InvalidateVisual(); RaiseContentChanged(); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.Y)) { _doc.History.Redo(); _selection.Clear(); InvalidateVisual(); RaiseContentChanged(); e.Handled = true; return; }

            if (ctrl && e.Key == Key.A) { _selection.SetRange(_page.Annotations.Where(a => a.IsVisible && !a.IsLocked)); e.Handled = true; return; }
            if (ctrl && e.Key == Key.D) { DuplicateSelection(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { _selection.Clear(); e.Handled = true; return; }
            if (e.Key == Key.Delete && !_selection.IsEmpty)
            {
                _doc.History.Push(new RemoveAnnotationsCommand(_page, _selection.Items.ToList()));
                _selection.Clear(); RaiseContentChanged(); e.Handled = true; return;
            }

            double dx = 0, dy = 0;
            switch (e.Key)
            {
                case Key.Left: dx = -step; break;
                case Key.Right: dx = step; break;
                case Key.Up: dy = -step; break;
                case Key.Down: dy = step; break;
                default: return;
            }
            if (_selection.IsEmpty) return;
            NudgeSelection(dx, dy);
            e.Handled = true;
        }

        private void NudgeSelection(double dxPt, double dyPt)
        {
            if (_doc == null) return;
            var states = _selection.Items.Select(a =>
            {
                var nb = new Rect(a.BoundsPoints.X + dxPt, a.BoundsPoints.Y + dyPt, a.BoundsPoints.Width, a.BoundsPoints.Height);
                return new TransformAnnotationsCommand.State(a, a.BoundsPoints, a.RotationDegrees, nb, a.RotationDegrees);
            }).ToList();
            _doc.History.Push(new TransformAnnotationsCommand(states, "Przesuń"));
            RaiseContentChanged();
            InvalidateVisual();
        }

        private void DuplicateSelection()
        {
            if (_page == null || _doc == null || _selection.IsEmpty) return;
            var clones = new List<AnnotationBase>();
            using (_doc.History.BeginBatch("Duplikuj"))
            {
                foreach (var a in _selection.Items.ToList())
                {
                    var c = a.Clone();
                    c.BoundsPoints = new Rect(a.BoundsPoints.X + 10, a.BoundsPoints.Y + 10, a.BoundsPoints.Width, a.BoundsPoints.Height);
                    _doc.History.Push(new AddAnnotationCommand(_page, c));
                    clones.Add(c);
                }
            }
            _selection.SetRange(clones);
            RaiseContentChanged();
        }

        // ── Snap ─────────────────────────────────────────────────────────

        private (double dx, double dy) ComputeSnap(Rect movedPt)
        {
            if (_page == null) return (0, 0);
            double thr = SnapPx / _scale;
            double bestDx = 0, bestDy = 0; double bx = thr, by = thr;

            var xTargets = new List<double> { 0, _page.WidthPoints, _page.WidthPoints / 2, 36, _page.WidthPoints - 36 };
            var yTargets = new List<double> { 0, _page.HeightPoints, _page.HeightPoints / 2, 36, _page.HeightPoints - 36 };
            foreach (var other in _page.Annotations.Where(a => !_selection.Contains(a) && a.IsVisible))
            {
                xTargets.Add(other.BoundsPoints.Left); xTargets.Add(other.BoundsPoints.Right);
                yTargets.Add(other.BoundsPoints.Top); yTargets.Add(other.BoundsPoints.Bottom);
            }

            double[] xEdges = { movedPt.Left, movedPt.Right, movedPt.Left + movedPt.Width / 2 };
            double[] yEdges = { movedPt.Top, movedPt.Bottom, movedPt.Top + movedPt.Height / 2 };

            foreach (var t in xTargets)
                foreach (var e in xEdges)
                    if (Math.Abs(e - t) < bx) { bx = Math.Abs(e - t); bestDx = t - e; }
            foreach (var t in yTargets)
                foreach (var e in yEdges)
                    if (Math.Abs(e - t) < by) { by = Math.Abs(e - t); bestDy = t - e; }

            _guides.Clear();
            if (bestDx != 0)
            {
                double x = movedPt.Left + bestDx;
                var a = ToScreen(new Point(x, 0));
                var b = ToScreen(new Point(x, _page.HeightPoints));
                _guides.Add((a.X, a.Y, b.X, b.Y));
            }
            if (bestDy != 0)
            {
                double y = movedPt.Top + bestDy;
                var a = ToScreen(new Point(0, y));
                var b = ToScreen(new Point(_page.WidthPoints, y));
                _guides.Add((a.X, a.Y, b.X, b.Y));
            }
            return (bestDx, bestDy);
        }

        private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}
