using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PDFtoImage;
using SkiaSharp;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

using PdfSharpPdfDocument = PdfSharp.Pdf.PdfDocument;
using PdfSharpPdfReader = PdfSharp.Pdf.IO.PdfReader;
using PdfDocumentOpenMode = PdfSharp.Pdf.IO.PdfDocumentOpenMode;

using ITextPdfReader = iText.Kernel.Pdf.PdfReader;
using ITextPdfDocument = iText.Kernel.Pdf.PdfDocument;
using ITextPdfWriter = iText.Kernel.Pdf.PdfWriter;
using ITextPdfMerger = iText.Kernel.Utils.PdfMerger;

namespace Segmento
{
    public partial class MainWindow : Window
    {
        private enum EditorTool { None, Text, Image, Eraser }
        private EditorTool _currentTool = EditorTool.None;
        private PageItem? _editorPage;
        private readonly List<PdfSource> _sources = new();
        private readonly ObservableCollection<PageItem> _pages = new();
        private readonly ObservableCollection<PageItem> _organizePages = new();
        private CancellationTokenSource? _thumbnailCts;
        private Point _dragStartPoint;
        private PageItem? _draggedItem;
        private readonly Dictionary<PageItem, byte[]> _editedPages = new();
        private bool _isPanning;
        private Point _panStartPoint;
        // --- Editor tool state ---
        private bool _isDrawingTextRect;
        private Point _textRectStart;
        private System.Windows.Shapes.Rectangle? _textRubberBand;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_MAINWINDOW = 2;

        public MainWindow()
        {
            InitializeComponent();
            PagesList.ItemsSource = _pages;
            OrganizeList.ItemsSource = _organizePages;
            _organizePages.CollectionChanged += OrganizePages_CollectionChanged;
            SourceInitialized += OnSourceInitialized;
            StateChanged += OnStateChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                int backdrop = DWMSBT_MAINWINDOW;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
            catch { }
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            MaximizeBtn.ToolTip = WindowState == WindowState.Maximized
                ? "Przywróć" : "Maksymalizuj";
        }

        #region Window Controls

