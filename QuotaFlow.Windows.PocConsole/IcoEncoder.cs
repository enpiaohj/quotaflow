using System.Drawing;
using System.Drawing.Imaging;

namespace QuotaFlow.Windows.PocConsole;

/// <summary>
/// 把一组不同尺寸的位图编码成标准 .ico 文件。每一帧用 PNG 压缩存储
/// （Windows Vista 及以上都支持 ICO 内嵌 PNG，比传统 BMP DIB 编码简单得多，
/// 而且 256×256 这种大尺寸用 BMP 编码体积会很夸张）。
/// </summary>
internal static class IcoEncoder
{
    public static void Save(string path, IReadOnlyList<Bitmap> images)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        var pngData = images.Select(img =>
        {
            using var ms = new MemoryStream();
            img.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        // ICONDIR
        writer.Write((short)0); // reserved
        writer.Write((short)1); // type = 1 (icon)
        writer.Write((short)images.Count);

        var offset = 6 + images.Count * 16; // header(6) + N * ICONDIRENTRY(16)

        // ICONDIRENTRY[]
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            var data = pngData[i];

            writer.Write((byte)(img.Width >= 256 ? 0 : img.Width));
            writer.Write((byte)(img.Height >= 256 ? 0 : img.Height));
            writer.Write((byte)0); // color palette count（真彩色填 0）
            writer.Write((byte)0); // reserved
            writer.Write((short)1); // color planes
            writer.Write((short)32); // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);

            offset += data.Length;
        }

        // 图像数据本体
        foreach (var data in pngData)
        {
            writer.Write(data);
        }
    }
}
