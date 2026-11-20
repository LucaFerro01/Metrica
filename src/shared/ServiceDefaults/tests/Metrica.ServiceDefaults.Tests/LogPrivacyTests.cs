using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Metrica.ServiceDefaults.Tests;

/// <summary>REQ-034: nei log non compaiono query string, corpo o intestazioni delle richieste.</summary>
public sealed class LogPrivacyTests
{
    // Marcatori non esadecimali: non possono comparire per caso in GUID, TraceId, SpanId o RequestId.
    private const string LatMarker = "zzlat-riservato";
    private const string HrMarker = "zzhr-riservato";
    private const string BodyMarker = "zzbody-riservato";
    private const string HeaderMarker = "zzheader-riservato";
    private const string Secret = $"lat={LatMarker}&hr={HrMarker}";

    [Fact]
    public async Task RequestLogs_DoNotContainQueryStringValues()
    {
        var logs = new InMemoryLoggerProvider();
        await using var app = await TestApp.StartAsync("Production", configure: b => b.Logging.AddProvider(logs));
        using var client = app.GetTestClient();

        await client.GetAsync($"/log?{Secret}");

        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, e =>
        {
            Assert.DoesNotContain(LatMarker, e.AllText);
            Assert.DoesNotContain(HrMarker, e.AllText);
        });
    }

    [Fact]
    public async Task Control_WithoutTheWarningFilter_FrameworkLogsDoLeakTheQueryString()
    {
        // Verifica che il test sopra sia significativo: abbassando il livello, il framework logga la query string.
        var logs = new InMemoryLoggerProvider();
        await using var app = await TestApp.StartAsync("Production", configure: b =>
        {
            b.Logging.AddProvider(logs);
            b.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
        });
        using var client = app.GetTestClient();

        await client.GetAsync($"/log?{Secret}");

        Assert.Contains(logs.Entries, e => e.AllText.Contains(LatMarker));
    }

    [Fact]
    public async Task RequestLogs_DoNotContainHeadersOrBody()
    {
        var logs = new InMemoryLoggerProvider();
        await using var app = await TestApp.StartAsync("Production", configure: b => b.Logging.AddProvider(logs));
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/log")
        {
            Content = new StringContent($"{{\"hr\":\"{BodyMarker}\"}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Secret-Header", HeaderMarker);

        await client.SendAsync(request);

        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, e =>
        {
            Assert.DoesNotContain(HeaderMarker, e.AllText);
            Assert.DoesNotContain(BodyMarker, e.AllText);
        });
    }
}
