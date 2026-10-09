using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Canvaas;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowCrashMessage(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowCrashMessage(ex);
    }

    private void ShowCrashMessage(Exception ex)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "canvaas_crash.txt");
        try
        {
            File.WriteAllText(logPath,
                $"{DateTime.Now:o}\n\n{ex.GetType().FullName}\n\n{ex.Message}\n\n{ex.StackTrace}\n\n--- Inner ---\n{ex.InnerException}",
                Encoding.UTF8);
        }
        catch { }

        MessageBox.Show(
            $"Canvaas ran into a problem and needs to close.\n\n" +
            $"{ex.GetType().Name}: {ex.Message}\n\n" +
            $"A full report was saved to:\n{logPath}\n\n" +
            $"Please copy the contents of that file and send it.",
            "Canvaas — startup error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}