using System.Security.Claims;

namespace Metrica.ServiceDefaults;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Identificativo dell'utente dal claim <c>sub</c>. Lancia se il claim manca, e duplicato (ambiguo),
    /// non e un UUID in formato standard con trattini o e l'UUID vuoto.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subjects = principal.FindAll("sub").ToList();
        if (subjects.Count > 1)
        {
            throw new InvalidOperationException("Il principal ha piu di un claim 'sub': identita ambigua.");
        }

        return subjects.Count == 1 && TryParseUserId(subjects[0].Value, out var userId)
            ? userId
            : throw new InvalidOperationException("Il claim 'sub' manca o non e un UUID valido e non vuoto.");
    }

    /// <summary>Solo UUID standard (formato "D", con trattini) e diverso da <see cref="Guid.Empty"/>.</summary>
    internal static bool TryParseUserId(string? value, out Guid userId) =>
        Guid.TryParseExact(value, "D", out userId) && userId != Guid.Empty;
}
