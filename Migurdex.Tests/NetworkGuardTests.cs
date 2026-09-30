using Migurdex.Shared;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class NetworkGuardTests
{
    // C1: API'de IsBlockedAddress vardı, CLI'nin indirici yolunda hiç yoktu.
    // Koruma Migurdex.Shared'e taşındı; iki taraf da aynı uygulamayı çağırıyor.
    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("127.10.20.30")]     // 127/8 tamamı loopback
    [InlineData("0.0.0.0")]          // "herhangi bir adres"
    [InlineData("0.1.2.3")]          // 0.0.0.0/8
    [InlineData("10.0.0.5")]         // 10/8
    [InlineData("172.16.0.1")]       // 172.16/12 alt
    [InlineData("172.31.255.254")]   // 172.16/12 ust
    [InlineData("192.168.1.1")]     // 192.168/16
    [InlineData("169.254.169.254")]  // cloud metadata (AWS/GCP/Azure)
    [InlineData("100.64.0.1")]       // 100.64/10 CGNAT
    [InlineData("100.127.255.255")]  // 100.64/10 ust
    public void IsBlockedAddress_RejectsPrivateAndLinkLocalV4(string address)
    {
        Assert.True(NetworkGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]       // 172.16/12 disi
    [InlineData("172.32.0.1")]       // 172.16/12 disi
    [InlineData("100.63.255.255")]   // 100.64/10 oncesi
    [InlineData("100.128.0.1")]      // 100.64/10 sonrasi
    [InlineData("192.169.1.1")]      // 192.168/16 disi
    [InlineData("11.0.0.1")]
    public void IsBlockedAddress_AllowsPublicV4(string address)
    {
        Assert.False(NetworkGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsBlockedAddress_RejectsIpv6LoopbackAndLinkLocal()
    {
        Assert.True(NetworkGuard.IsBlockedAddress(IPAddress.Parse("::1")));
        Assert.True(NetworkGuard.IsBlockedAddress(IPAddress.Parse("::")));
        Assert.True(NetworkGuard.IsBlockedAddress(IPAddress.Parse("fe80::1")));
        Assert.True(NetworkGuard.IsBlockedAddress(IPAddress.Parse("fec0::1")));
        Assert.False(NetworkGuard.IsBlockedAddress(IPAddress.Parse("2606:4700:4700::1111")));
    }

    [Fact]
    public async Task ResolvesToBlockedAddressAsync_BlocksLocalhostByName()
    {
        Assert.True(await NetworkGuard.ResolvesToBlockedAddressAsync("localhost",
                                                                     TestContext.Current.CancellationToken));
        Assert.True(await NetworkGuard.ResolvesToBlockedAddressAsync("LOCALHOST",
                                                                     TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolvesToBlockedAddressAsync_BlocksIpLiteralsWithoutDnsLookup()
    {
        Assert.True(await NetworkGuard.ResolvesToBlockedAddressAsync("127.0.0.1",
                                                                     TestContext.Current.CancellationToken));
        Assert.True(await NetworkGuard.ResolvesToBlockedAddressAsync("169.254.169.254",
                                                                     TestContext.Current.CancellationToken));
        Assert.False(await NetworkGuard.ResolvesToBlockedAddressAsync("8.8.8.8",
                                                                      TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolvesToBlockedAddressAsync_BlocksEmptyHost()
    {
        Assert.True(await NetworkGuard.ResolvesToBlockedAddressAsync(string.Empty,
                                                                     TestContext.Current.CancellationToken));
        Assert.True(await NetworkGuard.ResolvesToBlockedAddressAsync("   ",
                                                                     TestContext.Current.CancellationToken));
    }
}
