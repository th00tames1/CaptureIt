using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CaptureIt.Services;

/// <summary>전역 단축키 등록/해제. 창이 숨겨져 있어도 동작한다.</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle)!;
        _source.AddHook(WndProc);
    }

    /// <summary>등록 성공 여부를 반환한다 (다른 앱이 선점했으면 false).</summary>
    public bool Register(uint modifiers, uint vk, Action action)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers, vk)) return false;
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
    }
}
