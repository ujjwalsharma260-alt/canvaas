using System.Windows;
using System.Windows.Controls;

namespace Canvaas;

public class SimpleInputDialog : Window
{
    public string Result { get; private set; } = "";

    public SimpleInputDialog(string title, string prompt, string defaultValue)
    {
        Title = title;
        Width = 360;
        Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);
        var box = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
        ok.Click += (s, e) => { Result = box.Text; DialogResult = true; };
        cancel.Click += (s, e) => { DialogResult = false; };
        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        Grid.SetRow(panel, 2);
        grid.Children.Add(panel);
        Content = grid;
    }
}