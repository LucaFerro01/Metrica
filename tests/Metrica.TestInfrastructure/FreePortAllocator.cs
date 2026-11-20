using System.Net;
using System.Net.Sockets;

namespace Metrica.TestInfrastructure;

/// <summary>
/// Sceglie porte TCP host in un intervallo <b>non dinamico</b> (20000-39999), fuori dall'intervallo delle porte effimere
/// del sistema operativo (le porte in uscita e le mappature casuali di Docker non vi cadono), cosi un container che
/// mantiene la stessa porta dopo uno stop/start non la trova presa da un socket qualsiasi.
/// Una porta e' restituita solo se un <see cref="TcpListener"/> su tutte le interfacce (come il binding di Docker) riesce
/// ad aprirla; le porte gia' assegnate nel processo non sono riassegnate finche non sono rilasciate.
/// </summary>
public static class FreePortAllocator
{
    public const int RangeStart = 20000;
    public const int RangeEnd = 39999;

    private const int MaxAttemptsPerPort = 2000;

    private static readonly object Gate = new();
    private static readonly HashSet<int> Assigned = [];

    /// <summary>
    /// Restituisce <paramref name="count"/> porte distinte e disponibili. I listener restano aperti finche non sono state
    /// scelte tutte (altrimenti una scelta potrebbe restituire la porta appena rilasciata da un'altra).
    /// </summary>
    public static IReadOnlyList<int> Allocate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        lock (Gate)
        {
            var listeners = new List<TcpListener>();
            var ports = new List<int>();
            try
            {
                for (var attempt = 0; attempt < MaxAttemptsPerPort * count && ports.Count < count; attempt++)
                {
                    var port = Random.Shared.Next(RangeStart, RangeEnd + 1);
                    if (Assigned.Contains(port) || ports.Contains(port))
                    {
                        continue;
                    }

                    var listener = new TcpListener(IPAddress.Any, port);
                    try
                    {
                        listener.Start();
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    listeners.Add(listener);
                    ports.Add(port);
                }

                if (ports.Count < count)
                {
                    throw new InvalidOperationException(
                        $"Impossibile trovare {count} porte libere nell'intervallo {RangeStart}-{RangeEnd}.");
                }

                Assigned.UnionWith(ports);
                return ports;
            }
            finally
            {
                foreach (var listener in listeners)
                {
                    listener.Stop();
                }
            }
        }
    }

    /// <summary>Rende di nuovo assegnabili le porte (alla distruzione della fixture o dopo un tentativo fallito).</summary>
    public static void Release(IEnumerable<int> ports)
    {
        lock (Gate)
        {
            Assigned.ExceptWith(ports);
        }
    }
}
