using Metrica.Contracts;

namespace Metrica.Ingestion.Parsing;

/// <summary>
/// Costruisce <see cref="ParsedActivity"/> dai punti grezzi: ordinamento, misure derivate (distanza, tempo in
/// movimento, dislivello), colonne arrotondate e riepilogo. Comune a GPX e FIT, cosi' le regole sono una sola.
/// </summary>
/// <remarks>
/// Regole delle misure derivate (usate per GPX e come ripiego per FIT quando manca la sessione):
/// <list type="bullet">
/// <item><b>Ordine:</b> i punti sono ordinati per istante (ordinamento stabile); punti con lo stesso istante restano
/// entrambi, quindi gli offset sono non decrescenti.</item>
/// <item><b>Distanza:</b> somma delle distanze haversine (raggio medio terrestre 6.371.008,8 m) tra punti consecutivi
/// che hanno posizione; senza alcuna posizione, differenza tra ultima e prima distanza cumulata dei record, se presente.</item>
/// <item><b>Tempo in movimento:</b> somma degli intervalli tra punti consecutivi (durata maggiore di zero) la cui
/// velocita' e' almeno 0,5 m/s. Velocita' dell'intervallo: haversine diviso durata se entrambi i punti hanno posizione;
/// altrimenti la velocita' scritta nel punto di arrivo; altrimenti la differenza di distanza cumulata diviso durata;
/// se nessuna e' disponibile l'intervallo conta come in movimento.</item>
/// <item><b>Dislivello positivo:</b> somma delle differenze positive tra quote consecutive (tra punti che hanno la
/// quota), senza alcun filtro o smoothing: il rumore del barometro/GPS si somma. <c>null</c> se nessun punto ha la quota.</item>
/// <item><b>Plausibilita':</b> vedi <see cref="Plausibility"/>; valori fuori scala sono assenti e istanti fuori da
/// 1980-2100 invalidano il file; tutti gli aggregati finali devono essere finiti.</item>
/// <item><b>Riepilogo:</b> medie e massimo calcolati sui valori gia' arrotondati e presenti dello stream; <c>null</c> se
/// la colonna e' assente.</item>
/// </list>
/// </remarks>
internal static class ActivityAssembler
{
    /// <summary>Velocita' minima (m/s) perche' un intervallo conti come tempo in movimento.</summary>
    public const double MovingSpeedMetersPerSecond = 0.5;

    private const double EarthRadiusMeters = 6_371_008.8;

    internal const string NoPointsMessage = "Il file non contiene punti validi dell'attivita'.";
    internal const string InvalidTimeMessage = "Il file contiene istanti fuori dall'intervallo ammesso.";
    internal const string InvalidValueMessage = "Il file contiene valori non validi.";
    internal const string TooManyLapsMessage = "Il file contiene troppi giri.";

    /// <summary>Valori che, se presenti nel file (sessione FIT), sostituiscono quelli calcolati dai punti.</summary>
    internal sealed record Overrides(
        DateTimeOffset? StartTime = null,
        double? ElapsedSeconds = null,
        double? MovingSeconds = null,
        double? DistanceMeters = null,
        double? ElevationGainMeters = null,
        double? AvgHeartRate = null,
        double? MaxHeartRate = null,
        double? AvgPowerWatts = null,
        double? AvgCadence = null);

    /// <summary>Misure derivate da una sequenza di punti gia' in ordine di tempo.</summary>
    internal readonly record struct Measures(double Distance, double Moving, double? ElevationGain);

