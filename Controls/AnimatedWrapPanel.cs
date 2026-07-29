using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Segmento.Controls
{
    /// <summary>
    /// WrapPanel z animowanym przemieszczaniem elementów oraz nadpisywalną
    /// kolejnością slotów (używane przez ReorderDragController).
    /// </summary>
    public class AnimatedWrapPanel : Panel
    {
        private const int DurationMs = 180;

        public static readonly DependencyProperty SlotProperty =
            DependencyProperty.RegisterAttached("Slot", typeof(int), typeof(AnimatedWrapPanel),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static void SetSlot(UIElement e, int v) => e.SetValue(SlotProperty, v);
        public static int GetSlot(UIElement e) => (int)e.GetValue(SlotProperty);

        private readonly Dictionary<UIElement, Point> _origins = new();
        private Size _cell;
        private int _columns = 1;

        public Size CellSize => _cell;
        public int Columns => _columns;

        /// Kasuje pamięć pozycji - następny arrange odbędzie się bez animacji.
        public void ResetOrigins() => _origins.Clear();

        protected override Size MeasureOverride(Size availableSize)
        {
            _cell = new Size();
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _cell.Width = Math.Max(_cell.Width, c.DesiredSize.Width);
                _cell.Height = Math.Max(_cell.Height, c.DesiredSize.Height);
            }

            int n = InternalChildren.Count;
            if (n == 0 || _cell.Width <= 0) return new Size();

            double w = double.IsInfinity(availableSize.Width) ? _cell.Width * n : availableSize.Width;
            _columns = Math.Max(1, (int)(w / _cell.Width));
            int rows = (n + _columns - 1) / _columns;
            return new Size(_columns * _cell.Width, rows * _cell.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int n = InternalChildren.Count;
            if (n == 0 || _cell.Width <= 0) return finalSize;

            _columns = Math.Max(1, (int)(finalSize.Width / _cell.Width));

            for (int i = 0; i < n; i++)
            {
                UIElement child = InternalChildren[i];
                int slot = GetSlot(child);
                if (slot < 0) slot = i;

                var target = new Point((slot % _columns) * _cell.Width,
                                       (slot / _columns) * _cell.Height);
                child.Arrange(new Rect(target, _cell));
                AnimateFrom(child, target);
            }
            return finalSize;
        }

        private void AnimateFrom(UIElement child, Point target)
        {
            bool known = _origins.TryGetValue(child, out Point prev);
            _origins[child] = target;
            if (!known || prev == target) return;

            if (child.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            {
                tt = new TranslateTransform();
                child.RenderTransform = tt;
            }

            var d = new Duration(TimeSpan.FromMilliseconds(DurationMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(prev.X - target.X, 0, d) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(prev.Y - target.Y, 0, d) { EasingFunction = ease });
        }

        protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
        {
            if (removed is UIElement ue) _origins.Remove(ue);
            base.OnVisualChildrenChanged(added, removed);
        }
    }
}
