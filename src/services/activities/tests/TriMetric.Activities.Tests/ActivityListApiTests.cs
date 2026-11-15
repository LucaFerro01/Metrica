using System.Net;
using System.Text.Json;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Activities.Tests;

/// <summary>
/// <c>GET /activities</c> (T13; REQ-018, REQ-022; AC-010): paginazione totale e stabile, filtro per sport, validazione dei
/// parametri con 400. I dati si inseriscono direttamente nel database con utenti nuovi per ogni test.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Api")]
public sealed class ActivityListApiTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly DateTimeOffset Base = new(2026, 1, 10, 7, 30, 0, TimeSpan.Zero);
    private static readonly Sport[] Sports = [Sport.Run, Sport.Ride, Sport.Swim, Sport.Other];

    /// <summary>45 attivita': a gruppi di tre con lo stesso inizio (i pari si ordinano per id, anche a cavallo delle pagine).</summary>
    private static SeedActivity[] FortyFive(Guid user) =>
        [.. Enumerable.Range(0, 45).Select(i => SeedActivity.New(user, Sports[i % 4], Base.AddHours(i / 3)))];

    [Fact]
    public async Task Pagination_45Activities_PageSize10_EveryPageInOrder_WithoutDuplicatesOrGaps()
    {
        var user = Guid.NewGuid();
        var seeded = FortyFive(user);
        await ApiData.InsertAsync(db, seeded);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var seen = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await ApiData.ListAsync(client, $"?page={page}&pageSize=10");
            Assert.Equal(page, result.Page);
            Assert.Equal(10, result.PageSize);
            Assert.Equal(45, result.Total);
            Assert.Equal(page < 5 ? 10 : 5, result.Items.Count);
            seen.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(ApiData.ExpectedOrder(seeded), seen);   // stesso insieme, nessun doppione, ordine esatto

        var beyond = await ApiData.ListAsync(client, "?page=6&pageSize=10");
        Assert.Empty(beyond.Items);
        Assert.Equal(45, beyond.Total);
        Assert.Equal(6, beyond.Page);
    }

    [Fact]
    public async Task EqualStartTimes_AreOrderedById_AcrossPages()
    {
        var user = Guid.NewGuid();
        var seeded = Enumerable.Range(0, 7).Select(_ => SeedActivity.New(user, Sport.Run, Base)).ToArray();
        await ApiData.InsertAsync(db, seeded);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            seen.AddRange((await ApiData.ListAsync(client, $"?page={page}&pageSize=2")).Items.Select(i => i.Id));
        }

        Assert.Equal(seeded.Select(a => a.Id).OrderBy(id => id.ToString("D"), StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task Defaults_ArePage1AndPageSize20()
    {
        var user = Guid.NewGuid();
        var seeded = Enumerable.Range(0, 25).Select(i => SeedActivity.New(user, Sport.Ride, Base.AddMinutes(i))).ToArray();
        await ApiData.InsertAsync(db, seeded);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var result = await ApiData.ListAsync(client);

        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(25, result.Total);
        Assert.Equal(ApiData.ExpectedOrder(seeded).Take(20), result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task SportFilter_ReturnsOnlyThatSport_WithItsTotal_AndPagesWithoutDuplicates()
    {
        var user = Guid.NewGuid();
        var seeded = FortyFive(user);
        await ApiData.InsertAsync(db, seeded);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        foreach (var sport in Sports)
        {
            var text = SportConverter.ToText(sport);
            var expected = ApiData.ExpectedOrder(seeded.Where(a => a.Sport == sport));
            var seen = new List<Guid>();
            for (var page = 1; page <= 3; page++)
            {
                var result = await ApiData.ListAsync(client, $"?sport={text}&page={page}&pageSize=5");
                Assert.Equal(expected.Count, result.Total);
                Assert.All(result.Items, i => Assert.Equal(sport, i.Sport));
                seen.AddRange(result.Items.Select(i => i.Id));
            }

            Assert.Equal(expected, seen);
        }
    }

    [Fact]
    public async Task Items_HaveExactlyTheContractFields_SportLowercase_AndStartTimeInUtc()
    {
        var user = Guid.NewGuid();
        var withNull = SeedActivity.New(user, Sport.Swim, new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_560))
            with { ElevationGainMeters = null, DistanceMeters = 1500.5, ElapsedSeconds = 1800.25 };
        await ApiData.InsertAsync(db, withNull);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var body = await client.GetStringAsync("/activities");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(["items", "page", "pageSize", "total"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["distanceMeters", "elapsedSeconds", "elevationGainMeters", "externalId", "id", "sport", "startTime"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("swim", item.GetProperty("sport").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("elevationGainMeters").ValueKind);
        Assert.Equal(withNull.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(withNull.ExternalId, item.GetProperty("externalId").GetString());
        Assert.Equal(1500.5, item.GetProperty("distanceMeters").GetDouble());
        Assert.Equal(1800.25, item.GetProperty("elapsedSeconds").GetDouble());

        var startText = item.GetProperty("startTime").GetString()!;
        var start = DateTimeOffset.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, start.Offset);                       // istante in UTC
        Assert.Equal(withNull.StartTime.UtcTicks, start.UtcTicks);       // stesso istante, al microsecondo
        Assert.True(startText.EndsWith('Z') || startText.EndsWith("+00:00", StringComparison.Ordinal), startText);
    }

    [Fact]
    public async Task VeryLargePage_ReturnsEmptyItems_AndTheTotal()
    {
        var user = Guid.NewGuid();
        await ApiData.InsertAsync(db, SeedActivity.New(user, Sport.Run, Base));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var result = await ApiData.ListAsync(client, $"?page={int.MaxValue}&pageSize=100");

        Assert.Empty(result.Items);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task UserWithoutActivities_GetsAnEmptyPage()
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        var result = await ApiData.ListAsync(client);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?page=abc")]
    [InlineData("?page=1.5")]
    [InlineData("?page=")]
    [InlineData("?page=1&page=2")]
    [InlineData("?page=99999999999")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=abc")]
    [InlineData("?pageSize=-5")]
    [InlineData("?sport=Run")]
    [InlineData("?sport=RUN")]
    [InlineData("?sport=bike")]
    [InlineData("?sport=")]
    [InlineData("?sport=0")]
    [InlineData("?sport=run&sport=ride")]
    public async Task InvalidQuery_Returns400ProblemDetails_NotASilentCorrection(string query)
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        await ApiData.AssertProblemAsync(await client.GetAsync("/activities" + query), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("?pageSize=1", 1)]
    [InlineData("?pageSize=100", 100)]
    [InlineData("?sport=other&page=1&pageSize=20", 20)]
    public async Task BoundaryQuery_IsAccepted(string query, int pageSize)
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        Assert.Equal(pageSize, (await ApiData.ListAsync(client, query)).PageSize);
    }
}
