using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TripEx.Api.Auth;

/// <summary>
/// Authentication handler for static API Key (X-Api-Key header).
/// Used by external systems (e.g. TripEx mobile/web) for OCR endpoints.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    // Accept the standard X-Api-Key, and "Token" which is what COMBTAS/TAS sends.
    private static readonly string[] ApiKeyHeaderNames = { "X-Api-Key", "Token" };

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? providedKey = null;
        foreach (var headerName in ApiKeyHeaderNames)
        {
            if (Request.Headers.TryGetValue(headerName, out var values)
                && !string.IsNullOrEmpty(values.FirstOrDefault()))
            {
                providedKey = values.FirstOrDefault();
                break;
            }
        }

        if (string.IsNullOrEmpty(providedKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (providedKey != Options.ApiKey)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "api-key-user"),
            new Claim(ClaimTypes.Name, Options.ClientName ?? "ExternalSystem"),
            new Claim("auth_type", "api_key")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public string ApiKey { get; set; } = "";
    public string? ClientName { get; set; }
}
