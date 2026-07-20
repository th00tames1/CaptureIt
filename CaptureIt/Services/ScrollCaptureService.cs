using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace CaptureIt.Services;

/// <summary>
/// 스크롤 캡처: 선택 영역을 반복 캡처하면서 휠을 굴리고,
/// 행 해시 매칭(고유 행 앵커 기반)으로 겹치는 부분을 찾아 하나의 긴 이미지로 이어붙인다.
/// </summary>
public static class ScrollCaptureService
{
    private const int MaxFrames = 80;
    private const int MaxTotalHeight = 30000;
    private const int MinScrollPx = 8;        // 이보다 작은 이동은 무시
    private const int SettleDelayMs = 400;    // 스크롤 애니메이션 대기
    private const int MinAnchors = 5;         // 겹침 검증에 필요한 최소 고유 행 수

    /// <summary>
    /// region: 물리 픽셀 절대 좌표의 캡처 영역 (보통 창의 클라이언트 영역).
    /// progress: 프레임 수 보고 콜백. 반환: 합성된 이미지 (스크롤 감지 실패 시 첫 프레임만).
    /// 브라우저 툴바 같은 고정 상단/하단 밴드를 자동 감지해 본문만 이어붙인다.
    /// </summary>
    public static async Task<BitmapSource?> RunAsync(Rectangle region, Action<int> progress)
    {
        int w = region.Width, h = region.Height;
        if (w < 50 || h < 80) return null;

        // 영역 높이에 맞춘 휠 세기: 한 번에 화면의 절반 이하만 스크롤되게 (겹침 확보)
        int notches = Math.Clamp((h - 60) / 240, 1, 3);

        var contentRows = new List<byte[]>();   // 고정 밴드를 뺀 본문 행 누적 (BGRA, w*4)
        byte[][]? firstFrame = null, lastFrame = null;
        ulong[]? prevHashes = null;
        int fixedTop = -1, fixedBottom = 0;     // 첫 스크롤 쌍에서 감지 (-1 = 미정)
        int stagnant = 0;
        bool scrolledAtLeastOnce = false;

        CaptureService.GetCursorPosition(out var savedCursor);
        try
        {
            // 커서 위치: 해시 밴드(중앙 60%) 밖이면서 실제로 스크롤되는 본문 위여야 한다.
            // 구석은 상태바·스크롤바라 휠이 안 먹는 경우가 있어, 우측 20% 지대의 세로 중앙에 둔다.
            int cursorX = region.X + Math.Min(w - w / 5 + Math.Min(24, w / 12), w - 4);
            int cursorY = region.Y + h / 2;

            for (int frame = 0; frame < MaxFrames; frame++)
            {
                if (await EscapePressedDuringDelay(0)) break;

                var (curFrame, curHashes) = await Task.Run(() =>
                {
                    using var bmp = CaptureService.CaptureRect(region);
                    var f = ExtractRows(bmp);
                    return (f, HashRows(f, w));
                });

                if (prevHashes == null)
                {
                    firstFrame = curFrame;
                }
                else
                {
                    var prev = prevHashes;
                    var (offset, ft, fb) = await Task.Run(() =>
                    {
                        // 고정 밴드는 첫 스크롤 쌍에서 한 번만 감지해 계속 쓴다 (페이지 구조 불변 가정)
                        int top = fixedTop, bottom = fixedBottom;
                        if (top < 0) (top, bottom) = DetectFixedBands(prev, curHashes, h);
                        int d = FindScrollOffset(prev, curHashes, top, h - bottom);
                        return (d, top, bottom);
                    });

                    if (offset <= 0)
                    {
                        // 스크롤이 안 됐거나 감지 실패 — 휠을 다시 굴려 한 번 더 시도 후 종료
                        stagnant++;
                        if (stagnant >= 2) break;
                        CaptureService.WheelDownAt(cursorX, cursorY, notches);
                        if (await EscapePressedDuringDelay(SettleDelayMs)) break;
                        continue;
                    }

                    if (!scrolledAtLeastOnce)
                    {
                        // 첫 스크롤 확정: 고정 밴드를 채택하고 첫 프레임의 본문을 넣는다
                        fixedTop = ft;
                        fixedBottom = fb;
                        scrolledAtLeastOnce = true;
                        for (int i = fixedTop; i < h - fixedBottom; i++) contentRows.Add(firstFrame![i]);
                    }

                    stagnant = 0;
                    for (int i = h - fixedBottom - offset; i < h - fixedBottom; i++) contentRows.Add(curFrame[i]);
                    if (contentRows.Count >= MaxTotalHeight) break;
                }

                lastFrame = curFrame;
                prevHashes = curHashes;
                progress(frame + 1);

                CaptureService.WheelDownAt(cursorX, cursorY, notches);
                if (await EscapePressedDuringDelay(SettleDelayMs)) break;
            }
        }
        finally
        {
            CaptureService.SetCursorPosition(savedCursor.X, savedCursor.Y);
        }

        if (firstFrame == null) return null;

        // 최종 합성: 상단 고정 밴드(첫 프레임) + 본문 + 하단 고정 밴드(마지막 프레임)
        return await Task.Run(() =>
        {
            var rows = new List<byte[]>(contentRows.Count + h);
            if (scrolledAtLeastOnce)
            {
                for (int i = 0; i < fixedTop; i++) rows.Add(firstFrame[i]);
                rows.AddRange(contentRows);
                var tail = lastFrame ?? firstFrame;
                for (int i = h - fixedBottom; i < h; i++) rows.Add(tail[i]);
            }
            else
            {
                rows.AddRange(firstFrame);   // 스크롤이 전혀 안 됨 — 한 화면만 반환
            }
            return ComposeImage(rows, w);
        });
    }

