extern alias GatewayApp;

namespace Metrica.IntegrationTests.Hosting;

internal sealed class GatewayService(IReadOnlyDictionary<string, string> settings, Microsoft.Extensions.Logging.ILoggerProvider logs)
    : ServiceHost<GatewayApp::Program>(settings, logs);
