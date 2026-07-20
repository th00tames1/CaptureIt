using System.Text.Json;

namespace CaptureIt.Cross;

public class Settings
{
    public string Language { get; set; } = "en";
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureIt");

    public static Settings Current { get; private set; } = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureItCross");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Current = new(); }

        // MyPictures가 비어있는 환경(일부 리눅스) 대비
        if (string.IsNullOrWhiteSpace(Current.SaveFolder))
            Current.SaveFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CaptureIt");
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public string NewFilePath()
    {
        Directory.CreateDirectory(SaveFolder);
        var name = $"Capture_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        var path = Path.Combine(SaveFolder, $"{name}.png");
        int n = 1;
        while (File.Exists(path))
            path = Path.Combine(SaveFolder, $"{name}_{n++}.png");
        return path;
    }
}
