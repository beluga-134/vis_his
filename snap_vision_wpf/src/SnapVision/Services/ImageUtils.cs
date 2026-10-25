// 图片编码工具（对应 Tauri capture.rs / Dart image_utils.dart）：
// RGBA→PNG、缩略图（长边≤480）、dataURL（4.5MiB base64 上限）。
// 重活在 Task 线程跑，位图 Freeze 后跨线程安全。

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapVision.Services;

public sealed class EncodedCapture
{
    public EncodedCapture(byte[] png, byte[] thumbPng)
    {
        Png = png;
        ThumbPng = thumbPng;
    }

    /// <summary>存盘用 PNG（已做 4.5MiB 体积限制）。</summary>
    public byte[] Png { get; }

    /// <summary>气泡缩略图 PNG（长边 ≤480）。</summary>
    public byte[] ThumbPng { get; }
}

public static class ImageUtils
{
    /// <summary>base64 编码后体积上限（与 glm-vision 扩展的 4.5MiB 约定一致）。</summary>
    public const long MaxEncodedBytes = 4718592;

    private static BitmapSource FromRgba(byte[] rgba, int width, int height)
    {
        // WPF 无 Rgba32 格式（只有 Bgra32）：原地交换 R/B 通道
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource ResizeLongEdge(BitmapSource image, int maxEdge)
    {
        var longEdge = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longEdge <= maxEdge) return image;
        var scale = (double)maxEdge / longEdge;
        var nw = Math.Max(1, (int)Math.Round(image.PixelWidth * scale));
        var nh = Math.Max(1, (int)Math.Round(image.PixelHeight * scale));
        var resized = new TransformedBitmap(image, new ScaleTransform(
            (double)nw / image.PixelWidth, (double)nh / image.PixelHeight));
        resized.Freeze();
        return resized;
    }

    private static byte[] EncodeUnderLimit(BitmapSource image)
    {
        var png = EncodePng(image);
        if (png.LongLength * 4 / 3 > MaxEncodedBytes)
        {
            png = EncodePng(ResizeLongEdge(image, 1600));
        }
        return png;
    }

    /// <summary>原生裁剪结果（RGBA）→ PNG + 缩略图。</summary>
    public static Task<EncodedCapture> EncodeCaptureAsync(byte[] rgba, int width, int height)
        => Task.Run(() =>
        {
            var image = FromRgba(rgba, width, height);
            var png = EncodeUnderLimit(image);
            var thumb = EncodePng(ResizeLongEdge(image, 480));
            return new EncodedCapture(png, thumb);
        });

    /// <summary>
    /// 读取图片文件并转成 data URL；超过体积限制时先等比缩小。
    /// maxEdge 为最长边上限（送模型 1600）。
    /// </summary>
    public static Task<string> FileToDataUrlAsync(string path, int maxEdge)
        => Task.Run(() =>
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            var image = ResizeLongEdge(frame, maxEdge);
            var png = EncodeUnderLimit(image);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        });
}
