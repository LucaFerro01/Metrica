using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TriMetric.ServiceDefaults;

public static class DevelopmentIdentityExtensions
{
    internal const string DevelopmentScheme = "DevelopmentIdentity";
    internal const string DenyScheme = "Deny";
    internal const string UserIdConfigurationKey = "Dev:UserId";

    /// <summary>
    /// Registra l'autenticazione predefinita del servizio (DEC-006). CONFINE DI SICUREZZA:
    /// solo in <c>Development</c> ogni richiesta e autenticata come <c>Dev:UserId</c>; in ogni altro ambiente
    /// lo schema <c>DevelopmentIdentity</c> non esiste e lo schema predefinito <c>Deny</c> risponde sempre 401.
    /// </summary>
    public static IHostApplicationBuilder AddDevelopmentIdentity(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAuthorization();

        if (builder.Environment.IsDevelopment())
        {
            var userId = ReadDevelopmentUserId(builder.Configuration);
            builder.Services.AddAuthentication(DevelopmentScheme)
                .AddScheme<DevelopmentIdentityOptions, DevelopmentIdentityHandler>(
                    DevelopmentScheme, options => options.UserId = userId);
        }
        else
        {
            builder.Services.AddAuthentication(DenyScheme)
                .AddScheme<AuthenticationSchemeOptions, DenyAuthenticationHandler>(DenyScheme, _ => { });
        }

        return builder;
    }

    // Solo da configurazione, mai dalla richiesta.
    private static Guid ReadDevelopmentUserId(IConfiguration configuration) =>
        ClaimsPrincipalExtensions.TryParseUserId(configuration[UserIdConfigurationKey], out var userId)
            ? userId
            : throw new InvalidOperationException(
                $"In Development la configurazione '{UserIdConfigurationKey}' deve contenere un UUID valido e non vuoto (formato con trattini).");
}
