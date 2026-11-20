using System.Text.Json;
using System.Xml.Linq;

namespace Metrica.Gateway.Tests;

/// <summary>
/// Il gateway non ha logica di business, database, messaggistica ne' dipendenze dagli altri servizi (REQ-020): solo YARP e
/// ServiceDefaults. Controlla il progetto, le dipendenze risolte (deps.json) e i sorgenti.
/// </summary>
public sealed class IsolationTests
{
    private static readonly string[] ForbiddenFragments =
    [
        "Microsoft.EntityFrameworkCore", "Npgsql", "EFCore", "NATS", "Metrica.Messaging", "Metrica.Contracts",
        "Metrica.Ingestion", "Metrica.Activities",
    ];

    [Fact]
    public void Project_ReferencesOnlyYarpAndServiceDefaults()
    {
        var project = XDocument.Load(Path.Combine(ServicePaths.ProjectDirectory, "Metrica.Gateway.csproj"));

        var packages = project.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToList();
        var projects = project.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!)).ToList();

        Assert.Equal(["Yarp.ReverseProxy"], packages);
        Assert.Equal(["Metrica.ServiceDefaults"], projects);
        Assert.Equal("Microsoft.NET.Sdk.Web", (string?)project.Root!.Attribute("Sdk"));
    }

    [Fact]
    public void ResolvedDependencies_ContainNoDataAccessMessagingOrOtherServices()
    {
        var depsFile = Path.Combine(AppContext.BaseDirectory, "Metrica.Gateway.deps.json");
        Assert.True(File.Exists(depsFile), "deps.json del gateway atteso accanto ai test.");
        using var document = JsonDocument.Parse(File.ReadAllText(depsFile));

        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().Select(l => l.Name).ToList();

        Assert.Contains(libraries, l => l.StartsWith("Yarp.ReverseProxy/", StringComparison.Ordinal));
        Assert.Contains(libraries, l => l.StartsWith("Metrica.ServiceDefaults/", StringComparison.Ordinal));
        foreach (var fragment in ForbiddenFragments)
        {
            Assert.DoesNotContain(libraries, l => l.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void LoadedGatewayAssembly_DoesNotReferenceForbiddenAssemblies()
    {
        var referenced = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        foreach (var fragment in ForbiddenFragments)
        {
            Assert.DoesNotContain(referenced, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Sources_DoNotUseDataAccessMessagingOrOtherServices()
    {
        var sources = ServicePaths.ProductionSources().ToList();
        Assert.NotEmpty(sources);

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var fragment in ForbiddenFragments)
            {
                Assert.False(text.Contains(fragment, StringComparison.Ordinal), $"{Path.GetFileName(source)} cita '{fragment}'.");
            }

            Assert.DoesNotContain("DbContext", text, StringComparison.Ordinal);
        }
    }
}
