using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Metrica.Messaging.Tests;

/// <summary>Voce di log catturata: categoria, livello, messaggio formattato ed eventuale eccezione.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Logger in memoria per verificare cosa il componente scrive nei log.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private const string ComponentCategoryPrefix = "Metrica.Messaging";

    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    /// <summary>Solo i log del componente (categoria <c>Metrica.Messaging.*</c>), non quelli interni di NATS.Net.</summary>
    public IReadOnlyList<LogEntry> ComponentEntries =>
        Entries.Where(e => e.Category.StartsWith(ComponentCategoryPrefix, StringComparison.Ordinal)).ToList();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>Polling con timeout: attende che compaia una voce del componente che soddisfa il predicato.</summary>
    public async Task<bool> WaitForComponentEntryAsync(Func<LogEntry, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ComponentEntries.Any(predicate))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return ComponentEntries.Any(predicate);
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}
