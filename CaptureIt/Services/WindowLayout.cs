using System.Windows;

namespace CaptureIt.Services;

/// <summary>
/// CaptureIt 창들이 서로 가리지 않도록 배치를 계산한다.
/// 규칙: 움직일 창을 고정 창의 아래 → 위 → 오른쪽 → 왼쪽 순으로 붙여 보고,
/// 모두 안 되면 작업 영역 모서리 중 겹치지 않는 곳에 둔다.
/// </summary>
public static class WindowLayout
{
    private const double Gap = 12;

    public static void PlaceAvoiding(Window moving, Window? fixedWin)
    {
        if (fixedWin == null || !fixedWin.IsVisible) return;

        var work = SystemParameters.WorkArea;
        var m = new Rect(moving.Left, moving.Top, moving.ActualWidth > 0 ? moving.ActualWidth : moving.Width,
                                                  moving.ActualHeight > 0 ? moving.ActualHeight : moving.Height);
        var f = new Rect(fixedWin.Left, fixedWin.Top, fixedWin.ActualWidth, fixedWin.ActualHeight);

        if (!m.IntersectsWith(f)) return;   // 이미 안 겹침

        // 후보 위치들 (고정 창 기준 아래/위/오른쪽/왼쪽)
        var candidates = new[]
        {
            new Point(m.X, f.Bottom + Gap),
            new Point(m.X, f.Top - m.Height - Gap),
            new Point(f.Right + Gap, m.Y),
            new Point(f.Left - m.Width - Gap, m.Y),
        };

        foreach (var c in candidates)
        {
            var r = new Rect(c.X, c.Y, m.Width, m.Height);
            if (work.Contains(r) && !r.IntersectsWith(f))
            {
                moving.Left = c.X;
                moving.Top = c.Y;
                return;
            }
        }

        // 모서리 폴백: 겹치지 않는 첫 모서리
        var corners = new[]
        {
            new Point(work.Right - m.Width - Gap, work.Top + Gap),
            new Point(work.Left + Gap, work.Top + Gap),
            new Point(work.Right - m.Width - Gap, work.Bottom - m.Height - Gap),
            new Point(work.Left + Gap, work.Bottom - m.Height - Gap),
        };
        foreach (var c in corners)
        {
            var r = new Rect(c.X, c.Y, m.Width, m.Height);
            if (!r.IntersectsWith(f))
            {
                moving.Left = c.X;
                moving.Top = c.Y;
                return;
            }
        }
    }

    /// <summary>
    /// 메인 툴바(작음)와 편집기(큼)가 겹치면 세로 밴드로 분리한다.
    /// 편집기가 어느 가로 위치에 있어도 겹치지 않도록 세로 공간을 나누는 방식.
    /// </summary>
    public static void StackMainAndEditor(Window main, Window editor)
    {
        if (!main.IsVisible || !editor.IsVisible) return;
        // 최대화된 창은 재배치하지 않는다: ActualWidth/Height(최대화 크기)와 Left/Top(복원 위치)을
        // 섞어 실제로 존재하지 않는 사각형을 만들고, editor.Top/Height를 써서 복원 크기를 망가뜨린다.
        if (main.WindowState != WindowState.Normal || editor.WindowState != WindowState.Normal) return;

        var work = SystemParameters.WorkArea;
        var m = new Rect(main.Left, main.Top, main.ActualWidth, main.ActualHeight);
        var ed = new Rect(editor.Left, editor.Top, editor.ActualWidth, editor.ActualHeight);
        if (m.Width <= 0 || ed.Width <= 0 || !m.IntersectsWith(ed)) return;

        const double gap = 12, minEditorH = 420;

        // 1) 메인 아래 공간이 충분하면: 편집기를 메인 아래 밴드로
        if (work.Bottom - m.Bottom - gap * 2 >= minEditorH)
        {
            editor.Top = m.Bottom + gap;
            if (editor.Top + ed.Height > work.Bottom - gap)
                editor.Height = Math.Max(minEditorH, work.Bottom - gap - editor.Top);
            ClampToWorkArea(editor);
            return;
        }

        // 2) 메인 위 공간이 충분하면: 편집기를 위 밴드로
        if (m.Top - work.Top - gap * 2 >= minEditorH)
        {
            editor.Height = Math.Min(ed.Height, m.Top - work.Top - gap * 2);
            editor.Top = m.Top - gap - editor.Height;
            ClampToWorkArea(editor);
            return;
        }

        // 3) 메인을 화면 상단 중앙으로 올리고 편집기를 그 아래로
        main.Top = work.Top + 8;
        main.Left = work.Left + (work.Width - m.Width) / 2;
        editor.Top = main.Top + m.Height + gap;
        editor.Height = Math.Max(minEditorH, work.Bottom - gap - editor.Top);
        ClampToWorkArea(editor);
    }

    /// <summary>가상 화면(모든 모니터)을 벗어난 창을 안으로 끌어들인다 (모니터 구성 변경 대비).</summary>
    public static void ClampToWorkArea(Window w)
    {
        if (double.IsNaN(w.Left) || double.IsNaN(w.Top)) return;   // 아직 배치되지 않은 창

        var work = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        double width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        double height = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        if (w.Left + width < work.Left + 40) w.Left = work.Left + 20;
        if (w.Top + height < work.Top + 40) w.Top = work.Top + 20;
        if (w.Left > work.Right - 40) w.Left = work.Right - width - 20;
        if (w.Top > work.Bottom - 40) w.Top = work.Bottom - height - 20;
    }
}
