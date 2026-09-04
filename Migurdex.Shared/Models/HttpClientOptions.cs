using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public class HttpClientOptions
{
    public bool                                          UseCookies        { get; set; }
    public bool                                          AllowAutoRedirect { get; set; }
    public bool                                          SkipCertVerify    { get; set; } = false;
    public BrowserEmulation?                             Emulation         { get; set; }
    public Func<HttpMessageHandler, HttpMessageHandler>? ConfigureHandler  { get; set; }
}
