using System.Globalization;
using System.Text;

namespace Metrica.Ingestion.Tests;

/// <summary>Costruisce GPX in memoria per i test (valori testuali: si controlla la precisione in ingresso).</summary>
internal static class GpxBuilder
{
    public const string Ns11 = "http://www.topografix.com/GPX/1/1";
    public const string Ns10 = "http://www.topografix.com/GPX/1/0";
    public const string TpxNs = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";

    /// <summary>Un trkpt. <paramref name="inner"/> e' il contenuto oltre a ele e time (estensioni, speed GPX 1.0...).</summary>
    public static string Pt(string lat, string lon, string? time, string? ele = null, string? inner = null)
        => $"<trkpt lat=\"{lat}\" lon=\"{lon}\">"
           + (ele is null ? "" : $"<ele>{ele}</ele>")
           + (time is null ? "" : $"<time>{time}</time>")
           + inner
           + "</trkpt>";

    public static string Seg(params string[] points) => "<trkseg>" + string.Concat(points) + "</trkseg>";

    public static string Trk(string? type, params string[] segments)
        => "<trk>" + (type is null ? "" : $"<type>{type}</type>") + string.Concat(segments) + "</trk>";

    public static string Doc(string body, string ns = Ns11, string? prolog = null)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{prolog}<gpx version=\"1.1\" creator=\"test\" xmlns=\"{ns}\" xmlns:gpxtpx=\"{TpxNs}\">{body}</gpx>";

    public static MemoryStream Bytes(string xml) => new(Encoding.UTF8.GetBytes(xml));

    /// <summary>Estensione Garmin con frequenza cardiaca e cadenza.</summary>
    public static string Tpx(string? hr, string? cad)
        => "<extensions><gpxtpx:TrackPointExtension>"
           + (hr is null ? "" : $"<gpxtpx:hr>{hr}</gpxtpx:hr>")
           + (cad is null ? "" : $"<gpxtpx:cad>{cad}</gpxtpx:cad>")
           + "</gpxtpx:TrackPointExtension></extensions>";

    /// <summary>Istante UTC a <paramref name="seconds"/> secondi da 2024-05-01T06:00:00Z, in formato ISO 8601.</summary>
    public static string T(double seconds)
        => new DateTime(2024, 5, 1, 6, 0, 0, DateTimeKind.Utc).AddSeconds(seconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Genera un GPX con <paramref name="count"/> punti a 1 Hz scrivendo direttamente su un flusso in memoria.
    /// Con <paramref name="allColumns"/> ogni punto ha quota, FC, cadenza, potenza e velocita'.
    /// </summary>
    public static MemoryStream Generate(int count, bool allColumns)
    {
        var ms = new MemoryStream();
        using (var w = new StreamWriter(ms, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
        {
            w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><gpx version=\"1.1\" creator=\"test\" xmlns=\"{Ns11}\" xmlns:gpxtpx=\"{TpxNs}\"><trk><type>running</type><trkseg>");
            var start = new DateTime(2024, 5, 1, 6, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < count; i++)
            {
                var lat = (45 + i * 1e-6).ToString("F7", CultureInfo.InvariantCulture);
                var time = start.AddSeconds(i).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                w.Write($"<trkpt lat=\"{lat}\" lon=\"7.5\">");
                if (allColumns)
                {
                    w.Write($"<ele>{(100 + (i % 100) * 0.1).ToString("F1", CultureInfo.InvariantCulture)}</ele>");
                }

                w.Write($"<time>{time}</time>");
                if (allColumns)
                {
                    w.Write($"<extensions><power>{200 + (i % 50)}</power><gpxtpx:TrackPointExtension><gpxtpx:hr>{100 + (i % 80)}</gpxtpx:hr><gpxtpx:cad>{80 + (i % 10)}</gpxtpx:cad><gpxtpx:speed>3.0</gpxtpx:speed></gpxtpx:TrackPointExtension></extensions>");
                }

                w.Write("</trkpt>");
            }

            w.Write("</trkseg></trk></gpx>");
        }

        ms.Position = 0;
        return ms;
    }
}
