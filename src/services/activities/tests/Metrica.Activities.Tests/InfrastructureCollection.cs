using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>
/// Un solo TimescaleDB e un solo NATS per tutti i test di persistenza del servizio (nessun container per classe).
/// I container partono solo quando gira una classe di questa collection: i test sul convertitore dello sport e sui file
/// di configurazione non li avviano. Le classi della collection non girano in parallelo (un test ferma e riavvia NATS).
/// </summary>
[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<TimescaleFixture>, ICollectionFixture<NatsFixture>
{
    public const string Name = "activities-infrastructure";
}
