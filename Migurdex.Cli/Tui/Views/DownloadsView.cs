using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui;
using Spectre.Console;
using System.Runtime.CompilerServices;

namespace Migurdex.Cli.Tui.Views;

public class DownloadsView : BaseView
{
    private readonly BackgroundDownloadTracker _tracker;

    private string? _lastSelectedSearchable;

    public DownloadsView(BackgroundDownloadTracker tracker)
    {
        _tracker = tracker;
    }

    public override string GetRpcState()
    {
        var active = _tracker.ActiveCount;
        return active > 0 ? $"İndirilenler ({active} aktif)" : "İndirilenler";
    }

    public override Task RenderAsync(ITuiNavigator navigator)
    {
        while (!TuiConsole.IsAppExitRequested)
        {
            var jobs = _tracker.Snapshot();
            if (jobs.Count == 0)
            {
                FuzzyPrompt.Show("İndirilenler",
                                 [
                                     new FuzzyChoice
                                     {
                                         Display       = "[grey]Henüz indirilen iş yok.[/]",
                                         DisplayActive = "[grey]Henüz indirilen iş yok.[/]",
                                         Searchable    = "Henüz indirilen iş yok"
                                     },
                                     TuiHelpers.Back()
                                 ],
                                 headerLines: ["[grey]İndirdiğin bölümler burada listelenir[/]"]);
                navigator.Pop();
                return Task.CompletedTask;
            }

            var result = FuzzyPrompt.ShowDynamic("İndirilenler",
                                                 Ticks(),
                                                 _ => BuildJobChoices(_tracker.Snapshot()),
                                                 TuiHelpers.Back(),
                                                 headerLines: ["[grey]Satırlar canlı güncellenir[/]"]);

            var selection = result.Selection;
            if (selection is null || selection.Searchable == "Geri")
            {
                navigator.Pop();
                return Task.CompletedTask;
            }

            if (selection.Searchable == "Bitmişleri temizle")
            {
                _tracker.ClearFinished();
                _lastSelectedSearchable = null;
                Toast.Show("[green]Bitmiş işler temizlendi.[/]");
                continue;
            }

            if (selection.Searchable == "Toplu Seçim")
            {
                RunBatchSelect();
                continue;
            }

            if (selection.Searchable == "Tümünü Kaldır")
            {
                RunClearAll();
                continue;
            }

            if (selection.AssociatedValue is not Guid jobId)
            {
                continue;
            }

            _lastSelectedSearchable = selection.Searchable;
            RunJobActions(jobId);
        }

        return Task.CompletedTask;
    }

