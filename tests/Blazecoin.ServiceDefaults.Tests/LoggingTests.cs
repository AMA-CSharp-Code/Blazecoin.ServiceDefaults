using Blazecoin.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Blazecoin.ServiceDefaults.Tests;

/// <summary>
/// The logging shell: the configuration builds without a logs database (console only) and with an unreachable
/// one (the sink must never be a start-up dependency); the per-service customise hook is applied; audit-tagged
/// events carry the Audit property; the correlation middleware mints, forwards and echoes the id, stamping the
/// response at OnStarting (the gateway's duplicate-header fix, now the one behaviour).
/// </summary>
public class LoggingTests
{
    private static IConfiguration Config(string? logsConnection) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:LogsConnection"] = logsConnection })
        .Build();

    [Fact]
    public void Builds_without_a_logs_connection()
    {
        using var logger = BlazecoinLogging.Configure(new LoggerConfiguration(), Config(null), "TestService").CreateLogger();
        logger.Information("hello");
    }

    [Fact]
    public void Builds_against_an_unreachable_logs_database_and_does_not_block()
    {
        // 10.255.255.1 is unroutable; with AutoCreateSqlTable off the sink must not connect at construction.
        var conn = "Server=10.255.255.1,1;Database=BlazecoinLogs;User ID=x;Password=y;Connect Timeout=1;Encrypt=False";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var logger = BlazecoinLogging.Configure(new LoggerConfiguration(), Config(conn), "TestService").CreateLogger();
        logger.Warning("a warning that will never land");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_customise_hook_is_applied()
    {
        var called = false;
        using var logger = BlazecoinLogging.Configure(new LoggerConfiguration(), Config(null), "TestService",
            cfg => { called = true; cfg.MinimumLevel.Override("Noisy", LogEventLevel.Error); }).CreateLogger();
        Assert.True(called);
    }

    [Fact]
    public void LogAudit_tags_the_event_so_the_sink_filter_keeps_it()
    {
        var events = new List<LogEvent>();
        using var serilog = new LoggerConfiguration().WriteTo.Sink(new ListSink(events)).CreateLogger();
        using var factory = new LoggerFactory().AddSerilog(serilog);
        var logger = factory.CreateLogger("t");

        logger.LogAudit("Account {UserId} deleted", "u1");
        logger.LogInformation("ordinary");

        var audit = Assert.Single(events, e => e.MessageTemplate.Text.StartsWith("Account"));
        Assert.True(audit.Properties.ContainsKey("Audit"));
        Assert.Equal(LogEventLevel.Information, audit.Level);
        var plain = Assert.Single(events, e => e.MessageTemplate.Text == "ordinary");
        Assert.False(plain.Properties.ContainsKey("Audit"));
    }

    [Fact]
    public async Task Correlation_id_is_minted_forwarded_and_echoed_at_response_start()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        string? seenOnRequest = null;
        app.UseBlazecoinCorrelationId();
        app.Run(ctx =>
        {
            seenOnRequest = ctx.Request.Headers[BlazecoinLogging.CorrelationHeader];
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var ctx = new DefaultHttpContext();
        var starting = new CapturingResponseFeature();
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(starting);
        await pipeline(ctx);
        await starting.FireOnStartingAsync();   // what the server does before the first byte

        Assert.False(string.IsNullOrEmpty(seenOnRequest));
        Assert.Equal(32, seenOnRequest!.Length);
        Assert.Equal(seenOnRequest, ctx.Response.Headers[BlazecoinLogging.CorrelationHeader].ToString());
    }

    [Fact]
    public async Task A_caller_supplied_id_is_kept_and_an_oversized_one_is_replaced()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseBlazecoinCorrelationId();
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();

        var ok = new DefaultHttpContext();
        ok.Request.Headers[BlazecoinLogging.CorrelationHeader] = "edge-abc-123";
        await pipeline(ok);
        Assert.Equal("edge-abc-123", ok.Request.Headers[BlazecoinLogging.CorrelationHeader].ToString());

        var big = new DefaultHttpContext();
        big.Request.Headers[BlazecoinLogging.CorrelationHeader] = new string('x', 65);
        await pipeline(big);
        Assert.Equal(32, big.Request.Headers[BlazecoinLogging.CorrelationHeader].ToString().Length);
    }

    private sealed class ListSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    /// <summary>The default test response feature ignores OnStarting; this one keeps the callbacks so the test can run them.</summary>
    private sealed class CapturingResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = new();
        public override void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));
        public async Task FireOnStartingAsync()
        {
            foreach (var (callback, state) in _onStarting) await callback(state);
        }
    }
}
