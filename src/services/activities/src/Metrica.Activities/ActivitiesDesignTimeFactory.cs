using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Metrica.Activities;

/// <summary>
/// Usata solo da <c>dotnet ef</c> (migrazioni): costruisce il contesto senza avviare l'host. La stringa di connessione
/// e' di sola progettazione, fissa e senza segreti: <c>migrations add</c> non apre alcuna connessione.
/// </summary>
internal sealed class ActivitiesDesignTimeFactory : IDesignTimeDbContextFactory<ActivitiesDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Database=design_time_only;Username=design_time_only;Password=design_time_only";

    public ActivitiesDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ActivitiesDbContext>();
        ActivitiesDbContext.Configure(builder, DesignTimeConnectionString);
        return new ActivitiesDbContext(builder.Options);
    }
}
