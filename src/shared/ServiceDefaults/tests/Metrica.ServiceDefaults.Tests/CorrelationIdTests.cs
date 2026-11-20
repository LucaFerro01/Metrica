using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Metrica.ServiceDefaults.Tests;

public sealed class CorrelationIdTests
{
    private const string Header = "X-Correlation-Id";

    private static async Task<(WebApplication App, InMemoryLoggerProvider Logs)> StartAsync()
    {
        var logs = new InMemoryLoggerProvider();
        var app = await TestApp.StartAsync("Production", TestApp.WithDevUser(), b => b.Logging.AddProvider(logs));
        return (app, logs);
    }

    private static HttpRequestMessage Request(string path, string? correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation(Header, correlationId);
        }

        return request;
    }

    [Fact]
    public async Task ValidHeader_IsEchoedAndAppearsInLogScope()
    {
        var (app, logs) = await StartAsync();
        await using var _ = app;
        using var client = app.GetTestClient();
        var id = Guid.NewGuid();

        var response = await client.SendAsync(Request("/log", id.ToString()));

        Assert.Equal(id.ToString(), Assert.Single(response.Headers.GetValues(Header)));
        var entry = Assert.Single(logs.Entries, e => e.Category == "Test");
        Assert.Contains(entry.Scopes, s => s.Contains($"correlationId={id}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("non-un-uuid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidHeader_GeneratesNewUuid(string? incoming)
    {
        var (app, logs) = await StartAsync();
        await using var _ = app;
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Request("/log", incoming));

        var generated = Assert.Single(response.Headers.GetValues(Header));
        Assert.True(Guid.TryParse(generated, out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.NotEqual(incoming, generated);
        var entry = Assert.Single(logs.Entries, e => e.Category == "Test");
        Assert.Contains(entry.Scopes, s => s.Contains($"correlationId={generated}"));
    }

    [Fact]
    public async Task GetCorrelationId_ReturnsTheValidIncomingValue_TheSameAsHeaderAndLogScope()
    {
        var (app, logs) = await StartAsync();
        await using var _ = app;
        using var client = app.GetTestClient();
        var id = Guid.NewGuid();

        var response = await client.SendAsync(Request("/correlation", id.ToString()));

        Assert.Equal(id.ToString(), await response.Content.ReadAsStringAsync());
        Assert.Equal(id.ToString(), Assert.Single(response.Headers.GetValues(Header)));
        var entry = Assert.Single(logs.Entries, e => e.Category == "Test");
        Assert.Contains(entry.Scopes, s => s.Contains($"correlationId={id}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("non-un-uuid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task GetCorrelationId_WhenTheHeaderIsMissingOrInvalid_ReturnsTheGeneratedValue_TheSameAsHeaderAndLogScope(string? incoming)
    {
        var (app, logs) = await StartAsync();
        await using var _ = app;
        using var client = app.GetTestClient();

        var response = await client.SendAsync(Request("/correlation", incoming));

        var fromHeader = Assert.Single(response.Headers.GetValues(Header));
        var fromExtension = await response.Content.ReadAsStringAsync();
        Assert.Equal(fromHeader, fromExtension);
        Assert.True(Guid.TryParse(fromExtension, out var id));
        Assert.NotEqual(Guid.Empty, id);
        var entry = Assert.Single(logs.Entries, e => e.Category == "Test");
        Assert.Contains(entry.Scopes, s => s.Contains($"correlationId={id}"));
    }

    [Fact]
    public async Task GetCorrelationId_WithMultipleHeaderValues_GeneratesOne()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        using var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/correlation");
        request.Headers.TryAddWithoutValidation(Header, [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]);

        var response = await client.SendAsync(request);

        Assert.Equal(Assert.Single(response.Headers.GetValues(Header)), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void GetCorrelationId_WithoutTheMiddleware_ReturnsEmpty()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        Assert.Equal(Guid.Empty, context.GetCorrelationId());
    }

    [Fact]
    public async Task GeneratedIds_DifferBetweenRequests()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        using var client = app.GetTestClient();

        var first = (await client.SendAsync(Request("/open", null))).Headers.GetValues(Header).Single();
        var second = (await client.SendAsync(Request("/open", null))).Headers.GetValues(Header).Single();

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/open")]
    [InlineData("/me")] // 401: il middleware gira anche quando l'autorizzazione rifiuta
    [InlineData("/non-esiste")] // 404
    public async Task Header_IsReturnedForAnyEndpoint(string path)
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        using var client = app.GetTestClient();
        var id = Guid.NewGuid();

        var response = await client.SendAsync(Request(path, id.ToString()));

        Assert.Equal(id.ToString(), Assert.Single(response.Headers.GetValues(Header)));
    }
}
