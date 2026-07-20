using System.Windows;
using System.Windows.Input;
using CaptureIt.Models;
using CaptureIt.Services;

namespace CaptureIt;

/// <summary>
/// 고정 크기 캡처용 라이브 뷰파인더.
/// 화면을 정지시키지 않고 항상 위에 떠 있는 틀 — 중앙은 클릭이 통과해 아래 앱을
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
        // 크기 복원은 첫 레이아웃 이후에 — 칩 높이(FrameMarginY)가 그때야 실측된다.
        // SourceInitialized 시점엔 ActualHeight=0이라 세션마다 안쪽 영역이 줄어드는 버그가 있었다.
        Loaded += (_, _) => { RestorePlacement(); SyncBoxes(); Activate(); };
        SizeChanged += (_, _) => SyncBoxes();
        Closing += (_, _) => SavePlacement();
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

    // ── 하단 가장자리 리사이즈 (칩이 창 바닥을 차지해 WindowChrome이 못 잡는 부분) ──
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private void BottomStrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        const int WM_NCLBUTTONDOWN = 0x00A1, HTBOTTOM = 15;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTBOTTOM, IntPtr.Zero);
        e.Handled = true;
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
