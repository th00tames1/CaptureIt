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
        ChkPrintScreen.IsChecked = S.HotkeyPrintScreen.Length > 0;   // 고정 키 → 체크박스
        UpdatePrintScreenWarning();
    }

    /// <summary>PrtSc를 켰는데 Windows가 그 키를 캡처 도구에 쓰고 있으면 안내·설정 버튼을 보여준다.</summary>
    private void UpdatePrintScreenWarning() =>
        PrtScClaimedPanel.Visibility =
            ChkPrintScreen.IsChecked == true && Services.PrintScreenKeyService.IsClaimedByWindows()
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    private void PrintScreen_Toggled(object sender, RoutedEventArgs e) => UpdatePrintScreenWarning();

    private void OpenWinKeyboard_Click(object sender, RoutedEventArgs e) =>
        Services.PrintScreenKeyService.OpenWindowsKeyboardSettings();

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
        foreach (var (box, set) in hotkeyBoxes) set(box.Text.Trim());
        // PrtSc는 고정 키라 체크박스로만 켜고 끈다 (Windows가 이 키를 쓰고 있으면 설정 창에서 안내).
        S.HotkeyPrintScreen = ChkPrintScreen.IsChecked == true ? "PrtSc" : "";

        // 언어 · 시작 프로그램
        S.Language = CmbLanguage.SelectedIndex == 1 ? "en" : "ko";
        S.RunAtStartup = ChkStartup.IsChecked == true;
        StartupService.Sync(S.RunAtStartup);
        if (S.Language != Loc.CurrentLanguage) Loc.Apply(S.Language);

        S.Save();

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
