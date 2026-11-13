using System.Globalization;
using System.Text;
using System.Xml;
using TriMetric.Contracts;

namespace TriMetric.Ingestion.Parsing;

/// <summary>
/// Parser GPX 1.0 e 1.1 con un <see cref="XmlReader"/> indurito (input non attendibile), letto in avanti a ciclo
/// lineare: nessun albero di oggetti e nessuna ricorsione.
/// </summary>
/// <remarks>
/// <para><b>Sicurezza.</b> Qualsiasi DTD e' rifiutato (<c>DtdProcessing.Prohibit</c>: niente entita' interne, esterne
/// o espansioni), nessun resolver (<c>XmlResolver = null</c>: nessun accesso a file o rete), documento limitato a
/// 64 milioni di caratteri e a 1024 caratteri da entita' (seconda linea di difesa, inutile con il DTD vietato).
/// La profondita' di annidamento e' limitata a <see cref="Plausibility.MaxXmlDepth"/> (un GPX valido arriva a circa 8):
/// l'<c>XElement</c> e le sue proprieta' (<c>Value</c>) ricorrono sulla profondita' e con decine di migliaia di
/// livelli uccidono il processo con uno stack overflow, quindi non si usano. Ogni nodo si legge con
/// <see cref="Advance"/>, che controlla la profondita'. Il tetto di <see cref="StreamLimits.MaxPoints"/> punti scatta
/// appena superato; i segmenti sono al massimo <see cref="Plausibility.MaxLaps"/>.</para>
/// <para><b>Contenuto.</b> Si leggono <c>trk</c>, <c>trkseg</c>, <c>trkpt</c> (<c>lat</c>, <c>lon</c>, <c>ele</c>,
/// <c>time</c>), il <c>type</c> della traccia (sport) e, nelle <c>extensions</c> del punto, <c>hr</c>, <c>cad</c>,
/// <c>power</c> e <c>speed</c> (qualunque namespace e profondita': Garmin <c>TrackPointExtension</c> e altri); in
/// GPX 1.0 anche <c>speed</c> figlio del punto. Waypoint, rotte e il resto si ignorano. Un punto senza <c>time</c>
/// valido o con coordinate non valide invalida il file; quota e valori delle estensioni illeggibili, con elementi
/// annidati o fuori scala (vedi <see cref="Plausibility"/>) si trattano come assenti.</para>
/// <para><b>Giri.</b> Se il file ha piu' di un segmento (<c>trkseg</c>) non vuoto si crea un giro per segmento, in
/// ordine di inizio (quindi un GPX con un <c>trkseg</c> per pausa da' un giro per pausa); con un solo segmento non
/// ci sono giri. Piu' di <see cref="Plausibility.MaxLaps"/> segmenti non vuoti invalidano il file. Distanza di un
/// giro: haversine tra i punti del solo segmento.</para>
/// <para><b>Misure.</b> Vedi <see cref="ActivityAssembler"/> (distanza, tempo in movimento a soglia 0,5 m/s, dislivello
/// come somma delle salite sui valori grezzi, senza smoothing). Lo sport viene dal primo <c>type</c> non vuoto.</para>
/// </remarks>
public sealed class GpxParser : IActivityFileParser
{
    private const long MaxCharactersInDocument = 64L * 1024 * 1024;
    private const int CancellationCheckEveryPoints = 256;

    /// <summary>Lunghezza massima del testo di un valore (numeri e istanti); oltre, il valore e' illeggibile.</summary>
    private const int MaxValueLength = 256;

    private const string Invalid = "Il file non e' un GPX valido.";
    private const string MissingTime = "Il file GPX contiene punti senza un istante valido.";
    private const string InvalidPosition = "Il file GPX contiene punti con coordinate non valide.";
    private const string TooDeep = "Il file GPX e' annidato troppo in profondita'.";
    private const string TooManySegments = "Il file GPX contiene troppi segmenti.";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharactersInDocument,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    private const string Gpx10 = "http://www.topografix.com/GPX/1/0";
    private const string Gpx11 = "http://www.topografix.com/GPX/1/1";

    public bool CanParse(string fileName)
        => !string.IsNullOrEmpty(fileName)
           && string.Equals(Path.GetExtension(fileName), ".gpx", StringComparison.OrdinalIgnoreCase);

