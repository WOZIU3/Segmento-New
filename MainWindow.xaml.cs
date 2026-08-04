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
using Segmento.Controls;
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
        private ReorderDragController? _organizeDrag;
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
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        public MainWindow()
        {
            InitializeComponent();
            PagesList.ItemsSource = _pages;
            OrganizeList.ItemsSource = _organizePages;
            _organizePages.CollectionChanged += OrganizePages_CollectionChanged;
            _organizeDrag = new ReorderDragController(OrganizeList, OrganizeScroll, DragLayer,
                (from, to) => { _organizePages.Move(from, to); UpdateOrganizeOrder(); });
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
                int corner = DWMWCP_ROUND;   // Win11+; na Win10 zwraca blad i jest ignorowane
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
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
                Filter = "Dokumenty i obrazy (*.pdf;*.png;*.jpg;*.jpeg)|*.pdf;*.png;*.jpg;*.jpeg|Pliki PDF (*.pdf)|*.pdf|Obrazy (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Wszystkie pliki (*.*)|*.*",
                Title = "Wybierz pliki PDF lub obrazy",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                _ = LoadFilesAsync(dialog.FileNames);
            }
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                e.Effects = files.Any(f => IsSupportedFile(f))
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
                var supported = files.Where(f => IsSupportedFile(f)).ToArray();
                if (supported.Length > 0) _ = LoadFilesAsync(supported);
            }
        }

        private async Task LoadFilesAsync(string[] filePaths)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();
            var token = _thumbnailCts.Token;

            try
            {
                LoadingText.Text = "Wczytywanie plików...";
                LoadingOverlay.Visibility = Visibility.Visible;

                var newPages = new List<PageItem>();

                foreach (var filePath in filePaths)
                {
                    if (!File.Exists(filePath)) continue;
                    if (!IsSupportedFile(filePath)) continue;

                    var fileBytes = await File.ReadAllBytesAsync(filePath, token);
                    var fileInfo  = new FileInfo(filePath);

                    if (IsImageFile(filePath))
                    {
                        // Obraz PNG/JPG → traktujemy jako 1-stronicowe źródło
                        var source = new PdfSource(filePath, fileBytes, 1, fileInfo.Length);
                        _sources.Add(source);

                        var page = new PageItem(source.Id, source.FileName, 1, source.Bytes);
                        page.PropertyChanged += Page_PropertyChanged;
                        page.PageIndex = _pages.Count + newPages.Count;
                        newPages.Add(page);
                    }
                    else
                    {
                        // PDF
                        int pageCount = await Task.Run(() => GetPageCount(fileBytes), token);
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
                bool isImg = IsImageBytes(_sources[0].Bytes);
                string typeLabel = isImg ? "obraz" : $"{_sources[0].PageCount} stron";
                FileInfoText.Text = $"{typeLabel} · {FormatFileSize(_sources[0].FileSize)}";
            }
            else
            {
                long totalSize = _sources.Sum(s => s.FileSize);
                int imgCount  = _sources.Count(s => IsImageBytes(s.Bytes));
                int pdfCount  = _sources.Count - imgCount;
                string mix = imgCount > 0 && pdfCount > 0
                    ? $"{pdfCount} PDF + {imgCount} obraz(ów)"
                    : imgCount > 0 ? $"{imgCount} obraz(ów)" : $"{_sources.Count} plików PDF";
                FileNameText.Text = mix;
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
            int px = ThumbPixelWidth(pages.Count);
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
                            BitmapImage bitmap;
                            if (IsImageBytes(page.SourceBytes))
                                bitmap = LoadImageThumbnail(page.SourceBytes, px);
                            else
                                bitmap = RenderPageToThumbnail(page.SourceBytes, page.OriginalPageNumber - 1, px);
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

        /// Szerokosc bitmapy miniatury w px. Karta ma 202 px, wiec dajemy zapas
        /// na skalowanie DPI; przy duzych dokumentach schodzimy nizej ze wzgledu na RAM.
        private static int ThumbPixelWidth(int pageCount) =>
            pageCount <= 80 ? 560 : pageCount <= 250 ? 400 : 300;

        private static BitmapImage RenderPageToThumbnail(byte[] pdfBytes, int pageIndex, int pixelWidth)
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            var renderOptions = new PDFtoImage.RenderOptions
            {
                Width = pixelWidth,
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
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapImage LoadImageThumbnail(byte[] imageBytes, int pixelWidth)
        {
            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(imageBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.DecodePixelWidth = pixelWidth;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapImage LoadImageForEditor(byte[] imageBytes)
        {
            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(imageBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
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
                byte[] previewBytes = page.SourceBytes;
                int    previewPage  = page.OriginalPageNumber;

                // Dla stron-obrazów konwertuj do jednostroncowego PDF na podgląd
                if (IsImageBytes(page.SourceBytes))
                {
                    previewBytes = ImageBytesToSinglePagePdf(page.SourceBytes);
                    previewPage  = 1;
                }

                var preview = new PreviewWindow(previewBytes, previewPage) { Owner = this };
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

            BitmapImage bitmap;
            if (IsImageBytes(page.SourceBytes))
                bitmap = await Task.Run(() => LoadImageForEditor(page.SourceBytes));
            else
                bitmap = await Task.Run(() => RenderPageToEditorBitmap(page.SourceBytes, page.OriginalPageNumber - 1));

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

            EditorScrollViewer.Cursor = _currentTool == EditorTool.None
                ? Cursors.SizeAll : Cursors.Arrow;
        }

        private void ApplyToolToInkCanvas()
        {
            switch (_currentTool)
            {
                case EditorTool.Eraser:
                    EditorInkCanvas.DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
                    {
                        Color = Colors.White,
                        Width = 25,
                        Height = 25,
                        StylusTip = System.Windows.Ink.StylusTip.Rectangle,
                        IsHighlighter = false
                    };
                    EditorInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    EditorInkCanvas.IsHitTestVisible = true;
                    EditorInkCanvas.Cursor = Cursors.Cross;
                    break;
        
                default:
                    EditorInkCanvas.EditingMode = InkCanvasEditingMode.None;
                    EditorInkCanvas.IsHitTestVisible = false;
                    EditorInkCanvas.Cursor = Cursors.Arrow;
                    break;
            }
        }

        // ================================================================
        // NARZĘDZIE: ZMIANA ROZMIARU (%)
        // ================================================================

        private void ToolResize_Click(object sender, RoutedEventArgs e)
        {
            if (_editorPage == null)
            {
                StatusText.Text = "Najpierw załaduj stronę do edytora";
                return;
            }

            // Aktualne info o stronie
            if (EditorPageImage.Source is BitmapSource bmp)
            {
                ResizeCurrentDims.Text = $"Wymiary: {bmp.PixelWidth} × {bmp.PixelHeight} px";
            }
            else
            {
                ResizeCurrentDims.Text = "Wymiary: —";
            }

            long srcSize = _editorPage.SourceBytes.LongLength;
            ResizeCurrentMB.Text = $"Rozmiar pliku: {srcSize / 1024.0 / 1024.0:F2} MB";

            ResizePercentBox.Text = "100";
            ResizeNewMB.Text      = "Szacowany rozmiar po zmianie: —";

            ResizePopup.IsOpen = true;
            ResizePercentBox.Focus();
            ResizePercentBox.SelectAll();
        }

        private void ResizePercent_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_editorPage == null) return;
            if (ResizeNewMB == null) return;

            if (int.TryParse(ResizePercentBox.Text, out int pct) && pct > 0 && pct <= 1000)
            {
                double scale       = pct / 100.0;
                long   srcSize     = _editorPage.SourceBytes.LongLength;
                // Szacunek: rozmiar obrazu skaluje się ~kwadratowo z rozdzielczością
                double estimatedMB = srcSize * scale * scale / 1024.0 / 1024.0;
                ResizeNewMB.Text = $"Szacowany rozmiar po zmianie: ~{estimatedMB:F2} MB";

                // Pokaż nowe wymiary
                if (EditorPageImage.Source is BitmapSource bmp)
                {
                    int newW = (int)(bmp.PixelWidth  * scale);
                    int newH = (int)(bmp.PixelHeight * scale);
                    ResizeNewMB.Text = $"Nowe wymiary: {newW} × {newH} px\nSzacowany rozmiar: ~{estimatedMB:F2} MB";
                }
            }
            else
            {
                ResizeNewMB.Text = "Szacowany rozmiar po zmianie: —";
            }
        }

        private async void ResizeApply_Click(object sender, RoutedEventArgs e)
        {
            if (_editorPage == null) return;
            if (!int.TryParse(ResizePercentBox.Text, out int pct) || pct <= 0 || pct > 1000)
            {
                StatusText.Text = "Podaj wartość procentową od 1 do 1000";
                return;
            }

            ResizePopup.IsOpen    = false;
            ResizeApplyBtn.IsEnabled = false;

            double scale    = pct / 100.0;
            bool   isImgSrc = IsImageBytes(_editorPage.SourceBytes);

            StatusText.Text = $"Zmiana rozmiaru do {pct}%...";

            byte[] srcBytes = _editorPage.SourceBytes;
            int    pageIdx  = _editorPage.OriginalPageNumber - 1;

            BitmapImage newBitmap = await Task.Run(() =>
            {
                if (isImgSrc)
                    return ResizeImageBytes(srcBytes, scale);
                else
                    return RenderPageAtScale(srcBytes, pageIdx, scale);
            });

            // Wyczyść nakładki — rozmiar się zmienił
            EditorOverlayCanvas.Children.Clear();
            EditorInkCanvas.Strokes.Clear();

            EditorPageImage.Source = newBitmap;

            StatusText.Text = $"Rozmiar zmieniony do {pct}% ({newBitmap.PixelWidth}×{newBitmap.PixelHeight} px) · Zapisz aby zachować";

            ResizeApplyBtn.IsEnabled = true;
        }

        private static BitmapImage ResizeImageBytes(byte[] imageBytes, double scale)
        {
            var original = new BitmapImage();
            using (var ms = new MemoryStream(imageBytes))
            {
                original.BeginInit();
                original.CacheOption = BitmapCacheOption.OnLoad;
                original.StreamSource = ms;
                original.EndInit();
            }
            original.Freeze();

            var transformed = new TransformedBitmap(original, new ScaleTransform(scale, scale));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transformed));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            byte[] pngBytes = outMs.ToArray();

            var result = new BitmapImage();
            using (var ms2 = new MemoryStream(pngBytes))
            {
                result.BeginInit();
                result.CacheOption = BitmapCacheOption.OnLoad;
                result.StreamSource = ms2;
                result.EndInit();
            }
            result.Freeze();
            return result;
        }

        private static BitmapImage RenderPageAtScale(byte[] pdfBytes, int pageIndex, double scale)
        {
            int dpi = (int)(150 * scale);
            dpi = Math.Max(10, Math.Min(dpi, 600));

            using var pdfStream = new MemoryStream(pdfBytes);
            var opts = new PDFtoImage.RenderOptions { Dpi = dpi, WithAspectRatio = true };
            using var skBitmap = PDFtoImage.Conversion.ToImage(pdfStream, page: pageIndex, options: opts);
            using var skImage  = SkiaSharp.SKImage.FromBitmap(skBitmap);
            using var skData   = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);

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

            tb.GotFocus  += (s, ev) => Activate();
            tb.LostFocus += (s, ev) => Deactivate();

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
            container.Loaded += (s, ev) => tb.Focus();
        }

        private Popup BuildTextToolbar(TextBox tb, Grid container)
        {
            // A▲ — większa czcionka
            var incBtn = new Button
            {
                Content = "A▲", Focusable = false,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(1, 0, 1, 0),
                Foreground = Brushes.White, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand
            };
            incBtn.Click += (s, ev) => { tb.FontSize = Math.Min(tb.FontSize + 2, 72); tb.Focus(); };
        
            // A▼ — mniejsza czcionka
            var decBtn = new Button
            {
                Content = "A▼", Focusable = false,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(1, 0, 1, 0),
                Foreground = Brushes.White, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand
            };
            decBtn.Click += (s, ev) => { tb.FontSize = Math.Max(tb.FontSize - 2, 8); tb.Focus(); };
        
            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
            sizeRow.Children.Add(incBtn);
            sizeRow.Children.Add(decBtn);
        
            // Okrągłe próbki kolorów — dwie tablice zamiast krotek
            var swatchColors = new Color[]
            {
                Colors.White,
                Colors.Black,
                Colors.Red,
                Color.FromRgb(30, 144, 255)
            };
            var swatchBrushes = new Brush[]
            {
                Brushes.White,
                Brushes.Black,
                Brushes.Red,
                new SolidColorBrush(Color.FromRgb(30, 144, 255))
            };
        
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            for (int ci = 0; ci < swatchColors.Length; ci++)
            {
                var localBrush  = swatchBrushes[ci];
                var localColor  = swatchColors[ci];
                var swatch = new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill            = new SolidColorBrush(localColor),
                    Stroke          = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                    StrokeThickness = 1,
                    Margin          = new Thickness(3, 0, 3, 0),
                    Cursor          = Cursors.Hand
                };
                swatch.MouseLeftButtonDown += (s, ev) => { tb.Foreground = localBrush; tb.Focus(); };
                colorRow.Children.Add(swatch);
            }
        
            // Przycisk Kosz
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
            delBtn.Click += (s, ev) => EditorOverlayCanvas.Children.Remove(container);
        
            var sep1 = new Border { Width = 1, Margin = new Thickness(4, 3, 4, 3), Background = new SolidColorBrush(Color.FromRgb(70, 70, 70)) };
            var sep2 = new Border { Width = 1, Margin = new Thickness(4, 3, 4, 3), Background = new SolidColorBrush(Color.FromRgb(70, 70, 70)) };
        
            var toolStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            toolStack.Children.Add(sizeRow);
            toolStack.Children.Add(sep1);
            toolStack.Children.Add(colorRow);
            toolStack.Children.Add(sep2);
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

            string[] opLabels = { "25%", "50%", "75%", "100%" };
            double[] opValues = { 0.25, 0.50, 0.75, 1.0 };

            for (int oi = 0; oi < opLabels.Length; oi++)
            {
                double opacity = opValues[oi];
                var btn = new Button
                {
                    Content = opLabels[oi],
                    Focusable = false,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin  = new Thickness(1, 0, 1, 0),
                    Foreground = Brushes.White,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 11, Cursor = Cursors.Hand
                };
                btn.Click += (s, ev) => img.Opacity = opacity;
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
            delBtn.Click += (s, ev) => EditorOverlayCanvas.Children.Remove(container);

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

            var saveDialog = new SaveFileDialog
            {
                Filter     = "Dokument PDF (*.pdf)|*.pdf|Obraz PNG (*.png)|*.png",
                Title      = "Zapisz edytowaną stronę",
                DefaultExt = ".pdf",
                FileName   = System.IO.Path.GetFileNameWithoutExtension(_editorPage.SourceFileName) + "_edytowany"
            };
            if (saveDialog.ShowDialog() != true) return;

            try
            {
                SaveEditorBtn.IsEnabled = false;
                StatusText.Text = "Zapisywanie w jakości 300 DPI...";

                double prevScale = EditorScale.ScaleX;
                EditorScale.ScaleX = 1;
                EditorScale.ScaleY = 1;
                EditorCanvasGrid.UpdateLayout();

                byte[] sourceBytes        = _editorPage.SourceBytes;
                int    originalPageNumber = _editorPage.OriginalPageNumber;
                bool   isImgSrc           = IsImageBytes(sourceBytes);

                // Render bazy w 300 DPI w tle
                BitmapSource highResBitmap = await Task.Run(() =>
                {
                    if (isImgSrc)
                        return (BitmapSource)LoadImageForEditor(sourceBytes);

                    using var pdfStream = new MemoryStream(sourceBytes);
                    var opts = new PDFtoImage.RenderOptions { Dpi = 300, WithAspectRatio = true };
                    using var skBitmap = PDFtoImage.Conversion.ToImage(
                        pdfStream, page: originalPageNumber - 1, options: opts);
                    using var skImage = SkiaSharp.SKImage.FromBitmap(skBitmap);
                    using var skData  = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(skData.ToArray()))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    return (BitmapSource)bmp;
                });

                int renderW = highResBitmap.PixelWidth;
                int renderH = highResBitmap.PixelHeight;

                double scaleX = EditorPageImage.ActualWidth  > 0 ? renderW / EditorPageImage.ActualWidth  : 1;
                double scaleY = EditorPageImage.ActualHeight > 0 ? renderH / EditorPageImage.ActualHeight : 1;

                var renderBitmap  = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                var drawingVisual = new System.Windows.Media.DrawingVisual();

                using (var ctx = drawingVisual.RenderOpen())
                {
                    var fullRect = new Rect(0, 0, renderW, renderH);

                    ctx.DrawImage(highResBitmap, fullRect);

                    ctx.PushTransform(new ScaleTransform(scaleX, scaleY));
                    ctx.DrawRectangle(new System.Windows.Media.VisualBrush(EditorInkCanvas)    { Stretch = Stretch.Fill }, null, new Rect(0, 0, renderW / scaleX, renderH / scaleY));
                    ctx.Pop();

                    ctx.PushTransform(new ScaleTransform(scaleX, scaleY));
                    ctx.DrawRectangle(new System.Windows.Media.VisualBrush(EditorOverlayCanvas){ Stretch = Stretch.Fill }, null, new Rect(0, 0, renderW / scaleX, renderH / scaleY));
                    ctx.Pop();
                }
                renderBitmap.Render(drawingVisual);

                EditorScale.ScaleX = prevScale;
                EditorScale.ScaleY = prevScale;

                var pngEncoder = new PngBitmapEncoder();
                pngEncoder.Frames.Add(BitmapFrame.Create(renderBitmap));
                using var pngStream = new MemoryStream();
                pngEncoder.Save(pngStream);
                byte[] pngBytes = pngStream.ToArray();

                string outputPath = saveDialog.FileName;
                bool   savePng    = outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                if (savePng)
                {
                    await Task.Run(() => File.WriteAllBytes(outputPath, pngBytes));
                    StatusText.Text = $"Zapisano PNG 300 DPI: {System.IO.Path.GetFileName(outputPath)}";
                }
                else
                {
                    byte[] pdfBytes = await Task.Run(() =>
                    {
                        double widthPt, heightPt;
                        if (isImgSrc)
                        {
                            widthPt  = renderW * 72.0 / 300.0;
                            heightPt = renderH * 72.0 / 300.0;
                        }
                        else
                        {
                            try
                            {
                                var srcMs = new MemoryStream();
                                srcMs.Write(sourceBytes, 0, sourceBytes.Length);
                                srcMs.Position = 0;
                                using var srcDoc = PdfSharpPdfReader.Open(srcMs, PdfDocumentOpenMode.InformationOnly);
                                widthPt  = srcDoc.Pages[originalPageNumber - 1].Width.Point;
                                heightPt = srcDoc.Pages[originalPageNumber - 1].Height.Point;
                            }
                            catch
                            {
                                widthPt  = renderW * 72.0 / 300.0;
                                heightPt = renderH * 72.0 / 300.0;
                            }
                        }

                        using var outStream = new MemoryStream();
                        using var pdfDoc    = new PdfSharpPdfDocument();
                        var pdfPage = pdfDoc.AddPage();
                        pdfPage.Width  = PdfSharp.Drawing.XUnit.FromPoint(widthPt);
                        pdfPage.Height = PdfSharp.Drawing.XUnit.FromPoint(heightPt);

                        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(pdfPage);
                        var imgStream = new MemoryStream();
                        imgStream.Write(pngBytes, 0, pngBytes.Length);
                        imgStream.Position = 0;
                        using var xImage = PdfSharp.Drawing.XImage.FromStream(imgStream);
                        gfx.DrawImage(xImage, 0, 0, widthPt, heightPt);

                        pdfDoc.Save(outStream, false);
                        return outStream.ToArray();
                    });

                    _editedPages[_editorPage] = pdfBytes;
                    await Task.Run(() => File.WriteAllBytes(outputPath, pdfBytes));
                    StatusText.Text = $"Zapisano PDF 300 DPI: {System.IO.Path.GetFileName(outputPath)}";
                }

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

        private void OrganizeItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is PageItem page)
            {
                ShowPreview(page);
                e.Handled = true;
            }
        }

        #endregion

        #region Export (multi-source merge)

        private void UpdateExportButtonState()
        {
            ExportBtn.IsEnabled = _organizePages.Count > 0 || _pages.Any(p => p.IsSelected);
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget    = btn,
                Placement          = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                VerticalOffset     = 4,
                AllowsTransparency = true,
                StaysOpen          = false
            };

            var itemAll = CreateDropdownButton("Wszystkie — jeden plik PDF");
            var itemSep = CreateDropdownButton("Osobno — osobne pliki PDF");

            itemAll.Click += (s, ev) => { popup.IsOpen = false; ExportAll_Click(s, ev); };
            itemSep.Click += (s, ev) => { popup.IsOpen = false; ExportSeparate_Click(s, ev); };

            var stack = new StackPanel { Margin = new Thickness(4) };
            stack.Children.Add(itemAll);
            stack.Children.Add(itemSep);

            popup.Child = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(63, 63, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Child           = stack,
                MinWidth        = 240
            };

            popup.IsOpen = true;
        }

        private static Button CreateDropdownButton(string header)
        {
            var t      = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.BackgroundProperty,   Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.MarginProperty,       new Thickness(2, 1, 2, 1));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.MarginProperty,              new Thickness(14, 10, 14, 10));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            border.AppendChild(cp);
            t.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(50, 50, 55)), "bd"));
            t.Triggers.Add(hover);

            return new Button
            {
                Content    = header,
                Template   = t,
                Foreground = Brushes.White,
                FontSize   = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Focusable  = false,
                Cursor     = Cursors.Hand
            };
        }


        private async void ExportAll_Click(object sender, RoutedEventArgs e)
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
                Filter     = "Pliki PDF (*.pdf)|*.pdf",
                Title      = "Zapisz wszystkie strony jako jeden plik PDF",
                DefaultExt = ".pdf",
                FileName   = "Segmento_eksport.pdf"
            };
            if (saveDialog.ShowDialog() != true) return;

            try
            {
                ExportBtn.IsEnabled = false;
                ExportProgress.Visibility = Visibility.Visible;
                ExportProgress.IsIndeterminate = true;
                int count = pagesToExport.Count;
                StatusText.Text = $"Eksportowanie {count} stron do jednego pliku...";

                string outputPath = saveDialog.FileName;
                var exportData = pagesToExport
                    .Select(p => (
                        p.SourceBytes,
                        p.OriginalPageNumber,
                        _editedPages.TryGetValue(p, out var eb) ? eb : (byte[]?)null))
                    .ToList();

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

        private async void ExportSeparate_Click(object sender, RoutedEventArgs e)
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
                Filter     = "Pliki PDF (*.pdf)|*.pdf",
                Title      = "Podaj nazwę bazową — każda strona zostanie zapisana osobno",
                DefaultExt = ".pdf",
                FileName   = "Segmento_strona"
            };
            if (saveDialog.ShowDialog() != true) return;

            string dir      = System.IO.Path.GetDirectoryName(saveDialog.FileName) ?? ".";
            string baseName = System.IO.Path.GetFileNameWithoutExtension(saveDialog.FileName);

            try
            {
                ExportBtn.IsEnabled = false;
                ExportProgress.Visibility = Visibility.Visible;
                ExportProgress.IsIndeterminate = false;
                ExportProgress.Minimum = 0;
                ExportProgress.Maximum = pagesToExport.Count;
                ExportProgress.Value   = 0;

                int count = pagesToExport.Count;
                StatusText.Text = $"Eksportowanie {count} osobnych plików PDF...";

                for (int pi = 0; pi < pagesToExport.Count; pi++)
                {
                    var    page    = pagesToExport[pi];
                    string outPath = System.IO.Path.Combine(dir, $"{baseName}_{pi + 1:D3}.pdf");

                    bool hasEdited = _editedPages.TryGetValue(page, out var editedBytes);
                    var  single    = new List<(byte[], int, byte[]?)>
                    {
                        (page.SourceBytes, page.OriginalPageNumber, hasEdited ? editedBytes : (byte[]?)null)
                    };

                    await Task.Run(() => ExportMergedPdf(single, outPath));

                    ExportProgress.Value = pi + 1;
                    StatusText.Text = $"Eksport osobno: {pi + 1} / {count}";
                }

                ExportProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Wyeksportowano {count} plików PDF do: {dir}";
            }
            catch (Exception ex)
            {
                ExportProgress.Visibility = Visibility.Collapsed;
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
                    int    pageToUse  = editedBytes != null ? 1 : pageNumber;

                    // Strona-obraz (PNG/JPG) → konwertuj do strony PDF
                    if (IsImageBytes(bytesToUse))
                    {
                        byte[] imgPdfBytes = ImageBytesToSinglePagePdf(bytesToUse);
                        var imgMs = new MemoryStream();
                        imgMs.Write(imgPdfBytes, 0, imgPdfBytes.Length);
                        imgMs.Position = 0;
                        using var imgDoc = PdfSharpPdfReader.Open(imgMs, PdfDocumentOpenMode.Import);
                        outputDocument.AddPage(imgDoc.Pages[0]);
                        continue;
                    }

                    if (!cache.TryGetValue(bytesToUse, out var srcDoc))
                    {
                        var ms = new MemoryStream();
                        ms.Write(bytesToUse, 0, bytesToUse.Length);
                        ms.Position = 0;
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
            using var writer    = new ITextPdfWriter(outputPath);
            using var outputDoc = new ITextPdfDocument(writer);

            var cache = new Dictionary<byte[], ITextPdfDocument>();
            try
            {
                foreach (var (sourceBytes, pageNumber, editedBytes) in pages)
                {
                    byte[] bytesToUse = editedBytes ?? sourceBytes;
                    int    pageToUse  = editedBytes != null ? 1 : pageNumber;

                    // Strona-obraz → konwertuj przez PdfSharp do PDF
                    if (IsImageBytes(bytesToUse))
                    {
                        bytesToUse = ImageBytesToSinglePagePdf(bytesToUse);
                        pageToUse  = 1;
                    }

                    if (!cache.TryGetValue(bytesToUse, out var srcDoc))
                    {
                        var ms = new MemoryStream();
                        ms.Write(bytesToUse, 0, bytesToUse.Length);
                        ms.Position = 0;
                        var reader = new ITextPdfReader(ms);
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

        // ── Wykrywanie typu pliku ──────────────────────────────────────────

        private static bool IsSupportedFile(string path)
        {
            return path.EndsWith(".pdf",  StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".png",  StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImageFile(string path)
        {
            return path.EndsWith(".png",  StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImageBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return false;
            // PNG: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;
            // JPEG: FF D8
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true;
            return false;
        }

        // ── Konwersja obraz → jednostronicowy PDF (PdfSharp) ─────────────

        private static byte[] ImageBytesToSinglePagePdf(byte[] imageBytes)
        {
            using var imgStream = new MemoryStream();
            imgStream.Write(imageBytes, 0, imageBytes.Length);
            imgStream.Position = 0;

            using var xImage = PdfSharp.Drawing.XImage.FromStream(imgStream);

            using var outStream = new MemoryStream();
            using var pdfDoc    = new PdfSharpPdfDocument();
            var page = pdfDoc.AddPage();
            page.Width  = PdfSharp.Drawing.XUnit.FromPoint(xImage.PointWidth);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(xImage.PointHeight);

            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            gfx.DrawImage(xImage, 0, 0, xImage.PointWidth, xImage.PointHeight);

            pdfDoc.Save(outStream, false);
            return outStream.ToArray();
        }

        // ── Render strony PDF do PNG bytes (300 DPI) ─────────────────────

        private static byte[] RenderPageToPngBytes(byte[] pdfBytes, int pageIndex, int dpi)
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            var opts = new PDFtoImage.RenderOptions { Dpi = dpi, WithAspectRatio = true };
            using var skBitmap = PDFtoImage.Conversion.ToImage(pdfStream, page: pageIndex, options: opts);
            using var skImage  = SkiaSharp.SKImage.FromBitmap(skBitmap);
            using var skData   = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return skData.ToArray();
        }

        #endregion
    }
}
