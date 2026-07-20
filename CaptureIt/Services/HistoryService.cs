using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace CaptureIt.Services;

/// <summary>캡처 목록의 한 항목. 원본 PNG는 히스토리 폴더에 보관된다.</summary>
public class HistoryItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => $"{Width}×{Height} · {CreatedAt:HH:mm}";

    private BitmapImage? _thumb;

    /// <summary>목록 표시용 축소 썸네일 (지연 로드).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumb != null) return _thumb;
            _thumb = LoadBitmap(decodeWidth: 220);
            return _thumb;
        }
    }

    /// <summary>편집용 원본 이미지 로드.</summary>
    public BitmapSource? LoadFull() => LoadBitmap(decodeWidth: 0);

    private BitmapImage? LoadBitmap(int decodeWidth)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(FilePath);
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.CacheOption = BitmapCacheOption.OnLoad;                  // 파일 잠금 방지
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;    // 편집 후 갱신 반영
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>이미지 파일이 바뀐 뒤 썸네일·캡션을 다시 그리게 한다.</summary>
    public void Invalidate()
    {
        _thumb = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
    }
}

/// <summary>
/// 캡처 목록(최근 캡처) 저장소. 알캡처의 '최근 캡처 목록'을 벤치마크.
/// 모든 캡처는 자동으로 여기에 쌓이고 언제든 다시 열어 편집할 수 있다.
/// </summary>
public static class HistoryService
{
    public const int MaxItems = 100;

    public static ObservableCollection<HistoryItem> Items { get; } = new();

