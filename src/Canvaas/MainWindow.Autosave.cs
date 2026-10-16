using System.IO;
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

    private static string SessionMarkerPath
        => System.IO.Path.Combine(AutosaveDir, "session.lock");

    private static string AutosaveMetaPath
        => System.IO.Path.Combine(AutosaveDir, "autosave.meta.json");

    private DispatcherTimer? _autosaveTimer;
    private bool _isAutosaving;
    private bool _suppressSaveDialogs;

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

        // Mark this session as in-progress. If this file still exists at next
        // launch, we know the previous session ended unexpectedly.
        try { File.WriteAllText(SessionMarkerPath, DateTime.UtcNow.ToString("o")); } catch { }
    }

    private void AutosaveTick()
    {
        if (!_dirty) return;
        if (_isAutosaving) return;
        if (InkArea is null) return;
        if (_collapsedGroups.Count > 0) return; // avoid auto-expanding user chips
        AutosaveNow();
    }

    private void AutosaveNow()
    {
        _isAutosaving = true;
        try
        {
            // Mask the app's real file state so autosave never hijacks it.
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
        catch
        {
            // A failed autosave must never block the user.
        }
        finally
        {
            _isAutosaving = false;
        }
    }

    private void CleanupAutosave()
    {
        try { _autosaveTimer?.Stop(); } catch { }
        try { if (File.Exists(SessionMarkerPath)) File.Delete(SessionMarkerPath); } catch { }
        try { if (File.Exists(AutosaveFilePath)) File.Delete(AutosaveFilePath); } catch { }
        try { if (File.Exists(AutosaveMetaPath)) File.Delete(AutosaveMetaPath); } catch { }
    }

    private void CheckForCrashRecovery()
    {
        try
        {
            bool previousSessionCrashed = File.Exists(SessionMarkerPath);
            bool autosaveExists = File.Exists(AutosaveFilePath);
            if (!previousSessionCrashed || !autosaveExists) return;

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

            if (ans == MessageBoxResult.Yes)
            {
                OpenFromFile(AutosaveFilePath);
                _currentPath = original;
                _dirty = true;
                UpdateTitle();
                StatusText.Text = "Recovered autosave. Press Ctrl+S to save it to a file.";
            }

            // Consume the crash artifacts either way so we don't ask again.
            try { File.Delete(AutosaveFilePath); } catch { }
            try { File.Delete(AutosaveMetaPath); } catch { }
        }
        catch { }
    }
}