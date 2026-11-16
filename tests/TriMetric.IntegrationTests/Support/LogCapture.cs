using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TriMetric.IntegrationTests.Support;

/// <summary>Una voce di log di uno dei tre servizi, con tutto cio che puo' contenere un dato: messaggio, stato, scope, eccezione.</summary>
internal sealed record LogEntry(string Service, string Category, LogLevel Level, EventId EventId, string Text);

/// <summary>Raccolta condivisa dei log dei tre servizi (REQ-034).</summary>
internal sealed class LogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public int Count => _entries.Count;

    public void Add(LogEntry entry) => _entries.Enqueue(entry);

    /// <summary>Voci aggiunte dopo che la raccolta aveva <paramref name="mark"/> elementi.</summary>
    public IReadOnlyList<LogEntry> Since(int mark) => [.. _entries.Skip(mark)];

    /// <summary>Provider da collegare a un servizio.</summary>
    public ILoggerProvider ProviderFor(string service) => new Provider(this, service);

    private sealed class Provider(LogSink sink, string service) : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(Provider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var parts = new List<string> { formatter(state, exception) };

                // Valori di stato (anche quelli che il messaggio formattato non mostra) e valori degli scope.
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    parts.Add("state: " + string.Join("; ", values.Select(v => $"{v.Key}={v.Value}")));
                }

                var scopes = new List<string>();
                owner._scopes.ForEachScope(
                    (scope, list) => list.Add(scope is IEnumerable<KeyValuePair<string, object?>> pairs
                        ? string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"))
                        : scope?.ToString() ?? string.Empty),
                    scopes);
                if (scopes.Count > 0)
                {
                    parts.Add("scopes: " + string.Join(" | ", scopes));
                }

                if (exception is not null)
                {
                    parts.Add("exception: " + exception);
                }

                owner.Sink.Add(new LogEntry(owner.Service, category, logLevel, eventId, string.Join(" || ", parts)));
            }
        }

        public string Service => service;

        public LogSink Sink => sink;
    }
}
