using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace CaptureIt.Cross;

/// <summary>
/// 정지 화면 위에서 영역을 드래그로 고르는 오버레이 (Windows 테스트 경로에서 사용;
/// macOS/Linux는 OS 네이티브 영역 선택을 쓴다). 결과는 이미지 픽셀 좌표 Rect.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly Bitmap _frozen;
    private bool _dragging;
    private Point _start;
    private Rect _sel;
    private readonly TaskCompletionSource _closed = new();

    /// <summary>선택된 이미지 픽셀 영역 (취소 시 null).</summary>
    public Rect? Result { get; private set; }

    public OverlayWindow() : this(null!) { }   // XAML 미리보기용

    public OverlayWindow(Bitmap frozen)
    {
        InitializeComponent();
        _frozen = frozen;
        if (frozen != null) Frozen.Source = frozen;
        HintText.Text = Loc.Get("Overlay.Hint");

        // 정지 이미지는 '주 모니터' 캡처이므로 오버레이도 반드시 주 모니터에 전체 화면으로 연다.
        // (기본 FullScreen은 메인 창이 있는 모니터에 열려, 멀티 모니터에서 어긋났다)
        Opened += (_, _) =>
        {
            if (Screens.Primary is { } primary) Position = primary.Bounds.Position;
            WindowState = WindowState.FullScreen;
        };

        Loaded += (_, _) =>
        {
            Canvas.SetLeft(HintBar, (Bounds.Width - HintBar.Bounds.Width) / 2);
            Canvas.SetTop(HintBar, 28);
        };
        Closed += (_, _) => _closed.TrySetResult();

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public Task WaitClosedAsync() => _closed.Task;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed) { Close(); return; }
        _dragging = true;
        _start = pt.Position;
        HintBar.IsVisible = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(this);
        _sel = new Rect(
            Math.Min(_start.X, pos.X), Math.Min(_start.Y, pos.Y),
            Math.Abs(pos.X - _start.X), Math.Abs(pos.Y - _start.Y));
        SelRect.IsVisible = true;
        Canvas.SetLeft(SelRect, _sel.X);
        Canvas.SetTop(SelRect, _sel.Y);
        SelRect.Width = _sel.Width;
        SelRect.Height = _sel.Height;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_sel.Width < 3 || _sel.Height < 3) { Close(); return; }

        // 창 좌표 → 이미지 픽셀 (창이 이미지를 Fill로 덮으므로 비례 변환)
        double sx = _frozen.PixelSize.Width / Bounds.Width;
        double sy = _frozen.PixelSize.Height / Bounds.Height;
        Result = new Rect(
            Math.Clamp(_sel.X * sx, 0, _frozen.PixelSize.Width - 1),
            Math.Clamp(_sel.Y * sy, 0, _frozen.PixelSize.Height - 1),
            Math.Max(1, _sel.Width * sx),
            Math.Max(1, _sel.Height * sy));
        Close();
    }
}
