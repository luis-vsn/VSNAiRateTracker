using System;
using System.Collections.Generic;
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
    byte[] RenderBadgeIcoBytes(double deltaHours, string text);
    byte[] RenderBadgeIcoBytes(string text, string? colorHex = null);
    WindowIcon RenderBadgeWindowIcon(double deltaHours, string text, int size = 64);
    WindowIcon RenderBadgeWindowIcon(string text, string? colorHex = null, int size = 64);

    SKBitmap RenderAlertBitmap(bool isCritical = false, int size = 64);
    byte[] RenderAlertPngBytes(bool isCritical = false, int size = 64);
    byte[] RenderAlertIcoBytes(bool isCritical = false);
    WindowIcon RenderAlertWindowIcon(bool isCritical = false, int size = 64);

    SKBitmap RenderAppIconBitmap(int size = 64);
    byte[] RenderAppIconPngBytes(int size = 64);
    byte[] RenderAppIconIcoBytes();
    WindowIcon RenderAppIconWindowIcon(int size = 64);
}

public class IconRendererService : IIconRendererService
{
    public static readonly SKColor ColorGreen = SKColor.Parse("#2ECC71");
    public static readonly SKColor ColorAmber = SKColor.Parse("#F39C12");
    public static readonly SKColor ColorRed = SKColor.Parse("#E74C3C");
    public static readonly SKColor ColorBlue = SKColor.Parse("#3498DB");

    private static readonly int[] StandardTrayIconSizes = [16, 20, 24, 32, 48, 64];

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

        var displayText = string.IsNullOrWhiteSpace(text) ? "+0.0" : text.Trim();

