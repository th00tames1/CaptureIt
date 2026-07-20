using System.Diagnostics;

namespace CaptureIt.Cross;

/// <summary>
/// 플랫폼별 화면 캡처. 결과는 PNG 파일 경로 (실패/취소 시 null).
/// macOS/Linux는 OS 네이티브 도구에 위임한다 — Wayland·멀티모니터·권한 문제를
/// 가장 안정적으로 처리하는 방법이고, 영역 선택 UI도 OS 것을 그대로 쓴다.
/// </summary>
public static class ScreenGrab
{
    public enum Mode { Full, Region }

    public record Result(string? PngPath, string? Error);

    public static async Task<Result> CaptureAsync(Mode mode)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"captureit_{Guid.NewGuid():N}.png");
        try
        {
            if (OperatingSystem.IsWindows())
                return CaptureWindows(mode, tmp);
            if (OperatingSystem.IsMacOS())
                return await CaptureMacAsync(mode, tmp);
            return await CaptureLinuxAsync(mode, tmp);
        }
        catch (Exception ex)
        {
            return new Result(null, ex.Message);
        }
    }

    // ── Windows (개발/테스트용 — 정식 Windows 배포판은 WPF 에디션) ──────────
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static Result CaptureWindows(Mode mode, string tmp)
    {
#pragma warning disable CA1416
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);   // SM_CXSCREEN/SM_CYSCREEN (주 모니터)
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
#pragma warning restore CA1416
        // Region은 호출측에서 오버레이로 잘라낸다
        return new Result(tmp, null);
    }

    // ── macOS ──────────────────────────────────────────────────────────────
    private static async Task<Result> CaptureMacAsync(Mode mode, string tmp)
    {
        // -x: 소리 없음, -i: 네이티브 영역 선택 (Esc로 취소하면 파일이 안 생긴다)
        var args = mode == Mode.Region ? $"-i -x \"{tmp}\"" : $"-x \"{tmp}\"";
        var code = await RunAsync("/usr/sbin/screencapture", args, 120_000);
        if (File.Exists(tmp) && new FileInfo(tmp).Length > 0) return new Result(tmp, null);
        return new Result(null, code == 0 ? "cancelled" : "screencapture failed");
    }

    // ── Linux: 설치된 도구를 순서대로 시도 ─────────────────────────────────
    private static async Task<Result> CaptureLinuxAsync(Mode mode, string tmp)
    {
        var candidates = mode == Mode.Region
            ? new (string exe, string args)[]
            {
                ("gnome-screenshot", $"-a -f \"{tmp}\""),
                ("spectacle", $"-r -b -n -o \"{tmp}\""),
                ("grim", $"-g \"$(slurp)\" \"{tmp}\""),          // sh 경유 필요
                ("scrot", $"-s -o \"{tmp}\""),
                ("import", $"\"{tmp}\""),                          // ImageMagick (클릭-드래그)
            }
            : new (string exe, string args)[]
            {
                ("gnome-screenshot", $"-f \"{tmp}\""),
                ("spectacle", $"-f -b -n -o \"{tmp}\""),
                ("grim", $"\"{tmp}\""),
                ("scrot", $"-o \"{tmp}\""),
                ("import", $"-window root \"{tmp}\""),
            };

        foreach (var (exe, args) in candidates)
        {
            if (!ExistsOnPath(exe)) continue;

            // grim+slurp 조합은 셸 확장이 필요하다
            if (exe == "grim" && args.Contains("slurp"))
            {
                if (!ExistsOnPath("slurp")) continue;
                await RunAsync("/bin/sh", $"-c 'grim -g \"$(slurp)\" \"{tmp}\"'", 120_000);
            }
            else
            {
                await RunAsync(exe, args, 120_000);
            }

            if (File.Exists(tmp) && new FileInfo(tmp).Length > 0) return new Result(tmp, null);
            // 파일이 없으면 취소 또는 실패 — 취소로 보고 다음 도구는 시도하지 않는다
            // (도구는 있는데 사용자가 Esc를 눌렀을 가능성이 높다)
            return new Result(null, "cancelled");
        }
        return new Result(null, "no-tool");
    }

    // ── 공통 ───────────────────────────────────────────────────────────────
    private static bool ExistsOnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Any(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, exe)));
    }

    private static async Task<int> RunAsync(string exe, string args, int timeoutMs)
    {
        using var proc = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc == null) return -1;
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return -1; }
        return proc.ExitCode;
    }
}
