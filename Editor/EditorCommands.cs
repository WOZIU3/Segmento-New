using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Segmento.Editor.Annotations;

namespace Segmento.Editor
{
    public interface IUndoableCommand
    {
        string Label { get; }
        void Do();
        void Undo();
    }

    /// <summary>Stos historii z limitem 100 pozycji i grupowaniem operacji (BeginBatch).</summary>
    public sealed class UndoStack
    {
        private const int Limit = 100;
        private readonly LinkedList<IUndoableCommand> _undo = new();
        private readonly Stack<IUndoableCommand> _redo = new();

        private List<IUndoableCommand>? _batch;
        private string _batchLabel = "";

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string? NextUndoLabel => _undo.Last?.Value.Label;
        public string? NextRedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

        public event EventHandler? Changed;

        /// <summary>Wykonuje Do() i odkłada na stos; czyści stos redo. W trybie batch tylko kumuluje.</summary>
        public void Push(IUndoableCommand cmd)
        {
            cmd.Do();
            if (_batch != null) { _batch.Add(cmd); return; }
            PushExecuted(cmd);
        }

        private void PushExecuted(IUndoableCommand cmd)
        {
            _undo.AddLast(cmd);
            while (_undo.Count > Limit) _undo.RemoveFirst();
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            var cmd = _undo.Last!.Value;
            _undo.RemoveLast();
            cmd.Undo();
            _redo.Push(cmd);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            var cmd = _redo.Pop();
            cmd.Do();
            _undo.AddLast(cmd);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Grupuje kolejne Push() w jedną pozycję historii aż do Dispose().</summary>
        public IDisposable BeginBatch(string label)
        {
            if (_batch != null) return new Batch(this, false); // zagnieżdżony — nie zamyka
            _batch = new List<IUndoableCommand>();
            _batchLabel = label;
            return new Batch(this, true);
        }

        private void EndBatch()
        {
            var items = _batch;
            _batch = null;
            if (items == null || items.Count == 0) return;
            PushExecuted(new CompositeCommand(_batchLabel, items));
        }

        private sealed class Batch : IDisposable
        {
            private readonly UndoStack _s;
            private readonly bool _owner;
            private bool _done;
            public Batch(UndoStack s, bool owner) { _s = s; _owner = owner; }
            public void Dispose() { if (_done || !_owner) return; _done = true; _s.EndBatch(); }
        }
    }

    public sealed class CompositeCommand : IUndoableCommand
    {
        private readonly IReadOnlyList<IUndoableCommand> _items;
        public string Label { get; }
        public CompositeCommand(string label, IReadOnlyList<IUndoableCommand> items) { Label = label; _items = items; }
        public void Do() { foreach (var c in _items) c.Do(); }
        public void Undo() { for (int i = _items.Count - 1; i >= 0; i--) _items[i].Undo(); }
    }

    public sealed class AddAnnotationCommand : IUndoableCommand
    {
        private readonly EditorPage _page;
        private readonly AnnotationBase _ann;
        public string Label => "Dodaj obiekt";
        public AddAnnotationCommand(EditorPage page, AnnotationBase ann) { _page = page; _ann = ann; }
        public void Do() { if (!_page.Annotations.Contains(_ann)) _page.Annotations.Add(_ann); }
        public void Undo() { _page.Annotations.Remove(_ann); }
    }

    public sealed class RemoveAnnotationsCommand : IUndoableCommand
    {
        private readonly EditorPage _page;
        private readonly List<(AnnotationBase Ann, int Index)> _removed;
        public string Label => "Usuń obiekt";

        public RemoveAnnotationsCommand(EditorPage page, IEnumerable<AnnotationBase> anns)
        {
            _page = page;
            _removed = anns.Select(a => (a, page.Annotations.IndexOf(a)))
                           .Where(t => t.Item2 >= 0)
                           .OrderBy(t => t.Item2)
                           .ToList();
        }

        public void Do()
        {
            foreach (var (ann, _) in _removed.OrderByDescending(t => _page.Annotations.IndexOf(t.Ann)))
                _page.Annotations.Remove(ann);
        }

        public void Undo()
        {
            foreach (var (ann, index) in _removed)
            {
                int i = Math.Clamp(index, 0, _page.Annotations.Count);
                _page.Annotations.Insert(i, ann);
            }
        }
    }

    /// <summary>Przesunięcie / skala / obrót zaznaczenia — snapshot Bounds+Rotation przed i po.</summary>
    public sealed class TransformAnnotationsCommand : IUndoableCommand
    {
        public readonly struct State
        {
            public readonly AnnotationBase Ann;
            public readonly Rect OldBounds, NewBounds;
            public readonly double OldRotation, NewRotation;
            public State(AnnotationBase a, Rect ob, double or, Rect nb, double nr)
            { Ann = a; OldBounds = ob; OldRotation = or; NewBounds = nb; NewRotation = nr; }
        }

        private readonly List<State> _states;
        public string Label { get; }

        public TransformAnnotationsCommand(IEnumerable<State> states, string label = "Przekształć")
        { _states = states.ToList(); Label = label; }

        public void Do()
        {
            foreach (var s in _states) { s.Ann.BoundsPoints = s.NewBounds; s.Ann.RotationDegrees = s.NewRotation; }
        }
        public void Undo()
        {
            foreach (var s in _states) { s.Ann.BoundsPoints = s.OldBounds; s.Ann.RotationDegrees = s.OldRotation; }
        }
    }

    /// <summary>Zmiana pojedynczej właściwości obiektu (np. kolor, tekst, opacity).</summary>
    public sealed class ChangePropertyCommand<T> : IUndoableCommand
    {
        private readonly Action<T> _setter;
        private readonly T _oldValue, _newValue;
        public string Label { get; }

        public ChangePropertyCommand(Action<T> setter, T oldValue, T newValue, string label = "Zmień właściwość")
        { _setter = setter; _oldValue = oldValue; _newValue = newValue; Label = label; }

        public void Do() => _setter(_newValue);
        public void Undo() => _setter(_oldValue);
    }
}
