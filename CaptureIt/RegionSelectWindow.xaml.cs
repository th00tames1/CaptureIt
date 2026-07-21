using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CaptureIt.Services;

namespace CaptureIt;

/// <summary>
/// 정지된 화면 위에서 캡처 대상을 고르는 전체 화면 오버레이.
/// Region: 드래그로 영역 선택 · Window/ScrollWindow: 창 하이라이트 후 클릭 ·
/// Element: 커서 아래 UI 요소를 실시간 하이라이트 후 클릭 (클릭 통과 + 마우스 훅 + Win32 자식 창 감지).
/// 반환 좌표는 정지 이미지(가상 화면)의 픽셀 좌표.
/// </summary>
public partial class RegionSelectWindow : Window
{
    public enum SelectMode { Region, Window, Element, ScrollRegion, ScrollWindow }

    public Int32Rect? SelectedPixelRect { get; private set; }

    /// <summary>Window/ScrollWindow 모드에서 클릭으로 고른 창.</summary>
    public CaptureService.WindowInfo? SelectedWindow { get; private set; }

    private readonly BitmapSource _frozen;
    private readonly SelectMode _mode;
    private readonly List<CaptureService.WindowInfo> _windows;
    private readonly System.Drawing.Rectangle _virtualBounds;   // 물리 픽셀

    private double _scale = 1.0;        // DIU → 물리 픽셀 배율
    private bool _dragging;
    private Point _dragStart;           // DIU
    private Rect _selection;            // DIU
    private CaptureService.WindowInfo? _hoverWindow;
    private System.Drawing.Rectangle? _hoverElementRect;        // 물리 픽셀 (Element 모드)

    // Element 모드: 클릭 통과 + 저수준 마우스 훅
    private IntPtr _mouseHook = IntPtr.Zero;
    private HookProc? _hookProc;        // GC 방지용 참조 유지
    private bool _elementUpdateQueued;  // 이동 이벤트 폭주 시 프레임당 1회만 갱신
    private bool _leftPending, _rightPending;   // DOWN을 삼킨 버튼의 UP 대기
    private bool _done;                          // 중복 확정/취소 방지

    public RegionSelectWindow(BitmapSource frozen, SelectMode mode,
                              List<CaptureService.WindowInfo>? windows = null)
    {
        InitializeComponent();
        _frozen = frozen;
        _mode = mode;
        _windows = windows ?? new();
        _virtualBounds = System.Windows.Forms.SystemInformation.VirtualScreen;

        FrozenImage.Source = _frozen;
        HintText.Text = _mode switch
        {
            SelectMode.Window => Loc.Get("Overlay.WindowHint"),
            SelectMode.Element => Loc.Get("Overlay.ElementHint"),
            SelectMode.ScrollRegion or SelectMode.ScrollWindow => Loc.Get("Overlay.ScrollHint"),
            _ => Loc.Get("Overlay.RegionHint"),
        };

        SourceInitialized += (_, _) =>
        {
            FitToVirtualScreen();
            if (_mode == SelectMode.Element) EnterElementMode();
        };
        Loaded += (_, _) => { Activate(); Focus(); };
        Closed += (_, _) => LeaveElementMode();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += (_, _) => Cancel();
    }

    /// <summary>가상 화면 전체를 덮도록 창을 배치한다 (물리 픽셀 → DIU 변환).</summary>
    private void FitToVirtualScreen()
    {
        var src = PresentationSource.FromVisual(this);
        _scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        Left = _virtualBounds.X / _scale;
        Top = _virtualBounds.Y / _scale;
        Width = _virtualBounds.Width / _scale;
        Height = _virtualBounds.Height / _scale;

        UpdateDim(Rect.Empty);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        try { DialogResult = false; } catch (InvalidOperationException) { }
        Close();
    }

