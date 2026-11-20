using System.Net.Sockets;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Metrica.TestInfrastructure;

/// <summary>
/// PostgreSQL con TimescaleDB inizializzato con lo script REALE <c>deploy/postgres/init/01-schemas.sql</c>
/// (ruoli <c>*_svc</c>, schemi, permessi): i test verificano gli stessi permessi dello sviluppo (DEC-013).
/// Un solo database condiviso; isolare i test con identificativi unici e TRUNCATE, non con CREATE DATABASE.
/// </summary>
public sealed class TimescaleFixture : IAsyncLifetime
{
    public const string Image = "docker.io/timescale/timescaledb:latest-pg16";

    /// <summary>Servizi con ruolo <c>&lt;service&gt;_svc</c>. Lo schema di training-plan e <c>training_plan</c>.</summary>
    public static readonly IReadOnlyList<string> Services = ["identity", "ingestion", "activities", "analytics", "training_plan"];

    private const string AdminUser = "metrica";
    private const string AdminPassword = "dev_only_admin";
    private const string Database = "metrica";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase(Database)
        .WithUsername(AdminUser)
        .WithPassword(AdminPassword)
        .WithResourceMapping(
            RepositoryRoot.File("deploy", "postgres", "init", "01-schemas.sql"),
            "/docker-entrypoint-initdb.d/")
        .Build();

    /// <summary>Connessione del superutente (setup e verifiche amministrative dei test).</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Connessione come ruolo <c>&lt;service&gt;_svc</c> (password <c>dev_only_&lt;service&gt;</c>), database condiviso.</summary>
    public string ConnectionStringFor(string service)
    {
        if (!Services.Contains(service))
        {
            throw new ArgumentException(
                $"Servizio sconosciuto '{service}'. Validi: {string.Join(", ", Services)}.", nameof(service));
        }

        return Build($"{service}_svc", $"dev_only_{service}");
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        AdminConnectionString = Build(AdminUser, AdminPassword);
        await WaitUntilInitializedAsync(TimeSpan.FromSeconds(60));
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private string Build(string user, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = _container.Hostname,
        Port = _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort),
        Database = Database,
        Username = user,
        Password = password,
        // Nessun pool: ogni test apre e chiude le proprie connessioni, senza residui alla distruzione del container.
        Pooling = false,
    }.ConnectionString;

    /// <summary>
    /// L'immagine ufficiale esegue gli script di init su un server temporaneo (solo socket Unix) e poi riavvia:
    /// il container puo risultare pronto troppo presto. Un login TCP come ruolo di servizio riesce solo sul
    /// server definitivo dopo l'esecuzione dello script, quindi si attende con polling.
    /// </summary>
    private async Task WaitUntilInitializedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionStringFor("activities"));
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    SELECT (SELECT count(*) FROM pg_namespace WHERE nspname = ANY(@schemas))
                         + (SELECT count(*) FROM pg_roles WHERE rolname = ANY(@roles))
                    """,
                    connection);
                command.Parameters.AddWithValue("schemas", Services.ToArray());
                command.Parameters.AddWithValue("roles", Services.Select(s => $"{s}_svc").ToArray());
                var found = Convert.ToInt32(await command.ExecuteScalarAsync());
                if (found == Services.Count * 2)
                {
                    return;
                }

                last = new InvalidOperationException($"Trovati {found} tra schemi e ruoli, attesi {Services.Count * 2}.");
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or SocketException)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"TimescaleDB non inizializzato entro {timeout.TotalSeconds:0} s (login come activities_svc e schemi/ruoli).", last);
    }
}
