using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriMetric.ServiceDefaults;

internal sealed class DevelopmentIdentityOptions : AuthenticationSchemeOptions
{
    public Guid UserId { get; set; }
}

/// <summary>Autentica ogni richiesta come l'utente di sviluppo configurato; registrato solo in Development.</summary>
internal sealed class DevelopmentIdentityHandler(
    IOptionsMonitor<DevelopmentIdentityOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<DevelopmentIdentityOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", Options.UserId.ToString())], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
