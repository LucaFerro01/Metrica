using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TriMetric.Gateway.Tests;

/// <summary>
/// Client HTTP/1.1 minimale su socket: serve dove <c>HttpClient</c> nasconderebbe l'esito (413 inviato prima che il corpo sia
/// stato letto per intero: il server chiude e un client ad alto livello puo' vedere solo la connessione chiusa).
/// La risposta si legge in parallelo alla scrittura, cosi' lo stato arriva anche se la scrittura poi fallisce.
/// </summary>
internal static class RawHttp
{
    public const string Crlf = "\r\n";

    /// <summary>Invia intestazioni (e un corpo opzionale) e restituisce la risposta: intestazioni, o anche il corpo con <paramref name="readUntilClose"/>.</summary>
    public static async Task<string> SendAsync(
        string baseAddress, string headBlock, Func<NetworkStream, Task>? writeBody = null, bool readUntilClose = false)
    {
        var uri = new Uri(baseAddress);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(uri.Host), uri.Port);
        var stream = client.GetStream();

        var reader = Task.Run(async () =>
        {
            var received = new StringBuilder();
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    received.Append(Encoding.Latin1.GetString(buffer, 0, read));
                    if (!readUntilClose && received.ToString().Contains(Crlf + Crlf, StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
                // connessione azzerata dal server dopo la risposta
            }

            return received.ToString();
        });

        try
        {
            await stream.WriteAsync(Encoding.Latin1.GetBytes(headBlock + Crlf));
            if (writeBody is not null)
            {
                await writeBody(stream);
            }
        }
        catch (IOException)
        {
            // il server ha chiuso mentre scrivevamo: la risposta e' gia' in lettura
        }

        return await reader.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public static async Task WriteChunkAsync(NetworkStream stream, ReadOnlyMemory<byte> data)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{data.Length:X}{Crlf}"));
        await stream.WriteAsync(data);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Crlf));
    }

    public static Task WriteLastChunkAsync(NetworkStream stream) => stream.WriteAsync(Encoding.ASCII.GetBytes("0" + Crlf + Crlf)).AsTask();

    public static string StatusLine(string response) => response.Split(Crlf, 2)[0];
}
