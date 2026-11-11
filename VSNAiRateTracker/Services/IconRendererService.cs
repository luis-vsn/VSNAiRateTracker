using System;
using System.IO;
using Avalonia.Controls;
using SkiaSharp;

namespace VSNAiRateTracker.Services;

public interface IIconRendererService
{
    SKColor GetStatusColor(double deltaHours);
    SKBitmap RenderBadgeBitmap(double deltaHours, string text, int size = 64);
    SKBitmap RenderBadgeBitmap(string text, string? colorHex = null, int size = 64);
    byte[] RenderBadgePngBytes(double deltaHours, string text, int size = 64);
    byte[] RenderBadgePngBytes(string text, string? colorHex = null, int size = 64);
    WindowIcon RenderBadgeWindowIcon(double deltaHours, string text, int size = 64);
    WindowIcon RenderBadgeWindowIcon(string text, string? colorHex = null, int size = 64);

    SKBitmap RenderAlertBitmap(bool isCritical = false, int size = 64);
    byte[] RenderAlertPngBytes(bool isCritical = false, int size = 64);
    WindowIcon RenderAlertWindowIcon(bool isCritical = false, int size = 64);

    // Tray-specific icons: multi-resolution .ico on Windows (one natively rendered frame per DPI scale)
    byte[] RenderBadgeIcoBytes(string text, string? colorHex = null);
    byte[] RenderAlertIcoBytes(bool isCritical = false);
    WindowIcon RenderTrayBadgeIcon(string text, string? colorHex = null);
    WindowIcon RenderTrayBadgeIcon(double deltaHours, string text);
    WindowIcon RenderTrayAlertIcon(bool isCritical = false);

    SKBitmap RenderAppIconBitmap(int size = 64);
    byte[] RenderAppIconPngBytes(int size = 64);
    WindowIcon RenderAppIconWindowIcon(int size = 64);
}

public class IconRendererService : IIconRendererService
{
    public static readonly SKColor ColorGreen = SKColor.Parse("#2ECC71");
    public static readonly SKColor ColorAmber = SKColor.Parse("#F39C12");
    public static readonly SKColor ColorRed = SKColor.Parse("#E74C3C");
    public static readonly SKColor ColorBlue = SKColor.Parse("#3498DB");

    private const double Epsilon = 1e-6;

    public SKColor GetStatusColor(double deltaHours)
    {
        if (Math.Abs(deltaHours) <= 0.5 + Epsilon)
            return ColorGreen;
        if (deltaHours > 0.5 && deltaHours <= 5.0 + Epsilon)
            return ColorAmber;
        if (deltaHours > 5.0)
            return ColorRed;
        return ColorBlue;
    }

    public SKBitmap RenderBadgeBitmap(double deltaHours, string text, int size = 64)
    {
        var color = GetStatusColor(deltaHours);
        return RenderBadgeBitmap(text, color.ToString(), size);
    }

    /// <summary>
    /// Renders the tray badge text in the style of LibreHardwareMonitor's tray icons: flat colour, no outline and no
    /// horizontal squeeze, with fully hinted glyphs at a whole-pixel font size placed on whole pixels, so stems land on
    /// the pixel grid and stay crisp at 16-24 px. The font is the largest whole-pixel size whose ink fits the full icon
    /// width and the icon height minus a 1 px margin at the top and bottom.
    /// </summary>
    public SKBitmap RenderBadgeBitmap(string text, string? colorHex = null, int size = 64)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        SKColor color = ColorGreen;
        if (!string.IsNullOrWhiteSpace(colorHex) && SKColor.TryParse(colorHex, out var parsedColor))
        {
            color = parsedColor;
        }

        var displayText = string.IsNullOrWhiteSpace(text) ? "0.0" : text.Trim();

        using var typeface = ResolveBadgeTypeface();
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // Full width (three-character values are width-limited, every pixel counts at 16 px);
        // 1 px margin at the top and bottom
        const int marginX = 0, marginY = 1;
        int maxW = size - (2 * marginX);
        int maxH = size - (2 * marginY);

        int fontPx = FindLargestFittingFontSize(typeface, displayText, maxW, maxH, paint);
        using var font = CreateHintedFont(typeface, fontPx);
        font.MeasureText(displayText, out var bounds, paint);

