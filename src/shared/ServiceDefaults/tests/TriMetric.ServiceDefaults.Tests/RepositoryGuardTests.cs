using Xunit;

namespace TriMetric.ServiceDefaults.Tests;

/// <summary>Guardia di repository (REQ-034): nessun codice di produzione abilita il logging dei dati sensibili di EF Core.</summary>
public sealed class RepositoryGuardTests
{
    // Composta a runtime: il file del test non contiene la stringa vietata e non si auto-attiva.
    private static readonly string Forbidden = "EnableSensitive" + "DataLogging";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("global.json non trovato risalendo dalla cartella dei binari.");
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj");

    [Fact]
    public void ProductionSources_NeverEnableSensitiveLoggingOfData()
    {
        var root = FindRepositoryRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(root, path)))
            .ToList();

        Assert.NotEmpty(sources); // la guardia deve davvero scorrere dei file

        var offenders = sources
            .Where(path => File.ReadAllText(path).Contains(Forbidden, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(offenders.Count == 0, $"{Forbidden} e vietato (REQ-034): {string.Join(", ", offenders)}");
    }
}
