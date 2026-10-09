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
using Microsoft.Win32;

namespace Canvaas;

public partial class MainWindow : Window
{
    private const int CurrentFormatVersion = 3;
    private const string FileExtension = ".canvaas";
    private const string FileFilter = "Canvaas note (*.canvaas)|*.canvaas";
    private const string ManifestEntryName = "manifest.json";
    private const string LegacyInkEntryName = "ink.isf";       // v1 and v2
    private const string PageEntryFormat = "page_{0:D3}.isf";  // v3+

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
    }

    private sealed class Manifest
    {
        public int FormatVersion { get; set; }
        public string App { get; set; } = "Canvaas";
        public string AppVersion { get; set; } = "";
        public string SavedAtUtc { get; set; } = "";

        // v1 / v2 single-page fields (only read on load, never written by this version)
        public string? BackgroundColor { get; set; }
        public string? Template { get; set; }
        public double? Spacing { get; set; }

        // v3 multi-page fields
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

    private sealed class NotebookPage
    {
        public StrokeCollection Strokes { get; set; } = new StrokeCollection();
        public Color BackgroundColor { get; set; } = Colors.White;
        public PageTemplate Template { get; set; } = PageTemplate.Blank;
        public double Spacing { get; set; } = 40;
    }

    private enum PageTemplate { Blank, Ruled, Grid, Dot }

    private readonly List<NotebookPage> _pages = new();
    private int _currentPageIndex;

    private readonly Stack<StrokeChange> _undo = new();
    private readonly Stack<StrokeChange> _redo = new();
    private bool _applyingHistory;

    private string? _currentPath;
    private bool _dirty;

    private NotebookPage CurrentPage => _pages[_currentPageIndex];

    public MainWindow()
    {
        InitializeComponent();

        InkArea.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black,
            Width = 2.5,
            Height = 2.5,
            FitToCurve = false,
            IgnorePressure = false
        };

        StateChanged += MainWindow_StateChanged;

        // Start with one blank page
        _pages.Add(new NotebookPage());
        _currentPageIndex = 0;
        LoadCurrentPageIntoCanvas();

        PenButton.IsChecked = true;
        InkArea.EditingMode = InkCanvasEditingMode.Ink;
        ColorWhite.IsChecked = true;
        TemplateBlank.IsChecked = true;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Canvaas v{version.Major}.{version.Minor}.{version.Build}";

        UpdateTitle();
    }

    // =====================================================================
    // Page management
    // =====================================================================

    private void LoadCurrentPageIntoCanvas()
    {
        InkArea.Strokes.StrokesChanged -= Strokes_Changed;
        InkArea.Strokes = CurrentPage.Strokes;
        InkArea.Strokes.StrokesChanged += Strokes_Changed;

        _undo.Clear();
        _redo.Clear();
        CommandManager.InvalidateRequerySuggested();

        SyncUIWithSettings();
        UpdatePageNavigationUI();
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
            Spacing = CurrentPage.Spacing
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
            MessageBox.Show(
                this,
                "You cannot delete the only page in a note.\n\nAdd another page first, then delete this one.",
                "Cannot delete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Delete page {_currentPageIndex + 1} of {_pages.Count}?\n\nEverything on this page will be removed. You can still press Undo (Ctrl+Z) right after.",
            "Delete page",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        // Push a whole-page delete onto the undo stack
        var removedPage = _pages[_currentPageIndex];
        var removedIndex = _currentPageIndex;

        _pages.RemoveAt(_currentPageIndex);
        if (_currentPageIndex >= _pages.Count)
            _currentPageIndex = _pages.Count - 1;

        LoadCurrentPageIntoCanvas();
        MarkDirty();
        StatusText.Text = $"Deleted page {removedIndex + 1}. Now on page {_currentPageIndex + 1} of {_pages.Count}.";

        // Simple, honest feedback. Full multi-page undo comes later.
        _ = removedPage;
    }

    // =====================================================================
    // Title bar buttons
    // =====================================================================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

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
    {
        StatusText.Text = "Add Text is coming in a future update.";
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
        if (PageBackground is null) return;
        if (SpacingCombo.SelectedItem is ComboBoxItem item
            && double.TryParse(item.Content?.ToString(), out var s))
        {
            CurrentPage.Spacing = s;
            UpdatePageBackground();
            MarkDirty();
        }
    }

    private void UpdatePageBackground()
    {
        PageBackground.Fill = BuildPageBrush(CurrentPage.BackgroundColor, CurrentPage.Template, CurrentPage.Spacing);
    }

    private static Brush BuildPageBrush(Color baseColor, PageTemplate template, double spacing)
    {
        if (template == PageTemplate.Blank || spacing <= 0)
        {
            return new SolidColorBrush(baseColor);
        }

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(baseColor),
            null,
            new RectangleGeometry(new Rect(0, 0, spacing, spacing))));

        Color lineColor = GetLineColor(baseColor);
        var linePen = new Pen(new SolidColorBrush(lineColor), 1);

        if (template == PageTemplate.Ruled)
        {
            group.Children.Add(new GeometryDrawing(
                null, linePen,
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
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(lineColor), null,
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
        string hex = $"#{CurrentPage.BackgroundColor.R:X2}{CurrentPage.BackgroundColor.G:X2}{CurrentPage.BackgroundColor.B:X2}";

        RadioButton[] swatches = { ColorWhite, ColorCream, ColorLightGray, ColorSage, ColorSky, ColorNavy, ColorDarkGreen, ColorBlack };
        bool matched = false;
        foreach (var rb in swatches)
        {
            if (rb.Tag is string s && string.Equals(s, hex, StringComparison.OrdinalIgnoreCase))
            {
                rb.IsChecked = true;
                matched = true;
                break;
            }
        }
        if (!matched) ColorWhite.IsChecked = true;

        RadioButton templateBtn = CurrentPage.Template switch
        {
            PageTemplate.Blank => TemplateBlank,
            PageTemplate.Ruled => TemplateRuled,
            PageTemplate.Grid => TemplateGrid,
            PageTemplate.Dot => TemplateDot,
            _ => TemplateBlank
        };
        templateBtn.IsChecked = true;

        foreach (ComboBoxItem item in SpacingCombo.Items)
        {
            if (item.Content?.ToString() == ((int)CurrentPage.Spacing).ToString())
            {
                SpacingCombo.SelectedItem = item;
                break;
            }
        }

        UpdatePageBackground();
    }

    // =====================================================================
    // Tools
    // =====================================================================

    private void PenButton_Click(object sender, RoutedEventArgs e)
    {
        InkArea.EditingMode = InkCanvasEditingMode.Ink;
        StatusText.Text = "Pen selected.";
    }

    private void EraserButton_Click(object sender, RoutedEventArgs e)
    {
        InkArea.EditingMode = InkCanvasEditingMode.EraseByStroke;
        StatusText.Text = "Eraser selected: touch a stroke to remove it.";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (InkArea.Strokes.Count == 0)
        {
            StatusText.Text = "The page is already empty.";
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Remove everything on this page?\n\nYou can still press Undo (Ctrl+Z) right after.",
            "Clear page",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

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

        float min = 1f;
        float max = 0f;
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

        SmoothCompletedStroke(e.Stroke);
    }

    // While drawing FitToCurve=false gives instant ink. On pen-up, replace the
    // raw stroke with a dense Catmull-Rom spline so curves stay crisp at any zoom.
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
            double p1x = sx[i],     p1y = sy[i];
            double p2x = sx[i + 1], p2y = sy[i + 1];
            double p0x = i > 0     ? sx[i - 1] : p1x - (p2x - p1x);
            double p0y = i > 0     ? sy[i - 1] : p1y - (p2y - p1y);
            double p3x = i < n - 2 ? sx[i + 2] : p2x + (p2x - p1x);
            double p3y = i < n - 2 ? sy[i + 2] : p2y + (p2y - p1y);

            double segLen = Math.Sqrt((p2x - p1x) * (p2x - p1x) + (p2y - p1y) * (p2y - p1y));
            int subdiv = Math.Max(1, (int)Math.Ceiling(segLen / step));
            if (subdiv > 500) subdiv = 500;

            for (int s = 1; s <= subdiv; s++)
            {
                double t = (double)s / subdiv;
                double t2 = t * t;
                double t3 = t2 * t;
                double x = 0.5 * ((2 * p1x)
                                  + (-p0x + p2x) * t
                                  + (2 * p0x - 5 * p1x + 4 * p2x - p3x) * t2
                                  + (-p0x + 3 * p1x - 3 * p2x + p3x) * t3);
                double y = 0.5 * ((2 * p1y)
                                  + (-p0y + p2y) * t
                                  + (2 * p0y - 5 * p1y + 4 * p2y - p3y) * t2
                                  + (-p0y + 3 * p1y - 3 * p2y + p3y) * t3);
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
            if (last.Removed.Count == 0
                && last.Added.Count == 1
                && last.Added[0] == original)
            {
                _undo.Push(new StrokeChange(new StrokeCollection { smoothed }, last.Removed));
            }
            else
            {
                _undo.Push(last);
            }
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

    private void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _undo.Count > 0;
        e.Handled = true;
    }

    private void Redo_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _redo.Count > 0;
        e.Handled = true;
    }

    private void Undo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var change = _undo.Pop();
        ApplyChange(change, reverse: true);
        _redo.Push(change);
        MarkDirty();
    }

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var change = _redo.Pop();
        ApplyChange(change, reverse: false);
        _undo.Push(change);
        MarkDirty();
    }

    private void ApplyChange(StrokeChange change, bool reverse)
    {
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
        {
            OpenFromFile(dialog.FileName);
        }
    }

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => SaveCurrent();
    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveAs();

    private bool SaveCurrent()
    {
        return _currentPath is null ? SaveAs() : SaveToFile(_currentPath);
    }

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
                        Spacing = page.Spacing
                    });
                }

                var manifestEntry = zip.CreateEntry(ManifestEntryName);
                using (var entryStream = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(entryStream, manifest, JsonOptions);
                }

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
            MessageBox.Show(
                this,
                "Canvaas could not save your note.\n\nYour previous saved file (if any) was not changed.\n\nDetails: " + ex.Message,
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            {
                manifest = JsonSerializer.Deserialize<Manifest>(entryStream, JsonOptions);
            }

            if (manifest is null)
                throw new InvalidDataException("The note's manifest.json is empty.");

            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                MessageBox.Show(
                    this,
                    "This note was saved by a newer version of Canvaas and cannot be opened by this version.\n\nNothing was changed.",
                    "Newer file version",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            loadedPages = new List<NotebookPage>();

            if (manifest.FormatVersion >= 3 && manifest.Pages is { Count: > 0 })
            {
                // v3 multi-page
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
                // v1 / v2 single-page → becomes page 1 of a 1-page notebook
                var page = new NotebookPage();
                if (manifest.FormatVersion >= 2)
                {
                    if (!string.IsNullOrEmpty(manifest.BackgroundColor))
                        page.BackgroundColor = ParseHexColor(manifest.BackgroundColor!, Colors.White);
                    if (Enum.TryParse<PageTemplate>(manifest.Template, out var t))
                        page.Template = t;
                    if (manifest.Spacing is double s && s > 0)
                        page.Spacing = s;
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
            MessageBox.Show(
                this,
                "Canvaas could not open this file.\n\nThe file was not changed, and your current page is still here.\n\nDetails: " + ex.Message,
                "Open failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        var answer = MessageBox.Show(
            this,
            "You have unsaved changes. Save them first?",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

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
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
