using System.Net;
using System.Text.Json;
using Metrica.Contracts;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>
/// Filtro <c>externalId</c> di <c>GET /activities</c> (DEC-017, prerequisito di T18): corrispondenza esatta e case-sensitive
/// solo tra le attivita' dell'utente del principal (DEC-006), combinabile con <c>sport</c>, <c>page</c> e <c>pageSize</c>,
/// validato a mano come gli altri parametri (400 <c>invalid-query</c>, mai il valore ricevuto nella risposta o nei log) e
/// parametrizzato (nessun carattere speciale e' un jolly o rompe la query). I dati si inseriscono direttamente nel database
/// con utenti nuovi per ogni test.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Api")]
public sealed class ActivityExternalIdFilterTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly DateTimeOffset Base = new(2026, 4, 5, 8, 0, 0, TimeSpan.Zero);

    private static string Q(string value) => "?externalId=" + Uri.EscapeDataString(value);

    private static SeedActivity With(Guid user, string externalId, Sport sport = Sport.Run, int hours = 0, string source = "file") =>
        SeedActivity.New(user, sport, Base.AddHours(hours)) with { ExternalId = externalId, Source = source };

    [Fact]
    public async Task ExactMatch_ReturnsOnlyTheRightActivity_WithTotal1()
    {
        var user = Guid.NewGuid();
        var wanted = With(user, "a1b2c3", hours: 0);
        var other = With(user, "d4e5f6", hours: 1);
        await ApiData.InsertAsync(db, wanted, other);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var result = await ApiData.ListAsync(client, Q("a1b2c3"));

        var item = Assert.Single(result.Items);
        Assert.Equal(wanted.Id, item.Id);
        Assert.Equal("a1b2c3", item.ExternalId);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);

        var all = await ApiData.ListAsync(client);   // senza il parametro: nessun filtro
        Assert.Equal(2, all.Total);
    }

    [Fact]
    public async Task NoMatch_Returns200WithEmptyItems_AndTotal0_NotA404()
    {
        var user = Guid.NewGuid();
        await ApiData.InsertAsync(db, With(user, "present"));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        using var response = await client.GetAsync("/activities" + Q("absent"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = JsonSerializer.Deserialize<ActivityListPage>(body, ApiData.Json)!;
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(["items", "page", "pageSize", "total"], document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TwoUsersWithTheSameExternalId_EachSeesOnlyTheirOwn_AndNeverTheOthersOnlyOne()
    {
        var (alice, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var aliceShared = With(alice, "shared-id", hours: 0);
        var bobShared = With(bob, "shared-id", hours: 0);   // permesso dall'unicita' (user_id, source, external_id)
        var onlyBob = With(bob, "only-bob", hours: 1);
        var onlyAlice = With(alice, "only-alice", hours: 1);
        await ApiData.InsertAsync(db, aliceShared, bobShared, onlyBob, onlyAlice);
        await using var aliceHost = ActivitiesHost.Development(db, nats, userId: alice);
        await using var bobHost = ActivitiesHost.Development(db, nats, userId: bob);
        using var aliceClient = aliceHost.Start();
        using var bobClient = bobHost.Start();

        var aliceResult = await ApiData.ListAsync(aliceClient, Q("shared-id"));
        var bobResult = await ApiData.ListAsync(bobClient, Q("shared-id"));
        Assert.Equal(aliceShared.Id, Assert.Single(aliceResult.Items).Id);
        Assert.Equal(1, aliceResult.Total);
        Assert.Equal(bobShared.Id, Assert.Single(bobResult.Items).Id);
        Assert.Equal(1, bobResult.Total);

        // L'identificativo che esiste solo per l'altro utente: vuoto, con total 0 (indistinguibile da uno inesistente).
        var aliceLooksForBobs = await ApiData.ListAsync(aliceClient, Q("only-bob"));
        var bobLooksForAlices = await ApiData.ListAsync(bobClient, Q("only-alice"));
        Assert.Empty(aliceLooksForBobs.Items);
        Assert.Equal(0, aliceLooksForBobs.Total);
        Assert.Empty(bobLooksForAlices.Items);
        Assert.Equal(0, bobLooksForAlices.Total);
    }

    [Fact]
    public async Task HostileUserIdInQueryOrHeader_DoesNotChangeTheFilteredResult()
    {
        var (alice, bob) = (Guid.NewGuid(), Guid.NewGuid());
        var aliceShared = With(alice, "shared-id");
        var bobShared = With(bob, "shared-id");
        var onlyBob = With(bob, "only-bob", hours: 1);
        await ApiData.InsertAsync(db, aliceShared, bobShared, onlyBob);
        await using var host = ActivitiesHost.Development(db, nats, userId: alice);
        using var client = host.Start();

        foreach (var request in new[]
        {
            ApiData.Get($"/activities{Q("shared-id")}&userId={bob}"),
            ApiData.Get($"/activities{Q("shared-id")}&user_id={bob}&UserId={bob}"),
            ApiData.Get("/activities" + Q("shared-id"), ("X-User-Id", bob.ToString())),
            ApiData.Get($"/activities{Q("shared-id")}&userId={bob}", ("X-User-Id", bob.ToString()), ("X-Forwarded-User", bob.ToString())),
        })
        {
            using var response = await client.SendAsync(request);
            var page = JsonSerializer.Deserialize<ActivityListPage>(await response.Content.ReadAsStringAsync(), ApiData.Json)!;
            Assert.Equal(aliceShared.Id, Assert.Single(page.Items).Id);
            Assert.Equal(1, page.Total);
        }

        foreach (var request in new[]
        {
            ApiData.Get($"/activities{Q("only-bob")}&userId={bob}", ("X-User-Id", bob.ToString())),
            ApiData.Get("/activities" + Q("only-bob"), ("X-User-Id", bob.ToString())),
        })
        {
            using var response = await client.SendAsync(request);
            var page = JsonSerializer.Deserialize<ActivityListPage>(await response.Content.ReadAsStringAsync(), ApiData.Json)!;
            Assert.Empty(page.Items);
            Assert.Equal(0, page.Total);
        }
    }

    [Fact]
    public async Task CombinedWithSportPageAndPageSize_TotalCountsTheActivitiesMatchingEveryFilter()
    {
        // Lo stesso identificativo con fonti diverse a livello di dati (l'unicita' e' per utente, fonte e identificativo):
        // il filtro per solo externalId puo' restituire piu' righe (DEC-017), quindi la paginazione e' osservabile.
        var user = Guid.NewGuid();
        var matching = new[]
        {
            With(user, "dup", Sport.Run, hours: 0, source: "file"),
            With(user, "dup", Sport.Ride, hours: 1, source: "strava"),
            With(user, "dup", Sport.Run, hours: 2, source: "garmin"),
            With(user, "dup", Sport.Run, hours: 3, source: "other-source"),
            With(user, "dup", Sport.Swim, hours: 4, source: "polar"),
        };
        var others = new[]
        {
            With(user, "not-dup", Sport.Run, hours: 5),
            With(user, "dup2", Sport.Run, hours: 6),
            With(user, "Dup", Sport.Run, hours: 7),
        };
        await ApiData.InsertAsync(db, [.. matching, .. others]);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        // Solo externalId: tutte e cinque le righe, in ordine, su piu' pagine senza doppioni ne' buchi.
        var expectedAll = ApiData.ExpectedOrder(matching);
        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await ApiData.ListAsync(client, $"{Q("dup")}&page={page}&pageSize=2");
            Assert.Equal(5, result.Total);
            Assert.Equal(page, result.Page);
            Assert.Equal(2, result.PageSize);
            Assert.Equal(page < 3 ? 2 : 1, result.Items.Count);
            Assert.All(result.Items, i => Assert.Equal("dup", i.ExternalId));
            seen.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(expectedAll, seen);
        var beyond = await ApiData.ListAsync(client, $"{Q("dup")}&page=4&pageSize=2");
        Assert.Empty(beyond.Items);
        Assert.Equal(5, beyond.Total);

        // Con sport: total e pagine contano solo le righe che rispettano ENTRAMBI i filtri.
        var runs = matching.Where(a => a.Sport == Sport.Run).ToArray();
        var runSeen = new List<Guid>();
        for (var page = 1; page <= 2; page++)
        {
            var result = await ApiData.ListAsync(client, $"{Q("dup")}&sport=run&page={page}&pageSize=2");
            Assert.Equal(3, result.Total);
            runSeen.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(ApiData.ExpectedOrder(runs), runSeen);
        Assert.Equal(1, (await ApiData.ListAsync(client, $"{Q("dup")}&sport=ride")).Total);

        // Filtro che non corrisponde allo sport dell'attivita': vuoto, total 0 (anche se l'identificativo esiste per altri sport).
        var none = await ApiData.ListAsync(client, $"{Q("not-dup")}&sport=swim");
        Assert.Empty(none.Items);
        Assert.Equal(0, none.Total);
        Assert.Equal(1, (await ApiData.ListAsync(client, "?sport=run&externalId=not-dup")).Total);   // l'ordine dei parametri non conta
    }

    [Fact]
    public async Task Match_IsCaseSensitive_UppercaseDoesNotMatchLowercase()
    {
        var user = Guid.NewGuid();
        var lower = With(user, "abcdef0123456789");
        await ApiData.InsertAsync(db, lower);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        Assert.Equal(lower.Id, Assert.Single((await ApiData.ListAsync(client, Q("abcdef0123456789"))).Items).Id);
        var upper = await ApiData.ListAsync(client, Q("ABCDEF0123456789"));
        Assert.Empty(upper.Items);
        Assert.Equal(0, upper.Total);
        Assert.Equal(0, (await ApiData.ListAsync(client, Q("AbCdEf0123456789"))).Total);
    }

    private static readonly string[] SpecialValues =
    [
        "a'c", "a%c", "a_c", "a;c", "a--c", @"a\c", "a,c", "a\"c", "a/*c*/", "a=c&d", "a+c", "à-ü",
        "a';DROP/**/TABLE/**/activities;--", "'/**/OR/**/'1'='1",
    ];

    [Fact]
    public async Task SpecialCharacters_MatchOnlyTheExactValue_AndNeverCauseSqlErrors()
    {
        // Un solo utente con tutti i valori speciali e alcuni vicini per cui un LIKE/ILIKE o una concatenazione darebbero una
        // corrispondenza spuria; ogni valore deve trovare esattamente la propria riga.
        var user = Guid.NewGuid();
        string[] neighbours = ["abc", "ac", "a", "aXc", "ABC", "A_C", "a-c"];
        var seeded = SpecialValues.Concat(neighbours).Distinct().Select((value, i) => With(user, value, hours: i)).ToArray();
        await ApiData.InsertAsync(db, seeded);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        foreach (var value in SpecialValues)
        {
            var result = await ApiData.ListAsync(client, Q(value));

            Assert.True(result.Total == 1 && result.Items.Count == 1, $"valore {value}: attesa 1 corrispondenza, trovate {result.Total}");
            Assert.True(result.Items[0].Id == seeded.Single(a => a.ExternalId == value).Id, $"valore {value}: riga sbagliata");
            Assert.Equal(value, result.Items[0].ExternalId);
        }
    }

    [Fact]
    public async Task PercentUnderscoreAndOtherSpecials_AreNotWildcards_NoSpuriousMatch()
    {
        var user = Guid.NewGuid();
        await ApiData.InsertAsync(db, With(user, "abc", hours: 0), With(user, "abd", hours: 1), With(user, "a-c", hours: 2), With(user, "xyz", hours: 3));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        foreach (var value in new[] { "%", "_", "a%", "%c", "_bc", "a_c", "%%", "'", "--", ";", @"\", "ABC" })
        {
            var result = await ApiData.ListAsync(client, Q(value));

            Assert.True(result.Total == 0 && result.Items.Count == 0, $"valore {value}: nessuna corrispondenza attesa, trovate {result.Total}");
        }
    }

    [Fact]
    public async Task Exactly128Characters_IsAccepted_AndMatches_AlsoWithNonAsciiBmpCharacters()
    {
        var user = Guid.NewGuid();
        var ascii128 = new string('a', 64) + new string('b', 64);
        var accented128 = new string('è', 128);   // 128 unita' UTF-16 (e 256 byte UTF-8)
        Assert.Equal(128, ascii128.Length);
        Assert.Equal(128, accented128.Length);
        var asciiRow = With(user, ascii128, hours: 0);
        var accentedRow = With(user, accented128, hours: 1);
        // Righe piu' lunghe (non raggiungibili col filtro): non devono comparire per prefisso.
        await ApiData.InsertAsync(db, asciiRow, accentedRow, With(user, ascii128 + "c", hours: 2), With(user, accented128 + "è", hours: 3));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var ascii = await ApiData.ListAsync(client, Q(ascii128));
        Assert.Equal(asciiRow.Id, Assert.Single(ascii.Items).Id);
        Assert.Equal(1, ascii.Total);
        var accented = await ApiData.ListAsync(client, Q(accented128));
        Assert.Equal(accentedRow.Id, Assert.Single(accented.Items).Id);
        Assert.Equal(1, accented.Total);

        using var accented129 = await client.GetAsync("/activities" + Q(accented128 + "è"));
        await AssertInvalidExternalIdAsync(accented129, "129 caratteri non ASCII (BMP)");
    }

    [Fact]
    public async Task ParameterName_IsCaseInsensitive_AndAPercentEncodedNameIsDecoded()
    {
        // Il nome del parametro segue le regole di ASP.NET Core: le maiuscole non contano e un nome codificato in percentuale
        // viene decodificato prima del confronto. Il filtro vale in ogni caso (nessun modo di aggirare la validazione), e
        // lo stesso nome ripetuto con maiuscole diverse e' una ripetizione (400).
        var user = Guid.NewGuid();
        var wanted = With(user, "a", hours: 0);
        await ApiData.InsertAsync(db, wanted, With(user, "b", hours: 1));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        foreach (var query in new[] { "?externalid=a", "?EXTERNALID=a", "?%65xternalId=a", "?externalI%64=a" })
        {
            var result = await ApiData.ListAsync(client, query);
            Assert.True(result.Total == 1 && result.Items.Count == 1 && result.Items[0].Id == wanted.Id, $"{query}: attesa solo la riga 'a', total {result.Total}");
        }

        foreach (var query in new[] { "?externalId=a&externalid=b", "?externalid=a&EXTERNALID=a", "?externalId=a&%65xternalId=a", "?externalid=", "?%65xternalId=" })
        {
            await AssertInvalidExternalIdAsync(await client.GetAsync("/activities" + query), query);
        }
    }

    [Fact]
    public async Task InvalidUtf8Escapes_AreKeptAsLiteralText_AndAreNeverA500()
    {
        // %FF e %C3 non sono UTF-8 valido: ASP.NET Core (Uri.UnescapeDataString) lascia la sequenza com'e' e il valore diventa il
        // testo letterale "%FF" (3 caratteri), accettato e confrontato come qualunque altro testo. Una sequenza valida
        // (%EF%BF%BD) e' invece decodificata in U+FFFD. Esito definito per tutti: 200, mai 500, ciascuno trova la propria riga.
        var user = Guid.NewGuid();
        var literalFf = With(user, "%FF", hours: 0);
        var literalC3 = With(user, "%C3", hours: 1);
        var replacement = With(user, "�", hours: 2);
        await ApiData.InsertAsync(db, literalFf, literalC3, replacement, With(user, "x", hours: 3));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        foreach (var (query, expected) in new[]
        {
            ("?externalId=%FF", literalFf), ("?externalId=%C3", literalC3), ("?externalId=%EF%BF%BD", replacement),
        })
        {
            using var response = await client.GetAsync("/activities" + query);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{query}: {(int)response.StatusCode} {body}");
            var page = JsonSerializer.Deserialize<ActivityListPage>(body, ApiData.Json)!;
            Assert.True(page.Total == 1 && page.Items[0].Id == expected.Id, $"{query}: total {page.Total}");
        }
    }


    [Fact]
    public async Task ExplicitComma_IsAllowed_AndIsNotSplitIntoTwoValues()
    {
        var user = Guid.NewGuid();
        await ApiData.InsertAsync(db, With(user, "a", hours: 0), With(user, "b", hours: 1));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        foreach (var query in new[] { "?externalId=a,b", "?externalId=a%2Cb", "?externalId=,", "?externalId=a,", "?externalId=,b" })
        {
            var result = await ApiData.ListAsync(client, query);
            Assert.True(result.Total == 0 && result.Items.Count == 0, $"{query}: nessuna corrispondenza attesa, total {result.Total}");
        }
    }

    private static readonly (string Description, string Query)[] InvalidQueries =
    [
        ("empty", "?externalId="),
        ("empty without equals sign", "?externalId"),
        ("empty with other parameters", "?sport=run&externalId=&page=1"),
        ("repeated", "?externalId=zqxone&externalId=zqxtwo"),
        ("repeated, same value", "?externalId=zqxone&externalId=zqxone"),
        ("repeated, one empty", "?externalId=zqxone&externalId="),
        ("129 characters", "?externalId=zqx" + new string('q', 126)),
        ("much longer", "?externalId=zqx" + new string('q', 5000)),
        ("space", "?externalId=zqxa%20b"),
        ("leading space", "?externalId=%20zqx"),
        ("trailing space", "?externalId=zqx%20"),
        ("only space", "?externalId=%20"),
        ("plus (a space)", "?externalId=zqxa+b"),
        ("tab", "?externalId=zqxa%09b"),
        ("line feed", "?externalId=zqxa%0Ab"),
        ("carriage return", "?externalId=zqxa%0Db"),
        ("NUL", "?externalId=zqxa%00b"),
        ("escape", "?externalId=zqxa%1Bb"),
        ("DEL", "?externalId=zqxa%7Fb"),
        ("C1 control character", "?externalId=zqxa%C2%85b"),
        ("non-breaking space", "?externalId=zqxa%C2%A0b"),
        ("line separator", "?externalId=zqxa%E2%80%A8b"),
        ("ideographic space", "?externalId=zqxa%E3%80%80b"),
    ];

    [Fact]
    public async Task InvalidExternalId_Returns400InvalidQuery_WithoutEchoingTheReceivedValue()
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        foreach (var (description, query) in InvalidQueries)
        {
            using var response = await client.GetAsync("/activities" + query);
            await AssertInvalidExternalIdAsync(response, description);
        }
    }

    /// <summary>400 <c>invalid-query</c> con il testo fisso degli altri parametri e SENZA il valore ricevuto (la parola "zqx" dei casi di prova).</summary>
    private static async Task AssertInvalidExternalIdAsync(HttpResponseMessage response, string description)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{description}: atteso 400, ricevuto {(int)response.StatusCode}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("urn:metrica:problem:invalid-query", root.GetProperty("type").GetString());
        Assert.Equal("Parametro non valido", root.GetProperty("title").GetString());
        Assert.StartsWith("Il parametro 'externalId' ", root.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(body.Contains("zqx", StringComparison.Ordinal), $"{description}: il valore ricevuto compare nella risposta");
        Assert.DoesNotContain("qqqqqqqq", body, StringComparison.Ordinal);
        Assert.DoesNotContain("è", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidExternalId_NeverMasksAnInvalidOtherParameter()
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        foreach (var query in new[] { "?externalId=ok&page=0", "?externalId=ok&pageSize=101", "?externalId=ok&sport=Run" })
        {
            await ApiData.AssertProblemAsync(await client.GetAsync("/activities" + query), HttpStatusCode.BadRequest);
        }
    }


    [Fact]
    public async Task InvalidExternalId_OutsideDevelopmentWithoutIdentity_Returns401NotA400_AndNoData()
    {
        // L'autorizzazione precede la validazione: senza identita' nessuna risposta distingue un valore valido da uno non valido.
        var user = Guid.NewGuid();
        await ApiData.InsertAsync(db, With(user, "needs-auth"));
        await using var host = new ActivitiesHost("Production", db.ConnectionStringFor("activities"), nats.Url);
        using var client = host.Start();

        foreach (var query in new[] { "?externalId=", "?externalId=a&externalId=b", "?externalId=zqx%20x", "?externalId=" + new string('q', 129) })
        {
            using var response = await client.SendAsync(ApiData.Get("/activities" + query, ("X-User-Id", user.ToString())));

            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{query}: atteso 401, ricevuto {(int)response.StatusCode} {body}");
            Assert.Equal(string.Empty, body);
            Assert.Null(response.Content.Headers.ContentType);
        }
    }

    [Fact]
    public async Task ExternalIdFilter_RequiresAuthentication_OutsideDevelopment()
    {
        var user = Guid.NewGuid();
        var activity = With(user, "needs-auth");
        await ApiData.InsertAsync(db, activity);
        await using var host = new ActivitiesHost("Production", db.ConnectionStringFor("activities"), nats.Url);
        using var client = host.Start();

        using var response = await client.SendAsync(ApiData.Get("/activities" + Q("needs-auth"), ("X-User-Id", user.ToString())));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReceivedValue_IsNeverLogged_NeitherValidNorInvalid()
    {
        const string marker = "zqxlogprivacymarker";
        var user = Guid.NewGuid();
        await ApiData.InsertAsync(db, With(user, marker + "ext"));
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

        var hit = await client.GetStringAsync("/activities" + Q(marker + "ext"));
        await client.GetAsync("/activities" + Q(marker + "miss"));
        await client.GetAsync("/activities?externalId=" + marker + "%20invalid");
        await client.GetAsync($"/activities?externalId={marker}&externalId={marker}");

        Assert.Contains(marker, hit);   // controllo di sanita': il valore c'e' nella risposta, la sua assenza dai log non e' casuale
        var entries = logs.Entries.ToArray();
        Assert.Contains(entries, e => e.Category.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal));
        var all = string.Join('\n', entries.Select(e => e.Text));
        Assert.DoesNotContain(marker, all, StringComparison.Ordinal);
    }
}
