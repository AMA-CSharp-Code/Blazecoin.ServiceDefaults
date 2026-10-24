using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

/// <summary>Where the deny-list comes from and how often it is read. Section <c>TokenRevocation</c>.</summary>
public sealed class TokenRevocationOptions
{
    public const string SectionName = "TokenRevocation";
    /// <summary>The Identity service's base URL; the feed is <c>GET api/auth/token-revocations</c> under it.</summary>
    public string? IdentityBaseUrl { get; set; }
    /// <summary>Shared service key sent as <c>X-Service-Key</c>.</summary>
    public string? FeedKey { get; set; }
    /// <summary>Seconds between polls; anything under 5 is read as 5.</summary>
    public int PollSeconds { get; set; } = 30;
    /// <summary>Per-request timeout for the feed call.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(IdentityBaseUrl) && !string.IsNullOrWhiteSpace(FeedKey);
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(5, PollSeconds));
}

/// <summary>One fetch-and-apply of the feed. Separated from the loop so it can be tested with a stub handler.</summary>
public interface ITokenRevocationFeedRefresher
{
    /// <summary>Reads the feed once and replaces the cache snapshot. Never throws for feed failures: returns
    /// <see cref="TokenRevocationRefresh.Failed"/> with the reason (fail-open — the last snapshot stays in force).</summary>
    Task<TokenRevocationRefresh> RefreshOnceAsync(CancellationToken ct);
}

/// <summary>Outcome of one refresh: applied counts, or why nothing was applied.</summary>
public sealed record TokenRevocationRefresh(bool Applied, int Jtis, int UserCutoffs, string? Error)
{
    public static TokenRevocationRefresh Ok(int jtis, int cutoffs) => new(true, jtis, cutoffs, null);
    public static TokenRevocationRefresh Failed(string error) => new(false, 0, 0, error);
}

/// <summary>
/// The feed client (D1, 2026-10-24): an <see cref="IHttpClientFactory"/> client named <see cref="HttpClientName"/>
/// instead of a <c>new HttpClient</c> inside the loop, options instead of raw <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>,
/// and the key sent per request so it never sits in default headers. The feed's shape is Identity's contract.
/// </summary>
public sealed class TokenRevocationFeedRefresher : ITokenRevocationFeedRefresher
{
    public const string HttpClientName = "blazecoin-token-revocation";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly RevokedTokenCache _cache;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenRevocationOptions _options;

    public TokenRevocationFeedRefresher(RevokedTokenCache cache, IHttpClientFactory httpFactory, IOptions<TokenRevocationOptions> options)
    {
        _cache = cache;
        _httpFactory = httpFactory;
        _options = options.Value;
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

    public async Task<TokenRevocationRefresh> RefreshOnceAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured) return TokenRevocationRefresh.Failed("feed not configured (IdentityBaseUrl/FeedKey)");
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.IdentityBaseUrl!.TrimEnd('/') + "/"), "api/auth/token-revocations"));
            request.Headers.TryAddWithoutValidation("X-Service-Key", _options.FeedKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return TokenRevocationRefresh.Failed($"feed answered {(int)response.StatusCode}");
            var feed = await response.Content.ReadFromJsonAsync<Feed>(Json, timeout.Token);
            if (feed is null) return TokenRevocationRefresh.Failed("feed body was empty");
            _cache.Replace(feed.Jtis, feed.UserCutoffs.Select(c => new KeyValuePair<string, DateTime>(c.UserId, c.CutoffUtc)));
            return TokenRevocationRefresh.Ok(feed.Jtis.Count, feed.UserCutoffs.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return TokenRevocationRefresh.Failed(ex.Message); }
    }
}

/// <summary>
/// Background refresh of <see cref="RevokedTokenCache"/> from the Identity feed: one
/// <see cref="ITokenRevocationFeedRefresher.RefreshOnceAsync"/> per <see cref="TokenRevocationOptions.PollInterval"/>,
/// paced by the injected <see cref="TimeProvider"/> so the loop is testable. Fail-open: a failed refresh keeps the last
/// snapshot and is logged (as a Warning, once per transition into failure).
/// </summary>
public sealed class TokenRevocationPoller : BackgroundService
{
    private readonly ITokenRevocationFeedRefresher _refresher;
    private readonly RevokedTokenCache _cache;
    private readonly TokenRevocationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TokenRevocationPoller> _logger;

    public TokenRevocationPoller(ITokenRevocationFeedRefresher refresher, RevokedTokenCache cache,
        IOptions<TokenRevocationOptions> options, TimeProvider clock, ILogger<TokenRevocationPoller> logger)
    {
        _refresher = refresher;
        _cache = cache;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "TokenRevocation feed not configured (IdentityBaseUrl/FeedKey) — revoked JWTs "
                + "will NOT be rejected by this service until it is");
            return;
        }

        var firstLoad = true; var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            TokenRevocationRefresh result;
            try { result = await _refresher.RefreshOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

            if (result.Applied)
            {
                if (firstLoad)
                {
                    _logger.LogInformation(
                        "Token-revocation feed loaded: {Jtis} revoked jti(s), {Cutoffs} user cutoff(s); polling every {Poll}s",
                        result.Jtis, result.UserCutoffs, (int)_options.PollInterval.TotalSeconds);
                    firstLoad = false;
                }
                else if (failing)
                {
                    _logger.LogInformation("Token-revocation feed back: {Jtis} revoked jti(s), {Cutoffs} user cutoff(s)", result.Jtis, result.UserCutoffs);
                }
                failing = false;
            }
            else if (!failing)
            {
                // Fail-open: keep enforcing the last snapshot; never crash the service. Logged once per outage.
                _logger.LogWarning("Token-revocation feed poll failed ({Error}) — keeping last snapshot{Note}",
                    result.Error, _cache.Loaded ? "" : " (no snapshot yet: revoked tokens are NOT being rejected)");
                failing = true;
            }

            try { await Task.Delay(_options.PollInterval, _clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

/// <summary>One registration for a consuming service (D1/D3, 2026-10-24) instead of the hand-wired cache + hosted
/// service in every Program.cs: the cache, the options bound to <c>TokenRevocation</c>, the named HttpClient,
/// the refresher, the poller and a system <see cref="TimeProvider"/> if none is registered.</summary>
public static class TokenRevocationServiceCollectionExtensions
{
    public static IServiceCollection AddBlazecoinTokenRevocation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TokenRevocationOptions>(configuration.GetSection(TokenRevocationOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RevokedTokenCache>();
        services.AddHttpClient(TokenRevocationFeedRefresher.HttpClientName);
        services.TryAddSingleton<ITokenRevocationFeedRefresher, TokenRevocationFeedRefresher>();
        services.AddHostedService<TokenRevocationPoller>();
        return services;
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
