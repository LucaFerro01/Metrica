using Microsoft.EntityFrameworkCore;
using TriMetric.Activities;
using TriMetric.Messaging;
using TriMetric.ServiceDefaults;
using StreamWriter = TriMetric.Activities.StreamWriter;   // senza alias: ambiguo con System.IO.StreamWriter (using impliciti)

// Content root = cartella dell'assembly: appsettings*.json (Kestrel solo su 127.0.0.1:5082) si leggono anche se il
// processo parte da un'altra cartella (`dotnet <dll>`); altrimenti Kestrel ricadrebbe su localhost:5000 o su ASPNETCORE_URLS.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.AddServiceDefaults();
builder.AddDevelopmentIdentity();

builder.Services.AddNatsMessaging(builder.Configuration);

// La stringa di connessione si legge alla prima risoluzione del contesto (non qui), cosi' un override aggiunto dopo
// la registrazione (ad esempio dalle WebApplicationFactory) e' rispettato. Nessun logging dei dati sensibili (REQ-034).
builder.Services.AddDbContext<ActivitiesDbContext>((services, options) =>
    ActivitiesDbContext.Configure(
        options,
        services.GetRequiredService<IConfiguration>().GetConnectionString("Activities")
            ?? throw new InvalidOperationException("Manca la stringa di connessione 'ConnectionStrings:Activities'.")));

// Scrive i punti dello stream con COPY binario nella connessione e nella transazione correnti del contesto: nello scope
// di un messaggio (T12) inbox, gestore e punti si confermano o si annullano insieme.
builder.Services.AddScoped<StreamWriter>();

// /health/ready: solo il database. NATS non e' un controllo di prontezza (REQ-013).
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [ServiceDefaultsExtensions.ReadyTag]);

builder.Services.AddOpenApi();

var app = builder.Build();

// Migrazioni all'avvio SOLO in Development (REQ-024): negli altri ambienti si applicano con un processo dedicato.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<ActivitiesDbContext>().Database.Migrate();

    app.MapOpenApi();   // /openapi/v1.json: documento dell'API, solo in sviluppo
}

app.MapServiceDefaults();

app.Run();

/// <summary>Punto d'ingresso reso visibile ai test di integrazione (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
