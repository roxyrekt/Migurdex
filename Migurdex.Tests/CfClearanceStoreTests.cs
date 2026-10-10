using Migurdex.Core.Database;
using Migurdex.Core.Services.Turnstile;
using Xunit;

namespace Migurdex.Tests;

public sealed class CfClearanceStoreTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "migurdex-cftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SetAndGet_RoundtripByHost()
    {
        var store = new CfClearanceStore(new MigurdexDatabase(NewTempDir()));
        store.Set("ornek-saglayici.com", "abc123", DateTime.UtcNow.AddHours(2), "manual");

        Assert.True(store.TryGet("https://ornek-saglayici.com/anime/1", out var value, out var expires));
        Assert.Equal("abc123", value);
        Assert.True(expires > DateTime.UtcNow);
    }

    [Fact]
    public void ExpiredEntry_IsInvisible()
    {
        var dir = NewTempDir();
        var db  = new MigurdexDatabase(dir);
        db.SetCfClearance("eski-host.com", "bayat", DateTime.UtcNow.AddSeconds(-1), "manual");

        var store = new CfClearanceStore(db);
        Assert.False(store.TryGet("eski-host.com", out _, out _));
    }

    [Fact]
    public void MissingHost_ReturnsFalse()
    {
        var store = new CfClearanceStore(new MigurdexDatabase(NewTempDir()));

        Assert.False(store.TryGet("yok-host.com", out _, out _));
    }

    [Fact]
    public void MemoryOnly_DoesNotTouchDisk()
    {
        var dir   = NewTempDir();
        var store = new CfClearanceStore(new MigurdexDatabase(dir), memoryOnly: true);
        store.Set("gizli-host.com", "xyz", DateTime.UtcNow.AddHours(1), "manual");

        Assert.True(store.TryGet("gizli-host.com", out var value, out _));
        Assert.Equal("xyz", value);

        var disk = new MigurdexDatabase(dir);
        Assert.False(disk.TryGetCfClearance("gizli-host.com", out _, out _));
    }

    [Fact]
    public void GetHosts_ListsStoredEntries()
    {
        var store = new CfClearanceStore(new MigurdexDatabase(NewTempDir()));
        store.Set("b-host.com", "v1", DateTime.UtcNow.AddHours(1), "manual");
        store.Set("a-host.com", "v2", DateTime.UtcNow.AddHours(1), "userbrowser");

        var hosts = store.GetHosts();
        Assert.Equal(2, hosts.Count);
    }
}
