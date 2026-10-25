namespace TransactionIngest.Api.Sources;

public sealed record SourceTransaction(
    int TransactionId,
    string CardNumber,
    string LocationCode,
    string ProductName,
    decimal Amount,
    DateTime TransactionTime);
