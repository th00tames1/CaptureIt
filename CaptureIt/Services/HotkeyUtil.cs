using System.Windows.Input;

namespace CaptureIt.Services;

/// <summary>
/// 전역 단축키 문자열("Ctrl+Shift+A" 형식)의 파싱·포맷·키 이벤트 변환.
/// 허용 키: A-Z, 0-9, F1-F12, PrintScreen. 문자·숫자 키는 수정키가 하나 이상 필요하다.
/// </summary>
public static class HotkeyUtil
{
    private const uint VK_SNAPSHOT = 0x2C;

    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>(4);
        if ((mods & HotkeyManager.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & HotkeyManager.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & HotkeyManager.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk) => vk switch
    {
        VK_SNAPSHOT => "PrtSc",
        >= 0x70 and <= 0x7B => $"F{vk - 0x70 + 1}",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}"
    };

    public static bool TryParse(string? combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        foreach (var raw in combo.Split('+'))
        {
            var token = raw.Trim();
            switch (token.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= HotkeyManager.MOD_CONTROL; continue;
                case "ALT": mods |= HotkeyManager.MOD_ALT; continue;
                case "SHIFT": mods |= HotkeyManager.MOD_SHIFT; continue;
                case "PRTSC" or "PRINTSCREEN": vk = VK_SNAPSHOT; continue;
            }
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; continue; }
            }
            if (token.Length is 2 or 3 && char.ToUpperInvariant(token[0]) == 'F' &&
                int.TryParse(token[1..], out int fn) && fn is >= 1 and <= 12)
            {
                vk = (uint)(0x70 + fn - 1);
                continue;
            }
            return false;   // 알 수 없는 토큰
        }

        if (vk == 0) return false;
        // 문자·숫자 키는 수정키 없이 전역 등록하면 일반 입력을 가로채므로 금지
        if (mods == 0 && vk is not (VK_SNAPSHOT or >= 0x70 and <= 0x7B)) return false;
        return true;
    }

    /// <summary>
    /// 설정 창의 키 입력을 단축키 문자열로 바꾼다.
    /// 수정키만 눌린 상태이거나 허용되지 않는 키면 null.
    /// </summary>
    public static string? FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        uint vk = key switch
        {
            Key.PrintScreen => VK_SNAPSHOT,
            >= Key.F1 and <= Key.F12 => (uint)(0x70 + (key - Key.F1)),
            >= Key.A and <= Key.Z => (uint)('A' + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (uint)('0' + (key - Key.NumPad0)),
            _ => 0
        };
        if (vk == 0) return null;

        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyManager.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyManager.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyManager.MOD_SHIFT;

        if (mods == 0 && vk is not (VK_SNAPSHOT or >= 0x70 and <= 0x7B)) return null;
        return Format(mods, vk);
    }
}
