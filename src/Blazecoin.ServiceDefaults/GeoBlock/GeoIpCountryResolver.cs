using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blazecoin.ServiceDefaults.GeoBlock;

/// <summary>
/// IP → ISO country code. Seam so the middleware is unit-testable without a database file.
/// </summary>
public interface IGeoIpCountryResolver
{
    /// <summary>False when no database is loaded — the middleware then fails OPEN (loudly).</summary>
    bool IsAvailable { get; }

    /// <summary>Two-letter ISO 3166-1 code, or null when the IP is unknown/unresolvable.</summary>
    string? ResolveIsoCode(IPAddress ip);
}

/// <summary>
/// Read-only facts about the loaded database for an operations readout (the website's SuperAdmin card,
/// 2026-09-04). A separate interface so the <see cref="IGeoIpCountryResolver"/> contract and every test stub
/// of it stay untouched.
/// </summary>
public interface IGeoBlockDatabaseInfo
{
    bool Enabled { get; }
    bool Available { get; }
    string? DatabasePath { get; }
    DateTime? BuildDateUtc { get; }
}

/// <summary>What a readout shows when the resolver is a test stub or otherwise not the MaxMind one.</summary>
public sealed class UnknownGeoBlockDatabaseInfo : IGeoBlockDatabaseInfo
{
    public bool Enabled => false;
    public bool Available => false;
    public string? DatabasePath => null;
    public DateTime? BuildDateUtc => null;
}

/// <summary>
/// GeoLite2-Country reader. The database is loaded INTO MEMORY at construction (register as a
/// singleton) and hot-reloaded when the file on disk changes: every <see cref="ReloadCheckInterval"/>
/// a lookup compares the file's last-write time with the loaded one and, if it moved, loads the
/// new build and swaps it in atomically — so the scheduled updater can replace the file and every
/// service picks the new borders up within minutes, no restart. (In-memory rather than memory-mapped
/// precisely so the file is never held open: on Windows a mapped .mmdb cannot be overwritten.)
///
/// A missing/unreadable file logs ONE loud warning and leaves the resolver unavailable rather
/// than failing startup — the block ships dark and must never take the service down; a file
/// that DISAPPEARS later logs an error and the resolver goes unavailable (failing open, loudly),
/// while a reload that fails keeps the previous build. A build older than <see cref="StaleAfter"/>
/// is warned about at every (re)load. Lookups are local (never a network call).
/// </summary>
public sealed class MaxMindGeoIpCountryResolver : IGeoIpCountryResolver, IGeoBlockDatabaseInfo, IDisposable
{
    private readonly bool _enabled;
    private DateTime? _buildDateUtc;

    public bool Enabled => _enabled;
    public bool Available => IsAvailable;
    public string? DatabasePath => _path;
    public DateTime? BuildDateUtc => _buildDateUtc;

    private static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(45);

    private readonly ILogger<MaxMindGeoIpCountryResolver> _logger;
    private readonly string? _path;
    private readonly object _reloadLock = new();
    private DatabaseReader? _reader;
    private DatabaseReader? _retired;      // previous build, disposed one reload later (in-flight lookups)
    private DateTime _loadedWriteTimeUtc;
    private DateTime _nextCheckUtc;

    public MaxMindGeoIpCountryResolver(IOptions<GeoBlockOptions> options, ILogger<MaxMindGeoIpCountryResolver> logger)
    {
        _logger = logger;
        var path = options.Value.DatabasePath;
        _enabled = options.Value.Enabled;
        if (!options.Value.Enabled)
            return; // dark: don't touch the filesystem at all

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning(
                "GeoBlock is ENABLED but no GeoLite2 database exists at '{Path}' — geo-blocking is INACTIVE (failing open). " +
                "Download GeoLite2-Country.mmdb from MaxMind and set GeoBlock:DatabasePath.", path);
            return;
        }

        _path = path;
        TryLoad(initial: true);
        _nextCheckUtc = DateTime.UtcNow + ReloadCheckInterval;
    }

    public bool IsAvailable => Volatile.Read(ref _reader) != null;

    public string? ResolveIsoCode(IPAddress ip)
    {
        MaybeReload();
        var reader = Volatile.Read(ref _reader);
        if (reader == null) return null;
        try
        {
            return reader.Country(ip).Country.IsoCode;
        }
        catch (AddressNotFoundException)
        {
            return null; // not in the database (private ranges, unallocated space)
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GeoBlock lookup failed — failing open for this request");
            return null;
        }
    }

    /// <summary>Cheap time check on the hot path; the file system is touched at most once per interval.</summary>
    private void MaybeReload()
    {
        if (_path == null) return;
        var now = DateTime.UtcNow;
        if (now < _nextCheckUtc) return;

        lock (_reloadLock)
        {
            if (now < _nextCheckUtc) return;
            _nextCheckUtc = now + ReloadCheckInterval;
            try
            {
                if (!File.Exists(_path))
                {
                    if (Interlocked.Exchange(ref _reader, null) != null)
                        _logger.LogError(
                            "GeoBlock database at '{Path}' has DISAPPEARED — geo-blocking is now INACTIVE (failing open). Restore the file; it reloads automatically.", _path);
                    return;
                }
                if (File.GetLastWriteTimeUtc(_path) != _loadedWriteTimeUtc)
                    TryLoad(initial: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GeoBlock reload check failed — keeping the current build");
            }
        }
    }

    private void TryLoad(bool initial)
    {
        try
        {
            var writeTime = File.GetLastWriteTimeUtc(_path!);
            var fresh = new DatabaseReader(_path!, FileAccessMode.Memory);
            var build = fresh.Metadata.BuildDate;
            var old = Interlocked.Exchange(ref _reader, fresh);
            _loadedWriteTimeUtc = writeTime;
            _buildDateUtc = DateTime.SpecifyKind(build, DateTimeKind.Utc);

            _retired?.Dispose();
            _retired = old;

            _logger.LogInformation("GeoBlock database {Action} from {Path} (build {Build:yyyy-MM-dd})",
                initial ? "loaded" : "RELOADED", _path, build);

            var age = DateTime.UtcNow - build;
            if (age > StaleAfter)
                _logger.LogWarning(
                    "GeoBlock database build {Build:yyyy-MM-dd} is {Days} days old — borders drift; run the GeoLite2 updater (or check its scheduled task)",
                    build, (int)age.TotalDays);
        }
        catch (Exception ex)
        {
            if (initial)
                _logger.LogError(ex, "GeoBlock database at {Path} could not be read — geo-blocking is INACTIVE (failing open)", _path);
            else
                _logger.LogError(ex, "GeoBlock database at {Path} changed but could not be reloaded — keeping the previous build", _path);
        }
    }

    public void Dispose()
    {
        _retired?.Dispose();
        Interlocked.Exchange(ref _reader, null)?.Dispose();
    }
}
