using Migurdex.Cli.Configuration;
using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     FuzzyPrompt/TUI akışında dinamik metinlerin (anime, bölüm, fansub, kaynak, kullanıcı sorgusu)
///     Spectre markup güvenliği: `[`, `[/`, `]` içeren veriler escape edildiğinde üretilen
///     tüm prompt satırları ayrıştırılabilir (dengeli) olmalıdır.
/// </summary>
public sealed class TuiMarkupSafetyTests
{
    [Theory]
    [InlineData("[")]
    [InlineData("[/")]
    [InlineData("]")]
    [InlineData("[/]")]
    [InlineData("[grey]")]
    [InlineData("[SubsPlease]")]
    [InlineData("Re:Zero [TV] [/] 1080p")]
    [InlineData("[red]Sahte[/] etiket [")]
    [InlineData("")]
    public void EscapedDynamicText_ProducesParseableMarkup(string raw)
    {
        var markup = $"[grey]{Markup.Escape(raw)}[/]";

        AssertValidMarkup(markup);
    }

    [Fact]
    public void UnclosedTag_ThrowsUnbalancedMarkupStack()
    {
        // Kullanıcının aldığı hatanın kaynağı olan kalıp: iki [grey] açılışı, tek [/] kapanışı.
        var broken = $"[grey]{Markup.Escape("[Anime] Test")} › "
                     + $"[grey]{Markup.Escape("S1E5")}[/]";

        Assert.Throws<InvalidOperationException>(() => new Markup(broken));
    }

    [Theory]
    [InlineData("[Anime] Başlık [TV] [/red]")]
    [InlineData("[/")]
    [InlineData("[")]
    [InlineData("[SubsPlease] Re:Zero kara Hajimeru Isekai Seikatsu [TV] Çok Uzun Başlık Denemesi 123456789")]
    public void BuildSelectedSourceHeaders_BracketedAnimeTitles_ProduceParseableMarkup(string animeTitle)
    {
        var episode = new Episode { Id = "ep-5", Number = 5, Season = 1, Title = "[Final] Bölüm" };
        var source = new VideoSource
        {
            Group   = "[SubsPlease]",
            Hoster  = "[Uqload] Fan[/]host",
            Quality = "[1080p]",
            Type    = VideoType.Mp4
        };

        var headers = EpisodeSourcesView.BuildSelectedSourceHeaders(animeTitle, episode, source);

        Assert.Equal(2, headers.Count);
        foreach (var header in headers)
        {
            AssertValidMarkup(header);
        }
    }

    [Fact]
    public void FormatSources_BracketedFansubData_ProducesParseableChoiceMarkup()
    {
        var sources = new List<VideoSource>
        {
            new()
            {
                Group   = "[SubsPlease]",
                Hoster  = "[Uqload] Fan[/]host",
                Quality = "[1080p]",
                Type    = VideoType.Mp4
            },
            new()
            {
                Group   = null,
                Hoster  = null,
                Quality = "",
                Type    = VideoType.M3U8
            }
        };

        var choices = EpisodeSourcesView.FormatSources(sources, new CliConfig());

        Assert.Equal(2, choices.Count);
        foreach (var choice in choices)
        {
            AssertValidMarkup(choice.Display);
            AssertValidMarkup(choice.DisplayActive);
        }
    }

    [Theory]
    [InlineData("[SubsPlease]", 0)]
    [InlineData("[SubsPlease]", 11)]
    [InlineData("[/] filtre", 0)]
    [InlineData("anime [TV]", 6)]
    [InlineData("", 0)]
    public void FormatQueryWithCursor_BracketedQuery_ProducesParseableMarkup(string query, int cursorIndex)
    {
        var markup = FuzzyPrompt.FormatQueryWithCursor(query, cursorIndex);

        AssertValidMarkup($"[grey]Ara:[/] {markup}");
    }

    [Fact]
    public void FooterMarkup_BracketedCustomHelp_ProducesParseableMarkup()
    {
        AssertValidMarkup(FuzzyPrompt.FooterMarkup(2, 5, "Enter: seç [/] Esc: [geri]"));
        AssertValidMarkup(FuzzyPrompt.FooterMarkup(0, 1));
        AssertValidMarkup(FuzzyPrompt.FooterMarkup(1, 9, null));
    }

    [Fact]
    public void HighlightDisplay_BracketedContentAndQuery_ProducesParseableMarkup()
    {
        var display = Theme.Item("[SubsPlease] Re:Zero [TV] 1080p");

        var highlighted = Theme.HighlightDisplay(display, "[subs 1080");

        AssertValidMarkup($"  {highlighted}");
    }

    [Fact]
    public void EscapedLiteralBrackets_AroundHosterAndQuality_ProduceParseableMarkup()
    {
        // NonInteractiveCommand oynatma satırındaki [hoster / quality] kalıbı:
        // literal köşeli parantezler [[ ]] ile kaçırılmalı, iç veri Markup.Escape ile.
        var hoster  = "[Uqload] Fan[/]host";
        var quality = "[1080p]";

        var markup = $"[grey][[{Markup.Escape(hoster)} / {Markup.Escape(quality)}]][/]";

        AssertValidMarkup(markup);
    }

    private static void AssertValidMarkup(string markup)
    {
        var error = Record.Exception(() => _ = new Markup(markup));

        Assert.True(error is null, $"Markup ayrıştırılamadı: '{markup}' → {error?.Message}");
    }
}