    /// <summary>편집기에서 열어 둔 항목 — 100장 초과 정리에서 제외해 편집 중 작업을 보호한다.</summary>
    public static HistoryItem? Pinned { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CaptureIt", "History");

    private static string IndexPath => Path.Combine(Dir, "history.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(IndexPath)) return;
            var list = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(IndexPath), JsonOpts);
            if (list == null) return;
            Items.Clear();
            foreach (var item in list)
            {
                // 폴더 이전(EasyCapture → CaptureIt) 등으로 절대 경로가 바뀐 경우 파일명으로 복구
                if (!File.Exists(item.FilePath))
                {
                    var relocated = Path.Combine(Dir, Path.GetFileName(item.FilePath));
                    if (!File.Exists(relocated)) continue;
                    item.FilePath = relocated;
                }
                Items.Add(item);
            }
        }
        catch { /* 손상된 인덱스는 무시하고 빈 목록으로 시작 */ }
    }

    private static void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(Items.ToList(), JsonOpts));
        }
        catch { }
    }

    /// <summary>새 캡처를 목록 맨 앞에 추가한다 (원본 PNG를 히스토리 폴더에 저장).</summary>
    public static HistoryItem? Add(BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var item = new HistoryItem
            {
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                CreatedAt = DateTime.Now
            };
            item.FilePath = Path.Combine(Dir, $"{item.Id}.png");
            CaptureService.SaveToFile(image, item.FilePath);

            Items.Insert(0, item);
            TrimToLimit();
            SaveIndex();
            return item;
        }
        catch { return null; }
    }

    /// <summary>큰 이미지도 UI를 멈추지 않도록 PNG 인코딩을 백그라운드에서 수행하는 버전.</summary>
    public static async Task<HistoryItem?> AddAsync(BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var item = new HistoryItem
            {
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                CreatedAt = DateTime.Now
            };
            item.FilePath = Path.Combine(Dir, $"{item.Id}.png");
            await Task.Run(() => CaptureService.SaveToFile(image, item.FilePath));   // image는 Frozen

            Items.Insert(0, item);
            TrimToLimit();
            SaveIndex();
            return item;
        }
        catch { return null; }
    }

    /// <summary>편집 결과로 항목 이미지를 교체한다 (편집 내용이 목록에도 반영되도록).
    /// 교체 전 상태를 스냅샷으로 남겨 Ctrl+Z로 되돌릴 수 있다.</summary>
    public static void UpdateImage(HistoryItem item, BitmapSource image)
    {
        try
        {
            PushImageUndo(item);
            CaptureService.SaveToFile(image, item.FilePath);
            item.Width = image.PixelWidth;
            item.Height = image.PixelHeight;
            item.Invalidate();
            SaveIndex();
        }
        catch { }
    }

    // ── 이미지 단계 실행취소 (합성·자르기·저장으로 구워진 편집을 되돌리기, 세션 한정) ──
    private const int MaxImageUndoPerItem = 8;
    private static readonly Dictionary<string, LinkedList<byte[]>> _imgUndo = new();
    private static readonly Dictionary<string, Stack<byte[]>> _imgRedo = new();

    private static void PushImageUndo(HistoryItem item)
    {
        try
        {
            if (!File.Exists(item.FilePath)) return;
            var bytes = File.ReadAllBytes(item.FilePath);
            if (!_imgUndo.TryGetValue(item.Id, out var list)) _imgUndo[item.Id] = list = new();
            list.AddLast(bytes);
            // 아주 큰 이미지(스크롤 캡처 등)는 메모리 보호를 위해 얕게 유지
            int cap = bytes.Length > 40_000_000 ? 2 : MaxImageUndoPerItem;
            while (list.Count > cap) list.RemoveFirst();
            _imgRedo.Remove(item.Id);   // 새 편집이 쌓이면 '다시 실행' 이력은 무효
        }
        catch { }
    }

    /// <summary>새 주석이 추가되면 이미지 '다시 실행' 이력을 무효화한다 (표준 undo 규칙).</summary>
    public static void ClearImageRedo(HistoryItem item) => _imgRedo.Remove(item.Id);

    /// <summary>항목 이미지를 직전 편집 상태로 되돌린다. 성공 여부 반환.</summary>
    public static bool UndoImage(HistoryItem item)
    {
        if (!_imgUndo.TryGetValue(item.Id, out var list) || list.Count == 0) return false;
        try
        {
            var current = File.ReadAllBytes(item.FilePath);
            if (!_imgRedo.TryGetValue(item.Id, out var redo)) _imgRedo[item.Id] = redo = new();
            redo.Push(current);

            var bytes = list.Last!.Value;
            list.RemoveLast();
            File.WriteAllBytes(item.FilePath, bytes);
            RefreshDimensions(item, bytes);
            item.Invalidate();
            SaveIndex();
            return true;
        }
        catch { return false; }
    }

    /// <summary>되돌린 편집을 다시 적용한다. 성공 여부 반환.</summary>
    public static bool RedoImage(HistoryItem item)
    {
        if (!_imgRedo.TryGetValue(item.Id, out var redo) || redo.Count == 0) return false;
        try
        {
            var current = File.ReadAllBytes(item.FilePath);
            if (!_imgUndo.TryGetValue(item.Id, out var list)) _imgUndo[item.Id] = list = new();
            list.AddLast(current);

            var bytes = redo.Pop();
            File.WriteAllBytes(item.FilePath, bytes);
            RefreshDimensions(item, bytes);
            item.Invalidate();
            SaveIndex();
            return true;
        }
        catch { return false; }
    }

    private static void RefreshDimensions(HistoryItem item, byte[] png)
    {
        using var ms = new MemoryStream(png);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
        item.Width = frame.PixelWidth;
        item.Height = frame.PixelHeight;
    }

    private static void DropImageHistory(HistoryItem item)
    {
        _imgUndo.Remove(item.Id);
        _imgRedo.Remove(item.Id);
    }

    public static void Delete(HistoryItem item)
    {
        Items.Remove(item);
        DropImageHistory(item);
        try { if (File.Exists(item.FilePath)) File.Delete(item.FilePath); } catch { }
        SaveIndex();
    }

    public static void Clear()
    {
        foreach (var item in Items.ToList())
        {
            DropImageHistory(item);
            try { if (File.Exists(item.FilePath)) File.Delete(item.FilePath); } catch { }
        }
        Items.Clear();
        SaveIndex();
    }

    private static void TrimToLimit()
    {
        while (Items.Count > MaxItems)
        {
            // 편집 중(Pinned)인 항목은 건너뛰고 가장 오래된 것을 지운다
            var victim = Items.LastOrDefault(i => !ReferenceEquals(i, Pinned));
            if (victim == null) break;
            Items.Remove(victim);
            DropImageHistory(victim);
            try { if (File.Exists(victim.FilePath)) File.Delete(victim.FilePath); } catch { }
        }
    }
}
