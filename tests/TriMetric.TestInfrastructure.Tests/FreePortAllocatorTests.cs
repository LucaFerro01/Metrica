using System.Net;
using System.Net.Sockets;
using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.TestInfrastructure.Tests;

/// <summary>Scelta delle porte host in un intervallo non dinamico, senza collisioni (nessun contenitore necessario).</summary>
public sealed class FreePortAllocatorTests
{
    [Fact]
    public void Allocate_ReturnsDistinctPortsInsideTheStableRange()
    {
        var first = FreePortAllocator.Allocate(2);
        var second = FreePortAllocator.Allocate(2);
        try
        {
            var all = first.Concat(second).ToList();

            Assert.Equal(4, all.Distinct().Count());
            Assert.All(all, p => Assert.InRange(p, FreePortAllocator.RangeStart, FreePortAllocator.RangeEnd));
        }
        finally
        {
            FreePortAllocator.Release(first.Concat(second));
        }
    }

    [Fact]
    public void Allocate_ManyInstances_NeverCollide()
    {
        var batches = Enumerable.Range(0, 50).Select(_ => FreePortAllocator.Allocate(2)).ToList();
        try
        {
            var all = batches.SelectMany(b => b).ToList();

            Assert.Equal(all.Count, all.Distinct().Count());
        }
        finally
        {
            FreePortAllocator.Release(batches.SelectMany(b => b));
        }
    }

    [Fact]
    public void Allocate_ReturnsPortsThatAreImmediatelyBindable()
    {
        var ports = FreePortAllocator.Allocate(2);
        try
        {
            foreach (var port in ports)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
            }
        }
        finally
        {
            FreePortAllocator.Release(ports);
        }
    }

    [Fact]
    public void Allocate_SkipsPortsOccupiedByOtherListeners()
    {
        // Occupa un blocco di porte dell'intervallo (quelle gia' in uso altrove sono semplicemente saltate).
        var held = new List<TcpListener>();
        for (var port = FreePortAllocator.RangeStart; port < FreePortAllocator.RangeStart + 500; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                held.Add(listener);
            }
            catch (SocketException)
            {
            }
        }

        var occupied = held.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToHashSet();
        var batches = Enumerable.Range(0, 100).Select(_ => FreePortAllocator.Allocate(2)).ToList();
        try
        {
            Assert.All(batches.SelectMany(b => b), p => Assert.DoesNotContain(p, occupied));
        }
        finally
        {
            FreePortAllocator.Release(batches.SelectMany(b => b));
            held.ForEach(l => l.Stop());
        }
    }

    [Fact]
    public void Release_MakesPortsEligibleAgain_AndRejectsNonPositiveCounts()
    {
        var ports = FreePortAllocator.Allocate(1);
        FreePortAllocator.Release(ports);

        Assert.Throws<ArgumentOutOfRangeException>(() => FreePortAllocator.Allocate(0));
    }
}
