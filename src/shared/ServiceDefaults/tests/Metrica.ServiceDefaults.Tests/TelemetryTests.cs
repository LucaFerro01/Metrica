using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Metrica.ServiceDefaults.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task OpenTelemetry_ProvidersAreRegistered_WithoutEndpoint()
    {
        await using var app = await TestApp.StartAsync("Production");

        Assert.NotNull(app.Services.GetService<TracerProvider>());
        Assert.NotNull(app.Services.GetService<MeterProvider>());
    }

    [Theory]
    [InlineData("Otlp:Endpoint")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT")]
    public async Task OpenTelemetry_WithEndpointConfigured_StillStarts(string key)
    {
        var configuration = new Dictionary<string, string?> { [key] = "http://127.0.0.1:4317" };

        await using var app = await TestApp.StartAsync("Production", configuration);

        Assert.NotNull(app.Services.GetService<TracerProvider>());
        Assert.NotNull(app.Services.GetService<MeterProvider>());
    }

    [Fact]
    public void OpenTelemetry_WithInvalidOtlpEndpoint_FailsWithClearMessage()
    {
        var builder = TestApp.CreateBuilder("Production", new Dictionary<string, string?> { ["Otlp:Endpoint"] = "non un uri" });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddServiceDefaults());

        Assert.Contains("Otlp:Endpoint", ex.Message);
    }
}
