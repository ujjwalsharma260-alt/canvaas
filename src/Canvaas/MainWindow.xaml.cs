using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Canvaas;

public partial class MainWindow : Window
{
    private const int CurrentFormatVersion = 5;
    private const string FileExtension = ".canvaas";
    private const string FileFilter = "Canvaas note (*.canvaas)|*.canvaas";
    private const string ManifestEntryName = "manifest.json";
    private const string LegacyInkEntryName = "ink.isf";
    private const string PageEntryFormat = "page_{0:D3}.isf";

    private const double CanvasWorldSize = 40000;
    private const double DefaultViewX = -20000;
    private const double DefaultViewY = -20000;
    private const double MinZoom = 0.01;
    private const double MaxZoom = 40.0;
    private const double ExportMaxDim = 3000;
    private const double SwipeMinDist = 250;
    private const double SwipeMaxDurationMs = 700;
    private const double ZoomStepFactor = 1.25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class PageManifest
    {
        public string? BackgroundColor { get; set; }
        public string? Template { get; set; }
        public double? Spacing { get; set; }
        public string? Paper { get; set; }
    }

    private sealed class Manifest
    {
        public int FormatVersion { get; set; }
        public string App { get; set; } = "Canvaas";
        public string AppVersion { get; set; } = "";
        public string SavedAtUtc { get; set; } = "";
        public int CurrentPageIndex { get; set; }
        public List<PageManifest>? Pages { get; set; }
    }

    private sealed class AppSettings
    {
        public double PointerSize { get; set; } = 12.0;
        public int CursorStyleIndex { get; set; } = 2;
    }

    private sealed class StrokeChange
    {
        public StrokeCollection Added { get; }
        public StrokeCollection Removed { get; }
        public StrokeChange(StrokeCollection added, StrokeCollection removed)
        {
            Added = added; Removed = removed;
        }
    }

    private sealed class ElementMoveChange
    {
        public UIElement Element { get; set; } = null!;
        public double OldLeft { get; set; }
        public double OldTop { get; set; }
        public double NewLeft { get; set; }
        public double NewTop { get; set; }
    }

    private enum PageTemplate { Blank, Ruled, Grid, Dot }
    private enum PenCursorStyle { Arrow, Cross, HollowRingDot, Hidden }
    private enum ToolMode { Pen, Highlighter, Eraser, Lasso, Hand }
    private enum PaperStyle { White, Crumpled }

    private sealed class NotebookPage
    {
        public StrokeCollection Strokes { get; set; } = new StrokeCollection();
        public Color BackgroundColor { get; set; } = Colors.White;
        public PageTemplate Template { get; set; } = PageTemplate.Blank;
        public double Spacing { get; set; } = 40;
        public PaperStyle Paper { get; set; } = PaperStyle.White;

        public double ViewZoom { get; set; } = 1.0;
        public double ViewPanX { get; set; } = DefaultViewX;
        public double ViewPanY { get; set; } = DefaultViewY;
        public bool ViewInitialized { get; set; } = false;
    }

    public sealed class PopupColorItem
    {
        public Brush Brush { get; set; } = Brushes.Black;
        public string Hex { get; set; } = "#000000";
        public string Name { get; set; } = "";
    }

    private readonly List<NotebookPage> _pages = new();
    private int _currentPageIndex;

    private readonly Stack<object> _undo = new();
    private readonly Stack<object> _redo = new();
    private bool _applyingHistory;

    private string? _currentPath;
    private bool _dirty;

    private bool _suppressCounterChange;
    private bool _fullscreenMode;
    private bool _uiReady;

    private ToolMode _tool = ToolMode.Pen;
    private Color _penColor = Colors.Black;
    private double _penSize = 2.5;
    private bool _pressureEnabled = true;
    private PenCursorStyle _cursorStyle = PenCursorStyle.HollowRingDot;
    private double _pointerSize = 12.0;

    private Cursor? _hollowRingCursor;

    private bool _panning;
    private Point _panStart;
    private double _panStartPanX, _panStartPanY;
    private DateTime _panStartTime;

    private double _viewZoom = 1.0;
    private double _viewPanX = DefaultViewX;
    private double _viewPanY = DefaultViewY;

    private bool _draggingFloatingZoom;
    private Point _floatingZoomDragStart;
    private double _floatingZoomStartX, _floatingZoomStartY;

    private int _insertCounter = 0;

    private readonly List<UIElement> _selectedElements = new();
    private readonly List<Point> _lassoPointsScreen = new();
    private readonly List<Rectangle> _elementSelectionVisuals = new();
    private Path? _lassoVisual;
    private bool _lassoActive;
    private bool _draggingSelection;
    private Point _dragStartWorld;
    private readonly Dictionary<UIElement, (double L, double T)> _elementStartPositions = new();
    private StrokeCollection? _dragOriginalStrokes;
    private StrokeCollection? _dragPreviewStrokes;

    private NotebookPage CurrentPage => _pages[_currentPageIndex];

    private static string SettingsFilePath
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Canvaas", "settings.json");

    public MainWindow()
    {
        InitializeComponent();

        if (InkArea is not null)
        {
            InkArea.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = _penColor,
                Width = _penSize,
                Height = _penSize,
                FitToCurve = false,
                IgnorePressure = !_pressureEnabled
            };
        }

        _uiReady = true;

        LoadSettings();
        RebuildHollowRingCursor();

        if (PointerSizeSlider is not null)
            PointerSizeSlider.Value = _pointerSize;
        if (PointerSizeText is not null)
            PointerSizeText.Text = ((int)Math.Round(_pointerSize)).ToString();

        if (CursorCombo is not null)
            CursorCombo.SelectedIndex = _cursorStyle switch
            {
                PenCursorStyle.Arrow => 0,
                PenCursorStyle.Cross => 1,
                PenCursorStyle.HollowRingDot => 2,
                PenCursorStyle.Hidden => 3,
                _ => 2
            };

