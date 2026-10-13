namespace Blazecoin.ServiceDefaults.GeoBlock;

/// <summary>
/// The UK+OFAC geo-block (decided 2026-06-23/07-13, built 2026-08-14). Ships DARK:
/// <see cref="Enabled"/> defaults to false and the middleware is a no-op until it is set
/// AND a GeoLite2-Country database file exists at <see cref="DatabasePath"/>. Every layer that
/// geo-blocks (gateway, website, Identity) resolves independently, so a bypassed layer changes nothing.
/// </summary>
public sealed class GeoBlockOptions
{
    public bool Enabled { get; set; }

    /// <summary>Absolute path to GeoLite2-Country.mmdb (MaxMind free tier; refresh monthly).</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to GeoLite2-ASN.mmdb (2026-10-13; same MaxMind licence, kept fresh by the same updater).
    /// Independent of <see cref="Enabled"/>: it blocks nothing. The gateway stamps the caller's network number
    /// as X-Client-Asn from it, which is how share-to-earn counting tells a platform's own link checkers
    /// (Meta AS32934 and friends) from a person. Unset = GeoLite2-ASN.mmdb beside <see cref="DatabasePath"/>;
    /// a missing file = no header.
    /// </summary>
    public string? AsnDatabasePath { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 codes. Default = UK + the OFAC comprehensive-sanctions set
    /// (North Korea, Iran, Cuba, Syria, Russia) + Belarus (2026-09-01: near-comprehensive UK/EU/US regime
    /// mirroring Russia's). Occupied-Ukraine regions are country-level invisible — most Crimea ranges
    /// geo-resolve as RU; subdivision blocking is a City-database follow-up deliberately deferred.
    /// </summary>
    public string[] BlockedCountries { get; set; } = ["GB", "KP", "IR", "CU", "SY", "RU", "BY"];

    /// <summary>
    /// Operator bypass (2026-10-07). Visiting <see cref="BypassPath"/>?token=VALUE sets a cookie that lets
    /// THAT browser through the block from any address; null/empty = no bypass exists. The SAME value must
    /// be configured on every layer that geo-blocks: each resolves on its own and the gateway forwards the
    /// cookie. It is an operator key, not a public exemption: nothing changes for anyone without the token,
    /// and every issue is logged (country only, never the address).
    /// </summary>
    public string? BypassToken { get; set; }

    public string BypassCookieName { get; set; } = "blz_geo_bypass";

    public string BypassPath { get; set; } = "/geo-bypass";

    /// <summary>Cookie lifetime in hours. 30 days: a phone should not need the unlock link every week.</summary>
    public int BypassCookieHours { get; set; } = 720;
}
