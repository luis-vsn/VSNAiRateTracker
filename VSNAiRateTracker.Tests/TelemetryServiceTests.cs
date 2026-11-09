using System;
using FluentAssertions;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class TelemetryServiceTests
{
    [Theory]
    [InlineData("https://otlp-gateway-prod-eu-west-6.grafana.net/otlp", "v1/metrics", "https://otlp-gateway-prod-eu-west-6.grafana.net/otlp/v1/metrics")]
    [InlineData("https://otlp-gateway-prod-eu-west-6.grafana.net/otlp/", "v1/traces", "https://otlp-gateway-prod-eu-west-6.grafana.net/otlp/v1/traces")]
    [InlineData("https://otlp-gateway-prod-eu-west-6.grafana.net/otlp/v1/traces", "v1/metrics", "https://otlp-gateway-prod-eu-west-6.grafana.net/otlp/v1/metrics")]
    [InlineData("http://localhost:4318", "v1/logs", "http://localhost:4318/v1/logs")]
    [InlineData("http://localhost:4318/", "v1/traces", "http://localhost:4318/v1/traces")]
    public void ResolveSignalEndpoint_AppendsOrReplacesSignalPathAccurately(string raw, string signal, string expected)
    {
        var resolved = TelemetryService.ResolveSignalEndpoint(raw, signal);
        resolved.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("Authorization=Basic abc123==", "Authorization=Basic abc123==")]
    [InlineData("\"Authorization=Basic abc123==\"", "Authorization=Basic abc123==")]
    [InlineData("Authorization: Basic abc123==", "Authorization=Basic abc123==")]
    [InlineData("Authorization: Bearer token123", "Authorization=Bearer token123")]
    [InlineData("Basic abc123==", "Authorization=Basic abc123==")]
    [InlineData("Bearer token123", "Authorization=Bearer token123")]
    [InlineData("1981411:glc_token", "Authorization=Basic MTk4MTQxMTpnbGNfdG9rZW4=")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeHeaders_EnsuresKeyEqualsValueFormat(string? raw, string expected)
    {
        var normalized = TelemetryService.NormalizeHeaders(raw);
        normalized.Should().Be(expected);
    }
}
