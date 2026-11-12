using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace TriMetric.ServiceDefaults.Tests;

public sealed class HealthTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Live_Returns200(string environment)
    {
        await using var app = await TestApp.StartAsync(environment, TestApp.WithDevUser());
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Ready_WithHealthyChecks_Returns200(string environment)
    {
        await using var app = await TestApp.StartAsync(environment, TestApp.WithDevUser(), b =>
            b.Services.AddHealthChecks().AddCheck("db", () => HealthCheckResult.Healthy(), tags: ["ready"]));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Ready_WithoutChecks_Returns200()
    {
        await using var app = await TestApp.StartAsync("Production");
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Ready_WithUnhealthyReadyCheck_Returns503_ButLiveStays200()
    {
        await using var app = await TestApp.StartAsync("Production", configure: b =>
            b.Services.AddHealthChecks().AddCheck("db", () => HealthCheckResult.Unhealthy("down"), tags: ["ready"]));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Ready_IgnoresUnhealthyChecksWithoutReadyTag()
    {
        await using var app = await TestApp.StartAsync("Production", configure: b =>
            b.Services.AddHealthChecks().AddCheck("other", () => HealthCheckResult.Unhealthy("down")));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