    public ParsedActivity Parse(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Il flusso non e' leggibile.", nameof(content));
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            return ParseDocument(content, ct);
        }
        catch (ActivityFileException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is XmlException or IOException or System.Text.DecoderFallbackException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // Nessuna eccezione interna: il messaggio dell'XmlException puo' citare parti del file.
            throw new ActivityFileException(Invalid);
        }
    }

    /// <summary>Stato della lettura di un documento.</summary>
    private sealed class State
    {
        public State(CancellationToken ct) => Ct = ct;

        public CancellationToken Ct { get; }
        public List<RawPoint> Points { get; } = [];
        public List<(int Start, int Count)> Segments { get; } = [];
        public string? Type { get; set; }
    }

    private static ParsedActivity ParseDocument(Stream content, CancellationToken ct)
    {
        using var reader = XmlReader.Create(new CancellableStream(content, ct), ReaderSettings);

        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "gpx" || !IsGpxNamespace(reader.NamespaceURI))
        {
            throw new ActivityFileException(Invalid);
        }

        var state = new State(ct);
        if (EnterChildren(reader, out var depth))
        {
            while (!AtEnd(reader, depth))
            {
                if (IsGpxElement(reader, "trk"))
                {
                    ReadTrack(reader, state);
                }
                else
                {
                    SkipNode(reader);
                }
            }
        }

        while (Advance(reader))
        {
            // Legge fino alla fine per validare che dopo l'elemento radice non ci sia altro contenuto.
        }

        if (state.Points.Count == 0)
        {
            throw new ActivityFileException(ActivityAssembler.NoPointsMessage);
        }

        var laps = state.Segments.Count > 1 ? BuildLaps(state.Points, state.Segments) : [];
        return ActivityAssembler.Build(MapSport(state.Type), state.Points, laps);
    }

    // ---- Navigazione lineare: tutto passa da Advance, che limita la profondita'.

    private static bool Advance(XmlReader reader)
    {
        var more = reader.Read();
        if (reader.Depth > Plausibility.MaxXmlDepth)
        {
            throw new ActivityFileException(TooDeep);
        }

        return more;
    }

    /// <summary>
    /// Entra nei figli dell'elemento corrente. Falso se e' vuoto (e il lettore e' gia' oltre). Con vero, ripetere
    /// <see cref="AtEnd"/> finche' e' falso per scorrere i figli.
    /// </summary>
    private static bool EnterChildren(XmlReader reader, out int depth)
    {
        depth = reader.Depth;
        var empty = reader.IsEmptyElement;
        Advance(reader);
        return !empty;
    }

    /// <summary>Vero (consumando il tag di chiusura) quando il lettore e' alla fine dell'elemento di profondita' <paramref name="depth"/>.</summary>
    private static bool AtEnd(XmlReader reader, int depth)
    {
        if (reader.EOF)
        {
            throw new ActivityFileException(Invalid);
        }

        if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
        {
            Advance(reader);
            return true;
        }

        return false;
    }

    /// <summary>Salta il nodo corrente e, se e' un elemento, tutto il suo contenuto.</summary>
    private static void SkipNode(XmlReader reader)
    {
        if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement)
        {
            Advance(reader);
            return;
        }

        var depth = reader.Depth;
        while (Advance(reader) && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
        }

        Advance(reader);
    }

    /// <summary>
    /// Testo di un elemento semplice, consumandolo. <c>null</c> se contiene elementi figli o e' troppo lungo.
    /// </summary>
    private static string? ReadText(XmlReader reader)
    {
        if (!EnterChildren(reader, out var depth))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        var valid = true;
        while (!AtEnd(reader, depth))
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (text.Length + reader.Value.Length > MaxValueLength)
                {
                    valid = false;
                }
                else
                {
                    text.Append(reader.Value);
                }

                Advance(reader);
            }
            else
            {
                valid = false;
                SkipNode(reader);
            }
        }

        return valid ? text.ToString() : null;
    }

    // ---- Struttura GPX

    private static void ReadTrack(XmlReader reader, State state)
    {
        if (!EnterChildren(reader, out var depth))
        {
            return;
        }

        while (!AtEnd(reader, depth))
        {
            if (IsGpxElement(reader, "trkseg"))
            {
                ReadSegment(reader, state);
            }
            else if (IsGpxElement(reader, "type"))
            {
                var text = ReadText(reader)?.Trim();
                if (state.Type is null && !string.IsNullOrEmpty(text))
                {
                    state.Type = text;
                }
            }
            else
            {
                SkipNode(reader);
            }
        }
    }

    private static void ReadSegment(XmlReader reader, State state)
    {
        var first = state.Points.Count;
        if (EnterChildren(reader, out var depth))
        {
            while (!AtEnd(reader, depth))
            {
                if (IsGpxElement(reader, "trkpt"))
                {
                    if (state.Points.Count >= StreamLimits.MaxPoints)
                    {
                        throw new TooManyPointsException();
                    }

                    if (state.Points.Count % CancellationCheckEveryPoints == 0)
                    {
                        state.Ct.ThrowIfCancellationRequested();
                    }

                    state.Points.Add(ReadPoint(reader));
                }
                else
                {
                    SkipNode(reader);
                }
            }
        }

        if (state.Points.Count > first)
        {
            if (state.Segments.Count >= Plausibility.MaxLaps)
            {
                throw new ActivityFileException(TooManySegments);
            }

            state.Segments.Add((first, state.Points.Count - first));
        }
    }

    private static RawPoint ReadPoint(XmlReader reader)
    {
        if (!TryParseDouble(reader.GetAttribute("lat"), out var lat)
            || !TryParseDouble(reader.GetAttribute("lon"), out var lon)
            || lat is < -90 or > 90
            || lon is < -180 or > 180)
        {
            throw new ActivityFileException(InvalidPosition);
        }

        string? timeText = null;
        double? altitude = null, speed = null, heartRate = null, cadence = null, power = null;

        if (EnterChildren(reader, out var depth))
        {
            while (!AtEnd(reader, depth))
            {
                if (IsGpxElement(reader, "time"))
                {
                    var text = ReadText(reader)?.Trim();
                    timeText ??= text;
                }
                else if (IsGpxElement(reader, "ele"))
                {
                    var ele = ParseNumber(ReadText(reader));
                    altitude ??= ele;
                }
                else if (IsGpxElement(reader, "speed")) // GPX 1.0
                {
                    var v = ParseNonNegative(ReadText(reader));
                    speed ??= v;
                }
                else if (IsGpxElement(reader, "extensions"))
                {
                    ReadExtensions(reader, ref heartRate, ref cadence, ref power, ref speed);
                }
                else
                {
                    SkipNode(reader);
                }
            }
        }

        if (timeText is null
            || !DateTimeOffset.TryParse(
                timeText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var time))
        {
            throw new ActivityFileException(MissingTime);
        }

        return new RawPoint(time.ToUniversalTime(), lat, lon, altitude, heartRate, power, cadence, speed, null);
    }

    /// <summary>
    /// Cerca a qualunque profondita' (entro il limite) <c>hr</c>, <c>cad</c>, <c>power</c> e <c>speed</c>: il primo valore di
    /// ciascuno vince; il resto si ignora.
    /// </summary>
    private static void ReadExtensions(XmlReader reader, ref double? heartRate, ref double? cadence, ref double? power, ref double? speed)
    {
        if (!EnterChildren(reader, out var depth))
        {
            return;
        }

        while (!AtEnd(reader, depth))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                Advance(reader);
                continue;
            }

            // ReadText consuma sempre l'elemento: l'assegnazione condizionale (??=) segue la lettura, mai la salta.
            switch (reader.LocalName.ToLowerInvariant())
            {
                case "hr" or "heartrate":
                    var hr = ParseNonNegative(ReadText(reader));
                    heartRate ??= hr;
                    break;
                case "cad" or "cadence":
                    var cad = ParseNonNegative(ReadText(reader));
                    cadence ??= cad;
                    break;
                case "power" or "powerinwatts" or "watts":
                    var watts = ParseNonNegative(ReadText(reader));
                    power ??= watts;
                    break;
                case "speed":
                    var v = ParseNonNegative(ReadText(reader));
                    speed ??= v;
                    break;
                default:
                    Advance(reader); // scende nei figli: i loro nodi si leggono nel ciclo
                    break;
            }
        }
    }

    private static List<LapData> BuildLaps(List<RawPoint> points, List<(int Start, int Count)> segments)
    {
        var laps = new List<LapData>(segments.Count);
        foreach (var (start, count) in segments)
        {
            var slice = points.GetRange(start, count);
            ActivityAssembler.SortByTime(slice);
            var measures = ActivityAssembler.Measure(slice);
            laps.Add(ActivityAssembler.MakeLap(slice[0].Time, (slice[^1].Time - slice[0].Time).TotalSeconds, measures.Distance));
        }

        return laps;
    }

    private static bool IsGpxNamespace(string ns) => ns.Length == 0 || ns == Gpx11 || ns == Gpx10;

    private static bool IsGpxElement(XmlReader reader, string localName)
        => reader.NodeType == XmlNodeType.Element && reader.LocalName == localName && IsGpxNamespace(reader.NamespaceURI);

    private static double? ParseNumber(string? text)
        => TryParseDouble(text, out var v) ? v : null;

    private static double? ParseNonNegative(string? text)
        => TryParseDouble(text, out var v) && v >= 0 ? v : null;

    private static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        return text is not null
               && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    private static Sport MapSport(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "running" or "run" => Sport.Run,
        "cycling" or "biking" or "ride" => Sport.Ride,
        "swimming" or "swim" => Sport.Swim,
        _ => Sport.Other,
    };
}