    // ── 마우스 처리 (Region/Window/ScrollRegion/ScrollWindow) ──────────────
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (_mode)
        {
            case SelectMode.Window:
            case SelectMode.ScrollWindow:
                if (_hoverWindow != null)
                {
                    SelectedWindow = _hoverWindow;
                    ConfirmPhysicalRect(_hoverWindow.Bounds);
                }
                return;

            case SelectMode.Element:
                return;   // Element 모드는 훅으로 처리

            default:
                _dragging = true;
                _dragStart = e.GetPosition(this);
                CaptureMouse();
                CrossH.Visibility = CrossV.Visibility = Visibility.Collapsed;
                HintBar.Visibility = Visibility.Collapsed;
                return;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        switch (_mode)
        {
            case SelectMode.Window:
            case SelectMode.ScrollWindow:
                UpdateHoverWindow(pos);
                return;

            case SelectMode.Element:
                return;

            default:
                if (_dragging)
                {
                    _selection = new Rect(_dragStart, pos);
                    ShowSelection(_selection);
                }
                else
                {
                    CrossH.Visibility = CrossV.Visibility = Visibility.Visible;
                    CrossH.X1 = 0; CrossH.X2 = ActualWidth; CrossH.Y1 = CrossH.Y2 = pos.Y;
                    CrossV.Y1 = 0; CrossV.Y2 = ActualHeight; CrossV.X1 = CrossV.X2 = pos.X;
                }
                return;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode is not (SelectMode.Region or SelectMode.ScrollRegion) || !_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var pixelRect = ToPixelRect(_selection);
        if (pixelRect.Width < 3 || pixelRect.Height < 3)
        {
            // 실수 클릭: 선택을 지우고 계속 진행
            SelRect.Visibility = SizeLabel.Visibility = Visibility.Collapsed;
            HintBar.Visibility = Visibility.Visible;
            UpdateDim(Rect.Empty);
            return;
        }
        SelectedPixelRect = pixelRect;
        DialogResult = true;
        Close();
    }

    // ── 단위 영역(Element) 모드 ────────────────────────────────────────────
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point p);

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTTRANSPARENT = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    private HwndSource? _hwndSource;

