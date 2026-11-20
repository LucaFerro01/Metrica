using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Metrica.ServiceDefaults.Tests;

internal sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyList<string> Scopes)
{
    /// <summary>Tutto il testo del log (messaggio, scopi) in un'unica stringa.</summary>
    public string AllText => string.Join(' ', [Message, .. Scopes]);
}

/// <summary>Provider di log in memoria che supporta gli scopi (ISupportExternalScope).</summary>
internal sealed class InMemoryLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private static string Render(object? scope) => scope switch
    {
        IEnumerable<KeyValuePair<string, object?>> pairs => string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}")),
        _ => scope?.ToString() ?? string.Empty,
    };

    private sealed class InMemoryLogger(InMemoryLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            owner._scopes.ForEachScope((scope, list) => list.Add(Render(scope)), scopes);
            var message = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
            owner._entries.Enqueue(new LogEntry(category, logLevel, message, scopes));
        }
    }
}
