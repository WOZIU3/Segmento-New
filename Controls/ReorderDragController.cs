using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Segmento.Controls
{
    /// <summary>
    /// Przeciaganie kart w stylu iOS: karta unosi sie nad UI (osobna warstwa Canvas),
    /// siatka rozsuwa sie na biezaco, upuszczenie zatwierdza kolejnosc przez callback.
    /// Wymaga AnimatedWrapPanel jako ItemsPanel.
    /// </summary>
    public sealed class ReorderDragController
    {
        private const double LiftScale = 1.05;
        private const double EdgeZone = 70;      // strefa auto-scroll [px]
        private const double MaxScroll = 20;     // px / klatke
        private const int DropMs = 190;

        private readonly ItemsControl _list;
        private readonly ScrollViewer _scroll;
        private readonly Canvas _layer;
        private readonly Action<int, int> _commit;

        private AnimatedWrapPanel? _panel;
        private FrameworkElement? _container;
        private Image? _ghost;
        private Window? _window;

        private Point _pressPoint, _grabOffset;
        private int _fromIndex = -1, _toIndex = -1;
        private bool _pressed, _dragging;

        public ReorderDragController(ItemsControl list, ScrollViewer scroll, Canvas dragLayer, Action<int, int> commit)
        {
            _list = list; _scroll = scroll; _layer = dragLayer; _commit = commit;
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
            _pressPoint = e.GetPosition(_layer);
            _grabOffset = e.GetPosition(container);
            _pressed = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { if (!_dragging) _pressed = false; return; }

            if (!_dragging)
            {
                Point p = e.GetPosition(_layer);
                if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                if (!StartDrag()) return;
            }

            UpdateFromPointer();
            e.Handled = true;
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
            if (_container == null) { _pressed = false; return false; }

            FrameworkElement visual = VisualTreeHelper.GetChildrenCount(_container) == 1 &&
                                      VisualTreeHelper.GetChild(_container, 0) is FrameworkElement c
                                      ? c : _container;
            if (visual.ActualWidth <= 0 || visual.ActualHeight <= 0) { _pressed = false; return false; }

            _ghost = new Image
            {
                Source = Snapshot(visual),
                Width = visual.ActualWidth,
                Height = visual.ActualHeight,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 270,
                    ShadowDepth = 8,
                    BlurRadius = 26,
                    Opacity = 0.5
                }
            };
            _layer.Children.Add(_ghost);

            var lift = new DoubleAnimation(1, LiftScale, new Duration(TimeSpan.FromMilliseconds(140)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var st = (ScaleTransform)_ghost.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, lift);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, lift);

            _container.Opacity = 0;                       // placeholder - slot zostaje pusty
            _dragging = true;
            Mouse.Capture(_list, CaptureMode.SubTree);
            _window = Window.GetWindow(_list);
            if (_window != null) _window.PreviewKeyDown += OnKeyDown;
            CompositionTarget.Rendering += OnRendering;
            return true;
        }

        private void UpdateFromPointer()
        {
            if (_ghost == null || _panel == null) return;

            Point inLayer = Mouse.GetPosition(_layer);
            Canvas.SetLeft(_ghost, inLayer.X - _grabOffset.X);
            Canvas.SetTop(_ghost, inLayer.Y - _grabOffset.Y);

            Size cell = _panel.CellSize;
            if (cell.Width <= 0 || cell.Height <= 0) return;

            Point inPanel = Mouse.GetPosition(_panel);
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

            Point p = Mouse.GetPosition(_scroll);
            double h = _scroll.ActualHeight, f = 0;
            if (p.Y < EdgeZone) f = -(EdgeZone - p.Y) / EdgeZone;
            else if (p.Y > h - EdgeZone) f = (p.Y - (h - EdgeZone)) / EdgeZone;
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
            var ghost = _ghost!;
            var panel = _panel!;

            panel.UpdateLayout();
            Point target = SlotOrigin(to);

            var d = new Duration(TimeSpan.FromMilliseconds(DropMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var st = (ScaleTransform)ghost.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });

            var ax = new DoubleAnimation(target.X, d) { EasingFunction = ease };
            ax.Completed += (_, _) =>
            {
                _layer.Children.Remove(ghost);
                container.Opacity = 1;
                ClearSlots();
                if (commit && from != to) _commit(from, to);
                panel.ResetOrigins();
            };
            ghost.BeginAnimation(Canvas.LeftProperty, ax);
            ghost.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(target.Y, d) { EasingFunction = ease });

            _ghost = null; _container = null;
            _fromIndex = _toIndex = -1;
        }

        /// Lewy gorny rog slotu w ukladzie wspolrzednych warstwy przeciagania.
        private Point SlotOrigin(int slot)
        {
            Size cell = _panel!.CellSize;
            int cols = Math.Max(1, _panel.Columns);
            var p = new Point((slot % cols) * cell.Width, (slot / cols) * cell.Height);
            return _panel.TransformToVisual(_layer).Transform(p);
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

        #endregion
    }
}
