using Microsoft.Win32;

namespace CaptureIt.Services;

/// <summary>
/// Windows가 PrintScreen 키를 캡처 도구(Snipping Tool)에 묶어 두었는지 확인한다.
/// 이 설정이 켜져 있으면 RegisterHotKey가 성공하더라도 앱 창이 포그라운드일 때만 키가 전달되어
/// (MSDN RegisterHotKey 비고) 트레이 상태에서는 PrtSc가 동작하지 않는다.
/// 값은 읽기만 한다. OS 설정 변경은 사용자가 직접 해야 하므로 절대 쓰지 않는다.
/// </summary>
public static class PrintScreenKeyService
{
    private const string KeyboardKey = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// <summary>Windows 설정 &gt; 접근성 &gt; 키보드 (사용자가 직접 끄도록 안내).</summary>
    private const string SettingsUri = "ms-settings:easeofaccess-keyboard";

    /// <summary>true면 OS가 PrtSc를 선점한 상태.</summary>
    public static bool IsClaimedByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyboardKey);
            if (key?.GetValue(ValueName) is { } raw) return Convert.ToInt32(raw) != 0;
        }
        catch { /* 권한·형식 문제는 OS 기본값으로 판단 */ }

        // 값이 없으면 OS 기본값: Windows 11 22H2(빌드 22621+)부터 켜짐, 그 이전은 꺼짐
        return Environment.OSVersion.Version.Build >= 22621;
    }

    /// <summary>Windows 키보드 설정 페이지를 연다 (실패해도 조용히 무시).</summary>
    public static void OpenWindowsKeyboardSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(SettingsUri) { UseShellExecute = true });
        }
        catch { /* 설정 앱이 없거나 정책으로 차단된 경우 */ }
    }
}
