using System.Buffers.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Stripboard.Application.Services;

namespace Stripboard.Infrastructure.Services;

/// <summary>
/// Turns an HTTP request into a <see cref="CallerIdentity"/>, so authorisation is decided
/// from who the platform says is calling rather than from who the payload claims to be.
///
/// **Why reading the token without verifying its signature is sound here, and only here.**
/// The services this runs in are deployed to Cloud Run with `--no-allow-unauthenticated`.
/// Google's front end validates the identity token — signature, audience, expiry — and
/// rejects the request before our container ever sees it. A request that reaches this code
/// with an Authorization header has already been through that. Re-verifying would mean
/// fetching and caching Google's JWKS to re-derive a decision the platform already made.
///
/// That reasoning only holds behind Cloud Run, so it is gated on `K_SERVICE`, which the
/// runtime sets and nothing else does. Locally the header is ignored entirely and every
/// caller is <see cref="CallerIdentity.Asserted"/> — unable to commit, which is the correct
/// posture for a machine with no authentication in front of it.
/// </summary>
public sealed class CallerIdentityResolver
{
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<CallerIdentityResolver>? _logger;
    private readonly bool _behindAuthenticatingPlatform;

    public CallerIdentityResolver(
        IHttpContextAccessor http,
        ILogger<CallerIdentityResolver>? logger = null,
        bool? behindAuthenticatingPlatform = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        _behindAuthenticatingPlatform = behindAuthenticatingPlatform
            ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("K_SERVICE"));
    }

    /// <summary>
    /// Resolves the caller. <paramref name="asserted"/> is whatever name the request body
    /// carried; it is used only when the platform proved nothing, and then it is marked
    /// unverified so it cannot commit.
    /// </summary>
    public CallerIdentity Resolve(string? asserted = null)
    {
        var context = _http.HttpContext;
        if (context is null || !_behindAuthenticatingPlatform)
        {
            return CallerIdentity.Asserted(asserted);
        }

        // Identity-Aware Proxy, when a human is in front of it.
        var iapEmail = context.Request.Headers["X-Goog-Authenticated-User-Email"].ToString();
        if (!string.IsNullOrWhiteSpace(iapEmail))
        {
            // IAP prefixes the value with "accounts.google.com:".
            var separator = iapEmail.LastIndexOf(':');
            return CallerIdentity.FromToken(separator >= 0 ? iapEmail[(separator + 1)..] : iapEmail);
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && EmailFromValidatedToken(authorization["Bearer ".Length..].Trim()) is { } email)
        {
            return CallerIdentity.FromToken(email);
        }

        return CallerIdentity.Asserted(asserted);
    }

    /// <summary>Reads the `email` claim out of a token Cloud Run has already validated.</summary>
    private string? EmailFromValidatedToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(DecodeSegment(parts[1]));
            return payload.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Malformed is not "trusted anonymous": fall back to asserted, which cannot commit.
            _logger?.LogWarning("Ignoring an unreadable identity token: {Reason}", exception.Message);
            return null;
        }
    }

    private static byte[] DecodeSegment(string segment)
    {
        // JWT uses base64url without padding.
        var normalised = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalised.PadRight(
            normalised.Length + (4 - normalised.Length % 4) % 4, '='));
    }
}
