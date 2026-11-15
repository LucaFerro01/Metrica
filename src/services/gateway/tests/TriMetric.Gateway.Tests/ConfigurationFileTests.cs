using System.Text.Json;

namespace TriMetric.Gateway.Tests;

/// <summary>Lettore dei file <c>appsettings*.json</c> reali del servizio.</summary>
internal sealed class AppSettings(JsonElement root)
{
    public JsonElement Root { get; } = root;

    public static AppSettings Read(string fileName)
    {
        var path = Path.Combine(ServicePaths.ProjectDirectory, fileName);
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return new AppSettings(document.RootElement.Clone());
    }

    public bool TryGet(out JsonElement element, params string[] path)
    {
        element = Root;
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        return true;
    }

    public string String(params string[] path) =>
        TryGet(out var element, path) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new InvalidOperationException($"Chiave stringa assente: {string.Join(':', path)}");

    /// <summary>Tutti i valori stringa sotto un elemento, a qualunque profondita'.</summary>
    public static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}

/// <summary>
/// Controlli sui file di configurazione del servizio: solo loopback (DEC-009), rotte e cluster come da piano, nessun segreto
/// e nessuna regola di log che riattivi il log della query string (REQ-034). Non avviano nulla.
/// </summary>
public sealed class ConfigurationFileTests
{
    [Fact]
    public void AppSettings_BindKestrelToLocalhostOnPort5080_Only()
    {
        var settings = AppSettings.Read("appsettings.json");

        Assert.True(settings.TryGet(out var endpoints, "Kestrel", "Endpoints"));
        var endpoint = Assert.Single(endpoints.EnumerateObject());
        Assert.Equal("http://localhost:5080", settings.String("Kestrel", "Endpoints", endpoint.Name, "Url"));
    }