    public static ParsedActivity Build(Sport sport, List<RawPoint> points, IReadOnlyList<LapData> laps, Overrides? overrides = null)
    {
        if (points.Count == 0)
        {
            throw new ActivityFileException(NoPointsMessage);
        }

        if (laps.Count > Plausibility.MaxLaps)
        {
            throw new ActivityFileException(TooManyLapsMessage);
        }

        SanitizePoints(points);
        overrides = Sanitize(overrides);
        SortByTime(points);
        var measures = Measure(points);

        var start = points[0].Time;
        if (overrides?.StartTime is { } declared && declared <= start)
        {
            start = declared;
        }

        var n = points.Count;
        var time = new double[n];
        var lat = new double?[n];
        var lon = new double?[n];
        var alt = new double?[n];
        var hr = new double?[n];
        var power = new double?[n];
        var cadence = new double?[n];
        var speed = new double?[n];
        bool hasLat = false, hasAlt = false, hasHr = false, hasPower = false, hasCadence = false, hasSpeed = false;

        for (var i = 0; i < n; i++)
        {
            var p = points[i];
            time[i] = Rounding.Round((p.Time - start).TotalSeconds, Rounding.TimeDecimals);
            if (p is { Latitude: { } la, Longitude: { } lo })
            {
                lat[i] = Rounding.Round(la, Rounding.CoordinateDecimals);
                lon[i] = Rounding.Round(lo, Rounding.CoordinateDecimals);
                hasLat = true;
            }

            if (p.Altitude is { } a)
            {
                alt[i] = Rounding.Round(a, Rounding.AltitudeDecimals);
                hasAlt = true;
            }

            if (p.HeartRate is { } h)
            {
                hr[i] = Rounding.Round(h, 0);
                hasHr = true;
            }

            if (p.Power is { } w)
            {
                power[i] = Rounding.Round(w, 0);
                hasPower = true;
            }

            if (p.Cadence is { } c)
            {
                cadence[i] = Rounding.Round(c, 0);
                hasCadence = true;
            }

            if (p.Speed is { } s)
            {
                speed[i] = Rounding.Round(s, Rounding.SpeedDecimals);
                hasSpeed = true;
            }
        }

        var stream = new StreamColumns(
            time,
            hasLat ? lat : null,
            hasLat ? lon : null,
            hasAlt ? alt : null,
            hasHr ? hr : null,
            hasPower ? power : null,
            hasCadence ? cadence : null,
            hasSpeed ? speed : null);

        var elapsed = overrides?.ElapsedSeconds ?? (points[^1].Time - start).TotalSeconds;
        var summary = new ActivitySummary(
            overrides?.AvgHeartRate ?? Average(stream.HeartRate),
            overrides?.MaxHeartRate ?? Max(stream.HeartRate),
            overrides?.AvgPowerWatts ?? Average(stream.PowerWatts),
            overrides?.AvgCadence ?? Average(stream.Cadence));

        var result = new ParsedActivity(
            sport,
            start.ToUniversalTime(),
            Rounding.Round(elapsed, Rounding.SummaryDecimals),
            Rounding.Round(overrides?.MovingSeconds ?? measures.Moving, Rounding.SummaryDecimals),
            Rounding.Round(overrides?.DistanceMeters ?? measures.Distance, Rounding.SummaryDecimals),
            Rounding.Round(overrides?.ElevationGainMeters ?? measures.ElevationGain, Rounding.AltitudeDecimals),
            summary,
            laps.OrderBy(l => l.StartTime).ToList(),
            stream);

        if (!AllFinite(result))
        {
            throw new ActivityFileException(InvalidValueMessage);
        }

        return result;
    }

    private static bool AllFinite(ParsedActivity a)
        => double.IsFinite(a.ElapsedSeconds)
           && double.IsFinite(a.MovingSeconds)
           && double.IsFinite(a.DistanceMeters)
           && IsFinite(a.ElevationGainMeters)
           && IsFinite(a.Summary.AvgHeartRate)
           && IsFinite(a.Summary.MaxHeartRate)
           && IsFinite(a.Summary.AvgPowerWatts)
           && IsFinite(a.Summary.AvgCadence);

    private static bool IsFinite(double? v) => v is null || double.IsFinite(v.Value);

    /// <summary>Applica i limiti di <see cref="Plausibility"/>: valori fuori scala diventano assenti, istanti fuori intervallo invalidano il file.</summary>
    private static void SanitizePoints(List<RawPoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!Plausibility.TimeInRange(p.Time))
            {
                throw new ActivityFileException(InvalidTimeMessage);
            }

