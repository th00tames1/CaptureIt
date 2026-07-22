using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CaptureIt.Services;

/// <summary>
/// 캡처 과정의 시각 피드백: 지연 카운트다운, 처리 중 표시, 캡처 플래시.
/// 모든 창은 캡처 화면에 찍히지 않도록 WDA_EXCLUDEFROMCAPTURE로 제외한다.
/// </summary>
public static class FeedbackService
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    // ── 물리 픽셀 배치 ─────────────────────────────────────────────────────
    // Per-Monitor V2에서 WPF의 Left/Top(DIU)은 '창이 지금 올라가 있는 모니터'의 배율로 해석된다.
    // 다른 모니터를 겨냥한 좌표를 넣으면 창이 엉뚱한 자리에 놓이거나 모니터 경계에 걸쳐 반씩 잘린다.
    // 그래서 RegionSelectWindow.FitToVirtualScreen()과 같이 HWND가 생긴 뒤 물리 픽셀로 직접 배치한다.
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out CaptureService.RECT rect);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int WM_DPICHANGED = 0x02E0;

    /// <summary>(작업 영역, 창 크기, 대상 모니터 배율) → 창 좌상단. 좌표·크기는 모두 물리 픽셀.</summary>
    public delegate System.Drawing.Point PlaceFunc(System.Drawing.Rectangle work, System.Drawing.Size size, double scale);

    /// <summary>배치에 필요한 상태. 대상 모니터는 창을 만들 때 한 번만 정한다.</summary>
    private sealed class Placement
    {
        public System.Drawing.Rectangle Work;
        public double Scale = 1.0;
        public bool Busy;                  // SetWindowPos가 유발한 메시지로 재진입하는 것을 막는다
    }

    /// <summary>토스트를 띄울 모니터. anchor(물리 px)가 한 모니터 안에 들어가면 그 모니터, 아니면 커서가 있는 모니터.</summary>
    private static System.Windows.Forms.Screen TargetScreen(System.Drawing.Rectangle? anchor)
    {
        if (anchor is { Width: > 0, Height: > 0 } a)
        {
            var s = System.Windows.Forms.Screen.FromRectangle(a);
            if (s.Bounds.Contains(a)) return s;   // 여러 모니터에 걸친 영역이면 커서 쪽으로 넘긴다
        }
        CaptureService.GetCursorPosition(out var p);
        return System.Windows.Forms.Screen.FromPoint(p);
    }

    /// <summary>대상 모니터의 배율. 창이 올라가 있는 모니터가 아니라 '겨냥한' 모니터 기준이어야 한다.</summary>
    private static double ScreenScale(System.Windows.Forms.Screen s)
    {
        var c = new POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
        var hmon = MonitorFromPoint(c, MONITOR_DEFAULTTONEAREST);
        if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
            return dx / 96.0;
        return 1.0;
    }

    /// <summary>
    /// 창을 한 모니터의 작업 영역 안에 물리 픽셀로 배치하고, 내용 크기나 배율이 바뀌면 다시 맞춘다.
    /// 마지막에 작업 영역으로 클램프하므로 모니터 경계에 걸치는 일이 구조적으로 불가능하다.
    /// </summary>
    public static void PlaceOnMonitor(Window win, System.Drawing.Rectangle? anchor, PlaceFunc place)
    {
        var screen = TargetScreen(anchor);
        var st = new Placement { Work = screen.WorkingArea, Scale = ScreenScale(screen) };

        win.SourceInitialized += (_, _) => Nudge(win, st, place);   // 화면에 보이기 전에 자리부터 잡는다
        win.Loaded += (_, _) => Nudge(win, st, place);
        win.SizeChanged += (_, _) => Nudge(win, st, place);         // 카운트다운 글자 폭 변화
        win.DpiChanged += (_, _) => Nudge(win, st, place);          // 배율 다른 모니터로 옮겨졌을 때
    }

    private static void Nudge(Window win, Placement st, PlaceFunc place)
    {
        Reposition(win, st, place);
        // 레이아웃·배율 변경이 HWND 크기에 반영된 뒤 한 번 더 (이미 제자리면 아무 일도 하지 않는다)
        win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                                   () => Reposition(win, st, place));
    }

    /// <summary>
    /// 항상 같은 작업 영역(st.Work) 안으로 클램프하므로, 첫 이동 뒤에는 창이 대상 모니터를 벗어나지 않는다.
    /// 따라서 배율 전환은 많아야 한 번 일어나고 위치는 수렴한다. Busy는 그 한 번의 전환 중 재진입만 막는다.
    /// </summary>
    private static void Reposition(Window win, Placement st, PlaceFunc place)
    {
        if (st.Busy) return;
        var hwnd = new WindowInteropHelper(win).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;   // 아직 없거나 이미 닫힌 창

        // 창 크기를 DIU로 계산하지 않고 HWND에서 물리 픽셀 그대로 읽는다 → 배율에 영향받지 않는다
        var size = new System.Drawing.Size(r.Right - r.Left, r.Bottom - r.Top);
        if (size.Width <= 0 || size.Height <= 0) return;

        var p = place(st.Work, size, st.Scale);
        // 어떤 경우에도 모니터 하나를 벗어나지 않게 (경계에 반씩 걸치는 문제의 최종 방어선)
        int x = Math.Max(st.Work.Left, Math.Min(p.X, st.Work.Right - size.Width));
        int y = Math.Max(st.Work.Top, Math.Min(p.Y, st.Work.Bottom - size.Height));
        if (x == r.Left && y == r.Top) return;   // 제자리면 그대로 (반복 이동 방지)

        st.Busy = true;
        try { SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }
        finally { st.Busy = false; }
    }

    // ── 토스트 ─────────────────────────────────────────────────────────────
    /// <summary>
    /// 화면 캡처에서 제외되는 topmost 무테 토스트 창을 만든다 (대상 모니터 상단 중앙).
    /// anchor는 토스트를 띄울 모니터를 정하는 물리 픽셀 영역(보통 캡처 대상). null이면 커서가 있는 모니터.
    /// 대상 영역을 가리게 되면 아래쪽으로 피한다 (카운트다운 동안 화면을 준비할 수 있게).
    /// </summary>
    public static (Window win, TextBlock text) CreateToast(string initialText,
                                                           System.Drawing.Rectangle? anchor = null)
    {
        var text = new TextBlock
        {
            Text = initialText,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Malgun Gothic")
        };
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 31, 41, 55)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 12, 22, 12),
                Child = text
            }
        };
        win.SourceInitialized += (_, _) =>
        {
            try { SetWindowDisplayAffinity(new WindowInteropHelper(win).Handle, WDA_EXCLUDEFROMCAPTURE); }
            catch { }
        };

        // 대상 모니터 작업 영역의 상단 중앙 (전부 물리 픽셀).
        // 카운트다운 글자 폭이 바뀌어도 SizeChanged로 다시 계산되어 계속 가운데를 유지한다.
        PlaceOnMonitor(win, anchor, (work, size, scale) =>
        {
            int margin = (int)Math.Round(48 * scale);
            int cx = work.Left + (work.Width - size.Width) / 2;
            var top = new System.Drawing.Point(cx, work.Top + margin);
            if (anchor is not { Width: > 0, Height: > 0 } a) return top;

            if (!new System.Drawing.Rectangle(top.X, top.Y, size.Width, size.Height).IntersectsWith(a))
                return top;
            var bottom = new System.Drawing.Point(cx, work.Bottom - size.Height - margin);
            if (!new System.Drawing.Rectangle(bottom.X, bottom.Y, size.Width, size.Height).IntersectsWith(a))
                return bottom;
            return top;   // 대상이 모니터 전체면 피할 곳이 없다 (토스트는 캡처에서 제외되고, 촬영 전에 닫힌다)
        });
        return (win, text);
    }

    /// <summary>지연 캡처 카운트다운을 표시하며 기다린다. Esc로 취소하면 false.
    /// anchor에 캡처 대상 영역(물리 px)을 주면 그 모니터에 표시한다.
    /// abort가 true를 돌려주면(예: 비상 탈출로 캡처가 강제 취소되면) 즉시 멈추고 false.</summary>
    public static async Task<bool> CountdownAsync(int seconds, System.Drawing.Rectangle? anchor = null,
                                                  Func<bool>? abort = null)
    {
        if (seconds <= 0) return true;
        var (toast, text) = CreateToast(Loc.F("Feedback.Countdown", seconds), anchor);
        toast.Show();
        try
        {
            for (int remain = seconds; remain > 0; remain--)
            {
                text.Text = Loc.F("Feedback.Countdown", remain);
                for (int t = 0; t < 1000; t += 50)
                {
                    await Task.Delay(50);
                    if (CaptureService.IsEscapePressed() || abort?.Invoke() == true) return false;
                }
            }
            return true;
        }
        finally { toast.Close(); }
    }

    /// <summary>
    /// 오래 걸릴 수 있는 작업 동안 "처리 중…" 토스트를 보여준다.
    /// 250ms 안에 끝나면 아예 표시하지 않아 빠른 캡처에는 방해가 없다.
    /// (촬영이 끝난 뒤에 뜨므로 커서가 있는 모니터에 표시한다)
    /// </summary>
    public static async Task<T> WithBusyToastAsync<T>(Task<T> work)
    {
        var delay = Task.Delay(250);
        var completed = await Task.WhenAny(work, delay);
        if (completed == work) return await work;

        var (toast, _) = CreateToast(Loc.Get("Feedback.Processing"));
        toast.Show();
        try { return await work; }
        finally { toast.Close(); }
    }

    /// <summary>캡처 순간의 짧은 화면 플래시 (전체 캡처처럼 오버레이가 없는 모드용).</summary>
    public static void Flash()
    {
        // SystemParameters.VirtualScreen*는 '시스템 DPI' 기준 DIU라, 창이 놓인 모니터 배율과 다르면
        // 가상 화면을 정확히 덮지 못한다. 아래 SetWindowPos가 물리 픽셀로 정확히 덮고,
        // 여기 값들은 그것이 실패했을 때의 최소 보장(fallback)으로 남겨 둔다.
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.White,
            Opacity = 0,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
            catch { }

            // 배율이 다른 모니터에 걸치면 WPF가 '제안 rect'로 창을 줄여 덮임이 깨진다 → WM_DPICHANGED 흡수
            if (HwndSource.FromHwnd(hwnd) is { } hs) hs.AddHook(KeepCovering);
            SetWindowPos(hwnd, IntPtr.Zero, v.X, v.Y, v.Width, v.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        };
        win.Show();
        var anim = new DoubleAnimation(0.35, 0, TimeSpan.FromMilliseconds(220));
        anim.Completed += (_, _) => win.Close();
        win.BeginAnimation(UIElement.OpacityProperty, anim);

        IntPtr KeepCovering(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DPICHANGED)
            {
                SetWindowPos(hwnd, IntPtr.Zero, v.X, v.Y, v.Width, v.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
