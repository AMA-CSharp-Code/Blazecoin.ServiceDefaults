# Blazecoin.ServiceDefaults

Shared service plumbing for the Blazecoin website services. One copy of the code that every service used to carry
on its own, so a fix lands once and every service gets it at its next build.

| Namespace | What it is |
|---|---|
| `Blazecoin.ServiceDefaults.Logging` | `AddBlazecoinLogging()` wires Serilog to the console and, when a `LogsConnection` is configured, to the shared `BlazecoinLogs.dbo.AppLogs` table (Warning+ or audit-tagged events only). `LogAudit()` tags an Information event so the store keeps it. `UseBlazecoinCorrelationId()` mints or forwards `X-Correlation-Id` and echoes it on the response. |
| `Blazecoin.ServiceDefaults.Security` | `JwtKeyGuard.Require()` refuses to start with a missing or short HS256 key. `RevokedTokenCache` + `TokenRevocationPoller` + `TokenRevocationJwtEvents` enforce the Identity service's token deny-list in any JWT-validating service. |
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
builder.Services.AddSingleton<RevokedTokenCache>();
builder.Services.AddHostedService<TokenRevocationPoller>();
// options.Events = TokenRevocationJwtEvents.Create();  inside AddJwtBearer
```

The sink's SQL login needs SELECT as well as INSERT on `dbo.AppLogs`: the bulk copy it uses runs a metadata query first.

## Build and test

```
dotnet test
dotnet pack src/Blazecoin.ServiceDefaults -c Release -o out
```

Package versions are pinned to what the consuming services already carry, so adding this package changes no transitive graph.

## Licence

MIT.
