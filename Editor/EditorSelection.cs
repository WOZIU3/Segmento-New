using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Segmento.Editor.Annotations;

namespace Segmento.Editor
{
    /// <summary>Zaznaczenie wielokrotne w obrębie bieżącej strony (współrzędne w punktach PDF).</summary>
    public sealed class EditorSelection
    {
        private readonly ObservableCollection<AnnotationBase> _items = new();
        public IReadOnlyList<AnnotationBase> Items => _items;
        public int Count => _items.Count;
        public bool IsEmpty => _items.Count == 0;
        public AnnotationBase? Primary => _items.LastOrDefault();

        public event EventHandler? Changed;

        public bool Contains(AnnotationBase a) => _items.Contains(a);

        public void Set(AnnotationBase a) { _items.Clear(); if (!a.IsLocked) _items.Add(a); Raise(); }

        public void SetRange(IEnumerable<AnnotationBase> items)
        {
            _items.Clear();
            foreach (var a in items) if (!a.IsLocked && !_items.Contains(a)) _items.Add(a);
            Raise();
        }

        public void Toggle(AnnotationBase a)
        {
            if (_items.Contains(a)) _items.Remove(a);
            else if (!a.IsLocked) _items.Add(a);
            Raise();
        }

        public void Add(AnnotationBase a) { if (!a.IsLocked && !_items.Contains(a)) { _items.Add(a); Raise(); } }
        public void Remove(AnnotationBase a) { if (_items.Remove(a)) Raise(); }
        public void Clear() { if (_items.Count == 0) return; _items.Clear(); Raise(); }

        /// <summary>Prostokąt obejmujący całe zaznaczenie w punktach PDF (Empty gdy brak).</summary>
        public Rect BoundsPoints
        {
            get
            {
                Rect r = Rect.Empty;
                foreach (var a in _items)
                    if (r.IsEmpty) r = a.BoundsPoints; else r.Union(a.BoundsPoints);
                return r;
            }
        }

        private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
