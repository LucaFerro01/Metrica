using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Metrica.Gateway.Tests;

/// <summary>Opzione <c>Gateway:MaxRequestBodyBytes</c>: valore predefinito allineato a ingestion, validazione all'avvio, chiavi sconosciute rifiutate.</summary>
public sealed class OptionsTests
{
    private const long MiB = 1024 * 1024;

    private static Dictionary<string, string?> Setting(string key, string? value) => new() { [key] = value };

    [Fact]
    public async Task DefaultLimit_Is32MiBPlus1MiB_AndIsWhatKestrelEnforces()
    {
        await using var host = new GatewayHost();
        host.StartAndGetAddresses();

        var limit = host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize;

        Assert.Equal(34_603_008L, limit);
        Assert.Equal(32 * MiB + 1 * MiB, limit);
    }

    [Fact]
    public void DefaultLimit_IsIngestionUploadMaxBytesPlus1MiB()
    {
        // Allineamento con il servizio a cui il gateway inoltra gli upload: se cambia Upload:MaxBytes il gateway va aggiornato.
        var path = Path.Combine(ServicePaths.RepositoryRoot, "src", "services", "ingestion", "src", "Metrica.Ingestion", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var uploadMax = document.RootElement.GetProperty("Upload").GetProperty("MaxBytes").GetInt64();

        Assert.Equal(34_603_008L, uploadMax + 1 * MiB);
    }

    [Fact]
    public async Task ConfiguredLimit_IsAppliedToKestrel()
    {
        await using var host = new GatewayHost(settings: Setting("Gateway:MaxRequestBodyBytes", "123456"));
        host.StartAndGetAddresses();

        Assert.Equal(123_456L, host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("68157440")]   // 64 MiB + 1 MiB: massimo accettato
    public async Task LimitsAtTheEdgesOfTheRange_StartTheHost(string value)
    {
        await using var host = new GatewayHost(settings: Setting("Gateway:MaxRequestBodyBytes", value));

        host.StartAndGetAddresses();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("68157441")]
    [InlineData("99999999999")]
    [InlineData("abc")]
    [InlineData("34603008.5")]
    [InlineData("")]
    public async Task InvalidLimit_FailsTheStartup_NamingTheKey(string value)
    {
        await using var host = new GatewayHost(settings: Setting("Gateway:MaxRequestBodyBytes", value));

        var error = Assert.ThrowsAny<Exception>(() => host.StartAndGetAddresses());

        Assert.Contains("MaxRequestBodyBytes", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Gateway:MaxRequestBodyByte")]
    [InlineData("Gateway:MaxBodyBytes")]
    [InlineData("Gateway:Unknown")]
    public async Task UnknownKeyInTheGatewaySection_FailsTheStartup(string key)
    {
        await using var host = new GatewayHost(settings: Setting(key, "1000"));

        var error = Assert.ThrowsAny<Exception>(() => host.StartAndGetAddresses());

        Assert.Contains("Gateway", error.ToString(), StringComparison.Ordinal);
    }
}
