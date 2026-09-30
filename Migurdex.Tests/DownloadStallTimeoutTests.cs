using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Grup B / C2: HTTP istemcisinde zaman aşımı yok.
///
/// İki <c>HttpClient</c> factory de <c>Timeout = Timeout.InfiniteTimeSpan</c> ile
/// kuruluyordu. Sunucu başlığı gönderip gövdeyi hiç göndermezse (ya da bayt
/// akışını ortasında durdurursa) indirme sonsuza dek asılı kalıyor, hiçbir hata
/// fırlatmıyordu.
///
/// Düzeltme duvar-saati zaman aşımı değil: <c>DownloadStallGuard</c> "son N
/// saniyede hiç bayt gelmedi" koşulunu denetliyor. Aşağıdaki testler o koşulu
/// sahte bir sunucuyla ölçüyor; hiçbiri gerçek saatlerce süren bir indirme
/// beklemiyor — eşikler milisaniye mertebesine küçültülmüş durumda ve takılma
/// senaryoları ~200 ms'de bitiyor.
/// </summary>
public sealed class DownloadStallTimeoutTests
{
    /// <summary>Asılı kalmama garantisi: bir test bu sürede tamamlanmazsa hata verir.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(8);

    private static DownloadStallOptions QuickOptions()
    {
        return new DownloadStallOptions
        {
            ResponseHeaderTimeout = TimeSpan.FromSeconds(4),
            FirstByteTimeout      = TimeSpan.FromMilliseconds(200),
            IdleTimeout           = TimeSpan.FromMilliseconds(200)
        };
    }
    /// <summary>
    /// "Yavaş ama düzenli akış" testleri için eşikler.
    ///
    /// <para><b>Neden <see cref="QuickOptions"/> değil.</b> <c>QuickOptions</c> bilerek
    /// küçük (200 ms) bütçeler kullanır; <i>takılma</i> testlerinin hızlı tetiklenmesi
    /// için. Ancak bloklar arası yalnızca 40 ms boşluk bırakılan bir akış, 200 ms
    /// bütçeye karşı sadece <b>5 kat</b> başlığa sahip. Yüklü bir makinede — GitHub
    /// Actions koşucusu, 16 çekirdekte 48 işlemci yakınıyla ölçüldü — tek bir 1 KiB
    /// okuma 200 ms'yi aşabildi ve koruma doğru şekilde devreye girdi:
    /// <c>Sunucu 1 saniye boyunca veri göndermedi</c>.</para>
    ///
    /// <para>Ölçülen kırılma oranı: yüksüz 8 koşuda <b>0</b>, %100 işlemci yükünde
    /// 10 koşuda <b>1</b>. Demek ki test, kendi varsayımı zayıf kaldığında kırılıyor;
    /// üretim kodu doğru davranıyordu.</para>
    ///
    /// <para>3 saniyelik bütçe 40 ms'lik araya <b>75 kat</b> başlık verir; yanlış
    /// tetikleme için tek bir okumanın 3 saniyeyi aşması gerekirdi. Normal akışta test
    /// yine ~240 ms sürer, yani zaman kaybı yok. Bütçe <c>HangGuard</c>'ın (8 sn)
    /// altında kaldığı için gerçek bir regresyon yine hızlı yakalanır.</para>
    /// </summary>
    private static DownloadStallOptions SlowSteadyOptions()
    {
        return new DownloadStallOptions
        {
            ResponseHeaderTimeout = TimeSpan.FromSeconds(4),
            FirstByteTimeout      = TimeSpan.FromSeconds(3),
            IdleTimeout           = TimeSpan.FromSeconds(3)
        };
    }

    [Fact]
    public async Task Mp4_BodyStopsAfterFirstChunk_FailsWithDownloadException()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source();
            // Sunucu ilk 4 KB'ı gönderiyor, sonra bayt akışını kesiyor.
            var handler = new SingleResponseHandler(MediaResponse(new ScriptedBodyStream(4096,
                                                                                        1,
                                                                                        stall: true,
                                                                                        TimeSpan.Zero),
                                                                                   1024 * 1024));

