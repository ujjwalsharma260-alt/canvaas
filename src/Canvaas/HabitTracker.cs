using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Canvaas;

public sealed class HabitItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int Goal { get; set; } = 15;
    public string Color { get; set; } = "#2FE0B0";
}

public sealed class HabitDb
{
    public List<HabitItem> Habits { get; set; } = new();
    public Dictionary<string, List<int>> Checks { get; set; } = new();
}

public sealed class HabitTrackerWindow : Window
{
    private static readonly string DbPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Canvaas", "habits.json");

    private static readonly string[] Palette =
    {
        "#2FE0B0", "#1EA7E1", "#6C5BD1", "#FFB000", "#FF5A5F",
        "#43C59E", "#4DB6E6", "#8E6BD8", "#F4A261", "#EF476F"
    };

    private const double DayW = 28, RowH = 30, NameW = 190, GoalW = 52, ProgW = 150;

    private readonly Brush _page = new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B));
    private readonly Brush _white = Brushes.White;
    private readonly Brush _red = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private readonly Brush _cyan = new SolidColorBrush(Color.FromRgb(0x2B, 0xD9, 0xE0));
    private readonly Brush _grey = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

    private HabitDb _db = new();
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;
    private bool _yearView;

    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock _monthLabel = new()
    {
        Foreground = Brushes.White,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        MinWidth = 170,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };

    public HabitTrackerWindow(Window owner)
    {
        Owner = owner;
        Title = "Habit Tracker";
        WindowState = WindowState.Maximized;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        LoadDb();

        var root = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        DockPanel.SetDock(bar, Dock.Top);
        bar.Children.Add(MakeButton("\u25C0", (s, e) => Shift(-1)));
        bar.Children.Add(_monthLabel);
        bar.Children.Add(MakeButton("\u25B6", (s, e) => Shift(1)));
        bar.Children.Add(MakeButton("Today", (s, e) =>
        {
            _year = DateTime.Today.Year;
            _month = DateTime.Today.Month;
            Refresh();
        }));
        bar.Children.Add(MakeButton("Month", (s, e) => { _yearView = false; Refresh(); }));
        bar.Children.Add(MakeButton("Year", (s, e) => { _yearView = true; Refresh(); }));
        bar.Children.Add(MakeButton("\u2190 Back to Canvas", (s, e) =>
        {
            if (Owner is Window owner)
            {
                owner.Opacity = 0;
                owner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
                {
                    BeginTime = TimeSpan.FromMilliseconds(130),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }));
        root.Children.Add(bar);
        root.Children.Add(_scroll);
        Content = root;
        Refresh();
    }

    // ------------------------------------------------------------------ data
    private void LoadDb()
    {
        try
        {
            if (File.Exists(DbPath))
            {
                var loaded = JsonSerializer.Deserialize<HabitDb>(File.ReadAllText(DbPath));
                if (loaded is not null) _db = loaded;
            }
        }
        catch { }
    }

    private void SaveDb()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DbPath, JsonSerializer.Serialize(_db, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string Key(HabitItem h, int y, int m) => y.ToString("D4") + "-" + m.ToString("D2") + "|" + h.Id;

    private List<int> DaysFor(HabitItem h, int y, int m)
    {
        int dim = DateTime.DaysInMonth(y, m);
        if (_db.Checks.TryGetValue(Key(h, y, m), out var l))
            return l.Where(d => d >= 1 && d <= dim).Distinct().ToList();
        return new List<int>();
    }

    private void Toggle(HabitItem h, int d)
    {
        string k = Key(h, _year, _month);
        if (!_db.Checks.TryGetValue(k, out var l))
        {
            l = new List<int>();
            _db.Checks[k] = l;
        }
        if (!l.Remove(d)) l.Add(d);
        SaveDb();
    }

    private void DeleteHabit(HabitItem h)
    {
        _db.Habits.Remove(h);
        string suffix = "|" + h.Id;
        foreach (var k in _db.Checks.Keys.Where(x => x.EndsWith(suffix)).ToList()) _db.Checks.Remove(k);
        SaveDb();
        Refresh();
    }

    private void OnNameEdited(HabitItem? h, string? text)
    {
        text = (text ?? "").Trim();
        if (h != null)
        {
            if (text.Length > 0 && text != h.Name)
            {
                h.Name = text;
                SaveDb();
            }
            else if (text.Length == 0)
            {
                Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
            }
            return;
        }
        if (text.Length == 0) return;
        var item = new HabitItem { Name = text, Goal = 15, Color = Palette[_db.Habits.Count % Palette.Length] };
        _db.Habits.Add(item);
        SaveDb();
        Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnGoalEdited(HabitItem h, string? text)
    {
        if (int.TryParse((text ?? "").Trim(), out var g))
        {
            h.Goal = Math.Clamp(g, 1, 31);
            SaveDb();
        }
        Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ helpers
    private static Color Hex(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch { return Colors.Gray; }
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 40,
            Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            Cursor = Cursors.Hand
        };
        b.Click += click;
        return b;
    }

    private static TextBlock Txt(string text, double size, bool bold, Brush fg)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static void Place(Grid g, UIElement e, int row, int col, int rowSpan = 1, int colSpan = 1)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        if (rowSpan > 1) Grid.SetRowSpan(e, rowSpan);
        if (colSpan > 1) Grid.SetColumnSpan(e, colSpan);
        g.Children.Add(e);
    }

    private void Shift(int d)
    {
        if (_yearView) _year += d;
        else
        {
            var dt = new DateTime(_year, _month, 1).AddMonths(d);
            _year = dt.Year;
            _month = dt.Month;
        }
        Refresh();
    }

    private void Refresh()
    {
        _monthLabel.Text = _yearView
            ? _year.ToString()
            : new DateTime(_year, _month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        double ox = _scroll.HorizontalOffset, oy = _scroll.VerticalOffset;
        _scroll.Content = _yearView ? BuildYearView() : BuildMonthView();
        _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(ox);
        _scroll.ScrollToVerticalOffset(oy);
    }

    private static FrameworkElement Donut(double pct, Color color, double size)
    {
        pct = Math.Max(0, Math.Min(100, pct));
        var c = new Canvas { Width = size, Height = size };
        double th = size * 0.17, r = (size - th) / 2.0, cx = size / 2.0, cy = size / 2.0;
        var track = new Ellipse
        {
            Width = size - th,
            Height = size - th,
            Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            StrokeThickness = th
        };
        Canvas.SetLeft(track, th / 2.0);
        Canvas.SetTop(track, th / 2.0);
        c.Children.Add(track);
        if (pct > 0)
        {
            double angle = Math.Min(pct, 99.99) / 100.0 * 2.0 * Math.PI;
            var start = new Point(cx, cy - r);
            var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            var arc = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = th,
                Data = new PathGeometry(new[] { fig })
            };
            c.Children.Add(arc);
        }
        var label = new TextBlock
        {
            Text = pct.ToString("0.0") + "%",
            Width = size,
            TextAlignment = TextAlignment.Center,
            FontSize = size * 0.14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color)
        };
        Canvas.SetLeft(label, 0);
        Canvas.SetTop(label, size / 2.0 - size * 0.09);
        c.Children.Add(label);
        return c;
    }

    private FrameworkElement DonutBlock(double pct, string colorHex, string caption)
    {
        var color = Hex(colorHex);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(Donut(pct, color, 130));
        var t = new TextBlock
        {
            Text = caption,
            Width = 110,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        sp.Children.Add(t);
        return sp;
    }

    // ------------------------------------------------------------------ month view
    private UIElement BuildMonthView()
    {
        int dim = DateTime.DaysInMonth(_year, _month);
        var habits = _db.Habits;
        int rows = Math.Max(12, habits.Count + 1);
        var g = new Grid { Margin = new Thickness(16), Background = _page, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameW) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GoalW) });
        for (int d = 1; d <= 31; d++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(d <= dim ? DayW : 0) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ProgW) });
        const int ProgCol = 33;

        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        for (int i = 0; i < rows; i++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowH) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        int rBars = 2 + rows, rDonut = rBars + 1;

        // ---- statistics
        var perDay = new int[dim + 1];
        int totalChecks = 0, totalGoal = 0, cappedSum = 0;
        double pctSum = 0;
        var pcts = new double[habits.Count];
        for (int i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            var days = DaysFor(h, _year, _month);
            foreach (var d in days) perDay[d]++;
            totalChecks += days.Count;
            totalGoal += h.Goal;
            cappedSum += Math.Min(days.Count, h.Goal);
            pcts[i] = h.Goal > 0 ? Math.Min(100.0, days.Count * 100.0 / h.Goal) : 0;
            pctSum += pcts[i];
        }
        double success = habits.Count > 0 ? pctSum / habits.Count : 0;
        double monthly = totalGoal > 0 ? cappedSum * 100.0 / totalGoal : 0;

        var today = DateTime.Today;
        int elapsed = (today.Year == _year && today.Month == _month)
            ? today.Day
            : (new DateTime(_year, _month, 1) < new DateTime(today.Year, today.Month, 1) ? dim : 0);
        double expected = totalGoal * (double)elapsed / dim;
        int doneToDate = 0;
        for (int d = 1; d <= elapsed && d <= dim; d++) doneToDate += perDay[d];
        double normalized = expected > 0 ? Math.Min(100.0, doneToDate * 100.0 / expected) : 0;
        int mEnd = elapsed, mStart = Math.Max(1, mEnd - 2);
        double momentum = 0;
        if (mEnd >= 1 && habits.Count > 0)
        {
            int sum = 0;
            for (int d = mStart; d <= mEnd; d++) sum += perDay[d];
            momentum = sum * 100.0 / (habits.Count * (mEnd - mStart + 1));
        }
        int maxY = Math.Max(1, perDay.Skip(1).Max());

        // ---- info block
        var info = new Grid { Margin = new Thickness(8, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string[] labels = { "Year:", "Month:", "Start Date:", "End Date:", "Total Days:" };
        string[] values =
        {
            _year.ToString(),
            new DateTime(_year, _month, 1).ToString("MMMM", CultureInfo.InvariantCulture),
            new DateTime(_year, _month, 1).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
            new DateTime(_year, _month, dim).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
            dim.ToString()
        };
        for (int i = 0; i < 5; i++)
        {
            info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var a = Txt(labels[i], 12, true, _white);
            Grid.SetRow(a, i);
            var b = Txt(values[i], 12, false, _white);
            Grid.SetRow(b, i);
            Grid.SetColumn(b, 1);
            info.Children.Add(a);
            info.Children.Add(b);
        }
        Place(g, info, 0, 0, 1, 2);

        // ---- daily line graph
        double cw = dim * DayW, ch = 140;
        var chart = new Canvas
        {
            Width = cw,
            Height = ch,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0)
        };
        for (int i = 0; i <= 4; i++)
        {
            double y = 24 + i * (ch - 44) / 4.0;
            chart.Children.Add(new Line
            {
                X1 = 0, X2 = cw, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                StrokeThickness = 0.6
            });
        }
        var pts = new List<Point>();
        for (int d = 1; d <= dim; d++)
        {
            double x = (d - 0.5) * DayW;
            double y = 24 + (ch - 44) * (1 - perDay[d] / (double)maxY);
            pts.Add(new Point(x, y));
            var lab = Txt(perDay[d].ToString(), 10, false, _white);
            lab.Width = DayW;
            lab.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(lab, x - DayW / 2.0);
            Canvas.SetTop(lab, y - 18);
            chart.Children.Add(lab);
        }
        var lineGeo = new PathGeometry();
        if (pts.Count > 0)
        {
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = i == 0 ? pts[0] : pts[i - 1];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = i + 2 < pts.Count ? pts[i + 2] : pts[i + 1];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
            lineGeo.Figures.Add(fig);
        }
        var smoothLine = new System.Windows.Shapes.Path
        {
            Stroke = _cyan,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = lineGeo,
            StrokeDashArray = new DoubleCollection { 4000 },
            StrokeDashOffset = 4000
        };
        chart.Children.Add(smoothLine);
        var drawAnim = new DoubleAnimation(4000, 0, TimeSpan.FromMilliseconds(1100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        smoothLine.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, drawAnim);
        Place(g, chart, 0, 2, 1, 31);

        // ---- success rate
        var sp = new StackPanel { Margin = new Thickness(8, 8, 8, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var s1 = Txt("SUCCESS RATE", 12, true, _red);
        s1.HorizontalAlignment = HorizontalAlignment.Center;
        var s2 = Txt(success.ToString("0") + "%", 40, true, _white);
        s2.HorizontalAlignment = HorizontalAlignment.Center;
        var s3 = Txt("\u2665  \u2665", 18, false, _red);
        s3.FontFamily = new FontFamily("Segoe UI Symbol");
        s3.HorizontalAlignment = HorizontalAlignment.Center;
        string msg = success >= 80 ? "GOOD JOB" : (success >= 50 ? "KEEP GOING" : "LET'S START");
        var s4 = Txt(msg, 12, true, _red);
        s4.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(s1);
        sp.Children.Add(s2);
        sp.Children.Add(s3);
        sp.Children.Add(s4);
        Place(g, sp, 0, ProgCol);

        // ---- column headers
        var hHabit = Txt("HABIT", 11, true, _white);
        hHabit.Margin = new Thickness(8, 0, 0, 0);
        Place(g, hHabit, 1, 0);
        var hGoal = Txt("GOAL", 11, true, _white);
        hGoal.HorizontalAlignment = HorizontalAlignment.Center;
        Place(g, hGoal, 1, 1);
        for (int d = 1; d <= dim; d++)
        {
            int dow = (int)new DateTime(_year, _month, d).DayOfWeek;
            bool weekend = dow == 0 || dow == 6;
            var cell = new Border
            {
                Background = new SolidColorBrush(weekend ? Color.FromRgb(0xDD, 0xDD, 0xDD) : Colors.White),
                Margin = new Thickness(1, 6, 1, 6),
                CornerRadius = new CornerRadius(2)
            };
            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var n1 = Txt(d.ToString(), 11, true, Brushes.Black);
            n1.HorizontalAlignment = HorizontalAlignment.Center;
            var n2 = Txt("SMTWTFS"[dow].ToString(), 9, false, Brushes.Black);
            n2.HorizontalAlignment = HorizontalAlignment.Center;
            st.Children.Add(n1);
            st.Children.Add(n2);
            cell.Child = st;
            Place(g, cell, 1, 1 + d);
        }
        var hProg = Txt("PROGRESS", 11, true, _white);
        hProg.HorizontalAlignment = HorizontalAlignment.Center;
        Place(g, hProg, 1, ProgCol);

        // ---- habit rows
        for (int i = 0; i < rows; i++)
        {
            int row = 2 + i;
            HabitItem? h = i < habits.Count ? habits[i] : null;

            var nameBox = new TextBox
            {
                Text = h != null ? h.Name : "",
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Black,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 4, 0)
            };
            var nameCell = new Border { Background = Brushes.White, Margin = new Thickness(1), Child = nameBox };
            HabitItem? captured = h;
            nameBox.LostFocus += (s, e) => OnNameEdited(captured, nameBox.Text);

            if (h != null)
            {
                var hh = h;
                var cm = new ContextMenu();
                var del = new MenuItem { Header = "Delete habit" };
                del.Click += (s, e) =>
                {
                    var ans = MessageBox.Show(this, "Delete \"" + hh.Name + "\" and all its marks?", "Delete habit",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ans == MessageBoxResult.OK) DeleteHabit(hh);
                };
                cm.Items.Add(del);
                var colMenu = new MenuItem { Header = "Change colour" };
                foreach (var hex in Palette)
                {
                    string chosen = hex;
                    var ci = new MenuItem
                    {
                        Header = chosen,
                        Icon = new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(Hex(chosen)) }
                    };
                    ci.Click += (s, e) =>
                    {
                        hh.Color = chosen;
                        SaveDb();
                        Refresh();
                    };
                    colMenu.Items.Add(ci);
                }
                cm.Items.Add(colMenu);
                nameBox.ContextMenu = cm;
                nameCell.ContextMenu = cm;
            }
            Place(g, nameCell, row, 0);

            var goalBox = new TextBox
            {
                Text = h != null ? h.Goal.ToString() : "",
                BorderThickness = new Thickness(0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsEnabled = h != null,
                Margin = new Thickness(1)
            };
            if (h != null)
            {
                var hg = h;
                goalBox.LostFocus += (s, e) => OnGoalEdited(hg, goalBox.Text);
            }
            Place(g, goalBox, row, 1);

            var days = h != null ? DaysFor(h, _year, _month) : new List<int>();
            Color hc = h != null ? Hex(h.Color) : Colors.White;
            for (int d = 1; d <= dim; d++)
            {
                bool on = days.Contains(d);
                var cell = new Border
                {
                    Background = on ? new SolidColorBrush(hc) : Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    BorderThickness = new Thickness(0.6),
                    Margin = new Thickness(1.5),
                    Cursor = h != null ? Cursors.Hand : Cursors.Arrow
                };
                if (on)
                {
                    cell.Child = new TextBlock
                    {
                        Text = "X",
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                if (h != null)
                {
                    var hc2 = h;
                    int dd = d;
                    cell.MouseLeftButtonDown += (s, e) =>
                    {
                        Toggle(hc2, dd);
                        Refresh();
                    };
                }
                Place(g, cell, row, 1 + d);
            }

            double pct = h != null ? pcts[i] : 0;
            var prog = new Grid { Margin = new Thickness(4, 5, 8, 5) };
            prog.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            prog.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var pt = Txt(pct.ToString("0") + "%", 11, true, _white);
            pt.HorizontalAlignment = HorizontalAlignment.Right;
            var track = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(6, 2, 0, 2)
            };
            track.Child = new Border
            {
                Background = new SolidColorBrush(hc),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(0, 88 * pct / 100.0)
            };
            Grid.SetColumn(track, 1);
            prog.Children.Add(pt);
            prog.Children.Add(track);
            Place(g, prog, row, ProgCol);
        }

        // ---- bottom: how-to text, daily bars, completed counter
        var howTo = new TextBlock
        {
            Text = "Type a habit name in a white box and set its monthly goal. Click a day to mark it done. Right-click a habit to delete it or change its colour.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9.5,
            Foreground = _grey,
            Margin = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Top
        };
        Place(g, howTo, rBars, 0, 1, 2);

        var bars = new Canvas { Width = cw, Height = 100, HorizontalAlignment = HorizontalAlignment.Left };
        for (int d = 1; d <= dim; d++)
        {
            double bh = perDay[d] / (double)maxY * 60.0;
            var rect = new Rectangle
            {
                Width = 14,
                Height = Math.Max(0.1, bh),
                Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xE8, 0xB0)),
                RenderTransformOrigin = new Point(0.5, 1.0),
                RenderTransform = new ScaleTransform(1, 0)
            };
            Canvas.SetLeft(rect, (d - 0.5) * DayW - 7);
            Canvas.SetTop(rect, 70 - bh);
            bars.Children.Add(rect);
            var st = (ScaleTransform)rect.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                BeginTime = TimeSpan.FromMilliseconds(d * 22),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            var lab = Txt(perDay[d].ToString(), 10, false, _white);
            lab.Width = DayW;
            lab.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(lab, (d - 0.5) * DayW - DayW / 2.0);
            Canvas.SetTop(lab, 74);
            bars.Children.Add(lab);
        }
        Place(g, bars, rBars, 2, 1, 31);

        var done = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var d1 = Txt(totalChecks + "/" + totalGoal, 26, true, _white);
        d1.HorizontalAlignment = HorizontalAlignment.Center;
        var d2 = Txt("Completed", 11, true, _white);
        d2.HorizontalAlignment = HorizontalAlignment.Center;
        done.Children.Add(d1);
        done.Children.Add(d2);
        Place(g, done, rBars, ProgCol);

        // ---- donuts + title
        var donuts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        for (int i = 0; i < 4; i++) donuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dn1 = DonutBlock(monthly, "#FF6B6B", "MONTHLY PROGRESS");
        var dn2 = DonutBlock(normalized, "#26B5D9", elapsed + " DAYS PROGRESS NORMALIZED");
        var dn3 = DonutBlock(momentum, "#FFB000", "LAST 3 DAYS PROGRESS MOMENTUM");
        Grid.SetColumn(dn1, 0);
        Grid.SetColumn(dn2, 1);
        Grid.SetColumn(dn3, 2);
        donuts.Children.Add(dn1);
        donuts.Children.Add(dn2);
        donuts.Children.Add(dn3);
        var title = Txt("MONTHLY\nHABIT\nTRACKER", 26, true, _white);
        title.TextAlignment = TextAlignment.Right;
        title.HorizontalAlignment = HorizontalAlignment.Right;
        title.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(title, 3);
        donuts.Children.Add(title);
        Place(g, donuts, rDonut, 0, 1, 34);

        return g;
    }

    // ------------------------------------------------------------------ year view
    private UIElement BuildYearView()
    {
        var habits = _db.Habits;
        var checks = new int[13];
        var capped = new int[13];
        var goals = new int[13];
        for (int m = 1; m <= 12; m++)
            foreach (var h in habits)
            {
                int n = DaysFor(h, _year, m).Count;
                checks[m] += n;
                capped[m] += Math.Min(n, h.Goal);
                goals[m] += h.Goal;
            }
        int yChecks = checks.Sum(), yCapped = capped.Sum(), yGoals = goals.Sum();
        double yPct = yGoals > 0 ? yCapped * 100.0 / yGoals : 0;

        var root = new StackPanel { Margin = new Thickness(16), Background = _page, HorizontalAlignment = HorizontalAlignment.Left };
        var head = Txt("YEAR " + _year + " OVERVIEW", 20, true, _white);
        head.Margin = new Thickness(8, 8, 8, 12);
        root.Children.Add(head);

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        const double colW = 70, plotH = 220, baseY = 250;
        var chart = new Canvas { Width = colW * 12, Height = 310, Margin = new Thickness(8, 0, 24, 0) };
        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            double y = baseY - plotH * frac;
            chart.Children.Add(new Line
            {
                X1 = 0, X2 = colW * 12, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                StrokeThickness = 0.6
            });
            var gl = Txt((frac * 100).ToString("0") + "%", 9, false, _grey);
            Canvas.SetLeft(gl, 0);
            Canvas.SetTop(gl, y - 14);
            chart.Children.Add(gl);
        }
        for (int m = 1; m <= 12; m++)
        {
            double pct = goals[m] > 0 ? capped[m] * 100.0 / goals[m] : 0;
            double bh = plotH * pct / 100.0;
            bool current = m == DateTime.Today.Month && _year == DateTime.Today.Year;
            var bar = new Rectangle
            {
                Width = 36,
                Height = Math.Max(0, bh),
                Fill = new SolidColorBrush(current ? Color.FromRgb(0xFF, 0xB0, 0x00) : Color.FromRgb(0x2B, 0xD9, 0xE0))
            };
            Canvas.SetLeft(bar, (m - 1) * colW + 17);
            Canvas.SetTop(bar, baseY - bh);
            chart.Children.Add(bar);

            var val = Txt(pct.ToString("0") + "%", 10, true, _white);
            val.Width = colW;
            val.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(val, (m - 1) * colW);
            Canvas.SetTop(val, baseY - bh - 18);
            chart.Children.Add(val);

            var mn = Txt(new DateTime(2000, m, 1).ToString("MMM", CultureInfo.InvariantCulture), 11, true, _white);
            mn.Width = colW;
            mn.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(mn, (m - 1) * colW);
            Canvas.SetTop(mn, baseY + 6);
            chart.Children.Add(mn);

            var cnt = Txt(checks[m] + "/" + goals[m], 9, false, _grey);
            cnt.Width = colW;
            cnt.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(cnt, (m - 1) * colW);
            Canvas.SetTop(cnt, baseY + 24);
            chart.Children.Add(cnt);

            var hit = new Rectangle
            {
                Width = colW,
                Height = 300,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "Open " + new DateTime(_year, m, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture)
            };
            int mm = m;
            hit.MouseLeftButtonDown += (s, e) =>
            {
                _yearView = false;
                _month = mm;
                Refresh();
            };
            Canvas.SetLeft(hit, (m - 1) * colW);
            Canvas.SetTop(hit, 0);
            chart.Children.Add(hit);
        }
        row.Children.Add(chart);

        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 16, 0) };
        side.Children.Add(Donut(yPct, Hex("#FF6B6B"), 160));
        var cap = Txt("YEARLY PROGRESS", 12, true, _red);
        cap.HorizontalAlignment = HorizontalAlignment.Center;
        cap.Margin = new Thickness(0, 8, 0, 8);
        side.Children.Add(cap);
        var tot = Txt(yChecks + " checks / " + yGoals + " goal", 12, false, _white);
        tot.HorizontalAlignment = HorizontalAlignment.Center;
        side.Children.Add(tot);
        row.Children.Add(side);
        root.Children.Add(row);

        var hdr = Txt("PER HABIT (whole year)", 13, true, _white);
        hdr.Margin = new Thickness(8, 16, 8, 8);
        root.Children.Add(hdr);
        foreach (var h in habits)
        {
            int n = 0;
            for (int m = 1; m <= 12; m++) n += DaysFor(h, _year, m).Count;
            int max = Math.Max(1, h.Goal * 12);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2) };
            var nm = Txt(h.Name, 12, false, _white);
            nm.Width = 200;
            line.Children.Add(nm);
            var back = new Border
            {
                Width = 420,
                Height = 14,
                Background = Brushes.White,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center
            };
            back.Child = new Border
            {
                Background = new SolidColorBrush(Hex(h.Color)),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Width = Math.Min(1.0, n / (double)max) * 418
            };
            line.Children.Add(back);
            var cnt = Txt("  " + n + " / " + max, 11, false, _grey);
            line.Children.Add(cnt);
            root.Children.Add(line);
        }
        if (habits.Count == 0)
        {
            var none = Txt("No habits yet. Switch to Month view and type a habit name in the first white box.", 12, false, _grey);
            none.Margin = new Thickness(8);
            root.Children.Add(none);
        }
        return root;
    }
}
