using Migurdex.Core.Services;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 51 koruma testleri. Kırılma oranı 1/14 olduğu için kırılmayı kovalayan bir
/// test yerine <b>korumanın kendisini</b> sınayan belirleyici testler yazıldı:
/// sabit porta dönülürse bunlar kesin kırmızı olur, kırılma oranına bağlı değildir.
/// </summary>
public sealed class FreeLoopbackPortTests
{
    [Fact]
    public void Next_ReturnsAPortThatCanActuallyBeBound()
    {
        var port = FreeLoopbackPort.Next();

        var listener = new TcpListener(IPAddress.Loopback, port);
        var exception = Record.Exception(() => listener.Start());
        listener.Stop();

        Assert.Null(exception);
        Assert.InRange(port, 1, 65535);
    }

    [Fact]
    public void Next_ReturnsADifferentPortOnEveryCall()
    {
        // Sabit port dönerse bu 5 elemanlı küme 1 eleman olur.
        var ports = Enumerable.Range(0, 5)
                             .Select(_ => FreeLoopbackPort.Next())
                             .ToHashSet();

        Assert.Equal(5, ports.Count);
    }

    [Fact]
    public void Next_NeverReturnsOneOfThePreviouslyHardcodedPorts()
    {
        // Bu, PR'ın düzelttiği şeyin doğrudan regresyon koruması:
        // 46497 / 46498 / 46499'a dönülürse burası kesin kırmızı olur.
        Assert.NotEmpty(FreeLoopbackPort.EskiSabitPortlar);

        for (var i = 0; i < 50; i++)
        {
            var port = FreeLoopbackPort.Next();

            Assert.DoesNotContain(port, FreeLoopbackPort.EskiSabitPortlar);
        }
    }

    [Fact]
    public void EskiSabitPortlar_AreTheThreePortsThatWereHardcoded()
    {
        // Liste yanlışlıkla değiştirilirse önceki test boşa düşerdi; bu onu sabitler.
        Assert.Equal([46497, 46498, 46499], FreeLoopbackPort.EskiSabitPortlar.OrderBy(p => p));
    }

    [Fact]
    public async Task LoopbackReceiver_WorksOnADynamicallyAllocatedPort()
    {
        // Yardımcının ürettiği portun gerçekten kullanılabilir olduğunun uçtan uca
        // kanıtı: dinleyici açılır, istek alınır, kod döner, port serbest bırakılır.
        var port = FreeLoopbackPort.Next();

        var waitTask = LoopbackCodeReceiver.WaitForCodeAsync(port,
                                                           "/callback",
                                                           TimeSpan.FromSeconds(10),
                                                           cancellationToken: TestContext.Current.CancellationToken);

        using var http     = new HttpClient();
        var       response = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=dinamik-1",
                                                  TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("dinamik-1", await waitTask);
    }
}
