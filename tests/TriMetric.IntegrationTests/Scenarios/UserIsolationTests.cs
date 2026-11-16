using System.Net;
using TriMetric.Contracts;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>AC-007 (REQ-022): gli host girano come l'utente A; l'utente B esiste solo come dati e non e' mai visibile ad A.</summary>
[Collection(E2eCollection.Name)]
public sealed class UserIsolationTests(TimescaleFixture db, NatsFixture nats)
{
    [Fact]
    public async Task Ac007_UserA_SeesOnlyOwnActivities_ForeignIdIs404_AndClientIdentityHintsChangeNothing()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var userA = system.UserId;
        var userB = Guid.NewGuid();

        // A: un upload vero dal gateway e un evento costruito a mano. B: due eventi pubblicati come farebbe ingestion, uno dei
        // quali piu' recente di tutti (in cima all'elenco, se mai fosse visibile).
        var fileA = new GpxTrack { Seed = Guid.NewGuid(), Points = 100, Start = new DateTimeOffset(2024, 5, 1, 6, 0, 0, TimeSpan.Zero) }.Build();
        var eventA = EventFactory.Valid(userA, start: new DateTimeOffset(2024, 5, 2, 6, 0, 0, TimeSpan.Zero));
        var eventsB = new[]
        {
            EventFactory.Valid(userB, start: new DateTimeOffset(2024, 5, 3, 6, 0, 0, TimeSpan.Zero)),
            EventFactory.Valid(userB, start: new DateTimeOffset(2025, 1, 1, 6, 0, 0, TimeSpan.Zero)),
        };

        system.Protect(fileA.Probes);

        // L'upload di A porta con se' suggerimenti d'identita' di B (query e intestazione): il gateway li toglie, il servizio li ignora.
        var upload = await system.GatewayClient.UploadAsync(
            GatewayApi.UploadPath + $"?userId={userB}&UserId={userB}", fileA.FileName, fileA.Content, false, ("X-User-Id", userB.ToString()));
        Expect.Status(HttpStatusCode.Accepted, upload);
        await system.NatsProbe.PublishEventAsync(eventA);
        foreach (var e in eventsB)
        {
            await system.NatsProbe.PublishEventAsync(e);
        }

        await system.Api.WaitForActivityAsync(fileA.Sha256);
        await system.Api.WaitForActivityAsync(eventA.Data.ExternalId);
        await Eventually.AssertAsync(
            async () => (await system.Db.ActivitiesOfAsync(userB)).Count == 2,
            Eventually.Normal,
            "le due attivita' di B sono nel database");
        await Eventually.AssertAsync(() => system.NatsProbe.DurableDrainedAsync(), Eventually.Normal, "il consumatore ha elaborato tutto");

        var rowsA = await system.Db.ActivitiesOfAsync(userA);
        var rowsB = await system.Db.ActivitiesOfAsync(userB);
        Assert.Equal(2, rowsA.Count);
        Assert.Equal(2, rowsB.Count);
        var idsA = rowsA.Select(r => r.Id).Order().ToList();
        var idsB = rowsB.Select(r => r.Id).ToList();

        // Elenco di A (attraverso il gateway): solo le sue.
        var list = await system.Api.ListPageAsync("?pageSize=100");
        Assert.Equal(2, list.Total);
        Assert.Equal(idsA, list.Items.Select(i => i.Id).Order());
        Assert.DoesNotContain(list.Items, i => idsB.Contains(i.Id));

        // Dettaglio: le proprie si leggono, quelle di B no, con una risposta IDENTICA a quella di un id inesistente.
        foreach (var id in idsA)
        {
            Assert.Equal(id, (await system.Api.DetailOfAsync(id)).Id);
        }

        var missing = await system.Api.DetailAsync(Guid.NewGuid());
        Expect.Status(HttpStatusCode.NotFound, missing);
        foreach (var id in idsB)
        {
            var foreign = await system.Api.DetailAsync(id);
            Expect.Status(HttpStatusCode.NotFound, foreign);
            Assert.Equal(missing.MediaType, foreign.MediaType);
            Assert.Equal(missing.StableBody(), foreign.StableBody());
        }

        // Un userId di B in query o in X-User-Id, dal client, non cambia nulla (attraverso il gateway).
        var seenByActivities = system.Activities.Requests.Count;
        foreach (var hint in new[] { $"?userId={userB}", $"?pageSize=100&UserId={userB}", $"?userid={userB}&pageSize=100&userId={userB}" })
        {
            var hinted = await system.Api.ListPageAsync(hint);
            Assert.Equal(idsA, hinted.Items.Select(i => i.Id).Order());
            Assert.Equal(2, hinted.Total);
        }

