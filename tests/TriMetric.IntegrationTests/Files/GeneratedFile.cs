using System.Security.Cryptography;
using TriMetric.IntegrationTests.Support;

namespace TriMetric.IntegrationTests.Files;

/// <summary>
/// Un file di attivita' generato dai test, con il nome da inviare, il contenuto e cio che i log non devono mai mostrare di lui.
/// </summary>
internal sealed class GeneratedFile(string fileName, byte[] content, PrivacyProbes probes)
{
    public string FileName { get; } = fileName;

    public byte[] Content { get; } = content;

    public PrivacyProbes Probes { get; } = probes;

    /// <summary>SHA-256 esadecimale minuscolo del contenuto: l'<c>externalId</c> atteso (fonte <c>file</c>, REQ-011).</summary>
    public string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(content));
}
