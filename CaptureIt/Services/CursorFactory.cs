using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace CaptureIt.Services;

/// <summary>
/// 도구 모양 커서(펜·마커 등)를 런타임에 생성한다.
/// 툴바 아이콘과 같은 Path geometry를 그대로 렌더링해 일관된 모양을 유지한다.
/// </summary>
public static class CursorFactory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private sealed class CursorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CursorHandle(IntPtr h) : base(true) => SetHandle(h);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    private static readonly Dictionary<string, Cursor> _cache = new();

    /// <summary>
    /// Path geometry로 커서를 만든다. 색이 있는 글리프에 흰 테두리를 둘러
    /// 어두운 배경에서도 잘 보이게 한다. hotspot은 글리프 좌표(size 기준).
    /// </summary>
    public static Cursor FromGeometry(string pathData, Color color, int size, int hotX, int hotY)
    {
        string key = $"{pathData}|{color}|{size}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var geometry = Geometry.Parse(pathData);

            // 글리프를 커서 크기에 맞게 스케일 (원본 좌표계는 아이콘과 같은 ~18px)
            var bounds = geometry.Bounds;
            double scale = (size - 4) / Math.Max(bounds.Width, bounds.Height);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(2 - bounds.X * scale, 2 - bounds.Y * scale));
                dc.PushTransform(new ScaleTransform(scale, scale));
                // 흰 외곽선(할로) → 본체 순서로 그린다
                dc.DrawGeometry(null, new Pen(Brushes.White, 3.6 / scale)
                { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.6 / scale)
                { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
                dc.Pop();
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            using var bmp = ToGdiBitmap(rtb);
            var iconInfo = new ICONINFO
            {
                fIcon = false,   // false = 커서 (핫스팟 사용)
                xHotspot = hotX,
                yHotspot = hotY,
                hbmMask = new System.Drawing.Bitmap(size, size).GetHbitmap(),
                hbmColor = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0))
            };
            var hCursor = CreateIconIndirect(ref iconInfo);
            DeleteObject(iconInfo.hbmMask);
            DeleteObject(iconInfo.hbmColor);
            if (hCursor == IntPtr.Zero) return Cursors.Cross;

            var cursor = CursorInteropHelper.Create(new CursorHandle(hCursor));
            _cache[key] = cursor;
            return cursor;
        }
        catch
        {
            return Cursors.Cross;   // 어떤 실패에도 기본 커서로 안전하게
        }
    }

    private static System.Drawing.Bitmap ToGdiBitmap(BitmapSource src)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return new System.Drawing.Bitmap(ms);
    }
}
