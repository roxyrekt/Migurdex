using Migurdex.Cli.Services.Downloads;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// <see cref="Mp4ResumeMetadata"/> sözleşmesini kilitleyen testlerdir.
///
/// <para>Dosyada daha önce hiç test yoktu. Resume okuma yolunun davranışı
/// (sürüm, fingerprint, bozuk JSON, eksik dosya) burada sabitlenir; böylece
/// C16 değerlendirmesi için yapılan "senkron okuma zararsız, dokunma" kararı
/// ileride birisi <c>File.ReadAllTextAsync</c>'ye çevirmeye kalkarsa değişen
/// tek şeyin performans olduğu görülebilir.</para>
/// </summary>
public sealed class Mp4ResumeMetadataTests
{
    private const string Fingerprint = "abcdef0123456789";
    private const string Other       = "0123456789abcdef";

    private static string NewTempDir()
    {
        return TestTempDirectory.Create("migurdex-mp4meta-");
    }

    private static async Task<string> WriteMetaAsync(string dir, Mp4ResumeMetadata metadata)
    {
        var path = Path.Combine(dir, "video.mp4." + Fingerprint + ".part.meta");
        await Mp4ResumeMetadata.WriteAsync(path, metadata, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public void Create_NormalizesEmptyValidatorsToNull()
    {
        var meta = Mp4ResumeMetadata.Create(Fingerprint, "", "   ", 1024);

        Assert.Equal(1, meta.Version);
        Assert.Equal(Fingerprint, meta.SourceFingerprint);
        Assert.Null(meta.ETagDigest);
        Assert.Null(meta.LastModifiedDigest);
        Assert.Equal(1024L, meta.TotalBytes);
        Assert.Null(meta.SegmentCount);
    }

    [Fact]
    public void Create_DigestsValidators_NotRaw()
    {
        var meta = Mp4ResumeMetadata.Create(Fingerprint, "W/\"abc\"", null, 2048);

        Assert.NotNull(meta.ETagDigest);
        Assert.NotEqual("W/\"abc\"", meta.ETagDigest);
        Assert.Equal(64, meta.ETagDigest!.Length);
        Assert.Null(meta.LastModifiedDigest);
    }

    [Fact]
    public async Task TryRead_Roundtrips_WriteAsync()
    {
        var dir  = NewTempDir();
        var meta = Mp4ResumeMetadata.Create(Fingerprint, "etag-1", "lm-1", 9999, 6);
        var path = await WriteMetaAsync(dir, meta);

        Assert.True(Mp4ResumeMetadata.TryRead(path, Fingerprint, out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(Fingerprint, loaded!.SourceFingerprint);
        Assert.Equal(9999L, loaded.TotalBytes);
        Assert.Equal(6, loaded.SegmentCount);
        Assert.Equal(meta.ETagDigest, loaded.ETagDigest);
        Assert.Equal(meta.LastModifiedDigest, loaded.LastModifiedDigest);
    }

    [Fact]
    public async Task TryRead_Leaves_No_Temporary_File()
    {
        var dir  = NewTempDir();
        var path = await WriteMetaAsync(dir, Mp4ResumeMetadata.Create(Fingerprint, null, null, 10));

        Assert.True(Mp4ResumeMetadata.TryRead(path, Fingerprint, out _));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryRead_MissingPath_ReturnsFalse(string? path)
    {
        Assert.False(Mp4ResumeMetadata.TryRead(path!, Fingerprint, out var meta));
        Assert.Null(meta);
    }

    [Fact]
    public void TryRead_DirectoryPath_ReturnsFalse()
    {
        var dir = NewTempDir();

        Assert.False(Mp4ResumeMetadata.TryRead(dir, Fingerprint, out var meta));
        Assert.Null(meta);
    }

    [Fact]
    public async Task TryRead_MismatchedFingerprint_ReturnsFalse()
    {
        var dir  = NewTempDir();
        var path = await WriteMetaAsync(dir, Mp4ResumeMetadata.Create(Fingerprint, null, null, 10));

        Assert.False(Mp4ResumeMetadata.TryRead(path, Other, out var meta));
        Assert.Null(meta);
    }

    [Fact]
    public void TryRead_FutureVersion_ReturnsFalse()
    {
        var dir = NewTempDir();
        // WriteAsync yalnızca CurrentVersion=1 yazabildiği için sürüm alanı elle
        // yükseltilir: ileride eklenebilecek bir alan okuma yolunu bozmamalı.
        var path = Path.Combine(dir, "video.mp4." + Fingerprint + ".part.meta");
        File.WriteAllText(path,
                          JsonSerializer.Serialize(new
                          {
                              Version            = 99,
                              SourceFingerprint  = Fingerprint,
                              ETagDigest         = (string?)null,
                              LastModifiedDigest = (string?)null,
                              TotalBytes         = 10,
                              SegmentCount       = (int?)null
                          }));

        Assert.False(Mp4ResumeMetadata.TryRead(path, Fingerprint, out var meta));
        Assert.Null(meta);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public void TryRead_CorruptPayload_ReturnsFalse(string json)
    {
        var dir  = NewTempDir();
        var path = Path.Combine(dir, "video.mp4." + Fingerprint + ".part.meta");
        File.WriteAllText(path, json);

        Assert.False(Mp4ResumeMetadata.TryRead(path, Fingerprint, out var meta));
        Assert.Null(meta);
    }

    [Fact]
    public void MatchesResponse_EmptyValidatorOnBothSides_Matches()
    {
        var meta = Mp4ResumeMetadata.Create(Fingerprint, null, null, 10);

        Assert.True(meta.MatchesResponse(null, null));
        Assert.True(meta.MatchesResponse("", "   "));
    }

    [Fact]
    public void MatchesResponse_SameValidator_Matches()
    {
        var meta = Mp4ResumeMetadata.Create(Fingerprint, "etag-1", "lm-1", 10);

        Assert.True(meta.MatchesResponse("etag-1", "lm-1"));
    }

    [Fact]
    public void MatchesResponse_DifferentValidator_DoesNotMatch()
    {
        var meta = Mp4ResumeMetadata.Create(Fingerprint, "etag-1", "lm-1", 10);

        Assert.False(meta.MatchesResponse("etag-2", "lm-1"));
        Assert.False(meta.MatchesResponse("etag-1", "lm-2"));
    }

    [Fact]
    public void MatchesResponse_ValidatorDisappears_DoesNotMatch()
    {
        // Sunucu yeniden indirmeye zorladığında ETag/LM kaybolabilir. Kayıtlı
        // fingerprint ile eşleşmeyen sunucu, bayt bayt devam etmeye güvenilmez.
        var meta = Mp4ResumeMetadata.Create(Fingerprint, "etag-1", "lm-1", 10);

        Assert.False(meta.MatchesResponse(null, "lm-1"));
        Assert.False(meta.MatchesResponse("etag-1", null));
    }

    [Fact]
    public void MatchesResponse_NoStoredValidators_AlwaysMatches()
    {
        var meta = Mp4ResumeMetadata.Create(Fingerprint, null, null, 10);

        Assert.True(meta.MatchesResponse("etag-1", "lm-1"));
    }

    [Fact]
    public async Task Delete_RemovesMetaAndTemporaryFile()
    {
        var dir  = NewTempDir();
        var path = await WriteMetaAsync(dir, Mp4ResumeMetadata.Create(Fingerprint, null, null, 10));
        File.WriteAllText(path + ".tmp", "yarim");

        Mp4ResumeMetadata.Delete(path);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Delete_BlankPath_IsNoop(string? path)
    {
        Mp4ResumeMetadata.Delete(path);
    }

    [Fact]
    public void Delete_MissingFile_DoesNotThrow()
    {
        var dir  = NewTempDir();
        var path = Path.Combine(dir, "yok.meta");

        Mp4ResumeMetadata.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_Overwrites_PreviousMetadata()
    {
        var dir  = NewTempDir();
        var path = await WriteMetaAsync(dir,
                                         Mp4ResumeMetadata.Create(Fingerprint, "etag-1", null, 10));

        await Mp4ResumeMetadata.WriteAsync(path,
                                            Mp4ResumeMetadata.Create(Fingerprint, "etag-2", null, 20),
                                            TestContext.Current.CancellationToken);

        Assert.True(Mp4ResumeMetadata.TryRead(path, Fingerprint, out var loaded));
        Assert.Equal(20L, loaded!.TotalBytes);
        Assert.Equal(Mp4ResumeMetadata.Create(Fingerprint, "etag-2", null, 20).ETagDigest,
                     loaded.ETagDigest);
    }

    [Fact]
    public async Task MetaFile_IsSmall_JsonEnvelope()
    {
        // C16 gerekçesi: .meta dosyası sabit sayıda kısa alandan oluşan bir JSON
        // zarftır; indirme başındaki senkron okuma bu boyutta kalır. 200 baytlık
        // ETag/LM girdileri ve long.MaxValue boyut kullanılsa bile sınır içindedir.
        var dir  = NewTempDir();
        var path = await WriteMetaAsync(dir,
                                        Mp4ResumeMetadata.Create(Fingerprint,
                                                                 new string('e', 200),
                                                                 new string('m', 200),
                                                                 long.MaxValue,
                                                                 32));

        var length = new FileInfo(path).Length;

        Assert.InRange(length, 0, 4096);
    }
}