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
using System.Windows.Threading;
using Microsoft.Win32;

namespace Canvaas;

public partial class MainWindow : Window
{
    private const int CurrentFormatVersion = 4;
    private const string FileExtension = ".canvaas";
    private const string FileFilter = "Canvaas note (*.canvaas)|*.canvaas";
    private const string ManifestEntryName = "manifest.json";
    private const string LegacyInkEntryName = "ink.isf";
    private const string PageEntryFormat = "page_{0:D3}.isf";

    private const double PageWidthDefault = 800;
    private const double PageHeightDefault = 1120;
    private const double InfiniteSize = 6000;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 6.00;
    private const double ExportMaxDim = 3000;

    private static readonly double[] ZoomLevels =
    {
        0.05, 0.10, 0.15, 0.20, 0.25, 0.33, 0.50, 0.67, 0.75, 0.90,
        1.00, 1.10, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00, 4.00, 5.00, 6.00
    };

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
        public string? Mode { get; set; }
    }

    private sealed class Manifest
    {
        public int FormatVersion { get; set; }
        public string App { get; set; } = "Canvaas";
        public string AppVersion { get; set; } = "";
        public string SavedAtUtc { get; set; } = "";
        public string? BackgroundColor { get; set; }
        public string? Template { get; set; }
        public double? Spacing { get; set; }
        public int CurrentPageIndex { get; set; }
        public List<PageManifest>? Pages { get; set; }
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

    private enum PageTemplate { Blank, Ruled, Grid, Dot }
    private enum CanvasMode { Page, Infinite }
    private enum PenCursorStyle { Arrow, Cross, YellowArrow, YellowDot, Hidden }
    private enum ToolMode { Pen, Highlighter, Eraser, Lasso, Hand }

    private sealed class NotebookPage
    {
        public StrokeCollection Strokes { get; set; } = new StrokeCollection();
        public Color BackgroundColor { get; set; } = Colors.White;
        public PageTemplate Template { get; set; } = PageTemplate.Blank;
        public double Spacing { get; set; } = 40;
        public CanvasMode Mode { get; set; } = CanvasMode.Page;
    }

    public sealed class PageThumb
    {
        public int Index { get; set; }
        public string Label => $"Page {Index + 1}";
        public ImageSource? Thumbnail { get; set; }
    }

    public sealed class PopupColorItem
    {
        public Brush Brush { get; set; } = Brushes.Black;
        public string Hex { get; set; } = "#000000";
        public string Name { get; set; } = "";
    }

    private readonly List<NotebookPage> _pages = new();
    private int _currentPageIndex;

    private readonly Stack<StrokeChange> _undo = new();
    private readonly Stack<StrokeChange> _redo = new();
    private bool _applyingHistory;

    private string? _currentPath;
    private bool _dirty;

    private double _zoom = 1.0;
    private bool _suppressPageListChange;
    private bool _fullscreenMode;
    private bool _uiReady;

    private ToolMode _tool = ToolMode.Pen;
    private Color _penColor = Colors.Black;
    private double _penSize = 2.5;
    private bool _pressureEnabled = true;
    private PenCursorStyle _cursorStyle = PenCursorStyle.Arrow;

    private Cursor? _yellowArrowCursor;
    private Cursor? _yellowDotCursor;

    private bool _panning;
    private Point _panStart;
    private double _panStartTfX, _panStartTfY;

    private DispatcherTimer? _thumbnailTimer;

    private NotebookPage CurrentPage => _pages[_currentPageIndex];

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

        _yellowArrowCursor = CreateYellowArrowCursor();
        _yellowDotCursor = CreateYellowDotCursor();

        Loaded += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WindowState = WindowState.Maximized;
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
        CursorCombo.SelectedIndex = 0;

        ApplyPenAttributes();
        UpdateCursor();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Canvaas v{version.Major}.{version.Minor}.{version.Build}";

        ApplyZoom();
        UpdateTitle();

        Dispatcher.BeginInvoke(new Action(RefreshThumbnails), DispatcherPriority.Background);
    }

    // =====================================================================
    // Custom cursors
    // =====================================================================

    private static Cursor CreateYellowArrowCursor()
    {
        try
        {
            const int size = 32;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var yellow = new SolidColorBrush(Color.FromRgb(255, 210, 0));
                var blackPen = new Pen(Brushes.Black, 1.4);

                var geo = new StreamGeometry();
                using (var gc = geo.Open())
                {
                    gc.BeginFigure(new Point(3, 2), true, true);
                    gc.LineTo(new Point(3, 24), true, false);
                    gc.LineTo(new Point(9, 18), true, false);
                    gc.LineTo(new Point(13, 28), true, false);
                    gc.LineTo(new Point(17, 26), true, false);
                    gc.LineTo(new Point(13, 16), true, false);
                    gc.LineTo(new Point(21, 16), true, false);
                    gc.LineTo(new Point(3, 2), true, false);
                }
                geo.Freeze();

                dc.DrawGeometry(yellow, blackPen, geo);
            }

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            return CreateCursorFromBitmap(rtb, 3, 2);
        }
        catch { return Cursors.Arrow; }
    }

    private static Cursor CreateYellowDotCursor()
    {
        try
        {
            const int size = 32;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var yellow = new SolidColorBrush(Color.FromRgb(255, 210, 0));
                var blackPen = new Pen(Brushes.Black, 1.5);
                dc.DrawEllipse(yellow, blackPen, new Point(16, 16), 7, 7);
                dc.DrawEllipse(Brushes.Black, null, new Point(16, 16), 1.8, 1.8);
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            return CreateCursorFromBitmap(rtb, 16, 16);
        }
        catch { return Cursors.Cross; }
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
    // Colour palette for the tool popup
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
            _penColor = ParseHexColor(hex, Colors.Black);
            ApplyPenAttributes();
            UpdateThicknessPreview();
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

    // =====================================================================
    // Cursor
    // =====================================================================

    private void UpdateCursor()
    {
        if (PageBorder is null) return;

        Cursor c;
        if (_tool == ToolMode.Hand)
        {
            c = Cursors.SizeAll;
        }
        else if (_tool == ToolMode.Lasso)
        {
            c = Cursors.Cross;
        }
        else
        {
            c = _cursorStyle switch
            {
                PenCursorStyle.Cross => Cursors.Cross,
                PenCursorStyle.YellowArrow => _yellowArrowCursor ?? Cursors.Arrow,
                PenCursorStyle.YellowDot => _yellowDotCursor ?? Cursors.Cross,
                PenCursorStyle.Hidden => Cursors.None,
                _ => Cursors.Arrow
            };
        }

        PageBorder.Cursor = c;
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        }
    }

    // =====================================================================
    // Zoom
    // =====================================================================

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => StepZoom(+1);
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => StepZoom(-1);
    private void ZoomResetButton_Click(object sender, RoutedEventArgs e) { _zoom = 1.0; ApplyZoom(); }

    private void StepZoom(int direction)
    {
        int current = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < ZoomLevels.Length; i++)
        {
            double d = Math.Abs(ZoomLevels[i] - _zoom);
            if (d < bestDist) { bestDist = d; current = i; }
        }
        int next = current + direction;
        if (next < 0) next = 0;
        if (next >= ZoomLevels.Length) next = ZoomLevels.Length - 1;
        _zoom = ZoomLevels[next];
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (PageScale is null) return;
        PageScale.ScaleX = _zoom;
        PageScale.ScaleY = _zoom;
        if (ZoomText is not null && !ZoomText.IsFocused)
            ZoomText.Text = $"{(int)Math.Round(_zoom * 100)}%";
    }

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
            double z = pct / 100.0;
            if (z < MinZoom) z = MinZoom;
            if (z > MaxZoom) z = MaxZoom;
            _zoom = z;
        }
        ApplyZoom();
    }

    private void PageScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            StepZoom(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        }
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

        ApplyCanvasMode();
        SyncUIWithSettings();
        UpdatePageNavigationUI();

        if (PageListBox is not null && _currentPageIndex < PageListBox.Items.Count)
        {
            _suppressPageListChange = true;
            PageListBox.SelectedIndex = _currentPageIndex;
            _suppressPageListChange = false;
        }
    }

    private void ApplyCanvasMode()
    {
        if (PageBorder is null) return;

        if (CurrentPage.Mode == CanvasMode.Infinite)
        {
            PageBorder.BorderThickness = new Thickness(0);
            PageBorder.Width = InfiniteSize;
            PageBorder.Height = InfiniteSize;
        }
        else
        {
            PageBorder.BorderThickness = new Thickness(1);
            PageBorder.Width = PageWidthDefault;
            PageBorder.Height = PageHeightDefault;
        }

        if (InfiniteCanvasButton is not null)
        {
            InfiniteCanvasButton.Background = CurrentPage.Mode == CanvasMode.Infinite
                ? new SolidColorBrush(Color.FromRgb(0xDC, 0xE9, 0xF9))
                : Brushes.Transparent;
        }
    }

    private void InfiniteCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentPage.Mode = CurrentPage.Mode == CanvasMode.Page ? CanvasMode.Infinite : CanvasMode.Page;
        ApplyCanvasMode();
        StatusText.Text = CurrentPage.Mode == CanvasMode.Infinite
            ? "Infinite canvas mode."
            : "Fixed page mode.";
        MarkDirty();
        ScheduleThumbnailRefresh();
    }

    private void UpdatePageNavigationUI()
    {
        if (PageCounterText is null) return;
        PageCounterText.Text = $"{_currentPageIndex + 1} / {_pages.Count}";
        if (PrevPageButton is not null) PrevPageButton.IsEnabled = _currentPageIndex > 0;
        if (NextPageButton is not null) NextPageButton.IsEnabled = _currentPageIndex < _pages.Count - 1;
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
            Mode = CanvasMode.Page
        };

        _pages.Insert(_currentPageIndex + 1, newPage);
        _currentPageIndex++;
        LoadCurrentPageIntoCanvas();
        MarkDirty();
        RefreshThumbnails();
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
        RefreshThumbnails();
        StatusText.Text = $"Deleted. Now on page {_currentPageIndex + 1} of {_pages.Count}.";
    }

    private void PageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPageListChange) return;
        if (PageListBox is null) return;
        if (PageListBox.SelectedIndex >= 0 && PageListBox.SelectedIndex != _currentPageIndex)
        {
            _currentPageIndex = PageListBox.SelectedIndex;
            LoadCurrentPageIntoCanvas();
        }
    }

    private void RefreshThumbnails()
    {
        if (PageListBox is null) return;
        var items = new List<PageThumb>();
        for (int i = 0; i < _pages.Count; i++)
        {
            items.Add(new PageThumb
            {
                Index = i,
                Thumbnail = RenderThumbnail(_pages[i], 120, 168)
            });
        }
        _suppressPageListChange = true;
        PageListBox.ItemsSource = items;
        if (_currentPageIndex >= 0 && _currentPageIndex < items.Count)
            PageListBox.SelectedIndex = _currentPageIndex;
        _suppressPageListChange = false;
    }

    private void ScheduleThumbnailRefresh()
    {
        if (_thumbnailTimer is null)
        {
            _thumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _thumbnailTimer.Tick += (s, e) =>
            {
                _thumbnailTimer!.Stop();
                RefreshThumbnails();
            };
        }
        _thumbnailTimer.Stop();
        _thumbnailTimer.Start();
    }

    private ImageSource RenderThumbnail(NotebookPage page, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(page.BackgroundColor), null, new Rect(0, 0, w, h));

            double pageW = page.Mode == CanvasMode.Infinite ? InfiniteSize : PageWidthDefault;
            double pageH = page.Mode == CanvasMode.Infinite ? InfiniteSize : PageHeightDefault;
            double scale = Math.Min(w / pageW, h / pageH);

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.PushTransform(new ScaleTransform(scale, scale));
            try
            {
                foreach (var stroke in page.Strokes)
                    stroke.Draw(dc);
            }
            catch { }
            dc.Pop();
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
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
        double pageW = page.Mode == CanvasMode.Infinite ? InfiniteSize : PageWidthDefault;
        double pageH = page.Mode == CanvasMode.Infinite ? InfiniteSize : PageHeightDefault;

        double scale = 1.0;
        if (pageW > ExportMaxDim) scale = ExportMaxDim / pageW;
        if (pageH * scale > ExportMaxDim) scale = ExportMaxDim / pageH;

        int pxW = Math.Max(1, (int)Math.Round(pageW * scale));
        int pxH = Math.Max(1, (int)Math.Round(pageH * scale));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            var bg = BuildPageBrush(page.BackgroundColor, page.Template, page.Spacing);
            dc.DrawRectangle(bg, null, new Rect(0, 0, pageW, pageH));
            foreach (var s in page.Strokes)
                s.Draw(dc);
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
    // Panning
    // =====================================================================

    private void PageBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        bool middleDrag = e.MiddleButton == MouseButtonState.Pressed;
        bool handDrag = e.LeftButton == MouseButtonState.Pressed && _tool == ToolMode.Hand;

        if (!middleDrag && !handDrag) return;
        if (PageScroller is null || PageBorder is null || PanTransform is null) return;

        _panning = true;
        _panStart = e.GetPosition(PageScroller);
        _panStartTfX = PanTransform.X;
        _panStartTfY = PanTransform.Y;
        PageBorder.CaptureMouse();

        try
        {
            var cache = new BitmapCache
            {
                SnapsToDevicePixels = true,
                EnableClearType = false,
                RenderAtScale = 1.0
            };
            cache.Freeze();
            PageBorder.CacheMode = cache;
        }
        catch { }

        e.Handled = true;
    }

    private void PageBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || PageScroller is null || PanTransform is null) return;
        var p = e.GetPosition(PageScroller);
        PanTransform.X = _panStartTfX + (p.X - _panStart.X);
        PanTransform.Y = _panStartTfY + (p.Y - _panStart.Y);
        e.Handled = true;
    }

    private void PageBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
        e.Handled = true;
    }

    private void PageBorder_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_panning) EndPan();
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) EndPan();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;

        if (PageBorder is not null)
        {
            if (PageBorder.IsMouseCaptured)
                PageBorder.ReleaseMouseCapture();

            try { PageBorder.CacheMode = null; } catch { }
        }

        if (PageScroller is not null && PanTransform is not null)
        {
            double tx = PanTransform.X;
            double ty = PanTransform.Y;

            if (Math.Abs(tx) > 0.01 || Math.Abs(ty) > 0.01)
            {
                PageScroller.ScrollToHorizontalOffset(PageScroller.HorizontalOffset - tx);
                PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset - ty);
                PageScroller.UpdateLayout();
                PanTransform.X = 0;
                PanTransform.Y = 0;
            }
        }

        UpdateCursor();
    }

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (LeftPanel is null) return;
        LeftPanel.Visibility = LeftPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
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

    private void AddText_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = "Add Text is coming in a future update.";

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        SidePanel.Visibility = SidePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // =====================================================================
    // Page appearance
    // =====================================================================

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string hex)
        {
            CurrentPage.BackgroundColor = ParseHexColor(hex, Colors.White);
            UpdatePageBackground();
            StatusText.Text = $"Background: {rb.ToolTip}";
            MarkDirty();
            ScheduleThumbnailRefresh();
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
            ScheduleThumbnailRefresh();
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
            2 => PenCursorStyle.YellowArrow,
            3 => PenCursorStyle.YellowDot,
            4 => PenCursorStyle.Hidden,
            _ => PenCursorStyle.Arrow
        };
        UpdateCursor();
    }

    private void UpdatePageBackground()
    {
        if (PageBackground is null) return;
        PageBackground.Fill = BuildPageBrush(CurrentPage.BackgroundColor, CurrentPage.Template, CurrentPage.Spacing);
    }

    private static Brush BuildPageBrush(Color baseColor, PageTemplate template, double spacing)
    {
        if (template == PageTemplate.Blank || spacing <= 0)
            return new SolidColorBrush(baseColor);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(baseColor), null,
            new RectangleGeometry(new Rect(0, 0, spacing, spacing))));

        Color lineColor = GetLineColor(baseColor);
        var linePen = new Pen(new SolidColorBrush(lineColor), 1);

        if (template == PageTemplate.Ruled)
        {
            group.Children.Add(new GeometryDrawing(null, linePen,
                new LineGeometry(new Point(0, spacing - 0.5), new Point(spacing, spacing - 0.5))));
        }
        else if (template == PageTemplate.Grid)
        {
            var geo = new GeometryGroup();
            geo.Children.Add(new LineGeometry(new Point(0, spacing - 0.5), new Point(spacing, spacing - 0.5)));
            geo.Children.Add(new LineGeometry(new Point(spacing - 0.5, 0), new Point(spacing - 0.5, spacing)));
            group.Children.Add(new GeometryDrawing(null, linePen, geo));
        }
        else if (template == PageTemplate.Dot)
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
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.Ink;
        }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Pen selected.";
        ShowToolPopup(PenButton, "Pen");
    }

    private void HighlighterButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Highlighter;
        EndPan();
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.Ink;
        }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Highlighter selected.";
        ShowToolPopup(HighlighterButton, "Highlighter");
    }

    private void EraserButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Eraser;
        EndPan();
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.EraseByStroke;
        }
        UpdateCursor();
        if (StatusText is not null) StatusText.Text = "Eraser selected: touch a stroke to remove it.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void LassoButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Lasso;
        EndPan();
        if (InkArea is not null)
        {
            InkArea.IsHitTestVisible = true;
            InkArea.EditingMode = InkCanvasEditingMode.Select;
        }
        UpdateCursor();
        if (StatusText is not null)
            StatusText.Text = "Lasso select: drag around ink to select it. Drag inside the selection to move; drag a corner handle to resize.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void InkArea_SelectionChanged(object sender, EventArgs e)
    {
        if (InkArea is null) return;
        int n = InkArea.GetSelectedStrokes().Count;
        if (n > 0 && StatusText is not null)
            StatusText.Text = $"Selected {n} stroke{(n == 1 ? "" : "s")}. Drag to move, drag a handle to resize, Delete to remove.";
    }

    private void SelectAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (InkArea is null) return;
        if (_tool != ToolMode.Lasso)
        {
            _tool = ToolMode.Lasso;
            if (LassoButton is not null) LassoButton.IsChecked = true;
            InkArea.EditingMode = InkCanvasEditingMode.Select;
        }
        InkArea.SelectedStrokes.Clear();
        foreach (var s in InkArea.Strokes)
            InkArea.SelectedStrokes.Add(s);
        e.Handled = true;
    }

    private void HandButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Hand;
        if (InkArea is not null) InkArea.IsHitTestVisible = false;
        UpdateCursor();
        if (StatusText is not null) StatusText.Text = "Hand tool: drag over the page to move around.";
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

        ScheduleThumbnailRefresh();
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
            if (last.Removed.Count == 0 && last.Added.Count == 1 && last.Added[0] == original)
                _undo.Push(new StrokeChange(new StrokeCollection { smoothed }, last.Removed));
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
        ApplyChange(change, reverse: true);
        _redo.Push(change);
        MarkDirty();
        ScheduleThumbnailRefresh();
    }

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var change = _redo.Pop();
        ApplyChange(change, reverse: false);
        _undo.Push(change);
        MarkDirty();
        ScheduleThumbnailRefresh();
    }

    private void ApplyChange(StrokeChange change, bool reverse)
    {
        if (InkArea is null) return;

        var toRemove = reverse ? change.Added : change.Removed;
        var toAdd = reverse ? change.Removed : change.Added;

        _applyingHistory = true;
        try
        {
            if (toRemove.Count > 0) InkArea.Strokes.Remove(toRemove);
            if (toAdd.Count > 0) InkArea.Strokes.Add(toAdd);
        }
        finally
        {
            _applyingHistory = false;
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
        RefreshThumbnails();
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
                        Mode = page.Mode.ToString()
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
                    if (Enum.TryParse<PageTemplate>(pm.Template, out var t))
                        page.Template = t;
                    if (Enum.TryParse<CanvasMode>(pm.Mode, out var m))
                        page.Mode = m;

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
                    if (!string.IsNullOrEmpty(manifest.BackgroundColor))
                        page.BackgroundColor = ParseHexColor(manifest.BackgroundColor!, Colors.White);
                    if (Enum.TryParse<PageTemplate>(manifest.Template, out var t)) page.Template = t;
                    if (manifest.Spacing is double s && s > 0) page.Spacing = s;
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
        RefreshThumbnails();

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
