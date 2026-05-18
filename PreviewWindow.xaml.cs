using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace Segmento
{
    public partial class PreviewWindow : Window
    {
        private readonly byte[] _pdfBytes;
        private readonly int _pageNumber;
        private bool _isClosing;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public PreviewWindow(byte[] pdfBytes, int pageNumber)
        {
            InitializeComponent();
            _pdfBytes = pdfBytes;
            _pageNumber = pageNumber;
            TitleText.Text = $"Strona {pageNumber}";
            Loaded += PreviewWindow_Loaded;
            KeyDown += PreviewWindow_KeyDown;
            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                int backdrop = 2; // Mica
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
            catch { }
        }

        private async void PreviewWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var bitmap = await Task.Run(() => RenderHighQualityPreview(_pdfBytes, _pageNumber - 1));
                if (_isClosing) return;
                if (bitmap != null)
                {
                    PreviewImage.Source = bitmap;
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    ImageContainer.Visibility = Visibility.Visible;
                }
                else { LoadingPanel.Visibility = Visibility.Collapsed; SafeClose(); }
            }
            catch { LoadingPanel.Visibility = Visibility.Collapsed; SafeClose(); }
        }

        private void PreviewWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) SafeClose();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => SafeClose();

        private void Window_Deactivated(object sender, EventArgs e) => SafeClose();

        /// <summary>
        /// Zamyka okno tylko raz - chroni przed InvalidOperationException
        /// gdy Deactivated odpali sie w trakcie zamykania.
        /// </summary>
        private void SafeClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            base.OnClosing(e);
        }

        private static BitmapImage RenderHighQualityPreview(byte[] pdfBytes, int pageIndex)
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            var renderOptions = new PDFtoImage.RenderOptions
            {
                Dpi = 150,
                Width = 1400,
                WithAspectRatio = true
            };
            using var skBitmap = Conversion.ToImage(pdfStream, page: pageIndex, options: renderOptions);
            using var skImage = SKImage.FromBitmap(skBitmap);
            using var skData = skImage.Encode(SKEncodedImageFormat.Png, 95);

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
    }
}
