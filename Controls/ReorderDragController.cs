using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Segmento.Controls
{
    /// <summary>
    /// Przeciąganie kart w stylu iOS: karta unosi się nad UI, siatka rozsuwa
    /// się na bieżąco, upuszczenie zatwierdza kolejność przez callback.
    /// Wymaga panelu AnimatedWrapPanel jako ItemsPanel.
    /// </summary>
    public sealed class ReorderDragController
    {
        private const double LiftScale = 1.05;
        private const double EdgeZone = 70;      // strefa auto-scroll [px]
        private const double MaxScroll = 20;     // px / klatkę

        private readonly ItemsControl _list;
        private readonly ScrollViewer _scroll;
        private readonly Action<int, int> _commit;

        private AnimatedWrapPanel? _panel;
        private FrameworkElement? _container;
        private DragAdorner? _adorner;
        private AdornerLayer? _layer;
        private Window? _window;

        private Point _pressPoint, _grabOffset, _lastPoint;
        private int _fromIndex = -1, _toIndex = -1;
        private bool _pressed, _dragging;

        public ReorderDragController(ItemsControl list, ScrollViewer scroll, Action<int, int> commit)
        {
            _list = list; _scroll = scroll; _commit = commit;
            _list.PreviewMouseLeftButtonDown += OnPress;
            _list.PreviewMouseMove += OnMove;
            _list.PreviewMouseLeftButtonUp += OnRelease;
            _list.LostMouseCapture += (_, _) => { if (_dragging) Finish(false); };
        }

        #region Mouse

        private void OnPress(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(src) != null) return;

            _panel ??= FindDescendant<AnimatedWrapPanel>(_list);
            if (_panel == null) return;

            var container = ContainerFrom(src);
            if (container == null) return;

            _container = container;
            _fromIndex = _toIndex = _panel.Children.IndexOf(container);
            _pressPoint = _lastPoint = e.GetPosition(_scroll);
            _grabOffset = e.GetPosition(container);
            _pressed = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { if (!_dragging) _pressed = false; return; }

            _lastPoint = e.GetPosition(_scroll);

            if (!_dragging)
            {
                if (Math.Abs(_lastPoint.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(_lastPoint.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                if (!StartDrag()) return;
            }

            UpdateFromPointer();
        }

        private void OnRelease(object sender, MouseButtonEventArgs e)
        {
            if (!_pressed) return;
            if (!_dragging) { _pressed = false; _container = null; return; }

            Point p = e.GetPosition(_scroll);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= _scroll.ActualWidth && p.Y <= _scroll.ActualHeight;
            Finish(inside);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_dragging && e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        }

        #endregion

        #region Drag lifecycle

        private bool StartDrag()
        {
            _layer = AdornerLayer.GetAdornerLayer(_scroll);
            if (_layer == null || _container == null) { _pressed = false; return false; }

            _adorner = new DragAdorner(_scroll, _container, LiftScale);
            _layer.Add(_adorner);
            _container.Opacity = 0;                       // placeholder - slot zostaje

            _dragging = true;
            Mouse.Capture(_list, CaptureMode.SubTree);
            _window = Window.GetWindow(_list);
            if (_window != null) _window.PreviewKeyDown += OnKeyDown;
            CompositionTarget.Rendering += OnRendering;
            return true;
        }

        private void UpdateFromPointer()
        {
            if (_adorner == null || _panel == null) return;

            _adorner.SetPosition(_lastPoint.X - _grabOffset.X, _lastPoint.Y - _grabOffset.Y);

            Point inPanel = _scroll.TranslatePoint(_lastPoint, _panel);
            Size cell = _panel.CellSize;
            if (cell.Width <= 0 || cell.Height <= 0) return;

            double cx = inPanel.X - _grabOffset.X + cell.Width / 2;
            double cy = inPanel.Y - _grabOffset.Y + cell.Height / 2;

            int cols = Math.Max(1, _panel.Columns);
            int count = _panel.Children.Count;
            int col = Math.Clamp((int)Math.Floor(cx / cell.Width), 0, cols - 1);
            int row = Math.Max(0, (int)Math.Floor(cy / cell.Height));
            int idx = Math.Clamp(row * cols + col, 0, count - 1);

            if (idx == _toIndex) return;
            _toIndex = idx;
            ApplySlots();
        }

        private void ApplySlots()
        {
            var children = _panel!.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var ch = children[i];
                int slot = i == _fromIndex ? _toIndex
                    : _fromIndex < _toIndex ? (i > _fromIndex && i <= _toIndex ? i - 1 : i)
                                            : (i >= _toIndex && i < _fromIndex ? i + 1 : i);
                if (AnimatedWrapPanel.GetSlot(ch) != slot) AnimatedWrapPanel.SetSlot(ch, slot);
            }
        }

        private void ClearSlots()
        {
            foreach (UIElement ch in _panel!.Children) AnimatedWrapPanel.SetSlot(ch, -1);
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_dragging) return;

            double h = _scroll.ActualHeight, y = _lastPoint.Y, f = 0;
            if (y < EdgeZone) f = -(EdgeZone - y) / EdgeZone;
            else if (y > h - EdgeZone) f = (y - (h - EdgeZone)) / EdgeZone;
            if (f == 0) return;

            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + Math.Clamp(f, -1, 1) * MaxScroll);
            UpdateFromPointer();
        }

        private void Finish(bool commit)
        {
            if (!_dragging) return;
            _dragging = false;
            _pressed = false;

            CompositionTarget.Rendering -= OnRendering;
            if (_window != null) _window.PreviewKeyDown -= OnKeyDown;
            _list.ReleaseMouseCapture();

            if (!commit) { _toIndex = _fromIndex; ApplySlots(); }

            int from = _fromIndex, to = _toIndex;
            var container = _container!;
            var adorner = _adorner!;
            var layer = _layer!;
            var panel = _panel!;

            panel.UpdateLayout();
            Point target = SlotOrigin(to);

            adorner.AnimateTo(target, () =>
            {
                layer.Remove(adorner);
                container.Opacity = 1;
                ClearSlots();
                if (commit && from != to) _commit(from, to);
                panel.ResetOrigins();
            });

            _adorner = null; _container = null;
            _fromIndex = _toIndex = -1;
        }

        private Point SlotOrigin(int slot)
        {
            Size cell = _panel!.CellSize;
            int cols = Math.Max(1, _panel.Columns);
            var p = new Point((slot % cols) * cell.Width, (slot / cols) * cell.Height);
            return _panel.TransformToAncestor(_scroll).Transform(p);
        }

        #endregion

        #region Helpers

        private FrameworkElement? ContainerFrom(DependencyObject? d)
        {
            while (d != null && d != _list)
            {
                if (VisualTreeHelper.GetParent(d) == _panel && d is FrameworkElement fe) return fe;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) return t;
                var r = FindDescendant<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        #endregion

        /// Kopia karty renderowana nad całym UI (nieprzycinana przez ScrollViewer).
        private sealed class DragAdorner : Adorner
        {
            private readonly ImageSource _image;
            private readonly Size _size;
            private readonly TranslateTransform _pos = new();
            private readonly ScaleTransform _scale;

            public DragAdorner(UIElement adorned, FrameworkElement source, double liftScale) : base(adorned)
            {
                IsHitTestVisible = false;

                FrameworkElement visual = VisualTreeHelper.GetChildrenCount(source) == 1 &&
                                          VisualTreeHelper.GetChild(source, 0) is FrameworkElement c ? c : source;
                _size = new Size(visual.ActualWidth, visual.ActualHeight);
                _image = Snapshot(visual);

                _scale = new ScaleTransform(1, 1, _size.Width / 2, _size.Height / 2);
                var g = new TransformGroup();
                g.Children.Add(_scale);
                g.Children.Add(_pos);
                RenderTransform = g;

                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 270,
                    ShadowDepth = 8,
                    BlurRadius = 26,
                    Opacity = 0.5
                };

                var d = new Duration(TimeSpan.FromMilliseconds(140));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, liftScale, d) { EasingFunction = ease });
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(1, liftScale, d) { EasingFunction = ease });
            }

            public void SetPosition(double x, double y) { _pos.X = x; _pos.Y = y; }

            public void AnimateTo(Point target, Action completed)
            {
                var d = new Duration(TimeSpan.FromMilliseconds(190));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });

                var ax = new DoubleAnimation(target.X, d) { EasingFunction = ease };
                ax.Completed += (_, _) => completed();
                _pos.BeginAnimation(TranslateTransform.XProperty, ax);
                _pos.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(target.Y, d) { EasingFunction = ease });
            }

            protected override void OnRender(DrawingContext dc) =>
                dc.DrawImage(_image, new Rect(_size));

            private static ImageSource Snapshot(FrameworkElement src)
            {
                const double s = 1.5;
                var rtb = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(src.ActualWidth * s)),
                    Math.Max(1, (int)Math.Ceiling(src.ActualHeight * s)),
                    96 * s, 96 * s, PixelFormats.Pbgra32);
                rtb.Render(src);
                rtb.Freeze();
                return rtb;
            }
        }
    }
}
