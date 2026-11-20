extern alias ActivitiesApp;

namespace Metrica.IntegrationTests.Hosting;

internal sealed class ActivitiesService(IReadOnlyDictionary<string, string> settings, Microsoft.Extensions.Logging.ILoggerProvider logs)
    : ServiceHost<ActivitiesApp::Program>(settings, logs);
