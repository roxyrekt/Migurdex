using Microsoft.Extensions.DependencyInjection;
using Migurdex.Core.Database;

namespace Migurdex.Cli.Services.Downloads;

public interface IDownloadJobStore
{
    void                    Upsert(StoredDownloadJob job);
    void                    Delete(Guid              id);
    List<StoredDownloadJob> LoadAll();
}

public sealed class DownloadJobStore : IDownloadJobStore
{
    private readonly MigurdexDatabase _db;

    [ActivatorUtilitiesConstructor]
    public DownloadJobStore(MigurdexDatabase db)
    {
        _db = db;
    }

    public void Upsert(StoredDownloadJob job)
    {
        _db.SaveDownloadJob(job);
    }

    public void Delete(Guid id)
    {
        _db.DeleteDownloadJob(id);
    }

    public List<StoredDownloadJob> LoadAll()
    {
        return _db.LoadDownloadJobs();
    }
}
