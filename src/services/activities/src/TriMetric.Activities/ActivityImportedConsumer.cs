using System.Globalization;
using Microsoft.Extensions.Configuration;
using TriMetric.Contracts;
using TriMetric.Messaging;

namespace TriMetric.Activities;

/// <summary>
/// Opzioni del consumatore durevole <c>activities-activity-imported</c> (REQ-014, REQ-015, REQ-017; DEC-001, DEC-005):
/// valori predefiniti del piano, sostituibili dalla sezione <c>Consumers:ActivityImported</c> e validati all'avvio.
/// </summary>
/// <remarks>
/// <para><b>Chiavi</b> (tutte facoltative): <c>MaxDeliver</c> (intero 1-100, predefinito 5), <c>AckWaitSeconds</c> (intero 1-86400,
/// predefinito 60), <c>BackoffMilliseconds:0</c>, <c>:1</c>... (da 1 a 10 interi 1-3.600.000, predefinito 1 s, 2 s, 5 s, 10 s: la
/// lista configurata SOSTITUISCE quella predefinita). Il nome del durevole, il subject (<see cref="EventNames.ActivityImportedSubject"/>)
/// e la concorrenza (1) non sono configurabili. Una chiave sconosciuta (un refuso) o un valore non valido fanno fallire
/// l'avvio con un messaggio che nomina la chiave: mai ignorati in silenzio.</para>
/// <para><b>Quando si legge.</b> <c>AddEventConsumer</c> riceve un oggetto costruito alla registrazione (non pigro): questa
/// classe legge <c>builder.Configuration</c> mentre <c>Program</c> registra i servizi. Un override aggiunto DOPO la
/// costruzione dell'host (<c>ConfigureAppConfiguration</c> delle <c>WebApplicationFactory</c>) non arriva qui: nei test serve
/// <c>UseSetting</c>, come per <c>Dev:UserId</c>.</para>
/// <para><b>Backoff predefinito crescente</b> 1 s, 2 s, 5 s, 10 s (18 s in tutto prima della DLQ con 5 tentativi): un messaggio che
/// fallisce di continuo occupa lo slot del consumatore per questo tempo (decisione registrata in T07, <c>MaxAckPending = MaxConcurrency</c>).</para>
/// </remarks>
public static class ActivityImportedConsumer
{
    /// <summary>Nome del consumatore durevole, senza punti: e' anche il subject DLQ (<c>dlq.activities-activity-imported</c>) e la colonna <c>consumer</c> dell'inbox.</summary>
    public const string DurableName = "activities-activity-imported";

    /// <summary>Sezione di configurazione delle opzioni.</summary>
    public const string SectionName = "Consumers:ActivityImported";

    public const int DefaultMaxDeliver = 5;

    public const int DefaultAckWaitSeconds = 60;

    public const int MaxMaxDeliver = 100;

    public const int MaxAckWaitSeconds = 86_400;

    public const int MaxBackoffMilliseconds = 3_600_000;

    public const int MaxBackoffSteps = 10;

    private const string MaxDeliverKey = "MaxDeliver";
    private const string AckWaitKey = "AckWaitSeconds";
    private const string BackoffKey = "BackoffMilliseconds";

    private static readonly string[] AllowedKeys = [MaxDeliverKey, AckWaitKey, BackoffKey];

    private static readonly TimeSpan[] DefaultBackoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>Costruisce le opzioni dai valori predefiniti e dalla sezione <see cref="SectionName"/>.</summary>
    /// <exception cref="InvalidOperationException">Chiave sconosciuta o valore non valido (il messaggio nomina la chiave).</exception>
    public static ConsumerOptions CreateOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);

        foreach (var child in section.GetChildren())
        {
            if (!AllowedKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Chiave di configurazione sconosciuta '{SectionName}:{child.Key}': ammesse {string.Join(", ", AllowedKeys)} "
                    + "(nome del consumatore, subject e concorrenza non sono configurabili).");
            }
        }

        // Gli estremi ammessi qui sono dentro quelli che AddEventConsumer verifica (un test lo prova): niente sorprese piu' tardi.
        return new ConsumerOptions
        {
            DurableName = DurableName,
            FilterSubject = EventNames.ActivityImportedSubject,
            MaxDeliver = ReadInt(section, MaxDeliverKey, DefaultMaxDeliver, 1, MaxMaxDeliver),
            AckWait = TimeSpan.FromSeconds(ReadInt(section, AckWaitKey, DefaultAckWaitSeconds, 1, MaxAckWaitSeconds)),
            Backoff = ReadBackoff(section),
            MaxConcurrency = 1,
        };
    }

    private static int ReadInt(IConfigurationSection section, string key, int defaultValue, int min, int max)
    {
        var value = section[key];
        return value is null ? defaultValue : ParseInt(value, $"{SectionName}:{key}", min, max);
    }

    private static TimeSpan[] ReadBackoff(IConfigurationSection section)
    {
        var list = section.GetSection(BackoffKey);
        var name = $"{SectionName}:{BackoffKey}";
        // ConfigurationManager (host reale) restituisce "" e non null per una sezione che ha figli.
        if (!string.IsNullOrEmpty(list.Value))
        {
            throw new InvalidOperationException(
                $"'{name}' deve essere un elenco ('{name}:0', '{name}:1', ...) di attese in millisecondi, non un valore singolo.");
        }

        var steps = list.GetChildren().ToArray();
        if (steps.Length == 0)
        {
            return [.. DefaultBackoff];
        }

        if (steps.Length > MaxBackoffSteps)
        {
            throw new InvalidOperationException($"'{name}' ha {steps.Length} attese: al massimo {MaxBackoffSteps}.");
        }

        return [.. steps.Select(step =>
        {
            if (!int.TryParse(step.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidOperationException($"'{name}:{step.Key}': le voci dell'elenco si numerano con indici interi (0, 1, ...).");
            }

            return TimeSpan.FromMilliseconds(ParseInt(step.Value, $"{name}:{step.Key}", 1, MaxBackoffMilliseconds));
        })];
    }

    private static int ParseInt(string? value, string key, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
        {
            throw new InvalidOperationException($"'{key}' deve essere un numero intero tra {min} e {max}.");
        }

        return number;
    }
}
