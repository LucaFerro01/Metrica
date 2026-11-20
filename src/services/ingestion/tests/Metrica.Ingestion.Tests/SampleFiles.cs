namespace Metrica.Ingestion.Tests;

/// <summary>Accesso ai file di esempio copiati accanto all'assembly di test.</summary>
internal static class SampleFiles
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);

    public static FileStream Open(string name) => File.OpenRead(PathOf(name));
}
