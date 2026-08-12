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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PDFtoImage;
using SkiaSharp;
using System.Windows.Controls.Primitives;
using Segmento.Controls;
using Segmento.Editor;
using Segmento.Editor.Annotations;
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
        private readonly List<PdfSource> _sources = new();
        private readonly ObservableCollection<PageItem> _pages = new();
        private readonly ObservableCollection<PageItem> _organizePages = new();
        private CancellationTokenSource? _thumbnailCts;
        private ReorderDragController? _organizeDrag;
        private readonly Dictionary<PageItem, byte[]> _editedPages = new();

        // --- Nowy edytor (model obiektowy) ---
        private readonly EditorDocument _doc = new();
        private System.Windows.Threading.DispatcherTimer? _zoomDebounce;
        private bool _suppressStripSelection;
        private bool _suppressPropsUpdate;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_MAINWINDOW = 2;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

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
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        /// <summary>Skróty globalne edytora (Ctrl+0 — dopasuj do okna).</summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (EditorView.Visibility != Visibility.Visible) return;
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            if (e.Key is Key.D0 or Key.NumPad0)
            {
                ZoomFit();
                e.Handled = true;
            }
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

        private const double SidebarExpandedWidth = 240;
        private const double SidebarCollapsedWidth = 64;
        private bool _sidebarCollapsed = true;

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;

            // Tag steruje stylem SidebarLabel (fade etykiet) w Styles.xaml.
            Sidebar.Tag = _sidebarCollapsed ? "Collapsed" : "Expanded";
            SidebarToggleBtn.ToolTip = _sidebarCollapsed ? "Rozwiń panel" : "Zwiń panel";

            var icon = (TextBlock)SidebarToggleBtn.Template.FindName("tgl", SidebarToggleBtn);
            icon.Text = _sidebarCollapsed ? "\uE76C" : "\uE76B";

            Sidebar.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(_sidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth,
                                    new Duration(TimeSpan.FromMilliseconds(220)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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
                ActivateEditor();
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
            ResetEditor();

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

        private string _editorSignature = "";
        private System.Windows.Data.ListCollectionView? _stripView;
        private readonly DocMetadata _docMetadata = new();
        private readonly SecurityOptions _security = new();

        private void ActivateEditor()
        {
            var sig = string.Join("|", _organizePages.Select(p => p.SourceFileId + ":" + p.OriginalPageNumber + ":" + p.PageIndex));
            if (sig == _editorSignature && _doc.Pages.Count > 0) return;
            _editorSignature = sig;

            try { _doc.LoadFrom(_organizePages); }
            catch (Exception ex) { StatusText.Text = "Błąd wczytywania edytora: " + ex.Message; return; }

            Surface.Attach(_doc);
            _stripView = new System.Windows.Data.ListCollectionView(_doc.Pages)
            { Filter = o => o is EditorPage ep && !ep.IsDeleted };
            EditorPageStrip.ItemsSource = _stripView;

            NavEditor.IsEnabled = _doc.Pages.Count > 0;
            if (_doc.Pages.Count > 0)
            {
                SelectStripPage(_doc.Pages.FirstOrDefault(p => !p.IsDeleted));
            }
        }

        private void ResetEditor()
        {
            _editorSignature = "";
            _doc.Pages.Clear();
            _doc.History.Clear();
            if (Surface != null) Surface.SetPage(null);
            if (EditorPageStrip != null) EditorPageStrip.ItemsSource = null;
            if (LayersList != null) LayersList.ItemsSource = null;
            _stripView = null;
            UpdatePropsPanel();
        }

        private async void ShowPage(EditorPage? page)
        {
            if (page == null)
            {
                _doc.Current = null;
                Surface.SetPage(null);
                LayersList.ItemsSource = null;
                UpdatePropsPanel();
                return;
            }
            _doc.Current = page;
            Surface.SetPage(page);
            LayersList.ItemsSource = page.Annotations;
            UpdatePropsPanel();
            ZoomFit();
            Surface.Focus();
            await EnsureBackdropAsync(page);
        }

        private async System.Threading.Tasks.Task EnsureBackdropAsync(EditorPage page)
        {
            try
            {
                int widthPx = EditorRenderer.TargetWidth(page.WidthPoints * Surface.Scale);
                await _doc.Renderer.EnsureBackdropAsync(page, widthPx);
                if (_doc.Current == page) Surface.Refresh();
            }
            catch (Exception ex) { StatusText.Text = "Błąd renderowania: " + ex.Message; }
        }

        // ── Narzędzia ────────────────────────────────────────────────────

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || sender is not RadioButton rb || rb.Tag is not string tag) return;

            if (tag == "Image")
            {
                var dlg = new OpenFileDialog { Filter = "Obrazy|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(dlg.FileName);
                        Surface.PlaceImage(bytes, ImageAspect(bytes));
                    }
                    catch (Exception ex) { StatusText.Text = "Nie udało się wstawić obrazu: " + ex.Message; }
                }
                ToolSelect.IsChecked = true;
                return;
            }

            if (Enum.TryParse<SurfaceTool>(tag, out var tool))
                Surface.CurrentTool = tool;

            StatusText.Text = tag switch
            {
                "Polyline" => "Klikaj punkty łamanej, Enter kończy, Esc anuluje",
                "Redact" => "Zaznacz obszar do trwałego usunięcia treści (redakcja)",
                _ => "Gotowy"
            };
        }

        private static double ImageAspect(byte[] bytes)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms; bmp.EndInit();
                return bmp.PixelHeight > 0 ? (double)bmp.PixelWidth / bmp.PixelHeight : 1.0;
            }
            catch { return 1.0; }
        }

        // ── Zoom ─────────────────────────────────────────────────────────

        private void SetZoom(double scale)
        {
            Surface.Scale = scale;
            ZoomLabel.Text = $"{Math.Round(scale * 100)}%";
            ScheduleBackdrop();
        }

        private void ScheduleBackdrop()
        {
            _zoomDebounce ??= new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(220) };
            _zoomDebounce.Tick -= ZoomDebounce_Tick;
            _zoomDebounce.Tick += ZoomDebounce_Tick;
            _zoomDebounce.Stop();
            _zoomDebounce.Start();
        }

        private async void ZoomDebounce_Tick(object? sender, EventArgs e)
        {
            _zoomDebounce?.Stop();
            if (_doc.Current != null) await EnsureBackdropAsync(_doc.Current);
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Surface.Scale * 1.2);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Surface.Scale / 1.2);
        private void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomFit();

        private void ZoomFit()
        {
            var page = _doc.Current;
            if (page == null) return;
            double vw = EditorScrollViewer.ViewportWidth, vh = EditorScrollViewer.ViewportHeight;
            if (vw <= 0 || vh <= 0) { SetZoom(1.0); return; }
            double s = Math.Min((vw - 100) / page.WidthPoints, (vh - 100) / page.HeightPoints);
            SetZoom(Math.Clamp(s, 0.1, 4.0));
        }

        private void EditorScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SetZoom(Surface.Scale * (e.Delta > 0 ? 1.15 : 1 / 1.15));
                e.Handled = true;
            }
        }

        // ── Undo / Redo ──────────────────────────────────────────────────

        private void Undo_Click(object sender, RoutedEventArgs e)
        { _doc.History.Undo(); Surface.Selection.Clear(); Surface.Refresh(); UpdatePropsPanel(); }

        private void Redo_Click(object sender, RoutedEventArgs e)
        { _doc.History.Redo(); Surface.Selection.Clear(); Surface.Refresh(); UpdatePropsPanel(); }

        // ── Pasek stron ──────────────────────────────────────────────────

        private void EditorPageStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressStripSelection) return;
            if (EditorPageStrip.SelectedItem is EditorPage page) ShowPage(page);
        }

        private void RotateRight_Click(object sender, RoutedEventArgs e)
        {
            if (_doc.Current == null) return;
            _doc.History.Push(new RotatePageCommand(_doc.Current, 90));
            StatusText.Text = "Strona zostanie obrócona przy zapisie";
        }

        private void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc.Current == null) return;
            var toDelete = _doc.Current;
            int at = _doc.Pages.IndexOf(toDelete);

            // Następna żywa strona po usuwanej, a gdy brak — poprzednia.
            var next = _doc.Pages.Skip(at + 1).FirstOrDefault(p => !p.IsDeleted)
                       ?? _doc.Pages.Take(Math.Max(0, at)).LastOrDefault(p => !p.IsDeleted);

            _doc.History.Push(new DeletePageCommand(toDelete));
            _stripView?.Refresh();
            SelectStripPage(next);
        }

        private void InsertPage_Click(object sender, RoutedEventArgs e)
        {
            int index = _doc.Current != null ? _doc.Pages.IndexOf(_doc.Current) + 1 : _doc.Pages.Count;
            var cmd = new InsertBlankPageCommand(_doc, index, 595, 842);
            _doc.History.Push(cmd);
            _stripView?.Refresh();
            var inserted = index >= 0 && index < _doc.Pages.Count ? _doc.Pages[index] : _doc.Pages.LastOrDefault();
            SelectStripPage(inserted);
        }

        /// <summary>Ustawia stronę w pasku miniatur i na powierzchni (bez pętli zdarzeń zaznaczenia).</summary>
        private void SelectStripPage(EditorPage? page)
        {
            _suppressStripSelection = true;
            try { EditorPageStrip.SelectedItem = page; }
            finally { _suppressStripSelection = false; }
            ShowPage(page);
        }

        // ── Powierzchnia ─────────────────────────────────────────────────

        private void Surface_SelectionChanged(object sender, EventArgs e)
        {
            UpdatePropsPanel();
            if (_suppressPropsUpdate) return;
            _suppressPropsUpdate = true;
            LayersList.SelectedItem = Surface.Selection.Items.Count > 0 ? Surface.Selection.Items[0] : null;
            _suppressPropsUpdate = false;
        }

        private void Surface_ContentChanged(object sender, EventArgs e)
        {
            _doc.MarkDirty();
        }

        private void LayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPropsUpdate) return;
            if (LayersList.SelectedItem is AnnotationBase a)
            {
                _suppressPropsUpdate = true;
                Surface.Selection.Set(a);
                _suppressPropsUpdate = false;
                UpdatePropsPanel();
            }
        }

        // ── Panel właściwości ────────────────────────────────────────────

        private IEnumerable<AnnotationBase> Sel => Surface.Selection.Items;

        private void UpdatePropsPanel()
        {
            var sel = Sel.ToList();
            bool has = sel.Count > 0;
            PropsEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            PropCommon.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

            var first = sel.FirstOrDefault();
            bool isText = first is TextAnnotation;
            bool isShape = first is ShapeAnnotation;
            bool hasColor = first is TextAnnotation or ShapeAnnotation or InkAnnotation or HighlightAnnotation;

            PropText.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
            PropShape.Visibility = isShape ? Visibility.Visible : Visibility.Collapsed;
            PropColorRow.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;

            if (!has) return;
            _suppressPropsUpdate = true;
            PropOpacity.Value = first!.Opacity;
            if (first is TextAnnotation t)
            {
                PropFontSize.Value = t.FontSizePoints;
                PropBold.IsChecked = t.Bold;
                PropItalic.IsChecked = t.Italic;
            }
            if (first is ShapeAnnotation s)
            {
                PropThickness.Value = s.StrokeThicknessPoints;
                PropDashed.IsChecked = s.Dashed;
            }
            _suppressPropsUpdate = false;
        }

        private void AfterPropChange() { Surface.Refresh(); _doc.MarkDirty(); }

        private void PropOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressPropsUpdate) return;
            foreach (var a in Sel) a.Opacity = e.NewValue;
            AfterPropChange();
        }

        private void PropFontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressPropsUpdate) return;
            foreach (var a in Sel) if (a is TextAnnotation t) t.FontSizePoints = e.NewValue;
            AfterPropChange();
        }

        private void PropBold_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressPropsUpdate) return;
            foreach (var a in Sel) if (a is TextAnnotation t) t.Bold = PropBold.IsChecked == true;
            AfterPropChange();
        }

        private void PropItalic_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressPropsUpdate) return;
            foreach (var a in Sel) if (a is TextAnnotation t) t.Italic = PropItalic.IsChecked == true;
            AfterPropChange();
        }

        private void PropThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressPropsUpdate) return;
            foreach (var a in Sel)
            {
                if (a is ShapeAnnotation s) s.StrokeThicknessPoints = e.NewValue;
                else if (a is InkAnnotation ink) ink.ThicknessPoints = e.NewValue;
            }
            Surface.NewThickness = e.NewValue;
            AfterPropChange();
        }

        private void PropDashed_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressPropsUpdate) return;
            bool d = PropDashed.IsChecked == true;
            foreach (var a in Sel) if (a is ShapeAnnotation s) s.Dashed = d;
            Surface.NewDashed = d;
            AfterPropChange();
        }

        private void PropColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string hex) return;
            var col = (Color)ColorConverter.ConvertFromString(hex);
            foreach (var a in Sel)
            {
                switch (a)
                {
                    case TextAnnotation t: t.Foreground = col; break;
                    case ShapeAnnotation s: s.Stroke = col; break;
                    case InkAnnotation ink: ink.Color = col; break;
                    case HighlightAnnotation h: h.Color = col; break;
                }
            }
            Surface.NewStroke = col;
            Surface.NewHighlightColor = col;
            AfterPropChange();
        }

        private void BringToFront_Click(object sender, RoutedEventArgs e)
        {
            if (_doc.Current == null) return;
            int max = _doc.Current.Annotations.Count == 0 ? 0 : _doc.Current.Annotations.Max(a => a.ZIndex);
            foreach (var a in Sel) a.ZIndex = ++max;
            AfterPropChange();
        }

        private void SendToBack_Click(object sender, RoutedEventArgs e)
        {
            if (_doc.Current == null) return;
            int min = _doc.Current.Annotations.Count == 0 ? 0 : _doc.Current.Annotations.Min(a => a.ZIndex);
            foreach (var a in Sel) a.ZIndex = --min;
            AfterPropChange();
        }

        // ── Wyszukiwanie ─────────────────────────────────────────────────

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            var page = _doc.Current;
            if (page == null) return;
            if (page.IsImageSource) { StatusText.Text = "Ta strona nie zawiera warstwy tekstowej"; return; }
            var res = EditorDialogs.Search(this);
            if (res == null) return;

            var hits = PdfTextSearch.Find(page.Source.SourceBytes, page.Source.OriginalPageNumber, res.Value.query, res.Value.caseSensitive);
            if (hits.Count == 0) { StatusText.Text = "Nie znaleziono: " + res.Value.query; return; }

            using (_doc.History.BeginBatch("Podświetl wyniki"))
            {
                foreach (var h in hits)
                {
                    var hl = new HighlightAnnotation { Color = Colors.Yellow, Kind = HighlightKind.Highlight, BoundsPoints = h.BoundsPoints, Name = "Wynik: " + res.Value.query };
                    _doc.History.Push(new AddAnnotationCommand(page, hl));
                }
            }
            Surface.Refresh();
            StatusText.Text = $"Znaleziono {hits.Count} wystąpień";
        }

        // ── Operacje wsadowe / metadane / zabezpieczenia ─────────────────

        private void Batch_Click(object sender, RoutedEventArgs e)
        {
            var s = EditorDialogs.Batch(this);
            if (s == null) { StatusText.Text = "Anulowano operacje wsadowe"; return; }
            _doc.Batch = s;
            _doc.MarkDirty();
            StatusText.Text = "Operacje wsadowe zostaną zastosowane przy zapisie/eksporcie";
        }

        private void Metadata_Click(object sender, RoutedEventArgs e)
        {
            var m = EditorDialogs.Metadata(this, _docMetadata);
            if (m == null) return;
            _docMetadata.Title = m.Title; _docMetadata.Author = m.Author;
            _docMetadata.Subject = m.Subject; _docMetadata.Keywords = m.Keywords;
            StatusText.Text = "Metadane zostaną zapisane przy eksporcie";
        }

        private void Security_Click(object sender, RoutedEventArgs e)
        {
            var s = EditorDialogs.Security(this, _security);
            if (s == null) return;
            _security.Enabled = s.Enabled; _security.UserPassword = s.UserPassword; _security.OwnerPassword = s.OwnerPassword;
            _security.AllowPrint = s.AllowPrint; _security.AllowCopy = s.AllowCopy; _security.AllowModify = s.AllowModify;
            StatusText.Text = s.Enabled ? "Szyfrowanie zostanie zastosowane przy eksporcie" : "Szyfrowanie wyłączone";
        }

        // ── Eksport PNG ──────────────────────────────────────────────────

        private async void ExportPng_Click(object sender, RoutedEventArgs e)
        {
            var live = _doc.Pages.Where(p => !p.IsDeleted).ToList();
            if (live.Count == 0) return;
            var dpi = EditorDialogs.PngDpi(this);
            if (dpi == null) return;
            var folder = new OpenFolderDialog { Title = "Wybierz folder docelowy" };
            if (folder.ShowDialog() != true) return;
            string dir = folder.FolderName;

            StatusText.Text = "Eksportowanie PNG...";
            try
            {
                int total = live.Count;
                int count = await System.Threading.Tasks.Task.Run(() =>
                {
                    int c = 0;
                    for (int i = 0; i < live.Count; i++)
                    {
                        var bytes = PdfDocumentWriter.RenderPage(live[i], i + 1, total, _doc.Batch);
                        c += PdfPostProcess.ExportPagesToPng(bytes, dir, "strona", dpi.Value, i + 1);
                    }
                    return c;
                });
                StatusText.Text = $"Zapisano {count} plików PNG do: {dir}";
            }
            catch (Exception ex) { StatusText.Text = "Błąd eksportu PNG: " + ex.Message; }
        }

        // ── Zapis edycji ─────────────────────────────────────────────────

        /// <summary>
        /// Nanosi niezatwierdzone zmiany z edytora przed eksportem — bez tego eksport po cichu
        /// pominąłby wszystko, czego użytkownik nie zapisał przyciskiem „Zapisz”.
        /// </summary>
        private void EnsureEditorApplied()
        {
            if (!_doc.IsDirty || _doc.Pages.Count == 0) return;
            try
            {
                _doc.ApplyTo(_editedPages, _doc.Batch);
                SyncOrganizeFromEditor();
                _doc.MarkSaved();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Nie udało się nanieść zmian z edytora: " + ex.Message;
            }
        }

        private void SaveEditor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _doc.ApplyTo(_editedPages, _doc.Batch);
                SyncOrganizeFromEditor();
                _doc.MarkSaved();
                UpdateNavBadges();
                StatusText.Text = "Zapisano zmiany w edytorze";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Błąd zapisu edytora: " + ex.Message;
            }
        }

        private void SyncOrganizeFromEditor()
        {
            var live = _doc.Pages.Where(p => !p.IsDeleted).Select(p => p.Source).ToList();
            bool changed = live.Count != _organizePages.Count || !live.SequenceEqual(_organizePages);
            if (!changed) return;
            _organizePages.Clear();
            foreach (var it in live) _organizePages.Add(it);
            UpdateOrganizeOrder();
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
            EnsureEditorApplied();

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

                if (_docMetadata.Any || _security.Enabled)
                    await Task.Run(() =>
                    {
                        var bytes = File.ReadAllBytes(outputPath);
                        bytes = PdfPostProcess.ApplyMetadataAndSecurity(bytes, _docMetadata, _security);
                        File.WriteAllBytes(outputPath, bytes);
                    });

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
            EnsureEditorApplied();

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
