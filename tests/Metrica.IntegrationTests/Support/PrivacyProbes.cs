using System.Globalization;
using System.Text.RegularExpressions;

namespace Metrica.IntegrationTests.Support;

/// <summary>
/// Cio che NON deve comparire nei log (REQ-034): coordinate GPS in varie rappresentazioni, valori distintivi di frequenza
/// cardiaca e potenza, frammenti del contenuto del file, nome del file, frammenti del corpo dell'evento. Le ricerche sono
/// espressioni regolari con limiti che evitano falsi positivi (cifre dentro identificatori, tempi in ms, porte).
/// </summary>
internal sealed class PrivacyProbes
{
    /// <summary>
    /// Corpo dell'evento <c>ActivityImported</c>: frammenti del suo JSON (nome del campo tra virgolette seguito da <c>:</c>,
    /// maiuscole rispettate). Il solo nome del campo non basta: appare nei log del modello di EF Core (<c>a.ElapsedSeconds</c>).
    /// </summary>
    public static readonly string[] EventBodyFragments =
    [
        "\"timeOffsetSeconds\":[", "\"latitude\":[", "\"longitude\":[", "\"altitudeMeters\":[", "\"heartRate\":[",
        "\"powerWatts\":[", "\"cadence\":[", "\"speedMetersPerSecond\":[", "\"avgHeartRate\":", "\"elapsedSeconds\":",
        "\"externalId\":", "\"distanceMeters\":",
    ];

    private readonly List<(string Name, Regex Pattern)> _patterns = [];

    public int Count => _patterns.Count;

    /// <summary>Una coordinata (latitudine o longitudine) in tutte le forme in cui un log potrebbe stamparla.</summary>
    public PrivacyProbes AddCoordinate(string name, double value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("it-IT"), CultureInfo.GetCultureInfo("de-DE") })
        {
            var texts = new List<string> { value.ToString("R", culture) };
            for (var decimals = 4; decimals <= 7; decimals++)
            {
                texts.Add(Rounded(value, decimals, culture));
                texts.Add(Truncated(value, decimals, culture));
            }

            foreach (var text in texts)
            {
                if (seen.Add(text))
                {
                    // Non preceduta da cifra o separatore: "145.1234" non e' la coordinata 45.1234.
                    _patterns.Add(($"{name} come '{text}'", new Regex($@"(?<!\d)(?<!\d\.){Regex.Escape(text)}", RegexOptions.CultureInvariant)));
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Un intero distintivo (frequenza cardiaca, potenza). Si vedono <c>173</c>, <c>173.0</c>, <c>173bpm</c>, <c>389W</c> e l'elemento di
    /// un array (<c>[170,173,175]</c>); non si vedono il numero dentro un altro (<c>1173</c>, <c>0.173</c>, <c>12,173</c>), dentro un
    /// identificativo (<c>0HN5173A</c>, <c>50173</c>) o con quattro decimali, come i tempi di Kestrel in it-IT (<c>173,1861</c>).
    /// Dopo il numero si ammette solo una unita' (<c>bpm</c>, <c>W</c>, <c>watts</c>, <c>rpm</c>) o un carattere non alfanumerico: <c>173ms</c> e un identificativo esadecimale che inizia con <c>173f0e...</c> non sono una FC.
    /// </summary>
    public PrivacyProbes AddNumber(string name, int value)
    {
        _patterns.Add((
            $"{name} = {value}",
            new Regex($@"(?:(?<![\w.])(?<!\d[.,])|(?<=\[[\d,.\s]*,\s*)){value}(?:(?=(?:bpm|W|watts?|rpm)\b)|(?!\w))(?![.,]\d{{4}})", RegexOptions.CultureInvariant)));
        return this;
    }

    /// <summary>Un frammento di testo (contenuto del file, nome del file, corpo dell'evento), senza distinguere maiuscole.</summary>
    public PrivacyProbes AddText(string name, string text, bool ignoreCase = true)
    {
        var options = ignoreCase ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase : RegexOptions.CultureInvariant;
        _patterns.Add(($"{name} '{text}'", new Regex(Regex.Escape(text), options)));
        return this;
    }

    public PrivacyProbes AddEventBody()
    {
        foreach (var fragment in EventBodyFragments)
        {
            AddText("corpo dell'evento", fragment, ignoreCase: false);
        }

        return this;
    }

    public PrivacyProbes Merge(PrivacyProbes other)
    {
        _patterns.AddRange(other._patterns);
        return this;
    }

    /// <summary>Descrizioni (che cosa e dove) di tutte le corrispondenze trovate nelle voci date; vuoto = nessuna perdita.</summary>
    public IReadOnlyList<string> Find(IEnumerable<LogEntry> entries)
    {
        var findings = new List<string>();
        foreach (var entry in entries)
        {
            foreach (var (name, pattern) in _patterns)
            {
                var match = pattern.Match(entry.Text);
                if (match.Success)
                {
                    var from = Math.Max(0, match.Index - 40);
                    var excerpt = entry.Text.Substring(from, Math.Min(entry.Text.Length - from, 120));
                    findings.Add($"[{entry.Service}/{entry.Level}] {entry.Category}: {name} in «{excerpt}»");
                }
            }
        }

        return findings;
    }

    private static string Rounded(double value, int decimals, CultureInfo culture) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, culture);

    private static string Truncated(double value, int decimals, CultureInfo culture)
    {
        var factor = Math.Pow(10, decimals);
        return (Math.Truncate(value * factor) / factor).ToString("F" + decimals, culture);
    }
}
