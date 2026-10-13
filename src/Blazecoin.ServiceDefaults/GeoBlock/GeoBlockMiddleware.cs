using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blazecoin.ServiceDefaults.GeoBlock;

/// <summary>
/// Sitewide 451 for blocked jurisdictions (2026-08-14 decision: the WHOLE site, not just the
/// regulated flows — for the UK the FCA offence is the promotion itself). Runs AFTER
/// UseForwardedHeaders so <c>RemoteIpAddress</c> is the real client behind a trusted proxy.
/// Loopback/private/unknown addresses always pass (dev, service-to-service, health checks),
/// so enabling it never breaks the internal fleet.
/// </summary>
public sealed class GeoBlockMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IGeoIpCountryResolver _resolver;
    private readonly ILogger<GeoBlockMiddleware> _logger;
    private readonly bool _enabled;
    private readonly HashSet<string> _blocked;
    private readonly GeoBlockOptions _options;

    public GeoBlockMiddleware(
        RequestDelegate next,
        IGeoIpCountryResolver resolver,
        IOptions<GeoBlockOptions> options,
        ILogger<GeoBlockMiddleware> logger)
    {
        _next = next;
        _resolver = resolver;
        _logger = logger;
        _options = options.Value;
        _enabled = options.Value.Enabled;
        _blocked = new HashSet<string>(options.Value.BlockedCountries, StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The operator unlock link is honoured even while the block is dark, so the cookie can be
        // set ahead of enabling (the maintenance bypass works the same way).
        if (HandleBypassRequest(context)) return;

        if (!_enabled || !_resolver.IsAvailable)
        {
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress;
        if (ip == null || IsPrivateOrLocal(ip))
        {
            await _next(context);
            return;
        }

        var iso = _resolver.ResolveIsoCode(ip);
        if (iso == null || !_blocked.Contains(iso))
        {
            await _next(context);
            return;
        }

        if (HasValidBypassCookie(context.Request))
        {
            // The operator's own browser (2026-10-07). Country only, as everywhere in this middleware.
            _logger.LogInformation("GeoBlock bypass: {Country} request to {Path} let through by the operator cookie", iso, context.Request.Path);
            await _next(context);
            return;
        }

        // Country only in the log — the visitor's IP is not retained here (GDPR posture).
        _logger.LogInformation("GeoBlock 451: {Country} request to {Path}", iso, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status451UnavailableForLegalReasons;
        if (WantsJson(context.Request))
        {
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                "{\"type\":\"https://tools.ietf.org/html/rfc7725\",\"title\":\"Unavailable For Legal Reasons\"," +
                "\"status\":451,\"detail\":\"This service is not available in your region.\"}");
        }
        else
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                "<title>Not available in your region</title>" +
                "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
                "background:#0b0b0b;color:#e8e8e8;font-family:Georgia,'Times New Roman',serif;text-align:center}" +
                ".b{max-width:34rem;padding:2rem;border:1px solid #dc3545;border-radius:.5rem;background:#111}" +
                "h1{color:#dc3545;font-size:1.5rem;letter-spacing:.06em}p{line-height:1.6;color:#bbb}</style></head>" +
                "<body><div class=\"b\"><h1>NOT AVAILABLE IN YOUR REGION</h1>" +
                "<p>Blazecoin is not offered to persons in your jurisdiction for legal and regulatory reasons.</p>" +
                "<p style=\"font-size:.85rem;color:#777\">HTTP 451 &mdash; Unavailable For Legal Reasons</p></div></body></html>");
        }
    }

    private static bool WantsJson(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        || request.Headers.Accept.Any(a => a != null && a.Contains("application/json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>GET {BypassPath}?token=VALUE</c> with the configured token: set the bypass cookie and send the
    /// browser to the home page. A wrong or missing token is NOT answered specially - the request simply
    /// carries on into the normal block decision, so the path reveals nothing. Returns true when handled.
    /// </summary>
    private bool HandleBypassRequest(HttpContext context)
    {
        if (string.IsNullOrEmpty(_options.BypassToken)) return false;
        if (!context.Request.Path.Equals(_options.BypassPath, StringComparison.OrdinalIgnoreCase)) return false;

        var token = context.Request.Query["token"].ToString();
        if (token.Length == 0 || !FixedTimeEquals(token, _options.BypassToken)) return false;

        context.Response.Cookies.Append(_options.BypassCookieName, _options.BypassToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddHours(_options.BypassCookieHours)
        });
        var ip = context.Connection.RemoteIpAddress;
        var where = ip == null || IsPrivateOrLocal(ip) ? "private/local"
            : (_resolver.IsAvailable ? _resolver.ResolveIsoCode(ip) : null) ?? "unknown";
        _logger.LogWarning("GeoBlock bypass cookie ISSUED ({Country})", where);
        context.Response.Redirect("/");
        context.Response.StatusCode = StatusCodes.Status302Found;
        return true;
    }

    private bool HasValidBypassCookie(HttpRequest request) =>
        !string.IsNullOrEmpty(_options.BypassToken)
        && request.Cookies.TryGetValue(_options.BypassCookieName, out var value)
        && FixedTimeEquals(value, _options.BypassToken);

    /// <summary>Constant-time string compare via SHA-256 digests (equalises length too).</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    internal static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);   // link-local
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }
}

public static class GeoBlockMiddlewareExtensions
{
    public static IApplicationBuilder UseGeoBlock(this IApplicationBuilder app) =>
        app.UseMiddleware<GeoBlockMiddleware>();
}
