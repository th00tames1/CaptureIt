using System.Windows;
using System.Windows.Input;
using CaptureIt.Models;
using CaptureIt.Services;

namespace CaptureIt;

/// <summary>
/// 고정 크기 캡처용 라이브 뷰파인더.
/// 화면을 정지시키지 않고 항상 위에 떠 있는 틀: 중앙은 클릭이 통과해 아래 앱을
/// 그대로 조작할 수 있고, 내용이 바뀔 때마다 캡처 버튼(Enter)으로 반복 캡처한다.
/// </summary>
public partial class ViewfinderWindow : Window
{
    private readonly MainWindow _main;
    private bool _syncingBoxes;
    private double _scale = 1.0;

    private AppSettings S => App.Settings;

    public ViewfinderWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        SourceInitialized += (_, _) =>
        {
            _scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        };
        // 크기 복원은 첫 레이아웃 이후에: 칩 높이(FrameMarginY)가 그때야 실측된다.
        // SourceInitialized 시점엔 ActualHeight=0이라 세션마다 안쪽 영역이 줄어드는 버그가 있었다.
        Loaded += (_, _) => { RestorePlacement(); SyncBoxes(); Activate(); };
        SizeChanged += (_, _) => SyncBoxes();
        Closing += (_, _) => SavePlacement();
        MouseMove += Window_MouseMove;
        MouseLeftButtonUp += Window_MouseUp;
    }

    // ── 배치/크기 ─────────────────────────────────────────────────────────
    private void RestorePlacement()
    {
        // 저장된 안쪽 크기(물리 px) → 창 크기 (테두리·칩 여백 포함)
        double innerW = Math.Max(60, S.FixedWidth) / _scale;
        double innerH = Math.Max(60, S.FixedHeight) / _scale;
        Width = Math.Max(MinWidth, innerW + FrameMarginX);
        Height = Math.Max(MinHeight, innerH + FrameMarginY);

        var work = SystemParameters.WorkArea;
        Left = S.FixedLeft ?? work.Left + (work.Width - Width) / 2;
        Top = S.FixedTop ?? work.Top + (work.Height - Height) / 2;
        WindowLayout.ClampToWorkArea(this);
    }

    // ── 수동 리사이즈 (꼭지점 원형 핸들 + 하단 가장자리) ──────────────────
    // WM_NCLBUTTONDOWN 네이티브 방식 대신 마우스 캡처로 직접 계산해 부드럽고 예측 가능하게.
    private enum ResizeEdge { None, TL, TR, BL, BR, Left, Right, Top, Bottom }
    private ResizeEdge _resizeEdge = ResizeEdge.None;
    private Point _resizeStartScreen;   // 물리 px
    private Rect _resizeStartRect;      // DIU (Left/Top/Width/Height)

    /// <summary>꼭지점 원형 핸들 드래그: Tag(13=TL,14=TR,16=BL,17=BR)로 대각 리사이즈.</summary>
    private void Corner_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: string tag })
        {
            _resizeEdge = tag switch
            {
                "13" => ResizeEdge.TL, "14" => ResizeEdge.TR,
                "16" => ResizeEdge.BL, "17" => ResizeEdge.BR, _ => ResizeEdge.None
            };
            BeginResize(e);
        }
        e.Handled = true;
    }

    private void Edge_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: string tag })
        {
            _resizeEdge = tag switch
            {
                "left" => ResizeEdge.Left, "right" => ResizeEdge.Right,
                "top" => ResizeEdge.Top, "bottom" => ResizeEdge.Bottom, _ => ResizeEdge.None
            };
            BeginResize(e);
        }
        e.Handled = true;
    }

    private void BeginResize(MouseButtonEventArgs e)
    {
        if (_resizeEdge == ResizeEdge.None) return;
        _resizeStartScreen = PointToScreen(e.GetPosition(this));
        _resizeStartRect = new Rect(Left, Top, Width, Height);
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeEdge == ResizeEdge.None || !IsMouseCaptured) return;

        var cur = PointToScreen(e.GetPosition(this));
        double dx = (cur.X - _resizeStartScreen.X) / _scale;   // 물리 px → DIU
        double dy = (cur.Y - _resizeStartScreen.Y) / _scale;

        double nx = _resizeStartRect.X, ny = _resizeStartRect.Y;
        double nw = _resizeStartRect.Width, nh = _resizeStartRect.Height;

        bool left = _resizeEdge is ResizeEdge.TL or ResizeEdge.BL or ResizeEdge.Left;
        bool top = _resizeEdge is ResizeEdge.TL or ResizeEdge.TR or ResizeEdge.Top;
        bool right = _resizeEdge is ResizeEdge.TR or ResizeEdge.BR or ResizeEdge.Right;
        bool bottom = _resizeEdge is ResizeEdge.BL or ResizeEdge.BR or ResizeEdge.Bottom;

        if (left) { nx += dx; nw -= dx; }
        if (right) { nw += dx; }
        if (top) { ny += dy; nh -= dy; }
        if (bottom) { nh += dy; }

        // 최소 크기 보장 (왼쪽/위 가장자리는 위치도 함께 보정)
        if (nw < MinWidth) { if (left) nx -= (MinWidth - nw); nw = MinWidth; }
        if (nh < MinHeight) { if (top) ny -= (MinHeight - nh); nh = MinHeight; }

        Left = nx; Top = ny; Width = nw; Height = nh;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeEdge == ResizeEdge.None) return;
        _resizeEdge = ResizeEdge.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SavePlacement();
    }

    private void SavePlacement()
    {
        var inner = GetInnerPhysicalRect();
        if (inner.Width > 0)
        {
            S.FixedWidth = inner.Width;
            S.FixedHeight = inner.Height;
        }
        S.FixedLeft = Left;
        S.FixedTop = Top;
        S.Save();
    }

    /// <summary>프레임 안쪽(실제 캡처 영역) 좌우/상하 여백 (DIU).</summary>
    private double FrameMarginX => 18;                                   // 9 + 9
    private double FrameMarginY => 18 + (Chip?.ActualHeight ?? 40) + 6;  // 위 9 + 아래 9 + 칩 + 간격

    /// <summary>안쪽 영역의 화면 물리 픽셀 사각형.</summary>
    public System.Drawing.Rectangle GetInnerPhysicalRect()
    {
        try
        {
            var tl = InnerArea.PointToScreen(new Point(0, 0));
            return new System.Drawing.Rectangle(
                (int)Math.Round(tl.X), (int)Math.Round(tl.Y),
                (int)Math.Round(InnerArea.ActualWidth * _scale),
                (int)Math.Round(InnerArea.ActualHeight * _scale));
        }
        catch { return System.Drawing.Rectangle.Empty; }
    }

    private void SyncBoxes()
    {
        if (_syncingBoxes) return;
        _syncingBoxes = true;
        var inner = GetInnerPhysicalRect();
        if (!WBox.IsKeyboardFocused) WBox.Text = inner.Width.ToString();
        if (!HBox.IsKeyboardFocused) HBox.Text = inner.Height.ToString();
        _syncingBoxes = false;
    }

    private void ApplySizeInput()
    {
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (int.TryParse(WBox.Text.Trim(), out int w))
            Width = Math.Clamp(w, 40, virtualScreen.Width) / _scale + FrameMarginX;
        if (int.TryParse(HBox.Text.Trim(), out int h))
            Height = Math.Clamp(h, 40, virtualScreen.Height) / _scale + FrameMarginY;
        WindowLayout.ClampToWorkArea(this);
    }

    // ── 이벤트 ────────────────────────────────────────────────────────────
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.Enter && !WBox.IsKeyboardFocused && !HBox.IsKeyboardFocused)
        {
            Capture_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Chip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            SavePlacement();
        }
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySizeInput();
            Keyboard.ClearFocus();
            Focus();          // Enter·Esc 단축키가 다시 동작하도록 창으로 포커스 복귀
            e.Handled = true;
        }
    }

    private void SizeBox_LostFocus(object sender, RoutedEventArgs e) => ApplySizeInput();

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        var rect = GetInnerPhysicalRect();
        if (rect.Width < 4 || rect.Height < 4) return;
        SavePlacement();
        await _main.CaptureViewfinderRect(rect);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
