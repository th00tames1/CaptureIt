using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CaptureIt.Services;

/// <summary>
/// GitHub Releases 기반 업데이트 확인·다운로드.
/// 확인은 릴리스 메타데이터 1회 조회, 다운로드는 사용자가 동의했을 때만 수행한다.
/// </summary>
public static class UpdateService
{
    private const string ApiLatest = "https://api.github.com/repos/th00tames1/CaptureIt/releases/latest";
    public const string ReleasesPage = "https://github.com/th00tames1/CaptureIt/releases/latest";

    public record UpdateInfo(Version Latest, string TagName, string InstallerUrl, string HtmlUrl);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API는 User-Agent가 없으면 요청을 거부한다
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CaptureIt", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build)
            : new Version(0, 0, 0);

    /// <summary>최신 릴리스를 조회한다. 새 버전이 없거나 실패하면 null.
    /// (throwOnError: 수동 확인에서는 실패를 사용자에게 알리기 위해 예외를 던진다)</summary>
    public static async Task<UpdateInfo?> CheckAsync(bool throwOnError = false)
    {
        try
        {
            using var response = await Http.GetAsync(ApiLatest);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest <= CurrentVersion) return null;

            string htmlUrl = root.TryGetProperty("html_url", out var hu)
                ? hu.GetString() ?? ReleasesPage : ReleasesPage;

            // 자산에서 Windows 설치 프로그램을 찾는다 (버전 포함 이름)
            string? installer = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith("CaptureIt-Setup-") && name.EndsWith("-win-x64.exe"))
                    {
                        installer = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            installer ??= $"https://github.com/th00tames1/CaptureIt/releases/download/{tag}/CaptureIt-Setup-{tag.TrimStart('v')}-win-x64.exe";

            return new UpdateInfo(latest, tag, installer, htmlUrl);
        }
        catch when (!throwOnError)
        {
            return null;   // 자동 확인은 조용히 실패
        }
    }

    /// <summary>설치 프로그램을 임시 폴더로 내려받는다. 진행률 0..1 보고.</summary>
    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"CaptureIt-Setup-{info.Latest}-win-x64.exe");

        using var response = await Http.GetAsync(info.InstallerUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0) progress.Report((double)readTotal / total);
        }
        return path;
    }

    /// <summary>내려받은 설치 프로그램을 무음 모드로 실행하고 현재 앱을 종료한다.
    /// 설치가 끝나면 설치 프로그램이 새 버전을 자동으로 다시 실행한다.</summary>
    public static void RunInstallerAndExit(string installerPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES")
        {
            UseShellExecute = true
        });
        System.Windows.Application.Current.Shutdown();
    }
}
