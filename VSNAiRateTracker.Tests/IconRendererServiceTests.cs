using System;
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
    [InlineData(4.5, 0x2E, 0xCC, 0x71)]   // Green #2ECC71
    [InlineData(0.0, 0x2E, 0xCC, 0x71)]   // Green #2ECC71
    [InlineData(-1.2, 0xF3, 0x9C, 0x12)]  // Amber #F39C12
    [InlineData(-2.0, 0xF3, 0x9C, 0x12)]  // Amber #F39C12
    [InlineData(-2.1, 0xE7, 0x4C, 0x3C)]  // Red #E74C3C
    [InlineData(-5.0, 0xE7, 0x4C, 0x3C)]  // Red #E74C3C
    public void GetStatusColor_ReturnsExpectedColor(double delta, byte r, byte g, byte b)
    {
        var color = _sut.GetStatusColor(delta);
        color.Red.Should().Be(r);
        color.Green.Should().Be(g);
        color.Blue.Should().Be(b);
    }

    [Fact]
    public void RenderBadgeBitmap_GeneratesValid64x64Bitmap()
    {
        using var bitmap = _sut.RenderBadgeBitmap(3.0, "+3h", 64);

        bitmap.Should().NotBeNull();
        bitmap.Width.Should().Be(64);
        bitmap.Height.Should().Be(64);

        // Center should not be transparent
        var centerPixel = bitmap.GetPixel(32, 32);
        centerPixel.Alpha.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderBadgePngBytes_GeneratesValidPngData()
    {
        var bytes = _sut.RenderBadgePngBytes(-1.5, "-2h", 64);

        bytes.Should().NotBeNull();
        bytes.Length.Should().BeGreaterThan(100);

        // Standard PNG Magic Number: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50); // 'P'
        bytes[2].Should().Be(0x4E); // 'N'
        bytes[3].Should().Be(0x47); // 'G'
    }

    [Fact]
    public void RenderBadgePngBytes_DifferentDeltaAndText_ProducesDistinctPayloads()
    {
        var greenBytes = _sut.RenderBadgePngBytes(4.0, "+4h", 64);
        var redBytes = _sut.RenderBadgePngBytes(-4.0, "-4h", 64);

        greenBytes.Should().NotBeNull();
        redBytes.Should().NotBeNull();
        greenBytes.Should().NotEqual(redBytes);
    }
}
