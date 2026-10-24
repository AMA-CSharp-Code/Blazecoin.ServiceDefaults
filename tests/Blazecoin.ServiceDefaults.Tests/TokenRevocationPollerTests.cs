using System.Net;
using System.Text;
using Blazecoin.ServiceDefaults.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Blazecoin.ServiceDefaults.Tests;

/// <summary>
/// D1 (2026-10-24): the revocation feed poller used to read raw IConfiguration, build a new HttpClient inside
/// ExecuteAsync and pace itself with Task.Delay — untestable, so untested. Now one refresh step behind a stub
/// handler, options, and a TimeProvider-paced loop. Fail-open semantics pinned: a bad poll keeps the last snapshot.
/// </summary>
public class TokenRevocationPollerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests.Add(request); return Task.FromResult(answer(request)); }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TokenRevocationOptions Configured(int pollSeconds = 30)
        => new() { IdentityBaseUrl = "https://identity.test/", FeedKey = "k", PollSeconds = pollSeconds };

    private static TokenRevocationFeedRefresher Refresher(RevokedTokenCache cache, HttpMessageHandler handler, TokenRevocationOptions? o = null)
        => new(cache, new StubFactory(handler), Options.Create(o ?? Configured()));

    [Fact]
    public async Task A_good_feed_replaces_the_snapshot_and_sends_the_service_key()
    {
        var cache = new RevokedTokenCache();
        var handler = new StubHandler(_ => Json("""{"jtis":["j1","j2"],"userCutoffs":[{"userId":"u1","cutoffUtc":"2026-10-13T20:00:00Z"}]}"""));
        var r = await Refresher(cache, handler).RefreshOnceAsync(default);

        Assert.True(r.Applied); Assert.Equal(2, r.Jtis); Assert.Equal(1, r.UserCutoffs); Assert.Null(r.Error);
        Assert.True(cache.Loaded);
        Assert.True(cache.IsRevoked("j1", null, null));
        Assert.True(cache.IsRevoked("x", "u1", new DateTime(2026, 10, 13, 19, 0, 0, DateTimeKind.Utc)));
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://identity.test/api/auth/token-revocations", req.RequestUri!.ToString());
        Assert.Equal("k", Assert.Single(req.Headers.GetValues("X-Service-Key")));
    }

    [Fact]
    public async Task A_base_url_without_a_trailing_slash_still_reaches_the_feed()
    {
        var handler = new StubHandler(_ => Json("""{"jtis":[],"userCutoffs":[]}"""));
        var o = Configured(); o.IdentityBaseUrl = "https://identity.test";
        Assert.True((await Refresher(new RevokedTokenCache(), handler, o).RefreshOnceAsync(default)).Applied);
        Assert.Equal("https://identity.test/api/auth/token-revocations", handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_non_success_answer_keeps_the_last_snapshot(HttpStatusCode code)
    {
        var cache = new RevokedTokenCache(); cache.Replace(["old"], []);
        var r = await Refresher(cache, new StubHandler(_ => Json("{}", code))).RefreshOnceAsync(default);
        Assert.False(r.Applied); Assert.Contains(((int)code).ToString(), r.Error);
        Assert.True(cache.IsRevoked("old", null, null));                    // fail-open: the old snapshot stands
    }

    [Fact]
    public async Task Malformed_json_and_transport_errors_are_reported_not_thrown()
    {
        var cache = new RevokedTokenCache(); cache.Replace(["old"], []);
        var bad = await Refresher(cache, new StubHandler(_ => Json("not json"))).RefreshOnceAsync(default);
        Assert.False(bad.Applied); Assert.NotNull(bad.Error);
        var down = await Refresher(cache, new StubHandler(_ => throw new HttpRequestException("connection refused"))).RefreshOnceAsync(default);
        Assert.False(down.Applied); Assert.Contains("connection refused", down.Error);
        Assert.True(cache.IsRevoked("old", null, null));
    }

    [Fact]
    public async Task An_unconfigured_feed_is_reported_and_nothing_is_fetched()
    {
        var handler = new StubHandler(_ => Json("{}"));
        var r = await Refresher(new RevokedTokenCache(), handler, new TokenRevocationOptions()).RefreshOnceAsync(default);
        Assert.False(r.Applied); Assert.Contains("not configured", r.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Poll_interval_is_at_least_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new TokenRevocationOptions { PollSeconds = 1 }.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), new TokenRevocationOptions().PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(120), new TokenRevocationOptions { PollSeconds = 120 }.PollInterval);
    }

    // ── the loop ──────────────────────────────────────────────────────────────────────────────────

    private sealed class ScriptedRefresher : ITokenRevocationFeedRefresher
    {
        public int Calls;
        public Func<int, TokenRevocationRefresh> Script { get; set; } = _ => TokenRevocationRefresh.Ok(1, 0);
        public Task<TokenRevocationRefresh> RefreshOnceAsync(CancellationToken ct) => Task.FromResult(Script(Interlocked.Increment(ref Calls)));
    }

    private sealed class ListLogger : ILogger<TokenRevocationPoller>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Entries) Entries.Add((logLevel, formatter(state, exception))); }
    }

    private static async Task Settle() { for (var i = 0; i < 20; i++) await Task.Yield(); await Task.Delay(20); }

    [Fact]
    public async Task The_loop_refreshes_at_once_and_then_once_per_interval_on_the_injected_clock()
    {
        var clock = new FakeTimeProvider();
        var refresher = new ScriptedRefresher();
        var log = new ListLogger();
        var poller = new TokenRevocationPoller(refresher, new RevokedTokenCache(), Options.Create(Configured(30)), clock, log);

        await poller.StartAsync(default);
        await Settle();
        Assert.Equal(1, refresher.Calls);                                   // immediately
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("feed loaded"));

        clock.Advance(TimeSpan.FromSeconds(29)); await Settle();
        Assert.Equal(1, refresher.Calls);                                   // not yet
        clock.Advance(TimeSpan.FromSeconds(1)); await Settle();
        Assert.Equal(2, refresher.Calls);                                   // on the interval
        clock.Advance(TimeSpan.FromSeconds(30)); await Settle();
        Assert.Equal(3, refresher.Calls);

        await poller.StopAsync(default);
    }

    [Fact]
    public async Task A_failing_feed_is_warned_once_per_outage_and_recovery_is_logged()
    {
        var clock = new FakeTimeProvider();
        var refresher = new ScriptedRefresher { Script = n => n is 2 or 3 ? TokenRevocationRefresh.Failed("boom") : TokenRevocationRefresh.Ok(1, 0) };
        var log = new ListLogger();
        var poller = new TokenRevocationPoller(refresher, new RevokedTokenCache(), Options.Create(Configured(5)), clock, log);

        await poller.StartAsync(default); await Settle();
        for (var i = 0; i < 3; i++) { clock.Advance(TimeSpan.FromSeconds(5)); await Settle(); }
        await poller.StopAsync(default);

        Assert.Equal(4, refresher.Calls);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);                               // two failures, one warning
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("boom"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("feed back"));
    }

    [Fact]
    public async Task An_unconfigured_poller_warns_and_stops_without_polling()
    {
        var refresher = new ScriptedRefresher();
        var log = new ListLogger();
        var poller = new TokenRevocationPoller(refresher, new RevokedTokenCache(), Options.Create(new TokenRevocationOptions()), new FakeTimeProvider(), log);
        await poller.StartAsync(default); await Settle(); await poller.StopAsync(default);
        Assert.Equal(0, refresher.Calls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not configured"));
    }

    [Fact]
    public void One_registration_wires_the_whole_graph()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TokenRevocation:IdentityBaseUrl"] = "https://identity.test",
            ["TokenRevocation:FeedKey"] = "k",
            ["TokenRevocation:PollSeconds"] = "45",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlazecoinTokenRevocation(config);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<TokenRevocationOptions>>().Value;
        Assert.True(options.IsConfigured); Assert.Equal(TimeSpan.FromSeconds(45), options.PollInterval);
        Assert.Same(sp.GetRequiredService<RevokedTokenCache>(), sp.GetRequiredService<RevokedTokenCache>());
        Assert.IsType<TokenRevocationFeedRefresher>(sp.GetRequiredService<ITokenRevocationFeedRefresher>());
        Assert.Contains(sp.GetServices<IHostedService>(), h => h is TokenRevocationPoller);
        Assert.NotNull(sp.GetRequiredService<TimeProvider>());
        Assert.NotNull(sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenRevocationFeedRefresher.HttpClientName));
    }
}
