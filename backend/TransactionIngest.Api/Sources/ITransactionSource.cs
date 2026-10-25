namespace TransactionIngest.Api.Sources;

public interface ITransactionSource
{
    string SourceSystem { get; }

    Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
