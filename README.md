# Segmento

Premium aplikacja Windows 11 do dzielenia plików PDF.

## Design

- Monochromatyczny dark mode (czarny/biały)
- Windows 11 Mica/Acrylic backdrop
- Sidebar navigation
- Custom titlebar
- Premium card-based UI
- Segoe Fluent Icons + Segoe UI Variable

## Funkcje

- Drag & drop PDF
- Miniatury w wysokiej jakości
- Wybór stron + organizacja kolejności
- Podgląd HD (prawy klik)
- Eksport do nowego PDF

## Technologia

- .NET 8 + WPF
- PDFsharp + iText7 + PDFtoImage
- CommunityToolkit.Mvvm

## Architektura

```
Segmento/
├── Themes/
│   ├── Dark.xaml         (paleta kolorów)
│   └── Styles.xaml       (style komponentów)
├── App.xaml              (zasoby globalne)
├── MainWindow.xaml       (główny widok z sidebar)
├── PreviewWindow.xaml    (HD podgląd strony)
├── Segmento.csproj
├── Logo.png
└── app.ico
```

## Pobierz

Najnowszy build → zakładka **Releases**
