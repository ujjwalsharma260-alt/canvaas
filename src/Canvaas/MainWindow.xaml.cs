using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Canvaas;

/// <summary>
/// The main window: a white page you can write on, with pen, eraser,
/// undo/redo, clear, and save/open. See docs/FILE_FORMAT.md for the file format.
/// </summary>
public partial class MainWindow : Window
{
    // ---- Save-file format (see docs/FILE_FORMAT.md) ----
    private const int CurrentFormatVersion = 1;
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
    }

    // ---- Undo / redo history ----
    // Every change to the strokes is stored as "what was added, what was removed".
    private sealed class StrokeChange
    {
        public StrokeCollection Added { get; }
        public StrokeCollection Removed { get; }

        public StrokeChange(StrokeCollection added, StrokeCollection removed)
        {
            Added = added;
            Removed = removed;
        }
    }

    private readonly Stack<StrokeChange> _undo = new();
    private readonly Stack<StrokeChange> _redo = new();
    private bool _applyingHistory;   // true while undo/redo is changing the strokes

    // ---- Document state ----
    private string? _currentPath;    // null = not saved yet
    private bool _dirty;             // true = there are unsaved changes

    public MainWindow()
    {
        InitializeComponent();

        InkArea.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black,
            Width = 2.5,
            Height = 2.5,
            FitToCurve = true,
            IgnorePressure = false   // use pen pressure when the pen provides it
        };

        InkArea.Strokes.StrokesChanged += Strokes_Changed;

        PenButton.IsChecked = true;
        InkArea.EditingMode = InkCanvasEditingMode.Ink;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Canvaas v{version.Major}.{version.Minor}.{version.Build}";

        UpdateTitle();
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
            InkArea.Strokes.Clear();   // recorded in undo history by Strokes_Changed
            StatusText.Text = "Page cleared.";
        }
    }

    // Shows what the pen sent for the last stroke. Useful to test pressure on a tablet.
    private void InkArea_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        var points = e.Stroke.StylusPoints;
        if (points.Count == 0)
        {
            return;
        }

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
        if (_applyingHistory)
        {
            return;
        }

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
        if (_undo.Count == 0)
        {
            return;
        }

        var change = _undo.Pop();
        ApplyChange(change, reverse: true);
        _redo.Push(change);
        MarkDirty();
    }

    private void Redo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var change = _redo.Pop();
        ApplyChange(change, reverse: false);
        _undo.Push(change);
        MarkDirty();
    }

    // reverse = true  -> undo the change (remove what was added, put back what was removed)
    // reverse = false -> redo the change
    private void ApplyChange(StrokeChange change, bool reverse)
    {
        var toRemove = reverse ? change.Added : change.Removed;
        var toAdd = reverse ? change.Removed : change.Added;

        _applyingHistory = true;
        try
        {
            if (toRemove.Count > 0)
            {
                InkArea.Strokes.Remove(toRemove);
            }

            if (toAdd.Count > 0)
            {
                InkArea.Strokes.Add(toAdd);
            }
        }
        finally
        {
            _applyingHistory = false;
        }
    }

    // Swap in a whole new set of strokes (used by New and Open) and forget the history.
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
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        ReplaceStrokes(new StrokeCollection());
        _currentPath = null;
        _dirty = false;
        UpdateTitle();
        StatusText.Text = "New blank note.";
    }

    private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

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

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SaveCurrent();
    }

    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SaveAs();
    }

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

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        return SaveToFile(dialog.FileName);
    }

    // Saves to a temporary file first, then swaps it in. A failed save can never
    // destroy an older good copy of the note.
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
                    SavedAtUtc = DateTime.UtcNow.ToString("o")
                };

                var manifestEntry = zip.CreateEntry(ManifestEntryName);
                using (var entryStream = manifestEntry.Open())
                {
                    JsonSerializer.Serialize(entryStream, manifest, JsonOptions);
                }

                // An empty page has no ink file; opening handles that.
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
            {
                throw new InvalidDataException("The note's manifest.json is empty.");
            }

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

            var inkEntry = zip.GetEntry(InkEntryName);
            if (inkEntry is null)
            {
                loaded = new StrokeCollection();   // saved empty page
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

        ReplaceStrokes(loaded);
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
        {
            e.Cancel = true;
        }
    }

    // Returns true if it is safe to continue (nothing unsaved, or the user chose Save / Don't save).
    private bool ConfirmDiscardChanges()
    {
        if (!_dirty)
        {
            return true;
        }

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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Nothing more we can do; ignore.
        }
    }
}
