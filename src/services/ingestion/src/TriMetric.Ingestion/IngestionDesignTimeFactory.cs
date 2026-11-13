using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TriMetric.Ingestion;

/// <summary>
/// Usata solo da <c>dotnet ef</c> (migrazioni): costruisce il contesto senza avviare l'host. La stringa di connessione
/// e' di sola progettazione, fissa e senza segreti: <c>migrations add</c> non apre alcuna connessione.
/// </summary>
internal sealed class IngestionDesignTimeFactory : IDesignTimeDbContextFactory<IngestionDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Database=design_time_only;Username=design_time_only;Password=design_time_only";

    public IngestionDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<IngestionDbContext>();
        IngestionDbContext.Configure(builder, DesignTimeConnectionString);
        return new IngestionDbContext(builder.Options);
    }
}
