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
        KeyRegion.Text = MainWindow.RegionKeyLabel;
        KeyWindow.Text = MainWindow.WindowKeyLabel;
        KeyFull.Text = MainWindow.FullKeyLabel;
        KeyRepeat.Text = MainWindow.RepeatKeyLabel;
        KeyElement.Text = MainWindow.ElementKeyLabel;
        KeyScroll.Text = MainWindow.ScrollKeyLabel;
        KeyFixed.Text = MainWindow.FixedKeyLabel;
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
        S.AlwaysCopyToClipboard = ChkClipboard.IsChecked == true;
        S.HideMainWindowOnCapture = ChkHide.IsChecked == true;
        S.PlaySound = ChkSound.IsChecked == true;
        S.ShowTrayIcon = ChkTray.IsChecked == true;

        // 언어 · 시작 프로그램
        S.Language = CmbLanguage.SelectedIndex == 1 ? "en" : "ko";
        S.RunAtStartup = ChkStartup.IsChecked == true;
        StartupService.Sync(S.RunAtStartup);
        if (S.Language != Loc.CurrentLanguage) Loc.Apply(S.Language);

        S.Save();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
