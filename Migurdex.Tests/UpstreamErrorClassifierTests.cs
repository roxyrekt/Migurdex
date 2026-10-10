using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using Xunit;

namespace Migurdex.Tests;

public sealed class UpstreamErrorClassifierTests
{
    [Theory]
    [InlineData(200, "{\"error\":{\"title\":\"Özel içerik.\",\"message\":\"sahibi tarafından özel olarak etiketlendi\"}}", UpstreamErrorKind.Private)]
    [InlineData(200, "<title>Google Drive - Quota exceeded</title>", UpstreamErrorKind.QuotaExceeded)]
    [InlineData(429, "{\"error\":{\"code\":429}}", UpstreamErrorKind.RateLimited)]
    [InlineData(429, "", UpstreamErrorKind.RateLimited)]
    [InlineData(404, "<html>not found</html>", UpstreamErrorKind.Deleted)]
    [InlineData(404, "", UpstreamErrorKind.NotFound)]
    [InlineData(410, "", UpstreamErrorKind.Deleted)]
    [InlineData(403, "", UpstreamErrorKind.Unknown)]
    [InlineData(200, "this video has been deleted", UpstreamErrorKind.Deleted)]
    [InlineData(200, "Bu video silinmiş", UpstreamErrorKind.Deleted)]
    [InlineData(200, "{\"noAd\":{\"reason\":\"embed|blocklist\"}}", UpstreamErrorKind.EmbedBlocked)]
    [InlineData(200, "removed due to copyright claim", UpstreamErrorKind.CopyrightBlocked)]
    [InlineData(403, "<title>Just a moment...</title><form id=\"challenge-form\"", UpstreamErrorKind.CloudflareBlocked)]
    [InlineData(403, "checking your browser before you access the site", UpstreamErrorKind.CloudflareBlocked)]
    [InlineData(200, "Видео заблокировано из-за нарушений авторских прав", UpstreamErrorKind.CopyrightBlocked)]
    [InlineData(200, "File was deleted", UpstreamErrorKind.Deleted)]
    [InlineData(200, "File is no longer available as it expired or has been deleted.", UpstreamErrorKind.Deleted)]
    [InlineData(200, "Видео удалено", UpstreamErrorKind.Deleted)]
    [InlineData(200, "The author of this video has not been found or is blocked", UpstreamErrorKind.Deleted)]
    [InlineData(200, "This video was removed at the request of the copyright holder", UpstreamErrorKind.CopyrightBlocked)]
    [InlineData(200, "{\"error\":{\"title\":\"Publishing in progress...\",\"code\":\"DM006\"}}", UpstreamErrorKind.Processing)]
    [InlineData(200, "<div class=\"copyright\">&copy; mp4upload</div><h4>File Not Found</h4>", UpstreamErrorKind.Deleted)]
    [InlineData(200, "store everything privately and securely", UpstreamErrorKind.Unknown)]
    [InlineData(200, "<html><body>normal player page</body></html>", UpstreamErrorKind.Unknown)]
    [InlineData(200, null, UpstreamErrorKind.Unknown)]
    public void Detect_ClassifiesCorrectly(int status, string? body, UpstreamErrorKind expected)
    {
        Assert.Equal(expected, UpstreamErrorClassifier.Detect(status, body));
    }

    [Fact]
    public void Summarize_CollapsesToSingleShortLine()
    {
        var summary = UpstreamErrorClassifier.Summarize("<title>\n  Google Drive  -\n  Quota exceeded</title>");

        Assert.DoesNotContain('\n', summary);
        Assert.Contains("Quota exceeded", summary);
        Assert.True(summary.Length <= 201);
    }
}
