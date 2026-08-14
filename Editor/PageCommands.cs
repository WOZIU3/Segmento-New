using System.Windows;

namespace Segmento.Editor
{
    public sealed class RotatePageCommand : IUndoableCommand
    {
        private readonly EditorPage _page;
        private readonly int _delta;
        public RotatePageCommand(EditorPage page, int deltaDegrees) { _page = page; _delta = deltaDegrees; }
        public string Label => "Obróć stronę";
        public void Do() => _page.Rotation += _delta;
        public void Undo() => _page.Rotation -= _delta;
    }

    public sealed class DeletePageCommand : IUndoableCommand
    {
        private readonly EditorPage _page;
        private bool _prev;
        public DeletePageCommand(EditorPage page) { _page = page; }
        public string Label => "Usuń stronę";
        public void Do() { _prev = _page.IsDeleted; _page.IsDeleted = true; }
        public void Undo() => _page.IsDeleted = _prev;
    }

    public sealed class CropPageCommand : IUndoableCommand
    {
        private readonly EditorPage _page;
        private readonly Rect? _new;
        private Rect? _old;
        public CropPageCommand(EditorPage page, Rect? newCrop) { _page = page; _new = newCrop; }
        public string Label => "Kadruj stronę";
        public void Do() { _old = _page.CropBoxPoints; _page.CropBoxPoints = _new; }
        public void Undo() => _page.CropBoxPoints = _old;
    }

    public sealed class ReorderPagesCommand : IUndoableCommand
    {
        private readonly EditorDocument _doc;
        private readonly int _from, _to;
        public ReorderPagesCommand(EditorDocument doc, int from, int to) { _doc = doc; _from = from; _to = to; }
        public string Label => "Zmień kolejność stron";
        public void Do() { if (Valid()) _doc.Pages.Move(_from, _to); }
        public void Undo() { if (Valid()) _doc.Pages.Move(_to, _from); }
        private bool Valid() => _from >= 0 && _to >= 0 && _from < _doc.Pages.Count && _to < _doc.Pages.Count && _from != _to;
    }

}
