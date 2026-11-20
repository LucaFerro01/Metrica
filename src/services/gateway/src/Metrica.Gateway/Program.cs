using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Metrica.Gateway;
using Metrica.ServiceDefaults;

// Content root = cartella dell'assembly: appsettings*.json (Kestrel solo su localhost:5080, rotte e cluster YARP) si leggono
// anche se il processo parte da un'altra cartella (`dotnet <dll>`); altrimenti Kestrel ricadrebbe su localhost:5000 o su ASPNETCORE_URLS.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.AddServiceDefaults();

// REQ-034: a livello Information YARP registra l'URL di destinazione, query string compresa, che puo' contenere dati personali.
// La regola e' di codice (dopo quelle di configurazione, che quindi non la riabilitano).
builder.Logging.AddFilter("Yarp.ReverseProxy", LogLevel.Warning);

// Opzioni validate all'avvio (chiave sconosciuta, valore non numerico o fuori intervallo = avvio fallito). Si leggono alla prima
// risoluzione: un override aggiunto dopo la registrazione (WebApplicationFactory) e' rispettato.
builder.Services.AddOptions<GatewayOptions>()
    .BindConfiguration(GatewayOptions.SectionName, binder => binder.ErrorOnUnknownConfiguration = true)
    .Validate(o => o.IsValid(), GatewayOptions.ValidationMessage)
    .ValidateOnStart();

// Limite globale del corpo di Kestrel (Upload:MaxBytes di ingestion + 1 MiB di overhead multipart): per i corpi chunked, senza
// Content-Length, e' Kestrel a interrompere la lettura oltre il limite. Per quelli con Content-Length vedi RequestBodyLimitMiddleware.
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<IOptions<GatewayOptions>>((kestrel, gateway) => kestrel.Limits.MaxRequestBodySize = gateway.Value.MaxRequestBodyBytes);

// Il gateway non ha database ne' controlli di prontezza propri: /health/ready di ServiceDefaults, senza controlli, risponde 200.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(IdentityBoundary.AddTransforms);

var app = builder.Build();

app.UseMiddleware<RequestBodyLimitMiddleware>();

// Il parametro userId del client non deve arrivare ai backend (REQ-021): si toglie prima di YARP, sulla stringa grezza,
// cosi' gli altri parametri non vengono ricodificati.
app.Use((context, next) =>
{
    context.Request.QueryString = IdentityBoundary.RemoveUserIdParameter(context.Request.QueryString);
    return next(context);
});

app.MapServiceDefaults();
app.MapReverseProxy();

app.Run();

/// <summary>Punto d'ingresso reso visibile ai test di integrazione (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
