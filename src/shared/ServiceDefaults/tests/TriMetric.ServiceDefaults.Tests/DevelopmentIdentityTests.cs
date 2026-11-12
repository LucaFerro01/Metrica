using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TriMetric.ServiceDefaults.Tests;

public sealed class DevelopmentIdentityTests
{
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Development_ProtectedEndpoint_ReturnsConfiguredUser()
    {
        await using var app = await TestApp.StartAsync("Development", TestApp.WithDevUser());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestApp.DevUserId.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Development_UserIdFromRequest_DoesNotChangeIdentity()
    {
        await using var app = await TestApp.StartAsync("Development", TestApp.WithDevUser());
        using var client = app.GetTestClient();

        var query = await client.GetStringAsync($"/me?userId={Other}&sub={Other}");

        using var withHeader = new HttpRequestMessage(HttpMethod.Get, "/me");
        withHeader.Headers.Add("X-User-Id", Other.ToString());
        var header = await (await client.SendAsync(withHeader)).Content.ReadAsStringAsync();

        var body = await (await client.PostAsJsonAsync("/me", new { userId = Other, sub = Other }))
            .Content.ReadAsStringAsync();

        Assert.Equal(TestApp.DevUserId.ToString(), query);
        Assert.Equal(TestApp.DevUserId.ToString(), header);
        Assert.Equal(TestApp.DevUserId.ToString(), body);
    }

    [Fact]
    public async Task Development_RegistersDevelopmentIdentityAsDefaultScheme()
    {
        await using var app = await TestApp.StartAsync("Development", TestApp.WithDevUser());
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync("DevelopmentIdentity"));
        Assert.Null(await schemes.GetSchemeAsync("Deny"));
        Assert.Equal("DevelopmentIdentity", (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task NonDevelopment_ProtectedEndpoint_Returns401_NotAuthenticated(string environment)
    {
        // Anche con Dev:UserId configurato: fuori da Development non deve avere alcun effetto.
        // Se qualcuno rende DevelopmentIdentity incondizionata, queste richieste diventano 200 e il test fallisce.
        await using var app = await TestApp.StartAsync(environment, TestApp.WithDevUser());
        using var client = app.GetTestClient();

        var get = await client.GetAsync("/me");
        var spoofed = await client.GetAsync($"/me?userId={Other}");
        using var withHeader = new HttpRequestMessage(HttpMethod.Get, "/me");
        withHeader.Headers.Add("X-User-Id", Other.ToString());
        var header = await client.SendAsync(withHeader);
        var post = await client.PostAsJsonAsync("/me", new { userId = Other });

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, spoofed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, header.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.DoesNotContain(TestApp.DevUserId.ToString(), await get.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopment_WithoutDevUserId_StartsAndReturns401(string environment)
    {
        await using var app = await TestApp.StartAsync(environment);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopment_DevelopmentIdentitySchemeIsNotRegistered_AndDenyIsDefault(string environment)
    {
        await using var app = await TestApp.StartAsync(environment, TestApp.WithDevUser());
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync("DevelopmentIdentity"));
        Assert.Equal("Deny", (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal("Deny", (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Equal("Deny", (await schemes.GetDefaultForbidSchemeAsync())?.Name);
    }

    [Fact]
    public async Task NonDevelopment_UnprotectedEndpoint_StaysReachable()
    {
        await using var app = await TestApp.StartAsync("Production");
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/open")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-un-uuid")]
    [InlineData("12345")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Development_MissingOrInvalidDevUserId_FailsStartup(string? value)
    {
        var configuration = new Dictionary<string, string?> { ["Dev:UserId"] = value };

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            var builder = TestApp.CreateBuilder("Development", configuration);
            builder.AddDevelopmentIdentity();
        });

        Assert.Contains("Dev:UserId", ex.Message);
    }

    [Fact]
    public void Development_WithoutAnyConfiguration_FailsStartup()
    {
        var builder = TestApp.CreateBuilder("Development");

        Assert.Throws<InvalidOperationException>(() => builder.AddDevelopmentIdentity());
    }

    [Fact]
    public void NonDevelopment_InvalidDevUserId_DoesNotFail()
    {
        var builder = TestApp.CreateBuilder("Production", new Dictionary<string, string?> { ["Dev:UserId"] = "xx" });

        builder.AddDevelopmentIdentity();
    }
}
