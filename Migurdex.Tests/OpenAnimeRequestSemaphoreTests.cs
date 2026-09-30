using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Plugins.OpenAnime;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 1 (KRİTİK): <c>OpenAnimeProvider.SendRequestAsync</c>, 401/403 aldığında
/// yeniden denemeyi <b>permit hâlâ tutulurken</b> yapıyordu. <c>SemaphoreSlim</c>
/// yeniden girişe izin vermediği için, istek semaforu 5 izinliyken 5 eşzamanlı istek
/// 401 alıp hepsi aynı anda yeniden denediğinde talep 10, arz 5 olur: beşinin de
/// <c>finally</c>'si çalışmadan ikinci permiti bekler ve hepsi kalıcı olarak bloklanır.
///
/// Ölümcül tarafı kilitlenmenin <b>sokette değil semaforda</b> olması:
/// <c>HttpClient.Timeout</c> devreye girmez, API'nin <c>Task.WhenAll</c>'i hiç
/// dönmez, SSE <c>done</c> olayı yazılmaz ve CLI Ctrl+C'ye kadar asılı kalır.
///
/// Tetikleyici sıradandır: sağlayıcı sezon/episode/fansub isteklerini
/// <c>Task.WhenAll</c> ile eşzamanlı atar, kullanıcının gateway token'ı süresi
/// dolduğunda hepsi 401 alır.
///
/// <para><b>Test tasarımı.</b> İlk dalgadaki istekler, ya N tanesi bir araya geldiğinde
/// ya da <c>GateGrace</c> dolduğunda 401 alır. Sabit bir randevu noktası şart değildi:
/// randevuya bağlı bir kapı, iş parçacığı havuzu doyduğunda <c>HttpClient.Timeout</c>'a
/// (100 sn) kadar asılı kalıp <i>düzeltmeli durumda</i> yanlış kırmızı üretiyordu.
/// Tohumlama sayısı ölçülen eşik olan 5'tir: 2 eşzamanlı istek geçer.</para>
///
/// <para>Düzeltme geri alınırsa burası <b>asılmaz</b>, <see cref="Deadline"/> sonunda
/// başarısız olur — CI kilitlenmez.</para>
/// </summary>
public sealed class OpenAnimeRequestSemaphoreTests
{
    /// <summary>Ölçülen kilitlenme eşiği: istek semaforunun kapasitesi (5).</summary>
    private const int Concurrent = 5;

    private static readonly TimeSpan GateGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Deadline  = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SearchAsync_WhenEveryRequestIsUnauthorized_DoesNotDeadlockOnRetry()
    {
        using var handler    = new AlwaysUnauthorizedHandler(Concurrent);
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAnimeProvider(new StubBridge(httpClient));

        var searches = Enumerable.Range(0, Concurrent)
                                .Select(_ => Task.Run(
                                    () => provider.SearchAsync("naruto",
                                                               TestContext.Current.CancellationToken)))
                                .ToArray();

        await AssertCompletes(searches, handler);

        // 401 ikinci denemede de gelir; isRetry=true olduğu için her istek en fazla
        // iki deneme yapar ve istisna fırlatmadan boş liste döner.
        foreach (var search in searches)
        {
            Assert.Empty(await search);
        }
    }

    [Fact]
    public async Task SearchAsync_BelowPermitCapacity_AlsoCompletes()
    {
        using var handler    = new AlwaysUnauthorizedHandler(2);
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAnimeProvider(new StubBridge(httpClient));

        var searches = Enumerable.Range(0, 2)
                                .Select(_ => Task.Run(
                                    () => provider.SearchAsync("one piece",
                                                               TestContext.Current.CancellationToken)))
                                .ToArray();

        await AssertCompletes(searches, handler);
        Assert.Empty(await searches[0]);
        Assert.Empty(await searches[1]);
    }

    private static async Task AssertCompletes(Task<List<SearchResult>>[] searches,
                                             AlwaysUnauthorizedHandler handler)
    {
        var all    = Task.WhenAll(searches);
        var winner = await Task.WhenAny(all, Task.Delay(Deadline, TestContext.Current.CancellationToken));

        if (winner == all)
        {
            await all;

            return;
        }

        // Düzeltme geri alınmışsa buraya düşer: Concurrent isteğin hepsi 2. permiti
        // bekliyor, hiçbiri `finally`'ye ulaşamıyor, dolayısıyla permit geri dönmüyor.
        Assert.Fail(
            $"{Concurrent} eşzamanlı 401 isteği {Deadline.TotalSeconds:F0} saniyede tamamlanmadı. " +
            $"[teşhis: 401 isteği={handler.UnauthorizedCount}, oturum isteği={handler.SessionCount}, " +
            $"tamamlanan={searches.Count(s => s.IsCompleted)}] " +
            "Yeniden deneme, semafor permiti serbest bırakılmadan yapılıyor (yeniden giriş kilitlenmesi).");
    }

    /// <summary>
    /// <c>/session/init</c> dışındaki her isteğe 401 döner. Token akışı
    /// <c>EnsureTokenAsync</c> içinde yutulduğu için (istisnayı yakalayıp loglar)
    /// test yalnızca yeniden deneme yolunu ölçer.
    /// </summary>
    private sealed class AlwaysUnauthorizedHandler(int requiredArrivals) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;
        private int _unauthorized;
        private int _session;

        public int UnauthorizedCount => Volatile.Read(ref _unauthorized);

        public int SessionCount      => Volatile.Read(ref _session);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                      CancellationToken cancellationToken)
        {
            // Oturum kurulumuna geçerli yanıt ver: aksi halde her istek kendi yeniden
            // denemesinde yeni imza üretir ve 401 sayacı hedefiyle ilgisizleşir.
            if (request.RequestUri!.AbsolutePath.EndsWith("/session/init", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _session);

                return Json("""{"sessionId":"header.eyJleHAiOjk5OTk5OTk5OTl9.sig"}""",
                            HttpStatusCode.OK);
            }

            Interlocked.Increment(ref _unauthorized);

            // İlk dalgayı birlikte döndür: hepsi 401'i aynı anda görüp aynı anda
            // yeniden deneme yoluna girsin. Tohumlanamazsa süre dolunca yine de
            // serbest bırakılır — test asla kendi kuyruğunda beklemez.
            if (Interlocked.Increment(ref _arrived) >= requiredArrivals)
            {
                _gate.TrySetResult();
            }

            await Task.WhenAny(_gate.Task, Task.Delay(GateGrace, cancellationToken));

            return Json("""{"message":"token expired"}""", HttpStatusCode.Unauthorized);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode statusCode) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class StubBridge(HttpClient client) : ISharedBridge
    {
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();

        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null) => client;

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure) => client;

        public ILogger<T> CreateLogger<T>() => NullLogger<T>.Instance;
    }
}
