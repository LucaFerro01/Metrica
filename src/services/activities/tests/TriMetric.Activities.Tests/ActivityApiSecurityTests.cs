using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Activities.Tests;

/// <summary>
/// Confini delle API di <c>activities</c> (T13): isolamento tra utenti (REQ-022, AC-007), identita' solo dal principal e 401
/// fuori da Development (REQ-021, AC-009, DEC-006), documento OpenAPI con nomi stabili (REQ-019, DEC-007) e log senza dati
/// delle attivita' (REQ-034).
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Api")]
public sealed partial class ActivityApiSecurityTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly DateTimeOffset Base = new(2026, 2, 1, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoUsers_EachSeesOnlyTheirOwnActivities_AndTheOtherUsersDetailIs404LikeAMissingId()
    {
        var (alice, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var aliceActivities = Enumerable.Range(0, 3).Select(i => SeedActivity.New(alice, Sport.Run, Base.AddHours(i), StreamTestData.Build(5))).ToArray();
        var bobActivities = Enumerable.Range(0, 4).Select(i => SeedActivity.New(bob, Sport.Run, Base.AddHours(i), StreamTestData.Build(5))).ToArray();
        await ApiData.InsertAsync(db, [.. aliceActivities, .. bobActivities]);
        await using var aliceHost = ActivitiesHost.Development(db, nats, userId: alice);
        await using var bobHost = ActivitiesHost.Development(db, nats, userId: bob);
        using var aliceClient = aliceHost.Start();
        using var bobClient = bobHost.Start();

        var aliceList = await ApiData.ListAsync(aliceClient, "?pageSize=100");
        var bobList = await ApiData.ListAsync(bobClient, "?pageSize=100&sport=run");
        Assert.Equal(ApiData.ExpectedOrder(aliceActivities), aliceList.Items.Select(i => i.Id));
        Assert.Equal(3, aliceList.Total);
        Assert.Equal(ApiData.ExpectedOrder(bobActivities), bobList.Items.Select(i => i.Id));
        Assert.Equal(4, bobList.Total);

        // Il proprio dettaglio e' 200; quello dell'altro e' 404, indistinguibile da un id inesistente (stato, tipo e corpo).
        Assert.Equal(aliceActivities[0].Id, (await ApiData.DetailAsync(aliceClient, aliceActivities[0].Id)).Id);
        var foreign = await aliceClient.GetAsync($"/activities/{bobActivities[0].Id}");
        var missing = await aliceClient.GetAsync($"/activities/{Guid.NewGuid()}");
        var foreignWithMaxPoints = await aliceClient.GetAsync($"/activities/{bobActivities[0].Id}?maxPoints=2");
        var foreignBody = await ApiData.AssertProblemAsync(foreign, HttpStatusCode.NotFound);
        var missingBody = await ApiData.AssertProblemAsync(missing, HttpStatusCode.NotFound);
        Assert.Equal(missingBody, foreignBody);
        Assert.Equal(missingBody, await ApiData.AssertProblemAsync(foreignWithMaxPoints, HttpStatusCode.NotFound));
        Assert.Equal(
            missing.Headers.Select(h => h.Key).Where(k => k != "X-Correlation-Id").Order(),
            foreign.Headers.Select(h => h.Key).Where(k => k != "X-Correlation-Id").Order());
    }

    [Fact]
    public async Task UserIdInQueryOrHeader_IsIgnored()
    {
        var (alice, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var aliceActivity = SeedActivity.New(alice, Sport.Ride, Base);
        var bobActivity = SeedActivity.New(bob, Sport.Ride, Base);
        await ApiData.InsertAsync(db, aliceActivity, bobActivity);
        await using var host = ActivitiesHost.Development(db, nats, userId: alice);
        using var client = host.Start();

        foreach (var request in new[]
        {
            ApiData.Get($"/activities?userId={bob}"),
            ApiData.Get($"/activities?user_id={bob}&UserId={bob}"),
            ApiData.Get("/activities", ("X-User-Id", bob.ToString())),
            ApiData.Get($"/activities?userId={bob}", ("X-User-Id", bob.ToString()), ("X-Forwarded-User", bob.ToString())),
        })
        {
            using var response = await client.SendAsync(request);
            var page = JsonSerializer.Deserialize<ActivityListPage>(await response.Content.ReadAsStringAsync(), ApiData.Json)!;
            Assert.Equal(aliceActivity.Id, Assert.Single(page.Items).Id);
            Assert.Equal(1, page.Total);
        }

        using var detail = await client.SendAsync(ApiData.Get($"/activities/{bobActivity.Id}?userId={bob}", ("X-User-Id", bob.ToString())));
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task Production_WithoutIdentity_Returns401_WithoutData()
    {
        var user = Guid.NewGuid();
        var activity = SeedActivity.New(user, Sport.Run, Base, StreamTestData.Build(5));
        await ApiData.InsertAsync(db, activity);
        await using var host = new ActivitiesHost("Production", db.ConnectionStringFor("activities"), nats.Url);
        using var client = host.Start();

        foreach (var path in new[] { "/activities", "/activities?page=1&pageSize=10", $"/activities/{activity.Id}", $"/activities/{activity.Id}?maxPoints=2" })
        {
            using var response = await client.SendAsync(ApiData.Get(path, ("X-User-Id", user.ToString())));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain(activity.ExternalId, body);
            Assert.DoesNotContain(activity.Id.ToString(), body);
            Assert.Equal(string.Empty, body);
        }
    }

    [Fact]
    public async Task EveryActivitiesEndpoint_RequiresAuthorization()
    {
        // Gli handler rispondono 401 anche da soli se il principal non ha un utente valido: senza questo controllo un endpoint
        // senza RequireAuthorization() passerebbe inosservato (DEC-006 lo richiede su ogni endpoint).
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        host.Start();

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/activities", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(["/activities", "/activities/{id:guid}"], endpoints.Select(e => e.RoutePattern.RawText).Order(StringComparer.Ordinal));
        Assert.All(endpoints, e => Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>()));
        Assert.All(endpoints, e => Assert.Empty(e.Metadata.GetOrderedMetadata<IAllowAnonymous>()));
    }

    [Fact]
    public async Task Development_WithIdentity_Returns200()
    {
        var user = Guid.NewGuid();
        var activity = SeedActivity.New(user, Sport.Run, Base);
        await ApiData.InsertAsync(db, activity);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/activities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/activities/{activity.Id}")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_HasStableSchemaNames_OperationIds_LowercaseSport_AndDocumentedResponses()
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var names = schemas.EnumerateObject().Select(p => p.Name).ToList();

        foreach (var name in new[] { "ActivityListItem", "ActivityDetail", "StreamColumns", "ActivityListPage", "ActivitySummary", "LapData", "Sport", "ProblemDetails" })
        {
            Assert.Contains(name, names);
        }

        Assert.DoesNotContain(names, n => TrailingDigits().IsMatch(n) || n.EndsWith("Dto", StringComparison.OrdinalIgnoreCase));

        var sport = schemas.GetProperty("Sport");
        Assert.Equal("string", sport.GetProperty("type").GetString());
        Assert.Equal(["run", "ride", "swim", "other"], sport.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("#/components/schemas/Sport", schemas.GetProperty("ActivityListItem").GetProperty("properties").GetProperty("sport").GetRawText());
        Assert.Contains("#/components/schemas/StreamColumns", schemas.GetProperty("ActivityDetail").GetProperty("properties").GetProperty("stream").GetRawText());
        Assert.Contains("#/components/schemas/ActivityListItem", schemas.GetProperty("ActivityListPage").GetProperty("properties").GetProperty("items").GetRawText());
        // Numeri solo come numeri (nessun "number | string" nel client generato).
        var item = schemas.GetProperty("ActivityListItem").GetProperty("properties");
        Assert.Equal("number", item.GetProperty("distanceMeters").GetProperty("type").GetString());
        Assert.Equal(["null", "number"], item.GetProperty("elevationGainMeters").GetProperty("type").EnumerateArray().Select(t => t.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("integer", schemas.GetProperty("ActivityListPage").GetProperty("properties").GetProperty("total").GetProperty("type").GetString());
        Assert.Equal("date-time", item.GetProperty("startTime").GetProperty("format").GetString());
        foreach (var schema in new[] { "ActivityListItem", "ActivityDetail" })
        {
            var properties = schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            Assert.DoesNotContain("userId", properties);
            Assert.DoesNotContain("source", properties);
        }

        var list = root.GetProperty("paths").GetProperty("/activities").GetProperty("get");
        var detail = root.GetProperty("paths").GetProperty("/activities/{id}").GetProperty("get");
        Assert.Equal("ListActivities", list.GetProperty("operationId").GetString());
        Assert.Equal("GetActivity", detail.GetProperty("operationId").GetString());
        AssertResponses(list, "200", "400", "401");
        AssertResponses(detail, "200", "400", "401", "404");
        Assert.Contains("ActivityListPage", list.GetProperty("responses").GetProperty("200").GetRawText());
        Assert.Contains("ActivityDetail", detail.GetProperty("responses").GetProperty("200").GetRawText());
        Assert.Contains("ProblemDetails", detail.GetProperty("responses").GetProperty("404").GetRawText());

        // Parametri: tipi corretti per il client generato e nessun parametro di identita'.
        var listParameters = Parameters(list);
        Assert.Equal(["page", "pageSize", "sport"], listParameters.Keys.Order(StringComparer.Ordinal));
        AssertInteger(listParameters["page"], minimum: 1, maximum: null, @default: 1);
        AssertInteger(listParameters["pageSize"], minimum: 1, maximum: 100, @default: 20);
        Assert.Contains("#/components/schemas/Sport", listParameters["sport"].GetRawText());
        var detailParameters = Parameters(detail);
        Assert.Equal(["id", "maxPoints"], detailParameters.Keys.Order(StringComparer.Ordinal));
        AssertInteger(detailParameters["maxPoints"], minimum: 2, maximum: 5000, @default: 1000);

        foreach (var operation in new[] { list, detail })
        {
            foreach (var parameter in operation.GetProperty("parameters").EnumerateArray())
            {
                Assert.DoesNotContain("user", parameter.GetProperty("name").GetString()!, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual("header", parameter.GetProperty("in").GetString());
            }
        }

        Assert.DoesNotContain("userid", root.GetProperty("paths").GetRawText(), StringComparison.OrdinalIgnoreCase);

        // required e nullabilita' come le leggera' il client TypeScript generato (openapi-typescript): un campo non required diventa
        // `campo?: T` (controlli di undefined ovunque nel web); un tipo senza "null" diventa non annullabile (e si rompe a runtime).
        AssertRequired(schemas, "ActivityListItem", "id", "externalId", "sport", "startTime", "distanceMeters", "elapsedSeconds", "elevationGainMeters");
        AssertRequired(schemas, "ActivityDetail", "id", "externalId", "sport", "startTime", "distanceMeters", "elapsedSeconds", "elevationGainMeters", "movingSeconds", "summary", "laps", "stream");
        AssertRequired(schemas, "ActivityListPage", "items", "page", "pageSize", "total");
        AssertRequired(schemas, "ActivitySummary", "avgHeartRate", "maxHeartRate", "avgPowerWatts", "avgCadence");
        AssertRequired(schemas, "LapData", "startTime", "elapsedSeconds", "distanceMeters");
        AssertRequired(schemas, "StreamColumns", "timeOffsetSeconds", "latitude", "longitude", "altitudeMeters", "heartRate", "powerWatts", "cadence", "speedMetersPerSecond");

        foreach (var (schema, field) in new[]
        {
            ("ActivityListItem", "elevationGainMeters"), ("ActivityDetail", "elevationGainMeters"),
            ("ActivitySummary", "avgHeartRate"), ("ActivitySummary", "maxHeartRate"), ("ActivitySummary", "avgPowerWatts"), ("ActivitySummary", "avgCadence"),
        })
        {
            Assert.Contains("null", TypeNames(schemas.GetProperty(schema).GetProperty("properties").GetProperty(field)));
        }

        foreach (var column in new[] { "latitude", "longitude", "altitudeMeters", "heartRate", "powerWatts", "cadence", "speedMetersPerSecond" })
        {
            var property = schemas.GetProperty("StreamColumns").GetProperty("properties").GetProperty(column);
            Assert.Contains("null", TypeNames(property));    // la colonna puo' mancare del tutto...
            Assert.Contains("array", TypeNames(property));
            Assert.Contains("null", TypeNames(property.GetProperty("items")));   // ...e un valore dentro la colonna puo' essere null
        }

        var offsets = schemas.GetProperty("StreamColumns").GetProperty("properties").GetProperty("timeOffsetSeconds");
        Assert.Equal(["array"], TypeNames(offsets));   // mai null
        Assert.Equal(["number"], TypeNames(offsets.GetProperty("items")));
        foreach (var (schema, field) in new[]
        {
            ("ActivityListItem", "externalId"), ("ActivityDetail", "externalId"), ("ActivityDetail", "laps"),
            ("ActivityDetail", "movingSeconds"), ("ActivityListPage", "items"),
        })
        {
            Assert.DoesNotContain("null", TypeNames(schemas.GetProperty(schema).GetProperty("properties").GetProperty(field)));
        }

        foreach (var (schema, field) in new[] { ("ActivityDetail", "summary"), ("ActivityDetail", "stream") })
        {
            Assert.Contains("$ref", schemas.GetProperty(schema).GetProperty("properties").GetProperty(field).EnumerateObject().Select(p => p.Name));
        }
    }

    private static void AssertRequired(JsonElement schemas, string schema, params string[] expected)
    {
        var node = schemas.GetProperty(schema);
        Assert.True(node.TryGetProperty("required", out var required), $"{schema}: manca 'required'");
        Assert.Equal(expected.Order(StringComparer.Ordinal), required.EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            expected.Order(StringComparer.Ordinal));   // ogni proprieta' e' required: nessun campo opzionale nel contratto
    }

    /// <summary>Nomi di tipo di uno schema: <c>type</c> puo' essere una stringa o un elenco (es. <c>["null","number"]</c>); senza <c>type</c>, vuoto.</summary>
    private static List<string> TypeNames(JsonElement schema) =>
        !schema.TryGetProperty("type", out var type)
            ? []
            : type.ValueKind == JsonValueKind.Array
                ? [.. type.EnumerateArray().Select(t => t.GetString()!)]
                : [type.GetString()!];

    [Fact]
    public async Task Logs_ContainNoActivityData_ForListAndDetailRequests()
    {
        const string marker = "zqxapiprivacymarker";
        const double latitude = 47.3769123;
        const double longitude = 8.5417456;
        const double heartRate = 213.4375;
        const double power = 4739.25;
        var user = Guid.NewGuid();
        var stream = StreamTestData.Build(30, allColumns: true) with
        {
            Latitude = Enumerable.Repeat<double?>(latitude, 30).ToArray(),
            Longitude = Enumerable.Repeat<double?>(longitude, 30).ToArray(),
            HeartRate = Enumerable.Repeat<double?>(heartRate, 30).ToArray(),
            PowerWatts = Enumerable.Repeat<double?>(power, 30).ToArray(),
        };
        var activity = SeedActivity.New(user, Sport.Ride, Base, stream) with { ExternalId = $"{marker}ext" };
        await ApiData.InsertAsync(db, activity);
        var logs = new LogCapture();
        await using var host = ActivitiesHost.Development(
            db,
            nats,
            userId: user,
            startupSettings: new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace",
            },
            logs: logs);
        using var client = host.Start();

        var listBody = await client.GetStringAsync("/activities?sport=ride");
        var detailBody = await client.GetStringAsync($"/activities/{activity.Id}?maxPoints=10");
        await client.GetAsync($"/activities/{Guid.NewGuid()}");
        await client.GetAsync("/activities?page=abc");

        // Controllo di sanita': i valori ci sono nelle risposte, quindi la loro assenza dai log non e' casuale.
        Assert.Contains(marker, listBody);
        Assert.Contains("47.3769123", detailBody);
        Assert.Contains("4739.25", detailBody);

        var entries = logs.Entries.ToArray();
        Assert.Contains(entries, e => e.Level <= LogLevel.Debug);   // la cattura funziona davvero a Trace/Debug
        Assert.Contains(entries, e => e.Category.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal));
        var all = string.Join('\n', entries.Select(e => e.Text));
        foreach (var secret in new[] { marker, "47.3769123", "8.5417456", "213.4375", "4739.25" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, JsonElement> Parameters(JsonElement operation) =>
        operation.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p);

    private static void AssertResponses(JsonElement operation, params string[] statuses)
    {
        var responses = operation.GetProperty("responses");
        foreach (var status in statuses)
        {
            Assert.True(responses.TryGetProperty(status, out _), $"manca la risposta {status} in {operation.GetProperty("operationId")}");
        }
    }

    private static void AssertInteger(JsonElement parameter, int minimum, int? maximum, int @default)
    {
        var schema = parameter.GetProperty("schema");
        Assert.Contains("integer", schema.GetProperty("type").GetRawText());
        Assert.Equal(minimum, schema.GetProperty("minimum").GetInt32());
        if (maximum is { } max)
        {
            Assert.Equal(max, schema.GetProperty("maximum").GetInt32());
        }

        Assert.Equal(@default, schema.GetProperty("default").GetInt32());
    }

    [GeneratedRegex(@"\d+$")]
    private static partial Regex TrailingDigits();
}
