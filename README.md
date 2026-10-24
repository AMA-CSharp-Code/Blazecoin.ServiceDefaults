# Blazecoin.ServiceDefaults

Shared service plumbing for the Blazecoin website services. One copy of the code that every service used to carry
on its own, so a fix lands once and every service gets it at its next build.

| Namespace | What it is |
|---|---|
| `Blazecoin.ServiceDefaults.Logging` | `AddBlazecoinLogging()` wires Serilog to the console and, when a `LogsConnection` is configured, to the shared `BlazecoinLogs.dbo.AppLogs` table (Warning+ or audit-tagged events only). `LogAudit()` tags an Information event so the store keeps it. `UseBlazecoinCorrelationId()` mints or forwards `X-Correlation-Id` and echoes it on the response. |
| `Blazecoin.ServiceDefaults.Security` | `JwtKeyGuard.Require()` refuses to start with a missing or short HS256 key. `AddBlazecoinTokenRevocation()` (cache + options + feed refresher + poller) and `TokenRevocationJwtEvents` enforce the Identity service's token deny-list in any JWT-validating service. |
| `Blazecoin.ServiceDefaults.GeoBlock` | `GeoBlockMiddleware` answers 451 to blocked jurisdictions with an operator bypass cookie; `MaxMindGeoIpCountryResolver` and `MaxMindGeoIpAsnResolver` read GeoLite2 databases into memory and hot-reload them when the file changes. |

## Use

```csharp
using Blazecoin.ServiceDefaults.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.AddBlazecoinLogging();                       // or AddBlazecoinLogging(cfg => cfg.MinimumLevel.Override(...))

var app = builder.Build();
app.UseBlazecoinCorrelationId();                     // before UseSerilogRequestLogging
app.UseSerilogRequestLogging();
```

```csharp
using Blazecoin.ServiceDefaults.GeoBlock;

builder.Services.Configure<GeoBlockOptions>(builder.Configuration.GetSection("GeoBlock"));
builder.Services.AddSingleton<IGeoIpCountryResolver, MaxMindGeoIpCountryResolver>();
app.UseGeoBlock();                                   // after UseForwardedHeaders
```

```csharp
using Blazecoin.ServiceDefaults.Security;

var key = JwtKeyGuard.Require(builder.Configuration["Jwt:Key"]);
builder.Services.AddBlazecoinTokenRevocation(builder.Configuration);   // cache + options (TokenRevocation section) + named HttpClient + poller
// options.Events = TokenRevocationJwtEvents.Create();  inside AddJwtBearer
```

Since 1.0.2 the poller is `ITokenRevocationFeedRefresher` (one fetch-and-apply, testable with a stub handler) paced by
the registered `TimeProvider`; `TokenRevocationOptions` replaces the raw configuration reads (`IdentityBaseUrl`,
`FeedKey`, `PollSeconds` ≥ 5, `TimeoutSeconds`). The hand-wired pair (`AddSingleton<RevokedTokenCache>` +
`AddHostedService<TokenRevocationPoller>`) no longer resolves on its own — use the one registration above.

The sink's SQL login needs SELECT as well as INSERT on `dbo.AppLogs`: the bulk copy it uses runs a metadata query first.

## Build and test

```
dotnet test
dotnet pack src/Blazecoin.ServiceDefaults -c Release -o out
```

Package versions are pinned to what the consuming services already carry, so adding this package changes no transitive graph.

## Licence

MIT.

## Releasing

1. Bump `<Version>` in `src/Blazecoin.ServiceDefaults/Blazecoin.ServiceDefaults.csproj`, commit, push `main`.
2. Tag it and push the tag: `git tag v1.0.1 && git push origin v1.0.1`.
3. `.github/workflows/release.yml` runs the tests, packs, signs in to NuGet.org through **Trusted Publishing** and
   pushes the package. No API key exists anywhere: NuGet.org trusts this workflow file in this repository (policy on
   the `AMA_CODE` account's *API keys* page, *Trusted Publishing* section: owner `AMA-CSharp-Code`, repository
   `Blazecoin.ServiceDefaults`, workflow `release.yml`, no environment). The job refuses a tag that does not match
   `<Version>`; *Run workflow* by hand publishes the csproj version; a re-run is harmless (`--skip-duplicate`).
4. A new version takes a few minutes to index (poll
   `https://api.nuget.org/v3-flatcontainer/blazecoin.servicedefaults/index.json`), then bump the
   `Blazecoin.ServiceDefaults` reference in each consuming service.

`Publish-ServiceDefaults.ps1 -NoPush` still packs into `out\` for the local `blz-local` feed, so a consuming service
can be built and tested against an unpublished version before the tag goes up. Its push path (an API key read from a
file) is the fallback only; 1.0.0 went out that way on 2026-10-14 and the key was deleted the same morning.
