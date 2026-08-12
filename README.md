# Segmento

Aplikacja Windows do pracy z wieloma plikami PDF: import, wybór stron, organizacja kolejności, edycja i eksport.

## Design

- Monochromatyczny dark mode (czarny/biały)
- Windows 11 Mica backdrop + natywne zaokrąglone rogi (DWM `DWMWA_WINDOW_CORNER_PREFERENCE`)
- Zwijany sidebar (240 ↔ 64 px, animowany, start w stanie zwiniętym)
- Custom titlebar (`WindowChrome`, `CornerRadius="8"`)
- Karty stron 228 px / miniatury 202×252
- Segoe Fluent Icons + Segoe UI Variable

## Funkcje

**Import i wybór**
- Drag & drop wielu plików PDF oraz obrazów
- Miniatury renderowane adaptacyjnie (560 / 400 / 300 px zależnie od liczby stron)
- Zaznaczanie stron, w tym prostokątem (rubber band)
- Podgląd HD (prawy klik)

**Organizacja**
- Zmiana kolejności przeciąganiem w stylu iOS: karta unosi się nad interfejsem, siatka rozsuwa się w czasie rzeczywistym, auto-scroll przy krawędziach, anulowanie klawiszem ESC

**Edytor PDF (v3, model obiektowy)**
- Praca na wszystkich stronach wybranych do eksportu
- Adnotacje jako obiekty: tekst, obraz, rysunek odręczny, zakreślacz, kształty (prostokąt, elipsa, linia, strzałka, łamana), podświetlenia, stemple, redakcja
- Współrzędne w punktach PDF (1/72 cala) — niezależne od zoomu i DPI
- Zapis wektorowy przez iText7 **bez rasteryzacji** — oryginalna warstwa tekstowa zostaje nietknięta (tekst nadal daje się wyszukać i skopiować)
- Undo/redo z grupowaniem operacji
- Zaznaczanie wielokrotne, uchwyty skalowania i obrotu, snap do krawędzi
- Operacje na stronach: obrót, usuwanie, duplikowanie, wstawianie, kadrowanie
- Operacje wsadowe na zakres stron (`1-3,7,12-`): znak wodny, numeracja, nagłówek/stopka
- Wyszukiwanie i ekstrakcja tekstu
- Redakcja przez `pdfSweep` — trwałe usunięcie treści, nie zamalowanie

**Eksport**
- Scalanie stron z wielu źródeł do jednego PDF lub osobnych plików

## Technologia

- .NET 8 + WPF (`net8.0-windows`), C# code-behind
- PDFsharp 6.1.1 · iText7 8.0.4 · itext7.pdfsweep 5.0.4 · PDFtoImage 4.1.0
- CommunityToolkit.Mvvm 8.2.2

## Architektura

```
/
├── Controls/
│   ├── AnimatedWrapPanel.cs      (siatka z animowanym układem, sloty)
│   ├── ReorderDragController.cs  (drag & drop kart w stylu iOS)
│   └── EditorSurface.cs          (powierzchnia edycji: hit-test, uchwyty, snap)
├── Editor/
│   ├── Annotations/              (model adnotacji: tekst, obraz, ink, kształty,
│   │                              podświetlenia, stemple, redakcja)
│   ├── EditorDocument.cs         (dokument: strony, historia, stan dirty)
│   ├── EditorPage.cs             (strona: wymiary w punktach PDF, adnotacje)
│   ├── EditorSelection.cs        (zaznaczenie wielokrotne)
│   ├── EditorCommands.cs         (undo/redo)
│   ├── PageCommands.cs           (komendy operacji na stronach)
│   ├── EditorRenderer.cs         (render podkładów, cache LRU)
│   ├── EditorDialogs.cs          (okna dialogowe edytora)
│   ├── EditorBatchSettings.cs    (znak wodny, numeracja, nagłówek/stopka)
│   ├── PageRange.cs              (parser zakresów stron)
│   ├── PdfDocumentWriter.cs      (model → PDF, zapis wektorowy)
│   ├── PdfWriterContext.cs       (kontekst zapisu, konwersja układu współrzędnych)
│   ├── PdfPostProcess.cs         (operacje wsadowe, metadane, zabezpieczenia)
│   └── PdfTextSearch.cs          (wyszukiwanie i ekstrakcja tekstu)
├── Themes/
│   ├── Dark.xaml                 (paleta kolorów)
│   └── Styles.xaml               (style komponentów)
├── App.xaml                      (zasoby globalne)
├── MainWindow.xaml               (widoki: Import, Wybór stron, Organizuj, Eksport, Edytor)
├── PreviewWindow.xaml            (podgląd HD strony)
├── LicenseWindow.xaml            (licencja i prawa autorskie)
├── Models.cs                     (PdfSource, PageItem)
├── Segmento.csproj
├── Logo.png
└── app.ico
```

## Konwencje kodu

- Kolory wyłącznie przez `DynamicResource` — bez wartości wpisanych na sztywno
- `DropShadowEffect` nigdy w poddrzewie zawierającym `Image` (rasteryzacja efektu rozmywa obraz) — cień na osobnym `Border` tła
- Animacje 180–220 ms, `CubicEase` / `EaseOut`
- Pola prywatne `_camelCase`, handlery `Element_Event`
- Komunikaty dla użytkownika po polsku, w `StatusText.Text`

## Build

GitHub Actions → artefakt `Segmento-Windows-x64`.
