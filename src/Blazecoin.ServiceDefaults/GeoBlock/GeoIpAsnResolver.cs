using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blazecoin.ServiceDefaults.GeoBlock;

/// <summary>
/// IP → autonomous system number. Seam so a forwarding transform is testable without a database file.
/// </summary>
public interface IGeoIpAsnResolver
{
    /// <summary>False when no database is loaded — the gateway then stamps no X-Client-Asn at all.</summary>
    bool IsAvailable { get; }

    /// <summary>The network's AS number, or null when the address is unknown to the database.</summary>
    long? ResolveAsn(IPAddress ip);
}

/// <summary>
/// GeoLite2-ASN reader (2026-10-13), the country resolver's smaller sibling: loaded into memory (never held
/// open, so the updater can replace the file), reloaded when the file's write time moves, failing silent-but-
/// logged. It blocks nothing — its only consumer is the X-Client-Asn header that share-to-earn counting reads —
/// so a missing file is an Information line, not a warning: the feature is optional on every layer.
/// </summary>
public sealed class MaxMindGeoIpAsnResolver : IGeoIpAsnResolver, IDisposable
{
    private static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<MaxMindGeoIpAsnResolver> _logger;
    private readonly string? _path;
    private readonly object _reloadLock = new();
    private DatabaseReader? _reader;
    private DatabaseReader? _retired;
    private DateTime _loadedWriteTimeUtc;
    private DateTime _nextCheckUtc;

    public MaxMindGeoIpAsnResolver(IOptions<GeoBlockOptions> options, ILogger<MaxMindGeoIpAsnResolver> logger)
    {
        _logger = logger;
        var path = options.Value.AsnDatabasePath;
        // Unset = beside the Country database, so a production gateway needs no config change: drop
        // GeoLite2-ASN.mmdb next to GeoLite2-Country.mmdb and it is picked up within the reload interval.
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(options.Value.DatabasePath))
            path = Path.Combine(Path.GetDirectoryName(options.Value.DatabasePath) ?? "", "GeoLite2-ASN.mmdb");
        if (string.IsNullOrWhiteSpace(path))
            return; // nothing configured at all: no header, nothing to watch

        if (!File.Exists(path))
        {
            _logger.LogInformation("GeoLite2-ASN database not found at '{Path}' — X-Client-Asn will not be stamped until it appears", path);
        }

        _path = path;
        if (File.Exists(path)) TryLoad(initial: true);
        _nextCheckUtc = DateTime.UtcNow + ReloadCheckInterval;
    }

    public bool IsAvailable => Volatile.Read(ref _reader) != null;

    public long? ResolveAsn(IPAddress ip)
    {
        MaybeReload();
        var reader = Volatile.Read(ref _reader);
        if (reader == null) return null;
        try
        {
            return reader.Asn(ip).AutonomousSystemNumber;
        }
        catch (AddressNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GeoLite2-ASN lookup failed — no X-Client-Asn for this request");
            return null;
        }
    }

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
                        _logger.LogWarning("GeoLite2-ASN database at '{Path}' has disappeared — X-Client-Asn is no longer stamped", _path);
                    return;
                }
                if (File.GetLastWriteTimeUtc(_path) != _loadedWriteTimeUtc)
                    TryLoad(initial: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GeoLite2-ASN reload check failed — keeping the current build");
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

            _retired?.Dispose();
            _retired = old;

            _logger.LogInformation("GeoLite2-ASN database {Action} from {Path} (build {Build:yyyy-MM-dd})",
                initial ? "loaded" : "RELOADED", _path, build);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GeoLite2-ASN database at {Path} could not be {What} — X-Client-Asn {Effect}",
                _path, initial ? "read" : "reloaded", initial ? "is not stamped" : "keeps the previous build");
        }
    }

    public void Dispose()
    {
        _retired?.Dispose();
        Interlocked.Exchange(ref _reader, null)?.Dispose();
    }
}