            var exception = await AssertFailsWithinAsync(() => new Mp4Downloader(handler, QuickOptions())
                                                                  .DownloadAsync(source,
                                                                                 target,
                                                                                 cancellationToken:
                                                                                 TestContext.Current.CancellationToken));

            var failure = Assert.IsType<DownloadException>(exception);
            Assert.Contains("veri göndermedi", failure.Message, StringComparison.Ordinal);
            Assert.Contains("takıldı", failure.Message, StringComparison.Ordinal);

            // Yarım kalan indirme resume için korunur, hedef dosya oluşmaz.
            Assert.False(File.Exists(target));
            var part = Mp4Downloader.GetResumePartPath(target, source);
            Assert.True(File.Exists(part));
            Assert.True(new FileInfo(part).Length > 0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mp4_NeverSendsFirstByte_UsesFirstByteThreshold()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            // İlk bayt eşiği 200 ms, boşta bekleme eşiği 30 sn: hata 200 ms'de
            // doğmuş olmalı, yoksa yanlış eşik devreye girmiş demektir.
            var options = new DownloadStallOptions
            {
                ResponseHeaderTimeout = TimeSpan.FromSeconds(4),
                FirstByteTimeout      = TimeSpan.FromMilliseconds(200),
                IdleTimeout           = TimeSpan.FromSeconds(30)
            };
            var handler = new SingleResponseHandler(MediaResponse(new ScriptedBodyStream(4096,
                                                                                        0,
                                                                                        stall: true,
                                                                                        TimeSpan.Zero),
                                                                                   1024 * 1024));

            var stopwatch = Stopwatch.StartNew();
            var exception = await AssertFailsWithinAsync(() => new Mp4Downloader(handler, options)
                                                                  .DownloadAsync(Source(),
                                                                                 target,
                                                                                 cancellationToken:
                                                                                 TestContext.Current.CancellationToken));
            stopwatch.Stop();

            Assert.IsType<DownloadException>(exception);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                        $"İlk bayt eşiği uygulanmadı: {stopwatch.ElapsedMilliseconds} ms sürdü.");
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mp4_SlowButSteadyBody_CompletesWithoutError()
    {
        var root = NewTempDir();
        try
        {
            const int chunks    = 6;
            const int chunkSize = 1024;
            var        total    = chunks * chunkSize;
            var        target   = Path.Combine(root, "episode.mp4");
            var stream = new ScriptedBodyStream(chunkSize, chunks, stall: false, TimeSpan.FromMilliseconds(40));
            var handler = new SingleResponseHandler(MediaResponse(stream, total));

            // Bekleme eşiği (200 ms) bloklar arası 40 ms'lik boşluktan uzun:
            // yavaş ama düzenli akış hata üretmemeli.
            var stopwatch = Stopwatch.StartNew();
            var result = await AssertSucceedsWithinAsync(() => new Mp4Downloader(handler, SlowSteadyOptions())
                                                              .DownloadAsync(Source(),
                                                                             target,
                                                                             cancellationToken:
                                                                             TestContext.Current.CancellationToken));
            stopwatch.Stop();

            var written = await File.ReadAllBytesAsync(result.OutputPath, TestContext.Current.CancellationToken);
            Assert.Equal(total, written.Length);
            Assert.Equal(ScriptedBodyStream.ExpectedPattern(total), written);
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150),
                        "Akış aslında yavaş değildi; test ölçtüğünü sandığı şeyi ölçmüyor.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mp4_InfiniteThresholds_DisableWatchdog()
    {
        var root = NewTempDir();
        try
        {
            const int chunks    = 3;
            const int chunkSize = 512;
            var        target   = Path.Combine(root, "episode.mp4");
            var handler = new SingleResponseHandler(MediaResponse(new ScriptedBodyStream(chunkSize,
                                                                                           chunks,
                                                                                           stall: false,
                                                                                           TimeSpan.FromMilliseconds(80)),
                                                                                      chunks * chunkSize));
            var options = new DownloadStallOptions
            {
                ResponseHeaderTimeout = Timeout.InfiniteTimeSpan,
                FirstByteTimeout      = Timeout.InfiniteTimeSpan,
                IdleTimeout           = Timeout.InfiniteTimeSpan
            };

            var result = await AssertSucceedsWithinAsync(() => new Mp4Downloader(handler, options)
                                                              .DownloadAsync(Source(),
                                                                             target,
                                                                             cancellationToken:
                                                                             TestContext.Current.CancellationToken));

            Assert.True(File.Exists(result.OutputPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mp4_CancelWhileStalled_ThrowsOperationCanceledException()
    {
        var root = NewTempDir();
        try
        {
            var target   = Path.Combine(root, "episode.mp4");
            var source   = Source();
            var handler  = new SingleResponseHandler(MediaResponse(new ScriptedBodyStream(4096,
                                                                                        1,
                                                                                        stall: true,
                                                                                        TimeSpan.Zero),
                                                                                   1024 * 1024));
            // Eşikler çok büyük: hata yalnızca iptalden gelebilir, ve iptal
            // beklemek zorunda kalmamalıdır.
            var options = new DownloadStallOptions
            {
                ResponseHeaderTimeout = TimeSpan.FromSeconds(30),
                FirstByteTimeout      = TimeSpan.FromSeconds(30),
                IdleTimeout           = TimeSpan.FromSeconds(30)
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var download = new Mp4Downloader(handler, options).DownloadAsync(source,
                                                                             target,
                                                                             cancellationToken: cts.Token);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            cts.Cancel();
            var exception = await AssertFailsWithinAsync(() => download);
            stopwatch.Stop();

            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                        $"İptal gecikmeli işledi: {stopwatch.ElapsedMilliseconds} ms.");
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(Mp4Downloader.GetResumePartPath(target, source)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mp4_ServerNeverAnswers_FailsInsteadOfHangingForever()
    {
        var root = NewTempDir();
        try
        {
            var target  = Path.Combine(root, "episode.mp4");
            var options = new DownloadStallOptions
            {
                ResponseHeaderTimeout = TimeSpan.FromMilliseconds(200),
                FirstByteTimeout      = TimeSpan.FromMilliseconds(200),
                IdleTimeout           = TimeSpan.FromMilliseconds(200)
            };

            var stopwatch = Stopwatch.StartNew();
            var exception = await AssertFailsWithinAsync(() => new Mp4Downloader(new NeverAnsweringHandler(),
                                                                                options)
                                                                  .DownloadAsync(Source(),
                                                                                 target,
                                                                                 cancellationToken:
                                                                                 TestContext.Current.CancellationToken));
            stopwatch.Stop();

            var failure = Assert.IsType<DownloadException>(exception);
            Assert.Contains("yanıt vermedi", failure.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                        $"Başlık beklemesi sınırsız kaldı: {stopwatch.ElapsedMilliseconds} ms.");
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mp4_ParallelSegmentsStall_FailsInsteadOfHangingForever()
    {
        var root = NewTempDir();
        try
        {
            const long total = 9L * 1024 * 1024; // paralel eşiğin üstünde
            var        target = Path.Combine(root, "big-episode.mp4");
            var handler = new RangeStallingHandler(total);

            var stopwatch = Stopwatch.StartNew();
            var exception = await AssertFailsWithinAsync(() => new Mp4Downloader(handler, QuickOptions())
                                                                  .DownloadAsync(Source(),
                                                                                 target,
                                                                                 cancellationToken:
                                                                                 TestContext.Current.CancellationToken));
            stopwatch.Stop();

            var failure = Assert.IsType<DownloadException>(exception);
            Assert.Contains("takıldı", failure.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                        $"Paralel segmentlerde takılma yakalanmadı: {stopwatch.ElapsedMilliseconds} ms.");
            Assert.True(handler.RangedRequests >= 2, "Paralel indirme yolu denenmedi.");
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Subtitle_BodyStopsMidStream_FailsAndLeavesNoSidecar()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            var handler = new SingleResponseHandler(SubtitleResponse(new ScriptedBodyStream(1024,
                                                                                            1,
                                                                                            stall: true,
                                                                                            TimeSpan.Zero)));
            var subtitle = new Subtitle
            {
                Url = "http://8.8.8.8/subtitle.srt"
            };

            var exception = await AssertFailsWithinAsync(() => new SubtitleDownloader(handler, QuickOptions())
                                                                  .DownloadAsync(subtitle,
                                                                                 mediaPath,
                                                                                 cancellationToken:
                                                                                 TestContext.Current.CancellationToken));

            var failure = Assert.IsType<DownloadException>(exception);
            Assert.Contains("veri göndermedi", failure.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DefaultThresholds_AreStallBasedNotWallClock()
    {
        var defaults = DownloadStallOptions.Default;

        // Naif duvar-saati sınırı (ör. 5 dk) 272 MB'lık bir dosyayı 1 Mbps'de
        // keserdi; eşik bu yüzden hem çok kısa hem çok uzun olamaz.
        Assert.InRange(defaults.IdleTimeout, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2));
        Assert.True(defaults.FirstByteTimeout >= defaults.IdleTimeout,
                    "İlk bayt eşiği boşta bekleme eşiğinden kısa olamaz.");
        Assert.True(defaults.ResponseHeaderTimeout >= defaults.FirstByteTimeout,
                    "Başlık eşiği ilk bayt eşiğinden kısa olamaz.");
    }

    /// <summary>
    /// Hata beklenen yollarda kullanılır: iş <see cref="HangGuard"/> içinde
    /// tamamlanmazsa test asılı kalan davranışı raporlayıp düşer, aksi hâlde
    /// fırlatılan istisna döndürülür.
    /// </summary>
    private static async Task<Exception> AssertFailsWithinAsync(Func<Task> action)
    {
        var running = Task.Run(action);
        await AssertFinishesAsync(running);
        try
        {
            await running;
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("İşlem hata fırlatmadı; test beklenen hatayı kanıtlayamadı.");
    }

    /// <summary>Başarılı yollar için: iş süresinde tamamlanmazsa test düşer.</summary>
    private static async Task<T> AssertSucceedsWithinAsync<T>(Func<Task<T>> action)
    {
        var running = Task.Run(action);
        await AssertFinishesAsync(running);
        return await running;
    }

    private static async Task AssertFinishesAsync(Task running)
    {
        var finished = await Task.WhenAny(running, Task.Delay(HangGuard));
        Assert.True(ReferenceEquals(running, finished),
                    $"İşlem {HangGuard.TotalSeconds:F0} saniye içinde tamamlanmadı (asılı kaldı).");
    }

    private static VideoSource Source()
    {
        // IP literal: NetworkGuard DNS'e başvurmadan izin verir, test ağa çıkmaz.
        return new VideoSource
        {
            Url  = "http://8.8.8.8/video.mp4",
            Type = VideoType.Mp4
        };
    }

    private static HttpResponseMessage MediaResponse(Stream body, long declaredLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new RawStreamContent(body)
        };
        response.Content.Headers.ContentType     = new MediaTypeHeaderValue("video/mp4");
        response.Content.Headers.ContentLength   = declaredLength;
        return response;
    }

    private static HttpResponseMessage SubtitleResponse(Stream body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new RawStreamContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-subrip");
        return response;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SingleResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Bağlantı açılıyor ama status satırı hiç gelmiyor. Sınırsız beklemenin
    /// en klasik hali.
    /// </summary>
    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Paralel (aralıklı) indirme yolunda her iki segment de takılıyor. İlk 200
    /// yanıtının gövdesi hiç okunmuyor; yoklama yalnız boyutu bildiriyor.
    /// </summary>
    private sealed class RangeStallingHandler : HttpMessageHandler
    {
        private readonly Lock _sync  = new();
        private readonly long _total;
        private          int  _rangedRequests;

        public RangeStallingHandler(long total)
        {
            _total = total;
        }

        public int RangedRequests
        {
            get
            {
                lock (_sync)
                {
                    return _rangedRequests;
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = request.Headers.TryGetValues("Range", out var values)
                            ? string.Join(",", values)
                            : null;
            if (range is null)
            {
                var probe = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new RawStreamContent(new MemoryStream())
                };
                probe.Content.Headers.ContentType   = new MediaTypeHeaderValue("video/mp4");
                probe.Content.Headers.ContentLength = _total;
                return Task.FromResult(probe);
            }

            lock (_sync)
            {
                _rangedRequests++;
            }

            var (start, end) = ParseRange(range);
            var length       = end - start + 1;
            var partial      = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new RawStreamContent(new ScriptedBodyStream(4096, 0, stall: true, TimeSpan.Zero))
            };
            partial.Content.Headers.ContentType     = new MediaTypeHeaderValue("video/mp4");
            partial.Content.Headers.ContentLength   = length;
            partial.Content.Headers.TryAddWithoutValidation("Content-Range",
                                                            $"bytes {start}-{end}/{_total}");
            return Task.FromResult(partial);
        }

        private (long Start, long End) ParseRange(string range)
        {
            var value = range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                            ? range["bytes=".Length..]
                            : range;
            var dash = value.IndexOf('-', StringComparison.Ordinal);
            return (long.Parse(value[..dash], System.Globalization.CultureInfo.InvariantCulture),
                    long.Parse(value[(dash + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class RawStreamContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult(stream);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(stream);
        }
    }

    /// <summary>
    /// Gerçek bağlantıyı taklit eden gövde akışı. <c>chunksToServe</c> bloktan
    /// sonra ya akışı düzgünce sonlandırır (EOF) ya da hiç tamamlanmayan bir
    /// okumada asılı kalır (takılma). Bloklar arasına <c>interval</c> kadar ara
    /// verilebilir; yavaş ama düzenli akışı taklit eder.
    /// </summary>
    private sealed class ScriptedBodyStream : Stream
    {
        private readonly int      _chunkSize;
        private readonly int      _chunksToServe;
        private readonly bool     _stall;
        private readonly TimeSpan _interval;
        private          int      _served;
        private          long     _position;

        public ScriptedBodyStream(int chunkSize, int chunksToServe, bool stall, TimeSpan interval)
        {
            _chunkSize     = chunkSize;
            _chunksToServe = chunksToServe;
            _stall         = stall;
            _interval      = interval;
        }

        /// <summary>Akışın ürettiği baytların deterministik deseni.</summary>
        public static byte[] ExpectedPattern(long length)
        {
            var data = new byte[length];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 251);
            }

            return data;
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
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[]        buffer,
                                                  int            offset,
                                                  int            count,
                                                  CancellationToken cancellationToken)
        {
            if (_served >= _chunksToServe)
            {
                if (_stall)
                {
                    // Sunucu bayt göndermeyi kesti: okuma iptal edilene dek asılı kalır.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                              .ConfigureAwait(false);
                }

                return 0;
            }

            if (_served > 0 && _interval > TimeSpan.Zero)
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }

            var length = Math.Min(count, _chunkSize);
            for (var i = 0; i < length; i++)
            {
                buffer[offset + i] = (byte)((_position + i) % 251);
            }

            _position += length;
            _served++;
            return length;
        }
    }
}
