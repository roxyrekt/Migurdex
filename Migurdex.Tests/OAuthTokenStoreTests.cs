using Migurdex.Core.Services;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class OAuthTokenStoreTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "migurdex-tokentest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static OAuthToken Token(string provider = "anilist")
    {
        return new OAuthToken
        {
            Provider     = provider,
            AccessToken  = "access-123",
            RefreshToken = "refresh-456",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
    }

    [Fact]
    public void Set_Get_Roundtrip()
    {
        var store = new OAuthTokenStore(NewTempDir());

        store.Set(Token());

        Assert.True(store.TryGet("anilist", out var loaded));
        Assert.Equal("access-123", loaded!.AccessToken);
        Assert.Equal("refresh-456", loaded.RefreshToken);
        Assert.False(loaded.IsExpired);
        Assert.False(loaded.NeedsRefresh);
    }

    [Fact]
    public void Provider_Lookup_IsCaseInsensitive()
    {
        var store = new OAuthTokenStore(NewTempDir());
        store.Set(Token("AniList"));

        Assert.True(store.TryGet("ANILIST", out _));
        Assert.True(store.Remove("AnIlIsT"));
        Assert.False(store.TryGet("anilist", out _));
    }

    [Fact]
    public void Remove_Missing_ReturnsFalse()
    {
        var store = new OAuthTokenStore(NewTempDir());

        Assert.False(store.Remove("anilist"));
    }

    [Fact]
    public void Tokens_Persist_Across_Instances()
    {
        var dir = NewTempDir();
        new OAuthTokenStore(dir).Set(Token());

        var reopened = new OAuthTokenStore(dir);

        Assert.True(reopened.TryGet("anilist", out var loaded));
        Assert.Equal("access-123", loaded!.AccessToken);
        Assert.Equal(["anilist"], reopened.Providers);
    }

    [Fact]
    public void Corrupt_File_Resets_And_Keeps_Backup()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "tokens.json"), "{not-json");

        var store = new OAuthTokenStore(dir);

        Assert.Empty(store.Providers);
        Assert.False(store.TryGet("anilist", out _));
        Assert.Single(Directory.GetFiles(dir, "tokens.json.corrupt-*.bak"));
    }

    [Fact]
    public void Expiry_Helpers_Respect_Skew()
    {
        var fresh = Token();
        fresh.ExpiresAtUtc = DateTime.UtcNow.AddHours(1);
        Assert.False(fresh.IsExpired);
        Assert.False(fresh.NeedsRefresh);

        var nearExpiry = Token();
        nearExpiry.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(4);
        Assert.False(nearExpiry.IsExpired);
        Assert.True(nearExpiry.NeedsRefresh);

        var expired = Token();
        expired.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(expired.IsExpired);
        Assert.True(expired.NeedsRefresh);
    }

    [Fact]
    public void Token_File_IsOwnerOnly_OnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var dir = NewTempDir();
        new OAuthTokenStore(dir).Set(Token());

        var mode = File.GetUnixFileMode(Path.Combine(dir, "tokens.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
