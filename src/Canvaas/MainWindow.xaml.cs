using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Canvaas;

public partial class MainWindow : Window
{
    private const int CurrentFormatVersion = 7;
    private const string FileExtension = ".canvaas";
    private const string FileFilter = "Canvaas note (*.canvaas)|*.canvaas";
    private const string ManifestEntryName = "manifest.json";
    private const string LegacyInkEntryName = "ink.isf";
    private const string PageEntryFormat = "page_{0:D3}.isf";

    private const double A4Width = 794;
    private const double A4Height = 1123;
    private const double MaxCanvasDimension = 25_000;
    private const double InfiniteSize = 30_000;

    private const double MinZoom = 0.01;
    private const double MaxZoom = 40.0;
    private const double ExportMaxDim = 3000;
    private const double ZoomStepFactor = 1.25;

    private const double EdgeBtnSize = 30;
    private const double EdgeBtnMargin = 8;
    private const double EdgeBtnPad = 4;
    private const double ResizeHandleSize = 12;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private enum ExpandDirection { Right, Left, Up, Down }
    private enum ResizeHandle { NW, N, NE, E, SE, S, SW, W }

    private static readonly (string Id, string Name, string Default)[] ShortcutActions =
    {
        ("fullscreen",   "Toggle fullscreen",   "F11"),
        ("toggle_chrome","Show / hide toolbars","Tab"),
        ("pen",          "Pen tool",            "1"),
        ("highlighter",  "Highlighter tool",    "2"),
        ("eraser",       "Eraser tool",         "3"),
        ("lasso",        "Lasso tool",          "4"),
        ("hand",         "Hand (pan)",          "5"),
        ("undo",         "Undo",                "Ctrl+Z"),
        ("redo",         "Redo",                "Ctrl+Y"),
        ("delete",       "Delete selection",    "Del"),
        ("copy",         "Copy selection",      "Ctrl+C"),
        ("cut",          "Cut selection",       "Ctrl+X"),
        ("paste",        "Paste",               "Ctrl+V"),
        ("selectall",    "Select all",          "Ctrl+A"),
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
        public string? Paper { get; set; }
        public double? WorldWidth { get; set; }
        public double? WorldHeight { get; set; }
        public double? PatternOpacity { get; set; }
        public double? PatternThickness { get; set; }
        public double? PatternDotSize { get; set; }
        public List<ElementManifest>? Elements { get; set; }
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
        public bool ShowCursorDot { get; set; } = true;
        public Dictionary<string, string>? Shortcuts { get; set; }
        public int PdfPageSizeIndex { get; set; } = 0;
        public int PdfOrientationIndex { get; set; } = 0;
        public string? DefBackground { get; set; }
        public string? DefTemplate { get; set; }
        public string? DefPaper { get; set; }
        public double? DefSpacing { get; set; }
        public double? DefPatternOpacity { get; set; }
        public double? DefPatternThickness { get; set; }
        public double? DefPatternDotSize { get; set; }
        public string[]? LightQuick { get; set; }
        public string[]? DarkQuick { get; set; }
    }

    private sealed class StrokeChange
    {
        public StrokeCollection Added { get; }
        public StrokeCollection Removed { get; }
        public StrokeChange(StrokeCollection added, StrokeCollection removed) { Added = added; Removed = removed; }
    }

    private sealed class ElementMoveChange
    {
        public UIElement Element { get; set; } = null!;
        public double OldLeft { get; set; }
        public double OldTop { get; set; }
        public double NewLeft { get; set; }
        public double NewTop { get; set; }
    }

    public sealed class ShortcutItem : INotifyPropertyChanged
    {
        public string ActionId { get; set; } = "";
        public string Name { get; set; } = "";
        private string _keyLabel = "";
        public string KeyLabel
        {
            get => _keyLabel;
            set { _keyLabel = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyLabel))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private enum PageTemplate { Blank, Ruled, Grid, Dot }
    private enum PenCursorStyle { Arrow, Cross, HollowRingDot, Hidden }
    private enum ToolMode { Pen, Highlighter, Eraser, Lasso, Hand, Calligraphy }
    private enum PaperStyle { White, Crumpled }

    private sealed class NotebookPage
    {
        public StrokeCollection Strokes { get; set; } = new StrokeCollection();
        public List<UIElement> Elements { get; set; } = new();
        public Color BackgroundColor { get; set; } = Colors.White;
        public PageTemplate Template { get; set; } = PageTemplate.Blank;
        public double Spacing { get; set; } = 40;
        public PaperStyle Paper { get; set; } = PaperStyle.White;
        public double PatternOpacity { get; set; } = 50;
        public double PatternThickness { get; set; } = 1.0;
        public double PatternDotSize { get; set; } = 1.2;
        public double WorldWidth { get; set; } = InfiniteSize;
        public double WorldHeight { get; set; } = InfiniteSize;
        public double ViewZoom { get; set; } = 1.0;
        public double ViewPanX { get; set; } = double.NaN;
        public double ViewPanY { get; set; } = double.NaN;
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
    private bool _suppressSpacingChange;
    private bool _suppressPatternChange;
    private bool _fullscreenMode;
    private bool _uiReady;

    private DateTime _lastFullscreenToggle = DateTime.MinValue;

    private WindowState _preFullscreenWindowState = WindowState.Maximized;
    private bool _preFullscreenTopmost = false;

    private ToolMode _tool = ToolMode.Pen;
    private Color _penColor = Colors.Black;
    private double _penSize = 2.5;
    private bool _pressureEnabled = true;
    private PenCursorStyle _cursorStyle = PenCursorStyle.HollowRingDot;
    private double _pointerSize = 12.0;
    private bool _showCursorDot = true;

    private Cursor? _hollowRingCursor;

    private bool _panning;
    private Point _panStart;
    private double _panStartPanX, _panStartPanY;

    private double _viewZoom = 1.0;
    private double _viewPanX = 0;
    private double _viewPanY = 0;
    private double _lastBrushZoom = -1;

    private bool _rebuildEdgeScheduled;

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
    private StrokeCollection? _clipboardStrokes;
    private int _pasteCounter = 0;

    private Image? _strokeDragImage;
    private Rect _strokeDragStartBounds;
    private double _lastDragDx, _lastDragDy;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private bool _draggingColorSquare;
    private Point _colorSquareDragStart;
    private double _colorSquareStartX, _colorSquareStartY;

    private Dictionary<string, string> _shortcuts = new();
    private readonly ObservableCollection<ShortcutItem> _shortcutItems = new();
    private bool _capturingShortcut;
    private string _capturingActionId = "";

    private readonly List<Rectangle> _resizeHandles = new();
    private bool _resizingHandle = false;
    private ResizeHandle _activeHandle;
    private Rect _selectionWorldBounds;
    private Rect _resizeStartBounds;
    private StrokeCollection? _resizeOriginalStrokes;
    private List<(UIElement El, double L, double T, double W, double H, double FS)> _resizeOriginalElements = new();

    private int _pdfPageSizeIndex = 0;
    private int _pdfOrientationIndex = 0;

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
                Color = _penColor, Width = _penSize, Height = _penSize,
                FitToCurve = false, IgnorePressure = !_pressureEnabled
            };
        }

        _uiReady = true;
        LoadSettings();
        RebuildHollowRingCursor();
        InitializeShortcuts();

        if (PointerSizeSlider is not null) PointerSizeSlider.Value = _pointerSize;
        if (PointerSizeText is not null) PointerSizeText.Text = ((int)Math.Round(_pointerSize)).ToString();
        if (ShowCursorDotCheck is not null) ShowCursorDotCheck.IsChecked = _showCursorDot;

