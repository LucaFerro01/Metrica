namespace TriMetric.TestInfrastructure;

/// <summary>Trova la radice del repository risalendo dalla cartella dei binari fino a <c>global.json</c>.</summary>
public static class RepositoryRoot
{
    private static readonly Lazy<string> Root = new(Find);

    public static string Path => Root.Value;

    /// <summary>Percorso assoluto di un file della radice del repository (verifica che esista).</summary>
    public static string File(params string[] segments)
    {
        var path = System.IO.Path.Combine([Path, .. segments]);
        if (!System.IO.File.Exists(path))
        {
            throw new FileNotFoundException($"File non trovato nel repository: {path}", path);
        }

        return path;
    }

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"global.json non trovato risalendo da {AppContext.BaseDirectory}: radice del repository sconosciuta.");
    }
}
