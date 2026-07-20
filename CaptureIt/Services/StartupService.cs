using Microsoft.Win32;

namespace CaptureIt.Services;

/// <summary>Windows 시작 시 자동 실행 (HKCU Run 키) 관리.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CaptureIt";

    /// <summary>설정 값과 레지스트리를 일치시킨다. 자동 시작 시에는 트레이로 조용히 시작한다.</summary>
    public static void Sync(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;

            key.DeleteValue("EasyCapture", throwOnMissingValue: false);   // 구 이름 항목 정리

            if (enable && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --autostart");
            else if (!enable)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* 권한 문제 등은 조용히 무시 */ }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
