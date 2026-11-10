using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Diagnostics;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

public enum DownloadFilterTab
{
    All            = 0,
    Active         = 1,
    Completed      = 2,
    PausedOrFailed = 3
}

public class DownloadsView : BaseView
{
    private readonly IDownloadQueueService _queueService;
    private readonly IServiceProvider      _serviceProvider;
    private          DownloadFilterTab     _currentTab = DownloadFilterTab.All;
    private          int                   _cursorIndex;
    private          bool                  _confirmClearAll;
    private          string?               _statusMessage;
    private          DateTime              _statusMessageExpiryUtc;

    public DownloadsView(IDownloadQueueService queueService, IServiceProvider serviceProvider)
    {
        _queueService    = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public override string GetRpcState()
    {
        return "İndirmeler";
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        while (!TuiConsole.IsAppExitRequested)
        {
            LiveMenuItem? pendingModalItem = null;
            var           exitView         = false;

            await LiveMenu.RunAsync(
                buildFrame: BuildCurrentFrame,
                onKey: (ConsoleKeyInfo key, LiveMenuFrame frame, ref int cursor) =>
                {
                    if (_confirmClearAll)
                    {
                        if (key.Key is ConsoleKey.Y or ConsoleKey.E)
                        {
                            var totalCleared = _queueService.ClearAll(deletePartialFiles: false);
                            SetStatusMessage($"[yellow]{totalCleared} indirme kaydı temizlendi.[/]", 2.0);
                            _confirmClearAll = false;
                            cursor           = 0;
                            _cursorIndex     = 0;
                        }
                        else if (key.Key is ConsoleKey.N or ConsoleKey.H or ConsoleKey.Escape or ConsoleKey.Enter)
                        {
                            _confirmClearAll = false;
                            SetStatusMessage("[grey]Vazgeçildi.[/]", 1.5);
                        }

                        return LiveKeyResult.Handled;
                    }

                    var selectedItem = cursor >= 0 && cursor < frame.Items.Count ? frame.Items[cursor] : null;
                    var selectedSnap = selectedItem?.Tag as DownloadJobSnapshot;
                    var selectedJob  = selectedSnap?.Job;

                    switch (key.Key)
                    {
                        case ConsoleKey.Tab:
                            _currentTab  = NextTab(_currentTab, (key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1);
                            cursor       = 0;
                            _cursorIndex = 0;
                            return LiveKeyResult.Handled;

                        case ConsoleKey.D1:
                        case ConsoleKey.NumPad1:
                            _currentTab  = DownloadFilterTab.All;
                            cursor       = 0;
                            _cursorIndex = 0;
                            return LiveKeyResult.Handled;

                        case ConsoleKey.D2:
                        case ConsoleKey.NumPad2:
                            _currentTab  = DownloadFilterTab.Active;
                            cursor       = 0;
                            _cursorIndex = 0;
                            return LiveKeyResult.Handled;

                        case ConsoleKey.D3:
                        case ConsoleKey.NumPad3:
                            _currentTab  = DownloadFilterTab.Completed;
                            cursor       = 0;
                            _cursorIndex = 0;
                            return LiveKeyResult.Handled;

                        case ConsoleKey.D4:
                        case ConsoleKey.NumPad4:
                            _currentTab  = DownloadFilterTab.PausedOrFailed;
                            cursor       = 0;
                            _cursorIndex = 0;
                            return LiveKeyResult.Handled;

                        case ConsoleKey.Spacebar:
                        case ConsoleKey.P:
                            if (selectedJob is not null)
                            {
                                TogglePauseResume(selectedJob);
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.O:
                            if (selectedJob is not null && HasPlayableMedia(selectedJob))
                            {
                                _cursorIndex = cursor;
                                pendingModalItem = new LiveMenuItem
                                {
                                    Id  = "PlayJob",
                                    Tag = selectedSnap
                                };
                                return LiveKeyResult.Exit;
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.F:
                            if (selectedJob is not null)
                            {
                                OpenFileLocation(selectedJob);
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.OemPlus:
                        case ConsoleKey.Add:
                        case ConsoleKey.K:
                            if (selectedJob is not null && selectedJob.Status.IsActiveOrPending())
                            {
                                _queueService.BoostPriority(selectedJob.Id);
                                SetStatusMessage(
                                    $"[green]✓[/] {Markup.Escape(selectedJob.AnimeTitle)} S{selectedJob.Season}E{selectedJob.EpisodeNumber} başa alındı.",
                                    2.0);
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.R:
                            if (selectedJob is not null
                                && selectedJob.Status is DownloadJobStatus.Failed
                                                         or DownloadJobStatus.Cancelled
                                                         or DownloadJobStatus.Paused)
                            {
                                _queueService.Retry(selectedJob.Id, refreshSource: true);
                                SetStatusMessage(
                                    $"[green]✓[/] {Markup.Escape(selectedJob.AnimeTitle)} yeniden başlatılıyor.",
                                    2.0);
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.Delete:
                        case ConsoleKey.X:
                            if (selectedJob is not null)
                            {
                                _queueService.Remove(
                                    selectedJob.Id,
                                    deletePartialFiles: selectedJob.Status != DownloadJobStatus.Completed);
                                SetStatusMessage(
                                    $"[grey]'{Markup.Escape(Theme.Ellipsize(selectedJob.AnimeTitle, 20))}' kaldırıldı.[/]",
                                    1.5);
                                cursor       = Math.Max(0, cursor - 1);
                                _cursorIndex = cursor;
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.C:
                            var cleared = _queueService.ClearCompleted();
                            if (cleared > 0)
                            {
                                SetStatusMessage($"[grey]{cleared} tamamlanan kayıt temizlendi.[/]", 1.5);
                                cursor       = Math.Max(0, cursor - 1);
                                _cursorIndex = cursor;
                            }

                            return LiveKeyResult.Handled;

                        case ConsoleKey.Enter:
                            _cursorIndex = cursor;
                            if (selectedItem is null)
                            {
                                return LiveKeyResult.Handled;
                            }

                            if (selectedItem.Id == "Back")
                            {
                                exitView = true;
                                return LiveKeyResult.Exit;
                            }

                            if (selectedItem.IsAction)
                            {
                                HandleInlineAction(selectedItem.Id, ref cursor);
                                _cursorIndex = cursor;
                                return LiveKeyResult.Handled;
                            }

                            pendingModalItem = selectedItem;
                            return LiveKeyResult.Exit;

                        case ConsoleKey.Escape:
                            exitView = true;
                            return LiveKeyResult.Exit;

                        default:
                            return LiveKeyResult.Unhandled;
                    }
                },
                initialCursorIndex: _cursorIndex);

            if (exitView || TuiConsole.IsAppExitRequested)
            {
                navigator.Pop();
                return;
            }

            if (pendingModalItem is not null)
            {
                if (pendingModalItem.Id == "PlayJob" && pendingModalItem.Tag is DownloadJobSnapshot playSnap)
                {
                    await PlayLocalJobAsync(playSnap.Job);
                    continue;
                }

                if (pendingModalItem.Tag is DownloadJobSnapshot jobSnap)
                {
                    var navigatedAway = await ShowJobActionMenuAsync(navigator, jobSnap);
                    if (navigatedAway)
                    {
                        return;
                    }
                }
            }
        }
    }

    private LiveMenuFrame BuildCurrentFrame()
    {
        var allSnapshots = _queueService.GetSnapshots();
        var summary      = _queueService.GetSummary();
        var filtered     = FilterSnapshots(allSnapshots, _currentTab);
        var items        = BuildMenuItems(filtered, summary);

        var emptyHint = _currentTab switch
        {
            DownloadFilterTab.Active => "[grey]  Bu sekmede aktif indirme veya bekleyen iş yok.[/]",
            DownloadFilterTab.Completed => "[grey]  Henüz tamamlanan indirme yok.[/]",
            DownloadFilterTab.PausedOrFailed => "[grey]  Duraklatılan veya hatalı indirme yok.[/]",
            _ => "[grey]  Henüz kuyrukta indirme yok. Bir bölümde 'İndir' seçerek kuyruğa ekleyebilirsiniz.[/]"
        };

        var prompt = _confirmClearAll
                         ? "[red]Tüm indirme kuyruğu sıfırlansın ve kayıtlar silinsin mi?[/] [bold white][[y/n]] (n):[/]"
                         : null;

        return new LiveMenuFrame
        {
            Title = "İndirmeler",
            HeaderLines =
            [
                BuildSummaryBanner(summary, GetActiveStatusMessage()),
                string.Empty,
                BuildTabHeader(summary, _currentTab, allSnapshots.Count)
            ],
            Items        = items,
            EmptyMarkup  = emptyHint,
            PromptMarkup = prompt,
            FooterHelp =
                "Tab/1-4 sekme • O oynat • Space/P duraklat/başlat • F klasör • + başa al • Del kaldır • Esc geri"
        };
    }

    private void HandleInlineAction(string actionId, ref int cursor)
    {
        switch (actionId)
        {
            case "PauseAll":
                var paused = _queueService.PauseAll();
                if (paused > 0)
                {
                    SetStatusMessage($"[yellow]{paused} indirme duraklatıldı.[/]");
                }

                break;

            case "ResumeAll":
                var resumed = _queueService.ResumeAll();
                if (resumed > 0)
                {
                    SetStatusMessage($"[green]{resumed} indirme devam ettiriliyor.[/]");
                }

                break;

            case "ClearCompleted":
                var cleared = _queueService.ClearCompleted();
                if (cleared > 0)
                {
                    SetStatusMessage($"[grey]{cleared} tamamlanan kayıt temizlendi.[/]");
                    cursor = Math.Max(0, cursor - 1);
                }

                break;

            case "ClearFailed":
                var failedCleared = _queueService.ClearFailed();
                if (failedCleared > 0)
                {
                    SetStatusMessage($"[grey]{failedCleared} hatalı/iptal edilen kayıt temizlendi.[/]");
                    cursor = Math.Max(0, cursor - 1);
                }

                break;

            case "ClearAll":
                _confirmClearAll = true;
                break;
        }
    }

    private void SetStatusMessage(string markup, double seconds = 2.0)
    {
        _statusMessage          = markup;
        _statusMessageExpiryUtc = DateTime.UtcNow.AddSeconds(seconds);
    }

    private string? GetActiveStatusMessage()
    {
        if (_statusMessage is not null && DateTime.UtcNow < _statusMessageExpiryUtc)
        {
            return _statusMessage;
        }

        _statusMessage = null;
        return null;
    }

    private void TogglePauseResume(DownloadJobEntry job)
    {
        if (job.Status.IsActiveOrPending())
        {
            _queueService.Pause(job.Id);
            SetStatusMessage($"[yellow]⏸ {Markup.Escape(job.AnimeTitle)} duraklatıldı.[/]");
        }
        else if (job.Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
        {
            _queueService.Resume(job.Id);
            SetStatusMessage($"[green]▶ {Markup.Escape(job.AnimeTitle)} devam ettiriliyor.[/]");
        }
    }

    private async Task<bool> ShowJobActionMenuAsync(ITuiNavigator navigator, DownloadJobSnapshot snapshot)
    {
        var job   = snapshot.Job;
        var epRef = FormatEpisodeCode(job.Season, job.EpisodeNumber);
        var headers = new List<string>
        {
            $"[white]{Markup.Escape(job.AnimeTitle)}[/] [grey]•[/] [cyan]{Markup.Escape(epRef)}[/] [grey]({Markup.Escape(job.ProviderName)})[/]",
            $"[grey]Durum:[/] {FormatStatusBadge(job.Status)}  [grey]•[/]  {FormatProgressSummary(snapshot)}"
        };

        if (!string.IsNullOrWhiteSpace(job.MediaPath))
        {
            headers.Add($"[grey]Dosya:[/] [white]{Markup.Escape(job.MediaPath)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            headers.Add($"[red]Hata:[/] [grey]{Markup.Escape(job.ErrorMessage)}[/]");
        }

        foreach (var warning in job.Warnings)
        {
            headers.Add($"[yellow]Uyarı:[/] [grey]{Markup.Escape(warning)}[/]");
        }

        var choices = new List<FuzzyChoice>();

        if (HasPlayableMedia(job))
        {
            choices.Add(Theme.PlayChoice("MPV ile Oynat (Hemen İzle)"));
        }

        if (job.Status.IsActiveOrPending())
        {
            choices.Add(Theme.ActionChoice("Duraklat", Theme.Warning));
            choices.Add(Theme.ActionChoice("Önceliği Artır (Başa Al)", Theme.Primary));
            choices.Add(Theme.ActionChoice("İptal Et ve Parçaları Sil", Theme.Danger));
        }
        else if (job.Status == DownloadJobStatus.Paused)
        {
            choices.Add(Theme.PlayChoice("Devam Et"));
            choices.Add(Theme.ActionChoice("Önceliği Artır (Başa Al)", Theme.Primary));
            choices.Add(Theme.ActionChoice("Kaynağı Yenileyerek Devam Et", Theme.Primary));
            choices.Add(Theme.ActionChoice("İptal Et ve Parçaları Sil", Theme.Danger));
        }
        else if (job.Status is DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
        {
            choices.Add(Theme.PlayChoice("Yeniden Dene (Kaynağı Yenile)"));
            choices.Add(Theme.ActionChoice("Aynı Kaynakla Yeniden Dene", Theme.Primary));
        }
        else if (job.Status == DownloadJobStatus.Completed)
        {
            choices.Add(Theme.ActionChoice("Tekrar İndir (Üzerine Yaz)", Theme.Primary));
        }

        choices.Add(Theme.ActionChoice("Dosya Konumunu Aç (Klasörde Göster)", Theme.Text));
        choices.Add(Theme.ActionChoice("Anime Detaylarına Git", Theme.Text));
        choices.Add(Theme.ActionChoice("Akış ve Dosya Bilgisi", Theme.Text));
        choices.Add(Theme.ActionChoice("Listeden Kaldır", Theme.Muted));
        choices.Add(Theme.BackChoice("Geri"));

        var selected = FuzzyPrompt.Show($"{job.AnimeTitle} • {epRef}",
                                        choices,
                                        searchable: false,
                                        headerLines: headers,
                                        footerHelp: "↑↓ gez • Enter seç • Esc geri");

        if (selected is null || selected.Searchable == "Geri")
        {
            return false;
        }

        switch (selected.Searchable)
        {
            case "MPV ile Oynat (Hemen İzle)":
                await PlayLocalJobAsync(job);
                return false;

            case "Duraklat":
                _queueService.Pause(job.Id);
                SetStatusMessage($"[yellow]⏸ {Markup.Escape(job.AnimeTitle)} duraklatıldı.[/]");
                return false;

            case "Devam Et":
                _queueService.Resume(job.Id, refreshSource: false);
                SetStatusMessage($"[green]▶ {Markup.Escape(job.AnimeTitle)} devam ettiriliyor.[/]");
                return false;

            case "Önceliği Artır (Başa Al)":
                _queueService.BoostPriority(job.Id);
                SetStatusMessage($"[green]✓[/] {epRef} kuyruğun en başına taşındı.", 2.0);
                return false;

            case "Kaynağı Yenileyerek Devam Et":
            case "Yeniden Dene (Kaynağı Yenile)":
                _queueService.Retry(job.Id, refreshSource: true);
                SetStatusMessage($"[green]✓[/] {Markup.Escape(job.AnimeTitle)} yeniden başlatılıyor.", 2.0);
                return false;

            case "Aynı Kaynakla Yeniden Dene":
                _queueService.Retry(job.Id, refreshSource: false);
                SetStatusMessage($"[green]✓[/] {Markup.Escape(job.AnimeTitle)} yeniden başlatılıyor.", 2.0);
                return false;

            case "Dosya Konumunu Aç (Klasörde Göster)":
                OpenFileLocation(job);
                return false;

            case "Anime Detaylarına Git":
                return NavigateToAnimeDetails(navigator, job);

            case "Akış ve Dosya Bilgisi":
                ShowJobDetailsDialog(snapshot);
                return false;

            case "Tekrar İndir (Üzerine Yaz)":
                _queueService.Remove(job.Id, deletePartialFiles: false);
                _queueService.Enqueue(new DownloadJobRequest
                                      {
                                          ProviderName      = job.ProviderName,
                                          AnimeId           = job.AnimeId,
                                          AnimeTitle        = job.AnimeTitle,
                                          EpisodeId         = job.EpisodeId,
                                          EpisodeTitle      = job.EpisodeTitle,
                                          Season            = job.Season,
                                          EpisodeNumber     = job.EpisodeNumber,
                                          PreferredGroup    = job.PreferredGroup,
                                          Source            = job.Source,
                                          OutputDirectory   = job.OutputDirectory,
                                          Overwrite         = true,
                                          Resume            = true,
                                          DownloadSubtitles = job.DownloadSubtitles
                                      },
                                      out _);
                SetStatusMessage($"[green]✓[/] {epRef} tekrar indirme kuyruğuna eklendi.", 2.0);
                return false;

            case "İptal Et ve Parçaları Sil":
                _queueService.Cancel(job.Id, deletePartialFiles: true);
                SetStatusMessage($"[grey]{epRef} iptal edildi.[/]", 1.5);
                return false;

            case "Listeden Kaldır":
                _queueService.Remove(job.Id, deletePartialFiles: job.Status != DownloadJobStatus.Completed);
                SetStatusMessage($"[grey]{epRef} kaldırıldı.[/]", 1.5);
                return false;

            default:
                return false;
        }
    }

    private async Task PlayLocalJobAsync(DownloadJobEntry job)
    {
        if (string.IsNullOrWhiteSpace(job.MediaPath) || !File.Exists(job.MediaPath))
        {
            SetStatusMessage("[red]Oynatılacak medya dosyası bulunamadı.[/]");
            return;
        }

        var orchestrator = _serviceProvider.GetService(typeof(PlaybackOrchestrator)) as PlaybackOrchestrator;
        if (orchestrator is null)
        {
            SetStatusMessage("[red]Oynatıcı servisi kullanılamıyor.[/]");
            return;
        }

        var episode = new Episode
        {
            Id     = job.EpisodeId,
            Title  = job.EpisodeTitle,
            Season = job.Season,
            Number = job.EpisodeNumber
        };

        var historyEntry = orchestrator.BuildEntry(
            job.ProviderName,
            job.AnimeId,
            job.AnimeTitle,
            episode,
            posterUrl: null);

        var subtitles = job.SubtitlePaths
                           .Where(File.Exists)
                           .Select(p => new Subtitle
                           {
                               Url   = p,
                               Label = Path.GetFileName(p)
                           })
                           .ToList();

        var localSource = new VideoSource
        {
            Url       = job.MediaPath,
            Type      = VideoType.Mp4,
            Quality   = job.Source?.Quality ?? "Yerel",
            Hoster    = "Yerel Dosya",
            Group     = job.PreferredGroup ?? job.Source?.Group,
            Subtitles = subtitles.Count > 0 ? subtitles : null
        };

        await orchestrator.PlayAndNotifyAsync(localSource, historyEntry, showToast: false);
    }

    private static bool HasPlayableMedia(DownloadJobEntry job)
    {
        return job.Status == DownloadJobStatus.Completed
               && !string.IsNullOrWhiteSpace(job.MediaPath)
               && File.Exists(job.MediaPath);
    }

    private void OpenFileLocation(DownloadJobEntry job)
    {
        var targetPath = !string.IsNullOrWhiteSpace(job.MediaPath) && File.Exists(job.MediaPath)
                             ? job.MediaPath
                             : job.OutputDirectory;

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            SetStatusMessage("[yellow]Konum bilgisi bulunamadı.[/]");
            return;
        }

        try
        {
            var fullPath  = Path.GetFullPath(targetPath);
            var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory ?? ".");
            }

            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(fullPath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
                    {
                        UseShellExecute = true
                    });
                }

                SetStatusMessage("[green]✓[/] Dosya konumu açıldı.", 2.0);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                if (File.Exists(fullPath))
                {
                    Process.Start(new ProcessStartInfo("open", $"-R \"{fullPath}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("open", $"\"{directory}\"")
                    {
                        UseShellExecute = true
                    });
                }

                SetStatusMessage("[green]✓[/] Dosya konumu açıldı.", 2.0);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{directory}\"")
                {
                    UseShellExecute = true
                });
                SetStatusMessage("[green]✓[/] Dosya konumu açıldı.", 2.0);
                return;
            }
        }
        catch
        {
            SetStatusMessage("[red]Dosya yöneticisi açılamadı.[/]");
        }
    }

    private bool NavigateToAnimeDetails(ITuiNavigator navigator, DownloadJobEntry job)
    {
        if (string.IsNullOrWhiteSpace(job.ProviderName) || string.IsNullOrWhiteSpace(job.AnimeId))
        {
            SetStatusMessage("[yellow]Sağlayıcı veya anime kimliği eksik.[/]");
            return false;
        }

        var detailsView = _serviceProvider.GetService(typeof(AnimeDetailsView)) as AnimeDetailsView;
        if (detailsView is not null)
        {
            detailsView.SetTarget(job.ProviderName, job.AnimeId, initialPosterUrl: null, animeTitle: job.AnimeTitle);
            navigator.Push(detailsView);
            return true;
        }

        return false;
    }

    private static void ShowJobDetailsDialog(DownloadJobSnapshot snapshot)
    {
        var job   = snapshot.Job;
        var epRef = FormatEpisodeCode(job.Season, job.EpisodeNumber);
        var src   = job.Source;

        var groupText    = job.PreferredGroup ?? src?.Group ?? "Otomatik";
        var hosterText   = src?.Hoster ?? "Otomatik / Çözülmemiş";
        var formatColor  = EpisodeSourcesView.FormatColor(src?.Type ?? VideoType.Mp4);
        var qualityColor = EpisodeSourcesView.QualityColor(src?.Quality);
        var displayLabel = src?.DisplayLabel ?? job.PreferredFormat;
        var videoCodec   = src?.VideoCodec ?? "-";
        var audioCodec   = src?.AudioCodec ?? "-";

        var headers = new List<string>
        {
            $"[white]{Markup.Escape(job.AnimeTitle)}[/] [grey]•[/] [cyan]{Markup.Escape(epRef)}[/]",
            $"[grey]Sağlayıcı:[/] [white]{Markup.Escape(job.ProviderName)}[/]  [grey]• Fansub:[/] [silver]{Markup.Escape(groupText)}[/]",
            $"[grey]Sunucu (Hoster):[/] [cyan]{Markup.Escape(hosterText)}[/]",
            $"[grey]Format / Kalite:[/] [{formatColor}]{src?.Type}[/] • [{qualityColor}]{Markup.Escape(displayLabel)}[/]",
            $"[grey]Video Codec:[/] [white]{Markup.Escape(videoCodec)}[/]  [grey]• Ses Codec:[/] [white]{Markup.Escape(audioCodec)}[/]",
            $"[grey]Boyut:[/] {FormatBytes(job.BytesDownloaded)}"
            + (job.TotalBytes.HasValue ? $" / {FormatBytes(job.TotalBytes.Value)}" : "")
        };

        if (snapshot.SpeedBytesPerSecond > 0)
        {
            headers.Add($"[grey]Anlık Hız:[/] [green]{FormatBytes((long) snapshot.SpeedBytesPerSecond)}/s[/]");
        }

        if (snapshot.Eta is not null)
        {
            headers.Add($"[grey]Kalan Süre (ETA):[/] [white]{DownloadSpeedometer.FormatEta(snapshot.Eta.Value)}[/]");
        }

        headers.Add($"[grey]Kök Dizin:[/] [silver]{Markup.Escape(job.OutputDirectory)}[/]");
        if (!string.IsNullOrWhiteSpace(job.MediaPath))
        {
            headers.Add($"[grey]Medya Dosyası:[/] [white]{Markup.Escape(job.MediaPath)}[/]");
        }

        if (job.SubtitlePaths.Count > 0)
        {
            headers.Add($"[grey]Altyazılar ({job.SubtitlePaths.Count}):[/]");
            foreach (var sub in job.SubtitlePaths)
            {
                headers.Add($"  [silver]› {Markup.Escape(Path.GetFileName(sub))}[/]");
            }
        }

        if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            headers.Add($"[red]Son Hata:[/] {Markup.Escape(job.ErrorMessage)}");
        }

        FuzzyPrompt.Show("Akış ve Dosya Bilgisi",
                         [Theme.BackChoice("Geri")],
                         searchable: false,
                         headerLines: headers,
                         footerHelp: "Enter veya Esc geri döner");
    }

    private static IReadOnlyList<DownloadJobSnapshot> FilterSnapshots(
        IReadOnlyList<DownloadJobSnapshot> all,
        DownloadFilterTab                  tab)
    {
        return tab switch
        {
            DownloadFilterTab.Active    => all.Where(s => s.Job.Status.IsActiveOrPending()).ToList(),
            DownloadFilterTab.Completed => all.Where(s => s.Job.Status == DownloadJobStatus.Completed).ToList(),
            DownloadFilterTab.PausedOrFailed => all.Where(s => s.Job.Status is DownloadJobStatus.Paused
                                                                               or DownloadJobStatus.Failed
                                                                               or DownloadJobStatus.Cancelled)
                                                   .ToList(),
            _ => all
        };
    }

    private static DownloadFilterTab NextTab(DownloadFilterTab current, int direction)
    {
        const int count = 4;
        var       next  = ((int) current + direction + count) % count;
        return (DownloadFilterTab) next;
    }

    private static List<LiveMenuItem> BuildMenuItems(
        IReadOnlyList<DownloadJobSnapshot> snapshots,
        DownloadQueueSummary               summary)
    {
        var items = new List<LiveMenuItem>(snapshots.Count + 6);

        foreach (var snap in snapshots)
        {
            items.Add(new LiveMenuItem
            {
                Id            = snap.Job.Id,
                Display       = FormatJobRow(snap, isSelected: false),
                DisplayActive = FormatJobRow(snap, isSelected: true),
                Tag           = snap,
                IsAction      = false
            });
        }

        if (summary.ActiveCount > 0 || summary.QueuedCount > 0)
        {
            items.Add(BuildActionItem("PauseAll", "⏸ Tümünü Duraklat", Theme.Warning));
        }

        if (summary.PausedCount > 0 || summary.FailedCount > 0)
        {
            items.Add(BuildActionItem("ResumeAll", "▶ Duraklatılan / Başarısız Olanları Başlat", Theme.Success));
        }

        if (summary.CompletedCount > 0)
        {
            items.Add(BuildActionItem("ClearCompleted", "✓ Tamamlananları Temizle", Theme.Primary));
        }

        if (summary.FailedCount > 0 || summary.CancelledCount > 0)
        {
            items.Add(BuildActionItem("ClearFailed", "✗ Hatalı / İptal Edilenleri Temizle", Theme.Danger));
        }

        if (summary.TotalCount > 0)
        {
            items.Add(BuildActionItem("ClearAll", "🗑 Tümünü Temizle (Kuyruğu Sıfırla)", Theme.Danger));
        }

        items.Add(BuildActionItem("Back", "Geri", Theme.Danger));
        return items;
    }

    private static LiveMenuItem BuildActionItem(string id, string label, string color)
    {
        var safe = Markup.Escape(label);
        return new LiveMenuItem
        {
            Id            = id,
            Display       = $"  [{color}]{safe}[/]",
            DisplayActive = $"[bold white on grey23]› {safe} [/]",
            IsAction      = true
        };
    }

    internal static string BuildTabHeader(DownloadQueueSummary summary, DownloadFilterTab activeTab, int totalCount)
    {
        var activeCount       = summary.ActiveCount + summary.QueuedCount;
        var completedCount    = summary.CompletedCount;
        var pausedFailedCount = summary.PausedCount + summary.FailedCount + summary.CancelledCount;

        string FormatTab(DownloadFilterTab tab, string label, int count, string key)
        {
            var isActive = tab == activeTab;
            var text     = $"{label} ({count})";
            return isActive
                       ? $"[bold white on grey23] [[{key}]] {Markup.Escape(text)} [/]"
                       : $"[grey][[{key}]] {Markup.Escape(text)}[/]";
        }

        return string.Join("   ",
        [
            FormatTab(DownloadFilterTab.All, "Tümü", totalCount, "1"),
            FormatTab(DownloadFilterTab.Active, "Aktif & Kuyruk", activeCount, "2"),
            FormatTab(DownloadFilterTab.Completed, "Tamamlanan", completedCount, "3"),
            FormatTab(DownloadFilterTab.PausedOrFailed, "Durdurulan/Hata", pausedFailedCount, "4")
        ]);
    }

    internal static string BuildSummaryBanner(DownloadQueueSummary summary, string? statusMessage = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            parts.Add(statusMessage);
        }

        if (summary.ActiveCount > 0)
        {
            parts.Add($"[cyan]{summary.ActiveCount} aktif[/]");
        }

        if (summary.QueuedCount > 0)
        {
            parts.Add($"[silver]{summary.QueuedCount} kuyrukta[/]");
        }

        if (summary.PausedCount > 0)
        {
            parts.Add($"[yellow]{summary.PausedCount} duraklatıldı[/]");
        }

        if (summary.CompletedCount > 0)
        {
            parts.Add($"[green]{summary.CompletedCount} tamamlandı[/]");
        }

        if (summary.FailedCount > 0)
        {
            parts.Add($"[red]{summary.FailedCount} başarısız[/]");
        }

        if (summary.TotalSpeedBytesPerSecond > 0)
        {
            parts.Add($"[bold cyan]{FormatBytes((long) summary.TotalSpeedBytesPerSecond)}/s[/]");
        }

        return parts.Count == 0
                   ? "[grey]Kuyruk boş[/]"
                   : string.Join("  [grey]•[/]  ", parts);
    }

    private static string FormatJobRow(DownloadJobSnapshot snapshot, bool isSelected)
    {
        var job      = snapshot.Job;
        var prefix   = isSelected ? "[bold cyan]›[/] " : "  ";
        var icon     = StatusIcon(job.Status);
        var title    = Theme.Ellipsize(job.AnimeTitle, 26);
        var epCode   = FormatEpisodeCode(job.Season, job.EpisodeNumber);
        var progress = FormatProgressSummary(snapshot);

        var titleMarkup = isSelected
                              ? $"[bold white]{Markup.Escape(title)}[/]"
                              : $"[white]{Markup.Escape(title)}[/]";
        var epMarkup = isSelected
                           ? $"[bold cyan]{Markup.Escape(epCode)}[/]"
                           : $"[cyan]{Markup.Escape(epCode)}[/]";

        return $"{prefix}{icon} {titleMarkup} {epMarkup} [grey]│[/] {progress}";
    }

    private static string StatusIcon(DownloadJobStatus status)
    {
        return status switch
        {
            DownloadJobStatus.Downloading => "[bold cyan]↓[/]",
            DownloadJobStatus.Resolving   => "[bold cyan]⟳[/]",
            DownloadJobStatus.Subtitle    => "[bold cyan]字幕[/]",
            DownloadJobStatus.Finalizing  => "[bold cyan]⚙[/]",
            DownloadJobStatus.Queued      => "[grey]⋯[/]",
            DownloadJobStatus.Paused      => "[yellow]⏸[/]",
            DownloadJobStatus.Completed   => "[green]✓[/]",
            DownloadJobStatus.Failed      => "[red]✗[/]",
            DownloadJobStatus.Cancelled   => "[grey]⊘[/]",
            _                             => "[grey]•[/]"
        };
    }

    private static string FormatStatusBadge(DownloadJobStatus status)
    {
        return status switch
        {
            DownloadJobStatus.Downloading => "[cyan]İndiriliyor[/]",
            DownloadJobStatus.Resolving   => "[cyan]Kaynak çözülüyor[/]",
            DownloadJobStatus.Subtitle    => "[cyan]Altyazı indiriliyor[/]",
            DownloadJobStatus.Finalizing  => "[cyan]Birleştiriliyor[/]",
            DownloadJobStatus.Queued      => "[silver]Kuyrukta[/]",
            DownloadJobStatus.Paused      => "[yellow]Duraklatıldı[/]",
            DownloadJobStatus.Completed   => "[green]Tamamlandı[/]",
            DownloadJobStatus.Failed      => "[red]Başarısız[/]",
            DownloadJobStatus.Cancelled   => "[grey]İptal edildi[/]",
            _                             => "[grey]Bilinmiyor[/]"
        };
    }

    internal static string MiniProgressBar(double percent, int width = 10)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var filled  = (int) Math.Round(clamped / 100.0 * width);
        filled = Math.Clamp(filled, 0, width);
        var empty = width - filled;
        return $"[cyan]{new string('█', filled)}[/][grey]{new string('░', empty)}[/]";
    }

    private static string FormatProgressSummary(DownloadJobSnapshot snapshot)
    {
        var job = snapshot.Job;
        switch (job.Status)
        {
            case DownloadJobStatus.Resolving:
                return "[cyan]Kaynak aranıyor...[/]";
            case DownloadJobStatus.Subtitle:
                return "[cyan]Altyazılar indiriliyor...[/]";
            case DownloadJobStatus.Finalizing:
                return "[cyan]Birleştiriliyor...[/]";
            case DownloadJobStatus.Queued:
                return job.Priority > 0
                           ? $"[yellow]Öncelikli ({job.Priority}) • Sırada bekliyor[/]"
                           : "[grey]Sırada bekliyor[/]";
            case DownloadJobStatus.Paused:
                {
                    var pct   = job.Percent.HasValue ? $"%{job.Percent.Value:0} • " : string.Empty;
                    var bytes = job.BytesDownloaded > 0 ? FormatBytes(job.BytesDownloaded) : "0 B";
                    return $"[yellow]Duraklatıldı ({pct}{bytes})[/]";
                }
            case DownloadJobStatus.Completed:
                {
                    var size = job.BytesDownloaded > 0 ? $" • {FormatBytes(job.BytesDownloaded)}" : string.Empty;
                    return $"[green]Tamamlandı{size}[/]";
                }
            case DownloadJobStatus.Failed:
                {
                    var err = Theme.Ellipsize(job.ErrorMessage ?? "Hata", 36);
                    return $"[red]Hata: {Markup.Escape(err)}[/]";
                }
            case DownloadJobStatus.Cancelled:
                return "[grey]İptal edildi[/]";
            default:
                {
                    var details = new List<string>();
                    if (job.Percent.HasValue)
                    {
                        details.Add($"{MiniProgressBar(job.Percent.Value, 8)} [bold cyan]%{job.Percent.Value:0.0}[/]");
                    }

                    if (job.FragmentsTotal is > 0 && job.FragmentsDone is not null)
                    {
                        details.Add($"[silver]frag {job.FragmentsDone.Value}/{job.FragmentsTotal.Value}[/]");
                    }
                    else if (job.TotalBytes is > 0)
                    {
                        details.Add(
                            $"[silver]{FormatBytes(job.BytesDownloaded)} / {FormatBytes(job.TotalBytes.Value)}[/]");
                    }
                    else if (job.BytesDownloaded > 0)
                    {
                        details.Add($"[silver]{FormatBytes(job.BytesDownloaded)}[/]");
                    }

                    if (snapshot.SpeedBytesPerSecond > 0)
                    {
                        details.Add($"[green]{FormatBytes((long) snapshot.SpeedBytesPerSecond)}/s[/]");
                    }

                    if (snapshot.Eta is not null)
                    {
                        details.Add($"[grey]{DownloadSpeedometer.FormatEta(snapshot.Eta.Value)}[/]");
                    }

                    return details.Count == 0
                               ? "[cyan]İndiriliyor...[/]"
                               : string.Join(" [grey]•[/] ", details);
                }
        }
    }

    private static string FormatEpisodeCode(int season, double episodeNumber)
    {
        var ep = episodeNumber % 1 == 0
                     ? ((int) episodeNumber).ToString("D2", CultureInfo.InvariantCulture)
                     : episodeNumber.ToString("0.#", CultureInfo.InvariantCulture);
        return $"S{Math.Max(1, season):D2}E{ep}";
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double   value = Math.Max(0, bytes);
        var      unit  = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
                   ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unit]}"
                   : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
