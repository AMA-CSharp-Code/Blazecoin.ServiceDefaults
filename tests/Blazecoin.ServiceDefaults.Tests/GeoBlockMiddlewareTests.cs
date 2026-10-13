using System.Net;
using Blazecoin.ServiceDefaults.GeoBlock;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Blazecoin.ServiceDefaults.Tests;

/// <summary>
/// The UK+OFAC geo-block middleware over a stub resolver — no GeoLite2 file needed, so the suite is hermetic.
/// The contract: dark by default, fail-open without a database, loopback/private always pass, blocked ISO
/// codes get 451 (problem+json on /api, HTML elsewhere), and the operator bypass link/cookie (2026-10-07).
/// Carried over from the Identity service's suite when the copies were unified (2026-10-13).
/// </summary>
public class GeoBlockMiddlewareTests
{
    private sealed class StubResolver : IGeoIpCountryResolver
    {
        public bool IsAvailable { get; init; } = true;
        public Dictionary<string, string> Map { get; init; } = new();
        public string? ResolveIsoCode(IPAddress ip) => Map.GetValueOrDefault(ip.ToString());
    }

    private static GeoBlockMiddleware Build(StubResolver resolver, bool enabled, out List<int> reached,
        string[]? blocked = null, string? bypassToken = null)
    {
        var hits = new List<int>();
        reached = hits;
        return new GeoBlockMiddleware(
            _ => { hits.Add(1); return Task.CompletedTask; },
            resolver,
            MsOptions.Create(new GeoBlockOptions
            {
                Enabled = enabled,
                BlockedCountries = blocked ?? ["GB", "KP", "IR", "CU", "SY", "RU", "BY"],
                BypassToken = bypassToken
            }),
            NullLogger<GeoBlockMiddleware>.Instance);
    }

