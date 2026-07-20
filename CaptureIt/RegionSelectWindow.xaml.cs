using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaptureIt.Services;

namespace CaptureIt;

/// <summary>
/// 정지된 화면 위에서 캡처 대상을 고르는 전체 화면 오버레이.
/// Region/ScrollRegion: 드래그로 영역 선택 · Window: 창 클릭 ·
/// Element: UI 요소 하이라이트 후 클릭 (클릭 통과 + 마우스 훅) · Fixed: 고정 크기 틀 클릭.
/// 반환 좌표는 정지 이미지(가상 화면)의 픽셀 좌표.
/// </summary>
public partial class RegionSelectWindow : Window
{
    public enum SelectMode { Region, Window, Element, Fixed, ScrollRegion }

    public Int32Rect? SelectedPixelRect { get; private set; }

    private readonly BitmapSource _frozen;
    private readonly SelectMode _mode;
    private readonly List<CaptureService.WindowInfo> _windows;
    private readonly System.Drawing.Rectangle _virtualBounds;   // 물리 픽셀

    // Fixed(고정 크기) 모드 — "자르기 사각형" 모델: 항상 화면에 놓인 채 드래그로 이동/크기 조절.
    private int _fw, _fh;                                        // 현재 프레임 크기 (물리 픽셀)
    private System.Drawing.Rectangle _fixedFrame;                // 현재 프레임 (물리 절대 좌표)
    private enum FixedDrag { None, Move, N, S, E, W, NE, NW, SE, SW }
    private FixedDrag _fixedDrag = FixedDrag.None;
    private System.Drawing.Point _fixedDragStart;
    private System.Drawing.Rectangle _fixedDragFrame;
    private readonly System.Windows.Shapes.Rectangle[] _handles = new System.Windows.Shapes.Rectangle[8];
    private bool _syncingSizeBoxes;

    private double _scale = 1.0;        // DIU → 물리 픽셀 배율
    private bool _dragging;
    private Point _dragStart;           // DIU
    private Rect _selection;            // DIU
    private CaptureService.WindowInfo? _hoverWindow;
    private System.Drawing.Rectangle? _hoverElementRect;        // 물리 픽셀 (Element 모드)

    // Element 모드: 클릭 통과 + 저수준 마우스 훅
    private IntPtr _mouseHook = IntPtr.Zero;
    private HookProc? _hookProc;        // GC 방지용 참조 유지
    private volatile bool _uiaLoopRunning;
    private bool _leftPending, _rightPending;   // DOWN을 삼킨 버튼의 UP 대기
    private bool _done;                          // 중복 확정/취소 방지

    public RegionSelectWindow(BitmapSource frozen, SelectMode mode,
                              List<CaptureService.WindowInfo>? windows = null,
                              int fixedW = 0, int fixedH = 0)
    {
        InitializeComponent();
        _frozen = frozen;
        _mode = mode;
        _windows = windows ?? new();
        _fw = Math.Max(10, fixedW);
        _fh = Math.Max(10, fixedH);
        _virtualBounds = System.Windows.Forms.SystemInformation.VirtualScreen;

        FrozenImage.Source = _frozen;
        HintText.Text = _mode switch
        {
            SelectMode.Window => Loc.Get("Overlay.WindowHint"),
            SelectMode.Element => Loc.Get("Overlay.ElementHint"),
            SelectMode.Fixed => Loc.Get("Overlay.FixedHint"),
            SelectMode.ScrollRegion => Loc.Get("Overlay.ScrollHint"),
            _ => Loc.Get("Overlay.RegionHint"),
        };

        if (_mode == SelectMode.Fixed) InitFixedMode();

        SourceInitialized += (_, _) =>
        {
            FitToVirtualScreen();
            if (_mode == SelectMode.Element) EnterElementMode();
        };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();

            // 안내/패널을 커서가 있는 모니터의 가로 중앙에 배치 (듀얼 모니터 경계에 걸치지 않게)
            var cursor = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursor).Bounds;
            Dispatcher.BeginInvoke(() =>
            {
                double centerX = (screen.X + screen.Width / 2.0 - _virtualBounds.X) / _scale;
                foreach (var el in new FrameworkElement[] { HintBar, FixedPanel })
                {
                    if (el.Visibility != Visibility.Visible) continue;
                    el.HorizontalAlignment = HorizontalAlignment.Left;
                    el.Margin = new Thickness(Math.Max(8, centerX - el.ActualWidth / 2), el.Margin.Top, 0, 0);
                }
            }, DispatcherPriority.Loaded);

