using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface IBlameCollector
{
    void         RecordOperation(string operation, long elapsedMs, bool failed, bool clientError = false);
    void         RecordProvider(string provider, string operation, long elapsedMs, BlameOutcome outcome);
    BlameReport  Snapshot();
    void         SaveIfStale(TimeSpan? maxAge = null);
}
