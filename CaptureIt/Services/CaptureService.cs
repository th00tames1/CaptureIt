using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CaptureIt.Services;

/// <summary>화면 캡처와 이미지 변환·저장을 담당한다. 좌표는 모두 물리 픽셀 기준.</summary>
public static class CaptureService
{
    // ── Win32 ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public record WindowInfo(IntPtr Handle, string Title, Rectangle Bounds);

    // ── 화면 캡처 ───────────────────────────────────────────────────────────
    /// <summary>가상 화면(모든 모니터) 전체를 물리 픽셀로 캡처한다.</summary>
    public static Bitmap CaptureVirtualScreen()
    {
        var b = System.Windows.Forms.SystemInformation.VirtualScreen;
        return CaptureRect(new Rectangle(b.X, b.Y, b.Width, b.Height));
    }

    /// <summary>기본(주) 모니터만 캡처한다.</summary>
    public static Bitmap CapturePrimaryScreen()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return CaptureRect(b);
    }

    /// <summary>물리 픽셀 사각형 영역을 캡처한다.</summary>
    public static Bitmap CaptureRect(Rectangle rect)
    {
        var bmp = new Bitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.X, rect.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>현재 포커스된 창을 캡처한다 (그림자 제외한 실제 프레임).</summary>
    public static Bitmap? CaptureForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var rect = GetWindowBounds(hwnd);
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        return CaptureRect(rect);
    }

    /// <summary>DWM 확장 프레임 경계(그림자 제외)를 얻는다.</summary>
    public static Rectangle GetWindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>화면에 보이는 최상위 창 목록(Z순서, 위가 먼저). 창 캡처 하이라이트용.</summary>
    public static List<WindowInfo> GetVisibleWindows(IntPtr[] excludeHandles)
    {
        var list = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (excludeHandles.Contains(hwnd)) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var bounds = GetWindowBounds(hwnd);
            if (bounds.Width < 20 || bounds.Height < 20) return true;
            list.Add(new WindowInfo(hwnd, sb.ToString(), bounds));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ── 변환/저장 ───────────────────────────────────────────────────────────
    /// <summary>GDI Bitmap → WPF BitmapSource (메모리 안전 변환).</summary>
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }

    public static void SaveToFile(BitmapSource image, string path, int jpgQuality = 90)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = jpgQuality },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
    }

    /// <summary>
    /// 어디에나 붙여넣을 수 있도록 여러 포맷을 함께 올린다:
    /// DIB(일반 앱) + PNG 스트림(브라우저·채팅 앱) + 파일 드롭(탐색기·메신저 파일 첨부).
    /// 다른 앱이 클립보드를 잡고 있으면 잠시 기다렸다 재시도한다. 성공 여부를 반환.
    /// </summary>
    public static bool CopyToClipboard(BitmapSource image, string? filePath = null)
    {
        var formats = EncodeClipboardFormats(image);
        return formats != null && SetClipboardData(formats.Value.png, formats.Value.dib, filePath);
    }

    /// <summary>큰 이미지(스크롤 캡처 등)도 UI를 멈추지 않도록 인코딩을 백그라운드에서 수행.</summary>
    public static async Task<bool> CopyToClipboardAsync(BitmapSource image, string? filePath = null)
    {
        var formats = await Task.Run(() => EncodeClipboardFormats(image));   // image는 Frozen
        return formats != null && SetClipboardData(formats.Value.png, formats.Value.dib, filePath);
    }

    private static (byte[] png, byte[] dib)? EncodeClipboardFormats(BitmapSource image)
    {
        try
        {
            byte[] png;
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(ms);
                png = ms.ToArray();
            }
            return (png, EncodeDib(image));
        }
        catch (OutOfMemoryException) { return null; }
    }

    /// <summary>CF_DIB (BITMAPINFOHEADER + 하단부터의 BGRA 행) 바이트를 만든다.</summary>
    private static byte[] EncodeDib(BitmapSource image)
    {
        var src = image.Format == System.Windows.Media.PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        src.CopyPixels(pixels, stride, 0);

        var dib = new byte[40 + pixels.Length];
        using (var bw = new BinaryWriter(new MemoryStream(dib)))
        {
            bw.Write(40); bw.Write(w); bw.Write(h);           // biSize, biWidth, biHeight(+ = bottom-up)
            bw.Write((short)1); bw.Write((short)32);          // planes, bpp
            bw.Write(0); bw.Write(pixels.Length);             // BI_RGB, size
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        }
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(pixels, (h - 1 - y) * stride, dib, 40 + y * stride, stride);
        return dib;
    }

    private static bool SetClipboardData(byte[] png, byte[] dib, string? filePath)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = new System.Windows.DataObject();
                data.SetData(System.Windows.DataFormats.Dib, new MemoryStream(dib), false);
                data.SetData("PNG", new MemoryStream(png), false);
                if (filePath != null && File.Exists(filePath))
                    data.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath });

                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
            {
                if (attempt < 2) System.Threading.Thread.Sleep(150);
            }
        }
        return false;
    }

    // ── 스크롤/요소 캡처 보조 ──────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint GA_ROOT = 2;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    /// <summary>물리 좌표의 최상위 창 핸들.</summary>
    public static IntPtr GetRootWindowAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        return h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GA_ROOT);
    }

    /// <summary>커서를 옮기고 휠을 아래로 굴린다 (스크롤 캡처용).</summary>
    public static void WheelDownAt(int x, int y, int notches = 3)
    {
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(-120 * notches)), UIntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    public static void GetCursorPosition(out System.Drawing.Point p)
    {
        GetCursorPos(out var pt);
        p = new System.Drawing.Point(pt.X, pt.Y);
    }

    public static void SetCursorPosition(int x, int y) => SetCursorPos(x, y);

    public static bool IsEscapePressed() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;

    /// <summary>캡처 효과음 재생.</summary>
    public static void PlayShutterSound()
    {
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
    }
}