            if (_mode == SelectMode.Fixed)
            {
                // 프레임을 커서가 있는 모니터의 중앙에 놓는다 (커서를 따라다니지 않음)
                _fixedFrame = ClampFrame(new System.Drawing.Rectangle(
                    screen.X + (screen.Width - _fw) / 2,
                    screen.Y + (screen.Height - _fh) / 2, _fw, _fh));
                UpdateFixedVisuals();
            }
        };
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
        if (e.Key == Key.Escape) { Cancel(); return; }

        if (_mode != SelectMode.Fixed) return;
        if (FixedWBox.IsKeyboardFocused || FixedHBox.IsKeyboardFocused) return;   // 숫자 입력 중

        if (e.Key == Key.Enter)
        {
            ConfirmFixed();
            e.Handled = true;
        }
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        try { DialogResult = false; } catch (InvalidOperationException) { }
        Close();
    }

    // ── 마우스 처리 (Region/Window/Fixed/ScrollRegion) ─────────────────────
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (_mode)
        {
            case SelectMode.Window:
                if (_hoverWindow != null) ConfirmPhysicalRect(_hoverWindow.Bounds);
                return;

            case SelectMode.Fixed:
            {
                var phys = ToPhys(e.GetPosition(this));
                if (e.ClickCount == 2 && _fixedFrame.Contains(phys)) { ConfirmFixed(); return; }

                var hit = HitTestFixed(phys);
                if (hit == FixedDrag.None)
                {
                    // 프레임 밖 클릭: 프레임을 클릭 지점 중심으로 옮긴다
                    _fixedFrame = ClampFrame(new System.Drawing.Rectangle(
                        phys.X - _fw / 2, phys.Y - _fh / 2, _fw, _fh));
                    UpdateFixedVisuals();
                    hit = FixedDrag.Move;   // 그대로 끌면 이동으로 이어진다
                }
                _fixedDrag = hit;
                _fixedDragStart = phys;
                _fixedDragFrame = _fixedFrame;
                CaptureMouse();
                return;
            }

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
                UpdateHoverWindow(pos);
                return;

            case SelectMode.Fixed:
                if (_fixedDrag != FixedDrag.None) { ApplyFixedDrag(ToPhys(pos)); return; }
                Cursor = CursorFor(HitTestFixed(ToPhys(pos)));
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
        if (_mode == SelectMode.Fixed && _fixedDrag != FixedDrag.None)
        {
            _fixedDrag = FixedDrag.None;
            ReleaseMouseCapture();
            return;
        }
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

    // ── 고정 크기 모드 ─────────────────────────────────────────────────────
    private void InitFixedMode()
    {
        FixedPanel.Visibility = Visibility.Visible;
        FixedCaptureText.Text = Loc.Get("Overlay.CaptureBtn");
        Cursor = Cursors.Arrow;   // 프레임을 다루는 모드 — 십자선이 아니라 화살표
        SyncSizeBoxes();

        // 크기 조절 핸들 8개 (표시 전용 — 히트 테스트는 좌표 계산으로)
        for (int i = 0; i < 8; i++)
        {
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = System.Windows.Media.Brushes.White,
                Stroke = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D7DF6")),
                StrokeThickness = 1.6,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _handles[i] = handle;
            Root.Children.Add(handle);
        }
    }

    private System.Drawing.Point ToPhys(Point posDiu) => new(
        (int)(posDiu.X * _scale) + _virtualBounds.X,
        (int)(posDiu.Y * _scale) + _virtualBounds.Y);

    /// <summary>프레임·핸들·크기 라벨·입력 상자를 현재 상태로 갱신한다.</summary>
    private void UpdateFixedVisuals()
    {
        var r = PhysicalToLocal(_fixedFrame);
        ShowSelection(r);
        SyncSizeBoxes();

        // 핸들 위치: 모서리 4 + 변 중앙 4
        var pts = new (double x, double y)[]
        {
            (r.X, r.Y), (r.X + r.Width / 2, r.Y), (r.Right, r.Y),
            (r.X, r.Y + r.Height / 2), (r.Right, r.Y + r.Height / 2),
            (r.X, r.Bottom), (r.X + r.Width / 2, r.Bottom), (r.Right, r.Bottom),
        };
        for (int i = 0; i < 8; i++)
        {
            _handles[i].Visibility = Visibility.Visible;
            _handles[i].Margin = new Thickness(pts[i].x - 4.5, pts[i].y - 4.5, 0, 0);
        }
    }

    private void SyncSizeBoxes()
    {
        _syncingSizeBoxes = true;
        if (!FixedWBox.IsKeyboardFocused) FixedWBox.Text = _fw.ToString();
        if (!FixedHBox.IsKeyboardFocused) FixedHBox.Text = _fh.ToString();
        _syncingSizeBoxes = false;
    }

    /// <summary>입력 상자의 숫자를 프레임 크기에 적용한다.</summary>
    private void ApplySizeInput()
    {
        if (_syncingSizeBoxes) return;
        if (int.TryParse(FixedWBox.Text.Trim(), out int w))
            _fw = Math.Clamp(w, 10, _virtualBounds.Width);
        if (int.TryParse(FixedHBox.Text.Trim(), out int h))
            _fh = Math.Clamp(h, 10, _virtualBounds.Height);

        // 프레임 중심을 유지한 채 크기만 바꾼다
        var c = new System.Drawing.Point(_fixedFrame.X + _fixedFrame.Width / 2,
                                         _fixedFrame.Y + _fixedFrame.Height / 2);
        _fixedFrame = ClampFrame(new System.Drawing.Rectangle(c.X - _fw / 2, c.Y - _fh / 2, _fw, _fh));
        UpdateFixedVisuals();
    }

    private System.Drawing.Rectangle ClampFrame(System.Drawing.Rectangle f)
    {
        int w = Math.Min(f.Width, _virtualBounds.Width);
        int h = Math.Min(f.Height, _virtualBounds.Height);
        int x = Math.Clamp(f.X, _virtualBounds.X, _virtualBounds.Right - w);
        int y = Math.Clamp(f.Y, _virtualBounds.Y, _virtualBounds.Bottom - h);
        return new System.Drawing.Rectangle(x, y, w, h);
    }

    private FixedDrag HitTestFixed(System.Drawing.Point p)
    {
        int grip = (int)(10 * _scale);
        var f = _fixedFrame;
        bool nearL = Math.Abs(p.X - f.Left) <= grip, nearR = Math.Abs(p.X - f.Right) <= grip;
        bool nearT = Math.Abs(p.Y - f.Top) <= grip, nearB = Math.Abs(p.Y - f.Bottom) <= grip;
        bool inX = p.X >= f.Left - grip && p.X <= f.Right + grip;
        bool inY = p.Y >= f.Top - grip && p.Y <= f.Bottom + grip;

        if (nearT && nearL) return FixedDrag.NW;
        if (nearT && nearR) return FixedDrag.NE;
        if (nearB && nearL) return FixedDrag.SW;
        if (nearB && nearR) return FixedDrag.SE;
        if (nearT && inX) return FixedDrag.N;
        if (nearB && inX) return FixedDrag.S;
        if (nearL && inY) return FixedDrag.W;
        if (nearR && inY) return FixedDrag.E;
        if (f.Contains(p)) return FixedDrag.Move;
        return FixedDrag.None;
    }

    private static Cursor CursorFor(FixedDrag d) => d switch
    {
        FixedDrag.NW or FixedDrag.SE => Cursors.SizeNWSE,
        FixedDrag.NE or FixedDrag.SW => Cursors.SizeNESW,
        FixedDrag.N or FixedDrag.S => Cursors.SizeNS,
        FixedDrag.E or FixedDrag.W => Cursors.SizeWE,
        FixedDrag.Move => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    private void ApplyFixedDrag(System.Drawing.Point p)
    {
        int dx = p.X - _fixedDragStart.X, dy = p.Y - _fixedDragStart.Y;
        var s = _fixedDragFrame;
        int left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom;

        switch (_fixedDrag)
        {
            case FixedDrag.Move:
                _fixedFrame = ClampFrame(new System.Drawing.Rectangle(s.X + dx, s.Y + dy, s.Width, s.Height));
                _fw = _fixedFrame.Width; _fh = _fixedFrame.Height;
                UpdateFixedVisuals();
                return;
            case FixedDrag.N: top += dy; break;
            case FixedDrag.S: bottom += dy; break;
            case FixedDrag.W: left += dx; break;
            case FixedDrag.E: right += dx; break;
            case FixedDrag.NW: top += dy; left += dx; break;
            case FixedDrag.NE: top += dy; right += dx; break;
            case FixedDrag.SW: bottom += dy; left += dx; break;
            case FixedDrag.SE: bottom += dy; right += dx; break;
        }

        // 최소 크기 유지 + 화면 클램프
        if (right - left < 10) { if (_fixedDrag is FixedDrag.W or FixedDrag.NW or FixedDrag.SW) left = right - 10; else right = left + 10; }
        if (bottom - top < 10) { if (_fixedDrag is FixedDrag.N or FixedDrag.NW or FixedDrag.NE) top = bottom - 10; else bottom = top + 10; }
        left = Math.Max(left, _virtualBounds.X); top = Math.Max(top, _virtualBounds.Y);
        right = Math.Min(right, _virtualBounds.Right); bottom = Math.Min(bottom, _virtualBounds.Bottom);

        _fixedFrame = System.Drawing.Rectangle.FromLTRB(left, top, right, bottom);
        _fw = _fixedFrame.Width; _fh = _fixedFrame.Height;
        UpdateFixedVisuals();
    }

    private void ConfirmFixed()
    {
        if (_fixedFrame.Width >= 10) ConfirmPhysicalRect(_fixedFrame);
    }

    // ── 고정 크기 패널 이벤트 ──────────────────────────────────────────────
    private void FixedPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void FixedSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySizeInput();
            Focus();   // 창으로 포커스를 되돌려야 Enter 캡처가 다시 동작한다
            e.Handled = true;
        }
    }

    private void FixedSize_LostFocus(object sender, RoutedEventArgs e) => ApplySizeInput();

    private void FixedCaptureBtn_Click(object sender, RoutedEventArgs e) => ConfirmFixed();

    // ── 단위 영역(Element) 모드 ────────────────────────────────────────────
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point p);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    /// <summary>클릭 통과 창으로 전환하고 저수준 마우스 훅으로 이동/클릭을 받는다.
    /// (UIA가 오버레이 자신 대신 아래의 실제 UI 요소를 보게 하기 위함)</summary>
    private void EnterElementMode()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        // WS_EX_LAYERED를 나중에 붙인 창은 알파를 지정해야 다시 그려진다 (안 하면 투명 유령 창이 됨)
        SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);

        _hookProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
        {
            // 훅 없이 클릭 통과 창을 띄우면 사용자가 조작 불능이 된다 — 즉시 중단
            Dispatcher.BeginInvoke(Cancel);
            return;
        }

        StartUiaLoop();
    }

    /// <summary>
    /// UIA 조회는 느릴 수 있어(수백 ms) 반드시 백그라운드 MTA 스레드에서 돌린다.
    /// UI 스레드가 막히면 Windows가 저수준 훅을 조용히 제거해 버리기 때문.
    /// </summary>
    private void StartUiaLoop()
    {
        _uiaLoopRunning = true;
        var thread = new Thread(() =>
        {
            System.Drawing.Point lastQueried = new(-9999, -9999);
            while (_uiaLoopRunning)
            {
                try
                {
                    if (!GetCursorPos(out var pos)) { Thread.Sleep(60); continue; }
                    if (pos == lastQueried) { Thread.Sleep(40); continue; }
                    lastQueried = pos;

                    var el = System.Windows.Automation.AutomationElement.FromPoint(
                        new Point(pos.X, pos.Y));
                    var b = el.Current.BoundingRectangle;
                    if (!b.IsEmpty && !double.IsInfinity(b.Width) && b.Width >= 4 && b.Height >= 4)
                    {
                        var phys = System.Drawing.Rectangle.Intersect(
                            new System.Drawing.Rectangle((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height),
                            _virtualBounds);
                        // 데스크톱 루트(가상 화면 전체)는 의미 없는 히트 — 이전 선택 유지
                        bool desktopRoot = phys.Width >= _virtualBounds.Width * 98 / 100 &&
                                           phys.Height >= _virtualBounds.Height * 98 / 100;
                        if (phys.Width >= 4 && phys.Height >= 4 && !desktopRoot)
                        {
                            Dispatcher.BeginInvoke(() =>
                            {
                                if (_done) return;
                                if (_hoverElementRect is { } prev && prev == phys) return;
                                _hoverElementRect = phys;
                                ShowSelection(PhysicalToLocal(phys));
                            });
                        }
                    }
                }
                catch { /* UIA 일시 실패 — 다음 반복에서 재시도 */ }
                Thread.Sleep(40);
            }
        })
        { IsBackground = true, Name = "CaptureIt.UIA" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void LeaveElementMode()
    {
        _uiaLoopRunning = false;
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
            // DOWN에서 예약하고 짝이 되는 UP까지 삼킨 뒤 실행 —
            // 고아 UP이 아래 앱에 전달되어 컨텍스트 메뉴 등이 뜨는 것을 막는다.
            switch (wParam.ToInt32())
            {
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
