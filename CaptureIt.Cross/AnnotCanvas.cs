using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CaptureIt.Cross;

// ── 주석 모델: UI 렌더와 PNG 내보내기가 같은 데이터를 그린다 ────────────────
public abstract record Ann(Color Color, double Thickness);
public record PenAnn(Color Color, double Thickness, List<Point> Points) : Ann(Color, Thickness);
public record BoxAnn(Color Color, double Thickness, Rect Rect) : Ann(Color, Thickness);
public record ArrowAnn(Color Color, double Thickness, Point From, Point To) : Ann(Color, Thickness);

/// <summary>
/// 이미지 + 주석을 픽셀 1:1로 그리는 커스텀 컨트롤.
/// 크기는 항상 이미지 픽셀 크기와 같게 두고, 부모에서 ScaleTransform으로 줌한다.
/// RenderTargetBitmap으로 그대로 렌더링하면 내보내기 결과가 화면과 일치한다.
/// </summary>
public class AnnotCanvas : Control
{
    public Bitmap? Image;
    public readonly List<Ann> Annotations = new();
    public Ann? Preview;          // 드래그 중인 도형
    public Rect? CropPreview;     // 자르기 선택 표시

    public void SetImage(Bitmap? bmp)
    {
        Image = bmp;
        Annotations.Clear();
        Preview = null;
        CropPreview = null;
        Width = bmp?.PixelSize.Width ?? 0;
        Height = bmp?.PixelSize.Height ?? 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (Image == null) return;
        var full = new Rect(0, 0, Width, Height);
        ctx.DrawImage(Image, new Rect(Image.Size), full);

        foreach (var ann in Annotations) Draw(ctx, ann);
        if (Preview != null) Draw(ctx, Preview);

        if (CropPreview is { } crop)
        {
            var dim = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
            // 선택 영역 밖을 어둡게
            ctx.FillRectangle(dim, new Rect(0, 0, Width, crop.Y));
            ctx.FillRectangle(dim, new Rect(0, crop.Bottom, Width, Math.Max(0, Height - crop.Bottom)));
            ctx.FillRectangle(dim, new Rect(0, crop.Y, crop.X, crop.Height));
            ctx.FillRectangle(dim, new Rect(crop.Right, crop.Y, Math.Max(0, Width - crop.Right), crop.Height));
            ctx.DrawRectangle(new Pen(Brushes.White, 2, dashStyle: new DashStyle(new double[] { 4, 3 }, 0)), crop);
        }
    }

    private static void Draw(DrawingContext ctx, Ann ann)
    {
        var brush = new SolidColorBrush(ann.Color);
        var pen = new Pen(brush, ann.Thickness) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        switch (ann)
        {
            case PenAnn p when p.Points.Count > 1:
            {
                var geo = new PolylineGeometry(p.Points, false);
                ctx.DrawGeometry(null, pen, geo);
                break;
            }
            case BoxAnn b:
                ctx.DrawRectangle(null, pen, b.Rect, 2, 2);
                break;

            case ArrowAnn a:
            {
                ctx.DrawLine(pen, a.From, ArrowBase(a));
                ctx.DrawGeometry(brush, null, ArrowHead(a));
                break;
            }
        }
    }

    private static Point ArrowBase(ArrowAnn a)
    {
        var v = a.To - a.From;
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1) return a.To;
        var head = Math.Max(10, a.Thickness * 3.2);
        return a.To - v * (head / len);
    }

    private static Geometry ArrowHead(ArrowAnn a)
    {
        var v = a.To - a.From;
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        var geo = new StreamGeometry();
        if (len < 1) return geo;
        var dir = v / len;
        var perp = new Vector(-dir.Y, dir.X);
        var headLen = Math.Max(10, a.Thickness * 3.2);
        var headW = headLen * 0.75;
        var basePt = a.To - dir * headLen;
        using var g = geo.Open();
        g.BeginFigure(a.To, isFilled: true);
        g.LineTo(basePt + perp * (headW / 2));
        g.LineTo(basePt - perp * (headW / 2));
        g.EndFigure(true);
        return geo;
    }

    /// <summary>현재 화면(이미지+주석)을 PNG 바이트로 내보낸다. cropRect가 있으면 그 부분만.</summary>
    public byte[]? ExportPng(Rect? cropRect = null)
    {
        if (Image == null) return null;
        int w = Image.PixelSize.Width, h = Image.PixelSize.Height;

        // 자르기/미리보기 표시는 내보내기에서 제외
        var savedCrop = CropPreview;
        var savedPreview = Preview;
        CropPreview = null;
        Preview = null;

        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            Measure(new Size(w, h));
            Arrange(new Rect(0, 0, w, h));
            rtb.Render(this);

            if (cropRect is { } c)
            {
                int cx = Math.Clamp((int)c.X, 0, w - 1);
                int cy = Math.Clamp((int)c.Y, 0, h - 1);
                int cw = Math.Clamp((int)c.Width, 1, w - cx);
                int ch = Math.Clamp((int)c.Height, 1, h - cy);

                var cropped = new RenderTargetBitmap(new PixelSize(cw, ch), new Vector(96, 96));
                using (var ctx2 = cropped.CreateDrawingContext())
                    ctx2.DrawImage(rtb, new Rect(cx, cy, cw, ch), new Rect(0, 0, cw, ch));
                using var ms2 = new MemoryStream();
                cropped.Save(ms2);
                return ms2.ToArray();
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
        finally
        {
            CropPreview = savedCrop;
            Preview = savedPreview;
            InvalidateVisual();
        }
    }
}