        private void Titlebar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
            }
            else if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void License_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new LicenseWindow { Owner = this };
                win.ShowDialog();
            }
            catch { }
        }

        #endregion

        #region Navigation

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || !IsLoaded) return;

            ImportView.Visibility = Visibility.Collapsed;
            SelectView.Visibility = Visibility.Collapsed;
            OrganizeView.Visibility = Visibility.Collapsed;
            EditorView.Visibility = Visibility.Collapsed;

            if (rb == NavImport)
            {
                ImportView.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Import PDF";
                HeaderSubtitle.Text = _sources.Count > 0 ? "Dodaj kolejne pliki PDF" : "Zacznij od wybrania plików PDF";
            }
            else if (rb == NavSelect)
            {
                SelectView.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Wybór stron";
                HeaderSubtitle.Text = "Zaznacz strony do eksportu";
            }
            else if (rb == NavOrganize)
            {
                OrganizeView.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Organizuj kolejność";
                HeaderSubtitle.Text = "Przeciągnij strony aby zmienić kolejność";
            }
            else if (rb == NavExport)
            {
                NavOrganize.IsChecked = true;
                Export_Click(sender, e);
            }
            else if (rb == NavEditor)
            {
                EditorView.Visibility = Visibility.Visible;
                HeaderTitle.Text = "Edytor PDF";
                HeaderSubtitle.Text = "Dodaj tekst, obraz lub wymaż fragment strony";
            }
        }

        private void UpdateNavBadges()
        {
            int selectedCount = _pages.Count(p => p.IsSelected);
            int organizeCount = _organizePages.Count;

            SelectBadge.Visibility = selectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectBadgeText.Text = selectedCount.ToString();

            OrganizeBadge.Visibility = organizeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            OrganizeBadgeText.Text = organizeCount.ToString();
        }

        #endregion

        #region File Loading (multi-PDF)

        private void SelectFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Pliki PDF (*.pdf)|*.pdf|Wszystkie pliki (*.*)|*.*",
                Title = "Wybierz pliki PDF",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                _ = LoadPdfsAsync(dialog.FileNames);
            }
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                e.Effects = files.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    ? DragDropEffects.Copy : DragDropEffects.None;
            }
            else { e.Effects = DragDropEffects.None; }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (pdfs.Length > 0) _ = LoadPdfsAsync(pdfs);
            }
        }

        private async Task LoadPdfsAsync(string[] filePaths)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();
            var token = _thumbnailCts.Token;

            try
            {
                LoadingText.Text = "Wczytywanie plików PDF...";
                LoadingOverlay.Visibility = Visibility.Visible;

                var newPages = new List<PageItem>();

                foreach (var filePath in filePaths)
                {
                    if (!File.Exists(filePath)) continue;
                    if (!filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

                    var fileBytes = await File.ReadAllBytesAsync(filePath, token);
                    int pageCount = await Task.Run(() => GetPageCount(fileBytes), token);
                    var fileInfo = new FileInfo(filePath);

                    var source = new PdfSource(filePath, fileBytes, pageCount, fileInfo.Length);
                    _sources.Add(source);

                    for (int i = 1; i <= pageCount; i++)
                    {
                        var page = new PageItem(source.Id, source.FileName, i, source.Bytes);
                        page.PropertyChanged += Page_PropertyChanged;
                        page.PageIndex = _pages.Count + newPages.Count;
                        newPages.Add(page);
                    }
                }

                foreach (var p in newPages) _pages.Add(p);

                UpdateFileInfoBar();
                FileInfoBar.Visibility = Visibility.Visible;
                NavSelect.IsEnabled = true;
                NavExport.IsEnabled = true;
                NavSelect.IsChecked = true;

                StatusText.Text = $"Wczytano {_sources.Count} plików · {_pages.Count} stron";
                StatusRight.Text = "Generowanie miniatur...";
                UpdateSelectionInfo();
                LoadingOverlay.Visibility = Visibility.Collapsed;

                await GenerateThumbnailsAsync(newPages, token);

                if (!token.IsCancellationRequested)
                {
                    StatusRight.Text = "";
                    StatusText.Text = "Gotowy · Wybierz strony do eksportu";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Błąd: {ex.Message}";
            }
        }

        private void UpdateFileInfoBar()
        {
            if (_sources.Count == 0)
            {
                FileNameText.Text = "Brak plików";
                FileInfoText.Text = "";
            }
            else if (_sources.Count == 1)
            {
                FileNameText.Text = _sources[0].FileName;
                FileInfoText.Text = $"{_sources[0].PageCount} stron · {FormatFileSize(_sources[0].FileSize)}";
            }
            else
            {
                long totalSize = _sources.Sum(s => s.FileSize);
                FileNameText.Text = $"{_sources.Count} plików PDF";
                FileInfoText.Text = $"{_pages.Count} stron łącznie · {FormatFileSize(totalSize)}";
            }
        }

        private static int GetPageCount(byte[] pdfBytes)
        {
            try
            {
                using var ms = new MemoryStream(pdfBytes);
                using var doc = PdfSharpPdfReader.Open(ms, PdfDocumentOpenMode.InformationOnly);
                return doc.PageCount;
            }
            catch
            {
                using var ms = new MemoryStream(pdfBytes);
                using var reader = new ITextPdfReader(ms);
                reader.SetUnethicalReading(true);
                using var pdfDocument = new ITextPdfDocument(reader);
                return pdfDocument.GetNumberOfPages();
            }
        }

        #endregion

        #region Thumbnails

        private async Task GenerateThumbnailsAsync(List<PageItem> pages, CancellationToken cancellationToken)
        {
            const int batchSize = 4;
            for (int i = 0; i < pages.Count; i += batchSize)
            {
                if (cancellationToken.IsCancellationRequested) return;

                var batch = pages.Skip(i).Take(batchSize).ToList();

                var results = await Task.Run(() =>
                {
                    var imgs = new List<(PageItem Page, BitmapImage? Image)>();
                    foreach (var page in batch)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        try
                        {
                            var bitmap = RenderPageToThumbnail(page.SourceBytes, page.OriginalPageNumber - 1);
                            imgs.Add((page, bitmap));
                        }
                        catch { imgs.Add((page, null)); }
                    }
                    return imgs;
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                foreach (var (page, image) in results)
                {
                    if (image != null)
                    {
                        page.Thumbnail = image;
                        page.HasThumbnail = true;
                    }
                }
            }
        }

        private static BitmapImage RenderPageToThumbnail(byte[] pdfBytes, int pageIndex)
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            var renderOptions = new PDFtoImage.RenderOptions
            {
                Dpi = 60,
                Width = 220,
                WithAspectRatio = true
            };
            using var skBitmap = Conversion.ToImage(pdfStream, page: pageIndex, options: renderOptions);
            using var skImage = SKImage.FromBitmap(skBitmap);
            using var skData = skImage.Encode(SKEncodedImageFormat.Png, 85);

            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(skData.ToArray()))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.DecodePixelWidth = 220;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        #endregion

        #region Selection

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var page in _pages) page.IsSelected = true;
            UpdateSelectionInfo();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var page in _pages) page.IsSelected = false;
            UpdateSelectionInfo();
        }

        private void ResetAndGoBack_Click(object sender, RoutedEventArgs e)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts = null;

            _sources.Clear();
            _pages.Clear();
            _organizePages.Clear();

            FileInfoBar.Visibility = Visibility.Collapsed;
            NavSelect.IsEnabled = false;
            NavOrganize.IsEnabled = false;
            NavExport.IsEnabled = false;
            NavEditor.IsEnabled = false;
            GoToOrganizeBtn.IsEnabled = false;

            // Wyczyść edytor
            EditorInkCanvas.Strokes.Clear();
            EditorInkCanvas.Children.Clear();
            EditorOverlayCanvas.Children.Clear();
            EditorPageImage.Source = null;
            EditorPageCombo.ItemsSource = null;
            EditorScale.ScaleX = 1;
            EditorScale.ScaleY = 1;
            _currentTool = EditorTool.None;

            UpdateNavBadges();
            StatusText.Text = "Gotowy";
            StatusRight.Text = "";

            // Wymusz widok importu niezależnie od stanu nawigacji
            EditorView.Visibility = Visibility.Collapsed;
            SelectView.Visibility = Visibility.Collapsed;
            OrganizeView.Visibility = Visibility.Collapsed;
            ImportView.Visibility = Visibility.Visible;
            HeaderTitle.Text = "Import PDF";
            HeaderSubtitle.Text = "Zacznij od wybrania plików PDF";
            NavImport.IsChecked = true;
        }

        private void PageTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is PageItem page)
            {
                page.IsSelected = !page.IsSelected;
                UpdateSelectionInfo();
                e.Handled = true;
            }
        }

        private void PageTile_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is PageItem page)
            {
                ShowPreview(page);
                e.Handled = true;
            }
        }

        private void Checkbox_Click(object sender, RoutedEventArgs e)
        {
            UpdateSelectionInfo();
            e.Handled = true;
        }

        private void Page_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PageItem.IsSelected))
                Dispatcher.BeginInvoke(new Action(UpdateSelectionInfo));
        }

        private void UpdateSelectionInfo()
        {
            int selected = _pages.Count(p => p.IsSelected);
            int total = _pages.Count;
            SelectionCountText.Text = $"{selected} z {total} zaznaczonych";
            GoToOrganizeBtn.IsEnabled = selected > 0;
            UpdateNavBadges();
            UpdateExportButtonState();
        }

        #endregion

        #region Rubber Band Selection

        private bool _isRubberBanding;
        private Point _rubberStart;
        private HashSet<PageItem> _rubberInitialSelection = new();

        private void PagesArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src)
            {
                if (IsInsidePageTile(src)) return;
                if (IsInsideScrollBar(src)) return;
            }

            Point clickPos = e.GetPosition(PagesScroll);
            if (clickPos.X > PagesScroll.ViewportWidth || clickPos.Y > PagesScroll.ViewportHeight)
                return;

            _isRubberBanding = true;
            _rubberStart = clickPos;

            bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!additive)
            {
                foreach (var p in _pages) p.IsSelected = false;
            }
            _rubberInitialSelection = _pages.Where(p => p.IsSelected).ToHashSet();

            RubberBandRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(RubberBandRect, _rubberStart.X);
            Canvas.SetTop(RubberBandRect, _rubberStart.Y);
            RubberBandRect.Width = 0;
            RubberBandRect.Height = 0;

            PagesScroll.CaptureMouse();
        }

        private void PagesArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRubberBanding) return;

            Point current = e.GetPosition(PagesScroll);
            double x = Math.Min(current.X, _rubberStart.X);
            double y = Math.Min(current.Y, _rubberStart.Y);
            double w = Math.Abs(current.X - _rubberStart.X);
            double h = Math.Abs(current.Y - _rubberStart.Y);

            Canvas.SetLeft(RubberBandRect, x);
            Canvas.SetTop(RubberBandRect, y);
            RubberBandRect.Width = w;
            RubberBandRect.Height = h;

            var selectionRect = new Rect(x, y, w, h);

            foreach (var page in _pages)
            {
                var container = PagesList.ItemContainerGenerator.ContainerFromItem(page) as FrameworkElement;
                if (container == null) continue;

                var tileBorder = FindTileBorder(container);
                if (tileBorder == null) continue;

                try
                {
                    GeneralTransform transform = tileBorder.TransformToAncestor(PagesScroll);
                    Rect tileRect = transform.TransformBounds(
                        new Rect(0, 0, tileBorder.ActualWidth, tileBorder.ActualHeight));

                    bool intersects = selectionRect.IntersectsWith(tileRect);
                    bool wasInitial = _rubberInitialSelection.Contains(page);

                    page.IsSelected = intersects || wasInitial;
                }
                catch { }
            }

            UpdateSelectionInfo();
        }

        private void PagesArea_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRubberBanding) return;
            _isRubberBanding = false;
            RubberBandRect.Visibility = Visibility.Collapsed;
            PagesScroll.ReleaseMouseCapture();
            UpdateSelectionInfo();
        }

        private static bool IsInsidePageTile(DependencyObject src)
        {
            DependencyObject? current = src;
            while (current != null)
            {
                if (current is CheckBox) return true;
                if (current is Border b && b.Tag is PageItem) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static bool IsInsideScrollBar(DependencyObject src)
        {
            DependencyObject? current = src;
            while (current != null)
            {
                if (current is System.Windows.Controls.Primitives.ScrollBar) return true;
                if (current is System.Windows.Controls.Primitives.Thumb) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static Border? FindTileBorder(DependencyObject container)
        {
            if (container is Border cb && cb.Tag is PageItem) return cb;
            int count = VisualTreeHelper.GetChildrenCount(container);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(container, i);
                var result = FindTileBorder(child);
                if (result != null) return result;
            }
            return null;
        }

        #endregion

        #region Preview

        private void ShowPreview(PageItem page)
        {
            try
            {
                var preview = new PreviewWindow(page.SourceBytes, page.OriginalPageNumber) { Owner = this };
                preview.Show();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Nie można otworzyć podglądu: {ex.Message}";
            }
        }

        #endregion

        #region Organize

        private void OrganizePages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateExportButtonState();
            UpdateOrganizeOrder();
            NavOrganize.IsEnabled = _organizePages.Count > 0;
            UpdateNavBadges();
        }

        private void UpdateOrganizeOrder()
        {
            for (int i = 0; i < _organizePages.Count; i++)
                _organizePages[i].OrganizeOrder = i + 1;
        }

        private void GoToOrganize_Click(object sender, RoutedEventArgs e)
        {
            var newSelectedPages = _pages.Where(p => p.IsSelected).ToList();

            foreach (var page in newSelectedPages)
            {
                if (!_organizePages.Contains(page))
                    _organizePages.Add(page);
            }

            var toRemove = _organizePages.Where(p => !p.IsSelected).ToList();
            foreach (var item in toRemove) _organizePages.Remove(item);

            UpdateOrganizeOrder();
            NavOrganize.IsChecked = true;
            StatusText.Text = "Przeciągnij strony aby zmienić kolejność";

            ActivateEditor();
        }

        private void RemoveFromOrganize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PageItem page)
            {
                _organizePages.Remove(page);
                page.IsSelected = false;
                UpdateSelectionInfo();
            }
        }

        #endregion

        #region Editor

        private void ActivateEditor()
        {
            var source = _organizePages.Count > 0
                ? _organizePages.ToList()
                : _pages.Where(p => p.IsSelected).ToList();

            EditorPageCombo.ItemsSource = source;

            if (source.Count > 0)
                EditorPageCombo.SelectedIndex = 0;

            NavEditor.IsEnabled = true;
        }

        private async void EditorPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EditorPageCombo.SelectedItem is not PageItem page) return;
            _editorPage = page;

            EditorOverlayCanvas.Children.Clear();
            EditorInkCanvas.Strokes.Clear();

            var bitmap = await Task.Run(() =>
                RenderPageToEditorBitmap(page.SourceBytes, page.OriginalPageNumber - 1));

            EditorPageImage.Source = bitmap;
        }

        private static BitmapImage RenderPageToEditorBitmap(byte[] pdfBytes, int pageIndex)
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            var opts = new PDFtoImage.RenderOptions { Dpi = 150, WithAspectRatio = true };
            using var skBitmap = PDFtoImage.Conversion.ToImage(pdfStream, page: pageIndex, options: opts);
            using var skImage = SkiaSharp.SKImage.FromBitmap(skBitmap);
            using var skData = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);

            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(skData.ToArray()))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }

        // --- Przełączanie narzędzi ---

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb) return;
            string tag = rb.Tag as string ?? string.Empty;

            if (tag == "Text")        _currentTool = EditorTool.Text;
            else if (tag == "Image")  _currentTool = EditorTool.Image;
            else if (tag == "Eraser") _currentTool = EditorTool.Eraser;
            else                      _currentTool = EditorTool.None;

            ApplyToolToInkCanvas();

        if (_currentTool == EditorTool.Image)
        {
            OpenImageFromDialog();
            ToolImageBtn.IsChecked = false;
            _currentTool = EditorTool.None;
            ApplyToolToInkCanvas();
        }
        
        if (_currentTool == EditorTool.Eraser)
        {
            EditorOverlayCanvas.Children.Clear();
            EditorInkCanvas.Strokes.Clear();
            ToolEraserBtn.IsChecked = false;
            _currentTool = EditorTool.None;
            ApplyToolToInkCanvas();
            StatusText.Text = "Usunięto wszystkie naniesione zmiany";
        }
        
        EditorScrollViewer.Cursor = _currentTool == EditorTool.None
            ? Cursors.SizeAll : Cursors.Arrow;
        }

        private void ApplyToolToInkCanvas()
        {
            switch (_currentTool)
            {

                default:
                    EditorInkCanvas.EditingMode = InkCanvasEditingMode.None;
                    EditorInkCanvas.IsHitTestVisible = false;
                    EditorInkCanvas.Cursor = Cursors.Arrow;
                    break;
            }
        }

        // ================================================================
        // NARZĘDZIE: TEKST
        // ================================================================

        private void EditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentTool != EditorTool.Text) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            _isDrawingTextRect = true;
            _textRectStart = e.GetPosition(EditorOverlayCanvas);

            // Tymczasowa ramka wyboru obszaru
            _textRubberBand = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212)),
                Width = 0,
                Height = 0
            };
            Canvas.SetLeft(_textRubberBand, _textRectStart.X);
            Canvas.SetTop(_textRubberBand, _textRectStart.Y);
            EditorOverlayCanvas.Children.Add(_textRubberBand);
            EditorOverlayCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void EditorCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDrawingTextRect || _textRubberBand == null) return;
            Point cur = e.GetPosition(EditorOverlayCanvas);
            double x = Math.Min(cur.X, _textRectStart.X);
            double y = Math.Min(cur.Y, _textRectStart.Y);
            double w = Math.Abs(cur.X - _textRectStart.X);
            double h = Math.Abs(cur.Y - _textRectStart.Y);
            Canvas.SetLeft(_textRubberBand, x);
            Canvas.SetTop(_textRubberBand, y);
            _textRubberBand.Width = w;
            _textRubberBand.Height = h;
        }

        private void EditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDrawingTextRect || _textRubberBand == null) return;
            _isDrawingTextRect = false;
            EditorOverlayCanvas.ReleaseMouseCapture();

            double left   = Canvas.GetLeft(_textRubberBand);
            double top    = Canvas.GetTop(_textRubberBand);
            double width  = Math.Max(_textRubberBand.Width,  80);
            double height = Math.Max(_textRubberBand.Height, 40);

            EditorOverlayCanvas.Children.Remove(_textRubberBand);
            _textRubberBand = null;

            // Minimalny obszar — ignoruj kliknięcia bez przeciągania
            if (width < 10 && height < 10) return;

            AddTextBox(left, top, width, height);
        }

        private void AddTextBox(double left, double top, double w, double h)
        {
            const double H  = 8;  // uchwyt: średnica
            const double HH = 4;  // uchwyt: połowa

            // ── TextBox ────────────────────────────────────────────────────
            var tb = new TextBox
            {
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                TextWrapping    = TextWrapping.Wrap,
                AcceptsReturn   = true,
                FontSize        = 14,
                Foreground      = Brushes.Black,
                FontFamily      = new FontFamily("Segoe UI Variable"),
                Padding         = new Thickness(2),
                Margin          = new Thickness(HH),
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Cursor          = Cursors.IBeam
            };

            // ── Bounding box (ramka #4A90E2) ───────────────────────────────
            var bbox = new Border
            {
                BorderBrush      = new SolidColorBrush(Color.FromRgb(74, 144, 226)),
                BorderThickness  = new Thickness(1),
                Background       = Brushes.Transparent,
                Margin           = new Thickness(HH),
                IsHitTestVisible = false
            };

            // ── Warstwa uchwytów ───────────────────────────────────────────
            var handleLayer = new Canvas { ClipToBounds = false };

            // ── Kontener Grid ──────────────────────────────────────────────
            var container = new Grid { Width = w, Height = h, ClipToBounds = false };
            container.Children.Add(tb);
            container.Children.Add(bbox);
            container.Children.Add(handleLayer);

            // ── 8 uchwytów resize ──────────────────────────────────────────
            Cursor[] hCursors = {
                Cursors.SizeNWSE, Cursors.SizeNS,   Cursors.SizeNESW,
                Cursors.SizeWE,                      Cursors.SizeWE,
                Cursors.SizeNESW, Cursors.SizeNS,   Cursors.SizeNWSE
            };
            var handles = new Ellipse[8];
            for (int i = 0; i < 8; i++)
            {
                handles[i] = new Ellipse
                {
                    Width = H, Height = H,
                    Fill   = new SolidColorBrush(Color.FromRgb(74, 144, 226)),
                    Stroke = Brushes.White, StrokeThickness = 1,
                    Cursor = hCursors[i], Tag = i
                };
                handleLayer.Children.Add(handles[i]);
            }

            void LayoutHandles()
            {
                double cw = container.Width, ch = container.Height;
                (double x, double y)[] pts = {
                    (0,    0),    (cw/2, 0),    (cw,   0),
                    (0,    ch/2),               (cw,   ch/2),
                    (0,    ch),   (cw/2, ch),   (cw,   ch)
                };
                for (int i = 0; i < 8; i++)
                {
                    Canvas.SetLeft(handles[i], pts[i].x - HH);
                    Canvas.SetTop (handles[i], pts[i].y - HH);
                }
            }
            LayoutHandles();

            // ── Popup toolbar ──────────────────────────────────────────────
            var popup = BuildTextToolbar(tb, container);
            popup.PlacementTarget = container;

            // ── Aktywacja / dezaktywacja ────────────────────────────────────
            bool hasFocus = false;
            void Activate()
            {
                hasFocus = true;
                bbox.Visibility = handleLayer.Visibility = Visibility.Visible;
                popup.IsOpen = true;
            }
            void Deactivate()
            {
                hasFocus = false;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (hasFocus) return;
                    bbox.Visibility = handleLayer.Visibility = Visibility.Collapsed;
                    popup.IsOpen = false;
                }), System.Windows.Threading.DispatcherPriority.Input);
            }

            tb.GotFocus  += (_, _) => Activate();
            tb.LostFocus += (_, _) => Deactivate();

            container.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.OriginalSource is Ellipse) return;
                if (!hasFocus) { tb.Focus(); ev.Handled = true; }
            };

            tb.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Escape) EditorOverlayCanvas.Focus();
            };

            // ── Przenoszenie (LPM drag z progiem) ──────────────────────────
            Point moveStart   = default;
            bool  isMoving    = false;
            bool  movePending = false;
            
            tb.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                movePending = true;
                moveStart = ev.GetPosition(EditorOverlayCanvas);
            };
            tb.PreviewMouseLeftButtonUp += (s, ev) => movePending = false;
            
            container.PreviewMouseMove += (s, ev) =>
            {
                if (movePending && ev.LeftButton == MouseButtonState.Pressed)
                {
                    var cur = ev.GetPosition(EditorOverlayCanvas);
                    if (Math.Abs(cur.X - moveStart.X) > 6 || Math.Abs(cur.Y - moveStart.Y) > 6)
                    {
                        movePending = false;
                        isMoving    = true;
                        container.CaptureMouse();
                        ev.Handled  = true;
                    }
                }
                if (isMoving)
                {
                    var cur = ev.GetPosition(EditorOverlayCanvas);
                    Canvas.SetLeft(container, Canvas.GetLeft(container) + cur.X - moveStart.X);
                    Canvas.SetTop (container, Canvas.GetTop (container) + cur.Y - moveStart.Y);
                    moveStart = cur;
                }
            };
            container.MouseLeftButtonUp += (s, ev) =>
            {
                movePending = false;
                if (!isMoving) return;
                isMoving = false;
                container.ReleaseMouseCapture();
            };

            // ── Resize uchwytów ─────────────────────────────────────────────
            for (int i = 0; i < 8; i++)
            {
                int mode = i;
                Point rs = default;
                bool  resizing = false;
                double rsW = 0, rsH = 0, rsL = 0, rsT = 0;

                handles[i].MouseLeftButtonDown += (s, ev) =>
                {
                    resizing = true;
                    rs  = ev.GetPosition(EditorOverlayCanvas);
                    rsW = container.Width;  rsH = container.Height;
                    rsL = Canvas.GetLeft(container); rsT = Canvas.GetTop(container);
                    ((Ellipse)s).CaptureMouse();
                    ev.Handled = true;
                };
                handles[i].MouseMove += (s, ev) =>
                {
                    if (!resizing) return;
                    var cur = ev.GetPosition(EditorOverlayCanvas);
                    double dx = cur.X - rs.X, dy = cur.Y - rs.Y;
                    double nW = rsW, nH = rsH, nL = rsL, nT = rsT;
                    // 0=TL,1=TC,2=TR,3=ML,4=MR,5=BL,6=BC,7=BR
                    switch (mode)
                    {
                        case 0: nW=rsW-dx; nH=rsH-dy; nL=rsL+dx; nT=rsT+dy; break;
                        case 1: nH=rsH-dy; nT=rsT+dy; break;
                        case 2: nW=rsW+dx; nH=rsH-dy; nT=rsT+dy; break;
                        case 3: nW=rsW-dx; nL=rsL+dx; break;
                        case 4: nW=rsW+dx; break;
                        case 5: nW=rsW-dx; nH=rsH+dy; nL=rsL+dx; break;
                        case 6: nH=rsH+dy; break;
                        case 7: nW=rsW+dx; nH=rsH+dy; break;
                    }
                    if (nW < 60) { if (mode==0||mode==3||mode==5) nL=rsL+rsW-60; nW=60; }
                    if (nH < 30) { if (mode==0||mode==1||mode==2) nT=rsT+rsH-30; nH=30; }
                    container.Width = nW; container.Height = nH;
                    Canvas.SetLeft(container, nL); Canvas.SetTop(container, nT);
                    LayoutHandles();
                };
                handles[i].MouseLeftButtonUp += (s, ev) =>
                {
                    if (!resizing) return;
                    resizing = false;
                    ((Ellipse)s).ReleaseMouseCapture();
                };
            }

            Canvas.SetLeft(container, left);
            Canvas.SetTop (container, top);
            EditorOverlayCanvas.Children.Add(container);
            container.Loaded += (_, _) => tb.Focus();
        }

        private Popup BuildTextToolbar(TextBox tb, Grid container)
        {
            // A▲ / A▼ — skalowanie czcionki
            Button MakeSizeBtn(string label, int delta)
            {
                var b = new Button
                {
                    Content = label, Focusable = false,
                    Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(1, 0, 1, 0),
                    Foreground = Brushes.White, Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand
                };
                b.Click += (_, _) => { tb.FontSize = Math.Clamp(tb.FontSize + delta, 8, 72); tb.Focus(); };
                return b;
            }
        
            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
            sizeRow.Children.Add(MakeSizeBtn("A▲", +2));
            sizeRow.Children.Add(MakeSizeBtn("A▼", -2));
        
            // Okrągłe próbki kolorów: Biały, Czarny, Czerwony, Niebieski
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var (col, brush) in new (Color, Brush)[]
            {
                (Colors.White,            Brushes.White),
                (Colors.Black,            Brushes.Black),
                (Colors.Red,              Brushes.Red),
                (Color.FromRgb(30,144,255), new SolidColorBrush(Color.FromRgb(30, 144, 255)))
            })
            {
                var b = brush;
                var swatch = new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill            = new SolidColorBrush(col),
                    Stroke          = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                    StrokeThickness = 1,
                    Margin          = new Thickness(3, 0, 3, 0),
                    Cursor          = Cursors.Hand
                };
                swatch.MouseLeftButtonDown += (_, _) => { tb.Foreground = b; tb.Focus(); };
                colorRow.Children.Add(swatch);
            }
        
            // Przycisk Kosz (Segoe MDL2 Assets)
            var delBtn = new Button
            {
                Content = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13, Focusable = false,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            delBtn.Click += (_, _) => EditorOverlayCanvas.Children.Remove(container);
        
            Border Sep() => new Border
            {
                Width = 1, Margin = new Thickness(4, 3, 4, 3),
                Background = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
        
            var toolStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            toolStack.Children.Add(sizeRow);
            toolStack.Children.Add(Sep());
            toolStack.Children.Add(colorRow);
            toolStack.Children.Add(Sep());
            toolStack.Children.Add(delBtn);
        
            var toolBorder = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                CornerRadius    = new CornerRadius(6),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 6, 6, 6),
                Child           = toolStack
            };
        
            return new Popup
            {
                Placement = PlacementMode.Bottom, VerticalOffset = 8,
                AllowsTransparency = true, StaysOpen = false,
                IsOpen = false, Child = toolBorder
            };
        }
        private void OpenImageFromDialog()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Wybierz obraz",
                Filter = "Obrazy|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|Wszystkie pliki|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            BitmapImage bmp;
            try
            {
                bmp = new BitmapImage(new Uri(dlg.FileName));
            }
            catch
            {
                StatusText.Text = "Nie można wczytać pliku obrazu";
                return;
            }

            PlaceImageOnCanvas(bmp);
        }

        private void PlaceImageOnCanvas(BitmapSource src)
        {
            double initW  = Math.Min(src.PixelWidth, 400);
            double aspect = (double)src.PixelHeight / src.PixelWidth;
            double initH  = initW * aspect;

            // ── Image (IsHitTestVisible=false → eventy idą do kontenera) ──
            var img = new Image
            {
                Source = src, Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };

            // ── Resize handle (prawy dolny róg) ────────────────────────────
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = 14, Height = 14,
                Fill  = new SolidColorBrush(Color.FromRgb(74, 144, 226)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Margin = new Thickness(0, 0, -7, -7)
            };

            // ── Kontener ───────────────────────────────────────────────────
            var container = new Grid
            {
                Width      = initW,
                Height     = initH,
                Cursor     = Cursors.SizeAll,
                Background = Brushes.Transparent, // kluczowe: hit-test działa
                ClipToBounds = false
            };
            container.Children.Add(img);
            container.Children.Add(handle);

            // ── Popup toolbar (opacity + delete) ───────────────────────────
            var toolbar = BuildImageToolbar(container, img);
            toolbar.PlacementTarget = container;

            bool isSelected = false;
            void ShowToolbar() { isSelected = true;  toolbar.IsOpen = true;  }
            void HideToolbar() { isSelected = false; toolbar.IsOpen = false; }

            // ── Przeciąganie ───────────────────────────────────────────────
            Point dragStart = default;
            bool  isDragging = false;

            container.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.OriginalSource is System.Windows.Shapes.Rectangle) return;
                ShowToolbar();
                isDragging = true;
                dragStart  = ev.GetPosition(EditorOverlayCanvas);
                container.CaptureMouse();
                ev.Handled = true;
            };
            container.MouseMove += (s, ev) =>
            {
                if (!isDragging) return;
                Point cur = ev.GetPosition(EditorOverlayCanvas);
                Canvas.SetLeft(container, Canvas.GetLeft(container) + cur.X - dragStart.X);
                Canvas.SetTop (container, Canvas.GetTop(container)  + cur.Y - dragStart.Y);
                dragStart = cur;
            };
            container.MouseLeftButtonUp += (s, ev) =>
            {
                if (!isDragging) return;
                isDragging = false;
                container.ReleaseMouseCapture();
            };

            // ── Skalowanie za uchwyt ───────────────────────────────────────
            Point resizeStart = default;
            bool  isResizing  = false;
            double startW = initW, startH = initH;

            handle.MouseLeftButtonDown += (s, ev) =>
            {
                isResizing  = true;
                resizeStart = ev.GetPosition(EditorOverlayCanvas);
                startW = container.Width;
                startH = container.Height;
                handle.CaptureMouse();
                ev.Handled = true;
            };
            handle.MouseMove += (s, ev) =>
            {
                if (!isResizing) return;
                Point cur = ev.GetPosition(EditorOverlayCanvas);
                container.Width  = Math.Max(40, startW + cur.X - resizeStart.X);
                container.Height = Math.Max(40, startH + cur.Y - resizeStart.Y);
            };
            handle.MouseLeftButtonUp += (s, ev) =>
            {
                if (!isResizing) return;
                isResizing = false;
                handle.ReleaseMouseCapture();
            };

            // ── Klik poza kontenerem = chowaj toolbar ─────────────────────
            EditorOverlayCanvas.MouseLeftButtonDown += (s, ev) =>
            {
                if (!isSelected) return;
                if (ev.OriginalSource != EditorOverlayCanvas) return;
                HideToolbar();
            };

            Canvas.SetLeft(container, 60);
            Canvas.SetTop (container, 60);
            EditorOverlayCanvas.Children.Add(container);
            StatusText.Text = "Obraz dodany · przeciągnij = przesuń · róg = skaluj · kliknij = opcje";
        }

        private Popup BuildImageToolbar(Grid container, Image img)
        {
            // ── Przyciski przezroczystości ──────────────────────────────────
            var opacityRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, op) in new (string, double)[]
            {
                ("25%", 0.25), ("50%", 0.50), ("75%", 0.75), ("100%", 1.0)
            })
            {
                double opacity = op;
                var btn = new Button
                {
                    Content = label,
                    Focusable = false,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin  = new Thickness(1, 0, 1, 0),
                    Foreground = Brushes.White,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 11, Cursor = Cursors.Hand
                };
                btn.Click += (_, _) => img.Opacity = opacity;
                opacityRow.Children.Add(btn);
            }

            // ── Przycisk usuń ───────────────────────────────────────────────
            var delBtn = new Button
            {
                Content = "✕",
                Focusable = false,
                Padding = new Thickness(7, 2, 7, 2),
                Margin  = new Thickness(8, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12, FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand
            };
            delBtn.Click += (_, _) => EditorOverlayCanvas.Children.Remove(container);

            Border Sep() => new Border
            {
                Width = 1, Margin = new Thickness(5, 3, 5, 3),
                Background = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };

            var toolStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            toolStack.Children.Add(new TextBlock
            {
                Text = "Przezroczystość",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11, Margin = new Thickness(4, 0, 4, 0)
            });
            toolStack.Children.Add(opacityRow);
            toolStack.Children.Add(Sep());
            toolStack.Children.Add(delBtn);

            var toolBorder = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                CornerRadius    = new CornerRadius(6),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(4, 5, 4, 5),
                Child           = toolStack
            };

            return new Popup
            {
                Placement = PlacementMode.Bottom, VerticalOffset = 6,
                AllowsTransparency = true, StaysOpen = false,
                IsOpen = false, Child = toolBorder
            };
        }

        private void PasteImageFromClipboard()
        {
            if (Clipboard.ContainsImage())
            {
                PlaceImageOnCanvas(Clipboard.GetImage());
                return;
            }
            StatusText.Text = "Schowek nie zawiera obrazu — użyj przycisku Obraz aby wybrać plik";
        }

        // --- Cofnij ---

        private void EditorUndo_Click(object sender, RoutedEventArgs e)
        {
            if (EditorOverlayCanvas.Children.Count > 0)
            {
                EditorOverlayCanvas.Children.RemoveAt(
                    EditorOverlayCanvas.Children.Count - 1);
                return;
            }
            if (EditorInkCanvas.Strokes.Count > 0)
                EditorInkCanvas.Strokes.RemoveAt(
                    EditorInkCanvas.Strokes.Count - 1);
        }

        private async void SaveEditorPage_Click(object sender, RoutedEventArgs e)
        {
            if (_editorPage == null) return;
        
            try
            {
                SaveEditorBtn.IsEnabled = false;
                StatusText.Text = "Zapisywanie zmian...";
        
                // Reset zoom przed renderowaniem (UI thread)
                double prevScale = EditorScale.ScaleX;
                EditorScale.ScaleX = 1;
                EditorScale.ScaleY = 1;
                EditorCanvasGrid.UpdateLayout();
        
                // Renderuj na UI thread
                int renderW = (int)EditorPageImage.ActualWidth;
                int renderH = (int)EditorPageImage.ActualHeight;
        
                if (renderW <= 0 || renderH <= 0)
                {
                    StatusText.Text = "Błąd: brak załadowanej strony";
                    return;
                }
        
                var renderBitmap = new RenderTargetBitmap(
                    renderW, renderH, 96, 96, PixelFormats.Pbgra32);
        
                // Renderuj każdą warstwę osobno względem Image
                var drawingVisual = new System.Windows.Media.DrawingVisual();
                using (var ctx = drawingVisual.RenderOpen())
                {
                    var imageRect = new Rect(0, 0, renderW, renderH);
        
                    // Warstwa 1: obraz PDF
                    var imgBrush = new ImageBrush(EditorPageImage.Source as BitmapSource);
                    ctx.DrawRectangle(imgBrush, null, imageRect);
        
                    // Warstwa 2: InkCanvas (białe kreski gumki + ewentualne stroki)
                    var inkVisual = new System.Windows.Media.VisualBrush(EditorInkCanvas)
                    {
                        Stretch = Stretch.Fill
                    };
                    ctx.DrawRectangle(inkVisual, null, imageRect);
        
                    // Warstwa 3: Canvas z tekstem i obrazkami
                    var overlayVisual = new System.Windows.Media.VisualBrush(EditorOverlayCanvas)
                    {
                        Stretch = Stretch.Fill
                    };
                    ctx.DrawRectangle(overlayVisual, null, imageRect);
                }
                renderBitmap.Render(drawingVisual);
        
                // Przywróć zoom
                EditorScale.ScaleX = prevScale;
                EditorScale.ScaleY = prevScale;
        
                // Konwertuj do PNG na UI thread, zamroź przed przekazaniem do Task.Run
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));
                using var pngStream = new MemoryStream();
                encoder.Save(pngStream);
                byte[] pngBytes = pngStream.ToArray();
        
                // Tworzenie PDF w tle — PdfSharp, tylko byte[], zero obiektów UI
                byte[] pdfBytes = await Task.Run(() =>
                {
                    using var outStream = new MemoryStream();
                    using var pdfDoc = new PdfSharpPdfDocument();
                    var pdfPage = pdfDoc.AddPage();

                    // Piksele (96 dpi) → punkty PDF (72 dpi)
                    double widthPt  = renderW * 72.0 / 96.0;
                    double heightPt = renderH * 72.0 / 96.0;
                    pdfPage.Width  = PdfSharp.Drawing.XUnit.FromPoint(widthPt);
                    pdfPage.Height = PdfSharp.Drawing.XUnit.FromPoint(heightPt);

                    using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(pdfPage);
                    using var imgStream = new MemoryStream(pngBytes);
                    using var xImage = PdfSharp.Drawing.XImage.FromStream(imgStream);
                    gfx.DrawImage(xImage, 0, 0, widthPt, heightPt);

                    pdfDoc.Save(outStream, false);
                    return outStream.ToArray();
                });
        
                _editedPages[_editorPage] = pdfBytes;
        
                StatusText.Text = "Zapisano · Powrót do organizacji";
                NavOrganize.IsChecked = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Błąd zapisu: {ex.Message}";
            }
            finally
            {
                SaveEditorBtn.IsEnabled = true;
            }
        }

        private void EditorScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            e.Handled = true;

            double oldScale = EditorScale.ScaleX;
            double delta = e.Delta > 0 ? 0.1 : -0.1;
            double newScale = Math.Clamp(oldScale + delta, 0.2, 4.0);
            if (Math.Abs(newScale - oldScale) < 0.001) return;

            Point mouseInScroll = e.GetPosition(EditorScrollViewer);

            EditorScale.ScaleX = newScale;
            EditorScale.ScaleY = newScale;

            double scaleRatio = newScale / oldScale;

            double newOffsetX = scaleRatio * (EditorScrollViewer.HorizontalOffset + mouseInScroll.X)
                                - mouseInScroll.X;
            double newOffsetY = scaleRatio * (EditorScrollViewer.VerticalOffset + mouseInScroll.Y)
                                - mouseInScroll.Y;

            EditorScrollViewer.ScrollToHorizontalOffset(newOffsetX);
            EditorScrollViewer.ScrollToVerticalOffset(newOffsetY);
        }

        private void EditorScroll_PanStart(object sender, MouseButtonEventArgs e)
        {
            if (_currentTool != EditorTool.None) return;
            if (IsInsideOverlayChild(e.OriginalSource as DependencyObject)) return;
            _isPanning = true;
            _panStartPoint = e.GetPosition(EditorScrollViewer);
            EditorScrollViewer.Cursor = Cursors.SizeAll;
            EditorScrollViewer.CaptureMouse();
            e.Handled = true;
        }
        
        private bool IsInsideOverlayChild(DependencyObject? src)
        {
            var current = src;
            while (current != null)
            {
                if (current == EditorOverlayCanvas) return false;
                if (VisualTreeHelper.GetParent(current) == EditorOverlayCanvas) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void EditorScroll_PanMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            Point current = e.GetPosition(EditorScrollViewer);
            double dx = _panStartPoint.X - current.X;
            double dy = _panStartPoint.Y - current.Y;
            EditorScrollViewer.ScrollToHorizontalOffset(
                EditorScrollViewer.HorizontalOffset + dx);
            EditorScrollViewer.ScrollToVerticalOffset(
                EditorScrollViewer.VerticalOffset + dy);
            _panStartPoint = current;
        }

        private void EditorScroll_PanEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            _isPanning = false;
            EditorScrollViewer.Cursor = Cursors.Arrow;
            EditorScrollViewer.ReleaseMouseCapture();
        }

        #endregion

        #region Drag & Drop Organize

        private void OrganizeItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _dragStartPoint = e.GetPosition(null);
            if (sender is Border border && border.Tag is PageItem item)
                _draggedItem = item;
        }

        private void OrganizeItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
            {
                Point pos = e.GetPosition(null);
                Vector diff = _dragStartPoint - pos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is Border border)
                    {
                        var data = new DataObject("pageItem", _draggedItem);
                        try
                        {
                            ClearAllHighlights();
                            DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
                        }
                        finally
                        {
                            _draggedItem = null;
                            ClearAllHighlights();
                        }
                    }
                }
            }
        }

        private void OrganizeItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is PageItem page)
            {
                ShowPreview(page);
                e.Handled = true;
            }
        }

        private void OrganizeItem_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("pageItem") && sender is Border border && border.Tag is PageItem target)
            {
                var dragged = e.Data.GetData("pageItem") as PageItem;
                if (dragged != null && dragged != target)
                    target.IsDropTarget = true;
            }
        }

        private void OrganizeItem_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border && border.Tag is PageItem target)
                target.IsDropTarget = false;
        }

        private void OrganizeItem_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("pageItem") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OrganizeItem_Drop(object sender, DragEventArgs e)
        {
            ClearAllHighlights();
            if (e.Data.GetDataPresent("pageItem"))
            {
                var draggedItem = e.Data.GetData("pageItem") as PageItem;
                if (draggedItem == null) return;
                if (sender is Border border && border.Tag is PageItem targetItem)
                {
                    if (draggedItem == targetItem) return;
                    int oldIndex = _organizePages.IndexOf(draggedItem);
                    int newIndex = _organizePages.IndexOf(targetItem);
                    if (oldIndex < 0 || newIndex < 0) return;
                    _organizePages.Move(oldIndex, newIndex);
                    UpdateOrganizeOrder();
                }
            }
            e.Handled = true;
        }

        private void ClearAllHighlights()
        {
            foreach (var page in _organizePages) page.IsDropTarget = false;
        }

        #endregion

        #region Export (multi-source merge)

        private void UpdateExportButtonState()
        {
            ExportBtn.IsEnabled = _organizePages.Count > 0 || _pages.Any(p => p.IsSelected);
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            List<PageItem> pagesToExport = _organizePages.Count > 0
                ? _organizePages.ToList()
                : _pages.Where(p => p.IsSelected).ToList();

            if (pagesToExport.Count == 0)
            {
                StatusText.Text = "Zaznacz przynajmniej jedną stronę do eksportu";
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "Pliki PDF (*.pdf)|*.pdf",
                Title = "Zapisz wybrane strony jako",
                DefaultExt = ".pdf",
                FileName = "Segmento_eksport.pdf"
            };

            if (saveDialog.ShowDialog() != true) return;

            try
            {
                ExportBtn.IsEnabled = false;
                ExportProgress.Visibility = Visibility.Visible;
                ExportProgress.IsIndeterminate = true;
                StatusText.Text = $"Eksportowanie {pagesToExport.Count} stron...";

                string outputPath = saveDialog.FileName;
                var exportData = pagesToExport
                    .Select(p => (
                        p.SourceBytes,
                        p.OriginalPageNumber,
                        _editedPages.TryGetValue(p, out var eb) ? eb : (byte[]?)null
                    ))
                    .ToList();
                int count = pagesToExport.Count;

                await Task.Run(() => ExportMergedPdf(exportData, outputPath));

                ExportProgress.Visibility = Visibility.Collapsed;
                ExportProgress.IsIndeterminate = false;
                StatusText.Text = $"Wyeksportowano {count} stron do: {System.IO.Path.GetFileName(outputPath)}";
            }
            catch (Exception ex)
            {
                ExportProgress.Visibility = Visibility.Collapsed;
                ExportProgress.IsIndeterminate = false;
                StatusText.Text = $"Błąd eksportu: {GetFriendlyErrorMessage(ex)}";
            }
            finally
            {
                ExportBtn.IsEnabled = true;
            }
        }

        private static void ExportMergedPdf(List<(byte[] SourceBytes, int PageNumber, byte[]? EditedBytes)> pages, string outputPath)
        {
            try
            {
                ExportMergedUsingPdfSharp(pages, outputPath);
                return;
            }
            catch { }

            ExportMergedUsingIText(pages, outputPath);
        }

        private static void ExportMergedUsingPdfSharp(List<(byte[] SourceBytes, int PageNumber, byte[]? EditedBytes)> pages, string outputPath)
        {
            using var outputDocument = new PdfSharpPdfDocument();
            var cache = new Dictionary<byte[], PdfSharpPdfDocument>();

            try
            {
                foreach (var (sourceBytes, pageNumber, editedBytes) in pages)
                {
                    byte[] bytesToUse = editedBytes ?? sourceBytes;
                    int pageToUse = editedBytes != null ? 1 : pageNumber;

                    if (!cache.TryGetValue(bytesToUse, out var srcDoc))
                    {
                        var ms = new MemoryStream(bytesToUse);
                        srcDoc = PdfSharpPdfReader.Open(ms, PdfDocumentOpenMode.Import);
                        cache[bytesToUse] = srcDoc;
                    }

                    if (pageToUse >= 1 && pageToUse <= srcDoc.PageCount)
                        outputDocument.AddPage(srcDoc.Pages[pageToUse - 1]);
                }

                outputDocument.Save(outputPath);
            }
            finally
            {
                foreach (var doc in cache.Values) doc.Dispose();
            }
        }

        private static void ExportMergedUsingIText(List<(byte[] SourceBytes, int PageNumber, byte[]? EditedBytes)> pages, string outputPath)
        {
            using var writer = new ITextPdfWriter(outputPath);
            using var outputDoc = new ITextPdfDocument(writer);

            var cache = new Dictionary<byte[], ITextPdfDocument>();
            try
            {
                foreach (var (sourceBytes, pageNumber, editedBytes) in pages)
                {
                    byte[] bytesToUse = editedBytes ?? sourceBytes;
                    int pageToUse = editedBytes != null ? 1 : pageNumber;

                    if (!cache.TryGetValue(bytesToUse, out var srcDoc))
                    {
                        var reader = new ITextPdfReader(new MemoryStream(bytesToUse));
                        reader.SetUnethicalReading(true);
                        srcDoc = new ITextPdfDocument(reader);
                        cache[bytesToUse] = srcDoc;
                    }
                    srcDoc.CopyPagesTo(pageToUse, pageToUse, outputDoc);
                }
            }
            finally
            {
                foreach (var doc in cache.Values) doc.Close();
            }
        }

        private static string GetFriendlyErrorMessage(Exception ex)
        {
            string msg = ex.Message?.ToLowerInvariant() ?? "";
            if (msg.Contains("password") || msg.Contains("encrypted")) return "Plik PDF jest zaszyfrowany";
            if (msg.Contains("corrupt") || msg.Contains("damaged")) return "Plik PDF jest uszkodzony";
            if (msg.Contains("access") || msg.Contains("denied")) return "Brak dostępu do pliku";
            if (msg.Contains("space")) return "Brak miejsca na dysku";
            return ex.Message;
        }

        #endregion

        #region Helpers

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int order = 0;
            while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
            return $"{size:0.##} {sizes[order]}";
        }

        #endregion
    }
}
