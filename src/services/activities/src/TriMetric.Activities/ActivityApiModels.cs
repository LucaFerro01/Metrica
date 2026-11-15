using TriMetric.Contracts;

namespace TriMetric.Activities;

/// <summary>
/// Voce di <c>GET /activities</c> (schema OpenAPI <c>ActivityListItem</c>, nome stabile usato dal client generato). Unita' SI;
/// <see cref="StartTime"/> e' l'istante UTC di inizio (ISO 8601 con offset <c>+00:00</c>); <see cref="Sport"/> e' una stringa
/// minuscola (<c>run|ride|swim|other</c>). Non espone ne' il proprietario ne' la fonte interna.
/// </summary>
/// <param name="Id">Identificativo dell'attivita'.</param>
/// <param name="ExternalId">Identificativo nella fonte (per i file, lo SHA-256 esadecimale del contenuto).</param>
/// <param name="Sport">Sport dell'attivita'.</param>
/// <param name="StartTime">Istante UTC di inizio.</param>
/// <param name="DistanceMeters">Distanza in metri.</param>
/// <param name="ElapsedSeconds">Durata totale in secondi.</param>
/// <param name="ElevationGainMeters">Dislivello positivo in metri, <c>null</c> se non noto.</param>
public sealed record ActivityListItem(
    Guid Id,
    string ExternalId,
    Sport Sport,
    DateTimeOffset StartTime,
    double DistanceMeters,
    double ElapsedSeconds,
    double? ElevationGainMeters);

/// <summary>
/// Risposta di <c>GET /activities</c> (schema OpenAPI <c>ActivityListPage</c>): una pagina dell'elenco dell'utente, dalla piu'
/// recente (<c>startTime</c> decrescente, poi <c>id</c>). <see cref="Total"/> conta tutte le attivita' dell'utente che
/// rispettano il filtro, non solo quelle della pagina; una pagina oltre la fine ha <see cref="Items"/> vuoto.
/// </summary>
/// <param name="Items">Attivita' della pagina.</param>
/// <param name="Page">Numero di pagina (da 1), come richiesto.</param>
/// <param name="PageSize">Dimensione della pagina (1-100), come richiesta.</param>
/// <param name="Total">Numero totale di attivita' dell'utente con il filtro applicato.</param>
public sealed record ActivityListPage(
    IReadOnlyList<ActivityListItem> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>
/// Risposta di <c>GET /activities/{id}</c> (schema OpenAPI <c>ActivityDetail</c>): i campi di <see cref="ActivityListItem"/>
/// piu' riepilogo, giri (ordinati per inizio) e stream ridotto a passo costante (al massimo <c>maxPoints</c> punti, sempre
/// con il primo e l'ultimo). Le colonne dello stream assenti nell'attivita' sono <c>null</c>.
/// </summary>
/// <param name="Id">Identificativo dell'attivita'.</param>
/// <param name="ExternalId">Identificativo nella fonte.</param>
/// <param name="Sport">Sport dell'attivita'.</param>
/// <param name="StartTime">Istante UTC di inizio.</param>
/// <param name="DistanceMeters">Distanza in metri.</param>
/// <param name="ElapsedSeconds">Durata totale in secondi.</param>
/// <param name="ElevationGainMeters">Dislivello positivo in metri, <c>null</c> se non noto.</param>
/// <param name="MovingSeconds">Durata in movimento in secondi.</param>
/// <param name="Summary">Valori di sintesi (campi <c>null</c> se non disponibili).</param>
/// <param name="Laps">Giri, ordinati per istante di inizio.</param>
/// <param name="Stream">Stream ridotto in formato a colonne; <c>TimeOffsetSeconds</c> sono i secondi dall'inizio.</param>
public sealed record ActivityDetail(
    Guid Id,
    string ExternalId,
    Sport Sport,
    DateTimeOffset StartTime,
    double DistanceMeters,
    double ElapsedSeconds,
    double? ElevationGainMeters,
    double MovingSeconds,
    ActivitySummary Summary,
    IReadOnlyList<LapData> Laps,
    StreamColumns Stream);