        Loaded += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WindowState = WindowState.Maximized;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ResetViewToOrigin();
                }), DispatcherPriority.Loaded);
            }), DispatcherPriority.ApplicationIdle);
        };

        StateChanged += MainWindow_StateChanged;

        _pages.Add(new NotebookPage());
        _currentPageIndex = 0;

        PopulateToolColorGrid();
        LoadCurrentPageIntoCanvas();

        PenButton.IsChecked = true;
        InkArea.EditingMode = InkCanvasEditingMode.Ink;
        ColorWhite.IsChecked = true;
        TemplateBlank.IsChecked = true;
        PaperWhite.IsChecked = true;

        ApplyPenAttributes();
        UpdateCursor();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Canvaas v{version.Major}.{version.Minor}.{version.Build}";

        UpdateTitle();
    }

    // =====================================================================
    // Settings
    // =====================================================================

    private void LoadSettings()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (s is null) return;
            _pointerSize = Math.Clamp(s.PointerSize, 6.0, 32.0);
            _cursorStyle = s.CursorStyleIndex switch
            {
                0 => PenCursorStyle.Arrow,
                1 => PenCursorStyle.Cross,
                2 => PenCursorStyle.HollowRingDot,
                3 => PenCursorStyle.Hidden,
                _ => PenCursorStyle.HollowRingDot
            };
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var path = SettingsFilePath;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var s = new AppSettings
            {
                PointerSize = _pointerSize,
                CursorStyleIndex = (int)_cursorStyle
            };
            File.WriteAllText(path, JsonSerializer.Serialize(s, JsonOptions));
        }
        catch { }
    }

    // =====================================================================
    // Cursor: hollow black ring + tiny black dot
    // =====================================================================

    private void RebuildHollowRingCursor()
    {
        try
        {
            double ringRadius = _pointerSize / 2.0;
            double dotRadius = Math.Max(1.0, ringRadius * 0.17);
            int size = (int)Math.Ceiling(_pointerSize + 8);
            if (size < 20) size = 20;
            if (size > 60) size = 60;
            double center = size / 2.0;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var ringPen = new Pen(Brushes.Black, 1.3);
                dc.DrawEllipse(null, ringPen, new Point(center, center), ringRadius, ringRadius);
                dc.DrawEllipse(Brushes.Black, null, new Point(center, center), dotRadius, dotRadius);
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            _hollowRingCursor = CreateCursorFromBitmap(rtb, size / 2, size / 2);
        }
        catch { _hollowRingCursor = Cursors.Cross; }
    }

    private static Cursor CreateCursorFromBitmap(BitmapSource bmp, int hotX, int hotY)
    {
        int width = bmp.PixelWidth;
        int height = bmp.PixelHeight;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((short)0);
        bw.Write((short)2);
        bw.Write((short)1);

        bw.Write((byte)width);
        bw.Write((byte)height);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((short)hotX);
        bw.Write((short)hotY);
        bw.Write(width * height * 4 + 40);
        bw.Write(22);

        bw.Write(40);
        bw.Write(width);
        bw.Write(height * 2);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(width * height * 4);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        int stride = width * 4;
        var pixels = new byte[stride * height];
        bmp.CopyPixels(pixels, stride, 0);
        for (int y = height - 1; y >= 0; y--)
            bw.Write(pixels, y * stride, stride);

        ms.Position = 0;
        return new Cursor(ms);
    }

    private void PointerSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _pointerSize = e.NewValue;
        if (PointerSizeText is not null)
            PointerSizeText.Text = ((int)Math.Round(_pointerSize)).ToString();
        RebuildHollowRingCursor();
        UpdateCursor();
        SaveSettings();
    }

    // =====================================================================
    // Stylus release
    // =====================================================================

    private void InkArea_StylusOutOfRange(object sender, StylusEventArgs e)
    {
        ReleaseAllCaptures();
    }

    private void ReleaseAllCaptures()
    {
        if (_panning) EndPan();

        try
        {
            if (Mouse.Captured is not null && Mouse.Captured != this)
                Mouse.Capture(null);
        }
        catch { }

        try { Stylus.Capture(null); } catch { }
    }

    // =====================================================================
    // Colour palette data
    // =====================================================================

    private void PopulateToolColorGrid()
    {
        if (ToolColorGrid is null) return;

        var items = new List<PopupColorItem>
        {
            MakeColor("#111111", "Black"),
            MakeColor("#555555", "Dark grey"),
            MakeColor("#999999", "Grey"),
            MakeColor("#CCCCCC", "Light grey"),
            MakeColor("#FFFFFF", "White"),
            MakeColor("#FAF6E3", "Cream"),
            MakeColor("#C62828", "Red"),
            MakeColor("#EF6C00", "Orange"),
            MakeColor("#FBC02D", "Yellow"),
            MakeColor("#B8860B", "Gold"),
            MakeColor("#A1887F", "Tan"),
            MakeColor("#4E342E", "Brown"),
            MakeColor("#00ACC1", "Cyan"),
            MakeColor("#4FC3F7", "Sky"),
            MakeColor("#1565C0", "Blue"),
            MakeColor("#1A237E", "Navy"),
            MakeColor("#6A1B9A", "Purple"),
            MakeColor("#AD1457", "Magenta"),
            MakeColor("#AED581", "Light green"),
            MakeColor("#43A047", "Green"),
            MakeColor("#1B5E20", "Dark green"),
            MakeColor("#827717", "Olive"),
            MakeColor("#F06292", "Pink"),
            MakeColor("#7B1FA2", "Violet"),
        };

        ToolColorGrid.ItemsSource = items;
    }

    private static PopupColorItem MakeColor(string hex, string name)
    {
        var c = ParseHexColor(hex, Colors.Black);
        return new PopupColorItem
        {
            Brush = new SolidColorBrush(c),
            Hex = hex,
            Name = name
        };
    }

    // =====================================================================
    // Tool options popup
    // =====================================================================

    private void ShowToolPopup(UIElement target, string title)
    {
        if (ToolOptionsPopup is null) return;

        ToolOptionsPopup.IsOpen = false;
        if (ToolPopupTitle is not null) ToolPopupTitle.Text = title;
        ToolOptionsPopup.PlacementTarget = target;
        UpdateThicknessPreview();

        if (ToolSizeSlider is not null)
            ToolSizeSlider.Value = _penSize;
        if (ToolPressureToggle is not null)
            ToolPressureToggle.IsChecked = _pressureEnabled;

        ToolOptionsPopup.IsOpen = true;
    }

    private void UpdateThicknessPreview()
    {
        if (ToolThicknessPreview is null) return;

        double actual = _tool == ToolMode.Highlighter
            ? Math.Max(14, _penSize * 6)
            : _penSize;

        Color c = _tool == ToolMode.Highlighter
            ? Color.FromArgb(180, _penColor.R, _penColor.G, _penColor.B)
            : _penColor;

        ToolThicknessPreview.Stroke = new SolidColorBrush(c);
        ToolThicknessPreview.StrokeThickness = actual;
    }

    private void ToolColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            var colour = ParseHexColor(hex, Colors.Black);

            bool hasSelection = (_selectedElements.Count > 0)
                || (InkArea is not null && InkArea.GetSelectedStrokes().Count > 0);

            if (hasSelection)
                ApplyColourToSelection(colour);
            else
            {
                _penColor = colour;
                ApplyPenAttributes();
                UpdateThicknessPreview();
            }
        }
    }

    private void ToolSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _penSize = e.NewValue;
        ApplyPenAttributes();
        UpdateThicknessPreview();
    }

    private void ToolPressureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _pressureEnabled = ToolPressureToggle.IsChecked == true;
        ApplyPenAttributes();
    }

    private void ApplyColourToSelection(Color newColour)
    {
        if (InkArea is null) return;
        bool any = false;

        var selectedStrokes = InkArea.GetSelectedStrokes();
        if (selectedStrokes.Count > 0)
        {
            _applyingHistory = true;
            StrokeCollection added;
            StrokeCollection removed;
            try
            {
                removed = new StrokeCollection();
                added = new StrokeCollection();
                foreach (var s in selectedStrokes.ToList())
                {
                    var da = s.DrawingAttributes.Clone();
                    da.Color = newColour;
                    var ns = new Stroke(s.StylusPoints, da);
                    removed.Add(s);
                    added.Add(ns);
                }
                InkArea.Strokes.Remove(removed);
                InkArea.Strokes.Add(added);
            }
            finally { _applyingHistory = false; }

            InkArea.Select(added);
            _undo.Push(new StrokeChange(added, removed));
            _redo.Clear();
            any = true;
        }

        foreach (var el in _selectedElements)
        {
            if (el is TextBox tb)
            {
                tb.Foreground = new SolidColorBrush(newColour);
                any = true;
            }
        }

        if (any)
        {
            MarkDirty();
            StatusText.Text = "Recoloured selection.";
        }
        else
        {
            _penColor = newColour;
            ApplyPenAttributes();
            UpdateThicknessPreview();
            StatusText.Text = "Pen colour changed.";
        }
    }

    // =====================================================================
    // Cursor
    // =====================================================================

    private void UpdateCursor()
    {
        Cursor c;
        if (_tool == ToolMode.Hand)
            c = Cursors.SizeAll;
        else if (_tool == ToolMode.Lasso)
            c = Cursors.Cross;
        else
        {
            c = _cursorStyle switch
            {
                PenCursorStyle.Cross => Cursors.Cross,
                PenCursorStyle.HollowRingDot => _hollowRingCursor ?? Cursors.Cross,
                PenCursorStyle.Hidden => Cursors.None,
                _ => Cursors.Arrow
            };
        }

        if (CanvasHostBorder is not null) CanvasHostBorder.Cursor = c;
        if (InkArea is not null) InkArea.Cursor = c;
    }

    // =====================================================================
    // Pen attributes
    // =====================================================================

    private void ApplyPenAttributes()
    {
        if (!_uiReady || InkArea is null) return;
        if (_tool == ToolMode.Lasso) return;
        var da = InkArea.DefaultDrawingAttributes;
        if (da is null) return;

        if (_tool == ToolMode.Highlighter)
        {
            da.Color = Color.FromArgb(110, _penColor.R, _penColor.G, _penColor.B);
            double highlightSize = Math.Max(14, _penSize * 6);
            da.Width = highlightSize;
            da.Height = highlightSize;
            da.IsHighlighter = true;
            da.FitToCurve = false;
            da.IgnorePressure = true;
        }
        else
        {
            da.Color = _penColor;
            da.Width = _penSize;
            da.Height = _penSize;
            da.IsHighlighter = false;
            da.FitToCurve = false;
            da.IgnorePressure = !_pressureEnabled;
        }
    }

    // =====================================================================
    // Keyboard shortcuts
    // =====================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Escape && _fullscreenMode)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.D1: case Key.NumPad1:
                PenButton.IsChecked = true;
                PenButton_Click(PenButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.D2: case Key.NumPad2:
                HighlighterButton.IsChecked = true;
                HighlighterButton_Click(HighlighterButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.D3: case Key.NumPad3:
                EraserButton.IsChecked = true;
                EraserButton_Click(EraserButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.D4: case Key.NumPad4:
                LassoButton.IsChecked = true;
                LassoButton_Click(LassoButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.D5: case Key.NumPad5:
                HandButton.IsChecked = true;
                HandButton_Click(HandButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.Left:
                PrevPageButton_Click(PrevPageButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.Right:
                NextPageButton_Click(NextPageButton, new RoutedEventArgs());
                e.Handled = true;
                return;
        }
    }

    // =====================================================================
    // Fullscreen
    // =====================================================================

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreenMode = !_fullscreenMode;

        if (TitleBarBorder is not null)
            TitleBarBorder.Visibility = _fullscreenMode ? Visibility.Collapsed : Visibility.Visible;

        if (AppWindowChrome is not null)
        {
            AppWindowChrome.CaptionHeight = _fullscreenMode ? 0 : 36;
            AppWindowChrome.ResizeBorderThickness = _fullscreenMode ? new Thickness(0) : new Thickness(6);
        }

        ReleaseAllCaptures();

        if (StatusText is not null)
            StatusText.Text = _fullscreenMode
                ? "Fullscreen mode. Press ESC or F11 to exit."
                : "Exited fullscreen.";
    }

    // =====================================================================
    // View — pan and zoom
    // =====================================================================

    private void ApplyView()
    {
        if (ViewTransform is null) return;
        ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        if (ZoomText is not null && !ZoomText.IsFocused)
            ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";
        SaveCurrentViewToPage();
    }

    private void ZoomAt(Point viewPoint, double factor)
    {
        double newZoom = Math.Clamp(_viewZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _viewZoom) < 1e-9) return;

        double wx = (viewPoint.X - _viewPanX) / _viewZoom;
        double wy = (viewPoint.Y - _viewPanY) / _viewZoom;

        _viewPanX = viewPoint.X - wx * newZoom;
        _viewPanY = viewPoint.Y - wy * newZoom;
        _viewZoom = newZoom;

        ApplyView();
    }

    private void PanBy(double dxScreen, double dyScreen)
    {
        _viewPanX += dxScreen;
        _viewPanY += dyScreen;
        ApplyView();
    }

    private void ZoomAtViewCenter(double factor)
    {
        if (CanvasHostBorder is null) return;
        double vw = CanvasHostBorder.ActualWidth;
        double vh = CanvasHostBorder.ActualHeight;
        if (vw < 10 || vh < 10) return;
        ZoomAt(new Point(vw / 2.0, vh / 2.0), factor);
    }

    private void ResetViewToOrigin()
    {
        _viewZoom = 1.0;
        _viewPanX = DefaultViewX;
        _viewPanY = DefaultViewY;
        if (ViewTransform is not null)
            ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        if (ZoomText is not null && !ZoomText.IsFocused)
            ZoomText.Text = "100%";
    }

    private void SaveCurrentViewToPage()
    {
        if (CurrentPage is null) return;
        CurrentPage.ViewZoom = _viewZoom;
        CurrentPage.ViewPanX = _viewPanX;
        CurrentPage.ViewPanY = _viewPanY;
        CurrentPage.ViewInitialized = true;
    }

    private void RestoreViewFromPage()
    {
        if (CurrentPage is null) return;
        _viewZoom = CurrentPage.ViewZoom;
        _viewPanX = CurrentPage.ViewPanX;
        _viewPanY = CurrentPage.ViewPanY;
        if (ViewTransform is not null)
            ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        if (ZoomText is not null && !ZoomText.IsFocused)
            ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomAtViewCenter(ZoomStepFactor);
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomAtViewCenter(1.0 / ZoomStepFactor);
    private void ZoomResetButton_Click(object sender, RoutedEventArgs e) => ZoomAtViewCenter(1.0 / _viewZoom);

    private void ZoomText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyZoomFromText(); e.Handled = true; }
    }

    private void ZoomText_LostFocus(object sender, RoutedEventArgs e) => ApplyZoomFromText();

    private void ApplyZoomFromText()
    {
        if (ZoomText is null) return;
        string raw = ZoomText.Text.Trim().TrimEnd('%').Trim();
        if (double.TryParse(raw, out var pct))
        {
            double target = Math.Clamp(pct / 100.0, MinZoom, MaxZoom);
            double factor = target / _viewZoom;
            ZoomAtViewCenter(factor);
        }
        if (!ZoomText.IsFocused)
            ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";
    }

    private void CanvasHostBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CanvasHostBorder is null) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            var p = e.GetPosition(CanvasHostBorder);
            ZoomAt(p, factor);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            PanBy(-e.Delta, 0);
            e.Handled = true;
        }
        else
        {
            PanBy(0, -e.Delta);
            e.Handled = true;
        }
    }

    // =====================================================================
    // Floating zoom widget drag
    // =====================================================================

    private void FloatingZoomGrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        _draggingFloatingZoom = true;
        _floatingZoomDragStart = e.GetPosition(this);
        _floatingZoomStartX = FloatingZoomTransform.X;
        _floatingZoomStartY = FloatingZoomTransform.Y;
        el.CaptureMouse();
        el.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void FloatingZoomGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingFloatingZoom) return;
        var p = e.GetPosition(this);
        FloatingZoomTransform.X = _floatingZoomStartX + (p.X - _floatingZoomDragStart.X);
        FloatingZoomTransform.Y = _floatingZoomStartY + (p.Y - _floatingZoomDragStart.Y);
    }

    private void FloatingZoomGrip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingFloatingZoom) return;
        _draggingFloatingZoom = false;
        if (sender is FrameworkElement el)
        {
            el.ReleaseMouseCapture();
            el.Cursor = Cursors.SizeAll;
        }
        e.Handled = true;
    }

    // =====================================================================
    // Page management
    // =====================================================================

    private void LoadCurrentPageIntoCanvas()
    {
        if (InkArea is null) return;

        InkArea.Strokes.StrokesChanged -= Strokes_Changed;
        InkArea.Strokes = CurrentPage.Strokes;
        InkArea.Strokes.StrokesChanged += Strokes_Changed;

        _undo.Clear();
        _redo.Clear();
        CommandManager.InvalidateRequerySuggested();

        ClearElementSelection();

        SyncUIWithSettings();
        UpdatePageNavigationUI();

        if (CurrentPage.ViewInitialized)
            RestoreViewFromPage();
        else
            ResetViewToOrigin();
    }

    private void UpdatePageNavigationUI()
    {
        if (PageCounterBox is null) return;
        _suppressCounterChange = true;
        PageCounterBox.Text = $"{_currentPageIndex + 1} / {_pages.Count}";
        _suppressCounterChange = false;
        if (PrevPageButton is not null) PrevPageButton.IsEnabled = _currentPageIndex > 0;
        if (NextPageButton is not null) NextPageButton.IsEnabled = _currentPageIndex < _pages.Count - 1;
    }

    private void PageCounterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyPageCounterText(); e.Handled = true; }
    }

    private void PageCounterBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_suppressCounterChange) ApplyPageCounterText();
    }

    private void ApplyPageCounterText()
    {
        if (PageCounterBox is null) return;
        string raw = PageCounterBox.Text;
        int slash = raw.IndexOf('/');
        if (slash >= 0) raw = raw.Substring(0, slash);
        raw = raw.Trim();

        if (int.TryParse(raw, out var n))
        {
            int idx = n - 1;
            if (idx < 0) idx = 0;
            if (idx >= _pages.Count) idx = _pages.Count - 1;
            if (idx != _currentPageIndex)
            {
                _currentPageIndex = idx;
                LoadCurrentPageIntoCanvas();
                StatusText.Text = $"Jumped to page {_currentPageIndex + 1} of {_pages.Count}.";
            }
        }
        UpdatePageNavigationUI();
    }

    private void PrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPageIndex > 0)
        {
            _currentPageIndex--;
            LoadCurrentPageIntoCanvas();
            StatusText.Text = $"Page {_currentPageIndex + 1} of {_pages.Count}.";
        }
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPageIndex < _pages.Count - 1)
        {
            _currentPageIndex++;
            LoadCurrentPageIntoCanvas();
            StatusText.Text = $"Page {_currentPageIndex + 1} of {_pages.Count}.";
        }
    }

    private void AddPageButton_Click(object sender, RoutedEventArgs e)
    {
        var newPage = new NotebookPage
        {
            BackgroundColor = CurrentPage.BackgroundColor,
            Template = CurrentPage.Template,
            Spacing = CurrentPage.Spacing,
            Paper = CurrentPage.Paper
        };

        _pages.Insert(_currentPageIndex + 1, newPage);
        _currentPageIndex++;
        LoadCurrentPageIntoCanvas();
        MarkDirty();
        StatusText.Text = $"Added page {_currentPageIndex + 1} of {_pages.Count}.";
    }

    private void DeletePageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count <= 1)
        {
            MessageBox.Show(this,
                "You cannot delete the only page in a note.\n\nAdd another page first, then delete this one.",
                "Cannot delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete page {_currentPageIndex + 1} of {_pages.Count}?\n\nEverything on this page will be removed.",
            "Delete page", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        _pages.RemoveAt(_currentPageIndex);
        if (_currentPageIndex >= _pages.Count)
            _currentPageIndex = _pages.Count - 1;

        LoadCurrentPageIntoCanvas();
        MarkDirty();
        StatusText.Text = $"Deleted. Now on page {_currentPageIndex + 1} of {_pages.Count}.";
    }

    // =====================================================================
    // Export PNG
    // =====================================================================

    private void ExportCurrentPagePng_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = "Page " + (_currentPageIndex + 1),
            Title = "Export current page as PNG"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            ExportPageToPng(CurrentPage, dlg.FileName);
            StatusText.Text = "Exported: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not export image.\n\n" + ex.Message,
                "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportAllPagesPng_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = "Page.png",
            Title = "Choose a base name — each page becomes Page_01.png, Page_02.png, ..."
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string dir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            for (int i = 0; i < _pages.Count; i++)
            {
                string name = System.IO.Path.Combine(dir, $"{baseName}_{i + 1:D2}.png");
                ExportPageToPng(_pages[i], name);
            }
            StatusText.Text = $"Exported {_pages.Count} page(s) to {dir}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not export images.\n\n" + ex.Message,
                "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportPageToPng(NotebookPage page, string path)
    {
        Rect world = page.Strokes.Count > 0
            ? page.Strokes.GetBounds()
            : new Rect(0, 0, 800, 1120);
        world.Inflate(40, 40);

        double scale = 1.0;
        if (world.Width > ExportMaxDim) scale = ExportMaxDim / world.Width;
        if (world.Height * scale > ExportMaxDim) scale = ExportMaxDim / world.Height;

        int pxW = Math.Max(1, (int)Math.Round(world.Width * scale));
        int pxH = Math.Max(1, (int)Math.Round(world.Height * scale));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-world.X * scale, -world.Y * scale));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(new SolidColorBrush(page.BackgroundColor), null, world);

            foreach (var s in page.Strokes)
                s.Draw(dc);

            dc.Pop();
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // =====================================================================
    // Panning + custom lasso
    // =====================================================================

    private Point ScreenToWorld(Point p)
        => new Point((p.X - _viewPanX) / _viewZoom, (p.Y - _viewPanY) / _viewZoom);

    private void CanvasHostBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CanvasHostBorder is null) return;

        bool middleDrag = e.MiddleButton == MouseButtonState.Pressed;
        bool handDrag = e.LeftButton == MouseButtonState.Pressed && _tool == ToolMode.Hand;
        bool lassoDrag = e.LeftButton == MouseButtonState.Pressed && _tool == ToolMode.Lasso;

        if (middleDrag || handDrag)
        {
            _panning = true;
            _panStart = e.GetPosition(CanvasHostBorder);
            _panStartPanX = _viewPanX;
            _panStartPanY = _viewPanY;
            _panStartTime = DateTime.UtcNow;
            CanvasHostBorder.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (lassoDrag)
        {
            var screenPt = e.GetPosition(CanvasHostBorder);
            var worldPt = ScreenToWorld(screenPt);

            if (IsPointOnSelectedElement(worldPt) || IsPointOnSelectedStroke(worldPt))
            {
                StartSelectionDrag(worldPt);
                CanvasHostBorder.CaptureMouse();
                e.Handled = true;
                return;
            }

            StartLasso(screenPt);
            CanvasHostBorder.CaptureMouse();
            e.Handled = true;
        }
    }

    private void CanvasHostBorder_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (CanvasHostBorder is null) return;

        var screenPt = e.GetPosition(CanvasHostBorder);

        if (_panning)
        {
            _viewPanX = _panStartPanX + (screenPt.X - _panStart.X);
            _viewPanY = _panStartPanY + (screenPt.Y - _panStart.Y);
            if (ViewTransform is not null)
                ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
            e.Handled = true;
            return;
        }

        if (_lassoActive)
        {
            _lassoPointsScreen.Add(screenPt);
            UpdateLassoVisual();
            e.Handled = true;
            return;
        }

        if (_draggingSelection)
        {
            var worldPt = ScreenToWorld(screenPt);
            double dx = worldPt.X - _dragStartWorld.X;
            double dy = worldPt.Y - _dragStartWorld.Y;
            UpdateSelectionDrag(dx, dy);
            e.Handled = true;
        }
    }

    private void CanvasHostBorder_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) { EndPan(); e.Handled = true; return; }
        if (_lassoActive) { EndLasso(); e.Handled = true; return; }
        if (_draggingSelection) { EndSelectionDrag(); e.Handled = true; }
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) EndPan();
        else if (_lassoActive) EndLasso();
        else if (_draggingSelection) EndSelectionDrag();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;

        if (CanvasHostBorder is not null)
        {
            if (CanvasHostBorder.IsMouseCaptured) CanvasHostBorder.ReleaseMouseCapture();
            if (CanvasHost is not null) { try { CanvasHost.CacheMode = null; } catch { } }
        }

        double dx = _viewPanX - _panStartPanX;
        double dy = _viewPanY - _panStartPanY;
        double elapsedMs = (DateTime.UtcNow - _panStartTime).TotalMilliseconds;

        if (_tool == ToolMode.Hand
            && Math.Abs(dx) >= SwipeMinDist
            && Math.Abs(dx) > 2.5 * Math.Abs(dy)
            && elapsedMs <= SwipeMaxDurationMs)
        {
            _viewPanX = _panStartPanX;
            _viewPanY = _panStartPanY;
            if (ViewTransform is not null)
                ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);

            if (dx < 0) NextPageButton_Click(this, new RoutedEventArgs());
            else PrevPageButton_Click(this, new RoutedEventArgs());
            UpdateCursor();
            return;
        }

        ApplyView();
        UpdateCursor();
    }

    // ---------------- Lasso ----------------

    private void StartLasso(Point screenPt)
    {
        if (CanvasHost is null) return;

        ClearElementSelection();
        if (InkArea is not null) InkArea.Select(new StrokeCollection());

        _lassoActive = true;
        _lassoPointsScreen.Clear();
        _lassoPointsScreen.Add(screenPt);

        _lassoVisual = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(180, 26, 115, 232)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(18, 26, 115, 232)),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Stretch = Stretch.None
        };
        CanvasHost.Children.Add(_lassoVisual);
    }

    private void UpdateLassoVisual()
    {
        if (_lassoVisual is null || _lassoPointsScreen.Count < 2) return;

        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            var first = ScreenToWorld(_lassoPointsScreen[0]);
            gc.BeginFigure(first, true, true);
            for (int i = 1; i < _lassoPointsScreen.Count; i++)
                gc.LineTo(ScreenToWorld(_lassoPointsScreen[i]), true, false);
        }
        geo.Freeze();
        _lassoVisual.Data = geo;
        _lassoVisual.StrokeThickness = 1.5 / _viewZoom;
    }

    private void EndLasso()
    {
        if (!_lassoActive) return;
        _lassoActive = false;

        if (CanvasHostBorder is not null && CanvasHostBorder.IsMouseCaptured)
            CanvasHostBorder.ReleaseMouseCapture();

        if (_lassoVisual is not null)
        {
            if (_lassoVisual.Parent is Panel p) p.Children.Remove(_lassoVisual);
            _lassoVisual = null;
        }

        if (_lassoPointsScreen.Count < 3 || InkArea is null) return;

        var worldPoly = new List<Point>(_lassoPointsScreen.Count);
        foreach (var sp in _lassoPointsScreen)
            worldPoly.Add(ScreenToWorld(sp));

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(worldPoly[0], true, true);
            for (int i = 1; i < worldPoly.Count; i++)
                gc.LineTo(worldPoly[i], true, false);
        }
        geometry.Freeze();

        var strokesIn = new StrokeCollection();
        foreach (var s in InkArea.Strokes)
        {
            var b = s.GetBounds();
            var center = new Point(b.X + b.Width / 2.0, b.Y + b.Height / 2.0);
            if (geometry.FillContains(center))
                strokesIn.Add(s);
        }
        if (strokesIn.Count > 0) InkArea.Select(strokesIn);

        foreach (var child in InkArea.Children)
        {
            if (child is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe);
            double t = InkCanvas.GetTop(fe);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double cx = l + (fe.ActualWidth > 0 ? fe.ActualWidth / 2 : 60);
            double cy = t + (fe.ActualHeight > 0 ? fe.ActualHeight / 2 : 20);
            if (geometry.FillContains(new Point(cx, cy)))
                _selectedElements.Add(fe);
        }

        UpdateElementSelectionVisuals();

        int total = strokesIn.Count + _selectedElements.Count;
        StatusText.Text = total > 0
            ? $"Selected {strokesIn.Count} stroke(s) and {_selectedElements.Count} item(s). Tap a colour to recolour."
            : "Nothing selected.";
    }

    // ---------------- Selection drag ----------------

    private bool IsPointOnSelectedElement(Point worldPt)
    {
        foreach (var el in _selectedElements)
        {
            if (el is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe);
            double t = InkCanvas.GetTop(fe);
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : 200;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : 30;
            if (new Rect(l, t, w, h).Contains(worldPt)) return true;
        }
        return false;
    }

    private bool IsPointOnSelectedStroke(Point worldPt)
    {
        var selected = InkArea?.GetSelectedStrokes();
        if (selected is null || selected.Count == 0) return false;
        var hits = selected.HitTest(new[] { worldPt }, 4);
        return hits.Count > 0;
    }

    private void StartSelectionDrag(Point worldPt)
    {
        _draggingSelection = true;
        _dragStartWorld = worldPt;

        _elementStartPositions.Clear();
        foreach (var el in _selectedElements)
            _elementStartPositions[el] = (InkCanvas.GetLeft(el), InkCanvas.GetTop(el));

        var sel = InkArea?.GetSelectedStrokes();
        if (sel is not null && sel.Count > 0)
        {
            _dragOriginalStrokes = new StrokeCollection();
            foreach (var s in sel) _dragOriginalStrokes.Add(s);
            _dragPreviewStrokes = null;
        }
        else
        {
            _dragOriginalStrokes = null;
            _dragPreviewStrokes = null;
        }
    }

    private void UpdateSelectionDrag(double dx, double dy)
    {
        if (CanvasHost is null) return;

        foreach (var el in _selectedElements)
        {
            if (_elementStartPositions.TryGetValue(el, out var s))
            {
                InkCanvas.SetLeft(el, s.L + dx);
                InkCanvas.SetTop(el, s.T + dy);
            }
        }

        if (InkArea is not null && _dragOriginalStrokes is not null && _dragOriginalStrokes.Count > 0)
        {
            var rebuilt = new StrokeCollection();
            foreach (var orig in _dragOriginalStrokes)
            {
                var pts = new StylusPointCollection();
                foreach (var p in orig.StylusPoints)
                    pts.Add(new StylusPoint((float)(p.X + dx), (float)(p.Y + dy), p.PressureFactor));
                rebuilt.Add(new Stroke(pts, orig.DrawingAttributes.Clone()));
            }

            _applyingHistory = true;
            try
            {
                if (_dragPreviewStrokes is not null && _dragPreviewStrokes.Count > 0)
                    InkArea.Strokes.Remove(_dragPreviewStrokes);

                InkArea.Strokes.Add(rebuilt);
                InkArea.Select(rebuilt);
            }
            finally { _applyingHistory = false; }

            _dragPreviewStrokes = rebuilt;
        }

        UpdateElementSelectionVisuals();
    }

    private void EndSelectionDrag()
    {
        if (!_draggingSelection) return;
        _draggingSelection = false;

        if (CanvasHostBorder is not null && CanvasHostBorder.IsMouseCaptured)
            CanvasHostBorder.ReleaseMouseCapture();

        if (InkArea is not null && _dragOriginalStrokes is not null && _dragPreviewStrokes is not null)
        {
            if (_dragOriginalStrokes.Count > 0)
            {
                _undo.Push(new StrokeChange(_dragPreviewStrokes, _dragOriginalStrokes));
                _redo.Clear();
            }
        }

        foreach (var el in _selectedElements)
        {
            if (_elementStartPositions.TryGetValue(el, out var start))
            {
                double nowL = InkCanvas.GetLeft(el);
                double nowT = InkCanvas.GetTop(el);
                if (Math.Abs(nowL - start.L) > 0.1 || Math.Abs(nowT - start.T) > 0.1)
                {
                    _undo.Push(new ElementMoveChange
                    {
                        Element = el,
                        OldLeft = start.L, OldTop = start.T,
                        NewLeft = nowL, NewTop = nowT
                    });
                    _redo.Clear();
                }
            }
        }

        _elementStartPositions.Clear();
        _dragOriginalStrokes = null;
        _dragPreviewStrokes = null;

        MarkDirty();
    }

    // ---------------- Element selection visuals ----------------

    private void ClearElementSelection()
    {
        _selectedElements.Clear();
        if (CanvasHost is null) return;
        foreach (var v in _elementSelectionVisuals)
        {
            if (v.Parent is Panel p) p.Children.Remove(v);
        }
        _elementSelectionVisuals.Clear();
    }

    private void UpdateElementSelectionVisuals()
    {
        if (CanvasHost is null) return;

        foreach (var v in _elementSelectionVisuals)
            if (v.Parent is Panel p) p.Children.Remove(v);
        _elementSelectionVisuals.Clear();

        foreach (var el in _selectedElements)
        {
            if (el is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe);
            double t = InkCanvas.GetTop(fe);
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : 200;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : 30;

            var r = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = new SolidColorBrush(Color.FromArgb(200, 26, 115, 232)),
                StrokeThickness = 1.5 / Math.Max(_viewZoom, 0.01),
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(l, t, 0, 0)
            };

            CanvasHost.Children.Add(r);
            _elementSelectionVisuals.Add(r);
        }
    }

    // =====================================================================
    // Title bar buttons
    // =====================================================================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
    }

    // =====================================================================
    // Toolbar menus
    // =====================================================================

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = b;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    // =====================================================================
    // Insert
    // =====================================================================

    private void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            Title = "Insert image"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(dlg.FileName);
            bmp.EndInit();
            bmp.Freeze();

            AddImageToPage(bmp);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not load image.\n\n" + ex.Message,
                "Insert image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PasteClipboardImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                MessageBox.Show(this, "There is no image in the clipboard.",
                    "Paste image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var src = Clipboard.GetImage();
            if (src is null) return;

            if (src.CanFreeze) src.Freeze();
            AddImageToPage(src);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not paste image.\n\n" + ex.Message,
                "Paste image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddImageToPage(BitmapSource bmp)
    {
        double maxDim = 400;
        double scale = 1.0;
        if (bmp.PixelWidth > maxDim) scale = maxDim / bmp.PixelWidth;
        if (bmp.PixelHeight * scale > maxDim) scale = maxDim / bmp.PixelHeight;

        double w = Math.Max(40, bmp.PixelWidth * scale);
        double h = Math.Max(40, bmp.PixelHeight * scale);

        var img = new Image
        {
            Source = bmp,
            Width = w,
            Height = h,
            Stretch = Stretch.Uniform
        };

        PlaceOnCanvas(img);
        StatusText.Text = "Image inserted. Switch to Lasso to move or resize it.";
    }

    private void InsertText_Click(object sender, RoutedEventArgs e) => AddTextToPage(string.Empty);

    private void PasteClipboardText_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText())
        {
            MessageBox.Show(this, "There is no text in the clipboard.",
                "Paste text", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string text = Clipboard.GetText();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int placed = 0;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            AddTextToPage(line);
            placed++;
        }
        if (placed == 0) AddTextToPage(text.Trim());
        StatusText.Text = placed > 1
            ? $"Pasted {placed} lines as separate text boxes."
            : "Pasted text. Switch to Lasso to move it.";
    }

    private void AddTextToPage(string initialText)
    {
        var tb = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 18,
            Width = 260,
            MinHeight = 32,
            Padding = new Thickness(4, 2, 4, 2),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 26, 115, 232)),
            BorderThickness = new Thickness(1),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        PlaceOnCanvas(tb);
        tb.Focus();
        tb.CaretIndex = tb.Text.Length;
    }

    private void PlaceOnCanvas(FrameworkElement element)
    {
        if (InkArea is null) return;

        double visLeft = -_viewPanX / _viewZoom;
        double visTop = -_viewPanY / _viewZoom;

        double left = visLeft + 60;
        double top = visTop + 60 + (_insertCounter % 22) * 42;
        _insertCounter++;

        InkCanvas.SetLeft(element, left);
        InkCanvas.SetTop(element, top);
        InkCanvas.SetRight(element, double.NaN);
        InkCanvas.SetBottom(element, double.NaN);

        InkArea.Children.Add(element);

        MarkDirty();
    }

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        SidePanel.Visibility = SidePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // =====================================================================
    // Page appearance
    // =====================================================================

    private void PaperThumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name
            && Enum.TryParse<PaperStyle>(name, out var ps))
        {
            CurrentPage.Paper = ps;
            UpdatePageBackground();
            MarkDirty();
            StatusText.Text = $"Paper: {rb.ToolTip}";
        }
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string hex)
        {
            CurrentPage.BackgroundColor = ParseHexColor(hex, Colors.White);
            UpdatePageBackground();
            StatusText.Text = $"Background: {rb.ToolTip}";
            MarkDirty();
        }
    }

    private void TemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name
            && Enum.TryParse<PageTemplate>(name, out var t))
        {
            CurrentPage.Template = t;
            UpdatePageBackground();
            StatusText.Text = $"Template: {rb.ToolTip}";
            MarkDirty();
        }
    }

    private void SpacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || PageBackground is null) return;
        if (SpacingCombo.SelectedItem is ComboBoxItem item
            && double.TryParse(item.Content?.ToString(), out var s))
        {
            CurrentPage.Spacing = s;
            UpdatePageBackground();
            MarkDirty();
        }
    }

    private void CursorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CursorCombo is null) return;
        _cursorStyle = CursorCombo.SelectedIndex switch
        {
            1 => PenCursorStyle.Cross,
            2 => PenCursorStyle.HollowRingDot,
            3 => PenCursorStyle.Hidden,
            _ => PenCursorStyle.Arrow
        };
        UpdateCursor();
        SaveSettings();
    }

    private void UpdatePageBackground()
    {
        if (PageBackground is null) return;
        PageBackground.Fill = BuildPageBrush(CurrentPage);
    }

    private static Brush BuildPageBrush(NotebookPage page)
    {
        Brush baseBrush = BuildPaperBaseBrush(page.Paper, page.BackgroundColor);

        if (page.Template == PageTemplate.Blank || page.Spacing <= 0)
            return baseBrush;

        double spacing = page.Spacing;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(baseBrush, null,
            new RectangleGeometry(new Rect(0, 0, spacing, spacing))));

        Color lineColor = GetLineColor(page.BackgroundColor);
        var linePen = new Pen(new SolidColorBrush(lineColor), 1);

        if (page.Template == PageTemplate.Ruled)
        {
            group.Children.Add(new GeometryDrawing(null, linePen,
                new LineGeometry(new Point(0, spacing - 0.5), new Point(spacing, spacing - 0.5))));
        }
        else if (page.Template == PageTemplate.Grid)
        {
            var geo = new GeometryGroup();
            geo.Children.Add(new LineGeometry(new Point(0, spacing - 0.5), new Point(spacing, spacing - 0.5)));
            geo.Children.Add(new LineGeometry(new Point(spacing - 0.5, 0), new Point(spacing - 0.5, spacing)));
            group.Children.Add(new GeometryDrawing(null, linePen, geo));
        }
        else if (page.Template == PageTemplate.Dot)
        {
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(lineColor), null,
                new EllipseGeometry(new Point(spacing / 2, spacing / 2), 1.2, 1.2)));
        }

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, spacing, spacing),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
    }

    private static Brush BuildPaperBaseBrush(PaperStyle paper, Color userColor)
    {
        if (userColor != Colors.White)
            return new SolidColorBrush(userColor);

        if (paper == PaperStyle.White)
            return new SolidColorBrush(Colors.White);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xFC, 0xFC, 0xFA)), null,
            new RectangleGeometry(new Rect(0, 0, 60, 60))));

        var crinklePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0xD0, 0xD0, 0xCC)), 0.8);
        var crinkles = new GeometryGroup();
        crinkles.Children.Add(new LineGeometry(new Point(0, 12), new Point(38, 28)));
        crinkles.Children.Add(new LineGeometry(new Point(10, 0), new Point(46, 60)));
        crinkles.Children.Add(new LineGeometry(new Point(0, 44), new Point(60, 36)));
        crinkles.Children.Add(new LineGeometry(new Point(28, 0), new Point(52, 60)));
        crinkles.Children.Add(new LineGeometry(new Point(0, 56), new Point(60, 52)));
        crinkles.Children.Add(new LineGeometry(new Point(42, 0), new Point(60, 24)));
        crinkles.Children.Add(new LineGeometry(new Point(18, 0), new Point(0, 30)));
        group.Children.Add(new GeometryDrawing(null, crinklePen, crinkles));

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 60, 60),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
    }

    private static Color GetLineColor(Color bg)
    {
        double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return lum > 0.5
            ? Color.FromArgb(120, 60, 60, 60)
            : Color.FromArgb(120, 220, 220, 220);
    }

    private static Color ParseHexColor(string hex, Color fallback)
    {
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            if (obj is Color c) return c;
        }
        catch { }
        return fallback;
    }

    private void SyncUIWithSettings()
    {
        if (!_uiReady) return;

        if (PaperWhite is not null && PaperCrumpled is not null)
        {
            RadioButton paperBtn = CurrentPage.Paper switch
            {
                PaperStyle.Crumpled => PaperCrumpled,
                _ => PaperWhite
            };
            paperBtn.IsChecked = true;
        }

        string hex = $"#{CurrentPage.BackgroundColor.R:X2}{CurrentPage.BackgroundColor.G:X2}{CurrentPage.BackgroundColor.B:X2}";
        RadioButton[] swatches = { ColorWhite, ColorCream, ColorLightGray, ColorSage, ColorSky, ColorNavy, ColorDarkGreen, ColorBlack };
        bool matched = false;
        foreach (var rb in swatches)
        {
            if (rb is null) continue;
            if (rb.Tag is string s && string.Equals(s, hex, StringComparison.OrdinalIgnoreCase))
            {
                rb.IsChecked = true;
                matched = true;
                break;
            }
        }
        if (!matched && ColorWhite is not null) ColorWhite.IsChecked = true;

        if (TemplateBlank is not null && TemplateRuled is not null && TemplateGrid is not null && TemplateDot is not null)
        {
            RadioButton templateBtn = CurrentPage.Template switch
            {
                PageTemplate.Blank => TemplateBlank,
                PageTemplate.Ruled => TemplateRuled,
                PageTemplate.Grid => TemplateGrid,
                PageTemplate.Dot => TemplateDot,
                _ => TemplateBlank
            };
            templateBtn.IsChecked = true;
        }

        if (SpacingCombo is not null)
        {
            foreach (ComboBoxItem item in SpacingCombo.Items)
            {
                if (item.Content?.ToString() == ((int)CurrentPage.Spacing).ToString())
                {
                    SpacingCombo.SelectedItem = item;
                    break;
                }
            }
        }

        UpdatePageBackground();
    }

    // =====================================================================
    // Tools
    // =====================================================================

    private void PenButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Pen;
        EndPan();
        ClearElementSelection();
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.Ink;
        }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Pen selected (1).";
        ShowToolPopup(PenButton, "Pen");
    }

    private void HighlighterButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Highlighter;
        EndPan();
        ClearElementSelection();
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.Ink;
        }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Highlighter selected (2).";
        ShowToolPopup(HighlighterButton, "Highlighter");
    }

    private void EraserButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Eraser;
        EndPan();
        ClearElementSelection();
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
        UpdateCursor();
        if (StatusText is not null) StatusText.Text = "Eraser selected (3).";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void LassoButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Lasso;
        EndPan();

        if (CanvasHost is not null) { try { CanvasHost.CacheMode = null; } catch { } }

        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.None;
        }
        UpdateCursor();
        if (StatusText is not null)
            StatusText.Text = "Lasso select (4): drag around ink/items to select. Drag inside selection to move. Tap a colour to recolour.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void InkArea_SelectionChanged(object sender, EventArgs e)
    {
        if (InkArea is null) return;
        int n = InkArea.GetSelectedStrokes().Count;
        int m = _selectedElements.Count;
        if ((n > 0 || m > 0) && StatusText is not null)
            StatusText.Text = $"Selected {n} stroke(s) and {m} item(s). Tap a colour to recolour.";
    }

    private void SelectAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (InkArea is null) return;
        if (_tool != ToolMode.Lasso)
        {
            _tool = ToolMode.Lasso;
            if (LassoButton is not null) LassoButton.IsChecked = true;
            InkArea.EditingMode = InkCanvasEditingMode.None;
        }
        InkArea.Select(InkArea.Strokes);

        _selectedElements.Clear();
        foreach (var c in InkArea.Children)
            if (c is FrameworkElement fe) _selectedElements.Add(fe);
        UpdateElementSelectionVisuals();

        StatusText.Text = $"Selected all {InkArea.Strokes.Count} stroke(s) and {_selectedElements.Count} item(s).";
        e.Handled = true;
    }

    private void HandButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Hand;
        ClearElementSelection();
        if (InkArea is not null) InkArea.IsHitTestVisible = false;
        UpdateCursor();
        if (StatusText is not null)
            StatusText.Text = "Hand tool (5): drag to pan. Fast horizontal swipe turns the page.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (InkArea is null) return;
        if (InkArea.Strokes.Count == 0)
        {
            StatusText.Text = "The page is already empty.";
            return;
        }

        var answer = MessageBox.Show(this,
            "Remove everything on this page?\n\nYou can still press Undo (Ctrl+Z) right after.",
            "Clear page", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.OK)
        {
            InkArea.Strokes.Clear();
            StatusText.Text = "Page cleared.";
        }
    }

    private void InkArea_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        var points = e.Stroke.StylusPoints;
        if (points.Count == 0) return;

        float min = 1f, max = 0f;
        foreach (var p in points)
        {
            min = Math.Min(min, p.PressureFactor);
            max = Math.Max(max, p.PressureFactor);
        }

        bool varied = (max - min) > 0.01f;
        string verdict = varied
            ? "pressure detected"
            : "no pressure change (mouse, or the tablet driver is not sending pressure)";

        StatusText.Text = $"Last stroke: {points.Count} points, pressure {min:0.00} to {max:0.00} - {verdict}.";

        if (!e.Stroke.DrawingAttributes.IsHighlighter)
            SmoothCompletedStroke(e.Stroke);
    }

    private void SmoothCompletedStroke(Stroke original)
    {
        var pts = original.StylusPoints;
        int n = pts.Count;
        if (n < 3) return;

        var rawX = new double[n];
        var rawY = new double[n];
        var rawP = new float[n];
        for (int i = 0; i < n; i++)
        {
            rawX[i] = pts[i].X;
            rawY[i] = pts[i].Y;
            rawP[i] = pts[i].PressureFactor;
        }

        var sx = new double[n];
        var sy = new double[n];
        var sp = new float[n];
        const int half = 2;
        for (int i = 0; i < n; i++)
        {
            double sumX = 0, sumY = 0;
            float sumP = 0;
            int cnt = 0;
            for (int j = -half; j <= half; j++)
            {
                int k = i + j;
                if (k < 0 || k >= n) continue;
                sumX += rawX[k];
                sumY += rawY[k];
                sumP += rawP[k];
                cnt++;
            }
            sx[i] = sumX / cnt;
            sy[i] = sumY / cnt;
            sp[i] = sumP / cnt;
        }

        const double step = 1.2;
        const int maxOut = 6000;
        var outPts = new StylusPointCollection();
        outPts.Add(new StylusPoint((float)sx[0], (float)sy[0], Clamp01(sp[0])));

        for (int i = 0; i < n - 1; i++)
        {
            double p1x = sx[i], p1y = sy[i];
            double p2x = sx[i + 1], p2y = sy[i + 1];
            double p0x = i > 0 ? sx[i - 1] : p1x - (p2x - p1x);
            double p0y = i > 0 ? sy[i - 1] : p1y - (p2y - p1y);
            double p3x = i < n - 2 ? sx[i + 2] : p2x + (p2x - p1x);
            double p3y = i < n - 2 ? sy[i + 2] : p2y + (p2y - p1y);

            double segLen = Math.Sqrt((p2x - p1x) * (p2x - p1x) + (p2y - p1y) * (p2y - p1y));
            int subdiv = Math.Max(1, (int)Math.Ceiling(segLen / step));
            if (subdiv > 500) subdiv = 500;

            for (int s = 1; s <= subdiv; s++)
            {
                double t = (double)s / subdiv;
                double t2 = t * t, t3 = t2 * t;
                double x = 0.5 * ((2 * p1x) + (-p0x + p2x) * t + (2 * p0x - 5 * p1x + 4 * p2x - p3x) * t2 + (-p0x + 3 * p1x - 3 * p2x + p3x) * t3);
                double y = 0.5 * ((2 * p1y) + (-p0y + p2y) * t + (2 * p0y - 5 * p1y + 4 * p2y - p3y) * t2 + (-p0y + 3 * p1y - 3 * p2y + p3y) * t3);
                float pressure = (float)(sp[i] + (sp[i + 1] - sp[i]) * t);

                outPts.Add(new StylusPoint((float)x, (float)y, Clamp01(pressure)));
                if (outPts.Count >= maxOut) break;
            }
            if (outPts.Count >= maxOut) break;
        }

        var da = original.DrawingAttributes.Clone();
        da.FitToCurve = false;
        var smoothed = new Stroke(outPts, da);

        int idx = InkArea.Strokes.IndexOf(original);
        if (idx < 0) return;

        _applyingHistory = true;
        try
        {
            InkArea.Strokes.RemoveAt(idx);
            InkArea.Strokes.Insert(idx, smoothed);
        }
        finally
        {
            _applyingHistory = false;
        }

        if (_undo.Count > 0)
        {
            var last = _undo.Pop();
            if (last is StrokeChange sc && sc.Removed.Count == 0 && sc.Added.Count == 1 && sc.Added[0] == original)
                _undo.Push(new StrokeChange(new StrokeCollection { smoothed }, sc.Removed));
            else
                _undo.Push(last);
        }
    }

    private static float Clamp01(float v)
    {
        if (v < 0f) return 0f;
        if (v > 1f) return 1f;
        return v;
    }

    // =====================================================================
    // Undo / redo
    // =====================================================================

    private void Strokes_Changed(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_applyingHistory) return;
        _undo.Push(new StrokeChange(new StrokeCollection(e.Added), new StrokeCollection(e.Removed)));
        _redo.Clear();
        MarkDirty();
    }

    private void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = _undo.Count > 0; e.Handled = true; }
    private void Redo_CanExecute(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = _redo.Count > 0; e.Handled = true; }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var change = _undo.Pop();
        ReverseChange(change);
        _redo.Push(change);
        MarkDirty();
    }

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var change = _redo.Pop();
        ForwardChange(change);
        _undo.Push(change);
        MarkDirty();
    }

    private void ReverseChange(object change)
    {
        if (InkArea is null) return;
        if (change is StrokeChange sc)
        {
            _applyingHistory = true;
            try
            {
                if (sc.Added.Count > 0) InkArea.Strokes.Remove(sc.Added);
                if (sc.Removed.Count > 0) InkArea.Strokes.Add(sc.Removed);
            }
            finally { _applyingHistory = false; }
        }
        else if (change is ElementMoveChange emc)
        {
            InkCanvas.SetLeft(emc.Element, emc.OldLeft);
            InkCanvas.SetTop(emc.Element, emc.OldTop);
        }
    }

    private void ForwardChange(object change)
    {
        if (InkArea is null) return;
        if (change is StrokeChange sc)
        {
            _applyingHistory = true;
            try
            {
                if (sc.Removed.Count > 0) InkArea.Strokes.Remove(sc.Removed);
                if (sc.Added.Count > 0) InkArea.Strokes.Add(sc.Added);
            }
            finally { _applyingHistory = false; }
        }
        else if (change is ElementMoveChange emc)
        {
            InkCanvas.SetLeft(emc.Element, emc.NewLeft);
            InkCanvas.SetTop(emc.Element, emc.NewTop);
        }
    }

    // =====================================================================
    // New / Open / Save
    // =====================================================================

    private void New_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        _pages.Clear();
        _pages.Add(new NotebookPage());
        _currentPageIndex = 0;
        LoadCurrentPageIntoCanvas();

        _currentPath = null;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = "New blank note.";
    }

    private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        var dialog = new OpenFileDialog
        {
            Filter = FileFilter,
            DefaultExt = FileExtension,
            Title = "Open a Canvaas note"
        };

        if (dialog.ShowDialog(this) == true)
            OpenFromFile(dialog.FileName);
    }

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => SaveCurrent();
    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveAs();

    private bool SaveCurrent() => _currentPath is null ? SaveAs() : SaveToFile(_currentPath);

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = FileFilter,
            DefaultExt = FileExtension,
            AddExtension = true,
            Title = "Save your Canvaas note",
            FileName = _currentPath is null ? "My note" : System.IO.Path.GetFileNameWithoutExtension(_currentPath)
        };

        if (dialog.ShowDialog(this) != true) return false;
        return SaveToFile(dialog.FileName);
    }

    private bool SaveToFile(string path)
    {
        string tempPath = path + ".tmp";

        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var manifest = new Manifest
                {
                    FormatVersion = CurrentFormatVersion,
                    App = "Canvaas",
                    AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
                    SavedAtUtc = DateTime.UtcNow.ToString("o"),
                    CurrentPageIndex = _currentPageIndex,
                    Pages = new List<PageManifest>()
                };

                foreach (var page in _pages)
                {
                    manifest.Pages.Add(new PageManifest
                    {
                        BackgroundColor = $"#{page.BackgroundColor.R:X2}{page.BackgroundColor.G:X2}{page.BackgroundColor.B:X2}",
                        Template = page.Template.ToString(),
                        Spacing = page.Spacing,
                        Paper = page.Paper.ToString()
                    });
                }

                var manifestEntry = zip.CreateEntry(ManifestEntryName);
                using (var entryStream = manifestEntry.Open())
                    JsonSerializer.Serialize(entryStream, manifest, JsonOptions);

                for (int i = 0; i < _pages.Count; i++)
                {
                    if (_pages[i].Strokes.Count == 0) continue;
                    using var inkBuffer = new MemoryStream();
                    _pages[i].Strokes.Save(inkBuffer);
                    inkBuffer.Position = 0;

                    var inkEntry = zip.CreateEntry(string.Format(PageEntryFormat, i));
                    using var entryStream = inkEntry.Open();
                    inkBuffer.CopyTo(entryStream);
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            MessageBox.Show(this,
                "Canvaas could not save your note.\n\nYour previous saved file (if any) was not changed.\n\nDetails: " + ex.Message,
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _currentPath = path;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = "Saved: " + path;
        return true;
    }

    private void OpenFromFile(string path)
    {
        List<NotebookPage> loadedPages;
        int loadedCurrentPage = 0;

        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Read);

            var manifestEntry = zip.GetEntry(ManifestEntryName)
                ?? throw new InvalidDataException("This is not a Canvaas note (manifest.json is missing).");

            Manifest? manifest;
            using (var entryStream = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<Manifest>(entryStream, JsonOptions);

            if (manifest is null)
                throw new InvalidDataException("The note's manifest.json is empty.");

            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                MessageBox.Show(this,
                    "This note was saved by a newer version of Canvaas and cannot be opened by this version.\n\nNothing was changed.",
                    "Newer file version", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            loadedPages = new List<NotebookPage>();

            if (manifest.FormatVersion >= 3 && manifest.Pages is { Count: > 0 })
            {
                for (int i = 0; i < manifest.Pages.Count; i++)
                {
                    var pm = manifest.Pages[i];
                    var page = new NotebookPage
                    {
                        BackgroundColor = ParseHexColor(pm.BackgroundColor ?? "#FFFFFF", Colors.White),
                        Spacing = pm.Spacing is double s && s > 0 ? s : 40
                    };
                    if (Enum.TryParse<PageTemplate>(pm.Template, out var t)) page.Template = t;
                    if (Enum.TryParse<PaperStyle>(pm.Paper, out var ps)) page.Paper = ps;

                    var inkEntry = zip.GetEntry(string.Format(PageEntryFormat, i));
                    if (inkEntry is not null)
                    {
                        using var buffer = new MemoryStream();
                        using (var es = inkEntry.Open()) es.CopyTo(buffer);
                        buffer.Position = 0;
                        page.Strokes = new StrokeCollection(buffer);
                    }
                    loadedPages.Add(page);
                }

                loadedCurrentPage = Math.Clamp(manifest.CurrentPageIndex, 0, loadedPages.Count - 1);
            }
            else
            {
                var page = new NotebookPage();
                if (manifest.FormatVersion >= 2)
                {
                    // old v1/v2 files didn't have these fields; keep defaults if absent
                    if (Enum.TryParse<PageTemplate>("Blank", out var t)) page.Template = t;
                }

                var inkEntry = zip.GetEntry(LegacyInkEntryName);
                if (inkEntry is not null)
                {
                    using var buffer = new MemoryStream();
                    using (var es = inkEntry.Open()) es.CopyTo(buffer);
                    buffer.Position = 0;
                    page.Strokes = new StrokeCollection(buffer);
                }
                loadedPages.Add(page);
                loadedCurrentPage = 0;
            }

            if (loadedPages.Count == 0)
                loadedPages.Add(new NotebookPage());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Canvaas could not open this file.\n\nThe file was not changed, and your current page is still here.\n\nDetails: " + ex.Message,
                "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _pages.Clear();
        _pages.AddRange(loadedPages);
        _currentPageIndex = loadedCurrentPage;
        LoadCurrentPageIntoCanvas();

        _currentPath = path;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = $"Opened: {path}  ({_pages.Count} page{(_pages.Count == 1 ? "" : "s")})";
    }

    // =====================================================================
    // Unsaved-changes protection and small helpers
    // =====================================================================

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
            e.Cancel = true;
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty) return true;

        var answer = MessageBox.Show(this,
            "You have unsaved changes. Save them first?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => SaveCurrent(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateTitle()
    {
        string name = _currentPath is null ? "Untitled" : System.IO.Path.GetFileNameWithoutExtension(_currentPath);
        string text = $"{name}{(_dirty ? " *" : "")} - Canvaas";
        Title = text;
        if (TitleBarText is not null) TitleBarText.Text = text;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}