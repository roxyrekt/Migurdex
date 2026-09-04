using Migurdex.Shared.Enums;
using System.Text.RegularExpressions;

namespace Migurdex.Shared.Infrastructure;

public static partial class RustHttpOptions
{
    public static readonly HttpRequestOptionsKey<bool>   NoFollowKey       = new("Rust_NoFollow");
    public static readonly HttpRequestOptionsKey<bool>   SkipCertVerifyKey = new("Rust_SkipCertVerify");
    public static readonly HttpRequestOptionsKey<string> EmulationKey      = new("Rust_Emulation");

    extension(HttpRequestMessage request)
    {
        public HttpRequestMessage WithNoFollow(bool noFollow = true)
        {
            request.Options.Set(NoFollowKey, noFollow);
            return request;
        }

        public HttpRequestMessage WithSkipCertVerify(bool skip = true)
        {
            request.Options.Set(SkipCertVerifyKey, skip);
            return request;
        }

        public HttpRequestMessage WithEmulation(BrowserEmulation emulation)
        {
            request.Options.Set(EmulationKey, emulation.ToRustString());
            return request;
        }

        public HttpRequestMessage WithEmulation(string emulation)
        {
            request.Options.Set(EmulationKey, emulation);
            return request;
        }
    }

    private static string ToRustString(this BrowserEmulation emulation)
    {
        return emulation switch
        {
            BrowserEmulation.SafariIos17_2   => "safari_ios_17.2",
            BrowserEmulation.SafariIos17_4_1 => "safari_ios_17.4.1",
            BrowserEmulation.SafariIos16_5   => "safari_ios_16.5",
            BrowserEmulation.Safari15_3      => "safari_15.3",
            BrowserEmulation.Safari15_5      => "safari_15.5",
            BrowserEmulation.Safari15_6_1    => "safari_15.6.1",
            BrowserEmulation.Safari17_0      => "safari_17.0",
            BrowserEmulation.Safari17_2_1    => "safari_17.2.1",
            BrowserEmulation.Safari17_4_1    => "safari_17.4.1",
            BrowserEmulation.Safari17_5      => "safari_17.5",
            BrowserEmulation.Safari17_6      => "safari_17.6",
            BrowserEmulation.SafariIPad18    => "safari_ipad_18",
            BrowserEmulation.Safari18_2      => "safari_18.2",
            BrowserEmulation.SafariIos18_1_1 => "safari_ios_18.1.1",
            BrowserEmulation.Safari18_3      => "safari_18.3",
            BrowserEmulation.Safari18_3_1    => "safari_18.3.1",
            BrowserEmulation.Safari18_5      => "safari_18.5",
            BrowserEmulation.Safari26_1      => "safari_26.1",
            BrowserEmulation.Safari26_2      => "safari_26.2",
            BrowserEmulation.SafariIPad26    => "safari_ipad_26",
            BrowserEmulation.SafariIpad26_2  => "safari_ipad_26.2",
            BrowserEmulation.SafariIos26     => "safari_ios_26",
            BrowserEmulation.SafariIos26_2   => "safari_ios_26.2",
            BrowserEmulation.Safari16_5      => "safari_16.5",

            BrowserEmulation.OkHttp3_9  => "okhttp_3.9",
            BrowserEmulation.OkHttp3_11 => "okhttp_3.11",
            BrowserEmulation.OkHttp3_13 => "okhttp_3.13",
            BrowserEmulation.OkHttp3_14 => "okhttp_3.14",
            BrowserEmulation.OkHttp4_9  => "okhttp_4.9",
            BrowserEmulation.OkHttp4_10 => "okhttp_4.10",
            BrowserEmulation.OkHttp4_12 => "okhttp_4.12",
            BrowserEmulation.OkHttp5    => "okhttp_5",

            BrowserEmulation.FirefoxPrivate135 => "firefox_private_135",
            BrowserEmulation.FirefoxAndroid135 => "firefox_android_135",
            BrowserEmulation.FirefoxPrivate136 => "firefox_private_136",

            _ => ConvertCamelCaseToSnake(emulation.ToString())
        };
    }

    [GeneratedRegex(@"(\p{Ll}|\p{Lu})(\d+)")]
    private static partial Regex CamelCaseSnakeRegex();

    private static string ConvertCamelCaseToSnake(string input)
    {
        // Chrome147 -> chrome_147, Edge131 -> edge_131
        return CamelCaseSnakeRegex().Replace(input, "$1_$2").ToLowerInvariant();
    }
}
