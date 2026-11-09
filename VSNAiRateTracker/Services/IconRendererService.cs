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

    public SKBitmap RenderBadgeBitmap(string text, string? colorHex = null, int size = 64)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            SKColor color = ColorGreen;
            if (!string.IsNullOrWhiteSpace(colorHex) && SKColor.TryParse(colorHex, out var parsedColor))
            {
                color = parsedColor;
            }

            var displayText = text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayText))
            {
                displayText = "+0.0";
            }

            // Prefer Bahnschrift (Windows standard DIN-1451 font for high-legibility dials/gauges), fallback to Segoe UI Black, Arial Black
            using var typeface = SKTypeface.FromFamilyName("Bahnschrift", SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
                ?? SKTypeface.FromFamilyName("Segoe UI Variable Display", SKFontStyleWeight.Black, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
                ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Black, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
                ?? SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;

            // Target filling almost the entire canvas: 98% of width, 92% of height
            float targetW = size * 0.98f;
            float targetH = size * 0.92f;

            // Measure with a test font size using compact tracking (tight kerning for '.' and signs)
            float testFontSize = 32f;
            using var testFont = new SKFont(typeface, testFontSize);
            using var testPaint = new SKPaint { IsAntialias = true };

            var (initWidth, initHeight, _, _) = MeasureTightLayout(displayText, testFont, testPaint);

            float calculatedFontSize = testFontSize;
            if (initWidth > 0 && initHeight > 0)
            {
                float scaleX = targetW / initWidth;
                float scaleY = targetH / initHeight;
                calculatedFontSize = testFontSize * Math.Min(scaleX, scaleY);
            }

            using var font = new SKFont(typeface, calculatedFontSize);

            // Subtle dark contrast outline to guarantee sharpness on both light and dark system trays
            using var outlinePaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 220),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1.5f, size * 0.045f)
            };

            using var textPaint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            var (finalWidth, _, charWidths, charLefts) = MeasureTightLayout(displayText, font, textPaint);

            // Vertical centering based on digits
            float digitTop = 0f;
            float digitBottom = 0f;
            bool foundDigit = false;
            for (int i = 0; i < displayText.Length; i++)
            {
                if (char.IsDigit(displayText[i]))
                {
                    font.MeasureText(displayText.Substring(i, 1), out var db, textPaint);
                    if (!foundDigit || db.Top < digitTop) digitTop = db.Top;
                    if (!foundDigit || db.Bottom > digitBottom) digitBottom = db.Bottom;
                    foundDigit = true;
                }
            }

            float digitHeight = foundDigit ? (digitBottom - digitTop) : font.Size;
            float baseY = (size / 2f) - (digitTop + (digitHeight / 2f));
            float startX = (size - finalWidth) / 2f;

            float curX = startX;
            for (int i = 0; i < displayText.Length; i++)
            {
                var ch = displayText.Substring(i, 1);
                float drawX = curX - charLefts[i];

                canvas.DrawText(ch, drawX, baseY, SKTextAlign.Left, font, outlinePaint);
                canvas.DrawText(ch, drawX, baseY, SKTextAlign.Left, font, textPaint);

                float gap = font.Size * (ch == "." ? 0.03f : (ch == "+" || ch == "-" ? 0.04f : 0.035f));
                curX += charWidths[i] + gap;
            }
        }

        return bitmap;
    }

    private static (float totalWidth, float maxHeight, float[] charWidths, float[] charLefts) MeasureTightLayout(
        string text,
        SKFont font,
        SKPaint paint)
    {
        var charWidths = new float[text.Length];
        var charLefts = new float[text.Length];
        float curX = 0f;
        float maxHeight = 0f;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text.Substring(i, 1);
            font.MeasureText(ch, out var b, paint);

            float visualW = Math.Max(b.Width, 1f);
            charWidths[i] = visualW;
            charLefts[i] = b.Left;

            float gap = font.Size * (ch == "." ? 0.03f : (ch == "+" || ch == "-" ? 0.04f : 0.035f));
            curX += visualW + (i < text.Length - 1 ? gap : 0f);

            if (b.Height > maxHeight)
            {
                maxHeight = b.Height;
            }
        }

        return (curX, maxHeight, charWidths, charLefts);
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
