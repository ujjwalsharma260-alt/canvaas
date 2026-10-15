using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Canvaas;

public partial class MainWindow
{
    // =====================================================================
    // Element manifest (what is stored in the .canvaas file for text boxes / pictures)
    // =====================================================================
    private sealed class ElementManifest
    {
        public string Type { get; set; } = "";
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; } = 18;
        public string? Color { get; set; }
        public string? BorderColor { get; set; }
        public double BorderThickness { get; set; }
        public double PadH { get; set; } = 4;
        public double PadV { get; set; } = 2;
        public bool AcceptsReturn { get; set; }
        public bool Wrap { get; set; }
        public double MinHeight { get; set; }
        public bool VCenter { get; set; }
        public string? ImageEntry { get; set; }
    }

    private static string BrushToHex(Brush? b)
    {
        if (b is SolidColorBrush sb)
        {
            var c = sb.Color;
            return "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
        return "#00000000";
    }

    private static byte[] EncodePng(BitmapSource bs)
    {
        if (PngCache.TryGetValue(bs, out var cached) && cached is { Length: > 0 }) return cached;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        PngCache.AddOrUpdate(bs, bytes);
        return bytes;
    }

    private List<ElementManifest> BuildElementManifests(NotebookPage page, int pageIndex, Dictionary<string, byte[]> blobs)
    {
        var list = new List<ElementManifest>();
        int n = 0;
        foreach (var el in page.Elements)
        {
            if (el is not FrameworkElement fe) continue;
            double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
            if (double.IsNaN(l)) l = 0;
            if (double.IsNaN(t)) t = 0;
            double mw = !double.IsNaN(fe.Width) && fe.Width > 0 ? fe.Width
                      : (fe.ActualWidth > 0 ? fe.ActualWidth : 0);
            double mh = !double.IsNaN(fe.Height) && fe.Height > 0 ? fe.Height
                      : (fe.ActualHeight > 0 ? fe.ActualHeight : 0);
            var m = new ElementManifest
            {
                Left = l,
                Top = t,
                Width = mw,
                Height = mh
            };
            if (fe is TextBox tb)
            {
                m.Type = "text";
                m.Text = tb.Text;
                m.FontFamily = tb.FontFamily.Source;
                m.FontSize = tb.FontSize;
                m.Color = BrushToHex(tb.Foreground);
                m.BorderColor = BrushToHex(tb.BorderBrush);
                m.BorderThickness = tb.BorderThickness.Left;
                m.PadH = tb.Padding.Left;
                m.PadV = tb.Padding.Top;
                m.AcceptsReturn = tb.AcceptsReturn;
                m.Wrap = tb.TextWrapping == TextWrapping.Wrap;
                m.MinHeight = tb.MinHeight;
                m.VCenter = tb.VerticalContentAlignment == VerticalAlignment.Center;
            }
            else if (fe is Image img && img.Source is BitmapSource bs)
            {
                m.Type = "image";
                string name = "page_" + pageIndex.ToString("D3") + "_img_" + n.ToString("D3") + ".png";
                blobs[name] = EncodePng(bs);
                m.ImageEntry = name;
            }
            else continue;
            n++;
            list.Add(m);
        }
        return list;
    }

    private void LoadElementsInto(NotebookPage page, PageManifest pm, ZipArchive zip)
    {
        if (pm.Elements is null) return;
        foreach (var m in pm.Elements)
        {
            if (m.Type == "text")
            {
                var tb = new TextBox
                {
                    Text = m.Text ?? "",
                    FontSize = m.FontSize > 0 ? m.FontSize : 18,
                    AcceptsReturn = m.AcceptsReturn,
                    TextWrapping = m.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Padding = new Thickness(m.PadH, m.PadV, m.PadH, m.PadV),
                    Background = Brushes.Transparent,
                    BorderBrush = new SolidColorBrush(ParseHexColor(m.BorderColor ?? "#00000000", Colors.Transparent)),
                    BorderThickness = new Thickness(m.BorderThickness),
                    Foreground = new SolidColorBrush(ParseHexColor(m.Color ?? "#FF000000", Colors.Black)),
                    MinHeight = m.MinHeight > 0 ? m.MinHeight : 0,
                    VerticalContentAlignment = m.VCenter ? VerticalAlignment.Center : VerticalAlignment.Top
                };
                if (!string.IsNullOrEmpty(m.FontFamily)) tb.FontFamily = new FontFamily(m.FontFamily);
                if (m.Width > 0) tb.Width = m.Width;
                if (m.Height > 0) tb.Height = m.Height;
                if (tb.AcceptsReturn) tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                InkCanvas.SetLeft(tb, m.Left);
                InkCanvas.SetTop(tb, m.Top);
                page.Elements.Add(tb);
            }
            else if (m.Type == "image" && !string.IsNullOrEmpty(m.ImageEntry))
            {
                var entry = zip.GetEntry(m.ImageEntry);
                if (entry is null) continue;
                using var ms = new MemoryStream();
                using (var es = entry.Open()) es.CopyTo(ms);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                PngCache.AddOrUpdate(bmp, ms.ToArray());
                var img = new Image
                {
                    Source = bmp,
                    Width = m.Width > 0 ? m.Width : bmp.PixelWidth,
                    Height = m.Height > 0 ? m.Height : bmp.PixelHeight,
                    Stretch = Stretch.Uniform
                };
                InkCanvas.SetLeft(img, m.Left);
                InkCanvas.SetTop(img, m.Top);
                page.Elements.Add(img);
            }
        }
    }

    // =====================================================================
    // Geometry helpers shared by PNG and PDF export
    // =====================================================================
    private static Rect ElementRect(FrameworkElement fe)
    {
        double l = InkCanvas.GetLeft(fe), t = InkCanvas.GetTop(fe);
        if (double.IsNaN(l)) l = 0;
        if (double.IsNaN(t)) t = 0;
        double w = fe.ActualWidth > 0 ? fe.ActualWidth : (double.IsNaN(fe.Width) ? 100 : fe.Width);
        double h;
        if (fe.ActualHeight > 0) h = fe.ActualHeight;
        else if (!double.IsNaN(fe.Height)) h = fe.Height;
        else h = fe.MinHeight > 0 ? fe.MinHeight : 30;
        return new Rect(l, t, w, h);
    }

    private void SyncCurrentElements()
    {
        if (InkArea is null || _pages.Count == 0) return;
        var p = CurrentPage;
        p.Elements.Clear();
        foreach (var c in InkArea.Children)
            if (c is UIElement u) p.Elements.Add(u);
    }

    private Rect ContentBounds(NotebookPage page)
    {
        if (page.WorldWidth <= 5000 && page.WorldHeight <= 5000)
            return new Rect(0, 0, page.WorldWidth, page.WorldHeight);
        Rect? r = null;
        if (page.Strokes.Count > 0)
        {
            var b = page.Strokes.GetBounds();
            if (!b.IsEmpty) r = b;
        }
        foreach (var el in page.Elements)
        {
            if (el is not FrameworkElement fe) continue;
            var er = ElementRect(fe);
            r = r.HasValue ? Rect.Union(r.Value, er) : er;
        }
        if (r is null) return new Rect(page.WorldWidth / 2 - 400, page.WorldHeight / 2 - 560, 800, 1120);
        var res = r.Value;
        res.Inflate(40, 40);
        return res;
    }

    private static Rect FitUniform(double pw, double ph, Rect box)
    {
        if (pw <= 0 || ph <= 0) return box;
        double s = Math.Min(box.Width / pw, box.Height / ph);
        double w = pw * s, h = ph * s;
        return new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    private static FormattedText MakeFormattedText(TextBox tb, double boxWidth)
    {
        Brush brush = tb.Foreground ?? Brushes.Black;
        var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, FontStretches.Normal),
            tb.FontSize, brush, 1.0);
        if (tb.TextWrapping == TextWrapping.Wrap || tb.AcceptsReturn)
            ft.MaxTextWidth = Math.Max(10, boxWidth - tb.Padding.Left - tb.Padding.Right
                                              - tb.BorderThickness.Left - tb.BorderThickness.Right);
        return ft;
    }

    private static Point TextOrigin(TextBox tb, Rect r, FormattedText ft)
    {
        double x = r.X + tb.Padding.Left + tb.BorderThickness.Left;
        double y = r.Y + tb.Padding.Top + tb.BorderThickness.Top;
        if (tb.VerticalContentAlignment == VerticalAlignment.Center)
            y = r.Y + Math.Max(0, (r.Height - ft.Height) / 2);
        return new Point(x, y);
    }

    private static void DrawElements(DrawingContext dc, NotebookPage page)
    {
        foreach (var el in page.Elements)
        {
            if (el is not FrameworkElement fe) continue;
            var r = ElementRect(fe);
            if (fe is Image img && img.Source is BitmapSource bs)
            {
                dc.DrawImage(bs, FitUniform(bs.PixelWidth, bs.PixelHeight, r));
            }
            else if (fe is TextBox tb && !string.IsNullOrEmpty(tb.Text))
            {
                var ft = MakeFormattedText(tb, r.Width);
                dc.DrawText(ft, TextOrigin(tb, r, ft));
            }
        }
    }

    // =====================================================================
    // PNG export (now includes pictures, typed words and the page background)
    // =====================================================================
    private void ExportPageToPng(NotebookPage page, string path)
    {
        SyncCurrentElements();
        var world = ContentBounds(page);
        double scale = 2.0;
        const double maxPx = 8000;
        if (world.Width * scale > maxPx) scale = maxPx / world.Width;
        if (world.Height * scale > maxPx) scale = Math.Min(scale, maxPx / world.Height);
        int pxW = Math.Max(1, (int)Math.Round(world.Width * scale));
        int pxH = Math.Max(1, (int)Math.Round(world.Height * scale));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-world.X, -world.Y));
            dc.DrawRectangle(BuildPageBrush(page, 1.0), null, world);
            DrawElements(dc, page);
            foreach (var s in page.Strokes) s.Draw(dc);
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // =====================================================================
    // Save as PDF (real vector PDF: sharp at any zoom)
    // =====================================================================
    private void ExportAsPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save as PDF",
            Filter = "PDF file (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = _currentPath is null ? "My Notebook" : System.IO.Path.GetFileNameWithoutExtension(_currentPath)
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            WritePdf(dlg.FileName);
            StatusText.Text = "Saved PDF: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "PDF export failed.\n\n" + ex.Message, "Save as PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private sealed class PdfDoc
    {
        private readonly MemoryStream _ms = new();
        private readonly List<long> _offsets = new() { 0 };

        public void Write(string s)
        {
            var b = Encoding.Latin1.GetBytes(s);
            _ms.Write(b, 0, b.Length);
        }

        public void WriteBytes(byte[] b) => _ms.Write(b, 0, b.Length);

        public void Header() => Write("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");

        public int Reserve()
        {
            _offsets.Add(0);
            return _offsets.Count - 1;
        }

        public void SetObject(int num, string body)
        {
            _offsets[num] = _ms.Position;
            Write(num + " 0 obj\n" + body + "\nendobj\n");
        }

        public int AddObject(string body)
        {
            int num = Reserve();
            SetObject(num, body);
            return num;
        }

        private static byte[] ZCompress(byte[] data)
        {
            using var outMs = new MemoryStream();
            using (var z = new ZLibStream(outMs, CompressionLevel.Optimal, true))
                z.Write(data, 0, data.Length);
            return outMs.ToArray();
        }

        public int AddStream(string dictEntries, byte[] data)
        {
            int num = Reserve();
            byte[] body = ZCompress(data);
            _offsets[num] = _ms.Position;
            Write(num + " 0 obj\n<< " + dictEntries + " /Filter /FlateDecode /Length " + body.Length + " >>\nstream\n");
            WriteBytes(body);
            Write("\nendstream\nendobj\n");
            return num;
        }

        public byte[] Finish(int root)
        {
            long xrefPos = _ms.Position;
            int count = _offsets.Count;
            Write("xref\n0 " + count + "\n");
            Write("0000000000 65535 f \n");
            for (int i = 1; i < count; i++)
                Write(_offsets[i].ToString("D10") + " 00000 n \n");
            Write("trailer\n<< /Size " + count + " /Root " + root + " 0 R >>\nstartxref\n" + xrefPos + "\n%%EOF\n");
            return _ms.ToArray();
        }
    }

    private static void PdfSetAlpha(StringBuilder sb, Dictionary<int, string> map, int alpha)
    {
        if (alpha > 255) alpha = 255;
        if (alpha < 0) alpha = 0;
        if (!map.TryGetValue(alpha, out var name))
        {
            name = "G" + alpha;
            map[alpha] = name;
        }
        sb.Append("/").Append(name).Append(" gs\n");
    }

    private static string PdfRgb(Color c) =>
        N(c.R / 255.0) + " " + N(c.G / 255.0) + " " + N(c.B / 255.0);

    private static void PdfFillGeometry(StringBuilder sb, Dictionary<int, string> gs, Geometry? geo, Color c)
    {
        if (geo is null || geo.IsEmpty()) return;
        var flat = geo.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        PdfSetAlpha(sb, gs, c.A);
        sb.Append(PdfRgb(c)).Append(" rg\n");
        foreach (var fig in flat.Figures)
        {
            sb.Append(N(fig.StartPoint.X)).Append(' ').Append(N(fig.StartPoint.Y)).Append(" m ");
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment ls)
                    sb.Append(N(ls.Point.X)).Append(' ').Append(N(ls.Point.Y)).Append(" l ");
                else if (seg is PolyLineSegment pls)
                    foreach (var p in pls.Points)
                        sb.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(" l ");
            }
            sb.Append("h\n");
        }
        sb.Append(flat.FillRule == FillRule.EvenOdd ? "f*\n" : "f\n");
    }

    private static void PdfCircle(StringBuilder sb, double cx, double cy, double r)
    {
        const double k = 0.5522847498;
        sb.Append(N(cx + r)).Append(' ').Append(N(cy)).Append(" m ");
        sb.Append(N(cx + r)).Append(' ').Append(N(cy + k * r)).Append(' ').Append(N(cx + k * r)).Append(' ').Append(N(cy + r)).Append(' ').Append(N(cx)).Append(' ').Append(N(cy + r)).Append(" c ");
        sb.Append(N(cx - k * r)).Append(' ').Append(N(cy + r)).Append(' ').Append(N(cx - r)).Append(' ').Append(N(cy + k * r)).Append(' ').Append(N(cx - r)).Append(' ').Append(N(cy)).Append(" c ");
        sb.Append(N(cx - r)).Append(' ').Append(N(cy - k * r)).Append(' ').Append(N(cx - k * r)).Append(' ').Append(N(cy - r)).Append(' ').Append(N(cx)).Append(' ').Append(N(cy - r)).Append(" c ");
        sb.Append(N(cx + k * r)).Append(' ').Append(N(cy - r)).Append(' ').Append(N(cx + r)).Append(' ').Append(N(cy - k * r)).Append(' ').Append(N(cx + r)).Append(' ').Append(N(cy)).Append(" c h f\n");
    }

    private static void PdfPattern(StringBuilder sb, Dictionary<int, string> gs, NotebookPage page, Rect b)
    {
        double sp = page.Spacing;
        if (sp <= 0) return;
        Color lc = GetLineColor(page.BackgroundColor, page.PatternOpacity);
        PdfSetAlpha(sb, gs, lc.A);
        string rgb = PdfRgb(lc);
        double startX = Math.Floor(b.Left / sp) * sp;
        double startY = Math.Floor(b.Top / sp) * sp;

        if (page.Template == PageTemplate.Ruled || page.Template == PageTemplate.Grid)
        {
            sb.Append(rgb).Append(" RG ").Append(N(page.PatternThickness)).Append(" w\n");
            for (double y = startY + sp - 0.5; y <= b.Bottom; y += sp)
            {
                if (y < b.Top) continue;
                sb.Append(N(b.Left)).Append(' ').Append(N(y)).Append(" m ").Append(N(b.Right)).Append(' ').Append(N(y)).Append(" l S\n");
            }
            if (page.Template == PageTemplate.Grid)
            {
                for (double x = startX + sp - 0.5; x <= b.Right; x += sp)
                {
                    if (x < b.Left) continue;
                    sb.Append(N(x)).Append(' ').Append(N(b.Top)).Append(" m ").Append(N(x)).Append(' ').Append(N(b.Bottom)).Append(" l S\n");
                }
            }
        }
        else if (page.Template == PageTemplate.Dot)
        {
            double count = (b.Width / sp) * (b.Height / sp);
            if (count > 80000) return;
            sb.Append(rgb).Append(" rg\n");
            double radius = Math.Max(0.3, page.PatternDotSize);
            for (double y = startY; y <= b.Bottom; y += sp)
                for (double x = startX; x <= b.Right; x += sp)
                {
                    double cx = x + sp / 2, cy = y + sp / 2;
                    if (cx < b.Left || cx > b.Right || cy < b.Top || cy > b.Bottom) continue;
                    PdfCircle(sb, cx, cy, radius);
                }
        }
    }

    private static int PdfAddImage(PdfDoc doc, BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool hasAlpha = false;
        int j = 0, k = 0;
        for (int i = 0; i < w * h; i++)
        {
            rgb[j++] = px[k + 2];
            rgb[j++] = px[k + 1];
            rgb[j++] = px[k];
            byte a = px[k + 3];
            alpha[i] = a;
            if (a != 255) hasAlpha = true;
            k += 4;
        }
        string sm = "";
        if (hasAlpha)
        {
            int smask = doc.AddStream("/Type /XObject /Subtype /Image /Width " + w + " /Height " + h
                + " /ColorSpace /DeviceGray /BitsPerComponent 8", alpha);
            sm = "/SMask " + smask + " 0 R ";
        }
        return doc.AddStream("/Type /XObject /Subtype /Image /Width " + w + " /Height " + h
            + " /ColorSpace /DeviceRGB /BitsPerComponent 8 " + sm, rgb);
    }

    private void WritePdf(string filePath)
    {
        SyncCurrentElements();
        var doc = new PdfDoc();
        doc.Header();
        int pagesNum = doc.Reserve();
        var pageNums = new List<int>();

        foreach (var page in _pages)
        {
            var b = ContentBounds(page);
            double s = Math.Min(0.75, 14000.0 / Math.Max(b.Width, b.Height));
            double wPt = b.Width * s, hPt = b.Height * s;

            var sb = new StringBuilder();
            var gs = new Dictionary<int, string>();
            var imgRes = new List<(string Name, int Obj)>();

            // flip the y axis and scale so everything below is drawn in canvas coordinates
            sb.Append(N(s)).Append(" 0 0 ").Append(N(-s)).Append(' ')
              .Append(N(-b.X * s)).Append(' ').Append(N(hPt + b.Y * s)).Append(" cm\n");

            // background
            Color bg = page.BackgroundColor;
            if (page.Paper == PaperStyle.Crumpled && bg == Colors.White) bg = Color.FromRgb(0xF6, 0xF6, 0xF4);
            PdfSetAlpha(sb, gs, 255);
            sb.Append(PdfRgb(bg)).Append(" rg\n");
            sb.Append(N(b.X)).Append(' ').Append(N(b.Y)).Append(' ').Append(N(b.Width)).Append(' ').Append(N(b.Height)).Append(" re f\n");
            if (page.Template != PageTemplate.Blank) PdfPattern(sb, gs, page, b);

            // pictures and typed words
            foreach (var el in page.Elements)
            {
                if (el is not FrameworkElement fe) continue;
                var r = ElementRect(fe);
                if (fe is Image img && img.Source is BitmapSource bs)
                {
                    var fit = FitUniform(bs.PixelWidth, bs.PixelHeight, r);
                    int obj = PdfAddImage(doc, bs);
                    string name = "Im" + imgRes.Count;
                    imgRes.Add((name, obj));
                    PdfSetAlpha(sb, gs, 255);
                    sb.Append("q ").Append(N(fit.Width)).Append(" 0 0 ").Append(N(-fit.Height)).Append(' ')
                      .Append(N(fit.X)).Append(' ').Append(N(fit.Y + fit.Height)).Append(" cm /").Append(name).Append(" Do Q\n");
                }
                else if (fe is TextBox tb && !string.IsNullOrEmpty(tb.Text))
                {
                    var ft = MakeFormattedText(tb, r.Width);
                    var geo = ft.BuildGeometry(TextOrigin(tb, r, ft));
                    Color fc = (tb.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
                    PdfFillGeometry(sb, gs, geo, fc);
                }
            }

            // pen strokes as sharp vector shapes
            foreach (var st in page.Strokes)
                PdfFillGeometry(sb, gs, st.GetGeometry(), st.DrawingAttributes.Color);

            int contentObj = doc.AddStream("", Encoding.Latin1.GetBytes(sb.ToString()));

            var res = new StringBuilder("<< ");
            if (gs.Count > 0)
            {
                res.Append("/ExtGState << ");
                foreach (var kv in gs)
                    res.Append("/").Append(kv.Value).Append(" << /ca ").Append(N(kv.Key / 255.0))
                       .Append(" /CA ").Append(N(kv.Key / 255.0)).Append(" >> ");
                res.Append(">> ");
            }
            if (imgRes.Count > 0)
            {
                res.Append("/XObject << ");
                foreach (var im in imgRes) res.Append("/").Append(im.Name).Append(' ').Append(im.Obj).Append(" 0 R ");
                res.Append(">> ");
            }
            res.Append(">>");

            int pageNum = doc.AddObject("<< /Type /Page /Parent " + pagesNum + " 0 R /MediaBox [0 0 "
                + N(wPt) + " " + N(hPt) + "] /Resources " + res + " /Contents " + contentObj + " 0 R >>");
            pageNums.Add(pageNum);
        }

        var kids = new StringBuilder();
        foreach (var p in pageNums) kids.Append(p).Append(" 0 R ");
        doc.SetObject(pagesNum, "<< /Type /Pages /Kids [" + kids + "] /Count " + pageNums.Count + " >>");
        int catalog = doc.AddObject("<< /Type /Catalog /Pages " + pagesNum + " 0 R >>");
        File.WriteAllBytes(filePath, doc.Finish(catalog));
    }

    // =====================================================================
    // Page currently shown on the canvas (fixes pictures / text vanishing)
    // =====================================================================
    private NotebookPage? _displayedPage;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, byte[]> PngCache = new();

    // =====================================================================
    // Insert PDF: textbook page on the left, empty notes space on the right
    // =====================================================================
    private sealed class SpreadOptions
    {
        public double NotesFactor = 1.3;
        public int GridMode;        // 0 plain, 1 dots, 2 lines, 3 squares
        public bool Divider = true;
        public int Sharpness = 2;   // 1..4
    }

    private SpreadOptions? AskSpreadOptions()
    {
        var result = new SpreadOptions();
        bool ok = false;
        var win = new Window
        {
            Title = "Insert PDF: textbook page + notes space",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5))
        };
        var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 440 };

        TextBlock Section(string t) => new TextBlock
        {
            Text = t,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4)
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Each PDF page goes on the left. The space on its right is yours for notes.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var widthLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
        var slider = new Slider
        {
            Minimum = 0.5,
            Maximum = 3.0,
            Value = 1.3,
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 4, 0, 6)
        };
        slider.ValueChanged += (s, e) =>
            widthLabel.Text = "Notes area width: " + slider.Value.ToString("0.0") + "x the textbook page";
        widthLabel.Text = "Notes area width: " + slider.Value.ToString("0.0") + "x the textbook page";
        panel.Children.Add(widthLabel);
        panel.Children.Add(slider);

        var presets = new StackPanel { Orientation = Orientation.Horizontal };
        void AddPreset(string text, double v)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (s, e) => slider.Value = v;
            presets.Children.Add(b);
        }
        AddPreset("Equal halves", 1.0);
        AddPreset("Fit a laptop screen", 1.3);
        AddPreset("Big", 1.8);
        AddPreset("Huge", 2.6);
        panel.Children.Add(presets);

        panel.Children.Add(Section("Writing grid on the notes side"));
        var gridRow = new StackPanel { Orientation = Orientation.Horizontal };
        string[] gridNames = { "Plain", "Dots", "Lines", "Squares" };
        var gridBtns = new List<RadioButton>();
        for (int i = 0; i < gridNames.Length; i++)
        {
            var rb = new RadioButton
            {
                Content = gridNames[i],
                GroupName = "spreadGrid",
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = i == 0
            };
            gridBtns.Add(rb);
            gridRow.Children.Add(rb);
        }
        panel.Children.Add(gridRow);

        panel.Children.Add(Section("Page sharpness (when you zoom in)"));
        var sharpRow = new StackPanel { Orientation = Orientation.Horizontal };
        string[] sharpNames = { "Standard", "2x sharper", "3x sharper", "4x sharper" };
        var sharpBtns = new List<RadioButton>();
        for (int i = 0; i < sharpNames.Length; i++)
        {
            var rb = new RadioButton
            {
                Content = sharpNames[i],
                GroupName = "spreadSharp",
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = i == 1
            };
            sharpBtns.Add(rb);
            sharpRow.Children.Add(rb);
        }
        panel.Children.Add(sharpRow);
        panel.Children.Add(new TextBlock
        {
            Text = "Sharper pages use more memory. Long PDFs are limited automatically.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var divider = new CheckBox
        {
            Content = "Thin line between textbook and notes",
            IsChecked = true,
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(divider);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var okBtn = new Button { Content = "Insert", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
        okBtn.Click += (s, e) =>
        {
            result.NotesFactor = slider.Value;
            result.GridMode = Math.Max(0, gridBtns.FindIndex(r => r.IsChecked == true));
            result.Sharpness = Math.Max(0, sharpBtns.FindIndex(r => r.IsChecked == true)) + 1;
            result.Divider = divider.IsChecked == true;
            ok = true;
            win.Close();
        };
        cancelBtn.Click += (s, e) => win.Close();
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.ShowDialog();
        return ok ? result : null;
    }

    private static async System.Threading.Tasks.Task<(List<byte[]> Pages, int UsedWidth)> RenderPdfBytesAsync(string path, int width)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var doc = await global::Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        double budget = 1.6e9;
        int cap = (int)Math.Sqrt(budget / (Math.Max(1u, doc.PageCount) * 1.45 * 4.0));
        int used = Math.Max(1000, Math.Min(width, cap));
        var result = new List<byte[]>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            await page.PrepareAsync();
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            var options = new global::Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)used };
            await page.RenderToStreamAsync(stream, options);
            using var input = stream.GetInputStreamAt(0);
            using var reader = new global::Windows.Storage.Streams.DataReader(input);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            result.Add(bytes);
        }
        return (result, used);
    }

    private async void ImportPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Insert PDF" };
        if (dlg.ShowDialog(this) != true) return;
        var opt = AskSpreadOptions();
        if (opt is null) return;
        int[] widths = { 1600, 2800, 4000, 5200 };
        int wanted = widths[Math.Clamp(opt.Sharpness - 1, 0, 3)];

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            StatusText.Text = "Reading the PDF... this can take a moment.";
            string pdfPath = dlg.FileName;

            // Step 1: WinRT PDF rendering on a background thread.
            // Do NOT create WPF objects (BitmapImage) on that thread.
            List<byte[]> rawBytes;
            int used;
            try
            {
                (rawBytes, used) = await System.Threading.Tasks.Task.Run(
                    () => RenderPdfBytesAsync(pdfPath, wanted));
            }
            catch
            {
                rawBytes = new List<byte[]>();
                used = 0;
            }

            if (rawBytes.Count == 0)
            {
                MessageBox.Show(this,
                    "Could not read this PDF.\n\nYou need Windows 10 or later. If the PDF is protected with a password, remove the password first.",
                    "Insert PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Step 2: back on the UI thread — build BitmapImages from the raw bytes.
            var pages = new List<BitmapSource>();
            foreach (var bytes in rawBytes)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                PngCache.AddOrUpdate(bmp, bytes);
                pages.Add(bmp);
            }

            var cur = CurrentPage;
            int originalIndex = _currentPageIndex;
            PageTemplate tpl = opt.GridMode switch
            {
                1 => PageTemplate.Dot,
                2 => PageTemplate.Ruled,
                3 => PageTemplate.Grid,
                _ => PageTemplate.Blank
            };
            for (int i = 0; i < pages.Count; i++)
            {
                var bmp = pages[i];
                double aspect = bmp.PixelWidth / (double)bmp.PixelHeight;
                double tw = Math.Clamp(A4Height * aspect, 600, 1500);
                double th = tw / aspect;
                var np = new NotebookPage
                {
                    BackgroundColor = cur.BackgroundColor,
                    Template = tpl,
                    Paper = cur.Paper,
                    Spacing = cur.Spacing,
                    PatternOpacity = cur.PatternOpacity,
                    PatternThickness = cur.PatternThickness,
                    PatternDotSize = cur.PatternDotSize,
                    WorldWidth = tw * (1.0 + opt.NotesFactor),
                    WorldHeight = th
                };
                var img = new Image { Source = bmp, Width = tw, Height = th, Stretch = Stretch.Uniform };
                InkCanvas.SetLeft(img, 0);
                InkCanvas.SetTop(img, 0);
                np.Elements.Add(img);
                if (opt.Divider)
                {
                    bool dark = IsDarkColor(cur.BackgroundColor);
                    var da = new DrawingAttributes
                    {
                        Color = dark ? Color.FromRgb(0x77, 0x77, 0x77) : Color.FromRgb(0xB0, 0xB0, 0xB0),
                        Width = 1.5,
                        Height = 1.5,
                        FitToCurve = false,
                        IgnorePressure = true
                    };
                    var pts = new StylusPointCollection();
                    pts.Add(new StylusPoint(tw, 0));
                    pts.Add(new StylusPoint(tw, th));
                    np.Strokes.Add(new Stroke(pts, da));
                }
                _pages.Insert(originalIndex + 1 + i, np);
            }

            _currentPageIndex = originalIndex + 1;
            LoadCurrentPageIntoCanvas();
            MarkDirty();
            StatusText.Text = "Inserted " + pages.Count + " PDF page(s) with notes space. Use the arrows to move between pages.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "PDF import failed.\n\n" + ex.Message, "Insert PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
