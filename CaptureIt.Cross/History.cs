using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace CaptureIt.Cross;

public class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Width { get; set; }
    public int Height { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => $"{Width}×{Height} · {CreatedAt:HH:mm}";

    private Bitmap? _thumb;
    [System.Text.Json.Serialization.JsonIgnore]
    public Bitmap? Thumb
    {
        get
        {
            if (_thumb != null) return _thumb;
            try
            {
                using var fs = File.OpenRead(FilePath);
                _thumb = Bitmap.DecodeToWidth(fs, 208);
            }
            catch { }
            return _thumb;
        }
    }

    public void InvalidateThumb() => _thumb = null;
}

/// <summary>캡처 목록: PNG 파일 + JSON 인덱스 (최대 100개).</summary>
public static class History
{
    public const int Limit = 100;
    public static ObservableCollection<HistoryItem> Items { get; } = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaptureItCross", "History");
    private static string IndexPath => Path.Combine(Dir, "history.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var list = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(IndexPath));
            if (list == null) return;
            Items.Clear();
            foreach (var item in list)
            {
                if (!File.Exists(item.FilePath))
                {
                    var relocated = Path.Combine(Dir, Path.GetFileName(item.FilePath));
                    if (!File.Exists(relocated)) continue;
                    item.FilePath = relocated;
                }
                Items.Add(item);
            }
        }
        catch { }
    }

    public static void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(Items.ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>임시 PNG를 히스토리 폴더로 옮기고 목록 맨 앞에 추가한다.</summary>
    public static HistoryItem? Add(string tmpPng, int width, int height)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var item = new HistoryItem { Width = width, Height = height };
            item.FilePath = Path.Combine(Dir, $"{item.Id}.png");
            File.Copy(tmpPng, item.FilePath, overwrite: true);
            try { File.Delete(tmpPng); } catch { }

            Items.Insert(0, item);
            while (Items.Count > Limit)
            {
                var victim = Items[^1];
                Items.RemoveAt(Items.Count - 1);
                try { File.Delete(victim.FilePath); } catch { }
            }
            SaveIndex();
            return item;
        }
        catch { return null; }
    }

    /// <summary>편집 결과(PNG 바이트)로 항목 이미지를 교체한다.</summary>
    public static void UpdateImage(HistoryItem item, byte[] png, int width, int height)
    {
        try
        {
            File.WriteAllBytes(item.FilePath, png);
            item.Width = width;
            item.Height = height;
            item.InvalidateThumb();
            SaveIndex();
        }
        catch { }
    }

    public static void Delete(HistoryItem item)
    {
        Items.Remove(item);
        try { File.Delete(item.FilePath); } catch { }
        SaveIndex();
    }
}
