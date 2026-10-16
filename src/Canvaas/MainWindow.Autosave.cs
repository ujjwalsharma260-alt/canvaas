using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Canvaas;

public partial class MainWindow
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(45);

    private static string AutosaveDir
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Canvaas");

    private static string AutosaveFilePath
        => System.IO.Path.Combine(AutosaveDir, "autosave.canvaas");

    private static string AutosaveTmpPath
        => AutosaveFilePath + ".tmp";

    private static string SessionMarkerPath
        => System.IO.Path.Combine(AutosaveDir, "session.lock");

    private static string AutosaveMetaPath
        => System.IO.Path.Combine(AutosaveDir, "autosave.meta.json");

    private DispatcherTimer? _autosaveTimer;
    private bool _isAutosaving;
    private bool _suppressSaveDialogs;
    private bool _previousSessionCrashed;

    private sealed class AutosaveMeta
    {
        public string? OriginalPath { get; set; }
        public string? SavedAtUtc { get; set; }
    }

    private void InitAutosave()
    {
        try { Directory.CreateDirectory(AutosaveDir); } catch { }

        _autosaveTimer = new DispatcherTimer { Interval = AutosaveInterval };
        _autosaveTimer.Tick += (s, e) => AutosaveTick();
        _autosaveTimer.Start();

        // Capture whether a previous session crashed BEFORE we overwrite the marker.
        _previousSessionCrashed = File.Exists(SessionMarkerPath);
        try { File.WriteAllText(SessionMarkerPath, DateTime.UtcNow.ToString("o")); } catch { }
    }

    private void AutosaveTick()
    {
        if (!_dirty) return;
        if (_isAutosaving) return;
        if (InkArea is null) return;
        if (_collapsedGroups.Count > 0) return;
        AutosaveNow();
    }

    private void AutosaveNow()
    {
        _isAutosaving = true;
        try
        {
            string? oldPath = _currentPath;
            bool oldDirty = _dirty;
            string oldStatus = StatusText?.Text ?? "";

            _suppressSaveDialogs = true;
            try
            {
                bool ok = SaveToFile(AutosaveFilePath);
                if (ok)
                {
                    var meta = new AutosaveMeta
                    {
                        OriginalPath = oldPath,
                        SavedAtUtc = DateTime.UtcNow.ToString("o")
                    };
                    File.WriteAllText(AutosaveMetaPath, JsonSerializer.Serialize(meta, JsonOptions));
                }
            }
            finally
            {
                _suppressSaveDialogs = false;
                _currentPath = oldPath;
                _dirty = oldDirty;
                UpdateTitle();
                if (StatusText is not null) StatusText.Text = oldStatus;
            }
        }
        catch { }
        finally
        {
            _isAutosaving = false;
        }
    }

    private void CleanupAutosave()
    {
        try { _autosaveTimer?.Stop(); } catch { }
        TryDeleteFile(SessionMarkerPath);
        TryDeleteFile(AutosaveFilePath);
        TryDeleteFile(AutosaveTmpPath);
        TryDeleteFile(AutosaveMetaPath);
    }

    private void CheckForCrashRecovery()
    {
        try
        {
            if (!_previousSessionCrashed) return;

            bool mainExists = File.Exists(AutosaveFilePath);
            bool tmpExists = File.Exists(AutosaveTmpPath);
            if (!mainExists && !tmpExists) return;

            // Prefer the main autosave if its ZIP is intact; otherwise fall back to the .tmp.
            string? usable = null;
            if (mainExists && IsReadableZip(AutosaveFilePath)) usable = AutosaveFilePath;
            else if (tmpExists && IsReadableZip(AutosaveTmpPath)) usable = AutosaveTmpPath;
            else if (mainExists) usable = AutosaveFilePath;
            else usable = AutosaveTmpPath;

            string when = "earlier";
            string? original = null;
            try
            {
                if (File.Exists(AutosaveMetaPath))
                {
                    var meta = JsonSerializer.Deserialize<AutosaveMeta>(
                        File.ReadAllText(AutosaveMetaPath), JsonOptions);
                    if (meta is not null)
                    {
                        original = meta.OriginalPath;
                        if (DateTime.TryParse(meta.SavedAtUtc, out var dt))
                            when = dt.ToLocalTime().ToString("MMMM d, yyyy 'at' h:mm tt");
                    }
                }
            }
            catch { }

            string fileLabel = string.IsNullOrEmpty(original)
                ? "an unsaved notebook"
                : System.IO.Path.GetFileName(original);

            var ans = MessageBox.Show(this,
                "Canvaas found unsaved work from a session that ended unexpectedly.\n\n" +
                "File: " + fileLabel + "\n" +
                "Autosaved: " + when + "\n\n" +
                "Do you want to recover it?",
                "Recover unsaved work",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (ans == MessageBoxResult.Yes && usable is not null)
            {
                bool ok = OpenFromFile(usable);
                if (ok)
                {
                    _currentPath = original;
                    _dirty = true;
                    UpdateTitle();
                    StatusText.Text = "Recovered autosave. Press Ctrl+S to save it to a file.";
                    // Only clean up after a successful recovery.
                    TryDeleteFile(AutosaveFilePath);
                    TryDeleteFile(AutosaveTmpPath);
                    TryDeleteFile(AutosaveMetaPath);
                }
                else
                {
                    // Keep the files so the user can try to salvage them manually.
                    StatusText.Text = "Recovery failed. Autosave files kept for manual recovery.";
                    MessageBox.Show(this,
                        "The autosave file could not be opened. It has been kept at:\n\n" +
                        AutosaveDir + "\n\n" +
                        "You can try renaming autosave.canvaas.tmp to autosave.canvaas and opening it manually.",
                        "Recovery failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else if (ans == MessageBoxResult.No)
            {
                TryDeleteFile(AutosaveFilePath);
                TryDeleteFile(AutosaveTmpPath);
                TryDeleteFile(AutosaveMetaPath);
            }
        }
        catch { }
    }

    private static bool IsReadableZip(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return zip.Entries.Count > 0;
        }
        catch { return false; }
    }

    internal static void ForceFlushToDisk(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Flush(true);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}