        if (CursorCombo is not null)
            CursorCombo.SelectedIndex = _cursorStyle switch
            {
                PenCursorStyle.Arrow => 0,
                PenCursorStyle.Cross => 1,
                PenCursorStyle.HollowRingDot => 2,
                PenCursorStyle.Hidden => 3,
                _ => 2
            };

        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer.Stop();
            if (ToastBorder is not null) ToastBorder.Visibility = Visibility.Collapsed;
        };

        var dpd = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        if (StatusText is not null)
            dpd.AddValueChanged(StatusText, (s, e) =>
            {
                if (ToastBorder is null) return;
                ToastBorder.Visibility = Visibility.Visible;
                _toastTimer.Stop();
                _toastTimer.Start();
            });

        Loaded += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WindowState = WindowState.Maximized;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!CurrentPage.ViewInitialized) CenterViewOnCanvas();
                    ScheduleRebuildEdgeButtons();
                    PositionColorSquare();
                }), DispatcherPriority.Loaded);
            }), DispatcherPriority.ApplicationIdle);
        };

        StateChanged += MainWindow_StateChanged;

        if (CanvasHostBorder is not null)
            CanvasHostBorder.SizeChanged += (s, e) => ScheduleRebuildEdgeButtons();

        _pages.Add(NewStyledPage());
        _currentPageIndex = 0;

        PopulateToolColorGrid();
        WireQuickColorRightClick();
        LoadCurrentPageIntoCanvas();

        PenButton.IsChecked = true;
        InkArea.EditingMode = InkCanvasEditingMode.Ink;
        ColorWhite.IsChecked = true;
        TemplateBlank.IsChecked = true;
        PaperWhite.IsChecked = true;

        ApplyPenAttributes();
        UpdateCursor();
        TitleBarText.Text = "Canvaas";
        InitBars();
        UpdateTitle();
    }

    // =====================================================================
    // Main menu (hamburger)
    // =====================================================================
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainMenuFlyout is null) return;
        MainMenuFlyout.Visibility = MainMenuFlyout.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MenuButton2_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = b;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        MainMenuFlyout.Visibility = Visibility.Collapsed;
        OpenSettingsPanel("Settings");
    }

    private void OpenShortcuts_Click(object sender, RoutedEventArgs e)
    {
        MainMenuFlyout.Visibility = Visibility.Collapsed;
        OpenSettingsPanel("Keyboard shortcuts");
    }

    private void OpenSettingsPanel(string title)
    {
        if (SettingsOverlay is null) return;
        if (SettingsTitle is not null) SettingsTitle.Text = title;
        SyncSettingsUI();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay is not null) SettingsOverlay.Visibility = Visibility.Collapsed;
        SaveSettings();
    }

    private void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SettingsOverlay)
        {
            if (SettingsOverlay is not null) SettingsOverlay.Visibility = Visibility.Collapsed;
            SaveSettings();
        }
    }

    private void RateThisApp_Click(object sender, RoutedEventArgs e)
    {
        MainMenuFlyout.Visibility = Visibility.Collapsed;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/ujjwalsharma260-alt/canvaas",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void SendFeedback_Click(object sender, RoutedEventArgs e)
    {
        MainMenuFlyout.Visibility = Visibility.Collapsed;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "mailto:feedback@example.com?subject=Canvaas%20feedback",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private HabitTrackerWindow? _habitWin;

    private void OpenHabitTracker_Click(object sender, RoutedEventArgs e)
    {
        MainMenuFlyout.Visibility = Visibility.Collapsed;
        if (_habitWin is { IsLoaded: true }) { _habitWin.Activate(); return; }
        _habitWin = new HabitTrackerWindow(this);
        _habitWin.Closed += (s, a) => _habitWin = null;
        _habitWin.Show();
    }

    private void MoreApps_Click(object sender, RoutedEventArgs e)
    {
        MainMenuFlyout.Visibility = Visibility.Collapsed;
        StatusText.Text = "More apps coming soon.";
    }

    // =====================================================================
    // Settings sync
    // =====================================================================
    private void SyncSettingsUI()
    {
        if (PatternOpacitySlider is not null)
        {
            _suppressPatternChange = true;
            PatternOpacitySlider.Value = Math.Clamp(CurrentPage.PatternOpacity, 0, 100);
            _suppressPatternChange = false;
            if (PatternOpacityText is not null) PatternOpacityText.Text = ((int)CurrentPage.PatternOpacity).ToString();
        }
        if (PatternThicknessSlider is not null)
        {
            _suppressPatternChange = true;
            PatternThicknessSlider.Value = Math.Clamp(CurrentPage.PatternThickness, 0.5, 4);
            _suppressPatternChange = false;
            if (PatternThicknessText is not null) PatternThicknessText.Text = CurrentPage.PatternThickness.ToString("0.0");
        }
        if (PatternDotSizeSlider is not null)
        {
            _suppressPatternChange = true;
            PatternDotSizeSlider.Value = Math.Clamp(CurrentPage.PatternDotSize, 0.5, 4);
            _suppressPatternChange = false;
            if (PatternDotSizeText is not null) PatternDotSizeText.Text = CurrentPage.PatternDotSize.ToString("0.0");
        }
        if (ShowCursorDotCheck is not null) ShowCursorDotCheck.IsChecked = _showCursorDot;

        if (PdfPageSizeCombo is not null) PdfPageSizeCombo.SelectedIndex = Math.Clamp(_pdfPageSizeIndex, 0, 4);
        if (PdfOrientationCombo is not null) PdfOrientationCombo.SelectedIndex = Math.Clamp(_pdfOrientationIndex, 0, 1);
    }

    private void PatternOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressPatternChange) return;
        CurrentPage.PatternOpacity = e.NewValue;
        if (PatternOpacityText is not null) PatternOpacityText.Text = ((int)e.NewValue).ToString();
        UpdatePageBackground();
        MarkDirty();
    }

    private void PatternThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressPatternChange) return;
        CurrentPage.PatternThickness = e.NewValue;
        if (PatternThicknessText is not null) PatternThicknessText.Text = e.NewValue.ToString("0.0");
        UpdatePageBackground();
        MarkDirty();
    }

    private void PatternDotSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressPatternChange) return;
        CurrentPage.PatternDotSize = e.NewValue;
        if (PatternDotSizeText is not null) PatternDotSizeText.Text = e.NewValue.ToString("0.0");
        UpdatePageBackground();
        MarkDirty();
    }

    private void ShowCursorDotCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _showCursorDot = ShowCursorDotCheck.IsChecked == true;
        RebuildHollowRingCursor();
        UpdateCursor();
        SaveSettings();
    }

    private void ResetAllSettings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (id, _, def) in ShortcutActions) _shortcuts[id] = def;
        _showCursorDot = true;
        _pointerSize = 12;
        CurrentPage.PatternOpacity = 50;
        CurrentPage.PatternThickness = 1.0;
        CurrentPage.PatternDotSize = 1.2;
        RefreshShortcutUI();
        SyncSettingsUI();
        if (PointerSizeSlider is not null) PointerSizeSlider.Value = 12;
        UpdatePageBackground();
        RebuildHollowRingCursor();
        UpdateCursor();
        SaveSettings();
        StatusText.Text = "Settings reset to defaults.";
    }

    // =====================================================================
    // Selection action toolbar
    // =====================================================================
    private void SelToolDuplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelection();
    private void SelToolCopy_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void SelToolDelete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DuplicateSelection()
    {
        if (InkArea is null) return;
        const double offset = 30;

        var selStrokes = InkArea.GetSelectedStrokes();
        var dupedStrokes = new StrokeCollection();

        if (selStrokes.Count > 0)
        {
            foreach (var s in selStrokes)
            {
                var pts = new StylusPointCollection();
                foreach (var p in s.StylusPoints)
                    pts.Add(new StylusPoint((float)(p.X + offset), (float)(p.Y + offset), p.PressureFactor));
                dupedStrokes.Add(new Stroke(pts, s.DrawingAttributes.Clone()));
            }
            _applyingHistory = true;
            try { InkArea.Strokes.Add(dupedStrokes); }
            finally { _applyingHistory = false; }
            _undo.Push(new StrokeChange(dupedStrokes, new StrokeCollection()));
            _redo.Clear();
            InkArea.Select(dupedStrokes);
        }

        var newElements = new List<UIElement>();
        foreach (var el in _selectedElements.ToList())
        {
            if (el is not FrameworkElement fe) continue;
            if (fe is TextBox tb)
            {
                var newTb = new TextBox
                {
                    Text = tb.Text, FontSize = tb.FontSize, Width = tb.Width, Height = tb.Height,
                    Foreground = tb.Foreground, AcceptsReturn = tb.AcceptsReturn,
                    TextWrapping = tb.TextWrapping, Padding = tb.Padding,
                    Background = tb.Background, BorderBrush = tb.BorderBrush, BorderThickness = tb.BorderThickness
                };
                InkCanvas.SetLeft(newTb, InkCanvas.GetLeft(fe) + offset);
                InkCanvas.SetTop(newTb, InkCanvas.GetTop(fe) + offset);
                InkArea.Children.Add(newTb);
                CurrentPage.Elements.Add(newTb);
                newElements.Add(newTb);
            }
            else if (fe is Image img)
            {
                var newImg = new Image { Source = img.Source, Width = img.Width, Height = img.Height, Stretch = img.Stretch };
                InkCanvas.SetLeft(newImg, InkCanvas.GetLeft(fe) + offset);
                InkCanvas.SetTop(newImg, InkCanvas.GetTop(fe) + offset);
                InkArea.Children.Add(newImg);
                CurrentPage.Elements.Add(newImg);
                newElements.Add(newImg);
            }
        }

        _selectedElements.Clear();
        foreach (var ne in newElements) _selectedElements.Add(ne);

        UpdateElementSelectionVisuals();
        RebuildResizeHandles();
        MarkDirty();
        StatusText.Text = "Duplicated selection.";
    }

    // =====================================================================
    // Colour square
    // =====================================================================
    private void PositionColorSquare()
    {
        if (ColorSquareHost is null || ColorSquareRoot is null || CanvasHostBorder is null) return;
        if (!double.IsNaN(Canvas.GetLeft(ColorSquareRoot))) return;

        double vh = CanvasHostBorder.ActualHeight;
        if (vh < 100) vh = 700;

        Canvas.SetLeft(ColorSquareRoot, Math.Max(24, (CanvasHostBorder.ActualWidth > 200 ? CanvasHostBorder.ActualWidth : 1200) - 110));
        Canvas.SetTop(ColorSquareRoot, 24);
    }

    private void ColorSquareDragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ColorSquareRoot is null) return;
        _draggingColorSquare = true;
        _colorSquareDragStart = e.GetPosition(ColorSquareHost);
        _colorSquareStartX = Canvas.GetLeft(ColorSquareRoot);
        _colorSquareStartY = Canvas.GetTop(ColorSquareRoot);
        if (double.IsNaN(_colorSquareStartX)) _colorSquareStartX = 0;
        if (double.IsNaN(_colorSquareStartY)) _colorSquareStartY = 0;
        ColorSquareDragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void ColorSquareDragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingColorSquare) return;
        var p = e.GetPosition(ColorSquareHost);
        Canvas.SetLeft(ColorSquareRoot, _colorSquareStartX + (p.X - _colorSquareDragStart.X));
        Canvas.SetTop(ColorSquareRoot, _colorSquareStartY + (p.Y - _colorSquareDragStart.Y));
    }

    private void ColorSquareDragHandle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingColorSquare) return;
        _draggingColorSquare = false;
        ColorSquareDragHandle.ReleaseMouseCapture();
    }

    private void QuickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            var colour = ParseHexColor(hex, Colors.Black);
            bool hasSelection = (_selectedElements.Count > 0) ||
                (InkArea is not null && InkArea.GetSelectedStrokes().Count > 0);
            if (hasSelection) ApplyColourToSelection(colour);
            else
            {
                _penColor = colour;
                ApplyPenAttributes();
                UpdateThicknessPreview();
                StatusText.Text = $"Pen colour: {hex}";
            }
        }
    }

    // =====================================================================
    // Keyboard shortcuts
    // =====================================================================
    private void InitializeShortcuts()
    {
        _shortcuts.Clear();
        foreach (var (id, _, def) in ShortcutActions) _shortcuts[id] = def;
        if (_savedStyle?.Shortcuts is not null)
            foreach (var kv in _savedStyle.Shortcuts) _shortcuts[kv.Key] = kv.Value;
        if (ShortcutList is not null) ShortcutList.ItemsSource = _shortcutItems;
        RefreshShortcutUI();
    }

    private void RefreshShortcutUI()
    {
        _shortcutItems.Clear();
        foreach (var (id, name, _) in ShortcutActions)
        {
            string label = _shortcuts.TryGetValue(id, out var k) ? k : "";
            _shortcutItems.Add(new ShortcutItem { ActionId = id, Name = name, KeyLabel = label });
        }
    }

    private void ShortcutChange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string actionId) return;
        _capturingShortcut = true;
        _capturingActionId = actionId;
        foreach (var item in _shortcutItems)
            if (item.ActionId == actionId) { item.KeyLabel = "Press a key…"; break; }
        Focus();
    }

    private static string KeyToLabel(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        string k = key switch
        {
            Key.Delete => "Del", Key.Back => "Back", Key.Return => "Enter",
            Key.Escape => "Esc", Key.Space => "Space", Key.Tab => "Tab",
            _ => key.ToString()
        };
        parts.Add(k);
        return string.Join("+", parts);
    }

    private bool MatchesShortcut(string actionId, Key key, ModifierKeys mods)
    {
        if (!_shortcuts.TryGetValue(actionId, out var label)) return false;
        if (string.IsNullOrEmpty(label)) return false;
        var parts = label.Split('+', StringSplitOptions.RemoveEmptyEntries);
        ModifierKeys wantMods = 0;
        Key wantKey = Key.None;
        foreach (var p in parts)
        {
            switch (p.Trim().ToLowerInvariant())
            {
                case "ctrl": wantMods |= ModifierKeys.Control; break;
                case "shift": wantMods |= ModifierKeys.Shift; break;
                case "alt": wantMods |= ModifierKeys.Alt; break;
                default:
                    wantKey = p.Trim().ToLowerInvariant() switch
                    {
                        "del" or "delete" => Key.Delete,
                        "back" => Key.Back,
                        "enter" or "return" => Key.Return,
                        "esc" => Key.Escape,
                        "space" => Key.Space,
                        "tab" => Key.Tab,
                        "f11" => Key.F11,
                        _ => Enum.TryParse<Key>(p.Trim(), true, out var k) ? k : Key.None
                    };
                    break;
            }
        }
        ModifierKeys mask = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;
        return wantKey == key && (mods & mask) == wantMods;
    }

    // =====================================================================
    // Chrome toggle
    // =====================================================================
    private void ChromeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _barsPinned = !_barsPinned; _barsLeftAt = DateTime.UtcNow; ApplyBars(true);
        StatusText.Text = _barsPinned ? "Toolbars pinned. Press Tab to auto-hide." : "Toolbars auto-hide. Move the mouse to the top edge to show them.";
        bool show = true;
        TopToolbarBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // A4 sheet canvas
    // =====================================================================
    private void ApplyWorldSizeToCanvas()
    {
        if (CanvasHost is null || PageBackground is null || InkArea is null) return;
        double w = CurrentPage.WorldWidth, h = CurrentPage.WorldHeight;
        CanvasHost.Width = w; CanvasHost.Height = h;
        PageBackground.Width = w; PageBackground.Height = h;
        Canvas.SetLeft(PageBackground, 0); Canvas.SetTop(PageBackground, 0);
        InkArea.Width = w; InkArea.Height = h;
        Canvas.SetLeft(InkArea, 0); Canvas.SetTop(InkArea, 0);
        CanvasHost.UpdateLayout();
    }

    private void ExpandCanvas(ExpandDirection dir)
    {
        double addLeft = 0, addRight = 0, addTop = 0, addBottom = 0;
        switch (dir)
        {
            case ExpandDirection.Right: addRight = A4Width; break;
            case ExpandDirection.Left: addLeft = A4Width; break;
            case ExpandDirection.Up: addTop = A4Height; break;
            case ExpandDirection.Down: addBottom = A4Height; break;
        }
        double newW = CurrentPage.WorldWidth + addLeft + addRight;
        double newH = CurrentPage.WorldHeight + addTop + addBottom;

        if (newW > MaxCanvasDimension || newH > MaxCanvasDimension)
        {
            MessageBox.Show(this, "Sheet is at maximum size.", "Max size", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        double shiftX = addLeft, shiftY = addTop;
        if (shiftX > 0 || shiftY > 0) ShiftCurrentPageContent(shiftX, shiftY);

        CurrentPage.WorldWidth = newW;
        CurrentPage.WorldHeight = newH;
        _viewPanX -= shiftX * _viewZoom;
        _viewPanY -= shiftY * _viewZoom;

        ApplyWorldSizeToCanvas();
        if (CanvasHostBorder is not null) { CanvasHostBorder.InvalidateVisual(); CanvasHostBorder.UpdateLayout(); }
        if (ViewTransform is not null)
            ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        SaveCurrentViewToPage();
        ScheduleRebuildEdgeButtons();
        RebuildResizeHandles();

        int cols = (int)Math.Round(newW / A4Width);
        int rows = (int)Math.Round(newH / A4Height);
        StatusText.Text = $"Added A4 sheet {dir.ToString().ToLower()}. Sheet now {newW:0} × {newH:0} ({cols}×{rows}).";
        MarkDirty();
    }

    private void ExtendRight_Click(object sender, RoutedEventArgs e) => ExpandCanvas(ExpandDirection.Right);
    private void ExtendLeft_Click(object sender, RoutedEventArgs e) => ExpandCanvas(ExpandDirection.Left);
    private void ExtendUp_Click(object sender, RoutedEventArgs e) => ExpandCanvas(ExpandDirection.Up);
    private void ExtendDown_Click(object sender, RoutedEventArgs e) => ExpandCanvas(ExpandDirection.Down);

    private void ShiftCurrentPageContent(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.0001 && Math.Abs(dy) < 0.0001) return;
        if (InkArea is null) return;
        var strokeMap = new Dictionary<Stroke, Stroke>();
        var shifted = new StrokeCollection();
        foreach (var s in CurrentPage.Strokes)
        {
            var ns = ShiftStroke(s, dx, dy);
            strokeMap[s] = ns;
            shifted.Add(ns);
        }
        _applyingHistory = true;
        try
        {
            CurrentPage.Strokes.Clear();
            foreach (var s in shifted) CurrentPage.Strokes.Add(s);
        }
        finally { _applyingHistory = false; }

        foreach (var child in InkArea.Children)
        {
            if (child is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            if (!double.IsNaN(l)) InkCanvas.SetLeft(fe, l + dx);
            if (!double.IsNaN(t)) InkCanvas.SetTop(fe, t + dy);
        }
        RemapHistoryStack(_undo, strokeMap, dx, dy);
        RemapHistoryStack(_redo, strokeMap, dx, dy);
        ClearElementSelection();
        InkArea.Select(new StrokeCollection());
        CommandManager.InvalidateRequerySuggested();
    }

    private static Stroke ShiftStroke(Stroke s, double dx, double dy)
    {
        var pts = new StylusPointCollection();
        foreach (var p in s.StylusPoints)
            pts.Add(new StylusPoint((float)(p.X + dx), (float)(p.Y + dy), p.PressureFactor));
        return new Stroke(pts, s.DrawingAttributes.Clone());
    }

    private static void RemapHistoryStack(Stack<object> stack, Dictionary<Stroke, Stroke> map, double dx, double dy)
    {
        if (stack.Count == 0) return;
        var items = stack.ToArray();
        stack.Clear();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item is StrokeChange sc)
            {
                var na = new StrokeCollection();
                foreach (var s in sc.Added) na.Add(map.TryGetValue(s, out var ns) ? ns : s);
                var nr = new StrokeCollection();
                foreach (var s in sc.Removed) nr.Add(map.TryGetValue(s, out var ns) ? ns : s);
                stack.Push(new StrokeChange(na, nr));
            }
            else if (item is ElementMoveChange emc)
            {
                stack.Push(new ElementMoveChange
                {
                    Element = emc.Element,
                    OldLeft = emc.OldLeft + dx, OldTop = emc.OldTop + dy,
                    NewLeft = emc.NewLeft + dx, NewTop = emc.NewTop + dy
                });
            }
            else stack.Push(item);
        }
    }

    // =====================================================================
    // Edge buttons
    // =====================================================================
    private void ScheduleRebuildEdgeButtons()
    {
        if (_rebuildEdgeScheduled) return;
        _rebuildEdgeScheduled = true;
        Dispatcher.BeginInvoke(new Action(() => { _rebuildEdgeScheduled = false; RebuildEdgeButtons(); FitNavBarAboveTaskbar(); }), DispatcherPriority.Background);
    }

    private void RebuildEdgeButtons()
    {
        return;
    }

    private void RebuildEdgeButtonsOld()
    {
        if (CanvasHostBorder is null || EdgeButtonOverlay is null) return;
        double vw = CanvasHostBorder.ActualWidth, vh = CanvasHostBorder.ActualHeight;
        if (vw < 40 || vh < 40) return;

        foreach (var c in EdgeButtonOverlay.Children)
            if (c is Button oldBtn) oldBtn.Click -= EdgeButton_Click;
        EdgeButtonOverlay.Children.Clear();

        double w = CurrentPage.WorldWidth, h = CurrentPage.WorldHeight;
        int cols = Math.Max(1, (int)Math.Round(w / A4Width));
        int rows = Math.Max(1, (int)Math.Round(h / A4Height));

        double sheetL = _viewPanX, sheetT = _viewPanY;
        double sheetR = _viewPanX + w * _viewZoom;
        double sheetB = _viewPanY + h * _viewZoom;
        double colW = (sheetR - sheetL) / cols;
        double rowH = (sheetB - sheetT) / rows;

        double topY = sheetT - EdgeBtnSize - EdgeBtnMargin;
        if (topY >= EdgeBtnPad && topY + EdgeBtnSize <= vh - EdgeBtnPad)
            for (int i = 0; i < cols; i++)
            {
                double bx = sheetL + colW * (i + 0.5) - EdgeBtnSize / 2.0;
                if (bx >= EdgeBtnPad && bx + EdgeBtnSize <= vw - EdgeBtnPad)
                    AddEdgeButton(bx, topY, ExpandDirection.Up, "Add an A4 sheet above");
            }
        double botY = sheetB + EdgeBtnMargin;
        if (botY >= EdgeBtnPad && botY + EdgeBtnSize <= vh - EdgeBtnPad)
            for (int i = 0; i < cols; i++)
            {
                double bx = sheetL + colW * (i + 0.5) - EdgeBtnSize / 2.0;
                if (bx >= EdgeBtnPad && bx + EdgeBtnSize <= vw - EdgeBtnPad)
                    AddEdgeButton(bx, botY, ExpandDirection.Down, "Add an A4 sheet below");
            }
        double leftX = sheetL - EdgeBtnSize - EdgeBtnMargin;
        if (leftX >= EdgeBtnPad && leftX + EdgeBtnSize <= vw - EdgeBtnPad)
            for (int j = 0; j < rows; j++)
            {
                double by = sheetT + rowH * (j + 0.5) - EdgeBtnSize / 2.0;
                if (by >= EdgeBtnPad && by + EdgeBtnSize <= vh - EdgeBtnPad)
                    AddEdgeButton(leftX, by, ExpandDirection.Left, "Add an A4 sheet to the left");
            }
        double rightX = sheetR + EdgeBtnMargin;
        if (rightX >= EdgeBtnPad && rightX + EdgeBtnSize <= vw - EdgeBtnPad)
            for (int j = 0; j < rows; j++)
            {
                double by = sheetT + rowH * (j + 0.5) - EdgeBtnSize / 2.0;
                if (by >= EdgeBtnPad && by + EdgeBtnSize <= vh - EdgeBtnPad)
                    AddEdgeButton(rightX, by, ExpandDirection.Right, "Add an A4 sheet to the right");
            }
    }

    private void AddEdgeButton(double x, double y, ExpandDirection dir, string tooltip)
    {
        var btn = new Button
        { Style = (Style)FindResource("EdgeAddButton"), Content = "\uE710", ToolTip = tooltip, Tag = dir };
        btn.Click += EdgeButton_Click;
        Canvas.SetLeft(btn, x); Canvas.SetTop(btn, y);
        EdgeButtonOverlay.Children.Add(btn);
    }

    private void EdgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ExpandDirection dir)
            Dispatcher.BeginInvoke(new Action(() => ExpandCanvas(dir)), DispatcherPriority.Background);
    }

    // =====================================================================
    // Resize handles + selection toolbar
    // =====================================================================
    private void RebuildResizeHandles()
    {
        if (HandleOverlay is null) return;
        foreach (var h in _resizeHandles) HandleOverlay.Children.Remove(h);
        _resizeHandles.Clear();
        if (InkArea is null) { HideSelectionToolbar(); return; }

        var selStrokes = InkArea.GetSelectedStrokes();
        int total = selStrokes.Count + _selectedElements.Count;
        if (total == 0) { HideSelectionToolbar(); return; }

        Rect boundsWorld = ComputeSelectionWorldBounds();
        if (boundsWorld.IsEmpty || boundsWorld.Width < 1 || boundsWorld.Height < 1)
        { HideSelectionToolbar(); return; }
        _selectionWorldBounds = boundsWorld;

        double sl = _viewPanX + boundsWorld.Left * _viewZoom;
        double st = _viewPanY + boundsWorld.Top * _viewZoom;
        double sr = _viewPanX + boundsWorld.Right * _viewZoom;
        double sb = _viewPanY + boundsWorld.Bottom * _viewZoom;

        var outline = new Rectangle
        {
            Width = sr - sl, Height = sb - st,
            Stroke = new SolidColorBrush(Color.FromArgb(180, 26, 115, 232)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = Brushes.Transparent, IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, sl); Canvas.SetTop(outline, st);
        HandleOverlay.Children.Add(outline);
        _resizeHandles.Add(outline);

        AddResizeHandle(sl, st, ResizeHandle.NW);
        AddResizeHandle((sl + sr) / 2, st, ResizeHandle.N);
        AddResizeHandle(sr, st, ResizeHandle.NE);
        AddResizeHandle(sr, (st + sb) / 2, ResizeHandle.E);
        AddResizeHandle(sr, sb, ResizeHandle.SE);
        AddResizeHandle((sl + sr) / 2, sb, ResizeHandle.S);
        AddResizeHandle(sl, sb, ResizeHandle.SW);
        AddResizeHandle(sl, (st + sb) / 2, ResizeHandle.W);

        ShowSelectionToolbar(sl, sr, st);
    }

    private void ShowSelectionToolbar(double sl, double sr, double st)
    {
        if (SelectionToolbar is null) return;
        double centerX = (sl + sr) / 2.0;
        double tx = centerX - 62;
        double ty = st - 48;
        if (ty < 4) ty = st + 4;
        SelectionToolbar.Margin = new Thickness(tx, ty, 0, 0);
        SelectionToolbar.Visibility = Visibility.Visible;
    }

    private void HideSelectionToolbar()
    {
        if (SelectionToolbar is not null) SelectionToolbar.Visibility = Visibility.Collapsed;
    }

    private void TranslateResizeHandles(double sdx, double sdy)
    {
        foreach (var h in _resizeHandles)
        {
            double l = Canvas.GetLeft(h), t = Canvas.GetTop(h);
            if (!double.IsNaN(l)) Canvas.SetLeft(h, l + sdx);
            if (!double.IsNaN(t)) Canvas.SetTop(h, t + sdy);
        }
    }

    private void AddResizeHandle(double cx, double cy, ResizeHandle handle)
    {
        var r = new Rectangle
        {
            Width = ResizeHandleSize, Height = ResizeHandleSize,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)),
            StrokeThickness = 1.8, Tag = handle,
            Cursor = CursorForHandle(handle)
        };
        Canvas.SetLeft(r, cx - ResizeHandleSize / 2.0);
        Canvas.SetTop(r, cy - ResizeHandleSize / 2.0);
        HandleOverlay.Children.Add(r);
        _resizeHandles.Add(r);
    }

    private static Cursor CursorForHandle(ResizeHandle h) => h switch
    {
        ResizeHandle.NW or ResizeHandle.SE => Cursors.SizeNWSE,
        ResizeHandle.NE or ResizeHandle.SW => Cursors.SizeNESW,
        ResizeHandle.N or ResizeHandle.S => Cursors.SizeNS,
        ResizeHandle.E or ResizeHandle.W => Cursors.SizeWE,
        _ => Cursors.Arrow
    };

    private Rect ComputeSelectionWorldBounds()
    {
        if (InkArea is null) return Rect.Empty;
        Rect? bounds = null;
        var selStrokes = InkArea.GetSelectedStrokes();
        if (selStrokes.Count > 0)
        {
            var b = selStrokes.GetBounds();
            if (b.Width > 0 && b.Height > 0) bounds = b;
        }
        foreach (var el in _selectedElements)
        {
            if (el is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : 100;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : 30;
            if (double.IsNaN(l)) l = 0;
            if (double.IsNaN(t)) t = 0;
            var r = new Rect(l, t, w, h);
            bounds = bounds.HasValue ? Rect.Union(bounds.Value, r) : r;
        }
        return bounds ?? Rect.Empty;
    }

    private bool TryStartResize(Point screenPt)
    {
        if (_resizeHandles.Count == 0) return false;
        foreach (var h in _resizeHandles)
        {
            if (h.Tag is not ResizeHandle handle) continue;
            double l = Canvas.GetLeft(h), t = Canvas.GetTop(h);
            var rect = new Rect(l - 4, t - 4, ResizeHandleSize + 8, ResizeHandleSize + 8);
            if (rect.Contains(screenPt)) { BeginResize(handle); return true; }
        }
        return false;
    }

    private void BeginResize(ResizeHandle handle)
    {
        if (InkArea is null) return;
        HideSelectionToolbar();
        _resizingHandle = true;
        _activeHandle = handle;
        _resizeStartBounds = _selectionWorldBounds;
        _resizeOriginalStrokes = new StrokeCollection();
        foreach (var s in InkArea.GetSelectedStrokes()) _resizeOriginalStrokes.Add(s);
        _resizeOriginalElements.Clear();
        foreach (var el in _selectedElements)
        {
            if (el is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            double w = fe.ActualWidth, h = fe.ActualHeight;
            double fs = el is TextBox tb ? tb.FontSize : 0;
            _resizeOriginalElements.Add((el, l, t, w, h, fs));
        }
    }

    private void UpdateResize(Point mouseWorld)
    {
        if (InkArea is null) return;
        var start = _resizeStartBounds;
        double nL = start.Left, nT = start.Top, nR = start.Right, nB = start.Bottom;

        switch (_activeHandle)
        {
            case ResizeHandle.NW: nL = mouseWorld.X; nT = mouseWorld.Y; break;
            case ResizeHandle.N:  nT = mouseWorld.Y; break;
            case ResizeHandle.NE: nR = mouseWorld.X; nT = mouseWorld.Y; break;
            case ResizeHandle.E:  nR = mouseWorld.X; break;
            case ResizeHandle.SE: nR = mouseWorld.X; nB = mouseWorld.Y; break;
            case ResizeHandle.S:  nB = mouseWorld.Y; break;
            case ResizeHandle.SW: nL = mouseWorld.X; nB = mouseWorld.Y; break;
            case ResizeHandle.W:  nL = mouseWorld.X; break;
        }
        if (nR - nL < 10 || nB - nT < 10) return;
        double sx = (nR - nL) / Math.Max(0.0001, start.Right - start.Left);
        double sy = (nB - nT) / Math.Max(0.0001, start.Bottom - start.Top);

        if (_activeHandle is ResizeHandle.NW or ResizeHandle.NE or ResizeHandle.SE or ResizeHandle.SW)
        {
            double s = Math.Max(sx, sy);
            sx = s; sy = s;
            switch (_activeHandle)
            {
                case ResizeHandle.NW:
                    nL = start.Right - (start.Right - start.Left) * sx;
                    nT = start.Bottom - (start.Bottom - start.Top) * sy;
                    nR = start.Right; nB = start.Bottom; break;
                case ResizeHandle.NE:
                    nR = start.Left + (start.Right - start.Left) * sx;
                    nT = start.Bottom - (start.Bottom - start.Top) * sy;
                    nL = start.Left; nB = start.Bottom; break;
                case ResizeHandle.SE:
                    nR = start.Left + (start.Right - start.Left) * sx;
                    nB = start.Top + (start.Bottom - start.Top) * sy;
                    nL = start.Left; nT = start.Top; break;
                case ResizeHandle.SW:
                    nL = start.Right - (start.Right - start.Left) * sx;
                    nB = start.Top + (start.Bottom - start.Top) * sy;
                    nR = start.Right; nT = start.Top; break;
            }
        }

        _applyingHistory = true;
        try
        {
            if (_resizeOriginalStrokes is not null && _resizeOriginalStrokes.Count > 0)
            {
                var toRemove = new StrokeCollection();
                foreach (var s in InkArea.GetSelectedStrokes()) toRemove.Add(s);
                InkArea.Strokes.Remove(toRemove);
                var newStrokes = new StrokeCollection();
                foreach (var orig in _resizeOriginalStrokes)
                {
                    var newPts = new StylusPointCollection();
                    foreach (var p in orig.StylusPoints)
                    {
                        float nx = (float)(nL + (p.X - start.Left) * sx);
                        float ny = (float)(nT + (p.Y - start.Top) * sy);
                        newPts.Add(new StylusPoint(nx, ny, p.PressureFactor));
                    }
                    newStrokes.Add(new Stroke(newPts, orig.DrawingAttributes.Clone()));
                }
                InkArea.Strokes.Add(newStrokes);
                InkArea.Select(newStrokes);
            }
            foreach (var (el, ol, ot, ow, oh, ofs) in _resizeOriginalElements)
            {
                if (el is not FrameworkElement fe) continue;
                InkCanvas.SetLeft(fe, nL + (ol - start.Left) * sx);
                InkCanvas.SetTop(fe, nT + (ot - start.Top) * sy);
                fe.Width = Math.Max(20, ow * sx);
                fe.Height = Math.Max(20, oh * sy);
                if (el is TextBox tb && ofs > 0)
                    tb.FontSize = Math.Max(6, ofs * (sx + sy) / 2.0);
            }
        }
        finally { _applyingHistory = false; }

        _selectionWorldBounds = new Rect(nL, nT, nR - nL, nB - nT);
        RebuildResizeHandles();
        UpdateElementSelectionVisuals();
    }

    private void EndResize()
    {
        if (!_resizingHandle) return;
        _resizingHandle = false;
        if (_resizeOriginalStrokes is not null && InkArea is not null)
        {
            var current = new StrokeCollection();
            foreach (var s in InkArea.GetSelectedStrokes()) current.Add(s);
            if (current.Count > 0 && _resizeOriginalStrokes.Count > 0)
            {
                _undo.Push(new StrokeChange(current, _resizeOriginalStrokes));
                _redo.Clear();
            }
        }
        _resizeOriginalStrokes = null;
        _resizeOriginalElements.Clear();
        RebuildResizeHandles();
        MarkDirty();
        StatusText.Text = "Resized selection.";
    }

    // =====================================================================
    // Settings persistence
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
            _showCursorDot = s.ShowCursorDot;
            _pdfPageSizeIndex = Math.Clamp(s.PdfPageSizeIndex, 0, 4);
            _pdfOrientationIndex = Math.Clamp(s.PdfOrientationIndex, 0, 1);
            if (s.Shortcuts is not null) _shortcuts = new Dictionary<string, string>(s.Shortcuts);
            _savedStyle = s;
            if (s.LightQuick is { Length: 4 }) _lightQuick = s.LightQuick;
            if (s.DarkQuick is { Length: 4 }) _darkQuick = s.DarkQuick;
        }
        catch { }
    }

    private void SaveSettings()
    {
        if (!_uiReady) return;
        SaveSettingsCore();
    }

    private void SaveSettingsCore()
    {
        try
        {
            var path = SettingsFilePath;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var s = new AppSettings
            {
                PointerSize = _pointerSize,
                CursorStyleIndex = (int)_cursorStyle,
                ShowCursorDot = _showCursorDot,
                Shortcuts = _shortcuts,
                PdfPageSizeIndex = PdfPageSizeCombo?.SelectedIndex ?? _pdfPageSizeIndex,
                PdfOrientationIndex = PdfOrientationCombo?.SelectedIndex ?? _pdfOrientationIndex
            };
            CaptureStyle(s);
            File.WriteAllText(path, JsonSerializer.Serialize(s, JsonOptions));
        }
        catch { }
    }

    // =====================================================================
    // Cursor (adaptive colour + optional dot)
    // =====================================================================
    private void RebuildHollowRingCursor()
    {
        try
        {
            bool darkBg = IsDarkColor(CurrentPage.BackgroundColor);
            Brush ringBrush = darkBg ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
            Brush dotBrush = darkBg ? Brushes.White : Brushes.Black;

            double ringRadius = _pointerSize / 2.0;
            double dotRadius = Math.Max(1.0, ringRadius * 0.17);
            int size = (int)Math.Ceiling(_pointerSize + 8);
            if (size < 20) size = 20;
            if (size > 60) size = 60;
            double center = size / 2.0;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var ringPen = new Pen(ringBrush, 1.4);
                dc.DrawEllipse(null, ringPen, new Point(center, center), ringRadius, ringRadius);
                if (_showCursorDot)
                    dc.DrawEllipse(dotBrush, null, new Point(center, center), dotRadius, dotRadius);
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            _hollowRingCursor = CreateCursorFromBitmap(rtb, size / 2, size / 2);
        }
        catch { _hollowRingCursor = Cursors.Cross; }
    }

    private static bool IsDarkColor(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum < 0.5;
    }

    private static Cursor CreateCursorFromBitmap(BitmapSource bmp, int hotX, int hotY)
    {
        int width = bmp.PixelWidth, height = bmp.PixelHeight;
        int stride = ((width * 32 + 31) / 32) * 4;
        int imageSize = stride * height;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((short)0); bw.Write((short)2); bw.Write((short)1);
        bw.Write((byte)width); bw.Write((byte)height);
        bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((short)hotX); bw.Write((short)hotY);
        bw.Write(imageSize + 40); bw.Write(22);
        bw.Write(40); bw.Write(width); bw.Write(height * 2);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(imageSize); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        var pixels = new byte[imageSize];
        bmp.CopyPixels(pixels, stride, 0);
        for (int y = height - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
        ms.Position = 0;
        return new Cursor(ms);
    }

    private void PointerSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _pointerSize = e.NewValue;
        if (PointerSizeText is not null) PointerSizeText.Text = ((int)Math.Round(_pointerSize)).ToString();
        RebuildHollowRingCursor();
        UpdateCursor();
        SaveSettings();
    }

    private void InkArea_StylusOutOfRange(object sender, StylusEventArgs e) => ReleaseAllCaptures();

    private void ReleaseAllCaptures()
    {
        if (_panning) EndPan();
        try { if (Mouse.Captured is not null && Mouse.Captured != this) Mouse.Capture(null); } catch { }
        try { Stylus.Capture(null); } catch { }
    }

    // =====================================================================
    // Colour palette
    // =====================================================================
    private void PopulateToolColorGrid()
    {
        if (ToolColorGrid is null) return;
        var items = new List<PopupColorItem>
        {
            MakeColor("#111111", "Black"), MakeColor("#555555", "Dark grey"),
            MakeColor("#999999", "Grey"), MakeColor("#CCCCCC", "Light grey"),
            MakeColor("#FFFFFF", "White"), MakeColor("#FAF6E3", "Cream"),
            MakeColor("#C62828", "Red"), MakeColor("#EF6C00", "Orange"),
            MakeColor("#FBC02D", "Yellow"), MakeColor("#B8860B", "Gold"),
            MakeColor("#A1887F", "Tan"), MakeColor("#4E342E", "Brown"),
            MakeColor("#00ACC1", "Cyan"), MakeColor("#4FC3F7", "Sky"),
            MakeColor("#1565C0", "Blue"), MakeColor("#1A237E", "Navy"),
            MakeColor("#6A1B9A", "Purple"), MakeColor("#AD1457", "Magenta"),
            MakeColor("#AED581", "Light green"), MakeColor("#43A047", "Green"),
            MakeColor("#1B5E20", "Dark green"), MakeColor("#827717", "Olive"),
            MakeColor("#F06292", "Pink"), MakeColor("#7B1FA2", "Violet"),
        };
        ToolColorGrid.ItemsSource = items;
    }

    private static PopupColorItem MakeColor(string hex, string name)
    {
        var c = ParseHexColor(hex, Colors.Black);
        return new PopupColorItem { Brush = new SolidColorBrush(c), Hex = hex, Name = name };
    }

    private void ShowToolPopup(UIElement target, string title)
    {
        if (ToolOptionsPopup is null) return;
        ToolOptionsPopup.IsOpen = false;
        if (ToolPopupTitle is not null) ToolPopupTitle.Text = title;
        ToolOptionsPopup.PlacementTarget = target;
        UpdateThicknessPreview();
        if (ToolSizeSlider is not null) ToolSizeSlider.Value = _penSize;
        if (ToolPressureToggle is not null) ToolPressureToggle.IsChecked = _pressureEnabled;
        ToolOptionsPopup.IsOpen = true;
    }

    private void UpdateThicknessPreview()
    {
        if (ToolThicknessPreview is null) return;
        double actual = _tool == ToolMode.Highlighter ? Math.Max(14, _penSize * 6) : _penSize;
        Color c = _tool == ToolMode.Highlighter
            ? Color.FromArgb(180, _penColor.R, _penColor.G, _penColor.B) : _penColor;
        ToolThicknessPreview.Stroke = new SolidColorBrush(c);
        ToolThicknessPreview.StrokeThickness = actual;
    }

    private void ToolColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            var colour = ParseHexColor(hex, Colors.Black);
            bool hasSelection = (_selectedElements.Count > 0) ||
                (InkArea is not null && InkArea.GetSelectedStrokes().Count > 0);
            if (hasSelection) ApplyColourToSelection(colour);
            else { _penColor = colour; ApplyPenAttributes(); UpdateThicknessPreview(); }
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
            StrokeCollection added, removed;
            try
            {
                removed = new StrokeCollection();
                added = new StrokeCollection();
                foreach (var s in selectedStrokes.ToList())
                {
                    var da = s.DrawingAttributes.Clone();
                    da.Color = newColour;
                    var ns = new Stroke(s.StylusPoints, da);
                    removed.Add(s); added.Add(ns);
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
            if (el is TextBox tb) { tb.Foreground = new SolidColorBrush(newColour); any = true; }
        if (any) { MarkDirty(); StatusText.Text = "Recoloured selection."; }
        else { _penColor = newColour; ApplyPenAttributes(); UpdateThicknessPreview(); }
    }

    private void UpdateCursor()
    {
        Cursor c;
        if (_tool == ToolMode.Hand) c = Cursors.SizeAll;
        else if (_tool == ToolMode.Lasso) c = Cursors.Cross;
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

    private void HighlightToolbarColor()
    {
        foreach (var b in new[] { QuickColor1, QuickColor2, QuickColor3, QuickColor4 })
        {
            if (b is null) continue;
            bool active = b.Tag is string hex && ParseHexColor(hex, Colors.Black) == _penColor;
            b.BorderThickness = new Thickness(active ? 2.5 : 1);
            b.BorderBrush = new SolidColorBrush(active
                ? Color.FromRgb(0x1A, 0x73, 0xE8)
                : Color.FromRgb(0xBB, 0xBB, 0xBB));
        }
    }

    private void ApplyPenAttributes()
    {
        HighlightToolbarColor();
        ApplyPenAttributesCore();
    }

    private void ApplyPenAttributesCore()
    {
        if (!_uiReady || InkArea is null) return;
        if (_tool == ToolMode.Lasso) return;
        var da = InkArea.DefaultDrawingAttributes;
        if (da is null) return;
        da.StylusTip = StylusTip.Ellipse; da.StylusTipTransform = Matrix.Identity;
        if (_tool == ToolMode.Highlighter)
        {
            da.Color = Color.FromArgb(110, _penColor.R, _penColor.G, _penColor.B);
            double hs = Math.Max(14, _penSize * 6);
            da.Width = hs; da.Height = hs;
            da.IsHighlighter = true; da.FitToCurve = false; da.IgnorePressure = true;
        }
        else
        {
            da.Color = _penColor; da.Width = _penSize; da.Height = _penSize;
            da.IsHighlighter = false; da.FitToCurve = false; da.IgnorePressure = !_pressureEnabled;
            if (_tool == ToolMode.Calligraphy)
            {
                da.StylusTip = StylusTip.Rectangle;
                da.Width = Math.Max(3, _penSize * 2.6);
                da.Height = Math.Max(1, _penSize * 0.7);
                var nib = Matrix.Identity; nib.Rotate(-40);
                da.StylusTipTransform = nib;
            }
        }
    }

    // =====================================================================
    // Keyboard handling
    // =====================================================================
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ESC always exits fullscreen first
        if (_fullscreenMode && e.Key == Key.Escape)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (_capturingShortcut)
        {
            if (e.Key == Key.Escape)
            {
                _capturingShortcut = false;
                RefreshShortcutUI();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin)
            { e.Handled = true; return; }

            string label = KeyToLabel(e.Key, Keyboard.Modifiers);
            _shortcuts[_capturingActionId] = label;
            _capturingShortcut = false;
            RefreshShortcutUI();
            SaveSettings();
            StatusText.Text = $"Shortcut changed to {label}.";
            e.Handled = true;
            return;
        }

        if (SettingsOverlay is not null && SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                SaveSettings();
                e.Handled = true;
            }
            return;
        }

        if (MainMenuFlyout is not null && MainMenuFlyout.Visibility == Visibility.Visible
            && e.Key == Key.Escape)
        {
            MainMenuFlyout.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        var modsNow = Keyboard.Modifiers;
        bool textBoxFocused = Keyboard.FocusedElement is TextBox;

        if (MatchesShortcut("fullscreen", e.Key, modsNow)) { ToggleFullscreen(); e.Handled = true; return; }
        if (MatchesShortcut("toggle_chrome", e.Key, modsNow)) { ChromeToggleButton_Click(this, new RoutedEventArgs()); e.Handled = true; return; }

        if (!textBoxFocused)
        {
            if (MatchesShortcut("delete", e.Key, modsNow) && DeleteSelection()) { e.Handled = true; return; }
            if (MatchesShortcut("copy", e.Key, modsNow) && CopySelection()) { e.Handled = true; return; }
            if (MatchesShortcut("cut", e.Key, modsNow) && CutSelection()) { e.Handled = true; return; }
            if (MatchesShortcut("paste", e.Key, modsNow)) { PasteAnything(); e.Handled = true; return; }
            if (MatchesShortcut("selectall", e.Key, modsNow)) { ApplicationCommands.SelectAll.Execute(null, this); e.Handled = true; return; }
            if (MatchesShortcut("undo", e.Key, modsNow)) { ApplicationCommands.Undo.Execute(null, this); e.Handled = true; return; }
            if (MatchesShortcut("redo", e.Key, modsNow)) { ApplicationCommands.Redo.Execute(null, this); e.Handled = true; return; }
            if (MatchesShortcut("pen", e.Key, modsNow)) { PenButton.IsChecked = true; PenButton_Click(PenButton, new RoutedEventArgs()); e.Handled = true; return; }
            if (MatchesShortcut("highlighter", e.Key, modsNow)) { HighlighterButton.IsChecked = true; HighlighterButton_Click(HighlighterButton, new RoutedEventArgs()); e.Handled = true; return; }
            if (MatchesShortcut("eraser", e.Key, modsNow)) { EraserButton.IsChecked = true; EraserButton_Click(EraserButton, new RoutedEventArgs()); e.Handled = true; return; }
            if (MatchesShortcut("lasso", e.Key, modsNow)) { LassoButton.IsChecked = true; LassoButton_Click(LassoButton, new RoutedEventArgs()); e.Handled = true; return; }
            if (MatchesShortcut("hand", e.Key, modsNow)) { HandButton.IsChecked = true; HandButton_Click(HandButton, new RoutedEventArgs()); e.Handled = true; return; }
        }

        if (!textBoxFocused && e.Key == Key.Escape)
        {
            if (_selectedElements.Count > 0 || (InkArea is not null && InkArea.GetSelectedStrokes().Count > 0))
            {
                ClearElementSelection();
                InkArea?.Select(new StrokeCollection());
                RebuildResizeHandles();
                e.Handled = true;
                return;
            }
        }

        if (textBoxFocused) return;
        if (e.Key == Key.Left) { PrevPageButton_Click(PrevPageButton, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Right && _currentPageIndex < _pages.Count - 1) { GoToNextExistingPage(); e.Handled = true; }
    }

    // =====================================================================
    // Ctrl+V universal paste
    // =====================================================================
    private void PasteAnything()
    {
        if (InkArea is null) return;

        try
        {
            if (Clipboard.ContainsImage())
            {
                var src = Clipboard.GetImage();
                if (src is not null)
                {
                    if (src.CanFreeze) src.Freeze();
                    AddImageToPage(src);
                    return;
                }
            }
        }
        catch { }

        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    PasteTextAsKeywordBoxes(text);
                    return;
                }
            }
        }
        catch { }

        PasteClipboard();
    }

    private void PasteTextAsKeywordBoxes(string text)
    {
        if (InkArea is null) return;
        var tokens = new List<string>();
        foreach (var raw in SplitKeywords(text))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0) tokens.Add(trimmed);
        }
        if (tokens.Count == 0) return;
        var fontName = AskPasteFont();
        if (fontName is null) return;

        const double boxW = 220, boxH = 48, gap = 20, margin = 60;
        int perRow = 6;
        int numRows = (tokens.Count + perRow - 1) / perRow;
        double neededW = margin + perRow * (boxW + gap) + margin;
        double neededH = margin + numRows * (boxH + gap) + margin;

        if (neededW > CurrentPage.WorldWidth || neededH > CurrentPage.WorldHeight)
        {
            CurrentPage.WorldWidth = Math.Max(CurrentPage.WorldWidth, neededW);
            CurrentPage.WorldHeight = Math.Max(CurrentPage.WorldHeight, neededH);
            ApplyWorldSizeToCanvas();
        }

        double visLeft = -_viewPanX / _viewZoom;
        double visTop = -_viewPanY / _viewZoom;
        double rowX = 0; int row = 0;
        double wrapW = Math.Max(400, ((CanvasHostBorder?.ActualWidth ?? 1200) - 160) / Math.Max(_viewZoom, 0.05));
        for (int i = 0; i < tokens.Count; i++)
        {
            var tb = new TextBox
            {
                Text = tokens[i], AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap,
                FontSize = 24, Width = MeasureKeywordWidth(tokens[i], fontName, 24), MinHeight = 36,
                Padding = new Thickness(8, 4, 8, 4),
                Background = Brushes.Transparent, FontFamily = new FontFamily(fontName), Foreground = new SolidColorBrush(_penColor),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (rowX > 0 && rowX + tb.Width > wrapW) { rowX = 0; row++; }
            double colX = rowX; rowX += tb.Width + gap;
            InkCanvas.SetLeft(tb, visLeft + margin + colX);
            InkCanvas.SetTop(tb, visTop + margin + row * (boxH + gap));
            InkArea.Children.Add(tb);
            CurrentPage.Elements.Add(tb);
        }
        CenterViewOnCanvas();
        MarkDirty();
        StatusText.Text = $"Pasted {tokens.Count} item(s) as individual boxes.";
    }

    // =====================================================================
    // Fullscreen (debounced + fully chrome-free)
    // =====================================================================
    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFullscreenToggle).TotalMilliseconds < 400) return;
        _lastFullscreenToggle = now;

        _fullscreenMode = !_fullscreenMode;
        if (TitleBarBorder is not null)
            ApplyBars(_barsShown);
        if (AppWindowChrome is not null)
        {
            AppWindowChrome.CaptionHeight = _fullscreenMode ? 0 : 36;
            AppWindowChrome.ResizeBorderThickness = _fullscreenMode ? new Thickness(0) : new Thickness(6);
        }
        ReleaseAllCaptures();

        IntPtr hwnd;
        try { hwnd = new WindowInteropHelper(this).Handle; } catch { hwnd = IntPtr.Zero; }

        if (_fullscreenMode)
        {
            _preFullscreenWindowState = WindowState;
            _preFullscreenTopmost = Topmost;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            Left = 0; Top = 0; Width = sw; Height = sh;
            Topmost = true;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, (int)sw, (int)sh, SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                // Call again to force Windows to fully re-evaluate the frame
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, (int)sw, (int)sh, SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            }
            StatusText.Text = "Fullscreen. Press ESC or F11 to exit.";
        }
        else
        {
            Topmost = _preFullscreenTopmost;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullscreenWindowState;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            StatusText.Text = "Exited fullscreen.";
        }
        Dispatcher.BeginInvoke(new Action(ScheduleRebuildEdgeButtons), DispatcherPriority.Loaded);
    }

    // =====================================================================
    // View
    // =====================================================================
    private void ApplyView()
    {
        if (ViewTransform is null) return;
        ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        if (ZoomText is not null)
            ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";

        if (Math.Abs(_lastBrushZoom - _viewZoom) > 0.001)
        {
            _lastBrushZoom = _viewZoom;
            if (CurrentPage.Template != PageTemplate.Blank)
                UpdatePageBackground();
        }

        SaveCurrentViewToPage();
        ScheduleRebuildEdgeButtons();
        RebuildResizeHandles();
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

    private void PanBy(double dxScreen, double dyScreen) { _viewPanX += dxScreen; _viewPanY += dyScreen; ApplyView(); }

    private void ZoomAtViewCenter(double factor)
    {
        if (CanvasHostBorder is null) return;
        double vw = CanvasHostBorder.ActualWidth, vh = CanvasHostBorder.ActualHeight;
        if (vw < 10 || vh < 10) return;
        ZoomAt(new Point(vw / 2.0, vh / 2.0), factor);
    }

    private void CenterViewOnCanvas()
    {
        if (CanvasHostBorder is null) return;
        double vw = CanvasHostBorder.ActualWidth, vh = CanvasHostBorder.ActualHeight;
        if (vw < 10 || vh < 10) { vw = 1200; vh = 700; }
        double fitX = (vw - 80) / CurrentPage.WorldWidth;
        double fitY = (vh - 80) / CurrentPage.WorldHeight;
        _viewZoom = (CurrentPage.WorldWidth > 5000 || CurrentPage.WorldHeight > 5000) ? 1.0 : Math.Clamp(Math.Min(fitX, fitY), MinZoom, 1.0);
        _viewPanX = (vw - CurrentPage.WorldWidth * _viewZoom) / 2.0;
        _viewPanY = (vh - CurrentPage.WorldHeight * _viewZoom) / 2.0;
        if (ViewTransform is not null)
            ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        if (ZoomText is not null)
            ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";
        _lastBrushZoom = _viewZoom;
        ScheduleRebuildEdgeButtons();
        RebuildResizeHandles();
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
        if (double.IsNaN(CurrentPage.ViewPanX) || double.IsNaN(CurrentPage.ViewPanY)) { CenterViewOnCanvas(); return; }
        _viewZoom = CurrentPage.ViewZoom;
        _viewPanX = CurrentPage.ViewPanX;
        _viewPanY = CurrentPage.ViewPanY;
        if (ViewTransform is not null)
            ViewTransform.Matrix = new Matrix(_viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);
        if (ZoomText is not null)
            ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";
        _lastBrushZoom = _viewZoom;
        ScheduleRebuildEdgeButtons();
        RebuildResizeHandles();
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
            ZoomAtViewCenter(target / _viewZoom);
        }
        ZoomText.Text = $"{(int)Math.Round(_viewZoom * 100)}%";
    }

    private void CanvasHostBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CanvasHostBorder is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            ZoomAt(e.GetPosition(CanvasHostBorder), factor);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        { PanBy(-e.Delta, 0); e.Handled = true; }
        else { PanBy(0, -e.Delta); e.Handled = true; }
    }

    // =====================================================================
    // Page management
    // =====================================================================
    private void LoadCurrentPageIntoCanvas()
    {
        if (InkArea is null) return;

        if (_pages.Count > 0 && _currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
        {
            var oldPage = _pages[_currentPageIndex];
            oldPage.Elements.Clear();
            foreach (var c in InkArea.Children)
                if (c is UIElement uie) oldPage.Elements.Add(uie);
        }

        InkArea.Strokes.StrokesChanged -= Strokes_Changed;
        InkArea.Strokes = CurrentPage.Strokes;
        InkArea.Strokes.StrokesChanged += Strokes_Changed;

        InkArea.Children.Clear();
        foreach (var el in CurrentPage.Elements) InkArea.Children.Add(el);

        ApplyWorldSizeToCanvas();
        _undo.Clear();
        _redo.Clear();
        CommandManager.InvalidateRequerySuggested();
        ClearElementSelection();
        SyncUIWithSettings();
        UpdatePageNavigationUI();

        if (CurrentPage.ViewInitialized) RestoreViewFromPage();
        else CenterViewOnCanvas();

        RebuildHollowRingCursor();
        UpdateCursor();

        ScheduleRebuildEdgeButtons();
        RebuildResizeHandles();
    }

    private void UpdatePageNavigationUI()
    {
        if (PageCounterBox is null) return;
        _suppressCounterChange = true;
        PageCounterBox.Text = $"{_currentPageIndex + 1} / {_pages.Count}";
        _suppressCounterChange = false;
        if (PrevPageButton is not null) PrevPageButton.IsEnabled = _currentPageIndex > 0;
        if (NextPageButton is not null) NextPageButton.IsEnabled = true;
        if (PrevPageButton is not null) PrevPageButton.Opacity = PrevPageButton.IsEnabled ? 1.0 : 0.4;
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
            int idx = Math.Clamp(n - 1, 0, _pages.Count - 1);
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
        if (_currentPageIndex >= _pages.Count - 1) { NewNotebookPage_Click(sender, e); return; }
        GoToNextExistingPage();
    }

    private void GoToNextExistingPage()
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
        if (sender is Button b && b.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = b;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void NewNotebookPage_Click(object sender, RoutedEventArgs e)
    {
        var newPage = new NotebookPage
        {
            BackgroundColor = CurrentPage.BackgroundColor,
            Template = CurrentPage.Template,
            Spacing = CurrentPage.Spacing,
            Paper = CurrentPage.Paper,
            PatternOpacity = CurrentPage.PatternOpacity,
            PatternThickness = CurrentPage.PatternThickness,
            PatternDotSize = CurrentPage.PatternDotSize
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
            MessageBox.Show(this, "You cannot delete the only page.", "Cannot delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Delete page {_currentPageIndex + 1} of {_pages.Count}?",
            "Delete page", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _pages.RemoveAt(_currentPageIndex);
        if (_currentPageIndex >= _pages.Count) _currentPageIndex = _pages.Count - 1;
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
        { Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true, FileName = "Page " + (_currentPageIndex + 1) };
        if (dlg.ShowDialog(this) != true) return;
        try { ExportPageToPng(CurrentPage, dlg.FileName); StatusText.Text = "Exported: " + dlg.FileName; }
        catch (Exception ex) { MessageBox.Show(this, "Could not export.\n\n" + ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportAllPagesPng_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        { Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true, FileName = "Page.png" };
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
            StatusText.Text = $"Exported {_pages.Count} page(s).";
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not export.\n\n" + ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportPageToPngOld(NotebookPage page, string path)
    {
        Rect world = page.Strokes.Count > 0 ? page.Strokes.GetBounds() : new Rect(0, 0, 800, 1120);
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
            foreach (var s in page.Strokes) s.Draw(dc);
            dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // =====================================================================
    // PDF export
    // =====================================================================
    private void ExportAsPdfOld_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var printDialog = new PrintDialog();
            if (printDialog.ShowDialog() != true) return;

            double pw = printDialog.PrintableAreaWidth;
            double ph = printDialog.PrintableAreaHeight;

            var document = new FixedDocument();
            foreach (var page in _pages)
            {
                var fixedPage = new FixedPage { Width = pw, Height = ph, Background = Brushes.White };
                var img = new Image
                {
                    Source = RenderPageForPrint(page, (int)pw, (int)ph),
                    Width = pw, Height = ph, Stretch = Stretch.Uniform
                };
                FixedPage.SetLeft(img, 0);
                FixedPage.SetTop(img, 0);
                fixedPage.Children.Add(img);

                var pc = new PageContent();
                ((IAddChild)pc).AddChild(fixedPage);
                document.Pages.Add(pc);
            }
            printDialog.PrintDocument(document.DocumentPaginator, "Canvaas note");
            StatusText.Text = "Printed. Choose 'Microsoft Print to PDF' to save as PDF.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "PDF export failed.\n\n" + ex.Message,
                "Export as PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static BitmapSource RenderPageForPrint(NotebookPage page, int targetWidth, int targetHeight)
    {
        Rect world;
        if (page.Strokes.Count > 0) world = page.Strokes.GetBounds();
        else world = new Rect(0, 0, A4Width, A4Height);
        world.Inflate(40, 40);

        double scale = Math.Min(targetWidth / world.Width, targetHeight / world.Height);
        int pxW = (int)Math.Round(world.Width * scale);
        int pxH = (int)Math.Round(world.Height * scale);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, targetWidth, targetHeight));
            double ox = (targetWidth - pxW) / 2.0;
            double oy = (targetHeight - pxH) / 2.0;
            dc.PushTransform(new TranslateTransform(ox, oy));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-world.X, -world.Y));
            dc.DrawRectangle(new SolidColorBrush(page.BackgroundColor), null, world);
            foreach (var s in page.Strokes) s.Draw(dc);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // =====================================================================
    // PDF import (Windows.Data.Pdf reflection)
    // =====================================================================
    private void ImportPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Insert PDF" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var pages = RenderPdfToBitmaps(dlg.FileName);
            if (pages == null || pages.Count == 0)
            {
                MessageBox.Show(this,
                    "PDF rendering is not available on this system.\n\n" +
                    "You need Windows 10 or later with the Windows Runtime PDF APIs.\n\n" +
                    "Alternative: open the PDF in a viewer, screenshot each page, and use Paste from Clipboard.",
                    "PDF import not available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int originalIndex = _currentPageIndex;
            for (int i = 0; i < pages.Count; i++)
            {
                var newPage = new NotebookPage
                {
                    BackgroundColor = Colors.White,
                    Template = PageTemplate.Blank,
                    Paper = PaperStyle.White,
                    WorldWidth = A4Width,
                    WorldHeight = A4Height
                };

                var img = new Image
                {
                    Source = pages[i],
                    Width = A4Width,
                    Height = A4Height,
                    Stretch = Stretch.Uniform
                };
                InkCanvas.SetLeft(img, 0);
                InkCanvas.SetTop(img, 0);
                newPage.Elements.Add(img);

                _pages.Insert(originalIndex + 1 + i, newPage);
            }

            _currentPageIndex = originalIndex + 1;
            LoadCurrentPageIntoCanvas();
            MarkDirty();
            StatusText.Text = $"Inserted {pages.Count} PDF page(s) as new notebook pages. Use ← / → to navigate.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "PDF import failed.\n\n" + ex.Message,
                "PDF import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<BitmapSource>? RenderPdfToBitmaps(string path)
    {
        try
        {
            return System.Threading.Tasks.Task.Run(() => RenderPdfCoreAsync(path)).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private static async System.Threading.Tasks.Task<List<BitmapSource>?> RenderPdfCoreAsync(string path)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var doc = await global::Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        var result = new List<BitmapSource>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            var options = new global::Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1600 };
            await page.RenderToStreamAsync(stream, options);
            using var input = stream.GetInputStreamAt(0);
            using var reader = new global::Windows.Storage.Streams.DataReader(input);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            result.Add(bmp);
        }
        return result;
    }

    private static List<BitmapSource>? RenderPdfToBitmapsOld(string path)
    {
        try
        {
            var pdfDocType = Type.GetType("Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime");
            var storageFileType = Type.GetType("Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime");
            if (pdfDocType is null || storageFileType is null) return null;

            var getFileMethod = storageFileType.GetMethod("GetFileFromPathAsync",
                BindingFlags.Public | BindingFlags.Static);
            if (getFileMethod is null) return null;
            var op = getFileMethod.Invoke(null, new object[] { path });
            if (op is null) return null;

            var storageFile = AwaitWinRtOp(op);
            if (storageFile is null) return null;

            var loadMethod = pdfDocType.GetMethod("LoadFromFileAsync",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { storageFileType }, null);
            if (loadMethod is null) return null;
            var loadOp = loadMethod.Invoke(null, new[] { storageFile });
            if (loadOp is null) return null;

            var pdfDoc = AwaitWinRtOp(loadOp);
            if (pdfDoc is null) return null;

            int count = (int)(pdfDocType.GetProperty("PageCount")?.GetValue(pdfDoc) ?? 0);
            if (count <= 0) return null;

            var pages = new List<BitmapSource>();
            var getPageMethod = pdfDocType.GetMethod("GetPage");

            for (uint i = 0; i < count; i++)
            {
                var page = getPageMethod?.Invoke(pdfDoc, new object[] { i });
                if (page is null) continue;

                var bmp = RenderPdfPageToBitmap(page);
                if (bmp is not null) pages.Add(bmp);

                (page as IDisposable)?.Dispose();
            }

            (pdfDoc as IDisposable)?.Dispose();
            return pages;
        }
        catch { return null; }
    }

    private static object? AwaitWinRtOp(object op)
    {
        try
        {
            var asTaskMethod = op.GetType().GetMethod("AsTask",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (asTaskMethod is not null)
            {
                var task = asTaskMethod.Invoke(op, null) as System.Threading.Tasks.Task;
                if (task is not null)
                {
                    task.Wait(5000);
                    var resultProp = task.GetType().GetProperty("Result");
                    return resultProp?.GetValue(task);
                }
            }
        }
        catch { }
        return null;
    }

    private static BitmapSource? RenderPdfPageToBitmap(object pdfPage)
    {
        try
        {
            var streamType = Type.GetType("Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime");
            if (streamType is null) return null;

            var stream = Activator.CreateInstance(streamType);
            if (stream is null) return null;

            var renderMethod = pdfPage.GetType().GetMethod("RenderToStreamAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { streamType }, null);
            if (renderMethod is null) return null;

            var op = renderMethod.Invoke(pdfPage, new[] { stream });
            if (op is null) return null;
            AwaitWinRtOp(op);

            var getInputStreamMethod = streamType.GetMethod("GetInputStreamAt",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(ulong) }, null);
            var sizeProp = streamType.GetProperty("Size");
            ulong size = (ulong)(sizeProp?.GetValue(stream) ?? 0UL);
            if (size == 0) return null;

            var inputStream = getInputStreamMethod?.Invoke(stream, new object[] { 0UL });
            if (inputStream is null) return null;

            var dataReaderType = Type.GetType("Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType=WindowsRuntime");
            if (dataReaderType is null) return null;

            var createReaderMethod = dataReaderType.GetMethod("CreateDataReader",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { inputStream.GetType() }, null);
            var reader = createReaderMethod?.Invoke(null, new[] { inputStream });
            if (reader is null) return null;

            var loadAsyncMethod = dataReaderType.GetMethod("LoadAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(uint) }, null);
            var loadOp = loadAsyncMethod?.Invoke(reader, new object[] { (uint)size });
            if (loadOp is null) return null;
            AwaitWinRtOp(loadOp);

            var readBytesMethod = dataReaderType.GetMethod("ReadBytes",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(byte[]) }, null);
            var bytes = new byte[size];
            readBytesMethod?.Invoke(reader, new object[] { bytes });

            (reader as IDisposable)?.Dispose();
            (inputStream as IDisposable)?.Dispose();
            (stream as IDisposable)?.Dispose();

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // =====================================================================
    // Panning + lasso
    // =====================================================================
    private Point ScreenToWorld(Point p) => new Point((p.X - _viewPanX) / _viewZoom, (p.Y - _viewPanY) / _viewZoom);

    private void CanvasHostBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CanvasHostBorder is null) return;

        if (MainMenuFlyout is not null && MainMenuFlyout.Visibility == Visibility.Visible)
            MainMenuFlyout.Visibility = Visibility.Collapsed;

        var screenPt = e.GetPosition(CanvasHostBorder);
        bool middleDrag = e.MiddleButton == MouseButtonState.Pressed;
        bool handDrag = e.LeftButton == MouseButtonState.Pressed && _tool == ToolMode.Hand;
        bool lassoDrag = e.LeftButton == MouseButtonState.Pressed && _tool == ToolMode.Lasso;

        if (lassoDrag && TryStartResize(screenPt))
        {
            CanvasHostBorder.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (middleDrag || handDrag)
        {
            _panning = true;
            _panStart = screenPt;
            _panStartPanX = _viewPanX;
            _panStartPanY = _viewPanY;
            CanvasHostBorder.CaptureMouse();
            CanvasHostBorder.Cursor = Cursors.None;
            if (InkArea is not null) InkArea.Cursor = Cursors.None;
            // no bitmap cache on the infinite canvas
            e.Handled = true;
            return;
        }

        if (lassoDrag)
        {
            var worldPt = ScreenToWorld(screenPt);
            if (!IsPointOnSelectedElement(worldPt) && !IsPointOnSelectedStroke(worldPt)) SelectItemUnder(worldPt);
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

        if (_resizingHandle) { UpdateResize(ScreenToWorld(screenPt)); e.Handled = true; return; }

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
            UpdateSelectionDrag(worldPt.X - _dragStartWorld.X, worldPt.Y - _dragStartWorld.Y);
            e.Handled = true;
        }
    }

    private void CanvasHostBorder_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizingHandle) { EndResize(); e.Handled = true; return; }
        if (_panning) { EndPan(); e.Handled = true; return; }
        if (_lassoActive) { EndLasso(); e.Handled = true; return; }
        if (_draggingSelection) { EndSelectionDrag(); e.Handled = true; }
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizingHandle) EndResize();
        else if (_panning) EndPan();
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
        SaveCurrentViewToPage();
        ScheduleRebuildEdgeButtons();
        RebuildResizeHandles();
        UpdateCursor();
    }

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
        foreach (var sp in _lassoPointsScreen) worldPoly.Add(ScreenToWorld(sp));

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(worldPoly[0], true, true);
            for (int i = 1; i < worldPoly.Count; i++) gc.LineTo(worldPoly[i], true, false);
        }
        geometry.Freeze();

        var strokesIn = new StrokeCollection();
        foreach (var s in InkArea.Strokes)
        {
            var b = s.GetBounds();
            var center = new Point(b.X + b.Width / 2.0, b.Y + b.Height / 2.0);
            if (geometry.FillContains(center)) strokesIn.Add(s);
        }
        if (strokesIn.Count > 0) InkArea.Select(strokesIn);

        foreach (var child in InkArea.Children)
        {
            if (child is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double cx = l + (fe.ActualWidth > 0 ? fe.ActualWidth / 2 : 60);
            double cy = t + (fe.ActualHeight > 0 ? fe.ActualHeight / 2 : 20);
            if (geometry.FillContains(new Point(cx, cy))) _selectedElements.Add(fe);
        }
        UpdateElementSelectionVisuals();
        RebuildResizeHandles();
        int total = strokesIn.Count + _selectedElements.Count;
        StatusText.Text = total > 0
            ? $"Selected {strokesIn.Count} stroke(s) and {_selectedElements.Count} item(s)."
            : "Nothing selected.";
    }

    private bool IsPointOnSelectedElement(Point worldPt)
    {
        foreach (var el in _selectedElements)
        {
            if (el is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
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
        var hits = selected.HitTest(new[] { worldPt }, 6);
        if (hits.Count > 0) return true;
        var bounds = selected.GetBounds();
        bounds.Inflate(12, 12);
        return bounds.Contains(worldPt);
    }

    private void StartSelectionDrag(Point worldPt)
    {
        _draggingSelection = true;
        _dragStartWorld = worldPt;
        _lastDragDx = 0;
        _lastDragDy = 0;
        HideSelectionToolbar();

        _elementStartPositions.Clear();
        foreach (var el in _selectedElements)
            _elementStartPositions[el] = (InkCanvas.GetLeft(el), InkCanvas.GetTop(el));

        var sel = InkArea?.GetSelectedStrokes();
        if (sel is not null && sel.Count > 0 && CanvasHost is not null)
        {
            _dragOriginalStrokes = new StrokeCollection();
            foreach (var s in sel) _dragOriginalStrokes.Add(s);

            var bounds = _dragOriginalStrokes.GetBounds();
            _strokeDragStartBounds = bounds;

            var bmp = RenderStrokesToBitmap(_dragOriginalStrokes, bounds);
            _strokeDragImage = new Image
            {
                Source = bmp,
                Width = bounds.Width * _viewZoom,
                Height = bounds.Height * _viewZoom,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            double sl = _viewPanX + bounds.Left * _viewZoom;
            double st = _viewPanY + bounds.Top * _viewZoom;
            Canvas.SetLeft(_strokeDragImage, sl);
            Canvas.SetTop(_strokeDragImage, st);
            CanvasHost.Children.Add(_strokeDragImage);

            _applyingHistory = true;
            try
            {
                var toRemove = new StrokeCollection();
                foreach (var s in sel) toRemove.Add(s);
                InkArea.Strokes.Remove(toRemove);
            }
            finally { _applyingHistory = false; }
        }
        else
        {
            _dragOriginalStrokes = null;
            _strokeDragImage = null;
        }
    }

    private static RenderTargetBitmap RenderStrokesToBitmap(StrokeCollection strokes, Rect bounds)
    {
        double dpi = 96.0 * 2.0;
        int pxW = Math.Max(1, (int)Math.Ceiling(bounds.Width * 2));
        int pxH = Math.Max(1, (int)Math.Ceiling(bounds.Height * 2));
        if (pxW > 4000) pxW = 4000;
        if (pxH > 4000) pxH = 4000;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.PushTransform(new ScaleTransform(2, 2));
            dc.DrawRectangle(Brushes.Transparent, null, bounds);
            foreach (var s in strokes) s.Draw(dc);
            dc.Pop(); dc.Pop();
        }
        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private void UpdateSelectionDrag(double dx, double dy)
    {
        foreach (var el in _selectedElements)
            if (_elementStartPositions.TryGetValue(el, out var s))
            {
                InkCanvas.SetLeft(el, s.L + dx);
                InkCanvas.SetTop(el, s.T + dy);
            }

        if (_strokeDragImage is not null)
        {
            double sl = _viewPanX + (_strokeDragStartBounds.Left + dx) * _viewZoom;
            double st = _viewPanY + (_strokeDragStartBounds.Top + dy) * _viewZoom;
            Canvas.SetLeft(_strokeDragImage, sl);
            Canvas.SetTop(_strokeDragImage, st);
        }

        double sdx = (dx - _lastDragDx) * _viewZoom;
        double sdy = (dy - _lastDragDy) * _viewZoom;
        TranslateResizeHandles(sdx, sdy);

        _selectionWorldBounds = new Rect(
            _selectionWorldBounds.Left + (dx - _lastDragDx),
            _selectionWorldBounds.Top + (dy - _lastDragDy),
            _selectionWorldBounds.Width,
            _selectionWorldBounds.Height);

        _lastDragDx = dx;
        _lastDragDy = dy;

        UpdateElementSelectionVisuals();
    }

    private void EndSelectionDrag()
    {
        if (!_draggingSelection) return;
        _draggingSelection = false;
        if (CanvasHostBorder is not null && CanvasHostBorder.IsMouseCaptured)
            CanvasHostBorder.ReleaseMouseCapture();

        if (_strokeDragImage is not null && CanvasHost is not null)
        {
            CanvasHost.Children.Remove(_strokeDragImage);
            _strokeDragImage = null;
        }

        if (InkArea is not null && _dragOriginalStrokes is not null && _dragOriginalStrokes.Count > 0)
        {
            var shifted = new StrokeCollection();
            foreach (var orig in _dragOriginalStrokes)
            {
                var pts = new StylusPointCollection();
                foreach (var p in orig.StylusPoints)
                    pts.Add(new StylusPoint((float)(p.X + _lastDragDx), (float)(p.Y + _lastDragDy), p.PressureFactor));
                shifted.Add(new Stroke(pts, orig.DrawingAttributes.Clone()));
            }
            _applyingHistory = true;
            try
            {
                InkArea.Strokes.Add(shifted);
                InkArea.Select(shifted);
            }
            finally { _applyingHistory = false; }

            _undo.Push(new StrokeChange(shifted, _dragOriginalStrokes));
            _redo.Clear();
        }

        foreach (var el in _selectedElements)
            if (_elementStartPositions.TryGetValue(el, out var start))
            {
                double nowL = InkCanvas.GetLeft(el), nowT = InkCanvas.GetTop(el);
                if (Math.Abs(nowL - start.L) > 0.1 || Math.Abs(nowT - start.T) > 0.1)
                {
                    _undo.Push(new ElementMoveChange { Element = el, OldLeft = start.L, OldTop = start.T, NewLeft = nowL, NewTop = nowT });
                    _redo.Clear();
                }
            }

        _elementStartPositions.Clear();
        _dragOriginalStrokes = null;
        _lastDragDx = 0;
        _lastDragDy = 0;

        RebuildResizeHandles();
        MarkDirty();
    }

    // =====================================================================
    // Clipboard / delete
    // =====================================================================
    private bool DeleteSelection()
    {
        if (InkArea is null) return false;
        var selectedStrokes = InkArea.GetSelectedStrokes();
        bool didAnything = false;
        if (selectedStrokes.Count > 0)
        {
            _applyingHistory = true;
            try
            {
                var toRemove = new StrokeCollection();
                foreach (var s in selectedStrokes) toRemove.Add(s);
                InkArea.Strokes.Remove(toRemove);
            }
            finally { _applyingHistory = false; }
            _undo.Push(new StrokeChange(new StrokeCollection(), selectedStrokes));
            _redo.Clear();
            didAnything = true;
        }
        if (_selectedElements.Count > 0)
        {
            var removed = new List<UIElement>(_selectedElements);
            foreach (var el in removed)
            {
                if (InkArea.Children.Contains(el)) InkArea.Children.Remove(el);
                if (CurrentPage.Elements.Contains(el)) CurrentPage.Elements.Remove(el);
            }
            ClearElementSelection();
            didAnything = true;
        }
        RebuildResizeHandles();
        if (didAnything) { MarkDirty(); StatusText.Text = "Deleted selection."; }
        return didAnything;
    }

    private bool CopySelection()
    {
        if (InkArea is null) return false;
        var selectedStrokes = InkArea.GetSelectedStrokes();
        if (selectedStrokes.Count == 0) return false;
        _clipboardStrokes = new StrokeCollection();
        foreach (var s in selectedStrokes) _clipboardStrokes.Add(s);
        _pasteCounter = 0;
        StatusText.Text = $"Copied {selectedStrokes.Count} stroke(s).";
        return true;
    }

    private bool CutSelection()
    {
        if (!CopySelection()) return false;
        DeleteSelection();
        StatusText.Text = "Cut selection.";
        return true;
    }

    private bool PasteClipboard()
    {
        if (InkArea is null) return false;
        if (_clipboardStrokes is null || _clipboardStrokes.Count == 0) return false;
        _pasteCounter++;
        double offset = 24.0 * _pasteCounter;
        var pasted = new StrokeCollection();
        foreach (var s in _clipboardStrokes)
        {
            var pts = new StylusPointCollection();
            foreach (var p in s.StylusPoints)
                pts.Add(new StylusPoint((float)(p.X + offset), (float)(p.Y + offset), p.PressureFactor));
            pasted.Add(new Stroke(pts, s.DrawingAttributes.Clone()));
        }
        _applyingHistory = true;
        try { InkArea.Strokes.Add(pasted); }
        finally { _applyingHistory = false; }
        InkArea.Select(pasted);
        _undo.Push(new StrokeChange(pasted, new StrokeCollection()));
        _redo.Clear();
        MarkDirty();
        RebuildResizeHandles();
        StatusText.Text = $"Pasted {pasted.Count} stroke(s).";
        return true;
    }

    private void ClearElementSelection()
    {
        _selectedElements.Clear();
        if (CanvasHost is null) { RebuildResizeHandles(); return; }
        foreach (var v in _elementSelectionVisuals)
            if (v.Parent is Panel p) p.Children.Remove(v);
        _elementSelectionVisuals.Clear();
        RebuildResizeHandles();
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
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : 200;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : 30;
            var r = new Rectangle
            {
                Width = w, Height = h,
                Stroke = new SolidColorBrush(Color.FromArgb(200, 26, 115, 232)),
                StrokeThickness = 1.5 / Math.Max(_viewZoom, 0.01),
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(l, t, 0, 0)
            };
            CanvasHost.Children.Add(r);
            _elementSelectionVisuals.Add(r);
        }
    }

    // =====================================================================
    // Title bar
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
        Dispatcher.BeginInvoke(new Action(ScheduleRebuildEdgeButtons), DispatcherPriority.Loaded);
    }

    // =====================================================================
    // Insert / paste
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
        catch (Exception ex) { MessageBox.Show(this, "Could not load image.\n\n" + ex.Message, "Insert image", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void PasteClipboardImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsImage()) { StatusText.Text = "No image in clipboard."; return; }
            var src = Clipboard.GetImage();
            if (src is null) return;
            if (src.CanFreeze) src.Freeze();
            AddImageToPage(src);
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not paste image.\n\n" + ex.Message, "Paste image", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AddImageToPage(BitmapSource bmp)
    {
        double maxDim = 400, scale = 1.0;
        if (bmp.PixelWidth > maxDim) scale = maxDim / bmp.PixelWidth;
        if (bmp.PixelHeight * scale > maxDim) scale = maxDim / bmp.PixelHeight;
        double w = Math.Max(40, bmp.PixelWidth * scale);
        double h = Math.Max(40, bmp.PixelHeight * scale);
        var img = new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Uniform };
        PlaceOnCanvas(img);
        StatusText.Text = "Image inserted. Use Lasso to move or resize.";
    }

    private void InsertText_Click(object sender, RoutedEventArgs e) => AddTextToPage(string.Empty);

    private void PasteClipboardText_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) { StatusText.Text = "No text in clipboard."; return; }
        string text = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) return;
        PasteTextAsKeywordBoxes(text);
    }

    private void AddTextToPage(string initialText)
    {
        var tb = new TextBox
        {
            Text = initialText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontSize = 18, Width = 260, MinHeight = 32,
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
        CurrentPage.Elements.Add(element);
        MarkDirty();
    }

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsPanel("Settings");
    }

    // =====================================================================
    // Page appearance
    // =====================================================================
    private void PaperThumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name && Enum.TryParse<PaperStyle>(name, out var ps))
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
            ApplyThemeToChrome();
            UpdatePageBackground();
            RebuildHollowRingCursor();
            UpdateCursor();
            MarkDirty();
            StatusText.Text = $"Background: {rb.ToolTip}";
        }
    }

    private void TemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string name && Enum.TryParse<PageTemplate>(name, out var t))
        {
            CurrentPage.Template = t;
            UpdatePageBackground();
            MarkDirty();
            StatusText.Text = $"Template: {rb.ToolTip}";
        }
    }

    private void SpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressSpacingChange) return;
        if (PageBackground is null) return;
        CurrentPage.Spacing = e.NewValue;
        if (SpacingText is not null) SpacingText.Text = ((int)Math.Round(e.NewValue)).ToString();
        UpdatePageBackground();
        MarkDirty();
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
        PageBackground.Fill = BuildPageBrush(CurrentPage, _viewZoom);
    }

    private static Brush BuildPageBrush(NotebookPage page, double viewZoom)
    {
        Brush baseBrush = BuildPaperBaseBrush(page.Paper, page.BackgroundColor);
        if (page.Template != PageTemplate.Blank && baseBrush is ImageBrush)
            baseBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF2));
        if (page.Template == PageTemplate.Blank || page.Spacing <= 0) return baseBrush;

        double spacing = page.Spacing;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(baseBrush, null,
            new RectangleGeometry(new Rect(0, 0, spacing, spacing))));

        Color lineColor = GetLineColor(page.BackgroundColor, page.PatternOpacity);
        double lineThickness = page.PatternThickness / Math.Max(viewZoom, 0.01);
        if (lineThickness < 0.1) lineThickness = 0.1;
        if (lineThickness > 8) lineThickness = 8;

        var linePen = new Pen(new SolidColorBrush(lineColor), lineThickness);

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
            double dotRadius = page.PatternDotSize / Math.Max(viewZoom, 0.01);
            if (dotRadius < 0.3) dotRadius = 0.3;
            if (dotRadius > 6) dotRadius = 6;
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(lineColor), null,
                new EllipseGeometry(new Point(spacing / 2, spacing / 2), dotRadius, dotRadius)));
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
        if (userColor != Colors.White) return new SolidColorBrush(userColor);
        if (paper == PaperStyle.White) return new SolidColorBrush(Colors.White);
        var photo = GetCrumpledBrush();
        if (photo is not null) return photo;
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

    private static ImageBrush? _crumpledBrush;
    private static bool _crumpledTried;

    private static Brush? GetCrumpledBrush()
    {
        if (_crumpledTried) return _crumpledBrush;
        _crumpledTried = true;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/crumpled.jpg", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            var brush = new ImageBrush(bmp)
            {
                TileMode = TileMode.FlipXY,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1600, 1000),
                Stretch = Stretch.Fill
            };
            brush.Freeze();
            _crumpledBrush = brush;
        }
        catch { _crumpledBrush = null; }
        return _crumpledBrush;
    }

    private static Color GetLineColor(Color bg, double opacityPercent)
    {
        double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        byte alpha = (byte)Math.Clamp(opacityPercent * 2.55, 0, 255);
        return lum > 0.5
            ? Color.FromArgb(alpha, 60, 60, 60)
            : Color.FromArgb(alpha, 220, 220, 220);
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
            RadioButton paperBtn = CurrentPage.Paper switch { PaperStyle.Crumpled => PaperCrumpled, _ => PaperWhite };
            paperBtn.IsChecked = true;
        }
        string hex = $"#{CurrentPage.BackgroundColor.R:X2}{CurrentPage.BackgroundColor.G:X2}{CurrentPage.BackgroundColor.B:X2}";
        RadioButton[] swatches = { ColorWhite, ColorCream, ColorLightGray, ColorSage, ColorSky, ColorNavy, ColorDarkGreen, ColorBlack, ColorPitch };
        bool matched = false;
        foreach (var rb in swatches)
        {
            if (rb is null) continue;
            if (rb.Tag is string s && string.Equals(s, hex, StringComparison.OrdinalIgnoreCase))
            { rb.IsChecked = true; matched = true; break; }
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

        if (SpacingSlider is not null)
        {
            _suppressSpacingChange = true;
            SpacingSlider.Value = Math.Clamp(CurrentPage.Spacing, 1, 60);
            _suppressSpacingChange = false;
        }
        if (SpacingText is not null)
            SpacingText.Text = ((int)Math.Round(CurrentPage.Spacing)).ToString();
        ApplyThemeToChrome();

        UpdatePageBackground();
        SyncSettingsUI();
    }

    // =====================================================================
    // Tools
    // =====================================================================
    private void PenButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Pen;
        ClearElementSelection();
        if (InkArea is not null) { InkArea.IsHitTestVisible = true; InkArea.EditingMode = InkCanvasEditingMode.Ink; }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Pen selected.";
        ShowToolPopup(PenButton, "Pen");
    }

    private void CalligraphyButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Calligraphy;
        ClearElementSelection();
        if (InkArea is not null) { InkArea.IsHitTestVisible = true; InkArea.EditingMode = InkCanvasEditingMode.Ink; }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Calligraphy pen selected.";
        ShowToolPopup(CalligraphyButton, "Calligraphy pen");
    }

    private void HighlighterButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Highlighter;
        ClearElementSelection();
        if (InkArea is not null) { InkArea.IsHitTestVisible = true; InkArea.EditingMode = InkCanvasEditingMode.Ink; }
        ApplyPenAttributes();
        UpdateCursor();
        StatusText.Text = "Highlighter selected.";
        ShowToolPopup(HighlighterButton, "Highlighter");
    }

    private void EraserButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Eraser;
        ClearElementSelection();
        if (InkArea is not null) { InkArea.IsHitTestVisible = true; InkArea.EditingMode = InkCanvasEditingMode.EraseByStroke; }
        UpdateCursor();
        StatusText.Text = "Eraser selected.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void LassoButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Lasso;
        if (CanvasHost is not null) { try { CanvasHost.CacheMode = null; } catch { } }
        if (InkArea is not null) { InkArea.IsHitTestVisible = true; InkArea.EditingMode = InkCanvasEditingMode.None; }
        UpdateCursor();
        StatusText.Text = "Lasso: drag around ink or items.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void InkArea_SelectionChanged(object sender, EventArgs e)
    {
        if (InkArea is null) return;
        int n = InkArea.GetSelectedStrokes().Count;
        int m = _selectedElements.Count;
        if (n > 0 || m > 0) StatusText.Text = $"Selected {n} stroke(s) and {m} item(s).";
        RebuildResizeHandles();
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
        RebuildResizeHandles();
        StatusText.Text = $"Selected all {InkArea.Strokes.Count} stroke(s) and {_selectedElements.Count} item(s).";
        e.Handled = true;
    }

    private void HandButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = ToolMode.Hand;
        ClearElementSelection();
        if (InkArea is not null) InkArea.IsHitTestVisible = false;
        UpdateCursor();
        StatusText.Text = "Hand: drag to pan.";
        if (ToolOptionsPopup is not null) ToolOptionsPopup.IsOpen = false;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (InkArea is null) return;
        if (InkArea.Strokes.Count == 0) { StatusText.Text = "Already empty."; return; }
        var answer = MessageBox.Show(this, "Remove everything on this page?", "Clear page",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
        {
            InkArea.Strokes.Clear();
            var toRemove = new List<UIElement>();
            foreach (var c in InkArea.Children)
                if (c is UIElement uie) toRemove.Add(uie);
            foreach (var el in toRemove)
            {
                InkArea.Children.Remove(el);
                CurrentPage.Elements.Remove(el);
            }
            ClearElementSelection();
            StatusText.Text = "Page cleared.";
        }
    }

    private void InkArea_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        var points = e.Stroke.StylusPoints;
        if (points.Count == 0) return;
        if (_shapeAssist && _tool == ToolMode.Pen && TrySnapShape(e.Stroke)) return;
        if (!e.Stroke.DrawingAttributes.IsHighlighter) SmoothCompletedStroke(e.Stroke);
    }

    private void SmoothCompletedStroke(Stroke original)
    {
        if (InkArea is null) return;
        var pts = original.StylusPoints;
        int n = pts.Count;
        if (n < 3) return;

        var rawX = new double[n]; var rawY = new double[n]; var rawP = new float[n];
        for (int i = 0; i < n; i++) { rawX[i] = pts[i].X; rawY[i] = pts[i].Y; rawP[i] = pts[i].PressureFactor; }

        var sx = new double[n]; var sy = new double[n]; var sp = new float[n];

        sx[0] = rawX[0]; sy[0] = rawY[0]; sp[0] = rawP[0];
        sx[n - 1] = rawX[n - 1]; sy[n - 1] = rawY[n - 1]; sp[n - 1] = rawP[n - 1];

        for (int i = 1; i < n - 1; i++)
        {
            int lo = i - 1; int hi = i + 1;
            double sumX = rawX[lo] + rawX[i] + rawX[hi];
            double sumY = rawY[lo] + rawY[i] + rawY[hi];
            float sumP = rawP[lo] + rawP[i] + rawP[hi];
            sx[i] = sumX / 3.0;
            sy[i] = sumY / 3.0;
            sp[i] = sumP / 3f;
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
        try { InkArea.Strokes.RemoveAt(idx); InkArea.Strokes.Insert(idx, smoothed); }
        finally { _applyingHistory = false; }
        ReplaceStrokeInUndoStack(original, smoothed);
    }

    private void ReplaceStrokeInUndoStack(Stroke original, Stroke smoothed)
    {
        if (_undo.Count == 0) return;
        var drained = new List<object>();
        bool replaced = false;
        while (_undo.Count > 0)
        {
            var c = _undo.Pop();
            if (!replaced && c is StrokeChange sc && sc.Added.Contains(original))
            {
                var na = new StrokeCollection();
                foreach (var s in sc.Added) na.Add(s == original ? smoothed : s);
                drained.Add(new StrokeChange(na, sc.Removed));
                replaced = true;
                break;
            }
            drained.Add(c);
        }
        for (int i = drained.Count - 1; i >= 0; i--) _undo.Push(drained[i]);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

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
        RebuildResizeHandles();
    }

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var change = _redo.Pop();
        ForwardChange(change);
        _undo.Push(change);
        MarkDirty();
        RebuildResizeHandles();
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
        _pages.Add(NewStyledPage());
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
        var dialog = new OpenFileDialog { Filter = FileFilter, DefaultExt = FileExtension, Title = "Open a Canvaas note" };
        if (dialog.ShowDialog(this) == true) OpenFromFile(dialog.FileName);
    }

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => SaveCurrent();
    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveAs();

    private bool SaveCurrent() => _currentPath is null ? SaveAs() : SaveToFile(_currentPath);

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = FileFilter, DefaultExt = FileExtension, AddExtension = true,
            Title = "Save your Canvaas note",
            FileName = _currentPath is null ? "My note" : System.IO.Path.GetFileNameWithoutExtension(_currentPath)
        };
        if (dialog.ShowDialog(this) != true) return false;
        return SaveToFile(dialog.FileName);
    }

    private bool SaveToFile(string path)
    {
        if (_pages.Count > 0 && InkArea is not null)
        {
            var oldPage = _pages[_currentPageIndex];
            oldPage.Elements.Clear();
            foreach (var c in InkArea.Children)
                if (c is UIElement uie) oldPage.Elements.Add(uie);
        }

        string tempPath = path + ".tmp";
        var imageBlobs = new Dictionary<string, byte[]>();
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
                        Paper = page.Paper.ToString(),
                        WorldWidth = page.WorldWidth,
                        WorldHeight = page.WorldHeight,
                        PatternOpacity = page.PatternOpacity,
                        PatternThickness = page.PatternThickness,
                        PatternDotSize = page.PatternDotSize,
                        Elements = BuildElementManifests(page, _pages.IndexOf(page), imageBlobs)
                    });
                }
                foreach (var blob in imageBlobs)
                {
                    var imageEntry = zip.CreateEntry(blob.Key);
                    using var imageStream = imageEntry.Open();
                    imageStream.Write(blob.Value, 0, blob.Value.Length);
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
            MessageBox.Show(this, "Save failed.\n\n" + ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var manifestEntry = zip.GetEntry(ManifestEntryName) ?? throw new InvalidDataException("Not a Canvaas note.");
            Manifest? manifest;
            using (var entryStream = manifestEntry.Open())
                manifest = JsonSerializer.Deserialize<Manifest>(entryStream, JsonOptions);
            if (manifest is null) throw new InvalidDataException("Manifest is empty.");

            loadedPages = new List<NotebookPage>();
            if (manifest.FormatVersion >= 3 && manifest.Pages is { Count: > 0 })
            {
                for (int i = 0; i < manifest.Pages.Count; i++)
                {
                    var pm = manifest.Pages[i];
                    var page = new NotebookPage
                    {
                        BackgroundColor = ParseHexColor(pm.BackgroundColor ?? "#FFFFFF", Colors.White),
                        Spacing = pm.Spacing is double s && s > 0 ? Math.Clamp(s, 1, 60) : 40,
                        PatternOpacity = pm.PatternOpacity ?? 50,
                        PatternThickness = pm.PatternThickness ?? 1.0,
                        PatternDotSize = pm.PatternDotSize ?? 1.2
                    };
                    if (Enum.TryParse<PageTemplate>(pm.Template, out var t)) page.Template = t;
                    if (Enum.TryParse<PaperStyle>(pm.Paper, out var ps)) page.Paper = ps;
                    if (pm.WorldWidth is double ww && ww >= 100) page.WorldWidth = ww;
                    if (pm.WorldHeight is double wh && wh >= 100) page.WorldHeight = wh;

                    LoadElementsInto(page, pm, zip);
                    var inkEntry = zip.GetEntry(string.Format(PageEntryFormat, i));
                    if (inkEntry is not null)
                    {
                        using var buffer = new MemoryStream();
                        using (var es = inkEntry.Open()) es.CopyTo(buffer);
                        buffer.Position = 0;
                        page.Strokes = new StrokeCollection(buffer);
                        if (page.Strokes.Count > 0)
                        {
                            var b = page.Strokes.GetBounds();
                            if (b.Right + 200 > page.WorldWidth) page.WorldWidth = b.Right + 200;
                            if (b.Bottom + 200 > page.WorldHeight) page.WorldHeight = b.Bottom + 200;
                        }
                    }
                    loadedPages.Add(page);
                }
                loadedCurrentPage = Math.Clamp(manifest.CurrentPageIndex, 0, loadedPages.Count - 1);
            }
            else
            {
                var page = new NotebookPage();
                var inkEntry = zip.GetEntry(LegacyInkEntryName);
                if (inkEntry is not null)
                {
                    using var buffer = new MemoryStream();
                    using (var es = inkEntry.Open()) es.CopyTo(buffer);
                    buffer.Position = 0;
                    page.Strokes = new StrokeCollection(buffer);
                }
                loadedPages.Add(page);
            }
            if (loadedPages.Count == 0) loadedPages.Add(new NotebookPage());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open.\n\n" + ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _pages.Clear();
        _pages.AddRange(loadedPages);
        _currentPageIndex = loadedCurrentPage;
        LoadCurrentPageIntoCanvas();
        _currentPath = path;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = $"Opened: {path}";
    }

    // =====================================================================
    // Helpers
    // =====================================================================
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true; else SaveSettings();
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, "You have unsaved changes. Save them first?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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

    private AppSettings? _savedStyle;
    private string[] _lightQuick = { "#000000", "#E53935", "#1E88E5", "#43A047" };
    private string[] _darkQuick = { "#FFFFFF", "#FF6B6B", "#64B5F6", "#81C784" };
    private bool _quickIsDark;

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static string ColorToHex(Color c) =>
        "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    private NotebookPage NewStyledPage()
    {
        var p = new NotebookPage();
        var s = _savedStyle;
        if (s is null) return p;
        if (!string.IsNullOrEmpty(s.DefBackground)) p.BackgroundColor = ParseHexColor(s.DefBackground, Colors.White);
        if (Enum.TryParse<PageTemplate>(s.DefTemplate, out var t)) p.Template = t;
        if (Enum.TryParse<PaperStyle>(s.DefPaper, out var ps)) p.Paper = ps;
        if (s.DefSpacing is double sp && sp > 0) p.Spacing = Math.Clamp(sp, 1, 60);
        if (s.DefPatternOpacity is double po) p.PatternOpacity = po;
        if (s.DefPatternThickness is double pt) p.PatternThickness = pt;
        if (s.DefPatternDotSize is double pd) p.PatternDotSize = pd;
        return p;
    }

    private void CaptureStyle(AppSettings s)
    {
        s.LightQuick = _lightQuick;
        s.DarkQuick = _darkQuick;
        if (_pages.Count > 0)
        {
            var p = CurrentPage;
            s.DefBackground = ColorToHex(p.BackgroundColor);
            s.DefTemplate = p.Template.ToString();
            s.DefPaper = p.Paper.ToString();
            s.DefSpacing = p.Spacing;
            s.DefPatternOpacity = p.PatternOpacity;
            s.DefPatternThickness = p.PatternThickness;
            s.DefPatternDotSize = p.PatternDotSize;
        }
        else if (_savedStyle is not null)
        {
            s.DefBackground = _savedStyle.DefBackground;
            s.DefTemplate = _savedStyle.DefTemplate;
            s.DefPaper = _savedStyle.DefPaper;
            s.DefSpacing = _savedStyle.DefSpacing;
            s.DefPatternOpacity = _savedStyle.DefPatternOpacity;
            s.DefPatternThickness = _savedStyle.DefPatternThickness;
            s.DefPatternDotSize = _savedStyle.DefPatternDotSize;
        }
        _savedStyle = s;
    }

    private void ApplyThemeToChrome()
    {
        bool dark = IsDarkColor(CurrentPage.BackgroundColor);
        var oldSet = _quickIsDark ? _darkQuick : _lightQuick;
        var newSet = dark ? _darkQuick : _lightQuick;
        if (dark != _quickIsDark)
        {
            for (int i = 0; i < 4; i++)
            {
                if (ParseHexColor(oldSet[i], Colors.Black) == _penColor)
                { _penColor = ParseHexColor(newSet[i], Colors.Black); break; }
            }
        }
        _quickIsDark = dark;

        var btns = new[] { QuickColor1, QuickColor2, QuickColor3, QuickColor4 };
        for (int i = 0; i < 4; i++)
        {
            if (btns[i] is null) continue;
            btns[i].Tag = newSet[i];
            btns[i].Background = new SolidColorBrush(ParseHexColor(newSet[i], Colors.Black));
        }

        Color bg = CurrentPage.BackgroundColor;
        Color navBg = dark ? Blend(bg, Colors.White, 0.10) : Blend(bg, Colors.Black, 0.05);
        Color navBorder = dark ? Blend(bg, Colors.White, 0.25) : Blend(bg, Colors.Black, 0.30);
        var fg = new SolidColorBrush(dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x22, 0x22, 0x22));
        if (NavBar is not null)
        {
            NavBar.Background = new SolidColorBrush(navBg);
            NavBar.BorderBrush = new SolidColorBrush(navBorder);
        }
        foreach (var b in new[] { PrevPageButton, NextPageButton, ZoomInButton, ZoomOutButton, ZoomResetButton })
            if (b is not null) b.Foreground = fg;
        if (PageCounterBox is not null) PageCounterBox.Foreground = fg;
        Color sqBg = dark ? Blend(bg, Colors.White, 0.10) : Blend(bg, Colors.White, 0.6);
        Color sqBorder = dark ? Blend(bg, Colors.White, 0.25) : Blend(bg, Colors.Black, 0.2);
        if (ColorSquareRoot is not null) { ColorSquareRoot.Background = new SolidColorBrush(sqBg); ColorSquareRoot.BorderBrush = new SolidColorBrush(sqBorder); }
        if (ColorSquareDragHandle is not null)
        {
            ColorSquareDragHandle.Background = new SolidColorBrush(sqBg);
            if (ColorSquareDragHandle.Child is TextBlock grip) grip.Foreground = new SolidColorBrush(dark ? Color.FromRgb(0x99, 0x99, 0x99) : Color.FromRgb(0x77, 0x77, 0x77));
        }
        if (ZoomText is not null) ZoomText.Foreground = fg;
        if (PrevPageButton is not null) PrevPageButton.Opacity = PrevPageButton.IsEnabled ? 1.0 : 0.4;
        ApplyPenAttributes();
    }

    private void WireQuickColorRightClick()
    {
        foreach (var b in new[] { QuickColor1, QuickColor2, QuickColor3, QuickColor4 })
        {
            b.MouseRightButtonUp += QuickColor_RightClick;
            b.ToolTip = "Click: use this colour. Right-click: save the current pen colour here.";
        }
    }

    private void QuickColor_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button b) return;
        int idx = Array.IndexOf(new[] { QuickColor1, QuickColor2, QuickColor3, QuickColor4 }, b);
        if (idx < 0) return;
        string hex = ColorToHex(_penColor);
        var set = _quickIsDark ? _darkQuick : _lightQuick;
        set[idx] = hex;
        b.Tag = hex;
        b.Background = new SolidColorBrush(_penColor);
        HighlightToolbarColor();
        SaveSettings();
        StatusText.Text = "Saved " + hex + " in this colour box.";
        e.Handled = true;
    }

    private string _pasteFont = "Ink Free";

    private string? AskPasteFont()
    {
        string[] fonts = { "Ink Free", "Segoe Print", "Segoe Script" };
        string? chosen = null;
        var win = new Window
        {
            Title = "Choose handwriting font",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5))
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Pick a font for the pasted words",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        foreach (var f in fonts)
        {
            var name = f;
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "Electric field 123", FontFamily = new FontFamily(name), FontSize = 26 });
            stack.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Brushes.Gray });
            var btn = new Button
            {
                Content = stack,
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 0, 8),
                MinWidth = 320,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            if (name == _pasteFont)
            {
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8));
                btn.BorderThickness = new Thickness(2);
            }
            btn.Click += (s, e) => { chosen = name; win.DialogResult = true; };
            panel.Children.Add(btn);
        }
        win.Content = panel;
        win.KeyDown += (s, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
        if (chosen is not null) _pasteFont = chosen;
        return chosen;
    }

    // ---------- keyword splitting ----------
    private static List<string> SplitKeywords(string text)
    {
        var result = new List<string>();
        var parts = new List<string>(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (parts.Count == 1)
        {
            string one = parts[0];
            if (System.Text.RegularExpressions.Regex.Matches(one, @"(^|\s)\d{1,3}[\.\)]\s").Count >= 2)
                parts = new List<string>(System.Text.RegularExpressions.Regex.Split(one, @"\s+(?=\d{1,3}[\.\)]\s)"));
            else
                parts = new List<string>(one.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var part in parts)
        {
            string s = part.Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^(?:[\(\[]?\d{1,3}[\.\)\]:]+|[-*\u2022\u00B7\u25AA\u25CF\u2013\u2014]+)\s+", "").Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\(\[]?\d{1,3}[\.\)\]:]$")) continue;
            if (s.Length > 0) result.Add(s);
        }
        return result;
    }

    private static double MeasureKeywordWidth(string text, string fontName, double size)
    {
        try
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(fontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size, Brushes.Black, 1.0);
            return Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 28;
        }
        catch { return 220; }
    }

    // ---------- auto-hide bars ----------
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, ref NativeRect rect, uint winIni);

    private readonly DispatcherTimer _barsTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _barsPinned;
    private bool _barsShown;
    private DateTime _barsLeftAt = DateTime.MinValue;

    private void InitBars()
    {
        _barsTimer.Tick += (s, e) => BarsTick();
        _barsTimer.Start();
        ApplyBars(false);
    }

    private void ApplyBars(bool show)
    {
        _barsShown = show;
        bool visible = _barsPinned || show;
        if (TopToolbarBorder is not null) TopToolbarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (TitleBarBorder is not null) TitleBarBorder.Visibility = (visible && !_fullscreenMode) ? Visibility.Visible : Visibility.Collapsed;
        if (AppWindowChrome is not null) AppWindowChrome.CaptionHeight = (visible && !_fullscreenMode) ? 36 : 0;
        if (CanvasArea is not null)
        {
            Grid.SetRow(CanvasArea, _barsPinned ? 2 : 0);
            Grid.SetRowSpan(CanvasArea, _barsPinned ? 1 : 3);
        }
    }

    private bool AnyBarPopupOpen()
    {
        if (ToolOptionsPopup is not null && ToolOptionsPopup.IsOpen) return true;
        foreach (var b in new[] { AddMenuButton, AddPageButton, SaveMenuButton })
            if (b?.ContextMenu is { IsOpen: true }) return true;
        if (MainMenuFlyout is not null && MainMenuFlyout.Visibility == Visibility.Visible) return true;
        if (SettingsOverlay is not null && SettingsOverlay.Visibility == Visibility.Visible) return true;
        return false;
    }

    private void BarsTick()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized) return;
        bool want;
        if (_barsPinned) want = true;
        else
        {
            bool inZone = false;
            try
            {
                if (GetCursorPos(out var np) && CanvasArea is not null)
                {
                    var p = CanvasArea.PointFromScreen(new Point(np.X, np.Y));
                    double barsH = (TopToolbarBorder.Visibility == Visibility.Visible ? TopToolbarBorder.ActualHeight : 0)
                                 + (TitleBarBorder.Visibility == Visibility.Visible ? TitleBarBorder.ActualHeight : 0);
                    double limit = _barsShown ? barsH + 10 : 4;
                    inZone = p.X >= 0 && p.X <= CanvasArea.ActualWidth && p.Y >= -15 && p.Y <= limit;
                    if (inZone && !_barsShown && Mouse.LeftButton == MouseButtonState.Pressed) inZone = false;
                }
            }
            catch { }
            if (inZone || AnyBarPopupOpen()) { _barsLeftAt = DateTime.UtcNow; want = true; }
            else want = _barsShown && (DateTime.UtcNow - _barsLeftAt).TotalMilliseconds < 600;
        }
        ApplyBars(want);
    }

    private void FitNavBarAboveTaskbar()
    {
        try
        {
            if (CanvasArea is null || NavBar is null || !IsLoaded) return;
            double extra = 0;
            if (!_fullscreenMode)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var wa = new NativeRect();
                if (SystemParametersInfo(0x0030, 0, ref wa, 0))
                {
                    double bottomPx = CanvasArea.PointToScreen(new Point(0, CanvasArea.ActualHeight)).Y;
                    extra = Math.Max(0, (bottomPx - wa.Bottom) / dpi.DpiScaleY);
                    if (extra > 150) extra = 0;
                }
            }
            NavBar.Margin = new Thickness(0, 0, 6, 6 + extra);
        }
        catch { }
    }

    // ---------- click to select (lasso) ----------
    private bool SelectItemUnder(Point worldPt)
    {
        if (InkArea is null) return false;
        FrameworkElement? hit = null;
        foreach (var child in InkArea.Children)
        {
            if (child is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : 100;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : 30;
            if (new Rect(l, t, w, h).Contains(worldPt)) hit = fe;
        }
        StrokeCollection? hitStrokes = null;
        if (hit is null)
        {
            var hs = InkArea.Strokes.HitTest(new[] { worldPt }, 8);
            if (hs.Count > 0) hitStrokes = hs;
        }
        if (hit is null && hitStrokes is null) return false;
        ClearElementSelection();
        InkArea.Select(new StrokeCollection());
        if (hit is not null) _selectedElements.Add(hit);
        else InkArea.Select(hitStrokes);
        UpdateElementSelectionVisuals();
        RebuildResizeHandles();
        return true;
    }

    // ---------- auto shapes ----------
    private bool _shapeAssist;

    private void ShapeAssistButton_Click(object sender, RoutedEventArgs e)
    {
        _shapeAssist = !_shapeAssist;
        ShapeAssistButton.Background = _shapeAssist ? new SolidColorBrush(Color.FromArgb(70, 26, 115, 232)) : Brushes.Transparent;
        StatusText.Text = _shapeAssist
            ? "Auto shapes ON: draw a circle, square, triangle or straight line and lift the pen."
            : "Auto shapes OFF.";
    }

    private bool TrySnapShape(Stroke original)
    {
        if (InkArea is null) return false;
        var raw = original.StylusPoints;
        if (raw.Count < 8) return false;
        var pts = new List<Point>(raw.Count);
        for (int i = 0; i < raw.Count; i++) pts.Add(new Point(raw[i].X, raw[i].Y));

        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).Length;
        double minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X);
        double minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
        double w = maxX - minX, h = maxY - minY, maxDim = Math.Max(w, h);
        if (len < 60 || maxDim < 25) return false;

        var first = pts[0];
        var last = pts[pts.Count - 1];
        double gap = (last - first).Length;

        // open stroke: only straight lines
        if (gap >= 0.2 * len)
        {
            double dx = last.X - first.X, dy = last.Y - first.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 40 || len > 1.25 * chord) return false;
            double maxDev = 0;
            foreach (var pt in pts)
            {
                double dev = Math.Abs((pt.X - first.X) * dy - (pt.Y - first.Y) * dx) / chord;
                if (dev > maxDev) maxDev = dev;
            }
            if (maxDev > 0.07 * chord) return false;
            if (Math.Abs(dy) < 0.08 * Math.Abs(dx)) last = new Point(last.X, first.Y);
            else if (Math.Abs(dx) < 0.08 * Math.Abs(dy)) last = new Point(first.X, last.Y);
            return ReplaceWithShape(original, new List<Point> { first, last });
        }

        // closed stroke: triangle, rectangle/square, or circle/ellipse
        var verts = PolygonCorners(pts, 0.08 * maxDim);
        if (verts.Count == 3)
        {
            double area = Math.Abs((verts[1].X - verts[0].X) * (verts[2].Y - verts[0].Y)
                                 - (verts[2].X - verts[0].X) * (verts[1].Y - verts[0].Y)) / 2.0;
            if (area < 0.03 * maxDim * maxDim) return false;
            var tri = new List<Point>(verts) { verts[0] };
            return ReplaceWithShape(original, tri);
        }
        if (verts.Count == 4)
        {
            bool rectLike = true;
            for (int i = 0; i < 4; i++)
            {
                var v1 = verts[i] - verts[(i + 3) % 4];
                var v2 = verts[(i + 1) % 4] - verts[i];
                double ang = Math.Abs(Vector.AngleBetween(v1, v2));
                if (ang < 62 || ang > 118) rectLike = false;
            }
            var quad = new List<Point>(verts);
            if (rectLike)
            {
                bool axis = true;
                for (int i = 0; i < 4; i++)
                {
                    var edge = verts[(i + 1) % 4] - verts[i];
                    double a = (Math.Abs(Math.Atan2(edge.Y, edge.X)) * 180.0 / Math.PI) % 90.0;
                    if (a > 18 && a < 72) axis = false;
                }
                if (axis)
                {
                    double cx0 = (minX + maxX) / 2.0, cy0 = (minY + maxY) / 2.0;
                    double rw = w, rh = h;
                    if (Math.Abs(w - h) < 0.14 * maxDim) { rw = (w + h) / 2.0; rh = rw; }
                    quad = new List<Point>
                    {
                        new Point(cx0 - rw / 2, cy0 - rh / 2), new Point(cx0 + rw / 2, cy0 - rh / 2),
                        new Point(cx0 + rw / 2, cy0 + rh / 2), new Point(cx0 - rw / 2, cy0 + rh / 2)
                    };
                }
            }
            quad.Add(quad[0]);
            return ReplaceWithShape(original, quad);
        }

        double ex = (minX + maxX) / 2.0, ey = (minY + maxY) / 2.0, ra = w / 2.0, rb = h / 2.0;
        if (ra < 8 || rb < 8) return false;
        double sum = 0;
        foreach (var pt in pts)
        {
            double qd = Math.Sqrt(Math.Pow((pt.X - ex) / ra, 2) + Math.Pow((pt.Y - ey) / rb, 2));
            sum += Math.Abs(qd - 1.0);
        }
        if (sum / pts.Count > 0.14) return false;
        if (Math.Abs(ra - rb) < 0.15 * Math.Max(ra, rb)) { double rr = (ra + rb) / 2.0; ra = rr; rb = rr; }
        var ring = new List<Point>();
        for (int i = 0; i <= 72; i++)
        {
            double t = 2.0 * Math.PI * i / 72.0;
            ring.Add(new Point(ex + ra * Math.Cos(t), ey + rb * Math.Sin(t)));
        }
        return ReplaceWithShape(original, ring);
    }

    private static List<Point> PolygonCorners(List<Point> pts, double eps)
    {
        int n = pts.Count;
        int f = 0; double best = 0;
        for (int i = 1; i < n; i++)
        {
            double d = (pts[i] - pts[0]).Length;
            if (d > best) { best = d; f = i; }
        }
        var a = Rdp(pts, 0, f, eps);
        var b = Rdp(pts, f, n - 1, eps);
        var verts = new List<Point>(a);
        for (int i = 1; i < b.Count; i++) verts.Add(b[i]);
        if (verts.Count > 2 && (verts[verts.Count - 1] - verts[0]).Length < 0.35 * Math.Max(1.0, best))
            verts.RemoveAt(verts.Count - 1);
        bool changed = true;
        while (changed && verts.Count > 3)
        {
            changed = false;
            for (int i = 0; i < verts.Count; i++)
            {
                var p0 = verts[(i + verts.Count - 1) % verts.Count];
                var p1 = verts[i];
                var p2 = verts[(i + 1) % verts.Count];
                var v1 = p1 - p0; var v2 = p2 - p1;
                if (v1.Length < 1e-6 || v2.Length < 1e-6) { verts.RemoveAt(i); changed = true; break; }
                if (Math.Abs(Vector.AngleBetween(v1, v2)) < 25) { verts.RemoveAt(i); changed = true; break; }
            }
        }
        return verts;
    }

    private static List<Point> Rdp(List<Point> pts, int s, int e, double eps)
    {
        var result = new List<Point>();
        if (e <= s) { result.Add(pts[s]); return result; }
        var a = pts[s]; var b = pts[e];
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double maxD = 0; int idx = -1;
        for (int i = s + 1; i < e; i++)
        {
            double d = len < 1e-6 ? (pts[i] - a).Length
                                  : Math.Abs((pts[i].X - a.X) * dy - (pts[i].Y - a.Y) * dx) / len;
            if (d > maxD) { maxD = d; idx = i; }
        }
        if (idx >= 0 && maxD > eps)
        {
            var left = Rdp(pts, s, idx, eps);
            var right = Rdp(pts, idx, e, eps);
            result.AddRange(left);
            for (int i = 1; i < right.Count; i++) result.Add(right[i]);
        }
        else { result.Add(a); result.Add(b); }
        return result;
    }

    private bool ReplaceWithShape(Stroke original, List<Point> shape)
    {
        if (InkArea is null || shape.Count < 2) return false;
        var spc = new StylusPointCollection();
        foreach (var p in shape) spc.Add(new StylusPoint(p.X, p.Y, 0.5f));
        var da = original.DrawingAttributes.Clone();
        da.FitToCurve = false; da.IgnorePressure = true;
        var ns = new Stroke(spc, da);
        int idx = InkArea.Strokes.IndexOf(original);
        if (idx < 0) return false;
        _applyingHistory = true;
        try { InkArea.Strokes.RemoveAt(idx); InkArea.Strokes.Insert(idx, ns); }
        finally { _applyingHistory = false; }
        ReplaceStrokeInUndoStack(original, ns);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}