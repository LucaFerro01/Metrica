extern alias GatewayApp;

namespace TriMetric.IntegrationTests.Hosting;

internal sealed class GatewayService(IReadOnlyDictionary<string, string> settings, Microsoft.Extensions.Logging.ILoggerProvider logs)
    : ServiceHost<GatewayApp::Program>(settings, logs);
