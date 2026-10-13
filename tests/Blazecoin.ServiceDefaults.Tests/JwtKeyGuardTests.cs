using System.Text;
using Blazecoin.ServiceDefaults.Security;

namespace Blazecoin.ServiceDefaults.Tests;

/// <summary>
/// ASVS A2 fail-fast for the JWT signing key: null/empty/short aborts startup, 32 bytes or more passes through.
/// </summary>
public class JwtKeyGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    [InlineData("0123456789012345678901234567890")]    // 31 bytes — one short of the floor
    public void Require_throws_on_missing_or_short_key(string? key)
        => Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(key));

    [Fact]
    public void Require_rejects_exactly_31_bytes_and_accepts_32()
    {
        Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(new string('a', JwtKeyGuard.MinBytes - 1)));

        var ok = new string('a', JwtKeyGuard.MinBytes);
        Assert.Equal(ok, JwtKeyGuard.Require(ok));
        Assert.Equal(JwtKeyGuard.MinBytes, Encoding.UTF8.GetByteCount(ok));
    }

    [Fact]
    public void Byte_count_not_character_count_decides()
    {
        // 16 two-byte characters = 32 bytes: passes; 16 one-byte characters: does not.
        Assert.Equal(32, Encoding.UTF8.GetByteCount(new string('é', 16)));
        JwtKeyGuard.Require(new string('é', 16));
        Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(new string('a', 16)));
    }
}
