namespace CaptureIt.Cross;

/// <summary>가벼운 2개 언어(en/ko) 문자열 테이블.</summary>
public static class Loc
{
    public static string Language { get; set; } = "en";   // en | ko

    public static string Get(string key) =>
        Table.TryGetValue(key, out var pair) ? (Language == "ko" ? pair.ko : pair.en) : key;

    private static readonly Dictionary<string, (string en, string ko)> Table = new()
    {
        ["App.Title"] = ("CaptureIt", "CaptureIt"),
        ["Btn.Region"] = ("Region", "영역 캡처"),
        ["Btn.Full"] = ("Full screen", "전체 캡처"),
        ["Btn.Save"] = ("Save", "저장"),
        ["Btn.Copy"] = ("Copy", "복사"),
        ["Btn.Undo"] = ("Undo", "실행 취소"),
        ["Btn.Delete"] = ("Delete", "삭제"),
        ["Btn.Folder"] = ("Folder", "폴더 열기"),
        ["Btn.Lang"] = ("한국어", "English"),
        ["Tool.Select"] = ("Select", "선택"),
        ["Tool.Pen"] = ("Pen", "펜"),
        ["Tool.Box"] = ("Box", "사각형"),
        ["Tool.Arrow"] = ("Arrow", "화살표"),
        ["Tool.Crop"] = ("Crop", "자르기"),
        ["History.Title"] = ("Recent captures", "최근 캡처"),
        ["Status.Ready"] = ("Capture something to get started", "캡처 버튼을 눌러 시작하세요"),
        ["Status.Saved"] = ("Saved — {0}", "저장 완료 — {0}"),
        ["Status.Copied"] = ("Copied to clipboard", "클립보드에 복사되었습니다"),
        ["Status.CopyFailed"] = ("Copy failed — no clipboard tool found (install xclip or wl-clipboard)", "복사 실패 — 클립보드 도구가 없습니다 (xclip 또는 wl-clipboard 설치)"),
        ["Status.Captured"] = ("Captured {0}×{1}", "{0}×{1} 캡처됨"),
        ["Status.CaptureFailed"] = ("Capture failed — no screenshot tool found. Install one of: gnome-screenshot, spectacle, grim+slurp, scrot, ImageMagick", "캡처 실패 — 스크린샷 도구가 없습니다. gnome-screenshot, spectacle, grim+slurp, scrot, ImageMagick 중 하나를 설치하세요"),
        ["Status.Cancelled"] = ("Capture cancelled", "캡처가 취소되었습니다"),
        ["Status.Cropped"] = ("Cropped", "잘라내기 완료"),
        ["Overlay.Hint"] = ("Drag to select an area · Esc to cancel", "드래그하여 영역을 선택하세요 · Esc 취소"),
        ["About"] = ("CaptureIt cross-platform edition · © 2026 Heechan Jeong · MIT License", "CaptureIt 크로스 플랫폼 에디션 · © 2026 Heechan Jeong · MIT 라이선스"),
    };

    public static string F(string key, params object[] args) => string.Format(Get(key), args);
}
