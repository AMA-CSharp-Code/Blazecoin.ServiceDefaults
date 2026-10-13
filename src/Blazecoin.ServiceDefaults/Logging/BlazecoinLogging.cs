using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

namespace Blazecoin.ServiceDefaults.Logging;

/// <summary>
/// Central structured logging for the Blazecoin stack (ISO 27002 8.15/8.16).
/// Console (all levels) + the shared <c>BlazecoinLogs.dbo.AppLogs</c> table (Warning+ or audit-tagged).
/// One copy for every service since 2026-10-13 (S11); before that each service carried its own, and they drifted.
/// </summary>
public static class BlazecoinLogging
{
    /// <summary>
    /// Wire Serilog for the host. <paramref name="customise"/> is the per-service hook: level overrides for a
    /// framework source that is noise in that one service, or an extra filter (the website drops the repeat
    /// warnings that follow a stale session; the gateway silences the https-port chatter behind Caddy).
    /// </summary>
    public static WebApplicationBuilder AddBlazecoinLogging(this WebApplicationBuilder builder, Action<LoggerConfiguration>? customise = null)
    {
        var serviceName = builder.Configuration["Serilog:ServiceName"]
                          ?? builder.Environment.ApplicationName;

        builder.Host.UseSerilog((context, cfg) => Configure(cfg, context.Configuration, serviceName, customise));

        return builder;
    }

    /// <summary>
    /// The logger configuration itself, separated so a test can build it against a dead logs database.
    /// <para><b>A dead logs database must never stop a service (2026-10-07).</b> With
    /// <c>AutoCreateSqlTable = true</c> the SQL sink opened a connection INSIDE its constructor to check the
    /// table, so an unreachable BlazecoinLogs made <c>CreateLogger</c> throw and the host never built: every
    /// one of the seven services had the logs database as a start-up dependency (failure audit, measured with
    /// Serilog.Sinks.MSSqlServer 10.0.0). The table is created by the database rebuild script, not by the sink.
    /// Batches that fail while the database is away are dropped and reported through SelfLog on stderr;
    /// logging resumes by itself when it is back.</para>
    /// <para><b>The sink's login needs SELECT as well as INSERT on the table (2026-10-13):</b> SqlBulkCopy runs a
    /// metadata query first. Writer-only logins fail every batch silently.</para>
    /// </summary>
    public static LoggerConfiguration Configure(LoggerConfiguration cfg, IConfiguration configuration, string serviceName,
        Action<LoggerConfiguration>? customise = null)
    {
        cfg.MinimumLevel.Information()
           .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
           .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
           .Enrich.FromLogContext()
           .Enrich.WithProperty("ServiceName", serviceName)
           .Enrich.WithMachineName()
           .Enrich.WithEnvironmentName()
           .WriteTo.Console();

        customise?.Invoke(cfg);

        // Central SQL sink — only wired when a LogsConnection is present, so a service with no logs DB
        // configured still runs (console only). Warning+ or audit-tagged events only.
        var logsConn = configuration.GetConnectionString("LogsConnection");
        if (string.IsNullOrWhiteSpace(logsConn))
            return cfg;

        EnableSelfLogOnce();
        try
        {
            cfg.WriteTo.Logger(sub => sub
                .Filter.ByIncludingOnly(e =>
                    e.Level >= LogEventLevel.Warning || e.Properties.ContainsKey("Audit"))
                .WriteTo.MSSqlServer(
                    connectionString: logsConn,
                    sinkOptions: new MSSqlServerSinkOptions
                    {
                        TableName = "AppLogs",
                        SchemaName = "dbo",
                        AutoCreateSqlTable = false,   // the sink must not touch the database at start-up (see above)
                        // Up to 1000 events every 2 s (~500/s): at the old 50 / 5 s cap the sink fell ~8 min
                        // behind live during the Pool API outage burst (2026-08-17).
                        BatchPostingLimit = 1000,
                        BatchPeriod = TimeSpan.FromSeconds(2)
                    },
                    columnOptions: BuildColumnOptions()));
        }
        catch (Exception ex)
        {
            // Belt and braces: whatever a future sink version does in its constructor, the service starts.
            Console.Error.WriteLine($"[{serviceName}] central SQL log sink not attached ({ex.GetType().Name}: {ex.Message}) - console logging only");
        }
        return cfg;
    }

    private static int _selfLogEnabled;

    /// <summary>Serilog's own diagnostics (a batch the SQL sink could not write, a column mismatch) go to stderr instead of vanishing.</summary>
    private static void EnableSelfLogOnce()
    {
        if (Interlocked.Exchange(ref _selfLogEnabled, 1) == 0)
            Serilog.Debugging.SelfLog.Enable(m => Console.Error.WriteLine("[serilog] " + m));
    }

    /// <summary>Security/audit event (Info level, tagged so it always reaches the central store).</summary>
    public static void LogAudit(this Microsoft.Extensions.Logging.ILogger logger, string message, params object?[] args)
    {
        using (logger.BeginScope(new Dictionary<string, object> { ["Audit"] = true }))
            logger.LogInformation(message, args);
    }

    /// <summary>The request/response header that carries the correlation id.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>
    /// Reads or mints an X-Correlation-Id, stamps it onto every log event for the request (via
    /// LogContext → CorrelationId column), echoes it on the response, and writes it back onto the
    /// request so the gateway forwards it to backends (one id spans the whole request across services).
    /// Register BEFORE UseSerilogRequestLogging so the request-completion log carries the id.
    /// <para>The response header is stamped at <c>OnStarting</c>, not eagerly: behind YARP the backend's echo of
    /// the same id is copied onto the response AFTER this middleware ran, and an eager set ended up as a
    /// duplicate header (the gateway's 2026-08-14 fix, now the one behaviour everywhere).</para>
    /// </summary>
    public static IApplicationBuilder UseBlazecoinCorrelationId(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var cid = ctx.Request.Headers[CorrelationHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(cid) || cid!.Length > 64)
                cid = Guid.NewGuid().ToString("N");

            ctx.Request.Headers[CorrelationHeader] = cid;   // forwarded to backends by the YARP gateway
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[CorrelationHeader] = cid;  // returned for client-side tracing
                return Task.CompletedTask;
            });

            using (Serilog.Context.LogContext.PushProperty("CorrelationId", cid))
                await next();
        });
    }

    private static ColumnOptions BuildColumnOptions()
    {
        var options = new ColumnOptions();
        options.Store.Remove(StandardColumn.Properties);
        options.Store.Add(StandardColumn.LogEvent);
        options.AdditionalColumns = new List<SqlColumn>
        {
            new SqlColumn { ColumnName = "ServiceName",     DataType = SqlDbType.NVarChar, DataLength = 64,  AllowNull = true },
            new SqlColumn { ColumnName = "CorrelationId",   DataType = SqlDbType.NVarChar, DataLength = 64,  AllowNull = true },
            new SqlColumn { ColumnName = "EnvironmentName", DataType = SqlDbType.NVarChar, DataLength = 32,  AllowNull = true },
            new SqlColumn { ColumnName = "SourceContext",   DataType = SqlDbType.NVarChar, DataLength = 256, AllowNull = true },
        };
        return options;
    }
}
