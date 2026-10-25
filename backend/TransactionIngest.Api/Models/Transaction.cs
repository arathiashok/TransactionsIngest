namespace TransactionIngest.Api.Models;

public enum TransactionStatus
{
    Active,
    Revoked
}

public class Transaction
{
    public required string SourceSystem { get; set; }
    public int TransactionId { get; set; }
    public required string CardNumber { get; set; }
    public required string LocationCode { get; set; }
    public required string ProductName { get; set; }
    public decimal Amount { get; set; }
    public DateTime TransactionTime { get; set; }
    public TransactionStatus Status { get; set; }

    public ICollection<TransactionAudit> Audits { get; set; } = new List<TransactionAudit>();
}
