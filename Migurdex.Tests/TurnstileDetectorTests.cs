using Migurdex.Core.Services.Turnstile;
using Xunit;

namespace Migurdex.Tests;

public sealed class TurnstileDetectorTests
{
    [Theory]
    [InlineData("https://ornek-saglayici.com/anime/123", "ornek-saglayici.com")]
    [InlineData("http://ALT.ornek.COM:8080/yol", "alt.ornek.com")]
    [InlineData("ornek-saglayici.com", "ornek-saglayici.com")]
    [InlineData("", "")]
    public void NormalizeHost_LowercasesAndStripsUrl(string input, string expected)
    {
        Assert.Equal(expected, TurnstileDetector.NormalizeHost(input));
    }
}
