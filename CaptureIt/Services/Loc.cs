using System.Windows;

namespace CaptureIt.Services;

/// <summary>다국어 리소스 관리. Strings.{lang}.xaml을 앱 리소스에 병합한다.</summary>
public static class Loc
{
    private static ResourceDictionary? _current;

    /// <summary>언어가 바뀐 뒤 코드로 만든 텍스트(트레이 메뉴 등)를 갱신할 수 있게 알린다.</summary>
    public static event Action? LanguageChanged;

    public static string CurrentLanguage { get; private set; } = "ko";

    public static void Apply(string lang)
    {
        if (lang != "ko" && lang != "en") lang = "ko";

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Strings.{lang}.xaml")
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        CurrentLanguage = lang;

        LanguageChanged?.Invoke();
    }

    /// <summary>코드 비하인드용 문자열 조회.</summary>
    public static string Get(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    /// <summary>포맷 문자열 조회 ({0} 치환).</summary>
    public static string F(string key, params object[] args) =>
        string.Format(Get(key), args);
}
