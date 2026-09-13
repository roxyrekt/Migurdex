using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace Migurdex.Core.Services;

public static class LoopbackCodeReceiver
{
    private const string ClosePageHtml =
        "<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Migurdex bağlandı</title></head>" +
        "<body style=\"background:#1a1b26;color:#c0caf5;font-family:sans-serif;" +
        "display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0\">" +
        "<div style=\"text-align:center\">" +
        "<div style=\"font-size:48px\">&#10003;</div>" +
        "<h2 style=\"margin:8px 0\">Migurdex bağlandı</h2>" +
        "<p style=\"color:#9aa5ce\">Bu sekme otomatik kapanacak. Kapanmazsa kapatıp uygulamaya dönebilirsiniz.</p>" +
        "</div>" +
        "<script>setTimeout(function(){window.close()},2500);</script>" +
        "</body></html>";

    public static async Task<string?> WaitForCodeAsync(int               port,
        string                                                          path,
        TimeSpan                                                        timeout,
        ILogger?                                                        logger = null,
        CancellationToken                                               cancellationToken = default)
    {
        logger ??= NullLogger.Instance;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}{path}/");
        listener.Prefixes.Add($"http://localhost:{port}{path}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Loopback dinleyici {Port} portunda açılamadı", port);
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                var contextTask = listener.GetContextAsync();
                var completed   = await Task.WhenAny(contextTask,
                                                     Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token));
                if (completed != contextTask)
                {
                    return null;
                }

                var context = await contextTask;
                try
                {
                    var code  = context.Request.QueryString["code"];
                    var error = context.Request.QueryString["error"];

                    var body = Encoding.UTF8.GetBytes(ClosePageHtml);
                    context.Response.ContentType     = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, linkedCts.Token);
                    context.Response.Close();

                    if (!string.IsNullOrEmpty(error))
                    {
                        logger.LogWarning("OAuth yetkilendirme reddedildi: {Error}", error);
                        return null;
                    }

                    if (!string.IsNullOrEmpty(code))
                    {
                        return code;
                    }
                }
                catch (Exception ex) when (!linkedCts.Token.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Loopback callback işlenemedi");
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }
}
