using System.Diagnostics;

namespace CaptureIt.Cross;

/// <summary>PNG 파일을 OS 클립보드에 이미지로 올린다 (플랫폼별 도구 위임).</summary>
public static class ClipboardHelper
{
    public static async Task<bool> CopyImageAsync(string pngPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return await RunOk("powershell",
                    $"-NoProfile -Command \"Set-Clipboard -Path '{pngPath}'\"");

            if (OperatingSystem.IsMacOS())
                return await RunOk("/usr/bin/osascript",
                    $"-e 'set the clipboard to (read (POSIX file \"{pngPath}\") as «class PNGf»)'");

            // Linux: Wayland → X11 순서로 시도
            if (ExistsOnPath("wl-copy"))
                return await RunOk("/bin/sh", $"-c 'wl-copy < \"{pngPath}\"'");
            if (ExistsOnPath("xclip"))
                return await RunOk("xclip",
                    $"-selection clipboard -t image/png -i \"{pngPath}\"");
            return false;
        }
        catch { return false; }
    }

    private static bool ExistsOnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Any(p => !string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, exe)));
    }

    private static async Task<bool> RunOk(string exe, string args)
    {
        using var proc = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc == null) return false;
        using var cts = new CancellationTokenSource(15_000);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return false; }
        return proc.ExitCode == 0;
    }
}