        // Centre the ink box, snapped to whole pixels, and keep it inside the canvas
        float x = MathF.Round(((size - bounds.Width) / 2f) - bounds.Left);
        float y = MathF.Round(((size - bounds.Height) / 2f) - bounds.Top);
        x = Math.Clamp(x, marginX - bounds.Left, Math.Max(marginX - bounds.Left, size - marginX - bounds.Right));
        y = Math.Clamp(y, marginY - bounds.Top, Math.Max(marginY - bounds.Top, size - marginY - bounds.Bottom));

        canvas.DrawText(displayText, x, y, SKTextAlign.Left, font, paint);
        return bitmap;
    }

    private static SKFont CreateHintedFont(SKTypeface typeface, int sizePx) => new(typeface, sizePx)
    {
        Hinting = SKFontHinting.Full,
        Edging = SKFontEdging.Antialias,
        Subpixel = false
    };

    /// <summary>Largest whole-pixel font size whose ink bounds fit in <paramref name="maxW"/> x <paramref name="maxH"/>.</summary>
    private static int FindLargestFittingFontSize(SKTypeface typeface, string text, int maxW, int maxH, SKPaint paint)
    {
        const int minPx = 4;
        int lo = minPx, hi = Math.Max(minPx, maxH * 2);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            using var probe = CreateHintedFont(typeface, mid);
            probe.MeasureText(text, out var b, paint);
            if (b.Width <= maxW && b.Height <= maxH)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    // DIN-1451 style faces first for high-legibility dial digits: Bahnschrift (Windows), DIN Alternate (macOS)
    private static readonly (string family, SKFontStyleWeight weight, SKFontStyleWidth width)[] BadgeTypefaceCandidates =
    [
        ("Bahnschrift", SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed),
        ("DIN Alternate", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal),
        ("Segoe UI Variable Display", SKFontStyleWeight.Black, SKFontStyleWidth.Condensed),
        ("Segoe UI", SKFontStyleWeight.Black, SKFontStyleWidth.Condensed),
        ("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal)
    ];

    /// <summary>
    /// Returns the first installed candidate face. FromFamilyName never returns null on macOS
    /// (CoreText substitutes Helvetica), so the resolved family name is checked explicitly.
    /// </summary>
    private static SKTypeface ResolveBadgeTypeface()
    {
        foreach (var (family, weight, width) in BadgeTypefaceCandidates)
        {
            var typeface = SKTypeface.FromFamilyName(family, weight, width, SKFontStyleSlant.Upright);
            if (typeface != null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return typeface;
            }
            typeface?.Dispose();
        }

        return SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.Default;
    }

    // ------------------------------------------------------------------------------------------------
    // Tray icons
    //
    // Avalonia's Win32 backend asks the icon for its "small" variant at 16 px x taskbar DPI scale. Given
    // a single PNG it rescales that bitmap (64 -> 16 px), which is what blurred the figure. Given an ICO
    // it picks the frame of the matching size, so we ship one frame per common DPI scale, each rendered
    // natively on its own pixel grid.
    // ------------------------------------------------------------------------------------------------

    /// <summary>Frame sizes for 100/125/150/175/200/250/300/400 % scaling.</summary>
    public static readonly int[] TrayIconSizes = { 16, 20, 24, 28, 32, 40, 48, 64 };

    public byte[] RenderBadgeIcoBytes(string text, string? colorHex = null) =>
        BuildIco(size => RenderBadgeBitmap(text, colorHex, size));

    public byte[] RenderAlertIcoBytes(bool isCritical = false) =>
        BuildIco(size => RenderAlertBitmap(isCritical, size));

    public WindowIcon RenderTrayBadgeIcon(string text, string? colorHex = null)
    {
        var bytes = OperatingSystem.IsWindows()
            ? RenderBadgeIcoBytes(text, colorHex)
            : RenderBadgePngBytes(text, colorHex, 64);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }

    public WindowIcon RenderTrayBadgeIcon(double deltaHours, string text) =>
        RenderTrayBadgeIcon(text, GetStatusColor(deltaHours).ToString());

    public WindowIcon RenderTrayAlertIcon(bool isCritical = false)
    {
        var bytes = OperatingSystem.IsWindows()
            ? RenderAlertIcoBytes(isCritical)
            : RenderAlertPngBytes(isCritical, 64);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }

    /// <summary>
    /// Builds a multi-resolution .ico with classic 32-bit DIB frames (XOR bitmap + 1-bit AND mask).
    /// </summary>
    private static byte[] BuildIco(Func<int, SKBitmap> renderFrame)
    {
        var frames = new byte[TrayIconSizes.Length][];
        for (int i = 0; i < TrayIconSizes.Length; i++)
        {
            using var bitmap = renderFrame(TrayIconSizes[i]);
            frames[i] = EncodeIcoFrame(bitmap);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                 // reserved
        writer.Write((ushort)1);                 // type: icon
        writer.Write((ushort)frames.Length);

        int offset = 6 + (16 * frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            int size = TrayIconSizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));   // width
            writer.Write((byte)(size >= 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // palette colours
            writer.Write((byte)0);                          // reserved
            writer.Write((ushort)1);                        // planes
            writer.Write((ushort)32);                       // bits per pixel
            writer.Write((uint)frames[i].Length);
            writer.Write((uint)offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeIcoFrame(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int maskStride = ((width + 31) / 32) * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // BITMAPINFOHEADER (height counts the XOR bitmap plus the AND mask)
        writer.Write((uint)40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)0);                                          // BI_RGB
        writer.Write((uint)((width * height * 4) + (maskStride * height)));
        writer.Write(0);
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        // XOR bitmap: bottom-up BGRA, straight (non-premultiplied) alpha
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                writer.Write(pixel.Blue);
                writer.Write(pixel.Green);
                writer.Write(pixel.Red);
                writer.Write(pixel.Alpha);
            }
        }

        // AND mask: bottom-up, 1 = transparent
        var maskRow = new byte[maskStride];
        for (int y = height - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);
            for (int x = 0; x < width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0)
                {
                    maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            writer.Write(maskRow);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public byte[] RenderBadgePngBytes(double deltaHours, string text, int size = 64) =>
        RenderBadgePngBytes(text, GetStatusColor(deltaHours).ToString(), size);

    public byte[] RenderBadgePngBytes(string text, string? colorHex = null, int size = 64)
    {
        using var bitmap = RenderBadgeBitmap(text, colorHex, size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public WindowIcon RenderBadgeWindowIcon(double deltaHours, string text, int size = 64) =>
        RenderBadgeWindowIcon(text, GetStatusColor(deltaHours).ToString(), size);

    public WindowIcon RenderBadgeWindowIcon(string text, string? colorHex = null, int size = 64)
    {
        var bytes = RenderBadgePngBytes(text, colorHex, size);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }

    public SKBitmap RenderAlertBitmap(bool isCritical = false, int size = 64)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        if (!isCritical)
        {
            // Warning triangle (Amber/Yellow)
            using var builder = new SKPathBuilder();
            float topX = size / 2f;
            float topY = size * 0.10f;
            float leftX = size * 0.08f;
            float leftY = size * 0.90f;
            float rightX = size * 0.92f;
            float rightY = size * 0.90f;

            builder.MoveTo(topX, topY);
            builder.LineTo(rightX, rightY);
            builder.LineTo(leftX, leftY);
            builder.Close();
            using var path = builder.Detach();

            using var trianglePaint = new SKPaint
            {
                Color = ColorAmber,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawPath(path, trianglePaint);

            // Dark exclamation mark
            using var exclaimPaint = new SKPaint
            {
                Color = SKColor.Parse("#1A1A1A"),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            float barW = size * 0.09f;
            float barTop = size * 0.35f;
            float barH = size * 0.28f;
            canvas.DrawRoundRect(topX - (barW / 2f), barTop, barW, barH, barW / 2f, barW / 2f, exclaimPaint);

            float dotR = barW / 2f;
            float dotY = size * 0.75f;
            canvas.DrawCircle(topX, dotY, dotR, exclaimPaint);
        }
        else
        {
            // Critical / Prohibited sign (Red circle with white horizontal bar)
            using var circlePaint = new SKPaint
            {
                Color = ColorRed,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            float radius = (size / 2f) - 2f;
            canvas.DrawCircle(size / 2f, size / 2f, radius, circlePaint);

            using var barPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            float barW = size * 0.65f;
            float barH = size * 0.18f;
            canvas.DrawRoundRect((size - barW) / 2f, (size - barH) / 2f, barW, barH, barH / 2f, barH / 2f, barPaint);
        }

        return bitmap;
    }

    public byte[] RenderAlertPngBytes(bool isCritical = false, int size = 64)
    {
        using var bitmap = RenderAlertBitmap(isCritical, size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public WindowIcon RenderAlertWindowIcon(bool isCritical = false, int size = 64)
    {
        var bytes = RenderAlertPngBytes(isCritical, size);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }

    private static SKBitmap? _cachedMasterIcon;
    private static readonly object _masterLock = new();

    private static SKBitmap? GetMasterAppIcon()
    {
        if (_cachedMasterIcon != null) return _cachedMasterIcon;

        lock (_masterLock)
        {
            if (_cachedMasterIcon != null) return _cachedMasterIcon;

            // 1. Try embedded manifest resource in assembly
            try
            {
                using var resourceStream = typeof(IconRendererService).Assembly.GetManifestResourceStream("VSNAiRateTracker.Assets.icon.png");
                if (resourceStream != null)
                {
                    _cachedMasterIcon = SKBitmap.Decode(resourceStream);
                    if (_cachedMasterIcon != null) return _cachedMasterIcon;
                }
            }
            catch
            {
                // Fallback to file system
            }

            // 2. Try file system relative to AppContext.BaseDirectory
            try
            {
                var filePath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
                if (File.Exists(filePath))
                {
                    using var fileStream = File.OpenRead(filePath);
                    _cachedMasterIcon = SKBitmap.Decode(fileStream);
                    if (_cachedMasterIcon != null) return _cachedMasterIcon;
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        }
    }

    public SKBitmap RenderAppIconBitmap(int size = 64)
    {
        var master = GetMasterAppIcon();
        if (master != null)
        {
            if (master.Width == size && master.Height == size)
            {
                return master.Copy();
            }

            var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            var destRect = new SKRect(0, 0, size, size);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(master, destRect, new SKSamplingOptions(SKCubicResampler.CatmullRom), paint);
            return bitmap;
        }

        // Fallback: draw official VSN logo vector squircle if master image is not found
        return RenderFallbackVLogo(size);
    }

    private static SKBitmap RenderFallbackVLogo(int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // VSN brand colors
        var bg = SKColor.Parse("#1A262C");
        var lime = SKColor.Parse("#ADC736");
        var teal = SKColor.Parse("#008065");
        var shadow = SKColor.Parse("#006B54");

        float pad = Math.Max(1f, size * 0.02f);
        var squircleRect = new SKRoundRect(new SKRect(pad, pad, size - pad, size - pad), size * 0.20f, size * 0.20f);
        using var bgPaint = new SKPaint { Color = bg, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawRoundRect(squircleRect, bgPaint);

        // Right wing (teal)
        using var tealPaint = new SKPaint { Color = teal, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var rightWingBuilder = new SKPathBuilder();
        rightWingBuilder.MoveTo(size * 0.63f, size * 0.14f);
        rightWingBuilder.LineTo(size * 0.91f, size * 0.14f);
        rightWingBuilder.LineTo(size * 0.60f, size * 0.91f);
        rightWingBuilder.LineTo(size * 0.48f, size * 0.62f);
        rightWingBuilder.Close();
        using var rightWing = rightWingBuilder.Detach();
        canvas.DrawPath(rightWing, tealPaint);

        // Shadow overlap
        using var shadowPaint = new SKPaint { Color = shadow, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var shadowBuilder = new SKPathBuilder();
        shadowBuilder.MoveTo(size * 0.50f, size * 0.67f);
        shadowBuilder.LineTo(size * 0.64f, size * 0.91f);
        shadowBuilder.LineTo(size * 0.35f, size * 0.91f);
        shadowBuilder.Close();
        using var shadowPath = shadowBuilder.Detach();
        canvas.DrawPath(shadowPath, shadowPaint);

        // Left wing (lime)
        using var limePaint = new SKPaint { Color = lime, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var leftWingBuilder = new SKPathBuilder();
        leftWingBuilder.MoveTo(size * 0.08f, size * 0.14f);
        leftWingBuilder.LineTo(size * 0.36f, size * 0.14f);
        leftWingBuilder.LineTo(size * 0.64f, size * 0.91f);
        leftWingBuilder.LineTo(size * 0.35f, size * 0.91f);
        leftWingBuilder.Close();
        using var leftWing = leftWingBuilder.Detach();
        canvas.DrawPath(leftWing, limePaint);

        return bitmap;
    }

    public byte[] RenderAppIconPngBytes(int size = 64)
    {
        using var bitmap = RenderAppIconBitmap(size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public WindowIcon RenderAppIconWindowIcon(int size = 64)
    {
        var bytes = RenderAppIconPngBytes(size);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }
}
