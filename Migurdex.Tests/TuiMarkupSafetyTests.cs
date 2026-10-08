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

    [Fact]
    public void FormatSources_AlignsFirstSeparator()
    {
        var sources = new List<VideoSource>
        {
            new()
            {
                Group = "A",
                Hoster = "LongHoster",
                Quality = "1080p",
                Bitrate = 6_000_000,
                VideoCodec = "H.264",
                Type = VideoType.Mp4
            },
            new()
            {
                Group = "LongGroupName",
                Hoster = "B",
                Quality = "480p",
                Type = VideoType.M3U8
            }
        };

        var choices = EpisodeSourcesView.FormatSources(sources, new CliConfig());
        var firstSeparators = choices.Select(c => Markup.Remove(c.Display).IndexOf('•')).Distinct().ToList();

        Assert.Single(firstSeparators);
    }

    [Theory]
    [InlineData("1080p", "green")]
    [InlineData("2160p", "green")]
    [InlineData("720p", "yellow")]
    [InlineData("480p", "grey")]
    [InlineData("360p", "grey")]
    [InlineData("Auto", "grey")]
    [InlineData("", "red")]
    [InlineData(null, "red")]
    public void QualityColor_Maps(string? quality, string expected)
    {
        Assert.Equal(expected, EpisodeSourcesView.QualityColor(quality));
    }

    [Theory]
    [InlineData(VideoType.M3U8, "blue")]
    [InlineData(VideoType.Mp4, "green")]
    [InlineData(VideoType.Embed, "grey")]
    [InlineData(VideoType.Unknown, "grey")]
    public void FormatColor_Maps(VideoType type, string expected)
    {
        Assert.Equal(expected, EpisodeSourcesView.FormatColor(type));
    }
}
