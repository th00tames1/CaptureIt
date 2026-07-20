using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CaptureIt.Models;
using CaptureIt.Services;

namespace CaptureIt;

/// <summary>
/// 결과창(편집기). 알캡처 결과창을 벤치마크 — 우측에 최근 캡처 목록을 두고
/// 항목을 클릭하면 언제든 다시 편집할 수 있다. 창은 닫아도 숨겨질 뿐 상태를 유지한다.
/// </summary>
public partial class EditorWindow : Window
{
    private enum Tool { Select, Pen, Highlight, Line, Arrow, Rect, Ellipse, Text, Number, Mosaic, Eraser, Crop }

    /// <summary>실행 취소 단위: 캔버스에 요소를 추가(IsAdd=true)했거나 지웠거나. Index는 z순서 복원용.</summary>
    private readonly record struct EditAction(bool IsAdd, UIElement Element, int Index);

    private readonly MainWindow _main;
    private HistoryItem? _currentItem;
    private BitmapSource? _image;
    private Tool _tool = Tool.Select;
    private Color _color = (Color)ColorConverter.ConvertFromString("#EF4444");
    private double _thickness = 4;
    private bool _dirty;
    private int _numberCounter = 1;

    private readonly Stack<EditAction> _undo = new();
    private readonly Stack<EditAction> _redo = new();

    // 드래그 상태
    private bool _drawing;
    private Point _start;
    private Shape? _previewShape;
    private Polyline? _polyline;
    private TextBox? _activeTextBox;
    private bool _syncingSelection;

    private double _zoom = 1.0;

    private AppSettings S => App.Settings;

