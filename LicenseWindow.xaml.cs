using System;
using System.Windows;
using System.Windows.Input;

namespace Segmento
{
    public partial class LicenseWindow : Window
    {
        private bool _isClosing;

        public LicenseWindow()
        {
            InitializeComponent();
            KeyDown += LicenseWindow_KeyDown;
        }

        private void LicenseWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) SafeClose();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => SafeClose();

        private void Window_Deactivated(object sender, EventArgs e) => SafeClose();

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
    }
}
