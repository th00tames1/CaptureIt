using System.Windows;
using CaptureIt.Services;

namespace CaptureIt;

/// <summary>새 버전 안내 팝업: 지금 업데이트 / 나중에 / 이 버전 건너뛰기.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService.UpdateInfo _info;
    private System.Threading.CancellationTokenSource? _cts;
    private bool _downloading;

    public UpdateWindow(UpdateService.UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        TxtHeadline.Text = Loc.Get("Update.Available");
        TxtVersions.Text = Loc.F("Update.Versions", info.TagName, "v" + UpdateService.CurrentVersion);
        NotesRun.Text = Loc.Get("Update.ViewNotes");
        TxtSkip.Text = Loc.Get("Update.Skip");
        TxtLater.Text = Loc.Get("Update.Later");
        TxtUpdate.Text = Loc.Get("Update.Now");

        Closing += (_, _) => _cts?.Cancel();
    }

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_info.HtmlUrl)
            { UseShellExecute = true });
        }
        catch { }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.SkippedVersion = _info.TagName;
        App.Settings.Save();
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        _cts = new System.Threading.CancellationTokenSource();

        BtnUpdate.IsEnabled = false;
        BtnSkip.IsEnabled = false;
        TxtError.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        TxtProgress.Text = Loc.F("Update.Downloading", 0);

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p * 100;
                TxtProgress.Text = Loc.F("Update.Downloading", (int)(p * 100));
            });
            var path = await UpdateService.DownloadInstallerAsync(_info, progress, _cts.Token);

            TxtProgress.Text = Loc.Get("Update.Installing");
            UpdateService.RunInstallerAndExit(path);
        }
        catch (OperationCanceledException)
        {
            // 창을 닫아 취소한 경우: 조용히 종료
        }
        catch
        {
            _downloading = false;
            BtnUpdate.IsEnabled = true;
            BtnSkip.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            TxtError.Text = Loc.Get("Update.Failed");
            TxtError.Visibility = Visibility.Visible;
        }
    }
}
