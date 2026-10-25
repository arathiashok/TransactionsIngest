namespace TransactionIngest.Api.Services;

public enum SourceImportStatus
{
    Completed,
    SkippedIncomplete,
    SkippedInvalid,
    RetrievalFailed,
    DatabaseFailed,
    NotProcessed
}

public sealed record SourceImportResult(
    string SourceSystem,
    SourceImportStatus Status,
    int Inserted = 0,
    int Updated = 0,
    int Revoked = 0,
    int Reactivated = 0,
    int Unchanged = 0,
    string? Error = null);

public sealed record ImportResult(
    DateTime ReferenceTimeUtc,
    bool IsBusy,
    IReadOnlyList<SourceImportResult> Sources)
{
    public int Inserted => Sources.Sum(source => source.Inserted);
    public int Updated => Sources.Sum(source => source.Updated);
    public int Revoked => Sources.Sum(source => source.Revoked);
    public int Reactivated => Sources.Sum(source => source.Reactivated);
    public int Unchanged => Sources.Sum(source => source.Unchanged);
    public bool StoppedOnDatabaseFailure => Sources.Any(source => source.Status == SourceImportStatus.DatabaseFailed);
}