    /// <summary>
    /// 히트 테스트만 통과시키고(WM_NCHITTEST → HTTRANSPARENT) 저수준 마우스 훅으로
    /// 이동/클릭을 받는다. 레이어드 스타일을 쓰지 않으므로 오버레이 렌더링이 그대로 유지된다.
    /// </summary>
    private void EnterElementMode()
    {
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(HitTestPassThrough);

        _hookProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
        {
            // 훅 없이 클릭 통과 창을 띄우면 사용자가 조작 불능이 된다: 즉시 중단
            Dispatcher.BeginInvoke(Cancel);
            return;
        }

        // 첫 하이라이트 (마우스가 아직 안 움직여도 현재 위치 기준으로 표시)
        Dispatcher.BeginInvoke(() =>
        {
            if (!_done && GetCursorPos(out var p)) UpdateHoverElement(p.X, p.Y);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private IntPtr HitTestPassThrough(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return HTTRANSPARENT;   // 마우스 히트 테스트가 이 창을 지나쳐 아래 창을 보게 한다
        }
        return IntPtr.Zero;
    }

    private void LeaveElementMode()
    {
        _hwndSource?.RemoveHook(HitTestPassThrough);
        _hwndSource = null;
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            switch (wParam.ToInt32())
            {
                case WM_MOUSEMOVE:
                    // 훅 안에서는 최소 작업만: 갱신을 UI 큐에 1건만 예약한다.
                    // (훅 처리가 느리면 Windows가 훅을 강제 해제해 버린다)
                    if (!_elementUpdateQueued)
                    {
                        _elementUpdateQueued = true;
                        var pt = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        Dispatcher.BeginInvoke(() =>
                        {
                            _elementUpdateQueued = false;
                            if (!_done) UpdateHoverElement(pt.X, pt.Y);
                        });
                    }
                    break;   // 이동은 삼키지 않는다 (아래 앱 호버 효과 유지)

                // DOWN에서 예약하고 짝이 되는 UP까지 삼킨 뒤 실행 :
                // 고아 UP이 아래 앱에 전달되어 클릭이 실행되는 것을 막는다.
                case WM_LBUTTONDOWN:
                    _leftPending = true;
                    return (IntPtr)1;

                case WM_LBUTTONUP when _leftPending:
                    _leftPending = false;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_done) return;
                        if (_hoverElementRect is { } r) ConfirmPhysicalRect(r);
                        else Cancel();   // 아직 요소를 못 찾았으면 조용히 종료
                    });
                    return (IntPtr)1;

                case WM_RBUTTONDOWN:
                    _rightPending = true;
                    return (IntPtr)1;

                case WM_RBUTTONUP when _rightPending:
                    _rightPending = false;
                    Dispatcher.BeginInvoke(Cancel);
                    return (IntPtr)1;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>커서 아래 UI 요소를 Win32로 즉시 찾아 하이라이트한다 (UIA와 달리 지연 없음).</summary>
    private void UpdateHoverElement(int x, int y)
    {
        var rect = CaptureService.GetElementRectAt(x, y);
        if (rect.IsEmpty) return;

        var phys = System.Drawing.Rectangle.Intersect(rect, _virtualBounds);
        if (phys.Width < 4 || phys.Height < 4) return;
        if (_hoverElementRect is { } prev && prev == phys) return;

        _hoverElementRect = phys;
        ShowSelection(PhysicalToLocal(phys));
    }

    // ── 창 캡처 모드 ───────────────────────────────────────────────────────
    private void UpdateHoverWindow(Point posDiu)
    {
        // DIU → 물리 픽셀 (가상 화면 기준 절대 좌표)
        int px = (int)(posDiu.X * _scale) + _virtualBounds.X;
        int py = (int)(posDiu.Y * _scale) + _virtualBounds.Y;

        var hit = _windows.FirstOrDefault(w => w.Bounds.Contains(px, py));
        if (hit == _hoverWindow) return;
        _hoverWindow = hit;

        if (hit == null)
        {
            SelRect.Visibility = SizeLabel.Visibility = TitleLabel.Visibility = Visibility.Collapsed;
            UpdateDim(Rect.Empty);
            return;
        }

        var r = PhysicalToLocal(hit.Bounds);
        ShowSelection(r);

        TitleLabel.Visibility = Visibility.Visible;
        TitleText.Text = hit.Title.Length > 60 ? hit.Title[..60] + "…" : hit.Title;
        TitleLabel.Margin = new Thickness(Math.Max(8, r.X), Math.Max(8, r.Y - 38), 0, 0);
    }

    // ── 공통 ──────────────────────────────────────────────────────────────
    /// <summary>물리 절대 좌표 → 오버레이 로컬 DIU.</summary>
    private Rect PhysicalToLocal(System.Drawing.Rectangle phys)
    {
        var r = new Rect(
            (phys.X - _virtualBounds.X) / _scale,
            (phys.Y - _virtualBounds.Y) / _scale,
            phys.Width / _scale,
            phys.Height / _scale);
        r.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
        return r;
    }

    /// <summary>물리 절대 좌표 사각형으로 확정한다.</summary>
    private void ConfirmPhysicalRect(System.Drawing.Rectangle phys)
    {
        if (_done) return;
        var b = System.Drawing.Rectangle.Intersect(phys, _virtualBounds);
        if (b.Width < 3 || b.Height < 3) return;
        _done = true;
        SelectedPixelRect = new Int32Rect(
            b.X - _virtualBounds.X, b.Y - _virtualBounds.Y, b.Width, b.Height);
        try { DialogResult = true; } catch (InvalidOperationException) { }
        Close();
    }

    private void ShowSelection(Rect r)
    {
        SelRect.Visibility = Visibility.Visible;
        SelRect.Margin = new Thickness(r.X, r.Y, 0, 0);
        SelRect.Width = r.Width;
        SelRect.Height = r.Height;
        UpdateDim(r);

        var px = ToPixelRect(r);
        SizeText.Text = $"{px.Width} × {px.Height}";
        SizeLabel.Visibility = Visibility.Visible;

        double lx = r.X;
        double ly = r.Y + r.Height + 8;
        if (ly + 30 > ActualHeight) ly = Math.Max(4, r.Y - 34);
        SizeLabel.Margin = new Thickness(lx, ly, 0, 0);
    }

    /// <summary>선택 영역만 밝게 보이도록 EvenOdd 구멍을 만든다.</summary>
    private void UpdateDim(Rect hole)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height)));
        if (hole.IsEmpty || hole.Width <= 0 || hole.Height <= 0)
        {
            DimPath.Data = full;
            return;
        }
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(full);
        geo.Children.Add(new RectangleGeometry(hole));
        DimPath.Data = geo;
    }

    /// <summary>DIU 선택 영역 → 정지 이미지 픽셀 좌표 (클램프 포함).</summary>
    private Int32Rect ToPixelRect(Rect r)
    {
        int x = (int)Math.Round(r.X * _scale);
        int y = (int)Math.Round(r.Y * _scale);
        int w = (int)Math.Round(r.Width * _scale);
        int h = (int)Math.Round(r.Height * _scale);

        x = Math.Clamp(x, 0, Math.Max(0, _frozen.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _frozen.PixelHeight - 1));
        w = Math.Clamp(w, 0, _frozen.PixelWidth - x);
        h = Math.Clamp(h, 0, _frozen.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }
}
