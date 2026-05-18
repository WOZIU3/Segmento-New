using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Segmento
{
    public partial class App : Application
    {
        public App()
        {
            // Handler ustawiony w konstruktorze - łapie błędy najwcześniej jak się da
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                ShowError("Błąd podczas uruchamiania", ex);
                Shutdown(1);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ShowError("Błąd aplikacji", e.Exception);
            e.Handled = true;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                ShowError("Krytyczny błąd", ex);
        }

        private void ShowError(string title, Exception ex)
        {
            string message = BuildMessage(ex);

            // Zapis do logu - zawsze, nawet jeśli MessageBox zawiedzie
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Segmento");
                Directory.CreateDirectory(dir);
                string logPath = Path.Combine(dir, "error.log");
                File.AppendAllText(logPath,
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\n{message}\n{new string('-', 60)}\n");
            }
            catch { }

            try
            {
                MessageBox.Show(message, $"Segmento - {title}",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        private static string BuildMessage(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            var current = ex;
            int level = 0;
            while (current != null)
            {
                string indent = level == 0 ? "" : new string(' ', level * 2) + "-> ";
                sb.AppendLine($"{indent}{current.GetType().Name}: {current.Message}");
                current = current.InnerException;
                level++;
            }
            sb.AppendLine();
            sb.AppendLine("Stack trace:");
            sb.AppendLine(ex.StackTrace ?? "(brak)");
            return sb.ToString();
        }
    }
}
