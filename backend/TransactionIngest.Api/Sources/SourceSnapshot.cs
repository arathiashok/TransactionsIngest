namespace TransactionIngest.Api.Sources;

public sealed record SourceSnapshot(
    IReadOnlyList<SourceTransaction> Transactions,
    bool IsComplete);