            points[i] = p with
            {
                Altitude = Plausibility.In(p.Altitude, Plausibility.MinAltitude, Plausibility.MaxAltitude),
                HeartRate = Plausibility.In(p.HeartRate, Plausibility.MinHeartRate, Plausibility.MaxHeartRate),
                Power = Plausibility.In(p.Power, Plausibility.MinPower, Plausibility.MaxPower),
                Cadence = Plausibility.In(p.Cadence, Plausibility.MinCadence, Plausibility.MaxCadence),
                Speed = Plausibility.In(p.Speed, Plausibility.MinSpeed, Plausibility.MaxSpeed),
                CumulativeDistance = Plausibility.In(p.CumulativeDistance, 0, Plausibility.MaxDistance),
            };
        }
    }

    /// <summary>Valori di sessione fuori scala: si ignorano (restano quelli calcolati dai punti).</summary>
    private static Overrides? Sanitize(Overrides? o)
        => o is null
            ? null
            : new Overrides(
                o.StartTime is { } t && Plausibility.TimeInRange(t) ? t : null,
                Plausibility.In(o.ElapsedSeconds, 0, Plausibility.MaxDuration),
                Plausibility.In(o.MovingSeconds, 0, Plausibility.MaxDuration),
                Plausibility.In(o.DistanceMeters, 0, Plausibility.MaxDistance),
                Plausibility.In(o.ElevationGainMeters, 0, Plausibility.MaxAscent),
                Plausibility.In(o.AvgHeartRate, Plausibility.MinHeartRate, Plausibility.MaxHeartRate),
                Plausibility.In(o.MaxHeartRate, Plausibility.MinHeartRate, Plausibility.MaxHeartRate),
                Plausibility.In(o.AvgPowerWatts, Plausibility.MinPower, Plausibility.MaxPower),
                Plausibility.In(o.AvgCadence, Plausibility.MinCadence, Plausibility.MaxCadence));

    /// <summary>Giro con durata e distanza arrotondate al millesimo.</summary>
    public static LapData MakeLap(DateTimeOffset start, double elapsedSeconds, double distanceMeters)
    {
        if (!Plausibility.TimeInRange(start))
        {
            throw new ActivityFileException(InvalidTimeMessage);
        }

        if (!double.IsFinite(elapsedSeconds) || !double.IsFinite(distanceMeters))
        {
            throw new ActivityFileException(InvalidValueMessage);
        }

        return new LapData(
            start.ToUniversalTime(),
            Rounding.Round(elapsedSeconds, Rounding.SummaryDecimals),
            Rounding.Round(distanceMeters, Rounding.SummaryDecimals));
    }

    /// <summary>Ordina in modo stabile per istante; non fa nulla se i punti sono gia' in ordine.</summary>
    public static void SortByTime(List<RawPoint> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Time < points[i - 1].Time)
            {
                var sorted = points.OrderBy(p => p.Time).ToList();
                points.Clear();
                points.AddRange(sorted);
                return;
            }
        }
    }

    /// <summary>Distanza, tempo in movimento e dislivello di punti gia' ordinati per tempo.</summary>
    public static Measures Measure(IReadOnlyList<RawPoint> points)
    {
        double distance = 0, moving = 0, gain = 0;
        RawPoint? lastWithPosition = null;
        double? lastAltitude = null;
        var hasAltitude = false;
        double? firstCumulative = null, lastCumulative = null;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double? stepMeters = null;
            if (p.HasPosition)
            {
                if (lastWithPosition is { } q)
                {
                    stepMeters = Haversine(q.Latitude!.Value, q.Longitude!.Value, p.Latitude!.Value, p.Longitude!.Value);
                    distance += stepMeters.Value;
                }

                lastWithPosition = p;
            }

            if (p.Altitude is { } altitude)
            {
                if (lastAltitude is { } previous && altitude > previous)
                {
                    gain += altitude - previous;
                }

                lastAltitude = altitude;
                hasAltitude = true;
            }

            if (p.CumulativeDistance is { } cumulative)
            {
                firstCumulative ??= cumulative;
                lastCumulative = cumulative;
            }

            if (i == 0)
            {
                continue;
            }

            var before = points[i - 1];
            var seconds = (p.Time - before.Time).TotalSeconds;
            if (seconds <= 0)
            {
                continue;
            }

            double? intervalSpeed = null;
            if (p.HasPosition && before.HasPosition)
            {
                intervalSpeed = stepMeters!.Value / seconds;
            }
            else if (p.Speed is { } recorded)
            {
                intervalSpeed = recorded;
            }
            else if (p.CumulativeDistance is { } d1 && before.CumulativeDistance is { } d0)
            {
                intervalSpeed = (d1 - d0) / seconds;
            }

            if (intervalSpeed is null || intervalSpeed >= MovingSpeedMetersPerSecond)
            {
                moving += seconds;
            }
        }

        if (lastWithPosition is null && firstCumulative is { } first && lastCumulative is { } last && last > first)
        {
            distance = last - first;
        }

        return new Measures(distance, moving, hasAltitude ? gain : null);
    }

    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double toRadians = Math.PI / 180;
        var phi1 = lat1 * toRadians;
        var phi2 = lat2 * toRadians;
        var halfDeltaPhi = (lat2 - lat1) * toRadians / 2;
        var halfDeltaLambda = (lon2 - lon1) * toRadians / 2;
        var sinPhi = Math.Sin(halfDeltaPhi);
        var sinLambda = Math.Sin(halfDeltaLambda);
        var a = sinPhi * sinPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinLambda * sinLambda;
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static double? Average(IReadOnlyList<double?>? column)
    {
        if (column is null)
        {
            return null;
        }

        double sum = 0;
        var count = 0;
        foreach (var v in column)
        {
            if (v is { } d)
            {
                sum += d;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    private static double? Max(IReadOnlyList<double?>? column)
    {
        if (column is null)
        {
            return null;
        }

        double? max = null;
        foreach (var v in column)
        {
            if (v is { } d && (max is null || d > max))
            {
                max = d;
            }
        }

        return max;
    }
}
