using System.Diagnostics;
using System.Net;
using Xunit.Abstractions;

namespace TriMetric.Gateway.Tests;

/// <summary>
/// Il corpo attraversa il gateway in streaming, senza bufferizzarlo per intero: con un corpo di decine di MiB e senza
/// Content-Length il backend riceve i dati mentre il client sta ancora inviando e la memoria non cresce in proporzione.
/// </summary>
[Collection(SequentialCollection.Name)]
public sealed class StreamingTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public async Task LargeChunkedBody_IsRelayedWhileTheClientIsStillSending_AndArrivesIdentical()
    {
        // Limite alzato al massimo consentito (64 MiB + 1 MiB) per provare un corpo di 60 MiB.
        var settings = new Dictionary<string, string?> { ["Gateway:MaxRequestBodyBytes"] = (65 * MiB).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await using var scenario = await Scenario.StartAsync(settings);
        const long total = 60 * MiB;
        long relayedWhenHalfSent = -1;
        var gated = false;

        using var content = new GeneratedContent(total, async sent =>
        {
            if (!gated && sent >= 32 * MiB)
            {
                gated = true;
                // Il client e' fermo a 32 MiB: se il gateway bufferizzasse il corpo il backend non avrebbe ricevuto quasi nulla.
                var request = scenario.Ingestion.Requests.Single();
                await Eventually.AssertAsync(
                    () => request.BodyBytes >= 16 * MiB, TimeSpan.FromSeconds(20),
                    "il backend non riceve i dati mentre il client sta ancora inviando (corpo bufferizzato?)");
                relayedWhenHalfSent = request.BodyBytes;
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);
        var baselineAllocated = GC.GetTotalAllocatedBytes(precise: true);
        long peakMemory = baselineMemory;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peakMemory = Math.Max(peakMemory, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(5);
            }
        });
        var stopwatch = Stopwatch.StartNew();

        using var response = await scenario.Client.PostAsync("/api/ingestion/uploads", content);

        stopwatch.Stop();
        await sampling.CancelAsync();
        await sampler;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - baselineAllocated;
        var peakGrowth = peakMemory - baselineMemory;
        output.WriteLine(
            $"corpo={total / MiB} MiB; durata={stopwatch.ElapsedMilliseconds} ms; ricevuto dal backend a meta' invio >= {relayedWhenHalfSent / MiB} MiB; " +
            $"allocato={allocated / MiB} MiB (client+gateway+backend nello stesso processo); crescita massima dello heap gestito={peakGrowth / MiB} MiB");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.True(received.BodyCompleted);
        Assert.Equal(total, received.BodyBytes);
        Assert.Equal(content.Sha256, received.BodySha256);
        Assert.True(relayedWhenHalfSent >= 16 * MiB);
        Assert.True(allocated < 15 * MiB, $"allocati {allocated / MiB} MiB per un corpo di {total / MiB} MiB: il corpo sembra bufferizzato");
        Assert.True(peakGrowth < 30 * MiB, $"heap cresciuto di {peakGrowth / MiB} MiB per un corpo di {total / MiB} MiB");
    }
}

/// <summary>
/// Collezione senza parallelismo: le misure di memoria contano le allocazioni di tutto il processo, quindi nessun altro test
/// deve girare nello stesso momento.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SequentialCollection
{
    public const string Name = "Sequenziale";
}
