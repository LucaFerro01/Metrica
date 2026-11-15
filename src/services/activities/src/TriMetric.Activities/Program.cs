using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TriMetric.Activities;
using TriMetric.Contracts;
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

// Consumatore durevole di ActivityImported (T12): opzioni lette ORA da builder.Configuration (vedi ActivityImportedConsumer).
builder.Services.AddEventConsumer<ActivitiesDbContext, ActivityImported, ActivityImportedHandler>(
    ActivityImportedConsumer.CreateOptions(builder.Configuration));

// /health/ready: solo il database. NATS non e' un controllo di prontezza (REQ-013).
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [ServiceDefaultsExtensions.ReadyTag]);

// API di lettura (T13): opzioni validate all'avvio (chiave sconosciuta, valore non numerico o fuori intervallo = avvio fallito).
// Si leggono alla prima risoluzione: un override aggiunto dopo la registrazione (WebApplicationFactory) e' rispettato.
builder.Services.AddOptions<ActivitiesApiOptions>()
    .BindConfiguration(ActivitiesApiOptions.SectionName, binder => binder.ErrorOnUnknownConfiguration = true)
    .Validate(o => o.IsValid(), ActivitiesApiOptions.ValidationMessage)
    .ValidateOnStart();
builder.Services.AddScoped<ActivityStreamReader>();

// JSON delle API (e quindi dello schema OpenAPI) come i contratti: Sport e' "run" e non "Run" o 0 (DEC-005); numeri solo come
// numeri (le opzioni web predefinite accettano anche stringhe e lo schema diventerebbe "number | string" nel client generato).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});

builder.Services.AddOpenApi(options => options.AddActivityApiDocument());

var app = builder.Build();

// Migrazioni all'avvio SOLO in Development (REQ-024): negli altri ambienti si applicano con un processo dedicato.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<ActivitiesDbContext>().Database.Migrate();

    app.MapOpenApi();   // /openapi/v1.json: documento dell'API, solo in sviluppo
}

app.MapServiceDefaults();
app.MapActivityEndpoints();   // in tutti gli ambienti; fuori da Development lo schema Deny risponde 401

app.Run();

/// <summary>Punto d'ingresso reso visibile ai test di integrazione (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
