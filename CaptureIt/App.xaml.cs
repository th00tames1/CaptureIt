using System.Windows;
using CaptureIt.Models;
using CaptureIt.Services;

namespace CaptureIt;

public partial class App : Application
{
    private static System.Threading.Mutex? _mutex;
    private static bool _ownsMutex;
    private static bool _started;
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 구 버전(EasyCapture) 데이터 폴더가 있으면 한 번만 이전한다 (설정·캡처 목록 유지)
        MigrateLegacyData();

        // 언어를 먼저 적용해야 중복 실행 안내도 올바른 언어로 나온다
        Settings = AppSettings.Load();
        Loc.Apply(Settings.Language);

        // 중복 실행 방지
        _mutex = new System.Threading.Mutex(true, "CaptureIt_SingleInstance", out bool isNew);
        _ownsMutex = isNew;
        if (!isNew)
        {
            MessageBox.Show(Loc.Get("Msg.AlreadyRunning"), "CaptureIt",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _started = true;
        HistoryService.Load();
        StartupService.Sync(Settings.RunAtStartup);

        // 예기치 못한 오류로 앱 전체가 죽지 않도록 보호
        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CaptureIt_error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Exception}\n\n");
            }
            catch { }
            MessageBox.Show($"{Loc.Get("Msg.Error")}\n{ex.Exception.Message}",
                            Loc.Get("App.Name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        // 숨김 테스트 인자: --editor (전체 화면을 캡처해 편집기 바로 열기)
        if (e.Args.Contains("--editor"))
        {
            var img = CaptureService.ToBitmapSource(CaptureService.CaptureVirtualScreen());
            var item = HistoryService.Add(img);
            var main0 = new MainWindow();
            MainWindow = main0;
            main0.InitTrayOnly();   // 트레이·단축키가 있어야 종료 경로가 생긴다
            main0.OpenEditor(item);
            return;
        }

        var main = new MainWindow();
        MainWindow = main;

        // Windows 시작 시(--autostart)에는 트레이로 조용히 시작
        bool autostart = e.Args.Contains("--autostart") && Settings.ShowTrayIcon;
        if (!autostart) main.Show();
        else main.InitTrayOnly();

        // 숨김 테스트 인자: --region / --window (해당 캡처 즉시 시작)
        if (e.Args.Contains("--region"))
            Dispatcher.BeginInvoke(() => _ = main.StartRegionCapture());
        else if (e.Args.Contains("--window"))
            Dispatcher.BeginInvoke(() => _ = main.StartWindowCapture());
    }

    private static void MigrateLegacyData()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = System.IO.Path.Combine(appData, "EasyCapture");
            var newDir = System.IO.Path.Combine(appData, "CaptureIt");
            if (System.IO.Directory.Exists(oldDir) && !System.IO.Directory.Exists(newDir))
                System.IO.Directory.Move(oldDir, newDir);
        }
        catch { /* 이전 실패 시 새 폴더로 새로 시작 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 중복 실행으로 바로 종료되는 경우: 설정을 덮어쓰지도, 남의 뮤텍스를 풀지도 않는다
        if (_started) Settings.Save();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
