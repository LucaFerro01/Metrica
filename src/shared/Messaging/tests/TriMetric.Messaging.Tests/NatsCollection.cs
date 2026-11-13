using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.Messaging.Tests;

/// <summary>
/// Un solo NATS per tutti i test che lo usano; le classi della collection non girano in parallelo
/// (un test ferma e riavvia il container).
/// </summary>
[CollectionDefinition(Name)]
public sealed class NatsCollection : ICollectionFixture<NatsFixture>
{
    public const string Name = "nats";
}
