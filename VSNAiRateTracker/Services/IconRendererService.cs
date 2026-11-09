using System;
using System.IO;
using Avalonia.Controls;
using SkiaSharp;

namespace VSNAiRateTracker.Services;

public interface IIconRendererService
{
    SKColor GetStatusColor(double deltaHours);
    SKBitmap RenderBadgeBitmap(double deltaHours, string text, int size = 64);
    byte[] RenderBadgePngBytes(double deltaHours, string text, int size = 64);
    WindowIcon RenderBadgeWindowIcon(double deltaHours, string text, int size = 64);
}

public class IconRendererService : IIconRendererService
{
    public static readonly SKColor ColorGreen = SKColor.Parse("#2ECC71");
    public static readonly SKColor ColorAmber = SKColor.Parse("#F39C12");
    public static readonly SKColor ColorRed = SKColor.Parse("#E74C3C");

    private const double Epsilon = 1e-6;

    public SKColor GetStatusColor(double deltaHours)
    {
        if (deltaHours >= -Epsilon)
            return ColorGreen;
        if (deltaHours >= -2.0 - Epsilon)
            return ColorAmber;
        return ColorRed;
    }

    public SKBitmap RenderBadgeBitmap(double deltaHours, string text, int size = 64)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            var color = GetStatusColor(deltaHours);
            using var circlePaint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            float radius = (size / 2f) - 1.5f;
            canvas.DrawCircle(size / 2f, size / 2f, radius, circlePaint);

            // Subtle border highlight
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 70),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f
            };
            canvas.DrawCircle(size / 2f, size / 2f, radius - 1f, borderPaint);

            // Center text
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), size * 0.36f);
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            font.MeasureText(text, out var textBounds, textPaint);
            float x = size / 2f;
            float y = (size / 2f) - textBounds.MidY;
            canvas.DrawText(text, x, y, SKTextAlign.Center, font, textPaint);
        }

        return bitmap;
    }

    public byte[] RenderBadgePngBytes(double deltaHours, string text, int size = 64)
    {
        using var bitmap = RenderBadgeBitmap(deltaHours, text, size);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public WindowIcon RenderBadgeWindowIcon(double deltaHours, string text, int size = 64)
    {
        var bytes = RenderBadgePngBytes(deltaHours, text, size);
        using var stream = new MemoryStream(bytes);
        return new WindowIcon(stream);
    }
}
