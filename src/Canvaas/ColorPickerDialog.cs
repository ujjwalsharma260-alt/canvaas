using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Canvaas;

public class ColorPickerDialog : Window
{
    public Color Result { get; private set; } = Colors.Black;

    private readonly Slider _r, _g, _b;
    private readonly TextBox _hex;
    private readonly Border _preview;
    private bool _updating;

    public ColorPickerDialog(Color initial)
    {
        Title = "Pick a colour";
        Width = 360;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(12) };
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        _preview = new Border { Width = 40, Height = 40, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(_preview, 0); Grid.SetColumn(_preview, 2);
        grid.Children.Add(_preview);

        var hexLabel = new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hexLabel, 0); Grid.SetColumn(hexLabel, 0);
        grid.Children.Add(hexLabel);
        _hex = new TextBox { Text = $"#{initial.R:X2}{initial.G:X2}{initial.B:X2}", Margin = new Thickness(4, 0, 4, 8) };
        _hex.TextChanged += Hex_TextChanged;
        Grid.SetRow(_hex, 0); Grid.SetColumn(_hex, 1);
        grid.Children.Add(_hex);

        _r = MakeSlider("R", initial.R, 1, grid);
        _g = MakeSlider("G", initial.G, 2, grid);
        _b = MakeSlider("B", initial.B, 3, grid);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
        ok.Click += (s, e) => { Result = Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value); DialogResult = true; };
        cancel.Click += (s, e) => { DialogResult = false; };
        panel.Children.Add(ok); panel.Children.Add(cancel);
        Grid.SetRow(panel, 4); Grid.SetColumnSpan(panel, 3);
        grid.Children.Add(panel);

        Content = grid;
        UpdatePreview();
    }

    private Slider MakeSlider(string label, byte value, int row, Grid grid)
    {
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);
        var slider = new Slider { Minimum = 0, Maximum = 255, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(4, 0, 4, 4) };
        slider.ValueChanged += (s, e) => Slider_ValueChanged();
        Grid.SetRow(slider, row); Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        var val = new TextBlock { Text = value.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetRow(val, row); Grid.SetColumn(val, 2);
        grid.Children.Add(val);
        slider.Tag = val;
        return slider;
    }

    private void Slider_ValueChanged()
    {
        if (_updating) return;
        _updating = true;
        if (_r.Tag is TextBlock rt) rt.Text = ((int)_r.Value).ToString();
        if (_g.Tag is TextBlock gt) gt.Text = ((int)_g.Value).ToString();
        if (_b.Tag is TextBlock bt) bt.Text = ((int)_b.Value).ToString();
        _hex.Text = $"#{(int)_r.Value:X2}{(int)_g.Value:X2}{(int)_b.Value:X2}";
        UpdatePreview();
        _updating = false;
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        string hex = _hex.Text.Trim();
        if (hex.StartsWith("#") && (hex.Length == 7))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                _updating = true;
                _r.Value = c.R; _g.Value = c.G; _b.Value = c.B;
                if (_r.Tag is TextBlock rt) rt.Text = c.R.ToString();
                if (_g.Tag is TextBlock gt) gt.Text = c.G.ToString();
                if (_b.Tag is TextBlock bt) bt.Text = c.B.ToString();
                UpdatePreview();
                _updating = false;
            }
            catch { }
        }
    }

    private void UpdatePreview()
    {
        _preview.Background = new SolidColorBrush(Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value));
    }
}