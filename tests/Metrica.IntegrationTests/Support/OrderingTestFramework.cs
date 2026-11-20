using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Metrica.IntegrationTests.Support.OrderingTestFramework", "Metrica.IntegrationTests")]
[assembly: Xunit.TestCaseOrderer("Metrica.IntegrationTests.Support.KeepOrderTestCaseOrderer", "Metrica.IntegrationTests")]

namespace Metrica.IntegrationTests.Support;

/// <summary>
/// Permette di eseguire gli STESSI test nello STESSO processo (stessi contenitori) in ordine diverso, per dimostrare che gli scenari
/// non dipendono l'uno dall'altro. Variabile d'ambiente <c>METRICA_TEST_ORDER</c>: <c>reverse</c> (inverso di quello di
/// scoperta) oppure <c>random</c> (con <c>METRICA_TEST_SEED</c> intero per ripetere lo stesso ordine). Senza variabile non cambia nulla.
/// L'ordine scelto e' scritto nei messaggi diagnostici di xUnit.
/// </summary>
public sealed class OrderingTestFramework(IMessageSink diagnosticMessageSink) : XunitTestFramework(diagnosticMessageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new OrderingExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class OrderingExecutor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            var ordered = Reorder([.. testCases]);
            using var assemblyRunner = new XunitTestAssemblyRunner(TestAssembly, ordered, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await assemblyRunner.RunAsync();
        }

        private IReadOnlyList<IXunitTestCase> Reorder(List<IXunitTestCase> testCases)
        {
            var mode = Environment.GetEnvironmentVariable("METRICA_TEST_ORDER");
            if (string.IsNullOrEmpty(mode))
            {
                DiagnosticMessageSink.OnMessage(new DiagnosticMessage("Ordine dei test (predefinito): " + Describe(testCases)));
                return testCases;
            }

            List<IXunitTestCase> ordered;
            if (mode.Equals("reverse", StringComparison.OrdinalIgnoreCase))
            {
                ordered = [.. Enumerable.Reverse(testCases)];
            }
            else if (mode.Equals("random", StringComparison.OrdinalIgnoreCase))
            {
                var seed = int.TryParse(Environment.GetEnvironmentVariable("METRICA_TEST_SEED"), out var parsed) ? parsed : Environment.TickCount;
                var random = new Random(seed);
                ordered = [.. testCases.OrderBy(_ => random.Next())];
                DiagnosticMessageSink.OnMessage(new DiagnosticMessage($"METRICA_TEST_ORDER=random, seed {seed}"));
            }
            else
            {
                throw new InvalidOperationException($"METRICA_TEST_ORDER sconosciuto: '{mode}' (valori: reverse, random).");
            }

            // Anche i casi di una Theory sono riordinati; le classi seguono l'ordine di prima comparsa dei loro casi.
            DiagnosticMessageSink.OnMessage(new DiagnosticMessage(
                $"Ordine dei test ({mode}): " + Describe(ordered)));
            return ordered;
        }

        private static string Describe(IEnumerable<IXunitTestCase> testCases) =>
            string.Join(" | ", testCases.Select(t => $"{t.TestMethod.TestClass.Class.Name.Split('.')[^1]}.{t.TestMethod.Method.Name.Split('_')[0]}"));
    }
}

/// <summary>
/// Mantiene l'ordine dei casi di test ricevuto (di norma xUnit li riordina per un hash fisso dentro ogni classe): cosi l'ordine scelto
/// da <see cref="OrderingTestFramework"/> vale anche per i metodi della stessa classe.
/// </summary>
public sealed class KeepOrderTestCaseOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase => testCases;
}
