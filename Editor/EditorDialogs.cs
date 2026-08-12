using System;
using System.Windows;
using System.Windows.Controls;

namespace Segmento.Editor
{
    /// <summary>Proste, samodzielne okna dialogowe budowane w kodzie (bez plików XAML).</summary>
    public static class EditorDialogs
    {
        private static Window Shell(string title, double width, out StackPanel body)
        {
            var win = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };
            body = new StackPanel { Margin = new Thickness(16) };
            win.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return win;
        }

        private static TextBox Field(StackPanel body, string label, string value = "")
        {
            body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) });
            var tb = new TextBox { Text = value };
            body.Children.Add(tb);
            return tb;
        }

        private static (Button ok, Button cancel) Buttons(StackPanel body)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Anuluj", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
            row.Children.Add(cancel); row.Children.Add(ok);
            body.Children.Add(row);
            return (ok, cancel);
        }

        public static DocMetadata? Metadata(Window owner, DocMetadata current)
        {
            var win = Shell("Metadane dokumentu", 420, out var body);
            win.Owner = owner;
            var t = Field(body, "Tytuł", current.Title ?? "");
            var a = Field(body, "Autor", current.Author ?? "");
            var s = Field(body, "Temat", current.Subject ?? "");
            var k = Field(body, "Słowa kluczowe", current.Keywords ?? "");
            var (ok, _) = Buttons(body);
            bool done = false;
            ok.Click += (_, _) => { done = true; win.DialogResult = true; };
            if (win.ShowDialog() != true || !done) return null;
            // Puste pole = brak metadanej (nie nadpisujemy jej pustym łańcuchem).
            static string? N(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            return new DocMetadata
            {
                Title = N(t.Text), Author = N(a.Text), Subject = N(s.Text), Keywords = N(k.Text)
            };
        }

        public static SecurityOptions? Security(Window owner, SecurityOptions current)
        {
            var win = Shell("Zabezpieczenia", 420, out var body);
            win.Owner = owner;
            var enabled = new CheckBox { Content = "Zaszyfruj dokument (AES-256)", IsChecked = current.Enabled, Margin = new Thickness(0, 0, 0, 4) };
            body.Children.Add(enabled);
            var user = Field(body, "Hasło otwarcia (użytkownik)", current.UserPassword);
            var owner2 = Field(body, "Hasło właściciela (opcjonalne)", current.OwnerPassword);
            var print = new CheckBox { Content = "Zezwól na drukowanie", IsChecked = current.AllowPrint, Margin = new Thickness(0, 8, 0, 0) };
            var copy = new CheckBox { Content = "Zezwól na kopiowanie", IsChecked = current.AllowCopy, Margin = new Thickness(0, 4, 0, 0) };
            var modify = new CheckBox { Content = "Zezwól na modyfikację", IsChecked = current.AllowModify, Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(print); body.Children.Add(copy); body.Children.Add(modify);
            var (ok, _) = Buttons(body);
            ok.Click += (_, _) => win.DialogResult = true;
            if (win.ShowDialog() != true) return null;
            return new SecurityOptions
            {
                Enabled = enabled.IsChecked == true,
                UserPassword = user.Text, OwnerPassword = owner2.Text,
                AllowPrint = print.IsChecked == true, AllowCopy = copy.IsChecked == true, AllowModify = modify.IsChecked == true
            };
        }

        public static EditorBatchSettings? Batch(Window owner)
        {
            var win = Shell("Operacje wsadowe", 460, out var body);
            win.Owner = owner;

            var wmOn = new CheckBox { Content = "Znak wodny (tekst)", Margin = new Thickness(0, 0, 0, 2) };
            body.Children.Add(wmOn);
            var wmText = Field(body, "Tekst znaku wodnego", "POUFNE");

            var pnOn = new CheckBox { Content = "Numeracja stron", Margin = new Thickness(0, 12, 0, 2) };
            body.Children.Add(pnOn);
            var pnFmt = Field(body, "Format ({n}, {total})", "{n} / {total}");
            var pnSkip = new CheckBox { Content = "Pomiń pierwszą stronę", Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(pnSkip);

            var hdr = Field(body, "Nagłówek (opcjonalny)", "");
            var ftr = Field(body, "Stopka (opcjonalna)", "");

            var rng = Field(body, "Zakres stron (np. 1-3,5) — puste = wszystkie", "");

            var (ok, _) = Buttons(body);
            ok.Click += (_, _) => win.DialogResult = true;
            if (win.ShowDialog() != true) return null;

            var settings = new EditorBatchSettings();
            if (wmOn.IsChecked == true && !string.IsNullOrWhiteSpace(wmText.Text))
                settings.Watermark = new WatermarkOptions { Text = wmText.Text };
            if (pnOn.IsChecked == true)
                settings.PageNumbers = new PageNumberOptions { Format = pnFmt.Text, SkipFirst = pnSkip.IsChecked == true };
            if (!string.IsNullOrWhiteSpace(hdr.Text) || !string.IsNullOrWhiteSpace(ftr.Text))
                settings.HeaderFooter = new HeaderFooterOptions { HeaderText = hdr.Text, FooterText = ftr.Text };

            // Zakres stron zastosuj do wszystkich aktywnych operacji
            var pages = PageRange.Parse(rng.Text, 100000);
            if (!string.IsNullOrWhiteSpace(rng.Text))
            {
                var set = new System.Collections.Generic.HashSet<int>(pages);
                if (settings.Watermark != null) settings.Watermark.Pages = set;
                if (settings.PageNumbers != null) settings.PageNumbers.Pages = set;
                if (settings.HeaderFooter != null) settings.HeaderFooter.Pages = set;
            }
            return settings.Any ? settings : null;
        }

        public static (string query, bool caseSensitive)? Search(Window owner)
        {
            var win = Shell("Znajdź na stronie", 380, out var body);
            win.Owner = owner;
            var q = Field(body, "Szukana fraza", "");
            var cs = new CheckBox { Content = "Rozróżniaj wielkość liter", Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(cs);
            var (ok, _) = Buttons(body);
            ok.Click += (_, _) => win.DialogResult = true;
            q.Focus();
            if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(q.Text)) return null;
            return (q.Text, cs.IsChecked == true);
        }

        public static int? PngDpi(Window owner)
        {
            var win = Shell("Eksport PNG", 340, out var body);
            win.Owner = owner;
            body.Children.Add(new TextBlock { Text = "Rozdzielczość (DPI)" });
            var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var d in new[] { "96", "150", "300", "600" }) combo.Items.Add(d);
            combo.SelectedIndex = 2;
            body.Children.Add(combo);
            var (ok, _) = Buttons(body);
            ok.Click += (_, _) => win.DialogResult = true;
            if (win.ShowDialog() != true) return null;
            return int.TryParse(combo.SelectedItem?.ToString(), out int dpi) ? dpi : 300;
        }
    }
}
