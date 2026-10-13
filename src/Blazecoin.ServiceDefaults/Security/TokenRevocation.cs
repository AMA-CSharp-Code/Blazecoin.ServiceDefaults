using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Blazecoin.ServiceDefaults.Security;

/// <summary>
/// JWT revocation enforcement (ASVS V3.3 — B2, deny-list architecture).
///
/// The Identity service owns the deny-list (revoked jti's + per-user issued-before cutoffs). A consuming
/// service keeps an in-memory copy, refreshed by <see cref="TokenRevocationPoller"/> from
/// <c>GET {Identity}/api/auth/token-revocations</c> every <c>TokenRevocation:PollSeconds</c> (default 30),
/// authenticated by the shared <c>TokenRevocation:FeedKey</c> (X-Service-Key). Revocation latency here is
/// therefore one poll interval; Identity itself enforces instantly.
///
/// Availability choice — FAIL-OPEN: if the feed is unreachable the last snapshot stays in force, and if it
/// has never loaded tokens pass unchecked (pre-B2 behaviour) with a clear warning. A feed outage must not
/// take the whole API down.
/// </summary>
public sealed class RevokedTokenCache
{
    private sealed record State(HashSet<string> Jtis, Dictionary<string, DateTime> UserCutoffs);

    private volatile State _state = new(
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, DateTime>(StringComparer.Ordinal));

    /// <summary>True once the first successful feed fetch has been applied.</summary>
    public bool Loaded { get; private set; }

    public void Replace(IEnumerable<string> jtis, IEnumerable<KeyValuePair<string, DateTime>> userCutoffs)
    {
        _state = new State(
            new HashSet<string>(jtis, StringComparer.Ordinal),
            new Dictionary<string, DateTime>(userCutoffs, StringComparer.Ordinal));
        Loaded = true;
    }

    public bool IsRevoked(string? jti, string? userId, DateTime? issuedUtc)
    {
        var state = _state;

        if (!string.IsNullOrEmpty(jti) && state.Jtis.Contains(jti))
            return true;

        if (!string.IsNullOrEmpty(userId) && state.UserCutoffs.TryGetValue(userId, out var cutoff))
        {
            // Missing iat (token minted before B2) counts as issued-at-epoch — fails any
            // cutoff for the user. Only bites once a cutoff exists, so it's the safe default.
            if (!issuedUtc.HasValue || issuedUtc.Value < cutoff)
                return true;
        }

        return false;
    }
}

/// <summary>Background refresh of <see cref="RevokedTokenCache"/> from the Identity feed.</summary>
public sealed class TokenRevocationPoller : BackgroundService
{
    private readonly RevokedTokenCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TokenRevocationPoller> _logger;

    public TokenRevocationPoller(
        RevokedTokenCache cache, IConfiguration configuration, ILogger<TokenRevocationPoller> logger)
    {
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    private sealed class Feed
    {
        public List<string> Jtis { get; set; } = new();
        public List<CutoffEntry> UserCutoffs { get; set; } = new();
        public sealed class CutoffEntry
        {
            public string UserId { get; set; } = string.Empty;
            public DateTime CutoffUtc { get; set; }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseUrl = _configuration["TokenRevocation:IdentityBaseUrl"];
        var feedKey = _configuration["TokenRevocation:FeedKey"];
        var pollSeconds = Math.Max(5, _configuration.GetValue("TokenRevocation:PollSeconds", 30));

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(feedKey))
        {
            _logger.LogWarning(
                "TokenRevocation feed not configured (IdentityBaseUrl/FeedKey) — revoked JWTs "
                + "will NOT be rejected by this service until it is");
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Add("X-Service-Key", feedKey);
        http.Timeout = TimeSpan.FromSeconds(15);

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var firstLoad = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await http.GetAsync("api/auth/token-revocations", stoppingToken);
                response.EnsureSuccessStatusCode();

                var feed = await response.Content.ReadFromJsonAsync<Feed>(jsonOptions, stoppingToken);
                if (feed != null)
                {
                    _cache.Replace(
                        feed.Jtis,
                        feed.UserCutoffs.Select(c => new KeyValuePair<string, DateTime>(c.UserId, c.CutoffUtc)));

                    if (firstLoad)
                    {
                        _logger.LogInformation(
                            "Token-revocation feed loaded: {Jtis} revoked jti(s), {Cutoffs} user cutoff(s); polling every {Poll}s",
                            feed.Jtis.Count, feed.UserCutoffs.Count, pollSeconds);
                        firstLoad = false;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Fail-open: keep enforcing the last snapshot; never crash the service.
                _logger.LogWarning(ex, "Token-revocation feed poll failed — keeping last snapshot"
                    + (_cache.Loaded ? "" : " (no snapshot yet: revoked tokens are NOT being rejected)"));
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }
}

/// <summary>Plugs the cache check into JwtBearer validation.</summary>
public static class TokenRevocationJwtEvents
{
    public static JwtBearerEvents Create() => new()
    {
        OnTokenValidated = context =>
        {
            var cache = context.HttpContext.RequestServices.GetRequiredService<RevokedTokenCache>();

            var jwt = context.SecurityToken as JsonWebToken;
            var jti = context.Principal?.FindFirst("jti")?.Value ?? jwt?.Id;
            var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            DateTime? issuedUtc = jwt != null && jwt.IssuedAt != DateTime.MinValue ? jwt.IssuedAt : null;

            if (cache.IsRevoked(jti, userId, issuedUtc))
                context.Fail("Token has been revoked.");

            return Task.CompletedTask;
        }
    };
}
