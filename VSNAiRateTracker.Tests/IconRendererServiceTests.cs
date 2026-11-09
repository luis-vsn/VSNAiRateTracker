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
        contentHeight.Should().BeGreaterThanOrEqualTo(20); // >= 30% of 64px
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
        contentHeight.Should().BeGreaterThanOrEqualTo(20);
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

        var pngBytes = _sut.RenderAppIconPngBytes(64);
        pngBytes.Length.Should().BeGreaterThan(100);

        // Also save to Assets directory for application packaging
        var assetsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "VSNAiRateTracker", "Assets"));
        if (Directory.Exists(assetsDir))
        {
            File.WriteAllBytes(Path.Combine(assetsDir, "icon.png"), pngBytes);

            int[] sizes = [256, 48, 32, 16];
            var entries = new System.Collections.Generic.List<(int size, byte[] bytes)>();
            foreach (var s in sizes)
            {
                entries.Add((s, _sut.RenderAppIconPngBytes(s)));
            }

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

            File.WriteAllBytes(Path.Combine(assetsDir, "icon.ico"), ms.ToArray());
        }
    }
}
