using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptureIt.Models;

/// <summary>캡처 후 동작</summary>
public enum AfterCaptureAction
{
    OpenEditor,      // 편집기 열기 (기본)
    SaveAndCopy,     // 바로 저장 + 클립보드 복사
    CopyOnly         // 클립보드 복사만
}

public class AppSettings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureIt");

    public string ImageFormat { get; set; } = "png";   // png | jpg | bmp
    public int JpgQuality { get; set; } = 90;
    public AfterCaptureAction AfterCapture { get; set; } = AfterCaptureAction.OpenEditor;
    public bool AlwaysCopyToClipboard { get; set; } = true;
    public bool HideMainWindowOnCapture { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public int CaptureDelaySeconds { get; set; } = 0;   // 0 | 3 | 5 | 10
    public string FileNamePrefix { get; set; } = "Capture";

    public string Language { get; set; } = "en";        // en | ko (기본: English)
    public int FixedWidth { get; set; } = 800;          // 뷰파인더(고정 크기) 안쪽 영역 물리 px
    public int FixedHeight { get; set; } = 600;
    public double? FixedLeft { get; set; }              // 뷰파인더 창 위치 (DIU, null = 화면 중앙)
    public double? FixedTop { get; set; }
    public string LastCaptureMode { get; set; } = "Region";   // 편집기 '새 캡처'가 반복할 모드
    public string TextFontFamily { get; set; } = "Malgun Gothic";   // 텍스트 도구 글꼴
    public double TextFontSize { get; set; } = 16;
    public bool RunAtStartup { get; set; } = true;      // Windows 시작 시 자동 실행
    public bool MainTopmost { get; set; } = false;      // 메인 툴바 항상 위

    // 전역 단축키 (Ctrl+Shift+A 형식, 빈 문자열 = 사용 안 함). 설정에서 변경 가능.
    public string HotkeyRegion { get; set; } = "Ctrl+Shift+A";
    public string HotkeyElement { get; set; } = "Ctrl+Shift+E";
    public string HotkeyWindow { get; set; } = "Ctrl+Shift+W";
    public string HotkeyFull { get; set; } = "Ctrl+Shift+F";
    public string HotkeyScroll { get; set; } = "Ctrl+Shift+S";
    public string HotkeyFixed { get; set; } = "Ctrl+Shift+D";
    public string HotkeyRepeat { get; set; } = "Ctrl+Shift+R";
    // 메인 창 위치 기억 (null = 아직 없음). NaN은 JSON 직렬화가 불가능하므로 nullable 사용.
    public double? MainLeft { get; set; }
    public double? MainTop { get; set; }

    [JsonIgnore]
    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CaptureIt", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null) return loaded;
            }
        }
        catch { /* 손상된 설정 파일은 기본값으로 대체 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 저장 실패는 치명적이지 않음 */ }
    }

    private static readonly object FileNameLock = new();

    /// <summary>저장 폴더를 보장하고 새 파일 경로를 만든다. 예: Capture_2026-07-19_142030.png
    /// 자리 표시 파일을 먼저 만들어 두어 여러 스레드가 동시에 채번해도 충돌하지 않는다.</summary>
    public string NewFilePath()
    {
        lock (FileNameLock)
        {
            Directory.CreateDirectory(SaveFolder);
            var name = $"{FileNamePrefix}_{DateTime.Now:yyyy-MM-dd_HHmmss}";
            var path = Path.Combine(SaveFolder, $"{name}.{ImageFormat}");
            int n = 1;
            while (File.Exists(path))
                path = Path.Combine(SaveFolder, $"{name}_{n++}.{ImageFormat}");
            try { File.Create(path).Dispose(); } catch { }
            return path;
        }
    }
}
