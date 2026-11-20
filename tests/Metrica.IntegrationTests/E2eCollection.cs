using Metrica.TestInfrastructure;

// Un solo TimescaleDB e un solo NATS per tutti i test, e nessun test in parallelo: gli scenari condividono il database (con
// i ruoli e i grant veri), lo stream EVENTS e il consumatore durevole, e alcuni fermano NATS.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Metrica.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2eCollection : ICollectionFixture<TimescaleFixture>, ICollectionFixture<NatsFixture>
{
    public const string Name = "end-to-end";
}