    [Fact]
    public void NoAppSettingsFile_ListensOnAWildcardOrOnAnotherPort()
    {
        var files = ServicePaths.AppSettingsFiles();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var settings = AppSettings.Read(Path.GetFileName(file));
            Assert.False(settings.TryGet(out _, "Urls"), $"{Path.GetFileName(file)}: la chiave 'Urls' e' vietata, vale Kestrel:Endpoints.");

            if (!settings.TryGet(out var kestrel, "Kestrel"))
            {
                continue;
            }

            foreach (var value in AppSettings.Strings(kestrel))
            {
                Assert.DoesNotContain("0.0.0.0", value);
                Assert.DoesNotContain("*", value);
                Assert.DoesNotContain("+", value);
                Assert.DoesNotContain("[::]", value);
                Assert.Equal("http://localhost:5080", value);
            }
        }
    }

    [Fact]
    public void LaunchSettings_DefineNoUrlAndNoWildcardBinding_AndRunInDevelopment()
    {
        var path = Path.Combine(ServicePaths.ProjectDirectory, "Properties", "launchSettings.json");
        Assert.True(File.Exists(path), "launchSettings.json atteso (come gli altri servizi).");

        // L'indirizzo viene solo da Kestrel:Endpoints: un applicationUrl (ASPNETCORE_URLS) potrebbe aprire altre interfacce.
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("applicationUrl", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASPNETCORE_URLS", text, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "http://+:", "https://+:", "[::]", "0.0.0.0", "*" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(text);
        var profile = Assert.Single(document.RootElement.GetProperty("profiles").EnumerateObject());
        Assert.Equal("Project", profile.Value.GetProperty("commandName").GetString());
        Assert.False(profile.Value.GetProperty("launchBrowser").GetBoolean());
        Assert.Equal("Development", profile.Value.GetProperty("environmentVariables").GetProperty("ASPNETCORE_ENVIRONMENT").GetString());
    }

    [Fact]
    public void Clusters_PointToTheServicesOnLoopback()
    {
        var settings = AppSettings.Read("appsettings.json");

        Assert.True(settings.TryGet(out var clusters, "ReverseProxy", "Clusters"));
        Assert.Equal(["activities", "ingestion"], clusters.EnumerateObject().Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal("http://127.0.0.1:5081", settings.String("ReverseProxy", "Clusters", "ingestion", "Destinations", "primary", "Address"));
        Assert.Equal("http://127.0.0.1:5082", settings.String("ReverseProxy", "Clusters", "activities", "Destinations", "primary", "Address"));
        foreach (var cluster in clusters.EnumerateObject())
        {
            Assert.Single(cluster.Value.GetProperty("Destinations").EnumerateObject());
        }
    }

    [Fact]
    public void Routes_AreExactlyTheTwoDeclaredOnes_WithoutHostRestrictionsOrTotalTimeout()
    {
        var settings = AppSettings.Read("appsettings.json");

        Assert.True(settings.TryGet(out var routes, "ReverseProxy", "Routes"));
        Assert.Equal(["activities", "ingestion"], routes.EnumerateObject().Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal("ingestion", settings.String("ReverseProxy", "Routes", "ingestion", "ClusterId"));
        Assert.Equal("/api/ingestion/{**remainder}", settings.String("ReverseProxy", "Routes", "ingestion", "Match", "Path"));
        Assert.Equal("activities", settings.String("ReverseProxy", "Routes", "activities", "ClusterId"));
        Assert.Equal("/api/activities/{**remainder}", settings.String("ReverseProxy", "Routes", "activities", "Match", "Path"));
        foreach (var route in routes.EnumerateObject())
        {
            Assert.False(route.Value.TryGetProperty("Match", out var match) && match.TryGetProperty("Hosts", out _));
            Assert.False(route.Value.TryGetProperty("Timeout", out _), $"{route.Name}: nessun timeout totale sulla rotta (upload lenti ma legittimi).");
        }
    }

    [Fact]
    public void ActivityTimeout_IsTheIdleTimeoutOf100Seconds_NotATotalTimeout()
    {
        var settings = AppSettings.Read("appsettings.json");

        foreach (var cluster in new[] { "ingestion", "activities" })
        {
            Assert.Equal("00:01:40", settings.String("ReverseProxy", "Clusters", cluster, "HttpRequest", "ActivityTimeout"));
        }
    }

    [Fact]
    public void NoConnectionStrings_NoSecrets_NoDevelopmentIdentity()
    {
        foreach (var file in ServicePaths.AppSettingsFiles())
        {
            var name = Path.GetFileName(file);
            var settings = AppSettings.Read(name);

            Assert.False(settings.TryGet(out _, "ConnectionStrings"), $"{name}: il gateway non ha database.");
            Assert.False(settings.TryGet(out _, "Dev"), $"{name}: il gateway non ha un'identita' di sviluppo.");
            Assert.False(settings.TryGet(out _, "Nats"), $"{name}: il gateway non usa NATS.");
        }
    }

    [Fact]
    public void AppSettings_HaveNoProviderSpecificLogLevelRules_AndNoLowerHostingOrProxyLevel()
    {
        // REQ-034: AddServiceDefaults filtra Microsoft.AspNetCore.Hosting.Diagnostics a Warning (log di path e query string) e il
        // gateway fa lo stesso per Yarp.ReverseProxy (che a Information logga l'URL di destinazione con la query).
        // Una regola Logging:<provider>:LogLevel (es. Console) vincerebbe sul filtro e lo riattiverebbe.
        foreach (var file in ServicePaths.AppSettingsFiles())
        {
            var name = Path.GetFileName(file);
            var settings = AppSettings.Read(name);
            if (!settings.TryGet(out var logging, "Logging"))
            {
                continue;
            }

            foreach (var provider in logging.EnumerateObject().Where(p => p.Name != "LogLevel"))
            {
                Assert.False(
                    provider.Value.ValueKind == JsonValueKind.Object && provider.Value.TryGetProperty("LogLevel", out _),
                    $"{name}: Logging:{provider.Name}:LogLevel e' vietato (REQ-034).");
            }

            if (logging.TryGetProperty("LogLevel", out var levels))
            {
                foreach (var rule in levels.EnumerateObject().Where(r =>
                    r.Name.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)
                    || r.Name.StartsWith("Yarp", StringComparison.Ordinal)))
                {
                    Assert.True(
                        Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(rule.Value.GetString(), out var parsed)
                        && parsed >= Microsoft.Extensions.Logging.LogLevel.Warning,
                        $"{name}: {rule.Name} deve restare almeno Warning (REQ-034).");
                }
            }
        }
    }

    [Fact]
    public void ProductionSources_NeverEnableSensitiveLoggingOfData_AndDoNotWriteToTheConsoleDirectly()
    {
        // Composta a runtime: il file del test non contiene la stringa vietata (REQ-034).
        var forbidden = "EnableSensitive" + "DataLogging";
        var sources = ServicePaths.ProductionSources().ToList();

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, p => File.ReadAllText(p).Contains(forbidden, StringComparison.Ordinal));
        Assert.DoesNotContain(sources, p => File.ReadAllText(p).Contains("Console.", StringComparison.Ordinal));
    }
}
