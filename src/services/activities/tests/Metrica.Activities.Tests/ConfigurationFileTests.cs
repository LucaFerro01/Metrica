using System.Text.Json;

namespace Metrica.Activities.Tests;

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
/// Controlli sui file di configurazione del servizio: solo loopback (DEC-009), nessun segreto fuori da Development
/// (REQ-021) e nessuna regola di log che riattivi il log della query string (REQ-034). Non avviano container.
/// </summary>
[Trait("Category", "Persistence")]
public sealed class ConfigurationFileTests
{
    [Fact]
    public void AppSettings_BindKestrelOnlyToLoopbackOnPort5082()
    {
        var settings = AppSettings.Read("appsettings.json");

        Assert.True(settings.TryGet(out var endpoints, "Kestrel", "Endpoints"));
        var endpoint = Assert.Single(endpoints.EnumerateObject());
        Assert.Equal("http://127.0.0.1:5082", settings.String("Kestrel", "Endpoints", endpoint.Name, "Url"));
    }

    [Fact]
    public void NoAppSettingsFile_ListensOnAnotherInterface()
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
                Assert.DoesNotContain("localhost", value, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("http://127.0.0.1:", value);
            }
        }
    }

    [Fact]
    public void LaunchSettings_DefineNoUrlAndNoWildcardBinding_AndRunInDevelopment()
    {
        var path = Path.Combine(ServicePaths.ProjectDirectory, "Properties", "launchSettings.json");
        Assert.True(File.Exists(path), "launchSettings.json atteso (come ingestion).");

        // L'indirizzo viene solo da Kestrel:Endpoints: un applicationUrl (ASPNETCORE_URLS) potrebbe aprire altre interfacce.
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("applicationUrl", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASPNETCORE_URLS", text, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "http://+:", "https://+:", "[::]", "0.0.0.0", "*", "localhost" })
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
    public void ConnectionStrings_ExistOnlyInTheDevelopmentFile()
    {
        foreach (var file in ServicePaths.AppSettingsFiles())
        {
            var name = Path.GetFileName(file);
            var settings = AppSettings.Read(name);

            if (name == "appsettings.Development.json")
            {
                Assert.True(settings.TryGet(out _, "ConnectionStrings", "Activities"));
            }
            else
            {
                Assert.False(settings.TryGet(out _, "ConnectionStrings"), $"{name}: nessuna stringa di connessione fuori da Development.");
            }
        }
    }

    [Fact]
    public void DevelopmentFile_SetsTheSameFixedUserIdAsIngestion()
    {
        var userId = AppSettings.Read("appsettings.Development.json").String("Dev", "UserId");

        // Lo stesso UUID di ingestion: l'utente che carica un file e' quello che poi lo vede (walking skeleton).
        Assert.Equal("00000000-0000-4000-8000-000000000001", userId);
        Assert.False(AppSettings.Read("appsettings.json").TryGet(out _, "Dev"), "L'identita' di sviluppo non va nel file comune.");
    }

    [Fact]
    public void AppSettings_HaveNoProviderSpecificLogLevelRules_AndNoLowerHostingDiagnosticsLevel()
    {
        // REQ-034: AddServiceDefaults filtra Microsoft.AspNetCore.Hosting.Diagnostics a Warning (log di path e query string).
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

            foreach (var level in AllLogLevelSections(logging))
            {
                foreach (var rule in level.EnumerateObject().Where(r => r.Name.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal)))
                {
                    Assert.True(
                        rule.Value.ValueKind == JsonValueKind.String
                        && Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(rule.Value.GetString(), out var parsed)
                        && parsed >= Microsoft.Extensions.Logging.LogLevel.Warning,
                        $"{name}: Microsoft.AspNetCore.Hosting.Diagnostics deve restare almeno Warning (REQ-034).");
                }
            }
        }
    }

    [Fact]
    public void AppSettings_SilenceTheEfCommandLogCategory_WithACategoryRuleNotAProviderRule()
    {
        // I comandi EF (con parametri) non devono finire nei log a livello Information.
        Assert.Equal("Warning", AppSettings.Read("appsettings.json")
            .String("Logging", "LogLevel", "Microsoft.EntityFrameworkCore.Database.Command"));
    }

    [Fact]
    public void AppSettings_AreValidJsonAndKeepTheNatsDefault()
    {
        var settings = AppSettings.Read("appsettings.json");

        Assert.Equal("nats://127.0.0.1:4222", settings.String("Nats", "Url"));
    }

    [Fact]
    public void ProjectSources_NeverEnableSensitiveLoggingOfData()
    {
        // Composta a runtime: il file del test non contiene la stringa vietata (REQ-034).
        var forbidden = "EnableSensitive" + "DataLogging";
        var sources = Directory.EnumerateFiles(ServicePaths.ProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, p => File.ReadAllText(p).Contains(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionSources_DoNotWriteToTheConsoleDirectly()
    {
        var sources = Directory.EnumerateFiles(ServicePaths.ProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, p => File.ReadAllText(p).Contains("Console.", StringComparison.Ordinal));
    }

    private static IEnumerable<JsonElement> AllLogLevelSections(JsonElement logging)
    {
        if (logging.TryGetProperty("LogLevel", out var common) && common.ValueKind == JsonValueKind.Object)
        {
            yield return common;
        }

        foreach (var provider in logging.EnumerateObject().Where(p => p.Name != "LogLevel" && p.Value.ValueKind == JsonValueKind.Object))
        {
            if (provider.Value.TryGetProperty("LogLevel", out var level) && level.ValueKind == JsonValueKind.Object)
            {
                yield return level;
            }
        }
    }
}
