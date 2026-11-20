using Microsoft.EntityFrameworkCore;
using Metrica.Ingestion;
using Metrica.Ingestion.Parsing;
using Metrica.Messaging;
using Metrica.ServiceDefaults;

// Content root = cartella dell'assembly: appsettings*.json (Kestrel solo su 127.0.0.1:5081) si leggono anche se il
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
builder.Services.AddDbContext<IngestionDbContext>((services, options) =>
    IngestionDbContext.Configure(
        options,
        services.GetRequiredService<IConfiguration>().GetConnectionString("Ingestion")
            ?? throw new InvalidOperationException("Manca la stringa di connessione 'ConnectionStrings:Ingestion'.")));

builder.Services.AddOutbox<IngestionDbContext>();        // senza questa IOutbox non e' registrato (T10)
builder.Services.AddOutboxRelay<IngestionDbContext>();

// I parser non hanno stato: si condividono tra le richieste (T08).
builder.Services.AddSingleton<IActivityFileParser, GpxParser>();
builder.Services.AddSingleton<IActivityFileParser, FitParser>();

// Opzioni dell'upload (Upload:MaxBytes, Upload:ParseTimeoutSeconds), validate all'avvio. I valori si leggono alla prima
// risoluzione (non qui): un override aggiunto dopo la registrazione (WebApplicationFactory) e' rispettato.
builder.Services.AddOptions<UploadOptions>()
    .BindConfiguration(UploadOptions.SectionName)
    .Validate(
        o => o.IsValid(),
        $"Upload: MaxBytes deve essere tra 1 e {UploadOptions.MaxBytesCeiling} e ParseTimeoutSeconds tra 1 e {UploadOptions.ParseTimeoutCeilingSeconds}.")
    .ValidateOnStart();

builder.Services.AddOpenApi(options => options.AddUploadRequestBody());

// /health/ready: solo il database. NATS non e' un controllo di prontezza (REQ-013).
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [ServiceDefaultsExtensions.ReadyTag]);

var app = builder.Build();

// Migrazioni all'avvio SOLO in Development (REQ-024): negli altri ambienti si applicano con un processo dedicato.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<IngestionDbContext>().Database.Migrate();

    app.MapOpenApi();   // /openapi/v1.json: documento dell'API, solo in sviluppo
}

app.MapServiceDefaults();
app.MapUploadEndpoint();

app.Run();

/// <summary>Punto d'ingresso reso visibile ai test di integrazione (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
