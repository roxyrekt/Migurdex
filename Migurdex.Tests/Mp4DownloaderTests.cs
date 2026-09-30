using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class Mp4DownloaderTests
{
    [Fact]
    public async Task Download_200_StreamsBodyAndMovesPartAtomically()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "video-body")]);
            var downloader = new Mp4Downloader(handler);
            var target = Path.Combine(root, "episode.mp4");

            var result = await downloader.DownloadAsync(
                Source("https://origin.example/video"),
                target,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("video-body", await File.ReadAllTextAsync(result.OutputPath,
                                                                      TestContext.Current.CancellationToken));
            Assert.True(File.Exists(target));
            Assert.Empty(PartPaths(target));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ReportsBytesTotalAndStages()
    {
        var root = NewTempDir();
        try
        {
            var progress = new RecordingProgress();
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "12345")]);

            await new Mp4Downloader(handler).DownloadAsync(
                Source("https://origin.example/video"),
                Path.Combine(root, "episode.mp4"),
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(progress.Items, item => item.Stage == DownloadStage.Downloading
                                                          && item.BytesDownloaded == 5
                                                          && item.TotalBytes == 5);
            Assert.Equal(DownloadStage.Completed, progress.Items[^1].Stage);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_AppliesSourceHeaders()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "body")]);
            var source = Source("https://origin.example/video");
            source.Headers = new Dictionary<string, string>
            {
                ["X-Test"] = "header-value",
                ["Authorization"] = "Bearer secret"
            };

            await new Mp4Downloader(handler).DownloadAsync(
                source,
                Path.Combine(root, "episode.mp4"),
                cancellationToken: TestContext.Current.CancellationToken);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("header-value", request.Headers["X-Test"]);
            Assert.Equal("Bearer secret", request.Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RedirectDropsCustomAndSensitiveHeadersAcrossOrigin()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                Redirect("https://cdn.example/final"),
                Response(HttpStatusCode.OK, "body")
            ]);
            var source = Source("https://origin.example/video");
            source.Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "session=secret",
                ["Cookie2"] = "session=secret",
                ["Authorization"] = "Bearer secret",
                ["Proxy-Authorization"] = "Basic secret",
                ["X-Api-Key"] = "secret",
                ["X-Referer"] = "drop-me",
                ["User-Agent"] = "safe-agent",
                ["Referer"] = "https://origin.example/page"
            };

            await new Mp4Downloader(handler).DownloadAsync(
                source,
                Path.Combine(root, "episode.mp4"),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("session=secret", handler.Requests[0].Headers["Cookie"]);
            Assert.Equal("Bearer secret", handler.Requests[0].Headers["Authorization"]);
            Assert.False(handler.Requests[1].Headers.ContainsKey("Cookie"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("Cookie2"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("Proxy-Authorization"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("X-Api-Key"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("X-Referer"));
            Assert.Equal("safe-agent", handler.Requests[1].Headers["User-Agent"]);
            Assert.Equal("https://origin.example/page", handler.Requests[1].Headers["Referer"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_KeepsSensitiveHeadersOnSameOriginRedirect()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                Redirect("https://origin.example/redirected"),
                Response(HttpStatusCode.OK, "body")
            ]);
            var source = Source("https://origin.example/video");
            source.Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "session=secret",
                ["Authorization"] = "Bearer secret"
            };

            await new Mp4Downloader(handler).DownloadAsync(
                source,
                Path.Combine(root, "episode.mp4"),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("session=secret", handler.Requests[1].Headers["Cookie"]);
            Assert.Equal("Bearer secret", handler.Requests[1].Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_UsesDifferentPartForDifferentSourceCandidates()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var first = Source("https://origin.example/video-a");
            var second = Source("https://origin.example/video-b");
            var partA = Mp4Downloader.GetResumePartPath(target, first);
            var partB = Mp4Downloader.GetResumePartPath(target, second);
            var sameUrlDifferentHeaders = Source("https://origin.example/video-a");
            sameUrlDifferentHeaders.Headers = new Dictionary<string, string>
            {
                ["X-Api-Key"] = "different-source"
            };
            Assert.NotEqual(partA, partB);
            Assert.DoesNotContain("origin.example", partA, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(partA, Mp4Downloader.GetResumePartPath(target, sameUrlDifferentHeaders));

            await SeedPartAsync(first, target, "old-a", TestContext.Current.CancellationToken);
            await SeedPartAsync(second, target, "fresh-b", TestContext.Current.CancellationToken);
            Assert.Equal(2, PartPaths(target).Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_KeepsSeparatePartsForTwoSourceCandidates()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var first = Source("https://origin.example/candidate-a");
            var second = Source("https://origin.example/candidate-b");
            await SeedPartAsync(first, target, "aaaa", TestContext.Current.CancellationToken);
            await SeedPartAsync(second, target, "bbbb", TestContext.Current.CancellationToken);

            var parts = PartPaths(target);
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts, path => File.ReadAllText(path) == "aaaa");
            Assert.Contains(parts, path => File.ReadAllText(path) == "bbbb");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_StalePartFromPreviousCandidateIsNeverAppended()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var first = Source("https://origin.example/first");
            var second = Source("https://origin.example/second");
            var firstPart = Mp4Downloader.GetResumePartPath(target, first);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPart)!);
            await File.WriteAllTextAsync(firstPart,
                                         "old-provider",
                                         TestContext.Current.CancellationToken);

            var result = await new Mp4Downloader(new ScriptedHandler(
                    [Response(HttpStatusCode.OK, "new-provider")]))
                .DownloadAsync(second, target, cancellationToken: TestContext.Current.CancellationToken);

            // Farklı fingerprint'a ait parça hiçbir koşulda yanıt gövdesine eklenmez:
            // sonuç yalnız ikinci kaynağın içeriğidir.
            Assert.Equal("new-provider", await File.ReadAllTextAsync(result.OutputPath,
                                                                         TestContext.Current.CancellationToken));
            // Aday izolasyonu sözleşmesi: az sayıda aday varken hiçbiri silinmez, böylece
            // ileride aynı adaya dönüldüğünde kısmi indirme kullanılabilir.
            Assert.Equal("old-provider", await File.ReadAllTextAsync(firstPart,
                                                                         TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Fingerprint_IsStableAcrossSignedUrlRefresh_ButDistinctPerVideo()
    {
        // Aynı dosya, farklı imza/zaman aşımı/IP bağlama değerleriyle çözülür.
        const string template =
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/{0}/ei/aCW8atuEHZT3j/ip/{1}/sig/{2}/lsig/{3}/id/o-ABCD1234efgh/itag/137/source/youtube/index/1.m3u8?x-goog-signature={4}";
        var first = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "1790726040", "176.234.88.53", "SIGONE", "LSIGONE", "AAA"),
            null);
        var second = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "1790729999", "10.0.0.9", "SIGTWO", "LSIGTWO", "BBB"),
            null);

        Assert.Equal(first, second);

        // Kimlik (`id`) değişirse fingerprint de değişmelidir; aksi hâlde iki farklı
        // video aynı parçaya yönlenir.
        var otherVideo = DownloadHttp.CreateSourceFingerprint(
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1790726040/ei/aCW8atuEHZT3j/ip/1.2.3.4/sig/A/lsig/B/id/o-ZZZZ9999zzzz/itag/137/source/youtube/index/1.m3u8",
            null);
        Assert.NotEqual(first, otherVideo);

        // Kalite (`itag`) değişirse de farklı olmalı.
        var otherQuality = DownloadHttp.CreateSourceFingerprint(
            "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1790726040/ei/aCW8atuEHZT3j/ip/1.2.3.4/sig/A/lsig/B/id/o-ABCD1234efgh/itag/140/source/youtube/index/1.m3u8",
            null);
        Assert.NotEqual(first, otherQuality);
    }

    [Fact]
    public void Fingerprint_NonSignedUrlsAreUnchangedInBehaviour()
    {
        // İmzasız, sıradan URL'lerde maskeleme hiçbir şeye dokunmamalı: farklı yol,
        // farklı ana, farklı sorgu -> farklı fingerprint.
        var a = DownloadHttp.CreateSourceFingerprint("https://cdn.example/files/1.mp4", null);
        var b = DownloadHttp.CreateSourceFingerprint("https://cdn.example/files/2.mp4", null);
        var c = DownloadHttp.CreateSourceFingerprint("http://cdn.example/files/1.mp4", null);
        var d = DownloadHttp.CreateSourceFingerprint("https://cdn.example/files/1.mp4?v=2", null);
        var e = DownloadHttp.CreateSourceFingerprint("https://other.example/files/1.mp4", null);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.NotEqual(a, e);
        Assert.Equal(a, DownloadHttp.CreateSourceFingerprint("https://cdn.example/files/1.mp4", null));
    }

    [Fact]
    public void Fingerprint_IsStableForSendvidStyleTimeBoundedLinks()
    {
        // CANLI GÖZLEM: videos*.sendvid.com imzayı sorgu dizesinde taşıyor. İlk
        // düzeltme denemesinde `hash`/`validfrom`/`validto` liste dışı bırakıldığı için
        // bu sağlayıcıda resume hâlâ bozuktu. Test, canlı URL'den alınan gerçek
        // biçimi kilitler.
        const string template =
            "https://videos2.sendvid.com/{0}/{1}/{2}.mp4?validfrom={3}&validto={4}&rate=250k&hash={5}";
        var first = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "16", "6a", "r5r9j6jf", "1790712300", "1790726700",
                          "EuvBiUd%2BEiA%2FT8Q%2BFsi1ZQ9uk3c%3D"),
            null);
        var refreshed = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "16", "6a", "r5r9j6jf", "1790719800", "1790734200",
                          "ZZzOtherHash%2BValue%3D"),
            null);

        Assert.Equal(first, refreshed);

        // Farklı dosya yolu -> farklı fingerprint.
        var otherFile = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "9a", "e9", "95xbd5f8", "1790712300", "1790726700",
                          "NdyT9E9w%2BoYjeuMe9GvUDablqhw%3D"),
            null);
        Assert.NotEqual(first, otherFile);

        // Aynı yolda farklı kalite (`rate`) sorgu parametresidir ve kimlik listesinde yoktur;
        // ayrım artık `VideoSource.Quality` tanımından gelir. Bu yüzden tanım verildiğinde
        // ayrılır, verilmediğinde aynı yol tek dosya sayılır.
        var samePathOtherRate = string.Format(template, "16", "6a", "r5r9j6jf", "1790712300",
                                             "1790726700", "EuvBiUd%2BEiA%2FT8Q%2BFsi1ZQ9uk3c%3D")
                                 .Replace("rate=250k", "rate=500k");
        Assert.Equal(first, DownloadHttp.CreateSourceFingerprint(samePathOtherRate, null));
        Assert.NotEqual(first,
                        DownloadHttp.CreateSourceFingerprint(samePathOtherRate, null, "1080p"));
        Assert.NotEqual(DownloadHttp.CreateSourceFingerprint(samePathOtherRate, null, "1080p"),
                        DownloadHttp.CreateSourceFingerprint(samePathOtherRate, null, "720p"));
    }

    [Fact]
    public void Fingerprint_IsStableAcrossGoogleDriveStyleSignedUrls()
    {
        // CANLI GÖZLEM: c.drive.google.com/videoplayback her çözümlemede 17+ volatil
        // parametre yeniliyor (xpc met mh mm mn ms mv mvi pl rms cnr mt txp eaua fvip
        // sparams lsparams sig lsig expire ei ip). Kara liste bu sağlayıcı için yetersiz
        // kaldı; beyaz liste (id itag clen dur lmt mime) kararlılık sağlıyor.
        const string template =
            "https://rr3---sn-ajnv45-5p.c.drive.google.com/videoplayback?expire={0}&ei={1}&ip={2}" +
            "&id=001828c3a842e9e4&itag=37&source=webdrive&requiressl=yes&xpc={3}&met={4}," +
            "&mh=j4&mm=32,26&mn=sn-ajnv45-5p&ms=su,onr&mv=m&mvi=3&pl=21&rms=su,su" +
            "&ttl=transient&driveid=1VfeTq_uYg4rlmG1tkW02Lahv6g4Q8h_R&mime=video/mp4" +
            "&dur=1375.540&lmt=1734561105974535&txp={5}&cnr=14&eaua={6}" +
            "&sparams=expire,ei,ip,id,itag&sig={7}&lsparams=met,mh,mm&lsig={8}";

        var first = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "1790730789", "9Te8avn-Gbv6zLUPjur9kAs", "176.234.88.53",
                          "EghonaK1InoBAQ==", "1790719989", "0011224", "pBlcdwMgf58", "SIGONE", "LSIGONE"),
            null, "1080p");
        var refreshed = DownloadHttp.CreateSourceFingerprint(
            string.Format(template, "1790739999", "ZZzOtherEI-value", "10.0.0.9",
                          "OTHERxpc==", "1790729988", "0099887", "OTHEReaua", "SIGTWO", "LSIGTWO"),
            null, "1080p");

        Assert.Equal(first, refreshed);

        // Farklı kalite -> farklı fingerprint (itag sorguda, beyaz listede).
        var otherItag = DownloadHttp.CreateSourceFingerprint(
            "https://rr3---sn-ajnv45-5p.c.drive.google.com/videoplayback?expire=1&ei=x&ip=1.2.3.4" +
            "&id=001828c3a842e9e4&itag=22&mime=video/mp4&dur=1375.540&lmt=1734561105974535" +
            "&sig=A&lsig=B",
            null, "1080p");
        Assert.NotEqual(first, otherItag);

        // Farklı dosya -> farklı fingerprint (id).
        var otherId = DownloadHttp.CreateSourceFingerprint(
            "https://rr3---sn-ajnv45-5p.c.drive.google.com/videoplayback?expire=1&ei=x&ip=1.2.3.4" +
            "&id=2c29d78132173102&itag=37&mime=video/mp4&dur=1375.540&lmt=1734561105974535" +
            "&sig=A&lsig=B",
            null, "1080p");
        Assert.NotEqual(first, otherId);
    }

    [Fact]
    public void GetResumePartPath_IsIdenticalForRefreshedSignedUrls()
    {
        // Kullanıcıya görünen davranış: imzalı kaynakta URL her çözümlemede yenilendiği
        // için eski sürümde her deneme YENİ bir .part dosyası üretiyordu. Resume hiç
        // çalışmıyor, her deneme bölüm boyutunda orphan bırakıyordu.
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "S01E01 - Ornek.mp4");

            var firstUrl =
                "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1790726040/ei/aCW8atuEHZT3j/ip/176.234.88.53/ipbits/32/sig/SIG-ONE/lsig/LSIG-ONE/id/o-ABCD1234efgh/itag/137/source/youtube/index/1.m3u8";
            var refreshedUrl =
                "https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1790729999/ei/zZZ9xxxOtherEI/ip/10.0.0.9/ipbits/32/sig/SIG-TWO/lsig/LSIG-TWO/id/o-ABCD1234efgh/itag/137/source/youtube/index/1.m3u8";

            var firstPart = Mp4Downloader.GetResumePartPath(target, Source(firstUrl));
            var refreshedPart = Mp4Downloader.GetResumePartPath(target, Source(refreshedUrl));

            Assert.Equal(firstPart, refreshedPart);

            // Farklı bölüm/farklı video hâlâ ayrı dosya kullanır.
            var otherEpisode = Mp4Downloader.GetResumePartPath(
                Path.Combine(root, "S01E02 - Ornek.mp4"),
                Source(refreshedUrl));
            Assert.NotEqual(firstPart, otherEpisode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetStaleResumePartPaths_TrimsOldestGroupsBeyondRetentionLimit()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var current = DownloadHttp.CreateSourceFingerprint("https://origin.example/current", null);

            // 5 eski aday; her biri PARALEL indirme biçiminde: .part.meta + 4 segment.
            var groups = new List<string[]>();
            for (var index = 0; index < 5; index++)
            {
                var fingerprint = DownloadHttp.CreateSourceFingerprint(
                    "https://origin.example/other-" + index.ToString(CultureInfo.InvariantCulture),
                    null);
                var partBase = Path.GetFullPath(target) + "." + fingerprint + ".part";
                var paths = new[]
                {
                    partBase,
                    partBase + ".seg0",
                    partBase + ".seg1",
                    partBase + ".seg2",
                    partBase + ".seg3",
                    partBase + ".meta"
                };
                foreach (var path in paths)
                {
                    File.WriteAllText(path, "x");
                }

                // Grubun **her** dosyası damgalanmalı. `GetStaleResumePartPaths`
                // grup zamanını grup içindeki en yeni dosyadan alıyor
                // (`if (modified > group.ModifiedUtc)`), bu yüzden yalnız `.part`
                // damgalanırsa kalan dosyalar yazım anında kalır ve beş grubun
                // zamanı birebir eşitlenir.
                //
                // Bu dosya sistemi mtime'ı yazma döngüsünden kaba: Linux'ta 8
                // ardışık yazmada da mtime_ns birebir aynı ölçüldü. Eşitlikte seçim
                // kararlı bağlayıcıya (fingerprint sırası) düştüğü için test bu
                // senaryoda %95-100 kırılıyordu. Kardeş test
                // `HandlesGlobMetacharactersInFileName` aynı düzeltmeyi almıştı,
                // bu test unutulmuştu.
                var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                   .AddMinutes(index);
                foreach (var path in paths)
                {
                    File.SetLastWriteTimeUtc(path, stamp);
                }

                groups.Add(paths);
            }

            // 5 gruptan en yeni 2'si korunur -> 3 grup atılır.
            var stale = Mp4Downloader.GetStaleResumePartPaths(target, current);

            Assert.Equal(18, stale.Count); // 3 grup x 6 dosya
            // En eski 3 grup tamamen atılmalı (segmentler dahil, meta dahil).
            foreach (var path in groups[0].Concat(groups[1]).Concat(groups[2]))
            {
                Assert.Contains(path, stale);
            }

            // En yeni 2 grup korunmalı.
            foreach (var path in groups[3].Concat(groups[4]))
            {
                Assert.DoesNotContain(path, stale);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetStaleResumePartPaths_IgnoresCurrentGroupAndUnrelatedFiles()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var current = DownloadHttp.CreateSourceFingerprint("https://origin.example/current", null);
            var currentBase = Path.GetFullPath(target) + "." + current + ".part";
            foreach (var path in new[] { currentBase, currentBase + ".seg0", currentBase + ".meta" })
            {
                File.WriteAllText(path, "x");
            }

            // Komşu dosyalar TUTULMAMALI.
            var neighbour = Path.Combine(root, "other-episode.mp4.deadbeefdeadbeefdeadbeef.part.seg0");
            File.WriteAllText(neighbour, "x");
            // Benzer ama fingerprint uzunluğu yanlış -> eşleşmemeli.
            var wrongLength = Path.Combine(root, "episode.mp4.tooshort.part");
            File.WriteAllText(wrongLength, "x");

            var stale = Mp4Downloader.GetStaleResumePartPaths(target, current);

            Assert.Empty(stale);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetStaleResumePartPaths_HandlesGlobMetacharactersInFileName()
    {
        var root = NewTempDir();
        try
        {
            // `[` ve `]` Windows dosya adında yasal ama glob desen meta karakteridir;
            // desen tabanlı tarama bunları yanlış yorumlar.
            var target = Path.Combine(root, "b[1] bolum.mp4");
            var current = DownloadHttp.CreateSourceFingerprint("https://origin.example/x", null);
            var groups = new List<string>();
            for (var index = 0; index < 4; index++)
            {
                var other = DownloadHttp.CreateSourceFingerprint("https://origin.example/y" + index, null);
                var partBase = Path.GetFullPath(target) + "." + other + ".part";
                File.WriteAllText(partBase + ".seg0", "x");
                File.WriteAllText(partBase, "x");

                // Gruptaki **her** dosyanın zamanı ayarlanmalı. `ModifiedUtc` grubun en
                // yeni dosyasıdır; yalnız `.part` ayarlanırsa `.seg0` yazım anında
                // kalır, dört grubun hepsi eşitlenir ve sıralama keyfîleşir. Bu yüzden
                // test izole koşumda 12'de 3 kez (%25) kırılıyordu.
                var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                   .AddMinutes(index);
                File.SetLastWriteTimeUtc(partBase, stamp);
                File.SetLastWriteTimeUtc(partBase + ".seg0", stamp);
                groups.Add(partBase);
            }

            var stale = Mp4Downloader.GetStaleResumePartPaths(target, current);

            // En eski 2 grup korunur.
            Assert.Contains(groups[0], stale);
            Assert.Contains(groups[0] + ".seg0", stale);
            Assert.Contains(groups[1], stale);
            Assert.Contains(groups[1] + ".seg0", stale);

            // En yeni 2 grup resume için korunur.
            Assert.DoesNotContain(groups[2], stale);
            Assert.DoesNotContain(groups[3], stale);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetStaleResumePartPaths_IsDeterministicWhenTimestampsTie()
    {
        // Zaman damgaları çakıştığında korunan 2 grup **keyfî** olmamalı. Bağlayıcısız
        // `OrderByDescending(...).Skip(n)` dosya sistemi sıralamasına göre seçiyordu;
        // aynı girdi iki çağrıda farklı sonuç verebiliyordu. Üretimde bu, aday
        // izolasyonu sözleşmesinin sessizce bozulması demek.
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "belgesel.mp4");
            var current = DownloadHttp.CreateSourceFingerprint("https://origin.example/x", null);
            var all = new List<string>();

            for (var index = 0; index < 5; index++)
            {
                var other = DownloadHttp.CreateSourceFingerprint("https://origin.example/z" + index, null);
                var partBase = Path.GetFullPath(target) + "." + other + ".part";
                File.WriteAllText(partBase, "x");

                // Bütün gruplar bilinçli olarak **aynı** zamanı alıyor.
                File.SetLastWriteTimeUtc(partBase,
                                         new DateTime(2026, 5, 5, 5, 5, 5, DateTimeKind.Utc));
                all.Add(partBase);
            }

            // 5 grup, 2 korunur -> 3 grup atılmalı. 25 tekrar boyunca birebir aynı.
            var first = Mp4Downloader.GetStaleResumePartPaths(target, current);
            for (var repeat = 0; repeat < 25; repeat++)
            {
                var again = Mp4Downloader.GetStaleResumePartPaths(target, current);
                Assert.Equal(first, again);
            }

            Assert.Equal(3, first.Count);
            Assert.Equal(2, all.Count(part => !first.Contains(part)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_206_AppendsAtValidatedRangeAfterCancellation()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            var response = Response(HttpStatusCode.PartialContent, "def");
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 3-5/6");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var handler = new ScriptedHandler([response]);
            var downloader = new Mp4Downloader(handler);

            var result = await downloader.DownloadAsync(
                source,
                target,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("abcdef", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.Equal("bytes=3-", handler.Requests[0].Headers["Range"]);
            Assert.Empty(PartPaths(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_200_TruncatesPartWhenRangeIsIgnored()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "stale", TestContext.Current.CancellationToken);
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "fresh")]);
            var downloader = new Mp4Downloader(handler);

            var result = await downloader.DownloadAsync(source,
                                                         target,
                                                         cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.Equal("bytes=5-", handler.Requests[0].Headers["Range"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ResumeFalseStartsFreshWithoutRange()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source,
                                target,
                                "stale",
                                TestContext.Current.CancellationToken);

            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "fresh")]);
            var result = await new Mp4Downloader(handler).DownloadAsync(source,
                                                                          target,
                                                                          resume: false,
                                                                          cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.False(handler.Requests[0].Headers.ContainsKey("Range"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_Invalid206SafelyRestartsInsteadOfAppending()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var invalid = Response(HttpStatusCode.PartialContent, "fresh");
            invalid.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 99-103/104");

            var result = await new Mp4Downloader(new ScriptedHandler(
                    [invalid, Response(HttpStatusCode.OK, "fresh")]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ContentRangeUnknownTotalAndShortBodyDoesNotFinalize()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var shortResponse = Response(HttpStatusCode.PartialContent, "d");
            shortResponse.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 3-8/*");

            await Assert.ThrowsAsync<DownloadException>(() => new Mp4Downloader(
                    new ScriptedHandler([shortResponse]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(File.Exists(target));
            Assert.Empty(PartPaths(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ShortStandard206DoesNotFinalize()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var shortResponse = Response(HttpStatusCode.PartialContent, "d");
            shortResponse.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 3-8/20");

            await Assert.ThrowsAsync<DownloadException>(() => new Mp4Downloader(
                    new ScriptedHandler([shortResponse]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_416_CompletesOnlyWhenPartLengthMatchesTotal()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "done", TestContext.Current.CancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new StringContent(string.Empty)
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */4");
            var handler = new ScriptedHandler([response]);

            var result = await new Mp4Downloader(handler)
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("done", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.Equal("bytes=4-", handler.Requests[0].Headers["Range"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_416WithWrongPartSizeRestartsInsteadOfFinalizing()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new StringContent(string.Empty)
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */4");

            var result = await new Mp4Downloader(new ScriptedHandler(
                    [response, Response(HttpStatusCode.OK, "fresh")]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_OverwriteReplacesExistingFinalOnlyAfterSuccessfulMove()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(target, "old", TestContext.Current.CancellationToken);
            var result = await new Mp4Downloader(new ScriptedHandler([Response(HttpStatusCode.OK, "new")]))
                .DownloadAsync(Source("https://origin.example/video"),
                               target,
                               overwrite: true,
                               cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("new", await File.ReadAllTextAsync(result.OutputPath,
                                                               TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_CancellationLeavesSourcePartForResume()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "partial", TestContext.Current.CancellationToken);
            var part = Mp4Downloader.GetResumePartPath(target, source);
            using var cts = new CancellationTokenSource();
            var download = new Mp4Downloader(new ScriptedHandler(
                    [new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new DirectStreamContent(new PartialThenWaitStream("more"))
                    }]))
                .DownloadAsync(source, target, cancellationToken: cts.Token);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
            Assert.True(File.Exists(part));
            Assert.NotEmpty(await File.ReadAllTextAsync(part, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task SeedPartAsync(
        VideoSource source,
        string target,
        string body,
        CancellationToken cancellationToken)
    {
        var response = Response(HttpStatusCode.OK, body);
        response.Content.Headers.ContentLength = body.Length + 1;
        var task = new Mp4Downloader(new ScriptedHandler([response]))
            .DownloadAsync(source, target, cancellationToken: cancellationToken);
        await Assert.ThrowsAsync<DownloadException>(async () => await task);
        Assert.True(File.Exists(Mp4Downloader.GetResumePartPath(target, source)));
    }

    private static string[] PartPaths(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        return Directory.Exists(directory)
                   ? Directory.GetFiles(directory, Path.GetFileName(target) + ".*.part")
                   : [];
    }

    [Fact]
    public async Task Download_RejectsNonMediaContentTypeWithoutWritingAnyFile()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/plain")
            };
            var handler = new ScriptedHandler([response]);

            var exception = await Assert.ThrowsAsync<DownloadException>(() => new Mp4Downloader(handler)
                                                      .DownloadAsync(source,
                                                                     target,
                                                                     cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("medya", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(Mp4Downloader.GetResumePartPath(target, source)));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_KeepsValidMediaContentTypeWithoutExtraRangeRequest()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var handler = new ScriptedHandler(
                [new HttpResponseMessage(HttpStatusCode.OK)
                 {
                     Content = new StringContent("bytes", Encoding.UTF8, "application/octet-stream")
                 }]);

            var result = await new Mp4Downloader(handler)
                .DownloadAsync(Source("https://origin.example/video"),
                               target,
                               cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("bytes", await File.ReadAllTextAsync(result.OutputPath,
                                                              TestContext.Current.CancellationToken));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static VideoSource Source(string url)
    {
        return new VideoSource
        {
            Url = url,
            Type = VideoType.Mp4
        };
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "video/mp4")
        };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent(string.Empty)
        };
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-mp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(RequestSnapshot.Capture(request));
            return Task.FromResult(_responses.Count > 0
                                       ? _responses.Dequeue()
                                       : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed record RequestSnapshot(Uri? RequestUri, Dictionary<string, string> Headers)
    {
        public static RequestSnapshot Capture(HttpRequestMessage request)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            return new RequestSnapshot(request.RequestUri, headers);
        }
    }

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = [];

        public void Report(DownloadProgress value)
        {
            Items.Add(value);
        }
    }

    private sealed class DirectStreamContent : HttpContent
    {
        private readonly Stream _stream;

        public DirectStreamContent(Stream stream)
        {
            _stream = stream;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return _stream.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult(_stream);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_stream);
        }
    }

    private sealed class PartialThenWaitStream : Stream
    {
        private readonly byte[] _initial;
        private          bool   _sent;

        public PartialThenWaitStream(string initial)
        {
            _initial = Encoding.UTF8.GetBytes(initial);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_sent)
            {
                _sent = true;
                _initial.CopyTo(buffer, offset);
                return _initial.Length;
            }

            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _initial.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(_initial.Length);
            }

            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override Task<int> ReadAsync(byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                _initial.CopyTo(buffer, offset);
                return Task.FromResult(_initial.Length);
            }

            return WaitForCancellationAsync(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
