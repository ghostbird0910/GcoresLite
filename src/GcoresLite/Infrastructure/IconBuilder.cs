using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GcoresLite.Infrastructure;

/// <summary>
/// 从一张图片生成多尺寸 .ico。
/// 16~128 用 32bpp DIB（兼容性最好，System.Drawing.Icon 能直接读），
/// 256 用 PNG（Vista 以后的标准做法）。
/// </summary>
public static class IconBuilder
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void Build(string sourceImagePath, string outputIcoPath)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourceImagePath, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        var source = decoder.Frames[0];

        var entries = new List<(int Size, byte[] Data)>();

        foreach (var size in Sizes)
        {
            var scaled = Scale(source, size);
            var data = size >= 256 ? EncodePng(scaled) : EncodeDib(scaled, size);
            entries.Add((size, data));
        }

        WriteIco(outputIcoPath, entries);
    }

    private static BitmapSource Scale(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(source, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 生成圆角方形图标：外轮廓正方形，四角半径 R 的四分之一圆弧，四角外侧 alpha=0。
    /// 图片按原始像素 1:1 居中绘制（长边为正方形边长），不缩放、不裁切、不重绘，只施加 alpha 蒙版。
    /// </summary>
    public static void BuildRoundedIcon(
        string sourcePath,
        string outputPng,
        double radiusRatio = 0.225,
        double strokeRatio = 0,
        Color? strokeColor = null,
        double outerStrokeRatio = 0,
        Color? outerStrokeColor = null)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];

        // 原图不一定是正方形，取长边做画布，内容居中摆放，避免任何裁切或拉伸
        var side = Math.Max(frame.PixelWidth, frame.PixelHeight);
        var offsetX = (side - frame.PixelWidth) / 2.0;
        var offsetY = (side - frame.PixelHeight) / 2.0;
        var radius = side * radiusRatio;

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var geometry = new RectangleGeometry(new Rect(0, 0, side, side), radius, radius);

            var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, geometry))
            {
                Viewbox = new Rect(0, 0, side, side),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };

            dc.PushOpacityMask(mask);
            dc.DrawImage(frame, new Rect(offsetX, offsetY, frame.PixelWidth, frame.PixelHeight));

            // 内描边：外缘与图标边界重合，圆角与轮廓同心
            if (strokeRatio > 0 && strokeColor is { } color)
            {
                var stroke = side * strokeRatio;

                // 如果外面还有一圈黑边，绿边要整体往里让
                var inset = side * outerStrokeRatio + stroke / 2.0;

                var pen = new Pen(new SolidColorBrush(color), stroke)
                {
                    LineJoin = PenLineJoin.Round
                };

                var innerRadius = Math.Max(0, radius - inset);
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(inset, inset, side - inset * 2, side - inset * 2),
                    innerRadius, innerRadius);
            }

            // 最外层极细黑边：把绿色收在框内，压掉"太绿"的观感
            if (outerStrokeRatio > 0 && outerStrokeColor is { } outerColor)
            {
                var stroke = side * outerStrokeRatio;
                var inset = stroke / 2.0;

                var pen = new Pen(new SolidColorBrush(outerColor), stroke)
                {
                    LineJoin = PenLineJoin.Round
                };

                var innerRadius = Math.Max(0, radius - inset);
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(inset, inset, side - inset * 2, side - inset * 2),
                    innerRadius, innerRadius);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        SavePng(bitmap, outputPng);
    }

    /// <summary>
    /// 小尺寸简化版：只保留字形（如字母 G），放大居中，描边固定像素宽。
    /// 16/24px 塞不下完整 "G.FM"，缩下去会糊成一团，必须单独出图。
    /// </summary>
    private static BitmapSource RenderSimplifiedIcon(
        BitmapSource frame,
        Int32Rect glyphCrop,
        int targetSize,
        double radiusRatio,
        double strokeRatio,
        Color? strokeColor,
        double outerStrokeRatio,
        Color? outerStrokeColor)
    {
        var glyph = new CroppedBitmap(frame, glyphCrop);

        var side = targetSize;
        var radius = side * radiusRatio;

        // 字形占高度的 72%，四周留出呼吸空间
        var glyphHeight = side * 0.72;
        var glyphWidth = glyphHeight * glyph.PixelWidth / glyph.PixelHeight;
        var glyphX = (side - glyphWidth) / 2.0;
        var glyphY = (side - glyphHeight) / 2.0;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var dc = visual.RenderOpen())
        {
            var geometry = new RectangleGeometry(new Rect(0, 0, side, side), radius, radius);

            var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, geometry))
            {
                Viewbox = new Rect(0, 0, side, side),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };

            dc.PushOpacityMask(mask);

            // 底色取纯黑（原图底色），再压上放大的字形
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, side, side));
            dc.DrawImage(glyph, new Rect(glyphX, glyphY, glyphWidth, glyphHeight));

            // 小尺寸用固定像素描边，等比缩放会细到看不见
            if (strokeRatio > 0 && strokeColor is { } color && side >= 12)
            {
                var stroke = Math.Max(1.0, side * strokeRatio);
                var inset = stroke / 2.0;
                var innerRadius = Math.Max(0, radius - inset);

                dc.DrawRoundedRectangle(null,
                    new Pen(new SolidColorBrush(color), stroke) { LineJoin = PenLineJoin.Round },
                    new Rect(inset, inset, side - inset * 2, side - inset * 2),
                    innerRadius, innerRadius);
            }

            if (outerStrokeRatio > 0 && outerStrokeColor is { } outerColor && side >= 48)
            {
                var stroke = side * outerStrokeRatio;
                var inset = stroke / 2.0;
                var innerRadius = Math.Max(0, radius - inset);

                dc.DrawRoundedRectangle(null,
                    new Pen(new SolidColorBrush(outerColor), stroke) { LineJoin = PenLineJoin.Round },
                    new Rect(inset, inset, side - inset * 2, side - inset * 2),
                    innerRadius, innerRadius);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 生成多尺寸 ico。≤ simplifiedMaxSize 的尺寸用简化版（只留字形），其余用完整版。
    /// 16~128 走 32bpp DIB（System.Drawing.Icon 兼容性最好），256 走 PNG。
    /// </summary>
    public static void BuildIco(
        string sourcePath,
        string outputIco,
        double radiusRatio,
        double strokeRatio,
        Color strokeColor,
        double outerStrokeRatio,
        Color outerStrokeColor,
        Int32Rect? glyphCrop = null,
        int simplifiedMaxSize = 24)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var entries = new List<(int Size, byte[] Data)>();

        foreach (var size in sizes)
        {
            var useSimplified = glyphCrop is { } crop && size <= simplifiedMaxSize;

            var icon = useSimplified
                ? RenderSimplifiedIcon(frame, glyphCrop!.Value, size, radiusRatio, strokeRatio,
                    strokeColor, outerStrokeRatio, outerStrokeColor)
                : RenderRoundedIcon(frame, radiusRatio, strokeRatio, strokeColor,
                    outerStrokeRatio, outerStrokeColor, size);

            var data = size >= 256 ? EncodePng(icon) : EncodeDib(icon, size);
            entries.Add((size, data));
        }

        WriteIco(outputIco, entries);
    }

    /// <summary>按目标像素尺寸渲染圆角图标（矢量部分在目标尺寸下重新绘制，而不是缩放位图）。</summary>
    public static BitmapSource RenderRoundedIcon(
        BitmapSource frame,
        double radiusRatio,
        double strokeRatio,
        Color? strokeColor,
        double outerStrokeRatio,
        Color? outerStrokeColor,
        int targetSize)
    {
        var sourceSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
        var scale = (double)targetSize / sourceSide;

        var side = targetSize;
        var radius = side * radiusRatio;

        var drawWidth = frame.PixelWidth * scale;
        var drawHeight = frame.PixelHeight * scale;
        var offsetX = (side - drawWidth) / 2.0;
        var offsetY = (side - drawHeight) / 2.0;

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var geometry = new RectangleGeometry(new Rect(0, 0, side, side), radius, radius);

            var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, geometry))
            {
                Viewbox = new Rect(0, 0, side, side),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };

            dc.PushOpacityMask(mask);
            dc.DrawImage(frame, new Rect(offsetX, offsetY, drawWidth, drawHeight));

            if (strokeRatio > 0 && strokeColor is { } color)
            {
                var stroke = side * strokeRatio;
                var inset = side * outerStrokeRatio + stroke / 2.0;
                var innerRadius = Math.Max(0, radius - inset);

                dc.DrawRoundedRectangle(null,
                    new Pen(new SolidColorBrush(color), stroke) { LineJoin = PenLineJoin.Round },
                    new Rect(inset, inset, side - inset * 2, side - inset * 2),
                    innerRadius, innerRadius);
            }

            if (outerStrokeRatio > 0 && outerStrokeColor is { } outerColor)
            {
                var stroke = side * outerStrokeRatio;
                var inset = stroke / 2.0;
                var innerRadius = Math.Max(0, radius - inset);

                dc.DrawRoundedRectangle(null,
                    new Pen(new SolidColorBrush(outerColor), stroke) { LineJoin = PenLineJoin.Round },
                    new Rect(inset, inset, side - inset * 2, side - inset * 2),
                    innerRadius, innerRadius);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 拼一张真实尺寸对照图：深色/浅色背景各一行，尺寸 16~128 按 1:1 摆放。
    /// 图标最终就是在这个尺度下被看到的，大图容易骗眼睛。
    /// </summary>
    public static void BuildSizeComparison(
        string sourcePath,
        string outputPng,
        double radiusRatio,
        double strokeRatio,
        Color strokeColor,
        double outerStrokeRatio,
        Color outerStrokeColor,
        Int32Rect? glyphCrop = null,
        int simplifiedMaxSize = 24)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];

        var sizes = new[] { 16, 24, 32, 48, 64, 128 };
        const int slot = 140;
        const int labelHeight = 26;
        const int pad = 20;
        var rowHeight = 128 + labelHeight + 16;

        var width = pad * 2 + slot * sizes.Length;
        var height = pad * 2 + rowHeight * 2;

        var visuals = new Dictionary<int, BitmapSource>();
        foreach (var size in sizes)
        {
            visuals[size] = glyphCrop is { } crop && size <= simplifiedMaxSize
                ? RenderSimplifiedIcon(frame, crop, size, radiusRatio, strokeRatio,
                    strokeColor, outerStrokeRatio, outerStrokeColor)
                : RenderRoundedIcon(frame, radiusRatio, strokeRatio, strokeColor,
                    outerStrokeRatio, outerStrokeColor, size);
        }

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var dark = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            var light = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));

            dc.DrawRectangle(dark, null, new Rect(0, 0, width, pad + rowHeight));
            dc.DrawRectangle(light, null, new Rect(0, pad + rowHeight, width, rowHeight + pad));

            var typeface = new Typeface("Segoe UI");

            for (var row = 0; row < 2; row++)
            {
                var rowTop = row == 0 ? pad : pad + rowHeight;
                var labelBrush = row == 0
                    ? new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
                    : new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));

                for (var i = 0; i < sizes.Length; i++)
                {
                    var size = sizes[i];
                    var slotLeft = pad + i * slot;

                    // 底部对齐，便于比较同一条基线上的视觉重量
                    var x = slotLeft + (slot - size) / 2.0;
                    var y = rowTop + 128 - size;

                    dc.DrawImage(visuals[size], new Rect(x, y, size, size));

                    var text = new FormattedText(
                        $"{size}px", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 12, labelBrush, 1.0);

                    dc.DrawText(text, new Point(slotLeft + (slot - text.Width) / 2.0, rowTop + 128 + 6));
                }
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        SavePng(bitmap, outputPng);
    }

    /// <summary>把透明图标贴到浅色背景上，方便肉眼确认圆角（黑底图标在深色背景下看不出轮廓）。</summary>
    public static void BuildPreview(string roundedPng, string previewPng, int pad = 40)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(roundedPng, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];

        var width = frame.PixelWidth + pad * 2;
        var height = frame.PixelHeight + pad * 2;

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromRgb(0xF2, 0xEF, 0xEC));
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawImage(frame, new Rect(pad, pad, frame.PixelWidth, frame.PixelHeight));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        SavePng(bitmap, previewPng);
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] EncodeDib(BitmapSource source, int size)
    {
        var stride = size * 4;
        var pixels = new byte[stride * size];
        source.CopyPixels(pixels, stride, 0);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // BITMAPINFOHEADER
        writer.Write(40);              // biSize
        writer.Write(size);            // biWidth
        writer.Write(size * 2);        // biHeight（XOR + AND 两段）
        writer.Write((ushort)1);       // biPlanes
        writer.Write((ushort)32);      // biBitCount
        writer.Write(0);               // biCompression
        writer.Write(pixels.Length);   // biSizeImage
        writer.Write(0);               // biXPelsPerMeter
        writer.Write(0);               // biYPelsPerMeter
        writer.Write(0);               // biClrUsed
        writer.Write(0);               // biClrImportant

        // DIB 像素自下而上
        for (var y = size - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, stride);

        // AND 掩码全 0（全部不透明）；32bpp 的 alpha 已足够，这里只是占位
        var maskStride = (size + 31) / 32 * 4;
        writer.Write(new byte[maskStride * size]);
        writer.Flush();

        return stream.ToArray();
    }

    private static void WriteIco(string path, List<(int Size, byte[] Data)> entries)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                  // reserved
        writer.Write((ushort)1);                  // type = icon
        writer.Write((ushort)entries.Count);

        var offset = 6 + 16 * entries.Count;

        foreach (var (size, data) in entries)
        {
            var dim = size >= 256 ? 0 : size;     // 256 在目录项里记 0
            writer.Write((byte)dim);              // width
            writer.Write((byte)dim);              // height
            writer.Write((byte)0);                // colorCount
            writer.Write((byte)0);                // reserved
            writer.Write((ushort)1);              // planes
            writer.Write((ushort)32);             // bitCount
            writer.Write((uint)data.Length);
            writer.Write((uint)offset);
            offset += data.Length;
        }

        foreach (var (_, data) in entries)
            writer.Write(data);
    }
}