    private static DefaultHttpContext Context(string ip, string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static StubResolver Uk() => new() { Map = { ["81.2.69.142"] = "GB" } };

    [Fact]
    public async Task Disabled_passes_everything_and_never_resolves()
    {
        var mw = Build(Uk(), enabled: false, out var reached);
        var ctx = Context("81.2.69.142");
        await mw.InvokeAsync(ctx);
        Assert.Single(reached);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Missing_database_fails_open()
    {
        var mw = Build(new StubResolver { IsAvailable = false }, enabled: true, out var reached);
        await mw.InvokeAsync(Context("81.2.69.142"));
        Assert.Single(reached);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.9")]
    [InlineData("192.168.1.50")]
    [InlineData("169.254.10.10")]
    [InlineData("::1")]
    public async Task Loopback_and_private_addresses_always_pass(string ip)
    {
        var mw = Build(new StubResolver { Map = { [ip] = "GB" } }, enabled: true, out var reached);
        await mw.InvokeAsync(Context(ip));
        Assert.Single(reached);
    }

    [Fact]
    public async Task Unknown_and_non_blocked_countries_pass()
    {
        var mw = Build(new StubResolver(), enabled: true, out var reached);
        await mw.InvokeAsync(Context("203.0.113.7"));
        Assert.Single(reached);

        var fr = Build(new StubResolver { Map = { ["203.0.113.7"] = "FR" } }, enabled: true, out var reachedFr);
        await fr.InvokeAsync(Context("203.0.113.7"));
        Assert.Single(reachedFr);
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("KP")]
    [InlineData("IR")]
    [InlineData("CU")]
    [InlineData("SY")]
    [InlineData("RU")]
    [InlineData("BY")]
    [InlineData("gb")]   // comparison is case-insensitive
    public async Task Every_default_blocked_country_gets_451(string iso)
    {
        var mw = Build(new StubResolver { Map = { ["203.0.113.7"] = iso } }, enabled: true, out var reached);
        var ctx = Context("203.0.113.7");
        await mw.InvokeAsync(ctx);
        Assert.Empty(reached);
        Assert.Equal(451, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Api_paths_get_problem_json_pages_get_html()
    {
        var stub = new StubResolver { Map = { ["203.0.113.7"] = "GB" } };

        var api = Context("203.0.113.7", "/api/auth/login");
        await Build(stub, enabled: true, out _).InvokeAsync(api);
        Assert.Equal(451, api.Response.StatusCode);
        Assert.StartsWith("application/problem+json", api.Response.ContentType);

        var page = Context("203.0.113.7", "/some/page");
        await Build(stub, enabled: true, out _).InvokeAsync(page);
        Assert.Equal(451, page.Response.StatusCode);
        Assert.StartsWith("text/html", page.Response.ContentType);
        page.Response.Body.Position = 0;
        var body = await new StreamReader(page.Response.Body).ReadToEndAsync();
        Assert.Contains("NOT AVAILABLE IN YOUR REGION", body);
    }

    [Fact]
    public async Task The_unlock_link_with_the_right_token_issues_the_cookie_and_redirects_home()
    {
        var mw = Build(Uk(), enabled: true, out var reached, bypassToken: "s3cret-operator-token");
        var ctx = Context("81.2.69.142", "/geo-bypass");
        ctx.Request.QueryString = new QueryString("?token=s3cret-operator-token");

        await mw.InvokeAsync(ctx);

        Assert.Empty(reached);
        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Equal("/", ctx.Response.Headers.Location.ToString());
        var cookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains("blz_geo_bypass=s3cret-operator-token", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_unlock_link_with_a_wrong_token_is_just_a_blocked_request()
    {
        var mw = Build(Uk(), enabled: true, out var reached, bypassToken: "s3cret-operator-token");
        var ctx = Context("81.2.69.142", "/geo-bypass");
        ctx.Request.QueryString = new QueryString("?token=guess");

        await mw.InvokeAsync(ctx);

        Assert.Empty(reached);
        Assert.Equal(451, ctx.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(ctx.Response.Headers.SetCookie.ToString()));
    }

    [Fact]
    public async Task A_blocked_country_with_the_bypass_cookie_passes_and_with_a_stale_one_does_not()
    {
        var ok = Build(Uk(), enabled: true, out var reached, bypassToken: "s3cret-operator-token");
        var ctx = Context("81.2.69.142", "/Account/Login");
        ctx.Request.Headers.Cookie = "blz_geo_bypass=s3cret-operator-token";
        await ok.InvokeAsync(ctx);
        Assert.Single(reached);
        Assert.Equal(200, ctx.Response.StatusCode);

        var stale = Build(Uk(), enabled: true, out var reachedStale, bypassToken: "s3cret-operator-token");
        var ctx2 = Context("81.2.69.142", "/");
        ctx2.Request.Headers.Cookie = "blz_geo_bypass=old-token";
        await stale.InvokeAsync(ctx2);
        Assert.Empty(reachedStale);
        Assert.Equal(451, ctx2.Response.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_token_the_unlock_link_is_an_ordinary_request()
    {
        var mw = Build(Uk(), enabled: true, out var reached, bypassToken: null);
        var ctx = Context("81.2.69.142", "/geo-bypass");
        ctx.Request.QueryString = new QueryString("?token=anything");

        await mw.InvokeAsync(ctx);

        Assert.Empty(reached);
        Assert.Equal(451, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task The_cookie_does_not_matter_for_a_country_that_is_not_blocked_anyway()
    {
        var mw = Build(new StubResolver { Map = { ["8.8.8.8"] = "US" } }, enabled: true, out var reached, bypassToken: "s3cret-operator-token");
        await mw.InvokeAsync(Context("8.8.8.8"));
        Assert.Single(reached);
    }

    [Fact]
    public void A_resolver_that_is_not_the_maxmind_one_reads_as_unknown_to_a_readout()
    {
        var info = new UnknownGeoBlockDatabaseInfo();
        Assert.False(info.Enabled);
        Assert.False(info.Available);
        Assert.Null(info.DatabasePath);
        Assert.Null(info.BuildDateUtc);
    }
}
