using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.Messaging.Tests;

/// <summary>
/// Un solo NATS e un solo TimescaleDB per tutti i test di Messaging (nessun container per classe); le classi della
/// collection non girano in parallelo (un test ferma e riavvia il container NATS). Ogni test parte da tabelle
/// ricreate e usa subject e id unici, quindi non dipende dall'ordine di esecuzione.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NatsCollection : ICollectionFixture<NatsFixture>, ICollectionFixture<TimescaleFixture>
{
    public const string Name = "nats";
}
