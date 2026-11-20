using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Metrica.Ingestion;

/// <summary>Il corpo ha superato il tetto complessivo: si interrompe la lettura, senza leggere oltre.</summary>
internal sealed class UploadBodyTooLargeException : Exception
{
}

/// <summary>Perche' la lettura del corpo multipart non e' arrivata in fondo.</summary>
internal enum UploadReadFailure
{
    None,

    /// <summary>Il contenuto non e' <c>multipart/form-data</c> con un <c>boundary</c> valido.</summary>
    NotMultipart,

    /// <summary>Struttura multipart rotta, troncata o con troppe parti.</summary>
    Malformed,

    /// <summary>Il corpo supera il tetto complessivo (file piu' margine per l'overhead multipart).</summary>
    BodyTooLarge,
}

/// <summary>
/// Esito della lettura: quanti campi <c>file</c> ci sono, il nome dichiarato e il contenuto del primo, letto con un tetto.
/// </summary>
internal sealed class UploadFormReadResult
{
    public UploadReadFailure Failure { get; init; }

    /// <summary>Campi chiamati <c>file</c> (con o senza nome di file): ne serve esattamente uno.</summary>
    public int FileFields { get; init; }

    /// <summary>Il campo <c>file</c> ha un nome di file (<c>filename</c>): senza, e' un campo di testo.</summary>
    public bool FileFieldHasFileName { get; init; }

    /// <summary>Nome dichiarato dal client, non ripulito (da non usare mai cosi' com'e').</summary>
    public string? RawFileName { get; init; }

    /// <summary>Contenuto del primo campo <c>file</c>; <c>null</c> se oltre il tetto o se non c'e' il campo.</summary>
    public MemoryStream? Content { get; init; }

    /// <summary>Il primo campo <c>file</c> supera il tetto per file (il contenuto e' stato scartato, non tenuto in memoria).</summary>
    public bool FileTooLarge { get; init; }
}

/// <summary>
/// Lettura in streaming del corpo <c>multipart/form-data</c> di <c>POST /uploads</c>. Si evita di proposito il legame a
/// <c>IFormFile</c> (e <c>ReadFormAsync</c>): il framework riverserebbe su disco i file oltre 64 KB, e gli errori (413, corpo
/// troncato) arriverebbero come eccezioni diverse a seconda del server. Qui il corpo passa per un flusso con tetto
/// (indipendente da Kestrel, quindi vale anche senza <c>Content-Length</c> e con il server di prova), il file e' tenuto in
/// memoria solo fino a <c>maxFileBytes</c> e il resto e' scartato.
/// </summary>
internal static class MultipartUploadReader
{
    public const string FileFieldName = "file";

    /// <summary>Numero massimo di parti accettate: l'upload ne usa una, il resto e' rumore o abuso.</summary>
    public const int MaxParts = 32;

    private const int MaxBoundaryLength = 70;   // RFC 2046
    private const int BufferSize = 81_920;

    public static async Task<UploadFormReadResult> ReadAsync(HttpRequest request, long maxFileBytes, long maxBodyBytes, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(UploadReadFailure.NotMultipart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).ToString();
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaxBoundaryLength)
        {
            return Fail(UploadReadFailure.Malformed);
        }

        if (request.ContentLength > maxBodyBytes)
        {
            return Fail(UploadReadFailure.BodyTooLarge);   // dichiarato troppo grande: non si legge nemmeno
        }

        var reader = new MultipartReader(boundary, new LimitedReadStream(request.Body, maxBodyBytes))
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
        };

        var fileFields = 0;
        var fileHasName = false;
        string? rawFileName = null;
        MemoryStream? content = null;
        var tooLarge = false;

        try
        {
            for (var parts = 0; ;)
            {
                var section = await reader.ReadNextSectionAsync(ct);
                if (section is null)
                {
                    break;
                }

                if (++parts > MaxParts)
                {
                    return Fail(UploadReadFailure.Malformed);
                }

                var isFileField = TryGetContentDisposition(section, out var disposition)
                    && HeaderUtilities.RemoveQuotes(disposition!.Name).Equals(FileFieldName, StringComparison.Ordinal);
                if (!isFileField || ++fileFields > 1)
                {
                    await section.Body.CopyToAsync(Stream.Null, ct);   // campo che non ci interessa (compreso un eventuale userId)
                    continue;
                }

                fileHasName = disposition!.FileName.HasValue || disposition.FileNameStar.HasValue;
                var name = disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName;
                rawFileName = HeaderUtilities.RemoveQuotes(name).Value;
                (content, tooLarge) = await CopyCappedAsync(section.Body, maxFileBytes, ct);
            }
        }
        catch (UploadBodyTooLargeException)
        {
            return Fail(UploadReadFailure.BodyTooLarge);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Fail(UploadReadFailure.BodyTooLarge);   // il tetto di Kestrel (stessa soglia)
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadHttpRequestException or FormatException or ArgumentException)
        {
            ct.ThrowIfCancellationRequested();   // un client che si disconnette non e' una richiesta malformata
            return Fail(UploadReadFailure.Malformed);
        }

        return new UploadFormReadResult
        {
            Failure = UploadReadFailure.None,
            FileFields = fileFields,
            FileFieldHasFileName = fileHasName,
            RawFileName = rawFileName,
            Content = tooLarge ? null : content,
            FileTooLarge = tooLarge,
        };
    }

    private static UploadFormReadResult Fail(UploadReadFailure failure) => new() { Failure = failure };

    private static bool TryGetContentDisposition(MultipartSection section, out ContentDispositionHeaderValue? disposition)
    {
        disposition = null;
        return section.ContentDisposition is { Length: > 0 } header
            && ContentDispositionHeaderValue.TryParse(header, out disposition)
            && disposition.Name.HasValue;
    }

    /// <summary>
    /// Copia fino a <paramref name="cap"/> byte. Se il file e' piu' grande scarta cio' che ha accumulato e legge il resto
    /// senza tenerlo (il tetto complessivo del corpo limita comunque quanto si legge).
    /// </summary>
    private static async Task<(MemoryStream? Content, bool TooLarge)> CopyCappedAsync(Stream source, long cap, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var content = new MemoryStream();
            var tooLarge = false;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                if (tooLarge)
                {
                    continue;
                }

                if (content.Length + read > cap)
                {
                    tooLarge = true;
                    content = new MemoryStream();   // libera subito la memoria accumulata
                    continue;
                }

                content.Write(buffer, 0, read);
            }

            return (tooLarge ? null : content, tooLarge);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Flusso in sola lettura che lancia <see cref="UploadBodyTooLargeException"/> oltre il numero di byte dato.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Solo lettura asincrona.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _total += read;
            return _total > limit ? throw new UploadBodyTooLargeException() : read;
        }
    }
}
