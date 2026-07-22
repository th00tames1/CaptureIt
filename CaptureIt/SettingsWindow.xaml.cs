using System.Windows;
using CaptureIt.Models;
using CaptureIt.Services;

namespace CaptureIt;

public partial class SettingsWindow : Window
{
    private AppSettings S => App.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        LoadFromSettings();
        HkRegion.Text = S.HotkeyRegion;
        HkElement.Text = S.HotkeyElement;
        HkWindow.Text = S.HotkeyWindow;
        HkFull.Text = S.HotkeyFull;
        HkScroll.Text = S.HotkeyScroll;
        HkFixed.Text = S.HotkeyFixed;
        HkRepeat.Text = S.HotkeyRepeat;
        HkPrintScreen.Text = S.HotkeyPrintScreen;
    }

    /// <summary>
    /// 단축키 입력란: 누른 키 조합을 그대로 기록한다.
    /// Backspace/Delete는 해제(빈 값), Tab은 포커스 이동, Esc는 포커스 해제.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var box = (System.Windows.Controls.TextBox)sender;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (key == System.Windows.Input.Key.Tab) return;   // 기본 포커스 이동 허용
        e.Handled = true;

        switch (key)
        {
            case System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete:
                box.Text = "";
                return;
            case System.Windows.Input.Key.Escape:
                System.Windows.Input.Keyboard.ClearFocus();
                return;
            // 수정키 단독 입력은 무시 (조합 완성 대기)
            case System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                 or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                 or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                 or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin:
                return;
        }

        var combo = Services.HotkeyUtil.FromKeyEvent(key, System.Windows.Input.Keyboard.Modifiers);
        if (combo != null) box.Text = combo;
    }

    /// <summary>
    /// PrintScreen 입력란: 조합을 바꿀 수는 없고 사용/해제만 전환한다.
    /// PrtSc는 OS가 먼저 가져가서 일반 키 입력으로 들어오지 않는 경우가 많으므로
    /// 키를 받아 기록하는 방식(HotkeyBox_PreviewKeyDown)을 쓰지 않는다.
    /// Backspace/Delete = 사용 안 함, 그 밖의 키 = 사용.
    /// </summary>
    private void PrintScreenBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var box = (System.Windows.Controls.TextBox)sender;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (key == System.Windows.Input.Key.Tab) return;   // 기본 포커스 이동 허용
        e.Handled = true;

        switch (key)
        {
            case System.Windows.Input.Key.Escape:
                System.Windows.Input.Keyboard.ClearFocus();
                return;
            case System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete:
                box.Text = "";       // 사용 안 함
                return;
            // 수정키 단독 입력은 무시한다: Shift+Tab 등으로 포커스를 옮길 때 먼저 도착하는
            // Shift/Ctrl/Alt 키다운이 실수로 다시 켜 버리지 않게 한다.
            case System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                 or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                 or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                 or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin:
                return;
        }
        box.Text = "PrtSc";          // 그 밖의 키 = 사용
    }

    private void LoadFromSettings()
    {
        CmbLanguage.SelectedIndex = S.Language == "en" ? 1 : 0;
        ChkStartup.IsChecked = S.RunAtStartup;

        TxtFolder.Text = S.SaveFolder;
        TxtPrefix.Text = S.FileNamePrefix;

        FmtPng.IsChecked = S.ImageFormat == "png";
        FmtJpg.IsChecked = S.ImageFormat == "jpg";
        FmtBmp.IsChecked = S.ImageFormat == "bmp";

        ActEditor.IsChecked = S.AfterCapture == AfterCaptureAction.OpenEditor;
        ActSave.IsChecked = S.AfterCapture == AfterCaptureAction.SaveAndCopy;
        ActCopy.IsChecked = S.AfterCapture == AfterCaptureAction.CopyOnly;

        ChkAutoUpdate.IsChecked = S.AutoUpdateCheck;
        TxtCurrentVersion.Text = Loc.F("Settings.CurrentVersion", "v" + Services.UpdateService.CurrentVersion);
        ChkClipboard.IsChecked = S.AlwaysCopyToClipboard;
        ChkHide.IsChecked = S.HideMainWindowOnCapture;
        ChkSound.IsChecked = S.PlaySound;
        ChkTray.IsChecked = S.ShowTrayIcon;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.Get("Settings.BrowseDesc"),
            SelectedPath = System.IO.Directory.Exists(TxtFolder.Text) ? TxtFolder.Text : "",
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtFolder.Text = dlg.SelectedPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = TxtFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, Loc.Get("Settings.FolderMissing"), Loc.Get("App.Name"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { System.IO.Directory.CreateDirectory(folder); }
        catch
        {
            MessageBox.Show(this, Loc.Get("Settings.FolderCreateFail"), Loc.Get("App.Name"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        S.SaveFolder = folder;
        S.FileNamePrefix = string.IsNullOrWhiteSpace(TxtPrefix.Text) ? "Capture" : TxtPrefix.Text.Trim();
        S.ImageFormat = FmtJpg.IsChecked == true ? "jpg" : FmtBmp.IsChecked == true ? "bmp" : "png";
        S.AfterCapture = ActSave.IsChecked == true ? AfterCaptureAction.SaveAndCopy
                       : ActCopy.IsChecked == true ? AfterCaptureAction.CopyOnly
                       : AfterCaptureAction.OpenEditor;
        S.AutoUpdateCheck = ChkAutoUpdate.IsChecked == true;
        S.AlwaysCopyToClipboard = ChkClipboard.IsChecked == true;
        S.HideMainWindowOnCapture = ChkHide.IsChecked == true;
        S.PlaySound = ChkSound.IsChecked == true;
        S.ShowTrayIcon = ChkTray.IsChecked == true;

        // 단축키: 중복 검증 후 저장 (입력 단계에서 형식은 이미 보장됨)
        var hotkeyBoxes = new (System.Windows.Controls.TextBox box, Action<string> set)[]
        {
            (HkRegion, v => S.HotkeyRegion = v),
            (HkElement, v => S.HotkeyElement = v),
            (HkWindow, v => S.HotkeyWindow = v),
            (HkFull, v => S.HotkeyFull = v),
            (HkScroll, v => S.HotkeyScroll = v),
            (HkFixed, v => S.HotkeyFixed = v),
            (HkRepeat, v => S.HotkeyRepeat = v),
            (HkPrintScreen, v => S.HotkeyPrintScreen = v),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (box, _) in hotkeyBoxes)
        {
            var combo = box.Text.Trim();
            if (combo.Length == 0) continue;
            if (!Services.HotkeyUtil.TryParse(combo, out _, out _) || !seen.Add(combo))
            {
                MessageBox.Show(this, Loc.F("Settings.HotkeyDup", combo), Loc.Get("App.Name"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var prevPrintScreen = S.HotkeyPrintScreen;
        foreach (var (box, set) in hotkeyBoxes) set(box.Text.Trim());

        // 언어 · 시작 프로그램
        S.Language = CmbLanguage.SelectedIndex == 1 ? "en" : "ko";
        S.RunAtStartup = ChkStartup.IsChecked == true;
        StartupService.Sync(S.RunAtStartup);
        if (S.Language != Loc.CurrentLanguage) Loc.Apply(S.Language);

        S.Save();

        // PrtSc를 새로 켰는데 Windows가 이 키를 캡처 도구에 묶어 두었으면 한 번만 안내한다.
        // RegisterHotKey는 성공하지만 앱이 포그라운드일 때만 키가 오므로 등록 결과로는 알 수 없다.
        // OS 설정은 사용자가 직접 꺼야 하므로 설정 페이지만 열어 준다 (앱이 값을 쓰지는 않는다).
        if (prevPrintScreen.Length == 0 && S.HotkeyPrintScreen.Length > 0 &&
            PrintScreenKeyService.IsClaimedByWindows())
        {
            var answer = MessageBox.Show(this, Loc.Get("Settings.PrintScreenBlocked"), Loc.Get("App.Name"),
                                         MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) PrintScreenKeyService.OpenWindowsKeyboardSettings();
        }

        DialogResult = true;
        Close();
    }

    /// <summary>수동 업데이트 확인: 최신이면 안내, 새 버전이면 팝업 (건너뛴 버전도 다시 보여준다).</summary>
    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckNow.IsEnabled = false;
        TxtUpdateStatus.Text = Loc.Get("Update.Checking");
        try
        {
            S.LastUpdateCheck = DateTime.Now;
            S.Save();
            var info = await Services.UpdateService.CheckAsync(throwOnError: true);
            if (info == null)
            {
                TxtUpdateStatus.Text = Loc.Get("Update.UpToDate");
            }
            else
            {
                TxtUpdateStatus.Text = "";
                var win = new UpdateWindow(info) { Owner = this, Topmost = false };
                win.ShowDialog();
            }
        }
        catch
        {
            TxtUpdateStatus.Text = Loc.Get("Update.CheckFailed");
        }
        finally
        {
            BtnCheckNow.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
