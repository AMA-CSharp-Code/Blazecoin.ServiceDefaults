using Blazecoin.ServiceDefaults.Security;

namespace Blazecoin.ServiceDefaults.Tests;

/// <summary>
/// The deny-list cache semantics (ASVS V3.3 / B2): a revoked jti is refused; a per-user cutoff refuses tokens
/// issued before it and tokens with no issued-at claim at all; a snapshot replace is atomic.
/// </summary>
public class TokenRevocationTests
{
    [Fact]
    public void Empty_cache_revokes_nothing_and_reports_not_loaded()
    {
        var cache = new RevokedTokenCache();
        Assert.False(cache.Loaded);
        Assert.False(cache.IsRevoked("jti-1", "user-1", DateTime.UtcNow));
        Assert.False(cache.IsRevoked(null, null, null));
    }

    [Fact]
    public void A_listed_jti_is_revoked_whatever_the_user_or_time()
    {
        var cache = new RevokedTokenCache();
        cache.Replace(["jti-gone"], []);
        Assert.True(cache.Loaded);
        Assert.True(cache.IsRevoked("jti-gone", null, null));
        Assert.True(cache.IsRevoked("jti-gone", "someone", DateTime.UtcNow.AddDays(1)));
        Assert.False(cache.IsRevoked("jti-other", "someone", DateTime.UtcNow));
    }

    [Fact]
    public void A_user_cutoff_refuses_earlier_tokens_and_tokens_without_an_issued_at()
    {
        var cutoff = new DateTime(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc);
        var cache = new RevokedTokenCache();
        cache.Replace([], [new KeyValuePair<string, DateTime>("user-1", cutoff)]);

        Assert.True(cache.IsRevoked("j", "user-1", cutoff.AddSeconds(-1)));
        Assert.True(cache.IsRevoked("j", "user-1", null));                 // no iat = issued at epoch
        Assert.False(cache.IsRevoked("j", "user-1", cutoff));               // issued AT the cutoff is fine
        Assert.False(cache.IsRevoked("j", "user-1", cutoff.AddMinutes(5)));
        Assert.False(cache.IsRevoked("j", "user-2", cutoff.AddDays(-30)));  // another user, no cutoff
    }

    [Fact]
    public void Replace_swaps_the_whole_snapshot()
    {
        var cache = new RevokedTokenCache();
        cache.Replace(["a"], []);
        cache.Replace(["b"], []);
        Assert.False(cache.IsRevoked("a", null, null));
        Assert.True(cache.IsRevoked("b", null, null));
    }

    [Fact]
    public void The_jwt_bearer_events_hook_is_wired_to_token_validation()
    {
        var events = TokenRevocationJwtEvents.Create();
        Assert.NotNull(events.OnTokenValidated);
    }
}
