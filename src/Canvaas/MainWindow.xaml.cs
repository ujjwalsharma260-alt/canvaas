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
    private const int CurrentFormatVersion = 2;
    private const string FileExtension = ".canvaas";
    private const string FileFilter = "Canvaas note (*.canvaas)|*.canvaas";
    private const string ManifestEntryName = "manifest.json";
    private const string InkEntryName = "ink.isf";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class Manifest
    {
        public int FormatVersion { get; set; }
        public string App { get; set; } = "Canvaas";
        public string AppVersion { get; set; } = "";
        public string SavedAtUtc { get; set; } = "";
        public string? BackgroundColor { get; set; }
        public string? Template { get; set; }
        public double? Spacing { get; set; }
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

    private readonly Stack<StrokeChange> _undo = new();
    private readonly Stack<StrokeChange> _redo = new();
    private bool _applyingHistory;

    private string? _currentPath;
    private bool _dirty;

    // Current page style
    private Color _backgroundColor = Colors.White;
    private PageTemplate _template = PageTemplate.Blank;
    private double _spacing = 40;

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

        InkArea.Strokes.StrokesChanged += Strokes_Changed;

        PenButton.IsChecked = true;
        InkArea.EditingMode = InkCanvasEditingMode.Ink;
        ColorWhite.IsChecked = true;
        TemplateBlank.IsChecked = true;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Canvaas v{version.Major}.{version.Minor}.{version.Build}";

        UpdatePageBackground();
        UpdateTitle();
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
            _backgroundColor = ParseHexColor(hex, Colors.White);
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
            _template = t;
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
            _spacing = s;
            UpdatePageBackground();
            MarkDirty();
        }
    }

    private void UpdatePageBackground()
    {
        PageBackground.Fill = BuildPageBrush(_backgroundColor, _template, _spacing);
    }

    private static Brush BuildPageBrush(Color baseColor, PageTemplate template, double spacing)
    {
        if (template == PageTemplate.Blank || spacing <= 0)
        {
            return new SolidColorBrush(baseColor);
        }

        var group = new DrawingGroup();

        // Base fill for the tile
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

    // Sync the panel UI to reflect _backgroundColor / _template / _spacing
    private void SyncUIWithSettings()
    {
        string hex = $"#{_backgroundColor.R:X2}{_backgroundColor.G:X2}{_backgroundColor.B:X2}";

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

        RadioButton templateBtn = _template switch
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
            if (item.Content?.ToString() == ((int)_spacing).ToString())
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

    private void ReplaceStrokes(StrokeCollection strokes)
    {
        InkArea.Strokes.StrokesChanged -= Strokes_Changed;
        InkArea.Strokes = strokes;
        InkArea.Strokes.StrokesChanged += Strokes_Changed;

        _undo.Clear();
        _redo.Clear();
        CommandManager.InvalidateRequerySuggested();
    }

    // =====================================================================
    // New / Open / Save
    // =====================================================================

    private void New_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        ReplaceStrokes(new StrokeCollection());
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
                    BackgroundColor = $"#{_backgroundColor.R:X2}{_backgroundColor.G:X2}{_backgroundColor.B:X2}",
                    Template = _template.ToString(),
                    Spacing = _spacing
                };

                var manifestEntry = zip.CreateEntry(ManifestEntryName);
                using (var entryStream = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(entryStream, manifest, JsonOptions);
                }

                if (InkArea.Strokes.Count > 0)
                {
                    using var inkBuffer = new MemoryStream();
                    InkArea.Strokes.Save(inkBuffer);
                    inkBuffer.Position = 0;

                    var inkEntry = zip.CreateEntry(InkEntryName);
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
        StrokeCollection loaded;
        Color loadedBg = Colors.White;
        PageTemplate loadedTemplate = PageTemplate.Blank;
        double loadedSpacing = 40;

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

            // Page style is only present in v2+ files. v1 files get defaults.
            if (manifest.FormatVersion >= 2)
            {
                if (!string.IsNullOrEmpty(manifest.BackgroundColor))
                    loadedBg = ParseHexColor(manifest.BackgroundColor!, Colors.White);

                if (Enum.TryParse<PageTemplate>(manifest.Template, out var t))
                    loadedTemplate = t;

                if (manifest.Spacing is double s && s > 0)
                    loadedSpacing = s;
            }

            var inkEntry = zip.GetEntry(InkEntryName);
            if (inkEntry is null)
            {
                loaded = new StrokeCollection();
            }
            else
            {
                using var buffer = new MemoryStream();
                using (var entryStream = inkEntry.Open())
                {
                    entryStream.CopyTo(buffer);
                }
                buffer.Position = 0;
                loaded = new StrokeCollection(buffer);
            }
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

        _backgroundColor = loadedBg;
        _template = loadedTemplate;
        _spacing = loadedSpacing;

        ReplaceStrokes(loaded);
        SyncUIWithSettings();

        _currentPath = path;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = "Opened: " + path;
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
        Title = $"{name}{(_dirty ? " *" : "")} - Canvaas";
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