        var withHeader = await system.GatewayClient.GetAsync("/api/activities?pageSize=100", ("X-User-Id", userB.ToString()));
        Expect.Status(HttpStatusCode.OK, withHeader);
        Assert.Equal(idsA, withHeader.AsList().Items.Select(i => i.Id).Order());

        foreach (var id in idsB)
        {
            Expect.Status(HttpStatusCode.NotFound, await system.GatewayClient.GetAsync($"/api/activities/{id}", ("X-User-Id", userB.ToString())));
            Expect.Status(HttpStatusCode.NotFound, await system.GatewayClient.GetAsync($"/api/activities/{id}?userId={userB}"));
        }

        // Cio che il servizio ha RICEVUTO dal gateway: nessun userId in query e nessun X-User-Id (il gateway li toglie, REQ-021)...
        var forwarded = system.Activities.Requests.Since(seenByActivities).Where(r => r.PathAndQuery.StartsWith("/activities", StringComparison.Ordinal)).ToList();
        Assert.True(forwarded.Count >= 7, $"attese almeno 7 richieste inoltrate, viste {forwarded.Count}");
        Assert.All(forwarded, r =>
        {
            Assert.DoesNotContain("userid", r.PathAndQuery, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(r.Headers.Keys, h => h.Equals("X-User-Id", StringComparison.OrdinalIgnoreCase));
        });

        // ...mentre le stesse richieste fatte direttamente sul servizio (controllo: il registro funziona) li portano.
        var directMark = system.Activities.Requests.Count;
        // Anche direttamente sul servizio (senza gateway) l'identita' del client non conta.
        var direct = await system.ActivitiesClient.GetAsync("/activities?pageSize=100", ("X-User-Id", userB.ToString()));
        Expect.Status(HttpStatusCode.OK, direct);
        Assert.Equal(idsA, direct.AsList().Items.Select(i => i.Id).Order());
        Expect.Status(HttpStatusCode.NotFound, await system.ActivitiesClient.GetAsync($"/activities/{idsB[0]}?userId={userB}", ("X-User-Id", userB.ToString())));
        var directlyReceived = system.Activities.Requests.Since(directMark);
        Assert.All(directlyReceived, r =>
        {
            Assert.Equal(userB.ToString(), r.Header("X-User-Id"));
        });
        Assert.Contains(directlyReceived, r => r.PathAndQuery.Contains($"userId={userB}", StringComparison.Ordinal));

        // Lo stesso per l'upload di A (query e intestazione di B): ingestion non li ha ricevuti.
        var uploadsSeen = system.Ingestion.Requests.All.Where(r => r.Method == "POST" && r.PathAndQuery.StartsWith("/uploads", StringComparison.Ordinal)).ToList();
        var uploadSeen = Assert.Single(uploadsSeen);
        Assert.DoesNotContain("userid", uploadSeen.PathAndQuery, StringComparison.OrdinalIgnoreCase);
        Assert.False(uploadSeen.Headers.ContainsKey("X-User-Id"));

        // Database (superutente): l'upload di A e' di A, non di B, e non compare tra le attivita' di B.
        Assert.Equal(1L, await system.Db.UploadCountAsync(userA));
        Assert.Equal(0L, await system.Db.UploadCountAsync(userB));
        Assert.DoesNotContain(rowsB, r => r.ExternalId == fileA.Sha256 || r.ExternalId == eventA.Data.ExternalId);
        Assert.Contains(rowsA, r => r.ExternalId == fileA.Sha256);
        Assert.All(rowsA, r => Assert.Equal(userA, r.UserId));
        Assert.All(rowsB, r => Assert.Equal(userB, r.UserId));
        Assert.Equal(
            eventsB.Select(e => e.Data.ExternalId).Order(),
            rowsB.Select(r => r.ExternalId).Order());

        // L'evento pubblicato da ingestion per A ha la busta di A (mai quella suggerita dal client).
        var published = (await system.NatsProbe.EventsAsync()).Where(m => m.Envelope.Data.ExternalId == fileA.Sha256).ToList();
        Assert.Equal(userA, Assert.Single(published).Envelope.UserId);

        system.AssertLogsContainNoPersonalData();
    }
}
