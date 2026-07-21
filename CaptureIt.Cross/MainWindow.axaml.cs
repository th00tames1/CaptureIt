using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CaptureIt.Cross;

public partial class MainWindow : Window
{
    private enum Tool { Select, Pen, Box, Arrow, Crop }

    private Tool _tool = Tool.Select;
    private Color _color = Color.Parse("#EF4444");
    private double Thickness => CmbThickness.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 };

    private HistoryItem? _current;
    private Bitmap? _bitmap;
    private bool _dirty;

    private bool _drawing;
    private Point _start;
    private List<Point>? _penPoints;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTexts();
        HistoryList.ItemsSource = History.Items;
        if (History.Items.Count > 0) HistoryList.SelectedIndex = 0;
        Status(Loc.Get("Status.Ready"));

        // 생성자 시점엔 레이아웃 전이라 Scroller 크기가 0 — 첫 표시 후 맞춤 배율을 다시 계산
        Loaded += (_, _) => FitZoom();

        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control)
            {
                if (e.Key == Key.Z) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
                if (e.Key == Key.S) { Save_Click(this, new RoutedEventArgs()); e.Handled = true; }
                if (e.Key == Key.C) { Copy_Click(this, new RoutedEventArgs()); e.Handled = true; }
            }
        };
    }

    private void ApplyTexts()
    {
        Title = Loc.Get("App.Title");
        BtnRegionText.Text = Loc.Get("Btn.Region");
        BtnFullText.Text = Loc.Get("Btn.Full");
        ToolSelectText.Text = Loc.Get("Tool.Select");
        ToolPenText.Text = Loc.Get("Tool.Pen");
        ToolBoxText.Text = Loc.Get("Tool.Box");
        ToolArrowText.Text = Loc.Get("Tool.Arrow");
        ToolCropText.Text = Loc.Get("Tool.Crop");
        BtnUndoText.Text = Loc.Get("Btn.Undo");
        BtnCopyText.Text = Loc.Get("Btn.Copy");
        BtnSaveText.Text = Loc.Get("Btn.Save");
        BtnDeleteText.Text = Loc.Get("Btn.Delete");
        BtnFolderText.Text = Loc.Get("Btn.Folder");
        BtnLangText.Text = Loc.Get("Btn.Lang");
        LblHistory.Text = Loc.Get("History.Title");
        TxtAbout.Text = Loc.Get("About");
    }

    // ── 캡처 ──────────────────────────────────────────────────────────────
    private async void Region_Click(object? sender, RoutedEventArgs e) => await CaptureAsync(ScreenGrab.Mode.Region);
    private async void Full_Click(object? sender, RoutedEventArgs e) => await CaptureAsync(ScreenGrab.Mode.Full);

    private async Task CaptureAsync(ScreenGrab.Mode mode)
    {
        FlattenIfDirty();
        Hide();
        await Task.Delay(350);   // 창이 사라질 시간

        try
        {
            var result = await ScreenGrab.CaptureAsync(mode);
            if (result.PngPath == null)
            {
                Status(Loc.Get(result.Error == "cancelled" ? "Status.Cancelled" : "Status.CaptureFailed"));
                return;
            }

            string png = result.PngPath;

            // Windows에서 Region은 전체 캡처 후 자체 오버레이로 잘라낸다
            if (mode == ScreenGrab.Mode.Region && OperatingSystem.IsWindows())
            {
                var frozen = new Bitmap(png);
                var overlay = new OverlayWindow(frozen);
                overlay.Show();
                await overlay.WaitClosedAsync();
                frozen.Dispose();

                if (overlay.Result is not { } rect) { Status(Loc.Get("Status.Cancelled")); return; }

                var full = new Bitmap(png);
                var cropped = CropPng(full, rect);
                full.Dispose();
                if (cropped == null) { Status(Loc.Get("Status.CaptureFailed")); return; }
                File.WriteAllBytes(png, cropped);
            }

            // 크기 파악 후 히스토리에 추가
            int w, h;
            using (var probe = new Bitmap(png)) { w = probe.PixelSize.Width; h = probe.PixelSize.Height; }
            var item = History.Add(png, w, h);
            if (item == null) { Status(Loc.Get("Status.CaptureFailed")); return; }

            HistoryList.SelectedItem = item;
            Status(Loc.F("Status.Captured", w, h));
        }
        finally
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    /// <summary>비트맵의 일부를 PNG 바이트로 잘라낸다.</summary>
    private static byte[]? CropPng(Bitmap src, Rect rect)
    {
        try
        {
            int cw = Math.Max(1, (int)rect.Width), ch = Math.Max(1, (int)rect.Height);
            var rtb = new RenderTargetBitmap(new PixelSize(cw, ch), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
                ctx.DrawImage(src, new Rect(rect.X, rect.Y, cw, ch), new Rect(0, 0, cw, ch));
            using var ms = new MemoryStream();
            rtb.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ── 히스토리/편집기 ───────────────────────────────────────────────────
    private void History_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        FlattenIfDirty();
        LoadItem(HistoryList.SelectedItem as HistoryItem);
    }

    private void LoadItem(HistoryItem? item)
    {
        _current = item;
        _bitmap?.Dispose();
        _bitmap = null;
        _dirty = false;

        if (item == null || !File.Exists(item.FilePath))
        {
            Canvas.SetImage(null);
            return;
        }
        try { _bitmap = new Bitmap(item.FilePath); }
        catch { return; }

        Canvas.SetImage(_bitmap);
        FitZoom();
    }

    private void FitZoom()
    {
        if (_bitmap == null) return;
        double availW = Math.Max(100, Scroller.Bounds.Width - 48);
        double availH = Math.Max(100, Scroller.Bounds.Height - 48);
        double z = Math.Min(1.0, Math.Min(availW / _bitmap.PixelSize.Width,
                                          availH / _bitmap.PixelSize.Height));
        ZoomHost.LayoutTransform = new ScaleTransform(z, z);
    }

    /// <summary>주석이 있으면 항목 이미지에 합성해 저장하고 다시 로드한다.</summary>
    private void FlattenIfDirty()
    {
        if (!_dirty || _current == null || _bitmap == null) return;
        if (Canvas.Annotations.Count == 0) { _dirty = false; return; }

        var png = Canvas.ExportPng();
        if (png != null)
        {
            History.UpdateImage(_current, png, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
            var keep = _current;
            LoadItem(keep);
        }
        _dirty = false;
    }

    // ── 도구/색상 ─────────────────────────────────────────────────────────
    private void Tool_Click(object? sender, RoutedEventArgs e)
    {
        var buttons = new[] { ToolSelect, ToolPen, ToolBox, ToolArrow, ToolCrop };
        foreach (var b in buttons) b.IsChecked = ReferenceEquals(b, sender);
        _tool = sender == ToolPen ? Tool.Pen
              : sender == ToolBox ? Tool.Box
              : sender == ToolArrow ? Tool.Arrow
              : sender == ToolCrop ? Tool.Crop
              : Tool.Select;
    }

    private void Color_Click(object? sender, RoutedEventArgs e)
    {
        var buttons = new[] { ColR, ColY, ColG, ColB, ColK };
        foreach (var b in buttons)
        {
            bool sel = ReferenceEquals(b, sender);
            b.BorderBrush = new SolidColorBrush(Color.Parse("#1F5FCC"));
            b.BorderThickness = new Avalonia.Thickness(sel ? 3 : 0);
        }
        if (sender is Button { Tag: string hex })
            _color = Color.Parse(hex);
    }

    // ── 캔버스 그리기 ─────────────────────────────────────────────────────
    private void Canvas_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (_bitmap == null || _tool == Tool.Select) return;
        var pos = e.GetPosition(Canvas);
        _drawing = true;
        _start = pos;

        if (_tool == Tool.Pen)
        {
            _penPoints = new List<Point> { pos };
            Canvas.Preview = new PenAnn(_color, Thickness, _penPoints);
        }
        e.Pointer.Capture(Canvas);
    }

    private void Canvas_Moved(object? sender, PointerEventArgs e)
    {
        if (!_drawing) return;
        var pos = e.GetPosition(Canvas);
        pos = new Point(Math.Clamp(pos.X, 0, Canvas.Width), Math.Clamp(pos.Y, 0, Canvas.Height));

        switch (_tool)
        {
            case Tool.Pen:
                _penPoints!.Add(pos);
                break;
            case Tool.Box:
                Canvas.Preview = new BoxAnn(_color, Thickness, RectFrom(_start, pos));
                break;
            case Tool.Arrow:
                Canvas.Preview = new ArrowAnn(_color, Thickness, _start, pos);
                break;
            case Tool.Crop:
                Canvas.CropPreview = RectFrom(_start, pos);
                break;
        }
        Canvas.InvalidateVisual();
    }

    private void Canvas_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        e.Pointer.Capture(null);
        var pos = e.GetPosition(Canvas);
        pos = new Point(Math.Clamp(pos.X, 0, Canvas.Width), Math.Clamp(pos.Y, 0, Canvas.Height));

        switch (_tool)
        {
            case Tool.Pen when _penPoints is { Count: > 1 }:
                Canvas.Annotations.Add(new PenAnn(_color, Thickness, _penPoints));
                _dirty = true;
                break;

            case Tool.Box:
            {
                var r = RectFrom(_start, pos);
                if (r.Width >= 3 || r.Height >= 3)
                {
                    Canvas.Annotations.Add(new BoxAnn(_color, Thickness, r));
                    _dirty = true;
                }
                break;
            }

            case Tool.Arrow:
            {
                var v = pos - _start;
                if (Math.Sqrt(v.X * v.X + v.Y * v.Y) >= 3)
                {
                    Canvas.Annotations.Add(new ArrowAnn(_color, Thickness, _start, pos));
                    _dirty = true;
                }
                break;
            }

            case Tool.Crop:
            {
                var r = RectFrom(_start, pos);
                Canvas.CropPreview = null;
                if (r.Width >= 8 && r.Height >= 8 && _current != null)
                {
                    var png = Canvas.ExportPng(r);
                    if (png != null)
                    {
                        History.UpdateImage(_current, png, (int)r.Width, (int)r.Height);
                        LoadItem(_current);
                        Status(Loc.Get("Status.Cropped"));
                    }
                }
                break;
            }
        }

        _penPoints = null;
        Canvas.Preview = null;
        Canvas.InvalidateVisual();
    }

    private static Rect RectFrom(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    // ── 동작 버튼 ─────────────────────────────────────────────────────────
    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Annotations.Count == 0) return;
        Canvas.Annotations.RemoveAt(Canvas.Annotations.Count - 1);
        _dirty = Canvas.Annotations.Count > 0;
        Canvas.InvalidateVisual();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        FlattenIfDirty();
        try
        {
            var dest = Settings.Current.NewFilePath();
            File.Copy(_current.FilePath, dest, overwrite: true);
            Status(Loc.F("Status.Saved", dest));
        }
        catch (Exception ex) { Status(ex.Message); }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        FlattenIfDirty();
        bool ok = await ClipboardHelper.CopyImageAsync(_current.FilePath);
        Status(Loc.Get(ok ? "Status.Copied" : "Status.CopyFailed"));
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        int idx = History.Items.IndexOf(_current);
        History.Delete(_current);
        _current = null;
        _dirty = false;
        if (History.Items.Count > 0)
            HistoryList.SelectedIndex = Math.Min(idx, History.Items.Count - 1);
        else
            LoadItem(null);
    }

    private void Folder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Current.SaveFolder);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer"
                         : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                Arguments = $"\"{Settings.Current.SaveFolder}\"",
                UseShellExecute = false,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private void Lang_Click(object? sender, RoutedEventArgs e)
    {
        Loc.Language = Loc.Language == "ko" ? "en" : "ko";
        Settings.Current.Language = Loc.Language;
        Settings.Save();
        ApplyTexts();
    }

    // ── 상태 표시 ─────────────────────────────────────────────────────────
    private async void Status(string msg)
    {
        TxtStatus.Text = msg;
        await Task.Delay(5000);
        if (TxtStatus.Text == msg) TxtStatus.Text = "";
    }
}
