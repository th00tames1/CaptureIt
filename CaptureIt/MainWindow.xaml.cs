using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CaptureIt.Models;
using CaptureIt.Services;

namespace CaptureIt;

public partial class MainWindow : Window
{
    private HotkeyManager? _hotkeys;
    private System.Windows.Forms.NotifyIcon? _tray;
    private EditorWindow? _editor;
    private bool _reallyExit;
    private bool _capturing;               // 중복 캡처 방지
    private bool _wasVisibleBeforeCapture; // 캡처 후 창 복원용
    private bool _editorWasVisibleBeforeCapture;
    private bool _hotkeysRegistered;
    private bool _trayReady;
    private System.Drawing.Rectangle? _lastRegionRect;   // 같은 영역 다시 캡처

    private AppSettings S => App.Settings;

    public MainWindow()
    {
        InitializeComponent();
        RestorePosition();
        Loaded += OnLoaded;
        Closing += OnClosing;
        CmbDelay.SelectedIndex = S.CaptureDelaySeconds switch { 3 => 1, 5 => 2, 10 => 3, _ => 0 };
        BtnPin.IsChecked = S.MainTopmost;
        Topmost = S.MainTopmost;

        HistoryService.Items.CollectionChanged += (_, _) => UpdateHistoryCount();
        UpdateHistoryCount();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void RestorePosition()
    {
        if (S.MainLeft is double left && S.MainTop is double top)
        {
            Left = left;
            Top = top;
            WindowLayout.ClampToWorkArea(this);
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + work.Height * 0.18;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureHotkeys();
        if (S.ShowTrayIcon) EnsureTray();
    }

    /// <summary>--autostart: 창을 띄우지 않고 트레이+단축키만 준비한다.</summary>
    public void InitTrayOnly()
    {
        EnsureHotkeys();
        EnsureTray();
    }

    private void UpdateHistoryCount() =>
        LblHistoryCount.Text = $"{HistoryService.Items.Count}/{HistoryService.MaxItems}";

    // ── 전역 단축키 ────────────────────────────────────────────────────────
    /// <summary>실제 등록에 성공한 단축키 라벨 (설정 창 표시용).</summary>
    public static string RegionKeyLabel { get; private set; } = "—";
    public static string WindowKeyLabel { get; private set; } = "—";
    public static string FullKeyLabel { get; private set; } = "—";
    public static string RepeatKeyLabel { get; private set; } = "—";
    public static string ElementKeyLabel { get; private set; } = "—";
    public static string ScrollKeyLabel { get; private set; } = "—";
    public static string FixedKeyLabel { get; private set; } = "—";

    /// <summary>선호 조합이 다른 앱에 선점됐으면 대체 조합을 차례로 시도한다.</summary>
    private string TryRegister(uint vk, Action action)
    {
        (uint mods, string prefix)[] candidates =
        {
            (HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_SHIFT, "Ctrl+Shift+"),
            (HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,   "Ctrl+Alt+"),
            (HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT,     "Alt+Shift+"),
        };
        char key = (char)vk;
        foreach (var (mods, prefix) in candidates)
            if (_hotkeys!.Register(mods, vk, action))
                return prefix + key;
        return "—";
    }

    private void EnsureHotkeys()
    {
        if (_hotkeysRegistered) return;
        _hotkeysRegistered = true;

        _hotkeys = new HotkeyManager(this);
        const uint VK_A = 0x41, VK_W = 0x57, VK_F = 0x46, VK_R = 0x52,
                   VK_E = 0x45, VK_S = 0x53, VK_D = 0x44;

        RegionKeyLabel = TryRegister(VK_A, () => _ = StartRegionCapture());
        WindowKeyLabel = TryRegister(VK_W, () => _ = StartWindowCapture());
        FullKeyLabel = TryRegister(VK_F, () => _ = StartFullCapture());
        RepeatKeyLabel = TryRegister(VK_R, () => _ = StartRepeatCapture());
        ElementKeyLabel = TryRegister(VK_E, () => _ = StartElementCapture());
        ScrollKeyLabel = TryRegister(VK_S, () => _ = StartScrollCapture());
        FixedKeyLabel = TryRegister(VK_D, () => _ = StartFixedCapture(S.FixedWidth, S.FixedHeight));

        RefreshHotkeyLabels();
    }

    private void RefreshHotkeyLabels()
    {
        LblRegionKey.Text = RegionKeyLabel;
        LblWindowKey.Text = WindowKeyLabel;
        LblFullKey.Text = FullKeyLabel;
        LblElementKey.Text = ElementKeyLabel;
        LblScrollKey.Text = ScrollKeyLabel;
        LblFixedKey.Text = FixedKeyLabel;

        var dead = new List<string>();
        if (RegionKeyLabel == "—") dead.Add(Loc.Get("Word.Region"));
        if (WindowKeyLabel == "—") dead.Add(Loc.Get("Word.Window"));
        if (FullKeyLabel == "—") dead.Add(Loc.Get("Word.Full"));
        if (dead.Count > 0)
            TxtHint.Text = Loc.F("Main.Hint.HotkeyFail", string.Join("/", dead));
    }

    private void OnLanguageChanged()
    {
        RefreshHotkeyLabels();
        if (_trayReady) RebuildTrayMenu();
    }

    // ── 트레이 아이콘 ──────────────────────────────────────────────────────
    private void EnsureTray()
    {
        if (_trayReady) return;
        _trayReady = true;

        System.Drawing.Icon trayIcon;
        try
        {
            trayIcon = Environment.ProcessPath is { } exe
                ? System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application
                : System.Drawing.SystemIcons.Application;
        }
        catch { trayIcon = System.Drawing.SystemIcons.Application; }

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = Loc.Get("Tray.Tooltip"),
            Visible = true
        };
        RebuildTrayMenu();
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void RebuildTrayMenu()
    {
        if (_tray == null) return;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.Get("Tray.Open"), null, (_, _) => ShowFromTray());
        menu.Items.Add(Loc.F("Tray.RegionCapture", RegionKeyLabel), null, (_, _) => _ = StartRegionCapture());
        menu.Items.Add(Loc.F("Tray.FullCapture", FullKeyLabel), null, (_, _) => _ = StartFullCapture());
        menu.Items.Add(Loc.F("Tray.RepeatCapture", RepeatKeyLabel), null, (_, _) => _ = StartRepeatCapture());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.History"), null, (_, _) => OpenEditor(HistoryService.Items.FirstOrDefault()));
        menu.Items.Add(Loc.Get("Tray.About"), null, (_, _) => About_Click(this, new RoutedEventArgs()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.Exit"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.Text = Loc.Get("Tray.Tooltip");
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        if (_editor is { IsVisible: true }) WindowLayout.StackMainAndEditor(this, _editor);
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        SavePosition();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        Application.Current.Shutdown();
    }

    private void SavePosition()
    {
        S.MainLeft = Left;
        S.MainTop = Top;
        S.Save();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit && S.ShowTrayIcon && _tray != null)
        {
            // X 버튼 → 트레이로 최소화 (알캡처와 동일한 UX)
            e.Cancel = true;
            SavePosition();
            Hide();
            _tray.ShowBalloonTip(1500, Loc.Get("App.Name"), Loc.Get("Tray.Background"),
                                 System.Windows.Forms.ToolTipIcon.Info);
        }
        else
        {
            ExitApp();
        }
    }

    // ── 버튼/타이틀바 이벤트 ───────────────────────────────────────────────
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            SavePosition();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = BtnPin.IsChecked == true;
        S.MainTopmost = Topmost;
        S.Save();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow();
        if (IsVisible) win.Owner = this;
        win.ShowDialog();
    }

    private async void RegionCapture_Click(object sender, RoutedEventArgs e) => await StartRegionCapture();
    private async void WindowCapture_Click(object sender, RoutedEventArgs e) => await StartWindowCapture();
    private async void FullCapture_Click(object sender, RoutedEventArgs e) => await StartFullCapture();
    private async void ElementCapture_Click(object sender, RoutedEventArgs e) => await StartElementCapture();
    private async void ScrollCapture_Click(object sender, RoutedEventArgs e) => await StartScrollCapture();

    private async void FixedCapture_Click(object sender, RoutedEventArgs e) =>
        await StartFixedCapture(S.FixedWidth, S.FixedHeight);   // 크기는 오버레이에서 직접 조절

    private void History_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(HistoryService.Items.FirstOrDefault());

    private void Delay_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        S.CaptureDelaySeconds = CmbDelay.SelectedIndex switch { 1 => 3, 2 => 5, 3 => 10, _ => 0 };
        S.Save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(S.SaveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{S.SaveFolder}\"") { UseShellExecute = true });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow();
        if (IsVisible) win.Owner = this;
        win.ShowDialog();
        ApplyTraySetting();   // 트레이 설정 변경을 즉시 반영
    }

    private void ApplyTraySetting()
    {
        if (S.ShowTrayIcon)
        {
            EnsureTray();
        }
        else if (_tray != null)
        {
            _tray.Dispose();
            _tray = null;
            _trayReady = false;
        }
    }

    // ── 편집기(결과창) 관리 ────────────────────────────────────────────────
    /// <summary>편집기를 열고(싱글턴) 항목을 로드한다. item이 null이면 빈 상태로 연다.</summary>
    public void OpenEditor(HistoryItem? item)
    {
        if (_editor == null)
        {
            _editor = new EditorWindow(this);
            // 첫 렌더 후 실제 크기가 잡히면 메인 툴바와 겹치지 않게 배치
            _editor.ContentRendered += (_, _) =>
            {
                if (IsVisible) WindowLayout.StackMainAndEditor(this, _editor);
            };
        }
        _editor.Show();
        if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
        if (item != null) _editor.ShowItem(item);
        if (IsVisible) WindowLayout.StackMainAndEditor(this, _editor);
        _editor.Activate();
    }

    // ── 캡처 플로우 ────────────────────────────────────────────────────────
    /// <summary>캡처 전 준비: 창 숨김 + 지연 대기. 화면이 다시 그려질 시간을 준다.</summary>
    private async Task<bool> PrepareCaptureAsync()
    {
        if (_capturing) return false;
        _capturing = true;
        _wasVisibleBeforeCapture = IsVisible;
        _editorWasVisibleBeforeCapture = _editor is { IsVisible: true };

        if (S.CaptureDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(S.CaptureDelaySeconds));

        if (S.HideMainWindowOnCapture)
        {
            bool hidAny = false;
            if (IsVisible) { Hide(); hidAny = true; }
            if (_editor is { IsVisible: true }) { _editor.Hide(); hidAny = true; }
            if (hidAny) await Task.Delay(220);   // 창 사라짐 애니메이션 대기
        }
        return true;
    }

    private void FinishCapture()
    {
        _capturing = false;
        // 캡처 전에 보이던 창은 복원한다 (취소해도 창이 사라진 것처럼 보이지 않게).
        if (_editorWasVisibleBeforeCapture && _editor is { IsVisible: false }) _editor.Show();
        if (_wasVisibleBeforeCapture && !IsVisible) Show();
        if (IsVisible && _editor is { IsVisible: true })
            WindowLayout.StackMainAndEditor(this, _editor);
    }

    public async Task StartRegionCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Region);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                var v = System.Windows.Forms.SystemInformation.VirtualScreen;
                _lastRegionRect = new System.Drawing.Rectangle(v.X + rect.X, v.Y + rect.Y, rect.Width, rect.Height);
                var cropped = new CroppedBitmap(frozen, rect);
                cropped.Freeze();
                await HandleCaptured(cropped);
            }
        }
        finally { FinishCapture(); }
    }

    /// <summary>직전 영역을 같은 자리에서 다시 캡처한다 (오버레이 없이 즉시).</summary>
    public async Task StartRepeatCapture()
    {
        if (_lastRegionRect is not { } rect)
        {
            await StartRegionCapture();   // 저장된 영역이 없으면 일반 영역 캡처
            return;
        }

        // 모니터 구성이 바뀌어 영역이 화면 밖이면 일반 영역 캡처로 대체
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        rect = System.Drawing.Rectangle.Intersect(rect, virtualScreen);
        if (rect.Width < 3 || rect.Height < 3)
        {
            _lastRegionRect = null;
            await StartRegionCapture();
            return;
        }

        if (!await PrepareCaptureAsync()) return;
        try
        {
            using var shot = CaptureService.CaptureRect(rect);
            var img = CaptureService.ToBitmapSource(shot);
            await HandleCaptured(img);
        }
        finally { FinishCapture(); }
    }

    /// <summary>단위 영역: UI 요소를 하이라이트하고 클릭 한 번으로 캡처.</summary>
    public async Task StartElementCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Element);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                var cropped = new CroppedBitmap(frozen, rect);
                cropped.Freeze();
                await HandleCaptured(cropped);
            }
        }
        finally { FinishCapture(); }
    }

    /// <summary>고정 크기: 지정 크기 틀을 움직여 클릭한 곳을 캡처.</summary>
    public async Task StartFixedCapture(int width, int height)
    {
        if (!await PrepareCaptureAsync()) return;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Fixed,
                                                 fixedW: width, fixedH: height);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                // 오버레이에서 조절한 크기를 다음 기본값으로 기억
                S.FixedWidth = rect.Width;
                S.FixedHeight = rect.Height;
                S.Save();

                var cropped = new CroppedBitmap(frozen, rect);
                cropped.Freeze();
                await HandleCaptured(cropped);
            }
        }
        finally { FinishCapture(); }
    }

    /// <summary>스크롤 캡처: 영역을 드래그로 고른 뒤 자동 스크롤하며 이어붙인다.</summary>
    public async Task StartScrollCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        try
        {
            Int32Rect? sel = null;
            {
                using var shot = CaptureService.CaptureVirtualScreen();
                var frozen = CaptureService.ToBitmapSource(shot);
                var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.ScrollRegion);
                if (overlay.ShowDialog() == true) sel = overlay.SelectedPixelRect;
            }
            if (sel is not { } rect) return;

            var v = System.Windows.Forms.SystemInformation.VirtualScreen;
            var abs = new System.Drawing.Rectangle(v.X + rect.X, v.Y + rect.Y, rect.Width, rect.Height);

            var (toast, toastText) = CreateScrollToast(abs);
            toast.Show();
            // 토스트가 캡처 프레임에 찍히지 않도록 화면 캡처에서 제외 (Win10 2004+)
            try { SetWindowDisplayAffinity(new WindowInteropHelper(toast).Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); }
            catch { }
            try
            {
                await Task.Delay(300);   // 오버레이가 화면에서 사라질 시간
                var result = await ScrollCaptureService.RunAsync(abs,
                    n => Dispatcher.Invoke(() => toastText.Text = Loc.F("Scroll.Progress", n)));
                if (result != null) await HandleCaptured(result);
                else Notify(Loc.Get("Scroll.Failed"));
            }
            finally { toast.Close(); }
        }
        finally { FinishCapture(); }
    }

    /// <summary>스크롤 캡처 진행 표시 토스트 — 캡처 영역과 겹치지 않는 모서리에 둔다.</summary>
    private static (Window toast, System.Windows.Controls.TextBlock text) CreateScrollToast(System.Drawing.Rectangle captureRect)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = Loc.F("Scroll.Progress", 0),
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        var toast = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(230, 31, 41, 55)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 10, 18, 10),
                Child = text
            }
        };

        // 캡처 영역이 있는 모니터의 네 모서리 중 영역과 겹치지 않는 곳에 배치 (물리 px → DIU)
        var work = System.Windows.Forms.Screen.FromRectangle(captureRect).WorkingArea;
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleX;
        int tw = (int)(320 * scale), th = (int)(56 * scale), gap = (int)(16 * scale);
        var corners = new[]
        {
            new System.Drawing.Rectangle(work.Right - tw - gap, work.Top + gap, tw, th),
            new System.Drawing.Rectangle(work.Left + gap, work.Top + gap, tw, th),
            new System.Drawing.Rectangle(work.Right - tw - gap, work.Bottom - th - gap, tw, th),
            new System.Drawing.Rectangle(work.Left + gap, work.Bottom - th - gap, tw, th),
        };
        var spot = corners.FirstOrDefault(c => !c.IntersectsWith(captureRect));
        if (spot == default) spot = corners[0];   // 다 겹치면 캡처 제외 처리에 의존
        toast.Left = spot.X / scale;
        toast.Top = spot.Y / scale;
        return (toast, text);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public async Task StartWindowCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var exclude = new List<IntPtr> { new WindowInteropHelper(this).Handle };
            if (_editor != null) exclude.Add(new WindowInteropHelper(_editor).Handle);
            var windows = CaptureService.GetVisibleWindows(exclude.ToArray());

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Window, windows);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                var cropped = new CroppedBitmap(frozen, rect);
                cropped.Freeze();
                await HandleCaptured(cropped);
            }
        }
        finally { FinishCapture(); }
    }

    public async Task StartFullCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);
            await HandleCaptured(frozen);
        }
        finally { FinishCapture(); }
    }

    // ── 캡처 결과 처리 ─────────────────────────────────────────────────────
    private async Task HandleCaptured(BitmapSource image)
    {
        if (S.PlaySound) CaptureService.PlayShutterSound();

        // 모든 캡처는 목록에 쌓인다 — 언제든 다시 열어 편집 가능 (알캡처 벤치마크)
        // 스크롤 캡처처럼 큰 이미지도 UI가 멈추지 않게 인코딩은 백그라운드에서.
        var item = await HistoryService.AddAsync(image);

        // 클립보드에는 DIB + PNG + 파일까지 함께 올려 어디든 바로 붙여넣게 한다
        bool copied = false;
        if (S.AlwaysCopyToClipboard)
            copied = await CaptureService.CopyToClipboardAsync(image, item?.FilePath);

        switch (S.AfterCapture)
        {
            case AfterCaptureAction.OpenEditor:
                if (item != null) OpenEditor(item);
                else
                {
                    // 히스토리 저장 실패(디스크 부족 등) — 캡처를 조용히 잃지 않는다
                    if (!copied) copied = await CaptureService.CopyToClipboardAsync(image);
                    Notify(Loc.Get(copied ? "Main.Hint.HistorySaveFailed" : "Msg.CopyFailed"));
                }
                break;

            case AfterCaptureAction.SaveAndCopy:
                var path = S.NewFilePath();
                await Task.Run(() => CaptureService.SaveToFile(image, path, S.JpgQuality));
                if (!copied && S.AlwaysCopyToClipboard == false)
                    copied = await CaptureService.CopyToClipboardAsync(image, item?.FilePath);
                Notify(Loc.F("Main.Hint.Saved", System.IO.Path.GetFileName(path)));
                break;

            case AfterCaptureAction.CopyOnly:
                if (!copied) copied = await CaptureService.CopyToClipboardAsync(image, item?.FilePath);
                Notify(Loc.Get(copied ? "Main.Hint.Copied" : "Msg.CopyFailed"));
                break;
        }
    }

    private void Notify(string message)
    {
        if (_tray != null)
            _tray.ShowBalloonTip(2000, Loc.Get("App.Name"), message, System.Windows.Forms.ToolTipIcon.Info);
        TxtHint.Text = message;
    }
}