    private async IAsyncEnumerable<int> Ticks(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tick      = 0;
        var idleTicks = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return tick++;
            await Task.Delay(300, cancellationToken);
            if (_tracker.ActiveCount > 0)
            {
                idleTicks = 0;
            }
            else if (++idleTicks >= 4)
            {
                break;
            }
        }
    }

    private List<FuzzyChoice> BuildJobChoices(IReadOnlyList<BackgroundJob> jobs)
    {
        var choices     = new List<FuzzyChoice>();
        var maxTitleLen = jobs.Count > 0 ? jobs.Max(j => j.Title.Length) : 0;
        if (maxTitleLen > 30)
        {
            maxTitleLen = 30;
        }

        var index = 0;
        foreach (var job in jobs)
        {
            index++;
            var idxText  = $"#{index}";
            var rawTitle = job.Title;
            if (rawTitle.Length > maxTitleLen)
            {
                rawTitle = rawTitle[..(maxTitleLen - 3)] + "...";
            }

            var paddedTitle = rawTitle.PadRight(maxTitleLen);
            var (bar, tail) = JobRowParts(job);
            var head = $"[grey]{idxText}[/] {Markup.Escape(job.Label)} {Markup.Escape(paddedTitle)}  {bar}  {tail}";

            choices.Add(new FuzzyChoice
            {
                Display = head,
                DisplayActive =
                    $"[bold white]{Markup.Escape(job.Label)} {Markup.Escape(paddedTitle)}[/]  {bar}  {tail}",
                Searchable      = $"{idxText} {job.Label} {job.Title}",
                AssociatedValue = job.Id
            });
        }

        if (jobs.Any(j => j.State is BackgroundJobState.Done
                                     or BackgroundJobState.Failed
                                     or BackgroundJobState.Cancelled))
        {
            choices.Add(new FuzzyChoice
            {
                Display       = "[yellow]Bitmişleri temizle[/]",
                DisplayActive = "[bold yellow]Bitmişleri temizle[/]",
                Searchable    = "Bitmişleri temizle",
                IsAction      = true
            });
        }

        choices.Add(new FuzzyChoice
        {
            Display       = "[cyan]Toplu Seçim...[/]",
            DisplayActive = "[bold cyan]Toplu Seçim...[/]",
            Searchable    = "Toplu Seçim",
            IsAction      = true
        });
        choices.Add(new FuzzyChoice
        {
            Display       = "[red]Tümünü Kaldır[/]",
            DisplayActive = "[bold red]Tümünü Kaldır[/]",
            Searchable    = "Tümünü Kaldır",
            IsAction      = true
        });

        return choices;
    }

    private static (string Bar, string Tail) JobRowParts(BackgroundJob job)
    {
        const int totalBlocks  = 10;
        var       filledBlocks = (int) Math.Round(Math.Clamp(job.Percent, 0, 100) / 100.0 * totalBlocks);
        filledBlocks = Math.Clamp(filledBlocks, 0, totalBlocks);

        var filled = new string('━', filledBlocks);
        var empty  = new string('━', totalBlocks - filledBlocks);

        if (job.State is BackgroundJobState.Done)
        {
            var file = string.Empty;
            if (!string.IsNullOrWhiteSpace(job.MediaPath))
            {
                try
                {
                    file = " " + Path.GetFileName(job.MediaPath);
                }
                catch
                {
                    // ignored
                }
            }

            return ($"[green]{filled}[/]", $"OK{Markup.Escape(file)}");
        }

        if (job.State is BackgroundJobState.Failed)
        {
            var error = job.Error ?? "hata";
            if (error.Length > 50)
            {
                error = error[..49] + "…";
            }

            return ("[red]━[/]", $"hata  {Markup.Escape(error)}");
        }

        if (job.State is BackgroundJobState.Cancelled)
        {
            return ("[grey]━[/]", "iptal");
        }

        if (job.State is not BackgroundJobState.Active)
        {
            if (job.State == BackgroundJobState.Paused)
            {
                return ($"[grey]{empty}[/]", $"- • {PausedTail(job)}");
            }

            return ($"[grey]{empty}[/]", "- • sırada");
        }

        var detail = string.IsNullOrWhiteSpace(job.Detail) ? "başlıyor…" : job.Detail;
        if (job.ResumeBaselineBytes > 1024 * 1024
            && job.DownloadedBytes < job.ResumeBaselineBytes / 2)
        {
            detail = "yeni kaynak • " + detail;
        }

        string bar;
        if (filledBlocks == totalBlocks)
        {
            bar = $"[green]{filled}[/]";
        }
        else if (filledBlocks > 0)
        {
            bar = $"[cyan]{filled[..^1]}╸[/][grey]{empty}[/]";
        }
        else
        {
            bar = $"[grey]{empty}[/]";
        }

        return (bar, $"{job.Percent:0.#}%  {Markup.Escape(detail)}");
    }

    private static string PausedTail(BackgroundJob job)
    {
        if (job.TotalBytes is > 0 && job.DownloadedBytes > 0)
        {
            var percent = Math.Min(100, job.DownloadedBytes * 100.0 / job.TotalBytes.Value);
            return $"duraklatıldı • %{percent:0.#}";
        }

        if (job.DownloadedBytes > 0)
        {
            return $"duraklatıldı • {JobProgressText.FormatBytes(job.DownloadedBytes)}";
        }

        return "duraklatıldı";
    }

    private static string StateWord(BackgroundJob job)
    {
        return job.State switch
        {
            BackgroundJobState.Queued    => "sırada",
            BackgroundJobState.Active    => "aktif",
            BackgroundJobState.Paused    => "duraklatıldı",
            BackgroundJobState.Done      => "tamamlandı",
            BackgroundJobState.Failed    => "hata",
            BackgroundJobState.Cancelled => "iptal",
            _                            => job.State.ToString()
        };
    }

    private void RunBatchSelect()
    {
        var jobs = _tracker.Snapshot();
        if (jobs.Count == 0)
        {
            return;
        }

        var displayMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var options    = new List<string>();
        var index      = 0;
        foreach (var job in jobs)
        {
            index++;
            var label = Markup.Escape($"#{index} {job.Label} {job.Title} [{StateWord(job)}]");
            displayMap[label] = job.Id;
            options.Add(label);
        }

        List<string> selected;
        try
        {
            selected = AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
                                          .Title("Toplu işlem [grey](Space seç • Enter uygula • Esc vazgeç)[/]")
                                          .Required(false)
                                          .PageSize(14)
                                          .AddChoices(options));
        }
        catch
        {
            return;
        }

        if (selected.Count == 0)
        {
            return;
        }

        var ids = selected.Select(s => displayMap[s]).ToList();
        var action = FuzzyPrompt.Show($"{ids.Count} iş seçildi",
                                      [
                                          new FuzzyChoice
                                          {
                                              Display       = "[red]İptal et[/]",
                                              DisplayActive = "[bold red]İptal et[/]",
                                              Searchable    = "İptal et",
                                              IsAction      = true
                                          },
                                          new FuzzyChoice
                                          {
                                              Display       = "[green]Tekrar dene[/]",
                                              DisplayActive = "[bold green]Tekrar dene[/]",
                                              Searchable    = "Tekrar dene",
                                              IsAction      = true
                                          },
                                          new FuzzyChoice
                                          {
                                              Display       = "[yellow]Listeden kaldır[/]",
                                              DisplayActive = "[bold yellow]Listeden kaldır[/]",
                                              Searchable    = "Listeden kaldır",
                                              IsAction      = true
                                          },
                                          TuiHelpers.Back()
                                      ],
                                      searchable: false,
                                      footerHelp: "↑↓ gez • Enter seç • Esc geri");
        if (action is null || action.Searchable == "Geri")
        {
            return;
        }

        var byId    = _tracker.Snapshot().ToDictionary(j => j.Id);
        var applied = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var job))
            {
                skipped++;
                continue;
            }

            bool ok = action.Searchable switch
            {
                "İptal et" => job.State is BackgroundJobState.Queued or BackgroundJobState.Active
                              && _tracker.Cancel(id),
                "Tekrar dene"     => job.State == BackgroundJobState.Failed && _tracker.Retry(id),
                "Listeden kaldır" => RemoveWithCancel(id),
                _                 => false
            };

            if (ok)
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        Toast.Show(skipped == 0
                       ? $"[green]{applied} işe uygulandı.[/]"
                       : $"[green]{applied} uygulandı[/] [grey]/[/] [yellow]{skipped} atlandı.[/]",
                   1500);
    }

    private bool RemoveWithCancel(Guid id)
    {
        var job = _tracker.Snapshot().FirstOrDefault(j => j.Id == id);
        if (job is null)
        {
            return _tracker.Remove(id);
        }

        if (job.State is not (BackgroundJobState.Queued or BackgroundJobState.Active))
        {
            return _tracker.Remove(id);
        }

        _tracker.RemoveWhenSettled(id);
        if (_tracker.Cancel(id))
        {
            return true;
        }

        return _tracker.Remove(id);
    }

    private void RunClearAll()
    {
        if (!Theme.Confirm(
                "[red]Tüm işler kaldırılsın mı?[/] Aktif indirmeler iptal edilir, yarım parçalar silinir. Tamamlanmış videolar silinmez.",
                false))
        {
            Toast.Show("[grey]Vazgeçildi.[/]");
            return;
        }

        foreach (var job in _tracker.Snapshot())
        {
            _tracker.RemoveWhenSettled(job.Id);
            _tracker.Cancel(job.Id);
        }

        _lastSelectedSearchable = null;
        Toast.Show("[green]Kaldırılıyor...[/] Durulan işler listeden düşecek.", 1500);
    }

    private void RunJobActions(Guid jobId)
    {
        var job = _tracker.Snapshot().FirstOrDefault(j => j.Id == jobId);
        if (job is null)
        {
            return;
        }

        var jobActions = new List<FuzzyChoice>();
        if (job.State is BackgroundJobState.Queued or BackgroundJobState.Active)
        {
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[yellow]Duraklat[/]",
                DisplayActive = "[bold yellow]Duraklat[/]",
                Searchable    = "Duraklat",
                IsAction      = true
            });
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[red]İptal et[/]",
                DisplayActive = "[bold red]İptal et[/]",
                Searchable    = "İptal et",
                IsAction      = true
            });
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[yellow]Listeden kaldır[/]",
                DisplayActive = "[bold yellow]Listeden kaldır[/]",
                Searchable    = "Listeden kaldır",
                IsAction      = true
            });
        }
        else if (job.State == BackgroundJobState.Paused)
        {
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[green]Devam et[/]",
                DisplayActive = "[bold green]Devam et[/]",
                Searchable    = "Devam et",
                IsAction      = true
            });
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[red]İptal et[/]",
                DisplayActive = "[bold red]İptal et[/]",
                Searchable    = "İptal et",
                IsAction      = true
            });
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[yellow]Listeden kaldır[/]",
                DisplayActive = "[bold yellow]Listeden kaldır[/]",
                Searchable    = "Listeden kaldır",
                IsAction      = true
            });
        }
        else if (job.State == BackgroundJobState.Failed)
        {
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[green]Tekrar dene[/]",
                DisplayActive = "[bold green]Tekrar dene[/]",
                Searchable    = "Tekrar dene",
                IsAction      = true
            });
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[yellow]Listeden kaldır[/]",
                DisplayActive = "[bold yellow]Listeden kaldır[/]",
                Searchable    = "Listeden kaldır",
                IsAction      = true
            });
        }
        else
        {
            jobActions.Add(new FuzzyChoice
            {
                Display       = "[yellow]Listeden kaldır[/]",
                DisplayActive = "[bold yellow]Listeden kaldır[/]",
                Searchable    = "Listeden kaldır",
                IsAction      = true
            });
        }

        jobActions.Add(TuiHelpers.Back());

        var action = FuzzyPrompt.Show($"{job.Label} {job.Title}",
                                      jobActions,
                                      searchable: false,
                                      headerLines: string.IsNullOrWhiteSpace(job.MediaPath)
                                                       ? []
                                                       : [$"[grey]{Markup.Escape(job.MediaPath)}[/]"],
                                      footerHelp: "↑↓ gez • Enter seç • Esc geri");
        if (action is null || action.Searchable == "Geri")
        {
            return;
        }

        if (action.Searchable == "Duraklat")
        {
            if (_tracker.Pause(job.Id))
            {
                Toast.Show("[yellow]Duraklatıldı - Devam et ile sürdürebilirsin.[/]");
            }
        }
        else if (action.Searchable == "Devam et")
        {
            if (_tracker.Resume(job.Id))
            {
                Toast.Show("[green]Devam ediyor.[/]");
            }
            else
            {
                Toast.Show("[red]Devam ettirilemedi.[/]");
            }
        }
        else if (action.Searchable == "Tekrar dene")
        {
            if (_tracker.Retry(job.Id))
            {
                Toast.Show("[green]Tekrar deneniyor.[/]");
            }
            else
            {
                Toast.Show("[red]Tekrar başlatılamadı.[/]");
            }
        }
        else if (action.Searchable == "İptal et")
        {
            if (_tracker.Cancel(job.Id))
            {
                Toast.Show("[yellow]İptal isteği gönderildi.[/]");
            }
        }
        else if (action.Searchable == "Listeden kaldır")
        {
            if (RemoveWithCancel(job.Id))
            {
                Toast.Show("[green]Kaldırılıyor...[/]", 1500);
            }

            if (_lastSelectedSearchable?.Contains(job.Label) == true)
            {
                _lastSelectedSearchable = null;
            }
        }
    }
}
