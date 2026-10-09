using System.Reflection;
using System.Windows;

namespace Canvaas;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Version unknown" : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }
}
