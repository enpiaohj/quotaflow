using System.Drawing;
using System.Drawing.Drawing2D;

namespace QuotaFlow.Windows.Core.Assets;

/// <summary>
/// QuotaFlow 图标的统一绘制逻辑：圆角渐变背景 + 一个"额度环"（部分填充的圆环）。
/// 直接把产品定位——"一眼看清额度剩余"——画进图标本身，而不是随便放一个字母。
/// 托盘运行时图标（<c>TrayIconFactory</c>）和静态 .ico 文件生成工具共用这一份绘制代码，
/// 保证任务栏、托盘、文件图标看起来是同一个东西。
/// </summary>
public static class AppIconRenderer
{
    private static readonly Color BackgroundStart = Color.FromArgb(255, 91, 76, 230);
    private static readonly Color BackgroundEnd = Color.FromArgb(255, 138, 92, 232);

    /// <param name="size">正方形边长（像素）。</param>
    /// <param name="ringFraction">额度环的填充比例 0..1，用于表达"品牌形象"，不代表真实额度。</param>
    /// <param name="badgeColor">右下角状态徽标颜色；null 表示不画徽标（用于任务栏/文件图标等场景）。</param>
    public static Bitmap Render(int size, double ringFraction = 0.75, Color? badgeColor = null)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        var full = new RectangleF(0, 0, size, size);
        var inset = size * 0.03f;
        var bgRect = RectangleF.Inflate(full, -inset, -inset);

        using (var bgBrush = new LinearGradientBrush(bgRect, BackgroundStart, BackgroundEnd, 45f))
        using (var bgPath = RoundedRect(bgRect, size * 0.28f))
        {
            g.FillPath(bgBrush, bgPath);
        }

        // 额度环：半透明白色轨道 + 纯白填充弧，起点在正上方、顺时针方向，视觉上就是个"仪表盘"。
        var ringInset = size * 0.26f;
        var ringRect = RectangleF.Inflate(full, -ringInset, -ringInset);
        var strokeWidth = Math.Max(1.6f, size * 0.115f);

        using (var trackPen = new Pen(Color.FromArgb(70, 255, 255, 255), strokeWidth)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(trackPen, ringRect, 0, 360);
        }

        using (var fillPen = new Pen(Color.White, strokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var sweep = (float)(360.0 * Math.Clamp(ringFraction, 0.02, 1.0));
            g.DrawArc(fillPen, ringRect, -90, sweep);
        }

        if (badgeColor is { } badge)
        {
            var badgeSize = size * 0.34f;
            var badgeRect = new RectangleF(size - badgeSize - inset * 0.3f, size - badgeSize - inset * 0.3f, badgeSize, badgeSize);
            using var badgeBrush = new SolidBrush(badge);
            using var ringPen = new Pen(Color.White, Math.Max(1.2f, size * 0.02f));
            g.FillEllipse(badgeBrush, badgeRect);
            g.DrawEllipse(ringPen, badgeRect);
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