    /// <summary>
    /// 두 프레임에서 위·아래 고정 밴드(브라우저 툴바, 고정 푸터 등)의 높이를 감지한다.
    /// 스크롤 후에도 같은 자리에 같은 내용이 있는 행 = 고정 밴드.
    /// </summary>
    private static (int top, int bottom) DetectFixedBands(ulong[] prev, ulong[] cur, int h)
    {
        int top = 0;
        while (top < h * 2 / 5 && prev[top] == cur[top]) top++;

        int bottom = 0;
        while (bottom < h * 3 / 10 && prev[h - 1 - bottom] == cur[h - 1 - bottom]) bottom++;

        // 본문 창이 너무 작아지면 밴드 감지를 포기한다 (오탐 가능성)
        if (h - top - bottom < 80) return (0, 0);
        return (top, bottom);
    }

    /// <summary>지연 중에도 Esc 탭(짧은 입력)을 놓치지 않도록 25ms 간격으로 폴링한다.</summary>
    private static async Task<bool> EscapePressedDuringDelay(int delayMs)
    {
        if (CaptureService.IsEscapePressed()) return true;
        for (int waited = 0; waited < delayMs; waited += 25)
        {
            await Task.Delay(25);
            if (CaptureService.IsEscapePressed()) return true;
        }
        return false;
    }

    // ── 프레임 → 행 배열 ──────────────────────────────────────────────────
    private static byte[][] ExtractRows(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var result = new byte[h][];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                var row = new byte[w * 4];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w * 4);
                result[y] = row;
            }
        }
        finally { bmp.UnlockBits(data); }
        return result;
    }

    /// <summary>행 지문: 중앙 60% 폭에서 8픽셀 간격 샘플링한 FNV-1a 해시.</summary>
    private static ulong[] HashRows(byte[][] frame, int w)
    {
        int x0 = w / 5, x1 = w - w / 5;
        var hashes = new ulong[frame.Length];
        for (int y = 0; y < frame.Length; y++)
        {
            ulong hash = 14695981039346656037UL;
            var row = frame[y];
            for (int x = x0; x < x1; x += 8)
            {
                int i = x * 4;
                hash = (hash ^ row[i]) * 1099511628211UL;
                hash = (hash ^ row[i + 1]) * 1099511628211UL;
                hash = (hash ^ row[i + 2]) * 1099511628211UL;
            }
            hashes[y] = hash;
        }
        return hashes;
    }

    /// <summary>
    /// [from, to) 구간(고정 밴드를 뺀 본문)에서 이전 프레임 대비 아래로 스크롤된
    /// 픽셀 수를 찾는다. 빈 배경/반복 무늬의 오탐을 막기 위해, 구간 안에서 해시가
    /// 고유한 행(앵커)이 겹침 구간에서 일정 수 이상 정확히 맞아야만 인정한다.
    /// </summary>
    private static int FindScrollOffset(ulong[] prev, ulong[] cur, int from, int to)
    {
        int win = to - from;
        if (win < 60) return 0;

        // 완전히 동일하면 스크롤 안 된 것
        bool identical = true;
        for (int i = from; i < to; i++)
            if (prev[i] != cur[i]) { identical = false; break; }
        if (identical) return 0;

        // 구간 내에서 고유한 행 표시 (해시 등장 횟수 1회)
        var counts = new Dictionary<ulong, int>(win);
        for (int i = from; i < to; i++)
            counts[prev[i]] = counts.TryGetValue(prev[i], out int c) ? c + 1 : 1;
        var unique = new bool[win];
        int uniqueTotal = 0;
        for (int i = 0; i < win; i++)
        {
            unique[i] = counts[prev[from + i]] == 1;
            if (unique[i]) uniqueTotal++;
        }
        if (uniqueTotal < MinAnchors) return 0;   // 빈 화면 등 — 판단 불가

        int maxOffset = win - Math.Min(40, win / 4);
        for (int d = MinScrollPx; d <= maxOffset; d++)
        {
            int len = win - d;
            int allowed = Math.Max(2, len * 3 / 100);
            int mismatches = 0, anchors = 0;
            bool failed = false;
            for (int i = 0; i < len; i++)
            {
                if (prev[from + d + i] == cur[from + i])
                {
                    if (unique[d + i]) anchors++;
                }
                else if (++mismatches > allowed) { failed = true; break; }
            }
            if (!failed && anchors >= Math.Min(MinAnchors, Math.Max(1, uniqueTotal / 4)))
                return d;
        }
        return 0;
    }

    private static BitmapSource ComposeImage(List<byte[]> rows, int w)
    {
        int h = rows.Count;
        var buffer = new byte[w * 4 * h];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(rows[y], 0, buffer, y * w * 4, w * 4);

        var bs = BitmapSource.Create(w, h, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, buffer, w * 4);
        bs.Freeze();
        return bs;
    }
}