        // Prefer Bahnschrift (Windows standard DIN-1451 font for high-legibility dials/gauges), fallback to Segoe UI Black, Arial
        using var typeface = SKTypeface.FromFamilyName("Bahnschrift", SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName("Segoe UI Variable Display", SKFontStyleWeight.Black, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Black, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.Default;

        // Check if starts with sign
        bool hasSign = displayText.StartsWith('+') || displayText.StartsWith('-');
        char signChar = hasSign ? displayText[0] : ' ';
        string restText = hasSign ? displayText.Substring(1) : displayText;

        // Check if rest is numeric (with optional decimal point)
        int dotIdx = restText.IndexOf('.');
        string intPart = dotIdx >= 0 ? restText.Substring(0, dotIdx) : restText;
        string? decPart = dotIdx >= 0 ? restText.Substring(dotIdx + 1) : null;
        bool isNumeric = int.TryParse(intPart, out _) && (decPart == null || int.TryParse(decPart, out _));

        using var fillPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // Dark outer stroke drawn under fill to guarantee sharpness on both dark and light taskbars without eating into digit glyphs
        using var outlinePaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 230),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, size * 0.04f),
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round
        };

        using var testPaint = new SKPaint { IsAntialias = true };

        if (!isNumeric)
        {
            // Non-numeric fallback (e.g. "...", "ERR", "OK")
            float testFontSize = size * 0.5f;
            using var fbFont = new SKFont(typeface, testFontSize) { Subpixel = true };
            fbFont.MeasureText(displayText, out var fbBounds, testPaint);
            float scale = Math.Min((size * 0.92f) / Math.Max(fbBounds.Width, 1f), (size * 0.85f) / Math.Max(fbBounds.Height, 1f));
            using var finalFont = new SKFont(typeface, testFontSize * scale) { Subpixel = true };
            finalFont.MeasureText(displayText, out var b, testPaint);
            float drawX = (size - b.Width) / 2f - b.Left;
            float drawY = (size / 2f) - (b.Top + (b.Height / 2f));
            canvas.DrawText(displayText, drawX, drawY, SKTextAlign.Left, finalFont, outlinePaint);
            canvas.DrawText(displayText, drawX, drawY, SKTextAlign.Left, finalFont, fillPaint);
            return bitmap;
        }

        // Target digit height: 85% of canvas height to maximize visibility in Windows system tray
        float targetDigitH = size * 0.85f;
        float refFontSize = 40f;
        using var refFont = new SKFont(typeface, refFontSize);
        refFont.MeasureText("8", out var refBounds, testPaint);
        float naturalH = Math.Max(refBounds.Height, 1f);
        float digitFontSize = refFontSize * (targetDigitH / naturalH);

        // Sign dimensions
        bool isMinus = signChar == '-';
        float signW = hasSign ? size * (isMinus ? 0.16f : 0.20f) : 0f;
        float signThick = Math.Max(1.5f, size * (isMinus ? 0.09f : 0.08f));
        float signRightGap = hasSign ? size * 0.04f : 0f;

        // Decimal dot dimensions
        float dotW = decPart != null ? Math.Max(2f, size * 0.07f) : 0f;
        float dotGap = decPart != null ? size * 0.025f : 0f;

        string allDigits = intPart + (decPart ?? "");
        int digitCount = allDigits.Length;

        // Measure natural widths of all digits
        using var measureFont = new SKFont(typeface, digitFontSize);
        float naturalTotalDigitW = 0f;
        float interDigitGap = (intPart.Length > 1 ? size * 0.03f : 0f);
        for (int i = 0; i < digitCount; i++)
        {
            var ch = allDigits.Substring(i, 1);
            measureFont.MeasureText(ch, out var b, testPaint);
            naturalTotalDigitW += b.Width;
        }
        naturalTotalDigitW += (intPart.Length - 1) * interDigitGap;

        // Available width for digits
        float availW = size - (signW + signRightGap + (decPart != null ? (dotW + dotGap * 2) : 0f));
        float scaleX = Math.Min(1.05f, (availW * 0.98f) / Math.Max(naturalTotalDigitW, 1f));

        using var font = new SKFont(typeface, digitFontSize)
        {
            ScaleX = scaleX,
            Subpixel = true
        };

        // Re-measure with final scale
        float finalDigitW = 0f;
        float[] finalCharWidths = new float[digitCount];
        float[] finalCharLefts = new float[digitCount];
        for (int i = 0; i < digitCount; i++)
        {
            var ch = allDigits.Substring(i, 1);
            font.MeasureText(ch, out var b, testPaint);
            finalCharWidths[i] = b.Width;
            finalCharLefts[i] = b.Left;
            finalDigitW += b.Width;
        }
        finalDigitW += (intPart.Length - 1) * interDigitGap;

        float totalVisualW = signW + signRightGap + finalDigitW + (decPart != null ? (dotW + dotGap * 2) : 0f);
        float startX = Math.Max(0.5f, (size - totalVisualW) / 2f);

        // Center digits vertically
        font.MeasureText("8", out var final8Bounds, testPaint);
        float baseY = (size / 2f) - (final8Bounds.Top + (final8Bounds.Height / 2f));

        // 1. Draw Sign
        float signX = startX;
        float signMidY = size / 2f;
        if (hasSign)
        {
            if (isMinus)
            {
                float mLeft = signX;
                float mRight = signX + signW;
                float mTop = signMidY - (signThick / 2f);
                float mBottom = signMidY + (signThick / 2f);
                var rect = new SKRect(mLeft, mTop, mRight, mBottom);
                canvas.DrawRoundRect(rect, signThick * 0.3f, signThick * 0.3f, outlinePaint);
                canvas.DrawRoundRect(rect, signThick * 0.3f, signThick * 0.3f, fillPaint);
            }
            else
            {
                float crossR = signW / 2f;
                float cX = signX + crossR;
                float cY = signMidY;
                var hRect = new SKRect(cX - crossR, cY - (signThick / 2f), cX + crossR, cY + (signThick / 2f));
                var vRect = new SKRect(cX - (signThick / 2f), cY - crossR, cX + (signThick / 2f), cY + crossR);
                canvas.DrawRoundRect(hRect, signThick * 0.3f, signThick * 0.3f, outlinePaint);
                canvas.DrawRoundRect(vRect, signThick * 0.3f, signThick * 0.3f, outlinePaint);
                canvas.DrawRoundRect(hRect, signThick * 0.3f, signThick * 0.3f, fillPaint);
                canvas.DrawRoundRect(vRect, signThick * 0.3f, signThick * 0.3f, fillPaint);
            }
        }

        float curX = signX + signW + signRightGap;

        // 2. Draw Integer Part
        for (int i = 0; i < intPart.Length; i++)
        {
            var ch = intPart.Substring(i, 1);
            float drawX = curX - finalCharLefts[i];
            canvas.DrawText(ch, drawX, baseY, SKTextAlign.Left, font, outlinePaint);
            canvas.DrawText(ch, drawX, baseY, SKTextAlign.Left, font, fillPaint);
            curX += finalCharWidths[i] + (i < intPart.Length - 1 ? interDigitGap : 0f);
        }

        // 3. Draw Decimal Dot & Decimal Part if present
        if (decPart != null)
        {
            curX += dotGap;
            float dotBottom = baseY;
            float dotLeft = curX;
            var dotRect = new SKRect(dotLeft, dotBottom - dotW, dotLeft + dotW, dotBottom);
            canvas.DrawRoundRect(dotRect, dotW * 0.3f, dotW * 0.3f, outlinePaint);
            canvas.DrawRoundRect(dotRect, dotW * 0.3f, dotW * 0.3f, fillPaint);
            curX += dotW + dotGap;

            int decCharIdx = intPart.Length;
            for (int i = 0; i < decPart.Length; i++)
            {
                var ch = decPart.Substring(i, 1);
                float drawX = curX - finalCharLefts[decCharIdx + i];
                canvas.DrawText(ch, drawX, baseY, SKTextAlign.Left, font, outlinePaint);
                canvas.DrawText(ch, drawX, baseY, SKTextAlign.Left, font, fillPaint);
                curX += finalCharWidths[decCharIdx + i];
            }
        }

        return bitmap;
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

    public byte[] RenderBadgeIcoBytes(double deltaHours, string text) =>
        RenderBadgeIcoBytes(text, GetStatusColor(deltaHours).ToString());

    public byte[] RenderBadgeIcoBytes(string text, string? colorHex = null)
    {
        var entries = new (int size, byte[] bytes)[StandardTrayIconSizes.Length];
        for (int i = 0; i < StandardTrayIconSizes.Length; i++)
        {
            int s = StandardTrayIconSizes[i];
            entries[i] = (s, RenderBadgePngBytes(text, colorHex, s));
        }
        return PackIco(entries);
    }

    public WindowIcon RenderBadgeWindowIcon(double deltaHours, string text, int size = 64) =>
        RenderBadgeWindowIcon(text, GetStatusColor(deltaHours).ToString(), size);

    public WindowIcon RenderBadgeWindowIcon(string text, string? colorHex = null, int size = 64)
    {
        var bytes = RenderBadgeIcoBytes(text, colorHex);
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

    public byte[] RenderAlertIcoBytes(bool isCritical = false)
    {
        var entries = new (int size, byte[] bytes)[StandardTrayIconSizes.Length];
        for (int i = 0; i < StandardTrayIconSizes.Length; i++)
        {
            int s = StandardTrayIconSizes[i];
            entries[i] = (s, RenderAlertPngBytes(isCritical, s));
        }
        return PackIco(entries);
    }

    public WindowIcon RenderAlertWindowIcon(bool isCritical = false, int size = 64)
    {
        var bytes = RenderAlertIcoBytes(isCritical);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }

    public SKBitmap RenderAppIconBitmap(int size = 64)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Dark tech squircle background
        using var bgPaint = new SKPaint
        {
            Color = SKColor.Parse("#0D1B2A"),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        float cornerRadius = size * 0.22f;
        canvas.DrawRoundRect(2f, 2f, size - 4f, size - 4f, cornerRadius, cornerRadius, bgPaint);

        // Tech outer glow / border
        using var borderPaint = new SKPaint
        {
            Color = SKColor.Parse("#1B263B"),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.03f
        };
        canvas.DrawRoundRect(2f, 2f, size - 4f, size - 4f, cornerRadius, cornerRadius, borderPaint);

        // Modern stylized "V" with cyan-to-green gradient
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(size * 0.2f, size * 0.2f),
            new SKPoint(size * 0.8f, size * 0.8f),
            new[] { SKColor.Parse("#00F0FF"), SKColor.Parse("#2ECC71") },
            null,
            SKShaderTileMode.Clamp);

        using var vPaint = new SKPaint
        {
            Shader = shader,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.16f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var vBuilder = new SKPathBuilder();
        vBuilder.MoveTo(size * 0.22f, size * 0.28f);
        vBuilder.LineTo(size * 0.50f, size * 0.72f);
        vBuilder.LineTo(size * 0.78f, size * 0.28f);
        using var vPath = vBuilder.Detach();
        canvas.DrawPath(vPath, vPaint);

        return bitmap;
    }

    public byte[] RenderAppIconPngBytes(int size = 64)
    {
        using var bitmap = RenderAppIconBitmap(size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public byte[] RenderAppIconIcoBytes()
    {
        int[] appSizes = [256, 48, 32, 16];
        var entries = new (int size, byte[] bytes)[appSizes.Length];
        for (int i = 0; i < appSizes.Length; i++)
        {
            int s = appSizes[i];
            entries[i] = (s, RenderAppIconPngBytes(s));
        }
        return PackIco(entries);
    }

    public WindowIcon RenderAppIconWindowIcon(int size = 64)
    {
        var bytes = RenderAppIconIcoBytes();
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }

    private static byte[] PackIco(IReadOnlyList<(int size, byte[] bytes)> entries)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // ICO Header
        writer.Write((ushort)0); // Reserved
        writer.Write((ushort)1); // Type 1 = Icon
        writer.Write((ushort)entries.Count);

        // Directory entries
        int offset = 6 + (16 * entries.Count);
        foreach (var (size, bytes) in entries)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // Palette
            writer.Write((byte)0); // Reserved
            writer.Write((ushort)1); // Planes
            writer.Write((ushort)32); // Bits per pixel
            writer.Write((uint)bytes.Length);
            writer.Write((uint)offset);
            offset += bytes.Length;
        }

        // Image data
        foreach (var (_, bytes) in entries)
        {
            writer.Write(bytes);
        }

        return ms.ToArray();
    }
}
