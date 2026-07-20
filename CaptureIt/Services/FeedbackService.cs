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

    /// <summary>화면 캡처에서 제외되는 topmost 무테 토스트 창을 만든다 (주 모니터 상단 중앙).</summary>
    public static (Window win, TextBlock text) CreateToast(string initialText)
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
        win.Loaded += (_, _) =>
        {
            // 주 모니터 상단 중앙 (물리 px → DIU)
            var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            double scale = VisualTreeHelper.GetDpi(win).DpiScaleX;
            win.Left = (wa.Left + (wa.Width - win.ActualWidth * scale) / 2) / scale;
            win.Top = (wa.Top + 48) / scale;
        };
        return (win, text);
    }

    /// <summary>지연 캡처 카운트다운을 표시하며 기다린다. Esc로 취소하면 false.</summary>
    public static async Task<bool> CountdownAsync(int seconds)
    {
        if (seconds <= 0) return true;
        var (toast, text) = CreateToast(Loc.F("Feedback.Countdown", seconds));
        toast.Show();
        try
        {
            for (int remain = seconds; remain > 0; remain--)
            {
                text.Text = Loc.F("Feedback.Countdown", remain);
                for (int t = 0; t < 1000; t += 50)
                {
                    await Task.Delay(50);
                    if (CaptureService.IsEscapePressed()) return false;
                }
            }
            return true;
        }
        finally { toast.Close(); }
    }

    /// <summary>
    /// 오래 걸릴 수 있는 작업 동안 "처리 중…" 토스트를 보여준다.
    /// 250ms 안에 끝나면 아예 표시하지 않아 빠른 캡처에는 방해가 없다.
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
            try { SetWindowDisplayAffinity(new WindowInteropHelper(win).Handle, WDA_EXCLUDEFROMCAPTURE); }
            catch { }
        };
        win.Show();
        var anim = new DoubleAnimation(0.35, 0, TimeSpan.FromMilliseconds(220));
        anim.Completed += (_, _) => win.Close();
        win.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}
