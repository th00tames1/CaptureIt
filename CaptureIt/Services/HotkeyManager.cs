using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CaptureIt.Services;

/// <summary>전역 단축키 등록/해제. 창이 숨겨져 있어도 동작한다.</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int ProbeId = 0xBFFF;   // 가용성 확인 전용 id (UnregisterAll이 _nextId를 1로 되돌리므로 겹치지 않는다)
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly HashSet<int> _queued = new();   // 큐에 이미 올려둔 단축키 (키 반복 흡수)
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

    /// <summary>
    /// 지금 이 조합을 등록할 수 있는지만 확인한다 (즉시 해제하므로 상태는 그대로).
    /// 우리가 이미 잡고 있는 조합도 false가 되므로 '놓친 단축키' 확인 용도로만 쓴다.
    /// </summary>
    public bool IsAvailable(uint modifiers, uint vk)
    {
        if (!RegisterHotKey(_source.Handle, ProbeId, modifiers, vk)) return false;
        UnregisterHotKey(_source.Handle, ProbeId);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (!_actions.TryGetValue(id, out var action)) return IntPtr.Zero;
        handled = true;

        // 창 프로시저 안에서 바로 실행하면 오버레이의 ShowDialog(중첩 메시지 루프)가
        // WM_HOTKEY 처리 도중에 돌아 재진입·응답 없음 상태를 만든다. 큐로 넘기고 즉시 반환한다.
        if (!_queued.Add(id)) return IntPtr.Zero;   // 키를 누르고 있어 반복 전달된 경우
        _source.Dispatcher.BeginInvoke(new Action(() =>
        {
            _queued.Remove(id);
            action();
        }), DispatcherPriority.Input);
        return IntPtr.Zero;
    }

    /// <summary>등록된 단축키를 모두 해제한다 (설정 변경 후 재등록용).</summary>
    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _queued.Clear();
        _nextId = 1;   // 모든 id를 방금 반납했으므로 다시 1부터 (ProbeId와 영원히 겹치지 않게)
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
