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
    private ViewfinderWindow? _viewfinder;
    private bool _reallyExit;
    private bool _capturing;               // 중복 캡처 방지
    private bool _wasVisibleBeforeCapture; // 캡처 후 창 복원용
    private bool _editorWasVisibleBeforeCapture;
    private bool _viewfinderWasVisibleBeforeCapture;
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
        _ = AutoUpdateCheckAsync();
    }

    /// <summary>--autostart: 창을 띄우지 않고 트레이+단축키만 준비한다.</summary>
    public void InitTrayOnly()
    {
        EnsureHotkeys();
        EnsureTray();
        _ = AutoUpdateCheckAsync();
    }

    // ── 업데이트 자동 확인 (하루 1회) ─────────────────────────────────────
    private bool _updateCheckStarted;
    private UpdateWindow? _updateWindow;   // GC로 창이 수거되지 않도록 참조 유지

    private async Task AutoUpdateCheckAsync()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;

        if (!S.AutoUpdateCheck) return;
        if (S.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(24)) return;

        await Task.Delay(TimeSpan.FromSeconds(8));   // 시작 직후의 부하·네트워크 경합 회피
        S.LastUpdateCheck = DateTime.Now;
        S.Save();

        var info = await UpdateService.CheckAsync();
        if (info == null || info.TagName == S.SkippedVersion) return;
        ShowUpdatePopup(info);
    }

    /// <summary>업데이트 안내 팝업을 띄운다 (자동·수동 공통).</summary>
    private void ShowUpdatePopup(UpdateService.UpdateInfo info)
    {
        _updateWindow?.Close();
        _updateWindow = new UpdateWindow(info);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        if (IsVisible) _updateWindow.Owner = this;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private void UpdateHistoryCount() =>
        LblHistoryCount.Text = $"{HistoryService.Items.Count}/{HistoryService.MaxItems}";

    // ── 전역 단축키 ────────────────────────────────────────────────────────
    /// <summary>실제 등록에 성공한 단축키 라벨 (설정 창 표시용).</summary>
    public static string RegionKeyLabel { get; private set; } = "-";
    public static string WindowKeyLabel { get; private set; } = "-";
    public static string FullKeyLabel { get; private set; } = "-";
    public static string RepeatKeyLabel { get; private set; } = "-";
    public static string ElementKeyLabel { get; private set; } = "-";
    public static string ScrollKeyLabel { get; private set; } = "-";
    public static string FixedKeyLabel { get; private set; } = "-";

    /// <summary>
    /// 설정에 저장된 조합을 등록한다. 기본값 그대로인데 다른 앱이 선점한 경우에만
    /// 대체 수정키 조합을 시도하고, 성공한 조합을 설정에 반영한다.
    /// 사용자가 직접 바꾼 조합은 대체하지 않는다 (예측 가능성 우선).
    /// </summary>
    private string RegisterOne(string combo, string factoryDefault, Action<string> saveBack, Action action)
    {
        if (string.IsNullOrWhiteSpace(combo)) return "-";                       // 사용 안 함
        if (!HotkeyUtil.TryParse(combo, out uint mods, out uint vk)) return "-";

        if (_hotkeys!.Register(mods, vk, action))
            return HotkeyUtil.Format(mods, vk);

        if (combo == factoryDefault)
        {
            uint[] fallbacks =
            {
                HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
                HotkeyManager.MOD_ALT | HotkeyManager.MOD_SHIFT,
            };
            foreach (var fb in fallbacks)
            {
                if (_hotkeys.Register(fb, vk, action))
                {
                    var label = HotkeyUtil.Format(fb, vk);
                    saveBack(label);   // 설정 창에 실제 동작 조합이 보이도록 저장
                    return label;
                }
            }
        }
        return "-";
    }

    private void EnsureHotkeys()
    {
        if (_hotkeysRegistered) return;
        _hotkeysRegistered = true;
        _hotkeys = new HotkeyManager(this);
        RegisterAllHotkeys();
    }

    /// <summary>설정 변경 후 전체 재등록.</summary>
    public void ReloadHotkeys()
    {
        if (_hotkeys == null) { EnsureHotkeys(); return; }
        _hotkeys.UnregisterAll();
        RegisterAllHotkeys();
    }

    private void RegisterAllHotkeys()
    {
        RegionKeyLabel = RegisterOne(S.HotkeyRegion, "Ctrl+Shift+A", v => S.HotkeyRegion = v, () => _ = StartRegionCapture());
        ElementKeyLabel = RegisterOne(S.HotkeyElement, "Ctrl+Shift+E", v => S.HotkeyElement = v, () => _ = StartElementCapture());
        WindowKeyLabel = RegisterOne(S.HotkeyWindow, "Ctrl+Shift+W", v => S.HotkeyWindow = v, () => _ = StartWindowCapture());
        FullKeyLabel = RegisterOne(S.HotkeyFull, "Ctrl+Shift+F", v => S.HotkeyFull = v, () => _ = StartFullCapture());
        ScrollKeyLabel = RegisterOne(S.HotkeyScroll, "Ctrl+Shift+S", v => S.HotkeyScroll = v, () => _ = StartScrollCapture());
        FixedKeyLabel = RegisterOne(S.HotkeyFixed, "Ctrl+Shift+D", v => S.HotkeyFixed = v, ToggleViewfinder);
        RepeatKeyLabel = RegisterOne(S.HotkeyRepeat, "Ctrl+Shift+R", v => S.HotkeyRepeat = v, () => _ = StartRepeatCapture());

        S.Save();
        RefreshHotkeyLabels();
        if (_trayReady) RebuildTrayMenu();
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
        if (RegionKeyLabel == "-") dead.Add(Loc.Get("Word.Region"));
        if (WindowKeyLabel == "-") dead.Add(Loc.Get("Word.Window"));
        if (FullKeyLabel == "-") dead.Add(Loc.Get("Word.Full"));
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
        _viewfinder?.Close();
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
            // X 버튼: 종료가 아니라 트레이로 최소화해 단축키 캡처를 계속 유지한다
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
    private async void ElementCapture_Click(object sender, RoutedEventArgs e) => await StartElementCapture();

    /// <summary>전체 캡처 버튼: 모니터가 여러 대면 어느 화면을 캡처할지 메뉴로 고른다.</summary>
    private void FullCapture_Click(object sender, RoutedEventArgs e)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length <= 1) { _ = StartFullCaptureOf(null); return; }

        var menu = new System.Windows.Controls.ContextMenu { FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic") };
        var all = new System.Windows.Controls.MenuItem { Header = Loc.Get("Full.All"), FontWeight = FontWeights.SemiBold };
        all.Click += (_, _) => _ = StartFullCaptureOf(null);
        menu.Items.Add(all);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // 왼→오, 위→아래 순으로 번호를 매겨 사용자 화면 배치와 직관적으로 맞춘다
        var ordered = screens
            .Select((s, idx) => (s, idx))
            .OrderBy(t => t.s.Bounds.Y).ThenBy(t => t.s.Bounds.X)
            .ToList();
        int n = 1;
        foreach (var (s, _) in ordered)
        {
            var b = s.Bounds;
            string label = Loc.F("Full.Screen", n++) +
                           (s.Primary ? " " + Loc.Get("Full.Primary") : "") +
                           $"   {b.Width}×{b.Height}";
            var item = new System.Windows.Controls.MenuItem { Header = label };
            var rect = new System.Drawing.Rectangle(b.X, b.Y, b.Width, b.Height);
            item.Click += (_, _) => _ = StartFullCaptureOf(rect);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = BtnFull;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    private async void ScrollCapture_Click(object sender, RoutedEventArgs e) => await StartScrollCapture();

    private void FixedCapture_Click(object sender, RoutedEventArgs e) => ToggleViewfinder();

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
        bool? saved = win.ShowDialog();
        ApplyTraySetting();          // 트레이 설정 변경을 즉시 반영
        if (saved == true) ReloadHotkeys();   // 단축키 변경 즉시 적용
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
    /// <summary>캡처 전 준비: 창 숨김. 화면이 다시 그려질 시간을 준다.
    /// 지연(딜레이)은 여기가 아니라 대상 지정이 끝난 뒤에 건다. 순서:
    /// 대상 지정 → 카운트다운(그동안 메뉴를 열거나 화면을 준비) → 그 시점의 라이브 화면 캡처.</summary>
    private async Task<bool> PrepareCaptureAsync()
    {
        if (_capturing) return false;
        _capturing = true;
        try
        {
            _wasVisibleBeforeCapture = IsVisible;
            _editorWasVisibleBeforeCapture = _editor is { IsVisible: true };
            _viewfinderWasVisibleBeforeCapture = _viewfinder is { IsVisible: true };

            if (S.HideMainWindowOnCapture)
            {
                bool hidAny = false;
                if (IsVisible) { Hide(); hidAny = true; }
                if (_editor is { IsVisible: true }) { _editor.Hide(); hidAny = true; }
                if (_viewfinder is { IsVisible: true }) { _viewfinder.Hide(); hidAny = true; }
                if (hidAny) await Task.Delay(220);   // 창 사라짐 애니메이션 대기
            }
            return true;
        }
        catch
        {
            _capturing = false;   // 준비 중 예외로 캡처가 영구 잠기지 않게
            return false;
        }
    }

    /// <summary>딜레이 설정이 있으면 카운트다운을 표시하고 기다린다. Esc 취소 시 false.</summary>
    private async Task<bool> DelayBeforeShotAsync()
    {
        if (S.CaptureDelaySeconds <= 0) return true;
        return await FeedbackService.CountdownAsync(S.CaptureDelaySeconds);
    }

    /// <summary>지정해 둔 물리 영역을 (딜레이가 있으면 카운트다운 후) 라이브 화면에서 캡처한다.
    /// 취소(Esc) 시 null.</summary>
    private async Task<BitmapSource?> CaptureLiveAfterDelayAsync(System.Drawing.Rectangle abs)
    {
        if (!await DelayBeforeShotAsync()) return null;
        using var shot = CaptureService.CaptureRect(abs);
        FeedbackService.Flash();
        return CaptureService.ToBitmapSource(shot);
    }

    /// <summary>
    /// 오버레이에서 고른 영역을 최종 이미지로 만든다.
    /// 딜레이가 없으면 이미 찍어둔 정지 이미지를 잘라 즉시 반환하고,
    /// 딜레이가 있으면 카운트다운 뒤 그 영역의 '라이브' 화면을 캡처한다.
    /// 카운트다운을 Esc로 취소하면 null.
    /// </summary>
    private async Task<BitmapSource?> ResolveSelectionAsync(BitmapSource frozen, Int32Rect rect)
    {
        if (S.CaptureDelaySeconds > 0)
        {
            var v = System.Windows.Forms.SystemInformation.VirtualScreen;
            var abs = new System.Drawing.Rectangle(v.X + rect.X, v.Y + rect.Y, rect.Width, rect.Height);
            return await CaptureLiveAfterDelayAsync(abs);
        }
        var cropped = new CroppedBitmap(frozen, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>캡처 종료 정리. canceled=true(Esc 등)면 메인 창을 앞으로 가져온다.</summary>
    private void FinishCapture(bool canceled = false)
    {
        _capturing = false;
        // 캡처 전에 보이던 창은 복원한다 (취소해도 창이 사라진 것처럼 보이지 않게).
        if (_editorWasVisibleBeforeCapture && _editor is { IsVisible: false }) _editor.Show();
        if (_wasVisibleBeforeCapture && !IsVisible) Show();
        if (_viewfinderWasVisibleBeforeCapture && _viewfinder is { IsVisible: false }) _viewfinder.Show();
        if (IsVisible && _editor is { IsVisible: true })
            WindowLayout.StackMainAndEditor(this, _editor);
        if (canceled) ShowFromTray();   // Esc 취소 → 메인 화면 활성화
    }

    public async Task StartRegionCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        bool canceled = false;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Region);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                S.LastCaptureMode = "Region";
                var v = System.Windows.Forms.SystemInformation.VirtualScreen;
                _lastRegionRect = new System.Drawing.Rectangle(v.X + rect.X, v.Y + rect.Y, rect.Width, rect.Height);
                var img = await ResolveSelectionAsync(frozen, rect);
                if (img != null) await HandleCaptured(img);
                else canceled = true;   // 카운트다운 취소
            }
            else canceled = true;
        }
        finally { FinishCapture(canceled); }
    }

    /// <summary>편집기 '새 캡처' 버튼: 마지막에 사용한 캡처 모드를 반복한다.</summary>
    public Task StartLastCapture()
    {
        switch (S.LastCaptureMode)
        {
            case "Window": return StartWindowCapture();
            case "Full": return StartFullCapture();
            case "Element": return StartElementCapture();
            case "Scroll": return StartScrollCapture();
            case "Fixed":
                if (_viewfinder is not { IsVisible: true }) ToggleViewfinder();
                else _viewfinder.Activate();
                return Task.CompletedTask;
            default: return StartRegionCapture();
        }
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
        bool canceled = false;
        try
        {
            var img = await CaptureLiveAfterDelayAsync(rect);   // 딜레이 있으면 카운트다운 후
            if (img != null) await HandleCaptured(img);
            else canceled = true;
        }
        finally { FinishCapture(canceled); }
    }

    /// <summary>단위 영역: UI 요소를 하이라이트하고 클릭 한 번으로 캡처.</summary>
    public async Task StartElementCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        bool canceled = false;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Element);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                S.LastCaptureMode = "Element";
                var img = await ResolveSelectionAsync(frozen, rect);
                if (img != null) await HandleCaptured(img);
                else canceled = true;
            }
            else canceled = true;
        }
        finally { FinishCapture(canceled); }
    }

    /// <summary>고정 크기: 라이브 뷰파인더 틀을 토글한다 (화면 정지 없음, 즉시 표시).</summary>
    public void ToggleViewfinder()
    {
        if (_viewfinder is { IsVisible: true })
        {
            _viewfinder.Close();
            _viewfinder = null;
            return;
        }
        S.LastCaptureMode = "Fixed";
        _viewfinder = new ViewfinderWindow(this);
        _viewfinder.Closed += (_, _) => _viewfinder = null;
        _viewfinder.Show();
    }

    /// <summary>뷰파인더의 캡처 요청: 틀 안쪽 영역을 즉시 캡처한다 (틀은 유지).</summary>
    public async Task CaptureViewfinderRect(System.Drawing.Rectangle rect)
    {
        if (_capturing) return;
        _capturing = true;
        bool hidMain = false, hidEditor = false;
        try
        {
            var v = System.Windows.Forms.SystemInformation.VirtualScreen;
            rect = System.Drawing.Rectangle.Intersect(rect, v);
            if (rect.Width < 4 || rect.Height < 4) return;

            // 메인/편집기가 캡처 영역을 가리고 있으면 잠깐만 숨긴다
            hidMain = IsVisible && WindowIntersects(this, rect);
            hidEditor = _editor is { IsVisible: true } && WindowIntersects(_editor, rect);
            if (hidMain) Hide();
            if (hidEditor) _editor!.Hide();
            if (hidMain || hidEditor) await Task.Delay(200);

            using var shot = CaptureService.CaptureRect(rect);
            var img = CaptureService.ToBitmapSource(shot);
            FeedbackService.Flash();

            if (hidEditor) { _editor!.Show(); hidEditor = false; }
            if (hidMain) { Show(); hidMain = false; }

            await HandleCaptured(img);
        }
        finally
        {
            // 예외가 나도 숨긴 창은 반드시 복원한다
            if (hidEditor) _editor?.Show();
            if (hidMain) Show();
            _capturing = false;
        }
    }

    /// <summary>창(DIU)이 물리 픽셀 영역과 겹치는지.</summary>
    private static bool WindowIntersects(Window w, System.Drawing.Rectangle physRect)
    {
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;
        var r = new System.Drawing.Rectangle(
            (int)(w.Left * scale), (int)(w.Top * scale),
            (int)(w.ActualWidth * scale), (int)(w.ActualHeight * scale));
        return r.IntersectsWith(physRect);
    }

    /// <summary>스크롤 캡처: 창을 클릭하면 자동으로 끝까지 스크롤하며 이어붙인다.</summary>
    public async Task StartScrollCapture()
    {
        if (!await PrepareCaptureAsync()) return;
        bool canceled = false;
        try
        {
            CaptureService.WindowInfo? target = null;
            {
                using var shot = CaptureService.CaptureVirtualScreen();
                var frozen = CaptureService.ToBitmapSource(shot);
                var exclude = new List<IntPtr> { new WindowInteropHelper(this).Handle };
                if (_editor != null) exclude.Add(new WindowInteropHelper(_editor).Handle);
                if (_viewfinder != null) exclude.Add(new WindowInteropHelper(_viewfinder).Handle);
                var windows = CaptureService.GetVisibleWindows(exclude.ToArray());

                var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.ScrollWindow, windows);
                if (overlay.ShowDialog() == true) target = overlay.SelectedWindow;
            }
            if (target == null) { canceled = true; return; }

            S.LastCaptureMode = "Scroll";

            // 대상 창을 앞으로 가져와 가려진 부분 없이 캡처되게 한다
            CaptureService.BringToForeground(target.Handle);
            await Task.Delay(350);

            // 제목줄·테두리를 뺀 클라이언트 영역만 캡처
            var v = System.Windows.Forms.SystemInformation.VirtualScreen;
            var area = CaptureService.GetClientBounds(target.Handle);
            if (area.Width < 50 || area.Height < 80) area = CaptureService.GetWindowBounds(target.Handle);
            area = System.Drawing.Rectangle.Intersect(area, v);
            if (area.Width < 50 || area.Height < 80) { Notify(Loc.Get("Scroll.Failed")); return; }

            var (toast, toastText) = CreateScrollToast(area);
            toast.Show();
            // 토스트가 캡처 프레임에 찍히지 않도록 화면 캡처에서 제외 (Win10 2004+)
            try { SetWindowDisplayAffinity(new WindowInteropHelper(toast).Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); }
            catch { }
            try
            {
                var result = await ScrollCaptureService.RunAsync(area,
                    n => Dispatcher.Invoke(() => toastText.Text = Loc.F("Scroll.Progress", n)));
                if (result != null) await HandleCaptured(result);
                else Notify(Loc.Get("Scroll.Failed"));
            }
            finally { toast.Close(); }
        }
        finally { FinishCapture(canceled); }
    }

    /// <summary>스크롤 캡처 진행 표시 토스트: 캡처 영역과 겹치지 않는 모서리에 둔다.</summary>
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
        bool canceled = false;
        try
        {
            using var shot = CaptureService.CaptureVirtualScreen();
            var frozen = CaptureService.ToBitmapSource(shot);

            var exclude = new List<IntPtr> { new WindowInteropHelper(this).Handle };
            if (_editor != null) exclude.Add(new WindowInteropHelper(_editor).Handle);
            if (_viewfinder != null) exclude.Add(new WindowInteropHelper(_viewfinder).Handle);
            var windows = CaptureService.GetVisibleWindows(exclude.ToArray());

            var overlay = new RegionSelectWindow(frozen, RegionSelectWindow.SelectMode.Window, windows);
            bool? ok = overlay.ShowDialog();
            if (ok == true && overlay.SelectedPixelRect is { } rect)
            {
                S.LastCaptureMode = "Window";
                var img = await ResolveSelectionAsync(frozen, rect);
                if (img != null) await HandleCaptured(img);
                else canceled = true;
            }
            else canceled = true;
        }
        finally { FinishCapture(canceled); }
    }

    /// <summary>전체 캡처 (단축키·트레이·반복): 모든 모니터를 한 장으로.</summary>
    public Task StartFullCapture() => StartFullCaptureOf(null);

    /// <summary>bounds가 지정되면 그 모니터만, null이면 전체 가상 화면을 캡처한다.</summary>
    public async Task StartFullCaptureOf(System.Drawing.Rectangle? bounds)
    {
        if (!await PrepareCaptureAsync()) return;
        bool canceled = false;
        try
        {
            S.LastCaptureMode = "Full";
            var v = System.Windows.Forms.SystemInformation.VirtualScreen;
            var rect = bounds ?? new System.Drawing.Rectangle(v.X, v.Y, v.Width, v.Height);
            // 딜레이가 있으면 카운트다운 뒤 라이브 화면을, 없으면 즉시 캡처
            var img = await CaptureLiveAfterDelayAsync(rect);
            if (img != null) await HandleCaptured(img);
            else canceled = true;
        }
        finally { FinishCapture(canceled); }
    }

    // ── 캡처 결과 처리 ─────────────────────────────────────────────────────
    private async Task HandleCaptured(BitmapSource image)
    {
        if (S.PlaySound) CaptureService.PlayShutterSound();

        // 모든 캡처는 목록에 누적되어 언제든 다시 열어 편집할 수 있다
        // 스크롤 캡처처럼 큰 이미지도 UI가 멈추지 않게 인코딩은 백그라운드에서.
        // 250ms 이상 걸리면 "처리 중…" 토스트로 진행 상황을 보여준다.
        var item = await FeedbackService.WithBusyToastAsync(HistoryService.AddAsync(image));

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
                    // 히스토리 저장 실패(디스크 부족 등): 캡처를 조용히 잃지 않는다
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
