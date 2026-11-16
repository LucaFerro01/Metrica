extern alias IngestionApp;

namespace TriMetric.IntegrationTests.Hosting;

internal sealed class IngestionService(IReadOnlyDictionary<string, string> settings, Microsoft.Extensions.Logging.ILoggerProvider logs)
    : ServiceHost<IngestionApp::Program>(settings, logs);
