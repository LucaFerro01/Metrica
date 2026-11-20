using System.Net.Sockets;
using NATS.Client.Core;
using Xunit;

namespace Metrica.Messaging.Tests;

/// <summary>Formula del backoff e decisione della pausa del relay (senza contenitori).</summary>
[Trait("Category", "Relay")]
public sealed class RelayPacerTests
{
    [Theory]
    [InlineData(500, 30, 1, 1000)]
    [InlineData(500, 30, 2, 2000)]
    [InlineData(500, 30, 3, 4000)]
    [InlineData(500, 30, 4, 8000)]
    [InlineData(500, 30, 5, 16000)]
    [InlineData(500, 30, 6, 30000)]
    [InlineData(500, 30, 7, 30000)]
    [InlineData(500, 30, 1000, 30000)]
    [InlineData(50, 30, 1, 100)]
    [InlineData(50, 30, 2, 200)]
    [InlineData(50, 30, 3, 400)]
    [InlineData(1000, 1, 1, 1000)]
    [InlineData(1000, 1, 5, 1000)]
    public void Backoff_DoublesFromThePollIntervalUpToTheCap(int pollMs, int maxSeconds, int failures, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), RelayPacer.Backoff(failures, pollMs, maxSeconds));
    }

    [Fact]
    public void AfterBatch_FailuresWithoutProgress_GrowTheDelay_AndFirstSuccessResetsIt()
    {
        var pacer = new RelayPacer(pollIntervalMs: 50, batchSize: 10, maxBackoffSeconds: 30);

        Assert.Equal(TimeSpan.FromMilliseconds(100), pacer.AfterBatch(read: 1, published: 0, failed: 1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), pacer.AfterBatch(read: 1, published: 0, failed: 1));
        Assert.Equal(TimeSpan.FromMilliseconds(400), pacer.AfterBatch(read: 1, published: 0, failed: 1));

        // Primo successo: pausa di polling e contatore azzerato.
        Assert.Equal(TimeSpan.FromMilliseconds(50), pacer.AfterBatch(read: 1, published: 1, failed: 0));
        Assert.Equal(0, pacer.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMilliseconds(100), pacer.AfterBatch(read: 1, published: 0, failed: 1));
    }

    [Fact]
    public void AfterBatch_EmptyQueue_WaitsThePollInterval_AndFullCleanBatchDoesNotWait()
    {
        var pacer = new RelayPacer(pollIntervalMs: 50, batchSize: 10, maxBackoffSeconds: 30);

        Assert.Equal(TimeSpan.FromMilliseconds(50), pacer.AfterBatch(read: 0, published: 0, failed: 0));
        Assert.Equal(TimeSpan.Zero, pacer.AfterBatch(read: 10, published: 10, failed: 0));
        Assert.Equal(TimeSpan.FromMilliseconds(50), pacer.AfterBatch(read: 10, published: 9, failed: 1));
        Assert.Equal(0, pacer.ConsecutiveFailures);
    }

    [Fact]
    public void AfterDatabaseError_UsesTheSameBackoff_AndSuccessResetsIt()
    {
        var pacer = new RelayPacer(pollIntervalMs: 50, batchSize: 10, maxBackoffSeconds: 30);

        Assert.Equal(TimeSpan.FromMilliseconds(100), pacer.AfterDatabaseError());
        Assert.Equal(TimeSpan.FromMilliseconds(200), pacer.AfterDatabaseError());
        Assert.Equal(TimeSpan.FromMilliseconds(50), pacer.AfterBatch(read: 0, published: 0, failed: 0));
        Assert.Equal(TimeSpan.FromMilliseconds(100), pacer.AfterDatabaseError());
    }

    [Fact]
    public void IsBusUnavailable_OnlyForTimeoutAndConnectionErrors()
    {
        Assert.True(OutboxRelayErrors.IsBusUnavailable(new TimeoutException()));
        Assert.True(OutboxRelayErrors.IsBusUnavailable(new IOException()));
        Assert.True(OutboxRelayErrors.IsBusUnavailable(new SocketException()));
        Assert.True(OutboxRelayErrors.IsBusUnavailable(new NatsConnectionFailedException("x")));

        // Tutto il resto riguarda quel messaggio e non deve fermare la coda.
        Assert.False(OutboxRelayErrors.IsBusUnavailable(new InvalidOperationException()));
        Assert.False(OutboxRelayErrors.IsBusUnavailable(new NatsException("x")));
        Assert.False(OutboxRelayErrors.IsBusUnavailable(new NatsNoRespondersException()));
        Assert.False(OutboxRelayErrors.IsBusUnavailable(new ArgumentException()));
    }
}