    public EditorWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        HistoryList.ItemsSource = HistoryService.Items;
        HistoryService.Items.CollectionChanged += (_, _) => UpdateSidebarState();
        UpdateSidebarState();
        UpdateEmptyState();
        KeyDown += OnKeyDown;
        Closing += OnClosing;
        Loc.LanguageChanged += () => { RefreshTitle(); UpdateSidebarState(); };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 결과창은 닫아도 숨기기만 한다 (목록·편집 상태 유지). 앱 종료 시에만 실제로 닫힌다.
        FlattenPendingEdits();
        e.Cancel = true;
        Hide();
    }

    // ── 항목 로드/상태 ────────────────────────────────────────────────────
    /// <summary>목록 항목을 편집 캔버스에 로드한다. 미저장 편집은 해당 항목에 자동 보존.</summary>
    public void ShowItem(HistoryItem item)
    {
        if (ReferenceEquals(item, _currentItem)) { SyncSelection(); return; }

        FlattenPendingEdits();

        var img = item.LoadFull();
        if (img == null)
        {
            // CollectionChanged 디스패치 중 재진입(Items 수정)을 피하기 위해 삭제를 지연한다
            Dispatcher.BeginInvoke(() => HistoryService.Delete(item));
            return;
        }

        _currentItem = item;
        HistoryService.Pinned = item;   // 100장 초과 정리에서 편집 중 항목 보호
        _image = img;
        ResetCanvasState();
        _numberCounter = 1;             // 새 항목에서 번호 스탬프 다시 1부터
        ApplyImage();
        Dispatcher.BeginInvoke(FitZoom, DispatcherPriority.Loaded);
        SyncSelection();
        UpdateEmptyState();
    }

    /// <summary>미저장 주석이 있으면 현재 항목 이미지에 합성해 목록에 보존한다.</summary>
    private void FlattenPendingEdits()
    {
        CancelActiveDrawing();
        CommitActiveText();
        if (!_dirty || _currentItem == null || _image == null) return;
        if (!HistoryService.Items.Contains(_currentItem)) { ResetCanvasState(); return; }   // 목록에서 이미 제거됨
        var flat = Flatten();
        HistoryService.UpdateImage(_currentItem, flat);
        _image = flat;
        ResetCanvasState();
        ApplyImage();
    }

    private void ResetCanvasState()
    {
        // 주의: _numberCounter는 여기서 초기화하지 않는다 — 저장/자르기 후 같은 이미지 위에서
        // 번호가 이어져야 하므로, 항목 전환 시에만 명시적으로 1로 되돌린다.
        DrawCanvas.Children.Clear();
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        _activeTextBox = null;
        _drawing = false;
        _previewShape = null;
        _polyline = null;
    }

    private void SyncSelection()
    {
        _syncingSelection = true;
        HistoryList.SelectedItem = _currentItem;
        _syncingSelection = false;
    }

    private void UpdateSidebarState()
    {
        LblSidebarCount.Text = Loc.F("History.Count", HistoryService.Items.Count, HistoryService.MaxItems);
        SidebarEmpty.Visibility = HistoryService.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 현재 항목이 목록에서 사라졌으면 다음 항목 또는 빈 상태로
        if (_currentItem != null && !HistoryService.Items.Contains(_currentItem))
        {
            _currentItem = null;
            HistoryService.Pinned = null;
            _image = null;
            ResetCanvasState();
            _numberCounter = 1;
            var next = HistoryService.Items.FirstOrDefault();
            if (next != null) ShowItem(next);
            else { ApplyImage(); UpdateEmptyState(); }
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = _currentItem == null;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
        BtnSave.IsEnabled = BtnSaveAs.IsEnabled = BtnCopy.IsEnabled = !empty;
    }

    private void RefreshTitle()
    {
        Title = _image != null
            ? $"{Loc.Get("Editor.Title")} — {_image.PixelWidth}×{_image.PixelHeight}"
            : Loc.Get("Editor.Title");
    }

    // ── 이미지/줌 ─────────────────────────────────────────────────────────
    private void ApplyImage()
    {
        if (_image == null)
        {
            BaseImage.Source = null;
            TxtImgSize.Text = "";
            RefreshTitle();
            return;
        }
        BaseImage.Source = _image;
        BaseImage.Width = _image.PixelWidth;
        BaseImage.Height = _image.PixelHeight;
        DrawCanvas.Width = _image.PixelWidth;
        DrawCanvas.Height = _image.PixelHeight;
        CanvasHost.Width = _image.PixelWidth;
        CanvasHost.Height = _image.PixelHeight;
        TxtImgSize.Text = $"{_image.PixelWidth} × {_image.PixelHeight} px";
        RefreshTitle();
    }

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.1, 8.0);
        CanvasHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        TxtZoom.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void FitZoom()
    {
        if (_image == null) { SetZoom(1); return; }
        double availW = Scroller.ViewportWidth - 48;
        double availH = Scroller.ViewportHeight - 48;
        if (availW <= 0 || availH <= 0) { SetZoom(1); return; }
        double z = Math.Min(availW / _image.PixelWidth, availH / _image.PixelHeight);
        SetZoom(Math.Min(1.0, z));   // 작은 이미지는 100%
    }

    private void Scroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
    }

    // ── 도구 상태 ─────────────────────────────────────────────────────────
    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            CancelActiveDrawing();
            CommitActiveText();
            _tool = Enum.Parse<Tool>(tag);
            if (DrawCanvas == null) return;   // XAML 초기화 중에는 캔버스가 아직 없음
            DrawCanvas.Cursor = _tool switch
            {
                Tool.Select => Cursors.Arrow,
                Tool.Text => Cursors.IBeam,
                Tool.Eraser => Cursors.Hand,
                _ => Cursors.Cross
            };
        }
    }

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string hex })
        {
            _color = (Color)ColorConverter.ConvertFromString(hex);
            if (_activeTextBox != null) _activeTextBox.Foreground = new SolidColorBrush(_color);
        }
    }

    private void Thickness_Changed(object sender, SelectionChangedEventArgs e)
    {
        _thickness = CmbThickness.SelectedIndex switch { 0 => 2, 1 => 4, 2 => 7, 3 => 12, _ => 4 };
        if (_activeTextBox != null) _activeTextBox.FontSize = FontSizeFromThickness();
    }

    private double FontSizeFromThickness() => 12 + _thickness * 3;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_activeTextBox != null && e.Key != Key.Escape) return;   // 입력 중엔 단축키 무시

        if (e.Key == Key.Escape)
        {
            if (_activeTextBox != null) { CommitActiveText(); return; }
            if (_drawing) { CancelActiveDrawing(); return; }   // 그리던 도형 취소
            ToolSelect.IsChecked = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Undo_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Y: Redo_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.S: Save_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.C: Copy_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            }
        }
    }

    private void NewCapture_Click(object sender, RoutedEventArgs e) => _ = _main.StartRegionCapture();

    /// <summary>드래그 중이던 미완성 도형을 캔버스에서 제거한다 (Esc·도구 전환 시).</summary>
    private void CancelActiveDrawing()
    {
        if (!_drawing) return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();
        if (_previewShape != null) { DrawCanvas.Children.Remove(_previewShape); _previewShape = null; }
        if (_polyline != null) { DrawCanvas.Children.Remove(_polyline); _polyline = null; }
    }

    // ── 그리기 ────────────────────────────────────────────────────────────
    private SolidColorBrush StrokeBrush() => new(_color);

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentItem == null || _image == null) return;
        var pos = e.GetPosition(DrawCanvas);

        if (_tool == Tool.Text)
        {
            CommitActiveText();
            StartTextBox(pos);
            return;
        }
        if (_tool == Tool.Number)
        {
            CommitActiveText();
            StampNumber(pos);
            return;
        }
        if (_tool == Tool.Select) return;

        CommitActiveText();
        _drawing = true;
        _start = pos;
        DrawCanvas.CaptureMouse();

        switch (_tool)
        {
            case Tool.Eraser:
                EraseAt(pos);
                break;

            case Tool.Pen:
            case Tool.Highlight:
                _polyline = new Polyline
                {
                    Stroke = _tool == Tool.Highlight
                        ? new SolidColorBrush(Color.FromArgb(102, _color.R, _color.G, _color.B))
                        : StrokeBrush(),
                    StrokeThickness = _tool == Tool.Highlight ? _thickness * 3.5 : _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _polyline.Points.Add(pos);
                DrawCanvas.Children.Add(_polyline);
                break;

            case Tool.Line:
                _previewShape = new Line
                {
                    X1 = pos.X, Y1 = pos.Y, X2 = pos.X, Y2 = pos.Y,
                    Stroke = StrokeBrush(), StrokeThickness = _thickness,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                DrawCanvas.Children.Add(_previewShape);
                break;

            case Tool.Arrow:
                _previewShape = new Path
                {
                    Stroke = StrokeBrush(), StrokeThickness = _thickness,
                    Fill = StrokeBrush(),
                    StrokeLineJoin = PenLineJoin.Round
                };
                DrawCanvas.Children.Add(_previewShape);
                break;

            case Tool.Rect:
                _previewShape = new Rectangle
                {
                    Stroke = StrokeBrush(), StrokeThickness = _thickness,
                    RadiusX = 2, RadiusY = 2, Fill = Brushes.Transparent
                };
                PlaceShape(_previewShape, new Rect(pos, pos));
                DrawCanvas.Children.Add(_previewShape);
                break;

            case Tool.Ellipse:
                _previewShape = new Ellipse
                {
                    Stroke = StrokeBrush(), StrokeThickness = _thickness, Fill = Brushes.Transparent
                };
                PlaceShape(_previewShape, new Rect(pos, pos));
                DrawCanvas.Children.Add(_previewShape);
                break;

            case Tool.Mosaic:
            case Tool.Crop:
                _previewShape = new Rectangle
                {
                    Stroke = _tool == Tool.Crop ? Brushes.White : Brushes.Gray,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(40, 45, 125, 246))
                };
                PlaceShape(_previewShape, new Rect(pos, pos));
                DrawCanvas.Children.Add(_previewShape);
                break;
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var pos = e.GetPosition(DrawCanvas);
        pos.X = Math.Clamp(pos.X, 0, DrawCanvas.Width);
        pos.Y = Math.Clamp(pos.Y, 0, DrawCanvas.Height);

        switch (_tool)
        {
            case Tool.Eraser:
                EraseAt(pos);
                break;

            case Tool.Pen:
            case Tool.Highlight:
                _polyline?.Points.Add(pos);
                break;

            case Tool.Line when _previewShape is Line ln:
                ln.X2 = pos.X; ln.Y2 = pos.Y;
                break;

            case Tool.Arrow when _previewShape is Path p:
                p.Data = BuildArrowGeometry(_start, pos, _thickness);
                break;

            case Tool.Rect:
            case Tool.Ellipse:
            case Tool.Mosaic:
            case Tool.Crop:
                if (_previewShape != null) PlaceShape(_previewShape, new Rect(_start, pos));
                break;
        }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();
        var pos = e.GetPosition(DrawCanvas);
        var rect = new Rect(_start, pos);

        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Highlight:
                if (_polyline != null && _polyline.Points.Count > 1) RecordAdd(_polyline);
                else if (_polyline != null) DrawCanvas.Children.Remove(_polyline);
                _polyline = null;
                break;

            case Tool.Line:
            case Tool.Arrow:
            case Tool.Rect:
            case Tool.Ellipse:
                if (_previewShape != null)
                {
                    bool tiny = rect.Width < 3 && rect.Height < 3;
                    if (tiny) DrawCanvas.Children.Remove(_previewShape);
                    else RecordAdd(_previewShape);
                }
                _previewShape = null;
                break;

            case Tool.Mosaic:
                if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
                _previewShape = null;
                if (rect.Width >= 4 && rect.Height >= 4) ApplyMosaic(rect);
                break;

            case Tool.Crop:
                if (_previewShape != null) DrawCanvas.Children.Remove(_previewShape);
                _previewShape = null;
                if (rect.Width >= 8 && rect.Height >= 8) ApplyCrop(rect);
                break;
        }
    }

    private static void PlaceShape(Shape s, Rect r)
    {
        Canvas.SetLeft(s, r.X);
        Canvas.SetTop(s, r.Y);
        s.Width = Math.Max(1, r.Width);
        s.Height = Math.Max(1, r.Height);
    }

    /// <summary>줄기 + 삼각 촉으로 이루어진 화살표 geometry.</summary>
    private static Geometry BuildArrowGeometry(Point from, Point to, double thickness)
    {
        var dir = to - from;
        double len = dir.Length;
        if (len < 1) return Geometry.Empty;
        dir.Normalize();

        double headLen = Math.Max(10, thickness * 3.2);
        double headWidth = headLen * 0.75;
        Point basePt = to - dir * headLen;
        var perp = new Vector(-dir.Y, dir.X);

        var geo = new GeometryGroup();
        geo.Children.Add(new LineGeometry(from, basePt));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(basePt + perp * headWidth / 2, true, true);
            ctx.LineTo(basePt - perp * headWidth / 2, true, true);
        }
        geo.Children.Add(head);
        return geo;
    }

    // ── 번호 스탬프 ───────────────────────────────────────────────────────
    private void StampNumber(Point pos)
    {
        double d = 22 + _thickness * 2.5;
        bool lightColor = _color.R + _color.G + _color.B > 600;   // 흰색 계열이면 글자는 어둡게
        var stamp = new Grid { Width = d, Height = d, Tag = _numberCounter };   // Tag = 실행취소 시 카운터 복원용
        stamp.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(_color),
            Stroke = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            StrokeThickness = 1
        });
        stamp.Children.Add(new TextBlock
        {
            Text = _numberCounter.ToString(),
            Foreground = lightColor ? new SolidColorBrush(Color.FromRgb(17, 24, 39)) : Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = d * 0.52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(stamp, pos.X - d / 2);
        Canvas.SetTop(stamp, pos.Y - d / 2);
        DrawCanvas.Children.Add(stamp);
        RecordAdd(stamp);
        _numberCounter++;
    }

    // ── 지우개 ────────────────────────────────────────────────────────────
    /// <summary>포인트에 있는 주석(캔버스 자식)을 위에서부터 하나 지운다.</summary>
    private void EraseAt(Point p)
    {
        UIElement? hit = null;
        VisualTreeHelper.HitTest(DrawCanvas, null, result =>
        {
            DependencyObject? cur = result.VisualHit;
            while (cur != null)
            {
                var parent = VisualTreeHelper.GetParent(cur);
                if (ReferenceEquals(parent, DrawCanvas) && cur is UIElement el)
                {
                    hit = el;
                    return HitTestResultBehavior.Stop;
                }
                cur = parent;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));

        if (hit != null)
        {
            int index = DrawCanvas.Children.IndexOf(hit);
            DrawCanvas.Children.Remove(hit);
            _undo.Push(new EditAction(false, hit, index));
            _redo.Clear();
            _dirty = DrawCanvas.Children.Count > 0 || _undo.Count > 0;
            if (_currentItem != null) HistoryService.ClearImageRedo(_currentItem);
        }
    }

    // ── 텍스트 도구 ───────────────────────────────────────────────────────
    private void StartTextBox(Point pos)
    {
        var tb = new TextBox
        {
            MinWidth = 40,
            FontSize = FontSizeFromThickness(),
            FontFamily = new FontFamily("Malgun Gothic"),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(30, 45, 125, 246)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 45, 125, 246)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = true
        };
        Canvas.SetLeft(tb, pos.X);
        Canvas.SetTop(tb, pos.Y);
        DrawCanvas.Children.Add(tb);
        _activeTextBox = tb;
        tb.LostFocus += (_, _) => CommitActiveText();
        // AcceptsReturn이 개행을 넣기 전에 가로채야 하므로 PreviewKeyDown 사용
        tb.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            { ke.Handled = true; CommitActiveText(); }
        };
        tb.Loaded += (_, _) => tb.Focus();
    }

    private void CommitActiveText()
    {
        if (_activeTextBox == null) return;
        var tb = _activeTextBox;
        _activeTextBox = null;

        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        string text = tb.Text;
        DrawCanvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(text)) return;

        var block = new TextBlock
        {
            Text = text,
            FontSize = tb.FontSize,
            FontFamily = tb.FontFamily,
            FontWeight = tb.FontWeight,
            Foreground = tb.Foreground
        };
        Canvas.SetLeft(block, x + 3);   // TextBox 패딩 보정
        Canvas.SetTop(block, y + 3);
        DrawCanvas.Children.Add(block);
        RecordAdd(block);
    }

    // ── 모자이크 / 자르기 ─────────────────────────────────────────────────
    private void ApplyMosaic(Rect r)
    {
        if (_image == null) return;
        var px = ClampToImage(r);
        if (px.Width < 2 || px.Height < 2) return;

        int block = (int)Math.Max(6, _thickness * 2.5);
        var crop = new CroppedBitmap(_image, px);
        int smallW = Math.Max(1, px.Width / block);
        int smallH = Math.Max(1, px.Height / block);
        var small = new TransformedBitmap(crop,
            new ScaleTransform((double)smallW / px.Width, (double)smallH / px.Height));

        var img = new Image
        {
            Source = small,
            Width = px.Width,
            Height = px.Height,
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, px.X);
        Canvas.SetTop(img, px.Y);
        DrawCanvas.Children.Add(img);
        RecordAdd(img);
    }

    private void ApplyCrop(Rect r)
    {
        if (_image == null || _currentItem == null) return;
        var px = ClampToImage(r);
        if (px.Width < 4 || px.Height < 4) return;

        var flat = Flatten();
        var cropped = new CroppedBitmap(flat, px);
        cropped.Freeze();
        _image = cropped;

        // 자르기는 즉시 항목에 반영 (목록 썸네일도 갱신)
        HistoryService.UpdateImage(_currentItem, cropped);
        ResetCanvasState();
        ApplyImage();
        FitZoom();
        Status(Loc.Get("Editor.Status.Cropped"));
        ToolSelect.IsChecked = true;
    }

    private Int32Rect ClampToImage(Rect r)
    {
        int x = Math.Clamp((int)Math.Round(r.X), 0, _image!.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(r.Y), 0, _image.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(r.Width), 0, _image.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(r.Height), 0, _image.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    // ── 실행 취소 / 초기화 ────────────────────────────────────────────────
    private void RecordAdd(UIElement el)
    {
        _undo.Push(new EditAction(true, el, DrawCanvas.Children.IndexOf(el)));
        _redo.Clear();
        _dirty = true;
        if (_currentItem != null) HistoryService.ClearImageRedo(_currentItem);
    }

    /// <summary>원래 z순서 위치로 되돌려 넣는다.</summary>
    private void InsertAt(UIElement el, int index) =>
        DrawCanvas.Children.Insert(Math.Clamp(index, 0, DrawCanvas.Children.Count), el);

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        CommitActiveText();
        if (_undo.Count == 0)
        {
            // 캔버스에 되돌릴 것이 없으면 이미지에 구워진 편집(합성·자르기·저장)을 되돌린다
            if (_currentItem != null && HistoryService.UndoImage(_currentItem))
                ReloadAfterImageHistory(Loc.Get("Editor.Status.ImageUndo"));
            return;
        }
        var action = _undo.Pop();
        if (action.IsAdd)
        {
            DrawCanvas.Children.Remove(action.Element);
            if (action.Element is Grid { Tag: int n }) _numberCounter = n;   // 번호 스탬프 카운터 복원
        }
        else InsertAt(action.Element, action.Index);
        _redo.Push(action);
        _dirty = _undo.Count > 0 || DrawCanvas.Children.Count > 0;
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0)
        {
            // 되돌렸던 이미지 편집을 다시 적용 (캔버스에 새 주석이 없을 때만)
            if (_currentItem != null && DrawCanvas.Children.Count == 0 &&
                HistoryService.RedoImage(_currentItem))
                ReloadAfterImageHistory(Loc.Get("Editor.Status.ImageRedo"));
            return;
        }
        var action = _redo.Pop();
        if (action.IsAdd)
        {
            InsertAt(action.Element, action.Index);
            if (action.Element is Grid { Tag: int n }) _numberCounter = n + 1;
        }
        else DrawCanvas.Children.Remove(action.Element);
        _undo.Push(action);
        _dirty = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        CommitActiveText();
        if (DrawCanvas.Children.Count == 0) return;
        var answer = MessageBox.Show(this, Loc.Get("Editor.ResetConfirm"), Loc.Get("App.Name"),
                                     MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        ResetCanvasState();
        Status(Loc.Get("Editor.Status.Cleared"));
    }

    // ── 저장/복사 ─────────────────────────────────────────────────────────
    /// <summary>이미지 + 주석을 픽셀 단위로 합성한다.</summary>
    private BitmapSource Flatten()
    {
        CommitActiveText();

        // 줌을 잠시 1로 되돌려 원본 크기로 렌더링
        var savedZoom = _zoom;
        CanvasHost.LayoutTransform = Transform.Identity;
        CanvasHost.UpdateLayout();

        int w = _image!.PixelWidth, h = _image.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            // Viewbox를 절대 좌표로 고정: DropShadowEffect가 부풀린 경계 때문에
            // 픽셀이 밀리거나 리샘플링되는 것을 방지한다 (저장 반복 시 열화 원인)
            dc.DrawRectangle(new VisualBrush(CanvasHost)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h)
            }, null, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        SetZoom(savedZoom);
        return rtb;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null || _currentItem == null) return;
        try
        {
            var flat = Flatten();
            var path = S.NewFilePath();
            CaptureService.SaveToFile(flat, path, S.JpgQuality);
            if (S.AlwaysCopyToClipboard) CaptureService.CopyToClipboard(flat);

            // 편집 결과를 목록에도 반영하고, 이후 편집은 합성본 위에서 이어간다
            HistoryService.UpdateImage(_currentItem, flat);
            _image = flat;
            ResetCanvasState();
            ApplyImage();
            Status(Loc.F("Editor.Status.Saved", path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{Loc.Get("Editor.SaveFailed")}\n{ex.Message}", Loc.Get("App.Name"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("Editor.SaveAsTitle"),
            FileName = $"{S.FileNamePrefix}_{DateTime.Now:yyyy-MM-dd_HHmmss}",
            Filter = $"{Loc.Get("Editor.FilterPng")}|*.png|{Loc.Get("Editor.FilterJpg")}|*.jpg|{Loc.Get("Editor.FilterBmp")}|*.bmp",
            InitialDirectory = S.SaveFolder
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var flat = Flatten();
            CaptureService.SaveToFile(flat, dlg.FileName, S.JpgQuality);
            Status(Loc.F("Editor.Status.Saved", dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{Loc.Get("Editor.SaveFailed")}\n{ex.Message}", Loc.Get("App.Name"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        // 편집 중 주석을 항목에 합성한 뒤(되돌리기 가능) 파일 포맷까지 포함해 복사
        if (_currentItem == null) return;
        CopyHistoryItem(_currentItem);
    }

    /// <summary>이미지 단계 undo/redo 후 캔버스를 새 이미지로 다시 세운다.</summary>
    private void ReloadAfterImageHistory(string statusMsg)
    {
        if (_currentItem == null) return;
        var img = _currentItem.LoadFull();
        if (img == null) return;
        _image = img;
        ResetCanvasState();
        ApplyImage();
        FitZoom();
        Status(statusMsg);
    }

    // ── 캡처 목록(사이드바) ───────────────────────────────────────────────
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (HistoryList.SelectedItem is HistoryItem item)
            ShowItem(item);
    }

    private void HistoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && HistoryList.SelectedItem is HistoryItem item)
        {
            HistoryService.Delete(item);
            e.Handled = true;
        }
        // 목록에서 바로 Ctrl+C → 다른 앱에 즉시 붙여넣기 (이미지+PNG+파일 동시 복사)
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control
                 && HistoryList.SelectedItem is HistoryItem copyItem)
        {
            CopyHistoryItem(copyItem);
            e.Handled = true;
        }
    }

    private void CopyHistoryItem(HistoryItem item)
    {
        // 지금 편집 중인 항목이면 그리던 주석을 먼저 파일에 반영해 화면과 동일한 이미지를 복사한다
        FlattenPendingEdits();
        if (item.LoadFull() is { } img)
        {
            bool ok = CaptureService.CopyToClipboard(img, item.FilePath);
            Status(Loc.Get(ok ? "Editor.Status.Copied" : "Msg.CopyFailed"));
        }
    }

    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryItem item })
        {
            e.Handled = true;
            HistoryService.Delete(item);
        }
    }

    private void HistoryDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem item)
            HistoryService.Delete(item);
    }

    private void HistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem item)
            CopyHistoryItem(item);
    }

    private void HistorySaveOne_Click(object sender, RoutedEventArgs e)
    {
        FlattenPendingEdits();   // 편집 중 항목이면 화면 그대로 저장되도록
        if (HistoryList.SelectedItem is not HistoryItem item || item.LoadFull() is not { } img) return;
        try
        {
            var path = S.NewFilePath();
            CaptureService.SaveToFile(img, path, S.JpgQuality);
            Status(Loc.F("Editor.Status.Saved", path));
        }
        catch { }
    }

    private void HistorySaveAll_Click(object sender, RoutedEventArgs e)
    {
        FlattenPendingEdits();
        int saved = 0;
        foreach (var item in HistoryService.Items.ToList())
        {
            if (item.LoadFull() is not { } img) continue;
            try
            {
                CaptureService.SaveToFile(img, S.NewFilePath(), S.JpgQuality);
                saved++;
            }
            catch { }
        }
        Status(Loc.F("Editor.Status.SavedAll", saved));
    }

    private void HistoryDeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryService.Items.Count == 0) return;
        var answer = MessageBox.Show(this, Loc.Get("History.DeleteAllConfirm"), Loc.Get("App.Name"),
                                     MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _currentItem = null;   // 현재 항목 정리 로직이 재로드하지 않도록 먼저 비운다
        HistoryService.Pinned = null;
        _image = null;
        ResetCanvasState();
        _numberCounter = 1;
        HistoryService.Clear();
        ApplyImage();
        UpdateEmptyState();
    }

    private async void Status(string msg)
    {
        TxtStatus.Text = msg;
        await Task.Delay(4000);
        if (TxtStatus.Text == msg) TxtStatus.Text = "";
    }
}
