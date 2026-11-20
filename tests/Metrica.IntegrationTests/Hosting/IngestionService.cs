extern alias IngestionApp;

namespace Metrica.IntegrationTests.Hosting;

internal sealed class IngestionService(IReadOnlyDictionary<string, string> settings, Microsoft.Extensions.Logging.ILoggerProvider logs)
    : ServiceHost<IngestionApp::Program>(settings, logs);
