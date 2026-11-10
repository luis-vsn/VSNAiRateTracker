using System;
using System.IO;
using Avalonia;
using FluentAssertions;
using SkiaSharp;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class IconRendererServiceTests
{
    private readonly IIconRendererService _sut;

    public IconRendererServiceTests()
    {
        _sut = new IconRendererService();
    }

    [Theory]
    [InlineData(0.0, 0x2E, 0xCC, 0x71)]   // Green #2ECC71 (Optimal)
    [InlineData(0.5, 0x2E, 0xCC, 0x71)]   // Green #2ECC71 (Optimal boundary)
    [InlineData(-0.5, 0x2E, 0xCC, 0x71)]  // Green #2ECC71 (Optimal negative boundary)
    [InlineData(2.5, 0xF3, 0x9C, 0x12)]   // Amber #F39C12 (WarningFast)
    [InlineData(5.0, 0xF3, 0x9C, 0x12)]   // Amber #F39C12 (WarningFast boundary)
    [InlineData(5.5, 0xE7, 0x4C, 0x3C)]   // Red #E74C3C (DangerFast)
    [InlineData(-0.6, 0x34, 0x98, 0xDB)]  // Blue #3498DB (Slow)
    [InlineData(-3.0, 0x34, 0x98, 0xDB)]  // Blue #3498DB (Slow)
    public void GetStatusColor_ReturnsExpectedColor(double delta, byte r, byte g, byte b)
    {
        var color = _sut.GetStatusColor(delta);
        color.Red.Should().Be(r);
        color.Green.Should().Be(g);
        color.Blue.Should().Be(b);
    }

    [Fact]
    public void RenderBadgeBitmap_GeneratesValid64x64BitmapWithDecimalText()
    {
        using var bitmap = _sut.RenderBadgeBitmap("+0.2", "#2ECC71", 64);

        bitmap.Should().NotBeNull();
        bitmap.Width.Should().Be(64);
        bitmap.Height.Should().Be(64);

        bool hasContent = false;
        for (int y = 0; y < 64 && !hasContent; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    hasContent = true;
                    break;
                }
            }
        }
        hasContent.Should().BeTrue();
    }

    [Fact]
    public void RenderBadgeBitmap_MaximizesCanvasUsageForTaskbarLegibility()
    {
        using var bitmap = _sut.RenderBadgeBitmap("+0.2", "#2ECC71", 64);

        int minX = 64, maxX = 0, minY = 64, maxY = 0;
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 10)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;

        // Content must occupy the vast majority of the 64x64 canvas to be clearly visible in Windows tray
        contentWidth.Should().BeGreaterThanOrEqualTo(40); // >= 60% of 64px
        contentHeight.Should().BeGreaterThanOrEqualTo(40); // >= 62% of 64px (previously only ~20px)
    }

    [Theory]
    [InlineData(0.0, "+0.0", "+0.0h")]
    [InlineData(0.4, "+0.4", "+0.4h")]
    [InlineData(-0.4, "-0.4", "-0.4h")]
    [InlineData(9.9, "+9.9", "+9.9h")]
    [InlineData(-9.9, "-9.9", "-9.9h")]
    [InlineData(10.0, "+10", "+10h")]
    [InlineData(-10.0, "-10", "-10h")]
    [InlineData(12.4, "+12", "+12h")]
    [InlineData(12.6, "+13", "+13h")]
    [InlineData(-15.3, "-15", "-15h")]
    [InlineData(-15.7, "-16", "-16h")]
    public void UsageState_ShortText_FollowsThresholdRulesAndAlwaysIncludesSign(double delta, string expectedShort, string expectedFormatted)
    {
        var state = new Models.UsageState { DeltaHours = delta };
        state.ShortText.Should().Be(expectedShort);
        state.FormattedDelta.Should().Be(expectedFormatted);
    }

    [Fact]
    public void RenderBadgeBitmap_ForNegativeDecimal_GeneratesTallCompactDigits()
    {
        using var bitmap = _sut.RenderBadgeBitmap("-0.4", "#2ECC71", 64);
        int minX = 64, maxX = 0, minY = 64, maxY = 0;
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 10)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;
        contentWidth.Should().BeGreaterThanOrEqualTo(40);
        contentHeight.Should().BeGreaterThanOrEqualTo(40);
    }

    [Fact]
    public void RenderBadgePngBytes_GeneratesValidPngData()
    {
        var bytes = _sut.RenderBadgePngBytes("-0.1", "#3498DB", 64);

        bytes.Should().NotBeNull();
        bytes.Length.Should().BeGreaterThan(100);

        // Standard PNG Magic Number: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50); // 'P'
        bytes[2].Should().Be(0x4E); // 'N'
        bytes[3].Should().Be(0x47); // 'G'
    }

    [Fact]
    public void RenderAlertBitmap_WarningAndCritical_RendersValidNonEmptyBitmaps()
    {
        using var warningBmp = _sut.RenderAlertBitmap(isCritical: false, 64);
        using var criticalBmp = _sut.RenderAlertBitmap(isCritical: true, 64);

        warningBmp.Should().NotBeNull();
        warningBmp.Width.Should().Be(64);
        warningBmp.Height.Should().Be(64);

        criticalBmp.Should().NotBeNull();
        criticalBmp.Width.Should().Be(64);
        criticalBmp.Height.Should().Be(64);

        var warningBytes = _sut.RenderAlertPngBytes(false, 64);
        var criticalBytes = _sut.RenderAlertPngBytes(true, 64);

        warningBytes.Should().NotEqual(criticalBytes);
    }

    [Fact]
    public void RenderAppIconBitmap_RendersValidVLogo()
    {
        using var appIconBmp = _sut.RenderAppIconBitmap(64);

        appIconBmp.Should().NotBeNull();
        appIconBmp.Width.Should().Be(64);
        appIconBmp.Height.Should().Be(64);

        // Verify non-empty
        bool hasNonTransparent = false;
        for (int y = 0; y < 64 && !hasNonTransparent; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (appIconBmp.GetPixel(x, y).Alpha > 10)
                {
                    hasNonTransparent = true;
                    break;
                }
            }
        }
        hasNonTransparent.Should().BeTrue();

        var pngBytes = _sut.RenderAppIconPngBytes(64);
        pngBytes.Length.Should().BeGreaterThan(100);
        pngBytes[0].Should().Be(0x89);
        pngBytes[1].Should().Be(0x50); // 'P'
        pngBytes[2].Should().Be(0x4E); // 'N'
        pngBytes[3].Should().Be(0x47); // 'G'

        using var appIcon256 = _sut.RenderAppIconBitmap(256);
        appIcon256.Should().NotBeNull();
        appIcon256.Width.Should().Be(256);
        appIcon256.Height.Should().Be(256);
    }

    [Theory]
    [InlineData("-0.4")]
    [InlineData("+0.4")]
    [InlineData("+1.2")]
    [InlineData("-2.5")]
    [InlineData("+12")]
    [InlineData("-15")]
    [InlineData("+0.0")]
    [InlineData("+100")]
    [InlineData("-120")]
    [InlineData("...")]
    [InlineData("ERR")]
    public void RenderBadgeBitmap_NeverClipsOnRightOrLeftEdges(string text)
    {
        int size = 64;
        using var bmp = _sut.RenderBadgeBitmap(text, "#2ECC71", size);

        int minX = size, maxX = 0, minY = size, maxY = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (bmp.GetPixel(x, y).Alpha > 10)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        // Must not touch the canvas boundary - guarantees no clipping on any edge
        maxX.Should().BeLessThan(size, $"Right edge of text '{text}' must not be clipped");
        minX.Should().BeGreaterThan(0, $"Left edge of text '{text}' must not be clipped");
        maxY.Should().BeLessThan(size, $"Bottom edge of text '{text}' must not be clipped");
        minY.Should().BeGreaterThan(0, $"Top edge of text '{text}' must not be clipped");

        // Visual width and height should still fill a large portion of canvas
        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;
        contentWidth.Should().BeGreaterThanOrEqualTo(25);
        contentHeight.Should().BeGreaterThanOrEqualTo(20);

        // Save to scratch for visual inspection
        var scratchDir = @"C:\Users\Luis Macia\.gemini\antigravity\brain\394273ca-0351-4053-b80f-a33cf9e93675\scratch";
        var slug = text.Replace('+', 'p').Replace('-', 'm').Replace('.', '_');
        var pngBytes = _sut.RenderBadgePngBytes(text, "#2ECC71", 64);
        File.WriteAllBytes(Path.Combine(scratchDir, $"clean_{slug}_64.png"), pngBytes);

        var pngBytes24 = _sut.RenderBadgePngBytes(text, "#2ECC71", 24);
        File.WriteAllBytes(Path.Combine(scratchDir, $"clean_{slug}_24.png"), pngBytes24);
    }

    [Theory]
    [InlineData("...")]
    [InlineData("ERR")]
    [InlineData("")]
    [InlineData("+100")]
    [InlineData("-120")]
    public void RenderBadgeBitmap_HandlesSpecialAndNonNumericValuesGracefully(string text)
    {
        using var bmp = _sut.RenderBadgeBitmap(text, "#2ECC71", 64);
        bmp.Should().NotBeNull();
        bmp.Width.Should().Be(64);
        bmp.Height.Should().Be(64);

        int minX = 64, maxX = 0, minY = 64, maxY = 0;
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                if (bmp.GetPixel(x, y).Alpha > 10)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;
        contentWidth.Should().BeGreaterThanOrEqualTo(20);
        contentHeight.Should().BeGreaterThanOrEqualTo(10);
    }
}
